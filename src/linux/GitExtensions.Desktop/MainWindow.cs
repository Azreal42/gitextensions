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
using Avalonia.VisualTree;
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
    private readonly TreeView _tree = new() { SelectionMode = SelectionMode.Multiple, Focusable = true };
    private readonly BranchHistoryFilterComboBox _historyRevision = new();
    private readonly TextBox _filePath = new() { Watermark = "Repository-relative file path", MinWidth = 240 };
    private readonly TextBox _fileView = ReadOnlyText();
    private readonly TabControl _tabs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PreferencesStore _preferencesStore = new();
    private DesktopPreferences _preferences = new();
    private readonly DesktopFontSession _desktopFonts = DesktopFonts.Active;
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
    private Exception? _lastException;
    internal Exception? LastException
    {
        get => _lastException;
        private set
        {
            if (!ReferenceEquals(_lastException, value)) { InvalidateErrorReport(); }
            _lastException = value;
        }
    }
    private readonly Button _errorDetails = new() { Name = "ViewErrorDetails", IsVisible = false, MinHeight = 22, Padding = new Thickness(6, 0), Margin = new Thickness(8, 0, 0, 0) };
    private ExceptionReportSnapshot? _errorSnapshot;
    private ExceptionReportContext? _errorContext;
    private string? _operationReportDirectory;
    private long _errorGeneration;
    private CancellationTokenSource? _errorReportCancellation;
    private Task _errorReportCompletion = Task.CompletedTask;
    internal Task ErrorReportCompletion => _errorReportCompletion;
    internal ExceptionReportSnapshot? ErrorReportSnapshot => _errorSnapshot;
    internal ExceptionReportContext? ErrorReportContextSnapshot => _errorContext;
    internal Func<string, CancellationToken, Task>? ErrorReportCopyTextAsync { get; set; }


    public MainWindow() : this(new BrowseRequest()) { }

    internal MainWindow(BrowseRequest request) : this(request, MainWindowKind.Browse) { }

    internal MainWindow(BrowseRequest request, MainWindowKind windowKind, ResolvedCommitDiffSource? commitDiff = null)
    {
        if (!Enum.IsDefined(windowKind)) { throw new ArgumentOutOfRangeException(nameof(windowKind)); }
        WindowKind = windowKind;
        if ((windowKind == MainWindowKind.CommitDiff) != (commitDiff is not null)) { throw new ArgumentException("CommitDiff requires its resolved commit source.", nameof(commitDiff)); }
        _commitDiffSource = commitDiff; _displayedCommit = commitDiff?.Commit;
        _browseRequest = request ?? throw new ArgumentNullException(nameof(request));
        DetailedGraphSettings.Bind(_historyGraph);
        _desktopFonts.Apply(this, DesktopFontRole.Application);
        _desktopFonts.Apply(_output, DesktopFontRole.Monospace);
        ConsoleStylePresentation.Bind(_output, defaultFont: _desktopFonts.For(DesktopFontRole.Monospace));
        if (IsStandaloneDiff) { Name = IsCommitDiff ? "CommitDiffWindow" : "RevisionLogWindow"; }
        Title = IsStandaloneDiff ? T("Diff") : "Git Extensions";
        Width = IsCommitDiff ? 717 : IsRevisionLog ? 750 : 1280;
        Height = IsStandaloneDiff ? 529 : 840;
        MinWidth = IsCommitDiff ? 150 : IsRevisionLog ? 250 : 860;
        MinHeight = IsCommitDiff ? 148 : IsRevisionLog ? 250 : 620;
        if (IsStandaloneDiff) { WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        try
        {
            _preferences = _preferencesStore.Load();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            AppendOutput($"Unable to read preferences: {exception.Message}");
        }

        InitializeScripts();
        DesktopHotkeys.Reload();
        if (DesktopHotkeys.LoadError is { } hotkeyError) { AppendOutput($"Unable to read hotkeys: {hotkeyError.Message}"); }

        LoadRepositoryHistory();
        _historyView = _historyViewStore.Load();
        _historyQuery = new GitLogQuery { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000), Reflog = _historyView.ShowReflog };
        _appliedHistoryMaximum = _preferences.MaxRevisionGraphCommits; _appliedHistoryBatchSize = _historyQuery.Count;
        _historyWindow = HistoryPolicy.InitialWindow(_historyQuery.Count);
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
                new TextBlock { Text = commit is null ? "" : $"{commit.Hash[..Math.Min(9, commit.Hash.Length)]}  ·  {commit.AuthorName}  ·  {commit.AuthoredAt.LocalDateTime:g}", FontSize = Math.Max(1, _desktopFonts.For(DesktopFontRole.Application).SizeInDips - 1), Opacity = 0.7 }
            }
        });
        _references.ItemTemplate = TextTemplate<GitReference>(reference => $"{(reference.IsCurrent ? "● " : "  ")}{reference.ShortName}{(reference.IsTag ? "  [tag]" : reference.IsRemote ? "  [remote]" : "")}{(reference.Upstream is null ? "" : " → " + reference.Upstream)}");
        _remotes.ItemTemplate = TextTemplate<GitRemote>(remote => $"{remote.Name}  {string.Join(", ", remote.FetchUrls)}");
        _stashes.ItemTemplate = TextTemplate<GitStash>(stash => $"{stash.Reference}  {stash.Subject}");
        _worktrees.ItemTemplate = TextTemplate<GitWorktree>(worktree => $"{worktree.Path}  [{worktree.Branch ?? "detached"}]{(worktree.LockedReason is null ? "" : "  [locked]")}");
        _submodules.ItemTemplate = TextTemplate<GitSubmodule>(module => $"{module.Path}  {module.Url}  {(module.IsInitialized ? module.Head : "not initialized")}");
        _commits.SelectionChanged += async (_, args) =>
        {
            ObserveQuickSearchSelection(args);
            _parentChildNavigation?.RevisionsSelectionChanged();
            if (!_refreshing) { _browseInitialComparison = null; }
            if (!_refreshing && _commits.SelectedItem is GitCommit) { ClearArtificialHistorySelection(); }
            if (!_refreshing) { CancelRevisionReads(); }
            RememberRevisionNavigation();
            RefreshRebaseMenu();
            RefreshRevisionContextMenu();
            OnGitHubActionsSelectionChanged();
            if (!_refreshing) { UpdateHistoryPresentation(); }
            if (!_refreshing && _commits.SelectedItem is GitCommit commit)
            {
                if (_selectingRevisionForContextMenu) { _contextRevisionDiffPending = true; UpdateCommitInfo(commit); }
                else { await LoadSelectedRevisionDetailsAsync(commit); }
            }
        };
        _commits.DoubleTapped += async (_, args) =>
        {
            if (args.Handled || IsCommitDiff || (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Any(control => control.Name == "RevisionReference") == true) { return; }
            if (SelectedRevisionDiffCommits().FirstOrDefault() is GitCommit commit)
            { args.Handled = true; await ShowCommitDiffAsync(commit); }
            else if (!IsRevisionLog && _selectedArtificialHistoryRevision is null && (_commits.SelectedItems?.Count ?? 0) == 0)
            { args.Handled = true; await ShowRevisionLogAsync(); }
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
                if (SelectedTreePath is string path) { _explicitFileHistoryTreePath = path; _filePath.Text = path; await ShowSelectedTreeFileAsync(path); }
                else
                {
                    if (_fileTreeView?.SelectedFolderPath is string folder) { _explicitFileHistoryTreePath = folder; }
                    ShowSelectedTreeFolder();
                }
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
        InitializeErrorReportAccess();
        if (!IsCommitDiff) { InitializeFileHistoryBrowse(); InitializeGitHubActions(); }
        _commits.AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _workingRevision?.AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _indexRevision?.AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (!IsCommitDiff) { InitializeQuickSearch(); InitializeParentChildNavigation(); ApplyHistoryViewPresentation(); }
        ApplyTheme(_preferences.Theme);
        Opened += async (_, _) =>
        {
            if (_opened)
            {
                return;
            }

            _opened = true;
            if (IsCommitDiff) { await InitializeCommitDiffAsync(); return; }
            GitEnvironmentStartup? environmentStartup = Program.TakeEnvironmentStartup();
            string? explicitPath = environmentStartup is { PreparationError: not null, Arguments.BrowseRequest: not null }
                ? environmentStartup.Arguments.RepositoryArgument ?? environmentStartup.CurrentDirectory ?? Environment.CurrentDirectory
                : _browseRequest.RepositoryPath ?? environmentStartup?.Arguments.RepositoryArgument ?? Program.RepositoryPath;
            string? path = explicitPath ?? (_preferences.StartWithRecentWorkingDir ? _repositoryHistory.StartupRepositoryPath : null);
            bool reopenRecent = explicitPath is null && path is not null;
            if (environmentStartup is not null && path is not null)
            {
                _startupArgumentsToRetry = environmentStartup.Arguments with
                {
                    RepositoryArgument = path,
                    BrowseRequest = environmentStartup.Arguments.BrowseRequest is { } request ? request with { RepositoryPath = path } : new(path)
                };
                _startupDirectoryToRetry = environmentStartup.CurrentDirectory ?? Environment.CurrentDirectory;
            }
            if (path is not null && environmentStartup?.PreparationError is null)
            {
                if (reopenRecent) { await OpenLastVisitedRepositoryAsync(path); }
                else { await OpenRepositoryAsync(path); }
                if (_repository is not null) { _startupArgumentsToRetry = null; }
            }

            if (environmentStartup is not null && (environmentStartup.CheckAtStartup || GitEnvironmentStartup.IsExecutableUnavailable(LastException)))
            { await CheckStartupEnvironmentAsync(); }

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
        AddHandler(KeyDownEvent, HistoryAltKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private CancellationToken Token => _operation?.Token ?? _lifetime.Token;
    private string CurrentRevision => CurrentDisplayedCommit?.Hash ?? "HEAD";

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
            new TabItem { Header = "Remotes", Content = Panel(_remotes, Row(Button("Add…", AddRemoteAsync), Button("Edit URL…", EditRemoteAsync), Button("Remove…", RemoveRemoteAsync), Button("Fetch all", FetchAllWithScriptEventsAsync), Button("Pull…", PullAsync), Button("Push…", PushAsync), Button("Browse host", BrowseRemoteAsync))) },
            new TabItem { Header = "Stashes", Content = Panel(_stashes, Row(Button("Create…", StashAsync), Button("Apply", () => ApplyStashAsync(false)), Button("Pop", () => ApplyStashAsync(true)), Button("Delete…", DropStashAsync), Button("Show diff", ShowStashAsync))) },
            new TabItem { Header = "Worktrees", Content = Panel(_worktrees, Row(Button("Add…", AddWorktreeAsync), Button("Open", OpenWorktreeAsync), Button("Remove…", RemoveWorktreeAsync), Button("Prune", PruneWorktreesAsync))) },
            new TabItem { Header = "Submodules", Content = Panel(_submodules, Row(Button("Add…", AddSubmoduleAsync), Button("Manage…", () => ManageSubmodulesAsync()), BuildSubmoduleRemovalButton(), Button("Open", OpenSubmoduleAsync))) },
            new TabItem { Header = "Files", Content = BuildFileTreePane() },
            new TabItem { Header = "Configuration", Content = Panel(_configuration, Row(Button("Set local…", () => SetConfigurationAsync(GitConfigScope.Local)), Button("Set global…", () => SetConfigurationAsync(GitConfigScope.Global)), Button("Unset…", UnsetConfigurationAsync), Button(_translations.Translate("Git configuration") + "…", DiffToolAsync), Button(_translations.Translate("DiffViewerSettingsPage", "$this", "Text", "Diff viewer") + "…", DiffViewerSettingsAsync))) },
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
        _changesDiffView.FileViewerCommandRequested = command =>
        {
            Button? action = command switch { "StageLines" => _stageLinesButton, "UnstageLines" => _unstageLinesButton, _ => null };
            if (action?.IsEnabled != true || _busy || _workingDiffRead.Current is not null) { return false; }
            action.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            return true;
        };
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

    private static TextBox ReadOnlyText()
    {
        TextBox text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
        DesktopFonts.Apply(text, DesktopFontRole.Code); return text;
    }
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
        string? path = await ExportDialogControls.FolderAsync(this, T("Open Git repository"), new ClonePreferencesStore().Load().EffectiveDestination);
        if (path is not null)
        {
            await OpenRepositoryAsync(path);
        }
    }

    private async Task OpenLastVisitedRepositoryAsync(string path)
    {
        // A stale recorded root must never discover an unrelated parent repository.
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) { return; }
        using WindowWork work = TrackWindowWork();
        try
        {
            GitRepository candidate = await GitRepository.OpenAsync(path, _lifetime.Token);
            bool sameRoot = string.Equals(Path.TrimEndingDirectorySeparator(candidate.Path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.Ordinal);
            bool ownMetadata = File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))
                || candidate.IsBare && File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects"));
            if (!sameRoot && !ownMetadata) { return; }
            await OpenRepositoryAsync(candidate.Path);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (GitCommandException error) when (error.Result.ExitCode == 128
            && error.Result.StandardError.StartsWith("fatal: not a git repository", StringComparison.Ordinal)) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or GitCommandException)
        {
            LastException = error;
            AppendOutput("Unable to reopen the last working directory: " + error.Message);
        }
    }

    public async Task OpenRepositoryAsync(string path)
    {
        if (IsCommitDiff) { await InitializeCommitDiffAsync(); return; }
        if (_busy || _lifetime.IsCancellationRequested) { return; }
        GitRepository? previous = _repository;
        await RunAsync("Open repository", async _ =>
        {
            _operationReportDirectory = path;
            GitRepository candidate = await GitRepository.OpenAsync(path, Token);
            await candidate.GetStatusAsync(Token);
            await RefreshDataAsync(candidate, opening: true);
            if (!IsStandaloneDiff)
            {
                try { SaveRepositoryHistory(_repositoryHistory.Visit(candidate.Path)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { AppendOutput($"Unable to save preferences: {exception.Message}"); }
            }
            SetDashboardVisible(false);
            if (!IsStandaloneDiff) { await RegisterBackgroundFetchAsync(candidate); }
            ScheduleGitHubActionsRepositoryChanged(candidate);
        }, refresh: false, requireRepository: false);
        if (_repository == previous && previous is null) { _state.Text = "Could not open repository. See command output."; }
    }

    private async Task RefreshAsync()
    {
        if (IsCommitDiff) { await InitializeCommitDiffAsync(); return; }
        OperationFocus? historyFocus = _commits.IsKeyboardFocusWithin ? CaptureOperationFocus() : null;
        if (historyFocus is not null) { historyFocus = historyFocus with { PreserveUnselectedRow = true }; }
        BranchHistoryFilterComboBox? editing = _historyRevision.IsKeyboardFocusWithin ? _historyRevision
            : _branchFilterOverflow?.IsKeyboardFocusWithin == true ? _branchFilterOverflow : null;
        var bookmark = editing?.CaptureEditorSelection();
        if (_dashboard?.IsVisible == true) { await RefreshDashboardAsync(); }
        else { await RunAsync("Refresh", repository => RefreshDataAsync(repository), refresh: false); }
        ScheduleGitHubActionsRepositoryChanged(_repository);
        RestoreOperationFocus(historyFocus);
        if (!IsBranchFilterOwnerClosing && editing is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } && bookmark is { } selection)
        { editing.RestoreEditorSelection(selection); }
    }

    private async Task RefreshDataAsync(GitRepository repository, bool opening = false)
    {
        if (IsCommitDiff) { await ReloadCommitDiffDataAsync(repository, Token); return; }
        using IDisposable execution = repository.BeginOperation();
        BeginRepositoryBranchRefresh(repository);
        HideQuickSearch();
        _parentChildNavigation?.Clear();
        _parentChildFileFocus = null;
        _refreshing = true;
        try
        {
            ApplyReferenceSorting(repository);
            bool changedRepository = opening && _repository?.Path != repository.Path;
            if (changedRepository) { _quickSearchArtificialSelection = null; _quickSearchLastCommit = null; _quickSearchReturnTab = null; }
            GitCompareRevision? artificialSelection = changedRepository ? null : _selectedArtificialHistoryRevision;
            string? selectedPath = changedRepository ? null : SelectedWorkingChange?.Path;
            string? selectedHash = changedRepository ? null : (_commits.SelectedItem as GitCommit)?.Hash;
            GitCompareRevision? startupSelection = opening && !_browseSelectionApplied ? _browseRequest.SelectedRevision : null;
            GitCompareRevision? startupFirst = opening && !_browseSelectionApplied ? _browseRequest.FirstRevision : null;
            if (startupSelection?.Kind == GitCompareRevisionKind.Commit)
            { startupSelection = startupSelection with { Revision = await repository.ResolveRevisionAsync(startupSelection.Revision, Token) }; selectedHash = startupSelection.Revision; }
            else if (startupSelection is not null) { artificialSelection = startupSelection; }
            if (startupFirst?.Kind == GitCompareRevisionKind.Commit)
            { startupFirst = startupFirst with { Revision = await repository.ResolveRevisionAsync(startupFirst.Revision, Token) }; }
            BrowseInitialComparison? retainedComparison = changedRepository ? null : _browseInitialComparison;
            string[] requiredSelectionHashes = new[] { startupSelection, startupFirst, retainedComparison?.First, retainedComparison?.Selected }.OfType<GitCompareRevision>()
                .Where(source => source.Kind == GitCompareRevisionKind.Commit).Select(source => source.Revision).Distinct(StringComparer.Ordinal).ToArray();
            string[] selectedHashOrder = changedRepository ? [] : (_commits.SelectedItems?.OfType<GitCommit>() ?? []).Select(commit => commit.Hash).Distinct(StringComparer.Ordinal).ToArray();
            string[] selectionRecency = changedRepository ? [] : _historySelectionOrder.ToArray();
            string? parentChildAnchor = changedRepository ? null : _parentChildLatestCommit;
            HashSet<string> selectedHashes = selectedHashOrder.ToHashSet(StringComparer.Ordinal);
            object? selectedRevisionDiff = changedRepository ? null : CaptureRevisionFileSelection();
            string? selectedReferenceName = opening ? null : _referenceSelectionOverride ?? (_references.SelectedItem as GitReference)?.Name;
            GitLogQuery configuredQuery = opening ? new GitLogQuery
            { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000), Reflog = _historyView.ShowReflog,
                Path = _browseRequest.PathFilter?.Path } : _historyQuery;
            HistoryLoadingPolicy policy = HistoryPolicy;
            int historyWindow = policy.ClampWindow(opening ? configuredQuery.Count : _historyWindow);
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
                historyWindow = policy.InitialWindow(configuredQuery.Count);
            }
            configuredQuery = ConfigureHistoryViewQuery(configuredQuery);
            GitLogQuery query = configuredQuery with
            {
                Count = policy.ReadCount(historyWindow),
                Skip = 0,
                Revision = configuredQuery.Revision,
                BranchFilter = configuredQuery.BranchFilter
            };
            var historyRead = await ReadBrowseHistoryAsync(repository, query, status.IsUnborn, Token);
            IReadOnlyList<GitCommit> history = historyRead.Commits;
            // -commit selects a row. Expand from the newest page; it must never become a revision/ancestor filter.
            while (requiredSelectionHashes.Any(hash => !history.Take(historyWindow).Any(commit => commit.Hash == hash))
                && policy.HasMore(history.Count, historyWindow))
            {
                historyWindow = policy.NextWindow(historyWindow, configuredQuery.Count);
                query = query with { Count = policy.ReadCount(historyWindow) };
                historyRead = await ReadBrowseHistoryAsync(repository, query, status.IsUnborn, Token);
                history = historyRead.Commits;
            }
            IReadOnlyList<GitRemote> remotes = await repository.GetRemotesAsync(Token);
            IReadOnlyList<GitStash> stashes = await repository.GetStashesAsync(Token);
            _supportsStashStaged ??= await repository.SupportsStashStagedAsync(Token);
            IReadOnlyList<GitWorktree> worktrees = await repository.GetWorktreesAsync(Token);
            IReadOnlyList<GitSubmodule> submodules = await repository.GetSubmodulesAsync(Token);
            await ReadGeneralStatusDetailsAsync(repository, status, submodules, Token);
            var configuration = await repository.GetConfigAsync(cancellationToken: Token);
            if (_fileMenuAdditionalOperations is not null) { await _fileMenuAdditionalOperations.RefreshAsync(repository, Token); }
            GitCommit[] visibleHistory = history.Take(historyWindow).ToArray();
            await LoadHistoryViewMetadataAsync(repository, visibleHistory, Token);
            string treeRevision = visibleHistory.FirstOrDefault(commit => commit.Hash == selectedHash)?.Hash
                ?? visibleHistory.FirstOrDefault(commit => selectedHashes.Contains(commit.Hash))?.Hash
                ?? visibleHistory.FirstOrDefault()?.Hash ?? "HEAD";
            IReadOnlyList<GitComparisonTreeEntry>? artificialTree = artificialSelection is not null
                ? await repository.GetComparisonTreeAsync(artificialSelection, Token) : null;
            IReadOnlyList<GitTreeEntry> tree = artificialTree is not null || status.IsUnborn && visibleHistory.Length == 0
                ? [] : await repository.GetTreeAsync(treeRevision, cancellationToken: Token);
            Token.ThrowIfCancellationRequested();
            if (_fileHistoryTrackedFilter != configuredQuery.Path)
            { _explicitFileHistoryDiffPath = null; _explicitFileHistoryTreePath = null; _fileHistoryTrackedFilter = configuredQuery.Path; }
            _browseFileHistory = historyRead.Graph;
            if (opening)
            {
                await _backgroundFetch.UnregisterAsync();
                Token.ThrowIfCancellationRequested();
                _repository = repository;
                if (changedRepository) { _comparisonBase = null; _navigationSelectionKey = null; _authoredEmail = null; }
                _navigation.SelectedItem = null;
                _navigationRevisionFilter = null;
                _repositoryPath.Text = repository.Path;
                Title = IsRevisionLog ? T("Diff") : $"Git Extensions — {repository.Path}" + (configuredQuery.Path is null ? "" : " (" + configuredQuery.Path + ")");
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
            RefreshGeneralStatusPresentation(status, stashes);
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
            _configuredUserName = configuration.LastOrDefault(entry => entry.Key == "user.name").Value;
            _configuration.ItemsSource = configuration.Select(entry => $"{entry.Key} = {entry.Value}").ToList();
            if (artificialSelection is not null && artificialTree is not null)
            { SetComparisonFileTreePaths(repository, artificialSelection, artificialTree); }
            else { SetFileTreePaths(repository, treeRevision, tree); }
            UpdateBrowserNavigation(references, stashes, submodules, worktrees);
            _historyHasMore = policy.HasMore(history.Count, _historyWindow);
            _history = visibleHistory;
            if (changedRepository) { _browseInitialComparison = null; }
            if (startupFirst is not null && startupSelection is not null)
            { _browseInitialComparison = new(startupFirst, startupSelection); }
            if (_historyMore is not null) { _historyMore.IsVisible = _historyHasMore; }
            _historyGraph.OnlyFirstParent = configuredQuery.FirstParent;
            _historyGraph.Commits = _history;
            _historyGraph.References = HistoryViewReferences(references);
            _historyGraph.CurrentHead = status.Head;
            _highlightedHistoryBranch = null;
            _commits.ItemsSource = _historyGraph.OrderedCommits;
            _commits.SelectedItem = _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == selectedHash)
                ?? _historyGraph.OrderedCommits.FirstOrDefault(commit => selectedHashes.Contains(commit.Hash))
                ?? (startupSelection is null && artificialSelection is null ? _historyGraph.OrderedCommits.FirstOrDefault() : null);
            foreach (string hash in selectedHashOrder)
            {
                GitCommit? selected = _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == hash);
                if (selected is not null && selected.Hash != (_commits.SelectedItem as GitCommit)?.Hash) { _commits.SelectedItems?.Add(selected); }
            }
            if (startupFirst?.Kind == GitCompareRevisionKind.Commit
                && _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == startupFirst.Revision) is { } firstCommit
                && _commits.SelectedItems?.Contains(firstCommit) != true)
            { _commits.SelectedItems?.Add(firstCommit); }
            if (selectionRecency.Length > 0)
            {
                HashSet<string> selected = (_commits.SelectedItems?.OfType<GitCommit>() ?? []).Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);
                string[] added = _historySelectionOrder.Where(hash => !selectionRecency.Contains(hash, StringComparer.Ordinal)).ToArray();
                _historySelectionOrder.Clear();
                _historySelectionOrder.AddRange(selectionRecency.Concat(added).Where(selected.Contains));
                _quickSearchLastCommit = _historySelectionOrder.LastOrDefault();
            }
            if (parentChildAnchor is not null && _history.Any(commit => commit.Hash == parentChildAnchor)) { _parentChildLatestCommit = parentChildAnchor; }
            ClearRevisionDiff();
            _commitContent?.ClearImage(); _diff.Text = null; ClearCommitInformation(); _gpgInfo.Text = null;
            _changesContent?.ClearImage(); _changesDiff.Text = null; _selectedPatch = null; _hunks.ItemsSource = null;
            if (changedRepository) { _fileContent?.ClearImage(); _fileView.Text = null; _filePath.Text = null; }
            _selectedArtificialHistoryRevision = artificialSelection;
            _browseSelectionApplied = true;
            UpdateArtificialHistorySelectionAppearance();
            if ((_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab" && SelectedTreePath is string treePath
                && (!_fileGrepSettings.ShowSearchBox || _fileGrepSettings.Expression.Length == 0))
            { await LoadTreeFileAsync(repository, treePath); }
            else if ((_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab" && SelectedTreePath is null) { ShowSelectedTreeFolder(); }
            if (UsesArtificialInitialComparison && _browseInitialComparison is { } initialPair)
            {
                await LoadArtificialHistoryComparisonAsync(repository, initialPair.First, initialPair.Selected, Token);
                if (initialPair.Selected.Kind == GitCompareRevisionKind.Commit && _commits.SelectedItem is GitCommit selected) { UpdateCommitInfo(selected); }
            }
            else if (_selectedArtificialHistoryRevision is { } artificial)
            { await LoadArtificialHistoryRevisionAsync(repository, artificial, Token); }
            else if (_commits.SelectedItem is GitCommit selectedCommit)
            {
                await LoadCommitFilesAsync(repository, selectedCommit.Hash, selectedRevisionDiff);
                UpdateCommitInfo(selectedCommit);
            }
            else
            {
                ClearRevisionDiff();
                _commitContent?.ClearImage(); ClearCommitInformation(); _gpgInfo.Text = null;
                _diff.Text = status.IsUnborn ? "This repository has no commits yet."
                    : selectedHash is not null ? T("The revision is not visible with the current history filters.") + " " + selectedHash
                    : T("No commits match the current history filters.");
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
        RequestAutomaticUpdateCheck();
    }

    private async Task RunAsync(string label, Func<GitRepository, Task> action, bool refresh = true, bool requireRepository = true)
    {
        if (_busy || _lifetime.IsCancellationRequested) { return; }
        if (requireRepository && _repository is null) { _state.Text = "Open a repository first"; return; }
        InvalidateErrorReport();
        _operationReportDirectory = _repository?.Path;
        using IDisposable execution = _repository?.BeginOperation() ?? GitEnvironment.BeginOperation();
        using WindowWork work = TrackWindowWork();
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
            ExceptionReportSnapshot? captured = !Program.SmokeTest && !_lifetime.IsCancellationRequested && IsVisible
                ? ExceptionReportSnapshot.Capture(exception) : null;
            _state.Text = label + " failed — see command output";
            AppendOutput($"{label}: {captured?.Root.Message ?? ExceptionReportSnapshot.MessageOf(exception)}");
            if (Program.SmokeTest) { Console.Error.WriteLine(exception); _repository = null; }
            else if (!_lifetime.IsCancellationRequested && IsVisible)
            {
                _errorSnapshot = captured;
                _errorContext = ExceptionReportContext.Capture(label, _operationReportDirectory, DesktopVersion, exception);
                _errorDetails.IsVisible = true; _errorDetails.IsEnabled = _errorReportCompletion.IsCompleted;
            }
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _operationReportDirectory = null;
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
            if (!_lifetime.IsCancellationRequested) { RestoreOperationFocus(previousFocus); }
        }
    }

    private void InitializeErrorReportAccess()
    {
        _errorDetails.Content = _translations.Translate("TranslatedStrings", "_buttonViewDetails", "Text", "View details");
        ToolTip.SetTip(_errorDetails, _errorDetails.Content);
        _state.TextTrimming = TextTrimming.CharacterEllipsis;
        // Keep the original status text and its border. The explicit action replaces no workflow UI.
        if (_state.Parent is Border status)
        {
            status.Child = null;
            Grid content = new() { ColumnDefinitions = new("*,Auto") };
            content.Children.Add(_state); Grid.SetColumn(_errorDetails, 1); content.Children.Add(_errorDetails); status.Child = content;
        }
        _errorDetails.Click += async (_, _) =>
        {
            if (_busy || _lifetime.IsCancellationRequested || !_errorReportCompletion.IsCompleted
                || _errorSnapshot is not { } snapshot || _errorContext is not { } context) { return; }
            _errorReportCompletion = ShowCapturedErrorAsync(snapshot, context, _errorGeneration);
            await _errorReportCompletion;
        };
        Closing += (_, _) => InvalidateErrorReport();
    }

    private void InvalidateErrorReport()
    {
        ++_errorGeneration; _errorSnapshot = null; _errorContext = null;
        _errorDetails.IsVisible = _errorDetails.IsEnabled = false;
        _errorReportCancellation?.Cancel();
    }

    private async Task ShowCapturedErrorAsync(ExceptionReportSnapshot snapshot, ExceptionReportContext context, long generation)
    {
        using WindowWork work = TrackWindowWork();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _errorReportCancellation = cancellation; _errorDetails.IsEnabled = false;
        bool quit = false;
        try
        {
            BugReportDecision decision = await Dialogs.ShowErrorAsync(this, snapshot, context, _translations, cancellation.Token,
                ErrorReportCopyTextAsync, (uri, token) => { token.ThrowIfCancellationRequested(); return OpenWebAsync(uri.AbsoluteUri); },
                ownerCloseRequested: InvalidateErrorReport);
            quit = decision == BugReportDecision.Quit && !cancellation.IsCancellationRequested && generation == _errorGeneration;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsCancellationRequested) { AppendOutput(T("Unable to display error details.")); } }
        finally
        {
            if (ReferenceEquals(_errorReportCancellation, cancellation)) { _errorReportCancellation = null; }
            if (!_lifetime.IsCancellationRequested && IsVisible)
            {
                _errorDetails.IsEnabled = _errorSnapshot is not null;
                if (generation == _errorGeneration && _errorSnapshot is not null) { _errorDetails.Focus(); }
            }
        }
        // This job runs after this lease unwinds. Main's normal Closing path drains every other owner.
        if (quit) { Dispatcher.UIThread.Post(() => { if (IsVisible && generation == _errorGeneration) { Close(); } }); }
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
        RefreshScriptsEnabled();
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
