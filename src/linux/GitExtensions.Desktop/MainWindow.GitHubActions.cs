using System.Diagnostics;
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

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private GitHubActionsApiClient? _gitHubActionsClient;
    private GitHubActionsRunSnapshot? _gitHubActionsSnapshot;
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
    internal TimeSpan GitHubActionsPollInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal Func<GitHubActionsSettings, GitHubActionsApiClient> GitHubActionsClientFactory { get; set; } = settings => new(settings);
    internal event Action? GitHubActionsStatusChanged;

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
                || GitHubActionsStatusFor(RevisionHistoryRow.CommitHash(row))?.Runs.FirstOrDefault() is not { } run) { return; }
            args.Handled = true;
            await OpenGitHubActionsRunPageAsync(run);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _commits.LayoutUpdated += (_, _) => ApplyGitHubActionsRows();
        _gitHubActionsRuns.ItemTemplate = new FuncDataTemplate<GitHubActionsWorkflowRun>((run, _) => new TextBlock
        { Text = run?.Description ?? "", TextTrimming = TextTrimming.CharacterEllipsis });
        _gitHubActionsRuns.SelectionChanged += (_, _) => UpdateGitHubActionsCommands();
        _gitHubActionsRuns.DoubleTapped += async (_, _) => await OpenGitHubActionsRunPageAsync(_gitHubActionsRuns.SelectedItem as GitHubActionsWorkflowRun);
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
            { if (!_gitHubActionsClosing) { SetGitHubActionsError("Could not refresh GitHub Actions settings."); } }
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
        { _gitHubActionsSnapshot = null; _gitHubActionsEnabled = false; _gitHubActionsClient?.Dispose(); _gitHubActionsClient = null; _gitHubActionsConfiguration = null; }
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
        try
        {
            GitHubActionsSettings settings = await repository.ReadGitHubActionsSettingsAsync(DetailedRepositorySettings.GlobalPath, token);
            if (token.IsCancellationRequested || _gitHubActionsClosing || !ReferenceEquals(_repository, repository)) { return; }
            _gitHubActionsEnabled = settings.Active;
            SetGitHubActionsReportVisible(settings.Active && settings.ShowBuildResultPage);
            if (!settings.Active)
            {
                _gitHubActionsSnapshot = null; _gitHubActionsClient?.Dispose(); _gitHubActionsClient = null; _gitHubActionsConfiguration = null;
                SetGitHubActionsError(null); PublishGitHubActionsStatus(); return;
            }
            settings.Validate();
            string configuration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\0', settings.ApiUrl, settings.Owner, settings.Repository, settings.Token))));
            if (_gitHubActionsClient is null || configuration != _gitHubActionsConfiguration)
            {
                _gitHubActionsClient?.Dispose(); _gitHubActionsClient = GitHubActionsClientFactory(settings);
                _gitHubActionsConfiguration = configuration; _gitHubActionsSnapshot = null;
            }
            GitHubActionsRunSnapshot snapshot = await _gitHubActionsClient.ReadAsync(force, token);
            if (token.IsCancellationRequested || _gitHubActionsClosing || !ReferenceEquals(_repository, repository)) { return; }
            _gitHubActionsSnapshot = snapshot; SetGitHubActionsError(snapshot.Truncated ? "Only the most recent GitHub Actions runs are shown." : null);
            PublishGitHubActionsStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is GitHubActionsException or ArgumentException or IOException or UnauthorizedAccessException
            or System.Xml.XmlException or InvalidDataException or GitCommandException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested && !_gitHubActionsClosing && ReferenceEquals(_repository, repository))
            {
                // Only our fixed, credential-free API messages may be shown. Never log payloads, settings or transport exceptions.
                SetGitHubActionsError(error is GitHubActionsException ? error.Message : "Could not read GitHub Actions settings. Check the CI configuration.");
                PublishGitHubActionsStatus();
            }
        }
    }

    internal GitHubActionsCommitStatus? GitHubActionsStatusFor(string? sha)
        => _gitHubActionsEnabled && sha is not null && _gitHubActionsSnapshot?.Commits.TryGetValue(sha, out GitHubActionsCommitStatus? status) == true ? status : null;

    internal void OnGitHubActionsSelectionChanged()
    {
        GitHubActionsCommitStatus? status = GitHubActionsStatusFor((_commits.SelectedItem as GitCommit)?.Hash);
        GitHubActionsWorkflowRun? selected = _gitHubActionsRuns.SelectedItem as GitHubActionsWorkflowRun;
        _gitHubActionsRuns.ItemsSource = status?.Runs ?? [];
        _gitHubActionsRuns.SelectedItem = status?.Runs.FirstOrDefault(r => r.Id == selected?.Id) ?? status?.Runs.FirstOrDefault();
        UpdateGitHubActionsCommands();
    }

    private void PublishGitHubActionsStatus()
    { ApplyGitHubActionsRows(); OnGitHubActionsSelectionChanged(); GitHubActionsStatusChanged?.Invoke(); }
    private void ApplyGitHubActionsRows()
    {
        foreach (Control row in _commits.GetVisualDescendants().OfType<Control>().Where(c => c.Name == "RevisionHistoryRow"))
        { RevisionHistoryRow.ApplyBuildStatus(row, GitHubActionsStatusFor(RevisionHistoryRow.CommitHash(row)),
            _gitHubActionsEnabled && _historyView.ShowBuildStatusIcon, _gitHubActionsEnabled && _historyView.ShowBuildStatusText); }
    }
    private void SetGitHubActionsError(string? error)
    {
        if (error is null && GitHubActionsError is not null && _state.Text?.StartsWith("GitHub Actions:", StringComparison.Ordinal) == true) { _state.Text = _repositorySummary; }
        GitHubActionsError = error; _gitHubActionsError.Text = error is null ? "" : T(error); _gitHubActionsError.IsVisible = error is not null;
        if (error is not null) { _state.Text = "GitHub Actions: " + T(error); }
    }
    private void SetGitHubActionsReportVisible(bool visible)
    {
        if (!visible && (_gitHubActionsTab is null || !_browserDetailTabs.Contains(_gitHubActionsTab))) { return; }
        if (_gitHubActionsTab is null)
        {
            _gitHubActionsOpen = new() { Name = "ShowGitHubActionsBuildPage", Content = T("Show build page"), HorizontalAlignment = HorizontalAlignment.Left };
            _gitHubActionsOpen.Click += async (_, _) => await OpenGitHubActionsRunPageAsync(_gitHubActionsRuns.SelectedItem as GitHubActionsWorkflowRun);
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
        MenuItem item = new() { Name = "ShowBuildPage", Header = T("Show build page") };
        item.Click += async (_, _) => await ShowBuildPageAsync(); _gitHubActionsShowMenus.Add(item); UpdateGitHubActionsCommands(); return item;
    }
    internal Task ShowBuildPageAsync() => OpenGitHubActionsRunPageAsync(GitHubActionsStatusFor((_commits.SelectedItem as GitCommit)?.Hash)?.Runs.FirstOrDefault());
    private Task OpenGitHubActionsRunPageAsync(GitHubActionsWorkflowRun? run)
    {
        Uri? uri = run is null ? null : _gitHubActionsClient?.BuildPageUri(run);
        if (!_gitHubActionsEnabled || uri is null || _gitHubActionsClosing) { return Task.CompletedTask; }
        try { using Process? process = ExternalLauncher(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } }); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetGitHubActionsError("The build page could not be opened in the browser."); }
        return Task.CompletedTask;
    }
    private void UpdateGitHubActionsCommands()
    {
        GitHubActionsWorkflowRun? selected = GitHubActionsStatusFor((_commits.SelectedItem as GitCommit)?.Hash)?.Runs.FirstOrDefault();
        bool available = _gitHubActionsEnabled && !_gitHubActionsClosing && selected is not null && _gitHubActionsClient?.BuildPageUri(selected) is not null;
        foreach (MenuItem item in _gitHubActionsShowMenus) { item.IsEnabled = available; }
        if (_gitHubActionsOpen is not null) { _gitHubActionsOpen.IsEnabled = _gitHubActionsRuns.SelectedItem is GitHubActionsWorkflowRun run && _gitHubActionsClient?.BuildPageUri(run) is not null; }
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
        _gitHubActionsClient?.Dispose(); _gitHubActionsClient = null; _gitHubActionsSnapshot = null; _gitHubActionsEnabled = false;
        UpdateGitHubActionsCommands();
    }
}
