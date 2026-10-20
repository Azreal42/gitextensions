using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly RepositoryHistoryStore _repositoryHistoryStore = new();
    private RepositoryHistoryState _repositoryHistory = new();
    private DashboardControl? _dashboard;
    private StackPanel? _repositoryToolbars;
    private readonly MenuItem _recentMenu = new();
    private readonly MenuItem _favoritesMenu = new();
    internal RepositoryBranchMetadataService RepositoryBranches { get; } = new();
    internal bool ShowRepositoryCurrentBranch => _preferences.ShowRepoCurrentBranch;
    internal GitExecutionEnvironment RepositoryBranchExecution => _repository?.ExecutionEnvironment ?? GitEnvironment.Current;
    internal Task RepositoryBranchLoading { get; private set; } = Task.CompletedTask;
    private readonly Dictionary<string, RepositoryBranchMetadata> _recentBranchMetadata = new(StringComparer.Ordinal);
    private readonly List<(string Path, string Caption, WeakReference<TextBlock> Text)> _recentBranchLabels = [];
    private readonly List<(string Path, string Caption, WeakReference<MenuItem> Menu)> _repositoryBranchMenus = [];
    private CancellationTokenSource? _repositoryBranchRequest;
    private string? _repositoryBranchContext;
    private bool _repositoryBranchReadersClosing;

    private void LoadRepositoryHistory()
    {
        try { _repositoryHistory = _repositoryHistoryStore.Load(_preferences.RecentRepositories); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        { AppendOutput($"Unable to read repository history: {exception.Message}"); }
        _recent.ItemTemplate = new FuncDataTemplate<RepositoryHistoryRow>((row, _) =>
        {
            TextBlock text = new() { Name = "RecentRepositoryCaption", Text = row is null ? "" : RepositoryCaption(row.Path, row.Caption),
                Tag = row?.Path, FontWeight = row?.IsAnchored == true ? FontWeight.Bold : FontWeight.Normal };
            if (row is not null) { _recentBranchLabels.Add((row.Path, row.Caption, new(text))); }
            return text;
        });
        UpdateRecentSelector();
        BeginRepositoryBranchRead();
    }

    private void SaveRepositoryHistory(RepositoryHistoryState history)
    {
        _repositoryHistoryStore.Save(history);
        _repositoryHistory = history.Normalize();
        UpdateRecentSelector();
        UpdateRepositoryMenus();
        BeginRepositoryBranchRead();
    }

    private double RecentRepositoriesPopupWidth => _repositoryHistory.RecentReposComboMinWidth > 0 ? _repositoryHistory.RecentReposComboMinWidth : 300;

    private void UpdateRecentSelector()
    {
        RepositoryHistorySelection selection = RepositoryHistoryPresentation.Select(_repositoryHistory, MeasureRepositoryCaption);
        _recent.ItemsSource = selection.Top.Concat(selection.Recent).DistinctBy(row => row.Path, StringComparer.Ordinal).ToArray();
        _recent.Width = _browserToolbar is not null ? 24 : _repositoryHistory.RecentReposComboMinWidth > 0 ? _repositoryHistory.RecentReposComboMinWidth : 200;
        if (_recent.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault() is { } popup) { popup.MinWidth = RecentRepositoriesPopupWidth; }
    }

    private void UpdateRepositoryMenus()
    {
        _recentMenu.Header = T("Recent repositories");
        _favoritesMenu.Header = T("Favorite repositories");
        RepositoryHistorySelection selection = RepositoryHistoryPresentation.Select(_repositoryHistory, MeasureRepositoryCaption);
        _recentMenu.ItemsSource = selection.Top.Concat(selection.Recent).DistinctBy(row => row.Path, StringComparer.Ordinal).Select(row => RepositoryMenuItem(row.Path, row.Caption)).ToArray();
        if (_recentToolbarMenu is not null)
        { _recentToolbarMenu.ItemsSource = selection.Top.Concat(selection.Recent).DistinctBy(row => row.Path, StringComparer.Ordinal).Select(row => RepositoryMenuItem(row.Path, row.Caption)).ToArray(); }
        List<MenuItem> favorites = [];
        favorites.Add(RepositoryAction(T("Add to favorites"), () =>
        {
            if (_repository is not null) { SaveRepositoryHistory(_repositoryHistory.Favorite(_repository.Path, true)); }
            return Task.CompletedTask;
        }));
        foreach (var group in _repositoryHistory.Favorites.GroupBy(item => item.Category).OrderBy(group => group.Key))
        {
            MenuItem[] entries = group.Select(item => RepositoryMenuItem(item.Path)).ToArray();
            if (group.Key is null) { favorites.AddRange(entries); }
            else { favorites.Add(new MenuItem { Header = group.Key, ItemsSource = entries }); }
        }
        _favoritesMenu.ItemsSource = favorites;
    }

    private MenuItem RepositoryMenuItem(string path, string? caption = null)
    {
        string baseCaption = caption ?? RepositoryHistoryPresentation.DisplayPath(path);
        MenuItem item = RepositoryAction(RepositoryCaption(path, baseCaption), () => OpenRepositoryAsync(path));
        item.Tag = path;
        _repositoryBranchMenus.Add((path, baseCaption, new(item)));
        ToolTip.SetTip(item, path);
        return item;
    }
    private MenuItem RepositoryAction(string title, Func<Task> action)
    {
        MenuItem item = new() { Header = title };
        item.Click += async (_, _) =>
        {
            if (_busy) { return; }
            try { await action(); }
            catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
        };
        return item;
    }

    private void SetDashboardVisible(bool visible)
    {
        if (_dashboard is not null) { _dashboard.IsVisible = visible; if (visible) { _dashboard.Refresh(reload: _opened); BeginRepositoryBranchRead(); } }
        if (_browserBody is not null) { _browserBody.IsVisible = !visible; }
        if (_repositoryToolbars is not null) { _repositoryToolbars.IsVisible = !visible; }
        if (visible) { Title = "Git Extensions"; }
    }

    private async Task CloseRepositoryAsync()
    {
        if (_busy) { return; }
        await RunAsync("Close repository", async _ =>
        {
            HideQuickSearch(); _quickSearchArtificialSelection = null; _quickSearchLastCommit = null; _quickSearchReturnTab = null;
            _parentChildNavigation?.Clear();
            _parentChildFileFocus = null;
            _historySelectionOrder.Clear(); _parentChildLatestCommit = null;
            await _backgroundFetch.UnregisterAsync();
            _repository = null;
            await OnGitHubActionsRepositoryChangedAsync(null);
            _comparisonBase = null;
            _selectedArtificialHistoryRevision = null;
            _browseFileHistory = null;
            _browseInitialComparison = null;
            _explicitFileHistoryDiffPath = null; _explicitFileHistoryTreePath = null;
            _referenceSelectionOverride = null;
            _refreshing = true;
            try
            {
                _repositoryPath.Text = "";
                _history = [];
                _historyGraph.Commits = []; _historyGraph.References = []; _historyGraph.CurrentHead = null;
                foreach (ListBox list in new[] { _commits, _references, _remotes, _stashes, _worktrees, _submodules, _configuration, _hunks }) { list.ItemsSource = null; }
                _changesFilesView?.SetGroups([], preserveSelection: false);
                _fileTreeView?.SetGroups([], preserveSelection: false);
                CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
                _fileTreeRepository = null; _fileTreeRevision = null; _allFileTreePaths = [];
                _fileTreeEntries = new Dictionary<string, GitComparisonTreeEntry>(); _fileTreeSource = null; ClearFileGrepViewer();
                _navigation.ItemsSource = null;
                ClearRevisionDiff();
                _navigationRevisionFilter = null; ResetBranchFilter();
                _commitContent?.ClearImage(); _changesContent?.ClearImage(); _fileContent?.ClearImage();
                _diff.Text = null; _fileView.Text = null; ClearCommitInformation(); _gpgInfo.Text = null;
                _repositorySummary = T("Open a repository to begin");
                SetDashboardVisible(true);
            }
            finally { _refreshing = false; }
        }, refresh: false, requireRepository: false);
    }

    private async Task RefreshDashboardAsync()
    {
        _dashboard?.Refresh();
        BeginRepositoryBranchRead();
        await Task.WhenAll(RepositoryBranchLoading, _dashboard?.MetadataLoading ?? Task.CompletedTask);
        RequestAutomaticUpdateCheck();
    }

    private async Task RecentRepositorySettingsAsync()
    {
        if (_busy) { return; }
        RecentRepositoriesSettingsDialog dialog = new(_repositoryHistory);
        RepositoryHistoryState? result = await dialog.ShowDialog<RepositoryHistoryState?>(this);
        if (result is not null) { SaveRepositoryHistory(result); _dashboard?.Refresh(reload: false); }
    }

    private string RepositoryCaption(string path, string caption)
    {
        if (!_preferences.ShowRepoCurrentBranch || !_recentBranchMetadata.TryGetValue(path, out RepositoryBranchMetadata? metadata)
            || !metadata.Valid || metadata.IsBare || !metadata.BranchLoaded) { return caption; }
        string branch = metadata.IsDetached ? T("Detached HEAD") : metadata.Branch;
        return branch.Length == 0 ? caption : caption + " (" + branch + ")";
    }

    private void UpdateRepositoryBranchCaptions()
    {
        _recentBranchLabels.RemoveAll(item => !item.Text.TryGetTarget(out _));
        foreach (var label in _recentBranchLabels)
        { if (label.Text.TryGetTarget(out TextBlock? text)) { text.Text = RepositoryCaption(label.Path, label.Caption); } }
        _repositoryBranchMenus.RemoveAll(item => !item.Menu.TryGetTarget(out _));
        foreach (var label in _repositoryBranchMenus)
        { if (label.Menu.TryGetTarget(out MenuItem? menu)) { menu.Header = RepositoryCaption(label.Path, label.Caption); } }
    }

    private void BeginRepositoryBranchRead(GitExecutionEnvironment? execution = null)
    {
        if (_repositoryBranchReadersClosing || _lifetime.IsCancellationRequested) { return; }
        _repositoryBranchRequest?.Cancel();
        if (!_preferences.ShowRepoCurrentBranch)
        { UpdateRepositoryBranchCaptions(); RepositoryBranchLoading = Task.CompletedTask; return; }
        execution ??= RepositoryBranchExecution;
        string context = RepositoryBranchMetadataService.ContextIdentity(execution);
        if (_repositoryBranchContext != context)
        { _repositoryBranchContext = context; _recentBranchMetadata.Clear(); UpdateRepositoryBranchCaptions(); }
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _repositoryBranchRequest = request;
        string[] paths = _repositoryHistory.Recent.Concat(_repositoryHistory.Favorites).Select(entry => entry.Path).Distinct(StringComparer.Ordinal).ToArray();
        RepositoryBranchLoading = ReadRepositoryBranchCaptionsAsync(paths, request, execution);
    }

    private async Task ReadRepositoryBranchCaptionsAsync(string[] paths, CancellationTokenSource request, GitExecutionEnvironment execution)
    {
        using WindowWork work = TrackWindowWork();
        CancellationToken token = request.Token;
        try
        {
            var metadata = await Task.WhenAll(paths.Select(async path => (Path: path,
                Value: await RepositoryBranches.ReadAsync(path, true, execution, token))));
            if (token.IsCancellationRequested || _repositoryBranchReadersClosing || !ReferenceEquals(_repositoryBranchRequest, request)) { return; }
            foreach (var item in metadata) { _recentBranchMetadata[item.Path] = item.Value; }
            UpdateRepositoryBranchCaptions();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!_repositoryBranchReadersClosing && !token.IsCancellationRequested) { AppendOutput("Repository branch: " + error.Message); } }
        finally
        {
            if (ReferenceEquals(_repositoryBranchRequest, request)) { _repositoryBranchRequest = null; }
            request.Dispose();
        }
    }

    /// <summary>Publish the already-saved Appearance choice without rebuilding MRU rows or stealing selection focus.</summary>
    internal async Task ApplyRepositoryBranchVisibilityAsync(bool show, CancellationToken cancellationToken = default)
    {
        if (_repositoryBranchReadersClosing || _lifetime.IsCancellationRequested) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        _preferences = _preferences with { ShowRepoCurrentBranch = show };
        RepositoryBranches.Invalidate();
        _recentBranchMetadata.Clear(); UpdateRepositoryBranchCaptions();
        Task dashboard = _dashboard?.RefreshMetadata() ?? Task.CompletedTask;
        BeginRepositoryBranchRead();
        // Settings can stop waiting while the live window retains and observes the shared readers.
        await Task.WhenAll(RepositoryBranchLoading, dashboard).WaitAsync(cancellationToken);
    }

    private void BeginRepositoryBranchRefresh(GitRepository repository)
    {
        if (_repositoryBranchReadersClosing || _lifetime.IsCancellationRequested) { return; }
        GitExecutionEnvironment execution = repository.ExecutionEnvironment;
        RepositoryBranches.Invalidate(repository.Path);
        _recentBranchMetadata.Remove(repository.Path);
        // Decorations of other recent repositories must not delay the active history workflow.
        // ReadRepositoryBranchCaptionsAsync owns and observes its work through the window lease.
        BeginRepositoryBranchRead(execution);
    }

    private async Task DisposeRepositoryBranchReadersAsync()
    {
        if (_repositoryBranchReadersClosing) { return; }
        using WindowWork work = TrackWindowWork();
        _repositoryBranchReadersClosing = true; _repositoryBranchRequest?.Cancel();
        await RepositoryBranches.DisposeAsync();
        await Task.WhenAll(RepositoryBranchLoading, _dashboard?.MetadataLoading ?? Task.CompletedTask);
        _recentBranchLabels.Clear(); _repositoryBranchMenus.Clear(); _recentBranchMetadata.Clear();
    }

    private double MeasureRepositoryCaption(string text) => new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Black).Width;
}
