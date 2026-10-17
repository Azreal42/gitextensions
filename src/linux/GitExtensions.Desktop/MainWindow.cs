using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly TextBox _repositoryPath = new() { Watermark = "Repository directory", MinWidth = 280 };
    private readonly ComboBox _recent = new() { PlaceholderText = "Recent repositories", Width = 200 };
    private readonly TextBlock _state = new() { Name = "RepositoryStatus", Text = "Open a repository to begin", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _output = ReadOnlyText();
    private readonly TextBox _diff = ReadOnlyText();
    private readonly TextBox _commitMessage = new() { Watermark = "Commit message", AcceptsReturn = true, MinHeight = 86 };
    private readonly CheckBox _amend = new() { Content = "Amend latest commit" };
    private readonly ListBox _commits = new() { SelectionMode = SelectionMode.Multiple };
    private readonly CommitGraph _historyGraph = new();
    private readonly TreeView _changes = new() { Name = "WorkingFiles", SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _references = new();
    private readonly ListBox _remotes = new();
    private readonly ListBox _stashes = new();
    private readonly ListBox _worktrees = new();
    private readonly ListBox _submodules = new() { Name = "SubmodulesList" };
    private readonly ListBox _configuration = new();
    private readonly TreeView _tree = new() { SelectionMode = SelectionMode.Multiple };
    private readonly BranchHistoryFilterComboBox _historyRevision = new();
    private readonly TextBox _filePath = new() { Watermark = "Repository-relative file path", MinWidth = 240 };
    private readonly TextBox _fileView = ReadOnlyText();
    private readonly TabControl _tabs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PreferencesStore _preferencesStore = new();
    private DesktopPreferences _preferences = new();
    private GitRepository? _repository;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _refreshing;
    private bool _opened;
    private readonly List<Button> _actionButtons = [];
    private string _repositorySummary = "Open a repository to begin";
    private IReadOnlyList<GitCommit> _history = [];
    private GitLogQuery _historyQuery = new();
    private int _historyWindow;
    private bool _historyHasMore;
    private string? _navigationRevisionFilter;
    private string? _referenceSelectionOverride;

    internal bool IsBusy => _busy || BrowserReads.Any(read => read.Pending != 0) || _commitInfo.IsLoading;
    internal Exception? LastException { get; private set; }

    public MainWindow()
    {
        Title = "Git Extensions";
        Width = 1280;
        Height = 840;
        MinWidth = 860;
        MinHeight = 620;
        try
        {
            _preferences = _preferencesStore.Load();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            AppendOutput($"Unable to read preferences: {exception.Message}");
        }

        LoadRepositoryHistory();
        _historyView = _historyViewStore.Load();
        _historyQuery = new GitLogQuery { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000), Reflog = _historyView.ShowReflog };
        _historyWindow = _historyQuery.Count;
        _commits.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(HeightProperty, BrowseRowHeight),
                new Setter(MinHeightProperty, BrowseRowHeight),
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MarginProperty, new Thickness(0))
            }
        });
        _commits.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer viewer) { _historyGraph.VerticalOffset = viewer.Offset.Y; UpdateHistoryPresentation(); }
        });
        _recent.SelectionChanged += async (_, _) =>
        {
            if (_recent.SelectedItem is RepositoryHistoryRow row && !_busy)
            {
                await OpenRepositoryAsync(row.Path);
            }
        };
        _commits.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) => new StackPanel
        {
            Height = 40,
            Children =
            {
                new TextBlock { Text = commit?.Subject, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = commit is null ? "" : $"{commit.Hash[..Math.Min(9, commit.Hash.Length)]}  ·  {commit.AuthorName}  ·  {commit.AuthoredAt.LocalDateTime:g}", FontSize = 11, Opacity = 0.7 }
            }
        });
        _references.ItemTemplate = TextTemplate<GitReference>(reference => $"{(reference.IsCurrent ? "● " : "  ")}{reference.ShortName}{(reference.IsTag ? "  [tag]" : reference.IsRemote ? "  [remote]" : "")}{(reference.Upstream is null ? "" : " → " + reference.Upstream)}");
        _remotes.ItemTemplate = TextTemplate<GitRemote>(remote => $"{remote.Name}  {string.Join(", ", remote.FetchUrls)}");
        _stashes.ItemTemplate = TextTemplate<GitStash>(stash => $"{stash.Reference}  {stash.Subject}");
        _worktrees.ItemTemplate = TextTemplate<GitWorktree>(worktree => $"{worktree.Path}  [{worktree.Branch ?? "detached"}]{(worktree.LockedReason is null ? "" : "  [locked]")}");
        _submodules.ItemTemplate = TextTemplate<GitSubmodule>(module => $"{module.Path}  {module.Url}  {(module.IsInitialized ? module.Head : "not initialized")}");
        _commits.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing) { CancelRevisionReads(); }
            RememberRevisionNavigation();
            RefreshRebaseMenu();
            RefreshRevisionContextMenu();
            if (!_refreshing) { UpdateHistoryPresentation(); }
            if (!_refreshing && _commits.SelectedItem is GitCommit commit)
            {
                if (_selectingRevisionForContextMenu) { _contextRevisionDiffPending = true; UpdateCommitInfo(commit); }
                else { await LoadSelectedRevisionDetailsAsync(commit); }
            }
        };
        _changes.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && _changesFilesView?.IsUpdating != true && !_busy)
            {
                if (SelectedWorkingChange is not null) { await LoadSelectedDiffAsync(); }
                else { ClearWorkingDiff(); }
            }
        };
        _tree.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && !_settingGrepTree && _fileTreeView?.IsUpdating != true)
            {
                if (SelectedTreePath is string path) { _filePath.Text = path; await ShowSelectedTreeFileAsync(path); }
                else { ShowSelectedTreeFolder(); }
            }
        };
        _repositoryPath.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await OpenRepositoryAsync(_repositoryPath.Text ?? "");
            }
        };
        Content = BuildLayout();
        _commits.AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _workingRevision?.AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _indexRevision?.AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ApplyHistoryViewPresentation();
        ApplyTheme(_preferences.Theme);
        Opened += async (_, _) =>
        {
            if (_opened)
            {
                return;
            }

            _opened = true;
            string? path = Program.RepositoryPath ?? _repositoryHistory.Recent.FirstOrDefault()?.Path;
            if (path is not null)
            {
                await OpenRepositoryAsync(path);
            }

            if (Program.SmokeTest)
            {
                DispatcherTimer.RunOnce(() =>
                {
                    int code = path is not null && _repository is null ? 1 : 0;
                    if (code == 0 && Environment.GetEnvironmentVariable("GITEXTENSIONS_SMOKE_SCREENSHOT") is string screenshot)
                    {
                        try
                        {
                            using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
                            bitmap.Render(this);
                            bitmap.Save(screenshot);
                        }
                        catch (Exception exception) { Console.Error.WriteLine(exception); code = 1; }
                    }
                    Console.WriteLine(code == 0 ? "Desktop smoke test passed" : "Desktop repository loading failed");
                    (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(code);
                }, TimeSpan.FromMilliseconds(600));
            }
        };
        Closed += async (_, _) =>
        {
            _lifetime.Cancel(); _operation?.Cancel(); CancelBrowserReads();
            try { await _commitInfo.Completion; }
            catch (OperationCanceledException) { }
            catch (Exception exception) { AppendOutput("Commit information: " + exception.Message); }
            finally { _commitInfo.Dispose(); }
            CancelBranchMenuRead();
            _commitContent?.Dispose(); _changesContent?.Dispose(); _fileContent?.Dispose(); _dashboard?.Dispose();
            _revisionFilesView?.Dispose(); _changesFilesView?.Dispose(); _fileTreeView?.Dispose();
            await _backgroundFetch.DisposeAsync();
        };
        AddHandler(KeyDownEvent, HandleShortcuts, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private CancellationToken Token => _operation?.Token ?? _lifetime.Token;
    private string CurrentRevision => (_commits.SelectedItem as GitCommit)?.Hash ?? "HEAD";

    private IReadOnlyList<TabItem> BuildTabs()
    {
        Grid historyBody = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        historyBody.Children.Add(_historyGraph);
        Grid.SetColumn(_commits, 1);
        historyBody.Children.Add(_commits);
        DockPanel history = Panel(historyBody, Row(_historyRevision, Button("Load history", RefreshAsync),
            Button("Compare…", CompareAsync), Button("Cherry-pick", CherryPickAsync), Button("Revert", RevertAsync), Button("Reset…", ResetAsync)));
        DockPanel changes = Panel(BuildWorkingFilesView(), Row(Button("Stage selected", () => StageAsync(false)), Button("Stage all", () => StageAsync(true)),
            Button("Unstage selected", () => UnstageAsync(false)), Button("Unstage all", () => UnstageAsync(true)),
                Button("Discard…", DiscardAsync), Button("Resolve…", ResolveConflictAsync)));
        StackPanel commit = new() { Spacing = 8, Children = { _commitMessage, Row(_amend, Button("Commit", CommitAsync)) }, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(commit, Dock.Bottom);
        changes.Children.Insert(1, commit);
        return
        [
            new TabItem { Header = "History", Content = Split(history, _diff) },
            new TabItem { Header = "Changes", Content = Split(changes, BuildChangesDiff()) },
            new TabItem { Header = "Branches & tags", Content = Panel(_references, Row(Button("Create branch…", CreateBranchAsync), Button("Checkout", CheckoutAsync), Button("Delete…", DeleteReferenceAsync), Button("Rename…", RenameBranchAsync), Button("Create tag…", CreateTagAsync)), Row(Button("Merge", MergeAsync), Button("Rebase onto", RebaseOntoReferenceAsync), Button("Continue…", () => ManageOperationAsync(GitOperationAction.Continue)), Button("Abort…", () => ManageOperationAsync(GitOperationAction.Abort)), Button("Skip…", () => ManageOperationAsync(GitOperationAction.Skip)))) },
            new TabItem { Header = "Remotes", Content = Panel(_remotes, Row(Button("Add…", AddRemoteAsync), Button("Edit URL…", EditRemoteAsync), Button("Remove…", RemoveRemoteAsync), Button("Fetch all", () => RunAsync("Fetch all", async r => { GitResult result = await r.FetchAllAsync(Token); AppendOutput(result.StandardOutput + result.StandardError); })), Button("Pull…", PullAsync), Button("Push…", PushAsync), Button("Browse host", BrowseRemoteAsync))) },
            new TabItem { Header = "Stashes", Content = Panel(_stashes, Row(Button("Create…", StashAsync), Button("Apply", () => ApplyStashAsync(false)), Button("Pop", () => ApplyStashAsync(true)), Button("Delete…", DropStashAsync), Button("Show diff", ShowStashAsync))) },
            new TabItem { Header = "Worktrees", Content = Panel(_worktrees, Row(Button("Add…", AddWorktreeAsync), Button("Open", OpenWorktreeAsync), Button("Remove…", RemoveWorktreeAsync), Button("Prune", PruneWorktreesAsync))) },
            new TabItem { Header = "Submodules", Content = Panel(_submodules, Row(Button("Add…", AddSubmoduleAsync), Button("Manage…", () => ManageSubmodulesAsync()), BuildSubmoduleRemovalButton(), Button("Open", OpenSubmoduleAsync))) },
            new TabItem { Header = "Files", Content = BuildFileTreePane() },
            new TabItem { Header = "Configuration", Content = Panel(_configuration, Row(Button("Set local…", () => SetConfigurationAsync(GitConfigScope.Local)), Button("Set global…", () => SetConfigurationAsync(GitConfigScope.Global)), Button("Unset…", UnsetConfigurationAsync), Button("Diff tool…", DiffToolAsync), Button(_translations.Translate("DiffViewerSettingsPage", "$this", "Text", "Diff viewer") + "…", DiffViewerSettingsAsync))) },
            new TabItem { Header = "Repository", Content = BuildRepositoryTools() }
        ];
    }

    private readonly TextBox _changesDiff = ReadOnlyText();
    private readonly CheckBox _stagedDiff = new() { Content = "Show staged changes" };
    private readonly ListBox _hunks = new() { SelectionMode = SelectionMode.Multiple, MaxHeight = 140 };
    private GitPatchFile? _selectedPatch;
    private DiffView? _changesDiffView;

    private Control BuildChangesDiff()
    {
        _changesDiffView = new DiffView(_changesDiff) { Name = "WorktreeDiff" };
        _changesDiffView.Toolbar.SetNames("WorktreeDiff");
        _workingViewerOptions = _changesDiffView.ComparisonMenu.Options;
        _changesDiffView.ComparisonMenu.OptionsChanged += async (_, _) =>
        {
            _workingViewerOptions = _changesDiffView.ComparisonMenu.Options;
            await LoadSelectedDiffAsync();
        };
        _changesDiffView.DisplayOptionsChanged += RequestDiffAppearanceReload;
        _hunks.ItemTemplate = TextTemplate<GitPatchHunk>(hunk => hunk.Header);
        _stagedDiff.IsCheckedChanged += async (_, _) => await LoadSelectedDiffAsync();
        _changesContent = new FileContentView(_changesDiff, _changesDiffView);
        object[] menus = _changesDiffView.CreateContextMenuItems("WorktreeDiff").ToArray();
        _changesDiffView.ContextMenu = new ContextMenu { ItemsSource = menus };
        DockPanel panel = Panel(_changesContent, Row(_stagedDiff, Button("Refresh diff", LoadSelectedDiffAsync)),
            Row(_stageLinesButton = Button("Stage selected lines", () => ApplyLinesAsync(false)), _unstageLinesButton = Button("Unstage selected lines", () => ApplyLinesAsync(true))),
            Row(_stageHunksButton = Button("Stage selected hunks", () => ApplyHunksAsync(false)), _unstageHunksButton = Button("Unstage selected hunks", () => ApplyHunksAsync(true))), _hunks);
        return panel;
    }

    private static TextBox ReadOnlyText() => new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.NoWrap };
    private static FuncDataTemplate<T> TextTemplate<T>(Func<T, string> text) where T : class => new((item, _) => new TextBlock { Text = item is null ? "" : text(item), Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap });
    private static WrapPanel Row(params Control[] controls)
    {
        WrapPanel row = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        foreach (Control control in controls) { row.Children.Add(control); }
        return row;
    }

    private static DockPanel Panel(Control body, params Control[] rows)
    {
        DockPanel panel = new() { Margin = new Thickness(8), LastChildFill = true };
        foreach (Control row in rows)
        {
            DockPanel.SetDock(row, Dock.Top);
            panel.Children.Add(row);
        }

        panel.Children.Add(body);
        return panel;
    }

    private static Grid Split(Control left, Control right)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("2*,6,3*") };
        grid.Children.Add(left);
        GridSplitter splitter = new() { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private Button Button(string label, Func<Task> action)
    {
        Button button = new() { Content = T(label), Tag = RequiresWorktree(label) };
        if (label != "Cancel operation") { _actionButtons.Add(button); }
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
        };
        return button;
    }

    private async Task ChooseRepositoryAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Git repository", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is string path)
        {
            await OpenRepositoryAsync(path);
        }
    }

    public async Task OpenRepositoryAsync(string path)
    {
        if (_busy) { return; }
        GitRepository? previous = _repository;
        await RunAsync("Open repository", async _ =>
        {
            GitRepository candidate = await GitRepository.OpenAsync(path, Token);
            await candidate.GetStatusAsync(Token);
            await RefreshDataAsync(candidate, opening: true);
            try { SaveRepositoryHistory(_repositoryHistory.Visit(candidate.Path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { AppendOutput($"Unable to save preferences: {exception.Message}"); }
            SetDashboardVisible(false);
            await RegisterBackgroundFetchAsync(candidate);
        }, refresh: false, requireRepository: false);
        if (_repository == previous && previous is null) { _state.Text = "Could not open repository. See command output."; }
    }

    private async Task RefreshAsync()
    {
        BranchHistoryFilterComboBox? editing = _historyRevision.IsKeyboardFocusWithin ? _historyRevision
            : _branchFilterOverflow?.IsKeyboardFocusWithin == true ? _branchFilterOverflow : null;
        var bookmark = editing?.CaptureEditorSelection();
        if (_dashboard?.IsVisible == true) { await RefreshDashboardAsync(); }
        else { await RunAsync("Refresh", repository => RefreshDataAsync(repository), refresh: false); }
        if (!IsBranchFilterOwnerClosing && editing is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } && bookmark is { } selection)
        { editing.RestoreEditorSelection(selection); }
    }

    private async Task RefreshDataAsync(GitRepository repository, bool opening = false)
    {
        _refreshing = true;
        try
        {
            ApplyReferenceSorting(repository);
            bool changedRepository = opening && _repository?.Path != repository.Path;
            string? selectedPath = changedRepository ? null : SelectedWorkingChange?.Path;
            string? selectedHash = changedRepository ? null : (_commits.SelectedItem as GitCommit)?.Hash;
            string[] selectedHashOrder = changedRepository ? [] : (_commits.SelectedItems?.OfType<GitCommit>() ?? []).Select(commit => commit.Hash).Distinct(StringComparer.Ordinal).ToArray();
            HashSet<string> selectedHashes = selectedHashOrder.ToHashSet(StringComparer.Ordinal);
            object? selectedRevisionDiff = changedRepository ? null : CaptureRevisionFileSelection();
            string? selectedReferenceName = opening ? null : _referenceSelectionOverride ?? (_references.SelectedItem as GitReference)?.Name;
            GitLogQuery configuredQuery = opening ? new GitLogQuery { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000), Reflog = _historyView.ShowReflog } : _historyQuery;
            int historyWindow = opening ? configuredQuery.Count : _historyWindow;
            // The toolbar editor is a draft. Refresh/background fetch/load more
            // may only query the expression explicitly accepted by the user.
            string? revision = configuredQuery.BranchFilter ?? configuredQuery.Revision;
            GitStatus status = await repository.GetStatusAsync(Token);
            IReadOnlyList<GitReference> references = await repository.GetReferencesAsync(Token);
            bool missingNavigationReference = !opening && _navigationRevisionFilter is string navigationFilter && revision == navigationFilter && !references.Any(reference => reference.Name == navigationFilter);
            if (missingNavigationReference)
            {
                revision = null;
                configuredQuery = configuredQuery with { All = true, Revision = null, BranchFilter = null };
                historyWindow = configuredQuery.Count;
            }
            configuredQuery = ConfigureHistoryViewQuery(configuredQuery);
            GitLogQuery query = configuredQuery with
            {
                Count = historyWindow + 1,
                Skip = 0,
                Revision = configuredQuery.Revision,
                BranchFilter = configuredQuery.BranchFilter
            };
            IReadOnlyList<GitCommit> history = status.IsUnborn ? [] : await repository.GetLogAsync(query, Token);
            IReadOnlyList<GitRemote> remotes = await repository.GetRemotesAsync(Token);
            IReadOnlyList<GitStash> stashes = await repository.GetStashesAsync(Token);
            _supportsStashStaged ??= await repository.SupportsStashStagedAsync(Token);
            IReadOnlyList<GitWorktree> worktrees = await repository.GetWorktreesAsync(Token);
            IReadOnlyList<GitSubmodule> submodules = await repository.GetSubmodulesAsync(Token);
            var configuration = await repository.GetConfigAsync(cancellationToken: Token);
            if (_fileMenuAdditionalOperations is not null) { await _fileMenuAdditionalOperations.RefreshAsync(repository, Token); }
            GitCommit[] visibleHistory = history.Take(historyWindow).ToArray();
            await LoadHistoryViewMetadataAsync(repository, visibleHistory, Token);
            string treeRevision = visibleHistory.FirstOrDefault(commit => commit.Hash == selectedHash)?.Hash
                ?? visibleHistory.FirstOrDefault(commit => selectedHashes.Contains(commit.Hash))?.Hash
                ?? visibleHistory.FirstOrDefault()?.Hash ?? "HEAD";
            IReadOnlyList<GitTreeEntry> tree = status.IsUnborn ? [] : await repository.GetTreeAsync(treeRevision, cancellationToken: Token);
            Token.ThrowIfCancellationRequested();
            if (opening)
            {
                await _backgroundFetch.UnregisterAsync();
                Token.ThrowIfCancellationRequested();
                _repository = repository;
                if (changedRepository) { _comparisonBase = null; _navigationSelectionKey = null; _authoredEmail = null; }
                _navigation.SelectedItem = null;
                _navigationRevisionFilter = null;
                _repositoryPath.Text = repository.Path;
                Title = $"Git Extensions — {repository.Path}";
                if (repository.IsBare) { SelectDetailsTab("DiffTab"); }
                SetDashboardVisible(false);
            }
            if (missingNavigationReference)
            {
                if (_historyRevision.Text == _navigationRevisionFilter) { SetBranchFilterDraft(null); }
                _navigationRevisionFilter = null; _acceptedBranchFilter = null; _historyBranchScope = HistoryBranchScope.All;
            }
            _historyQuery = configuredQuery;
            _historyWindow = historyWindow;
            UpdateBranchFilterReferences(references, opening);
            _repositorySummary = $"{(repository.IsBare ? T("Bare repository") + "  ·  " : "")}{status.Branch ?? "detached"}  ·  {status.Changes.Count} changes  ·  ↑{status.Ahead} ↓{status.Behind}";
            _state.Text = _repositorySummary;
            UpdateBrowserToolbar(status);
            UpdateVirtualRevisionRows(status);
            SetWorkingFiles(status.Changes, selectedPath, preserveSelection: !changedRepository);
            _references.ItemsSource = references;
            _references.SelectedItem = references.FirstOrDefault(reference => reference.Name == selectedReferenceName);
            _referenceSelectionOverride = null;
            _remotes.ItemsSource = remotes;
            _stashes.ItemsSource = stashes;
            _worktrees.ItemsSource = worktrees;
            _submodules.ItemsSource = submodules;
            _configuredUserEmail = configuration.LastOrDefault(entry => entry.Key == "user.email").Value;
            _configuration.ItemsSource = configuration.Select(entry => $"{entry.Key} = {entry.Value}").ToList();
            SetFileTreePaths(repository, treeRevision, tree);
            UpdateBrowserNavigation(references, stashes, submodules, worktrees);
            _historyHasMore = history.Count > _historyWindow && _historyWindow < 100000;
            _history = visibleHistory;
            if (_historyMore is not null) { _historyMore.IsVisible = _historyHasMore; }
            _historyGraph.Commits = _history;
            _historyGraph.References = HistoryViewReferences(references);
            _historyGraph.CurrentHead = status.Head;
            _highlightedHistoryBranch = null;
            _commits.ItemsSource = _historyGraph.OrderedCommits;
            _commits.SelectedItem = _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == selectedHash)
                ?? _historyGraph.OrderedCommits.FirstOrDefault(commit => selectedHashes.Contains(commit.Hash))
                ?? _historyGraph.OrderedCommits.FirstOrDefault();
            foreach (string hash in selectedHashOrder)
            {
                GitCommit? selected = _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == hash);
                if (selected is not null && selected.Hash != (_commits.SelectedItem as GitCommit)?.Hash) { _commits.SelectedItems?.Add(selected); }
            }
            ClearRevisionDiff();
            _commitContent?.ClearImage(); _diff.Text = null; ClearCommitInformation(); _gpgInfo.Text = null;
            _changesContent?.ClearImage(); _changesDiff.Text = null; _selectedPatch = null; _hunks.ItemsSource = null;
            if (changedRepository) { _fileContent?.ClearImage(); _fileView.Text = null; _filePath.Text = null; }
            if ((_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab" && SelectedTreePath is string treePath
                && (!_fileGrepSettings.ShowSearchBox || _fileGrepSettings.Expression.Length == 0))
            { await LoadTreeFileAsync(repository, treePath); }
            else if ((_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab" && SelectedTreePath is null) { ShowSelectedTreeFolder(); }
            if (_commits.SelectedItem is GitCommit selectedCommit)
            {
                await LoadCommitFilesAsync(repository, selectedCommit.Hash, selectedRevisionDiff);
                UpdateCommitInfo(selectedCommit);
            }
            else
            {
                ClearRevisionDiff();
                _commitContent?.ClearImage(); ClearCommitInformation(); _gpgInfo.Text = null;
                _diff.Text = "This repository has no commits yet.";
            }
            if (SelectedWorkingChange is GitChange change)
            {
                bool staged = change.IsStaged && (!change.IsModified || _stagedDiff.IsChecked == true);
                _stagedDiff.IsChecked = staged;
                await LoadChangedFileContentAsync(repository, change, staged);
                _selectedPatch = _changesDiffView?.CanStagePatch == true ? GitPatch.Parse(_changesDiff.Text ?? "").FirstOrDefault() : null;
                _hunks.ItemsSource = _selectedPatch?.Hunks;
            }
            else { _changesContent?.ClearImage(); _changesDiff.Text = "Select a changed file to inspect its diff."; _selectedPatch = null; _hunks.ItemsSource = null; }
        }
        finally { _refreshing = false; RememberRevisionNavigation(); UpdateHistoryPresentation(); }
    }

    private async Task RunAsync(string label, Func<GitRepository, Task> action, bool refresh = true, bool requireRepository = true)
    {
        if (_busy) { return; }
        if (requireRepository && _repository is null) { _state.Text = "Open a repository first"; return; }
        OperationFocus? previousFocus = CaptureOperationFocus();
        bool resumeSelection = BrowserReads.Any(read => read.Current is not null) || _commitInfo.IsLoading;
        CancelBrowserReads();
        _busy = true;
        LastException = null;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetActionsEnabled(false);
        bool success = false;
        try
        {
            _state.Text = label + "…";
            await action(_repository!);
            if (refresh && _repository is not null) { await RefreshDataAsync(_repository); }
            success = true;
        }
        catch (OperationCanceledException) { _state.Text = "Operation cancelled"; AppendOutput(label + ": cancelled"); }
        catch (Exception exception)
        {
            LastException = exception;
            _state.Text = label + " failed — see command output";
            AppendOutput($"{label}: {exception.Message}");
            if (Program.SmokeTest) { Console.Error.WriteLine(exception); _repository = null; }
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            SetActionsEnabled(true);
            if (success) { _state.Text = _repositorySummary; }
            if (_pendingDiffAppearance && !_lifetime.IsCancellationRequested) { await ReloadDiffAppearancesAsync(); }
            if (resumeSelection && !_lifetime.IsCancellationRequested)
            {
                await LoadVisibleRevisionPanelAsync();
                if ((_tabs.SelectedItem as TabItem)?.Name == "ChangesTab") { await LoadSelectedDiffAsync(); }
            }
            else if (!_lifetime.IsCancellationRequested) { await LoadVisibleCommitInformationAsync(); }
            RestoreOperationFocus(previousFocus);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (Button button in _actionButtons) { button.IsEnabled = enabled && !(_repository?.IsBare == true && button.Tag is true); }
        foreach (MenuItem menu in _actionMenus) { menu.IsEnabled = enabled && !(_repository?.IsBare == true && menu.Tag is true); }
        if (_navigationPanel is not null) { _navigationPanel.IsEnabled = enabled; }
        _repositoryPath.IsEnabled = enabled;
        _recent.IsEnabled = enabled;
        _recentMenu.IsEnabled = _favoritesMenu.IsEnabled = enabled;
        if (_dashboard is not null) { _dashboard.IsEnabled = enabled; }
        if (_compareToBaseMenu is not null) { _compareToBaseMenu.IsEnabled = enabled && _comparisonBase is not null; }
        Control[] selectionControls = [_commits, _changes, _references, _remotes, _stashes, _worktrees, _submodules, _configuration, _tree, _navigation, _hunks, _stagedDiff, _historyRevision, _quickHistoryFilter, _filePath, _tabs, _revisionFiles];
        if (_revisionFilesView is not null) { _revisionFilesView.IsEnabled = enabled; }
        if (_changesFilesView is not null) { _changesFilesView.IsEnabled = enabled; }
        foreach (Control control in selectionControls) { control.IsEnabled = enabled; }
        foreach (TabItem tab in _tabs.Items.OfType<TabItem>()) { if (tab.Name == "ChangesTab") { tab.IsEnabled = enabled && _repository?.IsBare != true; } }
        RefreshExternalToolMenus();
        UpdatePartialStageActions();
        if (_cancelToolbarButton is not null) { _cancelToolbarButton.IsVisible = !enabled && _operation is not null; _cancelToolbarButton.IsEnabled = _operation is not null; }
        RefreshPullMenu();
        RefreshStashMenu();
        RefreshWorktreeMenu();
        RefreshBranchMenu();
        RefreshSubmoduleRemovalButton();
        RefreshRebaseMenu();
        RefreshRevisionContextMenu();
        if (enabled) { QueuePendingRevisionDetails(); }
        _browserToolbar?.Place();
    }

    private static bool RequiresWorktree(string label) => label.StartsWith("Stage ", StringComparison.Ordinal) || label.StartsWith("Unstage ", StringComparison.Ordinal)
        || label is "Commit" or "Commit…" or "Checkout" or "Checkout branch…" or "Checkout revision" or "Merge" or "Merge…" or "Rebase onto" or "Rebase…" or "Interactive rebase…"
            or "Reset…" or "Reset branch…" or "Cherry-pick" or "Revert" or "Revert commit" or "Discard…" or "Resolve…"
            or "Compare to working directory" or "Open selected commits with difftool";

    private async Task ExecuteAsync(GitRepository repository, params string[] arguments)
    {
        GitResult result = await repository.RunAsync(arguments, Token);
        AppendOutput(string.Join(" ", arguments.Take(2)) + "\n" + result.StandardOutput + result.StandardError);
    }

    private void AppendOutput(string text)
    {
        string next = (_output.Text ?? "") + text.TrimEnd() + Environment.NewLine;
        _output.Text = next.Length > 100_000 ? next[^100_000..] : next;
        _output.CaretIndex = _output.Text.Length;
    }

}
