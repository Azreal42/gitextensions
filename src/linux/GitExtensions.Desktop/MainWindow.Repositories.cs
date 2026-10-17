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

    private void LoadRepositoryHistory()
    {
        try { _repositoryHistory = _repositoryHistoryStore.Load(_preferences.RecentRepositories); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        { AppendOutput($"Unable to read repository history: {exception.Message}"); }
        _recent.ItemTemplate = new FuncDataTemplate<RepositoryHistoryRow>((row, _) => new TextBlock { Text = row?.Caption, FontWeight = row?.IsAnchored == true ? FontWeight.Bold : FontWeight.Normal });
        UpdateRecentSelector();
    }

    private void SaveRepositoryHistory(RepositoryHistoryState history)
    {
        _repositoryHistoryStore.Save(history);
        _repositoryHistory = history.Normalize();
        UpdateRecentSelector();
        UpdateRepositoryMenus();
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
        MenuItem item = RepositoryAction(caption ?? RepositoryHistoryPresentation.DisplayPath(path), () => OpenRepositoryAsync(path));
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
        if (_dashboard is not null) { _dashboard.IsVisible = visible; if (visible) { _dashboard.Refresh(); } }
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

    private Task RefreshDashboardAsync()
    {
        _dashboard?.Refresh();
        return _dashboard?.MetadataLoading ?? Task.CompletedTask;
    }

    private async Task RecentRepositorySettingsAsync()
    {
        if (_busy) { return; }
        RecentRepositoriesSettingsDialog dialog = new(_repositoryHistory);
        RepositoryHistoryState? result = await dialog.ShowDialog<RepositoryHistoryState?>(this);
        if (result is not null) { SaveRepositoryHistory(result); _dashboard?.Refresh(reload: false); }
    }

    private double MeasureRepositoryCaption(string text) => new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Black).Width;
}
