using System.Diagnostics;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private IBuildServerSource? _gitHubActionsClient;
    private string _buildServerProvider = "Build server";
    private BuildServerSnapshot? _gitHubActionsSnapshot;
    private GitRepository? _gitHubActionsPublishedRepository;
    private CancellationTokenSource? _gitHubActionsRequest;
    private Task _gitHubActionsCompletion = Task.CompletedTask;
    private readonly SemaphoreSlim _gitHubActionsRefreshGate = new(1, 1);
    private readonly HashSet<Task> _gitHubActionsManualReads = [];
    private readonly HashSet<Task> _gitHubActionsRestarts = [];
    private int _gitHubActionsGeneration;
    private string? _gitHubActionsConfiguration;
    private bool _gitHubActionsInitialized, _gitHubActionsClosing, _gitHubActionsEnabled;
    private bool _gitHubActionsSubscribed;
    private bool _gitHubActionsAttached;
    private int _gitHubActionsAttachment;
    private readonly TextBlock _gitHubActionsError = new() { Name = "GitHubActionsError", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _gitHubActionsRuns = new() { Name = "GitHubActionsRuns" };
    private readonly List<MenuItem> _gitHubActionsShowMenus = [];
    private TabItem? _gitHubActionsTab;
    private Button? _gitHubActionsOpen;
    internal string? GitHubActionsError { get; private set; }
    internal bool GitHubActionsEnabled => _gitHubActionsEnabled;
    internal Task GitHubActionsCompletion => Task.WhenAll(_gitHubActionsRestarts.Concat(_gitHubActionsManualReads).Append(_gitHubActionsCompletion).ToArray());
    private TimeSpan? _buildServerPollOverride;
    internal TimeSpan GitHubActionsPollInterval
    { get => _buildServerPollOverride ?? _gitHubActionsClient?.PreferredPollInterval ?? TimeSpan.FromSeconds(30); set => _buildServerPollOverride = value; }
    internal Func<GitHubActionsSettings, GitHubActionsApiClient> GitHubActionsClientFactory { get; set; } = settings => new(settings);
    internal event Action? GitHubActionsStatusChanged;
    internal Func<TeamCitySettings, IBuildServerCredentials, ITeamCityClient> TeamCityClientFactory { get; set; } = (settings, credentials) => new TeamCityApiClient(settings, credentials);
    internal Func<GitLabSettings, IGitLabClient> GitLabClientFactory { get; set; } = settings => new GitLabApiClient(settings);
    internal BuildServerCredentialsStore? CredentialsStoreOverride { get; set; }
    internal BuildServerCommitStatus? BuildServerStatusFor(string? sha)
        => _gitHubActionsEnabled && sha is not null && _gitHubActionsSnapshot?.Commits.TryGetValue(sha, out BuildServerCommitStatus? status) == true ? status : null;

    internal void InitializeGitHubActions()
    {
        if (_gitHubActionsInitialized) { return; } _gitHubActionsInitialized = true;
        _gitHubActionsAttached = IsVisible;
        AttachedToVisualTree += (_, _) => { _gitHubActionsAttached = true; UpdateGitHubActionsSubscription(); };
        DetachedFromVisualTree += (_, _) => { _gitHubActionsAttached = false; UpdateGitHubActionsSubscription(detached: true); };
        Opened += (_, _) => { _gitHubActionsAttached = true; UpdateGitHubActionsSubscription(); };
        Closed += (_, _) => { _gitHubActionsAttached = false; UpdateGitHubActionsSubscription(detached: true); };
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) { UpdateGitHubActionsSubscription(); } };
        UpdateGitHubActionsSubscription();
        _commits.AddHandler(PointerPressedEvent, async (_, args) =>
        {
            if (args.GetCurrentPoint(_commits).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) { return; }
            Control? cell = (args.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
                .FirstOrDefault(control => control.Name == "RevisionBuildStatusCell");
            Control? row = cell?.GetVisualAncestors().OfType<Control>().FirstOrDefault(control => control.Name == "RevisionHistoryRow");
            if (row is null || row.FindAncestorOfType<ListBox>() != _commits
                || BuildServerStatusFor(RevisionHistoryRow.CommitHash(row))?.Runs.FirstOrDefault() is not { } run) { return; }
            args.Handled = true;
            await OpenGitHubActionsRunPageAsync(run);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _commits.LayoutUpdated += (_, _) => ApplyGitHubActionsRows();
        _gitHubActionsRuns.ItemTemplate = new FuncDataTemplate<BuildServerRun>((run, _) => new TextBlock
        { Text = run?.Description ?? "", TextTrimming = TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = run?.Tooltip ?? run?.Description });
        _gitHubActionsRuns.SelectionChanged += (_, _) => UpdateGitHubActionsCommands();
        _gitHubActionsRuns.DoubleTapped += async (_, _) => await OpenGitHubActionsRunPageAsync(_gitHubActionsRuns.SelectedItem as BuildServerRun);
    }

    private void UpdateGitHubActionsSubscription(bool detached = false)
    {
        bool active = !detached && _gitHubActionsAttached && IsVisible && !_gitHubActionsClosing;
        if (active == _gitHubActionsSubscribed) { return; }
        _gitHubActionsSubscribed = active; ++_gitHubActionsAttachment;
        if (active)
        { GitHubActionsSettingsPublication.Saved += GitHubActionsSettingsSaved; if (_repository is not null) { ScheduleGitHubActionsRepositoryChanged(_repository); } }
        else
        { GitHubActionsSettingsPublication.Saved -= GitHubActionsSettingsSaved; _gitHubActionsRequest?.Cancel(); }
    }
    private void GitHubActionsSettingsSaved(string path)
    {
        int attachment = _gitHubActionsAttachment;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_gitHubActionsSubscribed || attachment != _gitHubActionsAttachment || _gitHubActionsClosing || _repository is not GitRepository repository) { return; }
            List<string> paths = [DetailedRepositorySettings.GlobalPath];
            if (repository.Info is { } info)
            {
                paths.Add(Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName));
                if (!info.IsBare) { paths.Add(Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName)); }
            }
            if (paths.Any(expected => string.Equals(Path.GetFullPath(expected), Path.GetFullPath(path), StringComparison.Ordinal)))
            { ScheduleGitHubActionsRepositoryChanged(repository); }
        });
    }
    /// <summary>Nonblocking host hook. Every restart and its first HTTP read remain owned and drained at Close.</summary>
    internal void ScheduleGitHubActionsRepositoryChanged(GitRepository? repository)
    {
        if (_gitHubActionsClosing || repository is not null && !IsVisible) { return; }
        Task observed = ObserveAsync(OnGitHubActionsRepositoryChangedAsync(repository));
        _gitHubActionsRestarts.Add(observed); _ = RemoveAsync(observed);
        async Task ObserveAsync(Task restart)
        {
            try { await restart; }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            { if (!_gitHubActionsClosing) { SetGitHubActionsError("Could not refresh build server settings."); } }
        }
        async Task RemoveAsync(Task restart) { try { await restart; } finally { _gitHubActionsRestarts.Remove(restart); } }
    }

    /// <summary>Await the first real read only; the owned polling task remains cancellable until Close.</summary>
    internal async Task OnGitHubActionsRepositoryChangedAsync(GitRepository? repository)
    {
        InitializeGitHubActions();
        int generation = ++_gitHubActionsGeneration;
        await StopGitHubActionsReadAsync();
        if (generation != _gitHubActionsGeneration) { return; }
        bool sameRepository = ReferenceEquals(repository, _gitHubActionsPublishedRepository);
        _gitHubActionsPublishedRepository = repository;
        if (!sameRepository)
        { _gitHubActionsSnapshot = null; _gitHubActionsEnabled = false; if (_gitHubActionsClient is not null) { await _gitHubActionsClient.DisposeAsync(); } _gitHubActionsClient = null; _gitHubActionsConfiguration = null; }
        SetGitHubActionsError(null);
        PublishGitHubActionsStatus();
        if (repository is null || _gitHubActionsClosing || _lifetime.IsCancellationRequested) { return; }
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _gitHubActionsRequest = request;
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _gitHubActionsCompletion = PollGitHubActionsAsync(repository, request.Token, first);
        await first.Task;
    }

    private async Task PollGitHubActionsAsync(GitRepository repository, CancellationToken token, TaskCompletionSource first)
    {
        try
        {
            await RefreshGitHubActionsCoreAsync(repository, force: true, token);
            first.TrySetResult();
            while (!token.IsCancellationRequested && _gitHubActionsEnabled && GitHubActionsPollInterval != Timeout.InfiniteTimeSpan)
            {
                await Task.Delay(GitHubActionsPollInterval, token);
                await RefreshGitHubActionsCoreAsync(repository, force: false, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { first.TrySetResult(); }
    }

    internal Task RefreshGitHubActionsNowAsync()
    {
        if (_repository is not GitRepository repository || _gitHubActionsRequest is not { } request) { return Task.CompletedTask; }
        Task read = RefreshGitHubActionsCoreAsync(repository, force: true, request.Token); _gitHubActionsManualReads.Add(read);
        return ObserveAsync();
        async Task ObserveAsync() { try { await read; } finally { _gitHubActionsManualReads.Remove(read); } }
    }

    private async Task RefreshGitHubActionsCoreAsync(GitRepository repository, bool force, CancellationToken token)
    {
        try { await _gitHubActionsRefreshGate.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        try { await ReadGitHubActionsAsync(repository, force, token); }
        finally { _gitHubActionsRefreshGate.Release(); }
    }
    private async Task ReadGitHubActionsAsync(GitRepository repository, bool force, CancellationToken token)
    {
        bool configurationResolved = false;
        try
        {
            GitDistributedSettingsSnapshot xml = await GitDistributedSettingsSnapshot.LoadAsync(DetailedRepositorySettings.GlobalPath,
                await repository.GetInfoAsync(token), token);
            if (token.IsCancellationRequested || _gitHubActionsClosing || !ReferenceEquals(_repository, repository)) { return; }
            string provider = xml.GetValue(GitHubActionsSettings.ProviderKey) ?? "";
            bool active = GitHubActionsSettings.Boolean(xml.GetValue(GitHubActionsSettings.EnableKey)) == true
                && provider is GitHubActionsSettings.ProviderName or TeamCitySettings.ProviderName or GitLabSettings.ProviderName or AppVeyorSettings.ProviderName or AzureDevOpsSettings.ProviderName;
            if (_buildServerProvider != provider) { SetGitHubActionsError(null); _gitHubActionsSnapshot = null; }
            _buildServerProvider = provider.Length == 0 ? "Build server" : provider;
            _gitHubActionsEnabled = active;
            SetGitHubActionsReportVisible(active && GitHubActionsSettings.Boolean(xml.GetValue(GitHubActionsSettings.ShowPageKey)) == true);
            if (!active)
            {
                _gitHubActionsSnapshot = null;
                if (_gitHubActionsClient is not null) { await _gitHubActionsClient.DisposeAsync(); }
                _gitHubActionsClient = null; _gitHubActionsConfiguration = null;
                SetGitHubActionsError(null); PublishGitHubActionsStatus(); return;
            }
            GitHubActionsSettings? github = null; TeamCitySettings? teamCity = null; GitLabSettings? gitLab = null; AppVeyorSettings? appVeyor = null; AzureDevOpsSettings? azureDevOps = null; IReadOnlySet<string>? admittedCommits = null; BuildServerCredentials? credentials = null;
            string configuration;
            if (provider == GitHubActionsSettings.ProviderName)
            {
                github = await repository.ReadGitHubActionsSettingsAsync(DetailedRepositorySettings.GlobalPath, token); github.Validate();
                configuration = string.Join('\0', provider, github.ApiUrl, github.Owner, github.Repository, github.Token);
            }
            else if (provider == GitLabSettings.ProviderName)
            {
                var remotes = await repository.GetRemotesAsync(token);
                gitLab = GitLabSettings.Read(xml, remotes.SelectMany(remote => remote.PushUrls.Count > 0 ? remote.PushUrls : remote.FetchUrls));
                gitLab.Validate();
                configuration = string.Join('\0', provider, gitLab.InstanceUri.AbsoluteUri,
                    gitLab.ProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture), gitLab.ApiToken, gitLab.PagesLimit);
            }
            else if (provider == AzureDevOpsSettings.ProviderName)
            {
                BuildServerRepositoryVariables variables = await repository.GetBuildServerRepositoryVariablesAsync(token);
                var remotes = await repository.GetRemotesAsync(token);
                azureDevOps = AzureDevOpsSettings.Read(xml, remotes.SelectMany(remote => remote.FetchUrls), variables.Expand);
                azureDevOps.Validate();
                configuration = string.Join('\0', provider, azureDevOps.ProjectUri.AbsoluteUri,
                    azureDevOps.BuildDefinitionFilter, azureDevOps.ApiToken, azureDevOps.RepositoryName);
            }
            else if (provider == AppVeyorSettings.ProviderName)
            {
                BuildServerRepositoryVariables variables = await repository.GetBuildServerRepositoryVariablesAsync(token);
                appVeyor = AppVeyorSettings.Read(xml, expandVariables: variables.Expand); appVeyor.Validate();
                admittedCommits = _commits.Items.OfType<GitCommit>().Select(commit => commit.Hash)
                    .Where(AppVeyorBuild.IsCommitHash).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                configuration = string.Join('\0', provider, appVeyor.AccountName, appVeyor.AccountToken,
                    appVeyor.ProjectName, appVeyor.LoadTestsResults, string.Join('\n', admittedCommits.Order(StringComparer.Ordinal)));
            }
            else
            {
                teamCity = TeamCitySettings.Read(xml, Path.GetFileName(repository.Path.TrimEnd(Path.DirectorySeparatorChar))); teamCity.Validate();
                BuildServerCredentialsSnapshot saved = (CredentialsStoreOverride ?? new BuildServerCredentialsStore()).Load(provider, teamCity.ServerUri);
                if (!saved.Exists)
                { throw new InvalidOperationException("Configure TeamCity credentials in Settings."); }
                credentials = saved.Credentials;
                configuration = string.Join('\0', provider, teamCity.ServerUri.AbsoluteUri, teamCity.ProjectName, teamCity.BuildIdFilter, teamCity.LogAsGuest,
                    credentials.BuildServerCredentialsType, credentials.Username, credentials.Password, credentials.BearerToken);
            }
            configuration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));
            if (token.IsCancellationRequested) { return; }
            configurationResolved = true;
            if (_gitHubActionsClient is null || configuration != _gitHubActionsConfiguration)
            {
                IBuildServerSource? previous = _gitHubActionsClient;
                _gitHubActionsClient = null; _gitHubActionsConfiguration = null; _gitHubActionsSnapshot = null;
                // Invalidate first: even a replacement constructor can reject changed credentials/settings.
                PublishGitHubActionsStatus();
                if (previous is not null) { await previous.DisposeAsync(); }
                token.ThrowIfCancellationRequested();
                _gitHubActionsClient = github is not null ? new GitHubActionsBuildSource(GitHubActionsClientFactory(github))
                    : gitLab is not null ? new GitLabBuildSource(GitLabClientFactory(gitLab))
                    : azureDevOps is not null ? new AzureDevOpsBuildSource(AzureDevOpsClientFactory(azureDevOps))
                    : appVeyor is not null ? new AppVeyorBuildSource(AppVeyorClientFactory(appVeyor, admittedCommits!))
                    : new TeamCityBuildSource(TeamCityClientFactory(teamCity!, credentials!));
                _gitHubActionsConfiguration = configuration;
            }
            BuildServerSnapshot snapshot = await _gitHubActionsClient.ReadAsync(force, token);
            if (token.IsCancellationRequested || _gitHubActionsClosing || !ReferenceEquals(_repository, repository)) { return; }
            _gitHubActionsSnapshot = snapshot; SetGitHubActionsError(snapshot.Truncated ? "Only the most recent builds are shown." : snapshot.Diagnostics.FirstOrDefault());
            PublishGitHubActionsStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is GitHubActionsException or TeamCityException or GitLabException or AppVeyorException or AzureDevOpsException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or System.Xml.XmlException or InvalidDataException or GitCommandException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested && !_gitHubActionsClosing && ReferenceEquals(_repository, repository))
            {
                if (!configurationResolved)
                {
                    _gitHubActionsSnapshot = null; _gitHubActionsConfiguration = null;
                    if (_gitHubActionsClient is not null) { await _gitHubActionsClient.DisposeAsync(); }
                    _gitHubActionsClient = null;
                }
                // Only our fixed, credential-free API messages may be shown. Never log payloads, settings or transport exceptions.
                SetGitHubActionsError(error is GitHubActionsException or TeamCityException or GitLabException or AppVeyorException or AzureDevOpsException ? error.Message : "Could not read build server settings. Check the CI configuration and credentials.");
                PublishGitHubActionsStatus();
            }
        }
    }

    // Compatibility accessor for the first provider; the grid and report use the provider-neutral projection.
    internal GitHubActionsCommitStatus? GitHubActionsStatusFor(string? sha)
        => _gitHubActionsEnabled && sha is not null && _gitHubActionsClient is GitHubActionsBuildSource source
            && source.LastSnapshot?.Commits.TryGetValue(sha, out GitHubActionsCommitStatus? status) == true ? status : null;

    internal void OnGitHubActionsSelectionChanged()
    {
        BuildServerCommitStatus? status = BuildServerStatusFor((_commits.SelectedItem as GitCommit)?.Hash);
        BuildServerRun? selected = _gitHubActionsRuns.SelectedItem as BuildServerRun;
        _gitHubActionsRuns.ItemsSource = status?.Runs ?? [];
        _gitHubActionsRuns.SelectedItem = status?.Runs.FirstOrDefault(r => r.Id == selected?.Id) ?? status?.Runs.FirstOrDefault();
        UpdateGitHubActionsCommands();
    }

    private void PublishGitHubActionsStatus()
    { ApplyGitHubActionsRows(); OnGitHubActionsSelectionChanged(); GitHubActionsStatusChanged?.Invoke(); }
    private void ApplyGitHubActionsRows()
    {
        foreach (Control row in _commits.GetVisualDescendants().OfType<Control>().Where(c => c.Name == "RevisionHistoryRow"))
        { RevisionHistoryRow.ApplyProviderBuildStatus(row, BuildServerStatusFor(RevisionHistoryRow.CommitHash(row)),
            _gitHubActionsEnabled && _historyView.ShowBuildStatusIcon, _gitHubActionsEnabled && _historyView.ShowBuildStatusText); }
    }
    private void SetGitHubActionsError(string? error)
    {
        if (error is null && GitHubActionsError is not null && _state.Text?.StartsWith(_buildServerProvider + ":", StringComparison.Ordinal) == true) { _state.Text = _repositorySummary; }
        GitHubActionsError = error; _gitHubActionsError.Text = error is null ? "" : T(error); _gitHubActionsError.IsVisible = error is not null;
        if (error is not null) { _state.Text = _buildServerProvider + ": " + T(error); }
    }
    private void SetGitHubActionsReportVisible(bool visible)
    {
        if (!visible && (_gitHubActionsTab is null || !_browserDetailTabs.Contains(_gitHubActionsTab))) { return; }
        if (_gitHubActionsTab is null)
        {
            _gitHubActionsOpen = new() { Name = "ShowGitHubActionsBuildPage", Content = T("Show build page"), HorizontalAlignment = HorizontalAlignment.Left };
            _gitHubActionsOpen.Click += async (_, _) => await OpenGitHubActionsRunPageAsync(_gitHubActionsRuns.SelectedItem as BuildServerRun);
            DockPanel content = new(); StackPanel header = new() { Spacing = 4, Children = { _gitHubActionsError, _gitHubActionsOpen } };
            DockPanel.SetDock(header, Dock.Top); content.Children.Add(header); content.Children.Add(_gitHubActionsRuns);
            _gitHubActionsTab = new() { Name = "GitHubActionsBuildReport", Header = T("Build report"), Content = content };
        }
        if (_browserDetailTabs.Contains(_gitHubActionsTab) == visible) { return; }
        // Browser layout binds ItemsSource. Keep its tab list current so changing
        // the Commit panel's position also retains the optional build report.
        TabItem? selected = _tabs.SelectedItem as TabItem;
        TabItem[] tabs = _tabs.Items.OfType<TabItem>().Where(item => item != _gitHubActionsTab).ToArray();
        _browserDetailTabs = _browserDetailTabs.Where(item => item != _gitHubActionsTab)
            .Concat(visible ? [_gitHubActionsTab] : []).ToArray();
        TabItem[] updated = visible ? tabs.Append(_gitHubActionsTab).ToArray() : tabs;
        bool wasLayingOut = _layingOutCommitInfo; _layingOutCommitInfo = true;
        try
        {
            _tabs.ItemsSource = updated;
            _tabs.SelectedItem = selected is not null && updated.Contains(selected) ? selected : updated.FirstOrDefault();
        }
        finally { _layingOutCommitInfo = wasLayingOut; }
    }
    internal MenuItem BuildGitHubActionsShowBuildPageMenu()
    {
        MenuItem item = new() { Name = "ShowBuildPage", Header = RevisionHeader("openBuildReportToolStripMenuItem", "View &build report in a browser"), Icon = OriginalIconImage("Integration.png") };
        item.Click += async (_, _) => await WithRevisionActionFocusAsync(ShowBuildPageAsync); _gitHubActionsShowMenus.Add(item); UpdateGitHubActionsCommands(); return item;
    }
    internal Task ShowBuildPageAsync() => OpenGitHubActionsRunPageAsync(BuildServerStatusFor((_commits.SelectedItem as GitCommit)?.Hash)?.Runs.FirstOrDefault());
    private Task OpenGitHubActionsRunPageAsync(BuildServerRun? run)
    {
        Uri? uri = run?.PageUri;
        if (!_gitHubActionsEnabled || uri is null || _gitHubActionsClosing) { return Task.CompletedTask; }
        try { using Process? process = ExternalLauncher(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } }); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetGitHubActionsError("The build page could not be opened in the browser."); }
        return Task.CompletedTask;
    }
    private void UpdateGitHubActionsCommands()
    {
        BuildServerRun? selected = BuildServerStatusFor((_commits.SelectedItem as GitCommit)?.Hash)?.Runs.FirstOrDefault();
        bool available = _gitHubActionsEnabled && !_gitHubActionsClosing && selected is not null && selected.PageUri is not null;
        foreach (MenuItem item in _gitHubActionsShowMenus) { item.IsEnabled = item.IsVisible = available; }
        UpdateBuildServerPullRequestCommands();
        if (_gitHubActionsOpen is not null) { _gitHubActionsOpen.IsEnabled = _gitHubActionsRuns.SelectedItem is BuildServerRun run && run.PageUri is not null; }
    }
    private async Task StopGitHubActionsReadAsync()
    {
        CancellationTokenSource? previous = _gitHubActionsRequest; _gitHubActionsRequest = null; previous?.Cancel();
        Task previousCompletion = _gitHubActionsCompletion;
        await previousCompletion; await Task.WhenAll(_gitHubActionsManualReads.ToArray()); previous?.Dispose();
        if (ReferenceEquals(_gitHubActionsCompletion, previousCompletion)) { _gitHubActionsCompletion = Task.CompletedTask; }
    }
    internal async Task CloseGitHubActionsAsync()
    {
        _gitHubActionsClosing = true; ++_gitHubActionsGeneration; await StopGitHubActionsReadAsync();
        UpdateGitHubActionsSubscription(detached: true);
        await Task.WhenAll(_gitHubActionsRestarts.ToArray());
        if (_gitHubActionsClient is not null) { await _gitHubActionsClient.DisposeAsync(); } _gitHubActionsClient = null; _gitHubActionsSnapshot = null; _gitHubActionsEnabled = false;
        UpdateGitHubActionsCommands();
    }
}
