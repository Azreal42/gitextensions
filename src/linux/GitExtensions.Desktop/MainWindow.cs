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
    private readonly ListBox _commits = new();
    private readonly CommitGraph _historyGraph = new();
    private readonly ListBox _changes = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _references = new();
    private readonly ListBox _remotes = new();
    private readonly ListBox _stashes = new();
    private readonly ListBox _worktrees = new();
    private readonly ListBox _submodules = new();
    private readonly ListBox _configuration = new();
    private readonly ListBox _tree = new();
    private readonly TextBox _historyRevision = new() { Watermark = "Revision (default: all refs)", Width = 240 };
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

    internal bool IsBusy => _busy;
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

        _recent.ItemsSource = _preferences.RecentRepositories;
        _historyQuery = new GitLogQuery { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000) };
        _historyWindow = _historyQuery.Count;
        _commits.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(HeightProperty, CommitGraph.RowHeight),
                new Setter(MinHeightProperty, CommitGraph.RowHeight),
                new Setter(PaddingProperty, new Thickness(4, 1)),
                new Setter(MarginProperty, new Thickness(0))
            }
        });
        _commits.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer viewer) { _historyGraph.VerticalOffset = viewer.Offset.Y; }
        });
        _recent.SelectionChanged += async (_, _) =>
        {
            if (_recent.SelectedItem is string directory && !_busy)
            {
                await OpenRepositoryAsync(directory);
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
        _changes.ItemTemplate = TextTemplate<GitChange>(change => $"{change.IndexStatus}{change.WorkTreeStatus}  {change.Path}{(change.OriginalPath is null ? "" : " ← " + change.OriginalPath)}{(change.IsConflict ? "  [conflict]" : "")}");
        _references.ItemTemplate = TextTemplate<GitReference>(reference => $"{(reference.IsCurrent ? "● " : "  ")}{reference.ShortName}{(reference.IsTag ? "  [tag]" : reference.IsRemote ? "  [remote]" : "")}{(reference.Upstream is null ? "" : " → " + reference.Upstream)}");
        _remotes.ItemTemplate = TextTemplate<GitRemote>(remote => $"{remote.Name}  {string.Join(", ", remote.FetchUrls)}");
        _stashes.ItemTemplate = TextTemplate<GitStash>(stash => $"{stash.Reference}  {stash.Subject}");
        _worktrees.ItemTemplate = TextTemplate<GitWorktree>(worktree => $"{worktree.Path}  [{worktree.Branch ?? "detached"}]{(worktree.LockedReason is null ? "" : "  [locked]")}");
        _submodules.ItemTemplate = TextTemplate<GitSubmodule>(module => $"{module.Path}  {module.Url}  {(module.IsInitialized ? module.Head : "not initialized")}");
        _commits.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && _commits.SelectedItem is GitCommit commit)
            {
                await RunAsync("Commit diff", async repository =>
                {
                    await LoadCommitFilesAsync(repository, commit.Hash);
                    UpdateCommitInfo(commit);
                    _tree.ItemsSource = (await repository.GetTreeAsync(commit.Hash, cancellationToken: Token)).Select(entry => entry.Path).ToArray();
                    _fileContent?.ClearImage();
                    _fileView.Text = null;
                    _gpgInfo.Text = null;
                }, refresh: false);
            }
        };
        _changes.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && _changes.SelectedItem is GitChange)
            {
                await LoadSelectedDiffAsync();
            }
        };
        _tree.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && _tree.SelectedItem is string path)
            {
                _filePath.Text = path;
                await ShowFileAsync(path);
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
        _historyRevision.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !_busy) { e.Handled = true; await ReloadHistoryAsync(); }
        };
        Content = BuildLayout();
        ApplyTheme(_preferences.Theme);
        Opened += async (_, _) =>
        {
            if (_opened)
            {
                return;
            }

            _opened = true;
            string? path = Program.RepositoryPath ?? _preferences.RecentRepositories.FirstOrDefault();
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
            _lifetime.Cancel(); _operation?.Cancel();
            _commitContent?.Dispose(); _changesContent?.Dispose(); _fileContent?.Dispose();
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
        DockPanel changes = Panel(_changes, Row(Button("Stage selected", () => StageAsync(false)), Button("Stage all", () => StageAsync(true)),
            Button("Unstage selected", () => UnstageAsync(false)), Button("Unstage all", () => UnstageAsync(true)),
                Button("Discard…", DiscardAsync), Button("Resolve…", ResolveConflictAsync)));
        StackPanel commit = new() { Spacing = 8, Children = { _commitMessage, Row(_amend, Button("Commit", CommitAsync)) }, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(commit, Dock.Bottom);
        changes.Children.Insert(1, commit);
        return
        [
            new TabItem { Header = "History", Content = Split(history, _diff) },
            new TabItem { Header = "Changes", Content = Split(changes, BuildChangesDiff()) },
            new TabItem { Header = "Branches & tags", Content = Panel(_references, Row(Button("Create branch…", CreateBranchAsync), Button("Checkout", CheckoutAsync), Button("Delete…", DeleteReferenceAsync), Button("Rename…", RenameBranchAsync), Button("Create tag…", CreateTagAsync)), Row(Button("Merge", MergeAsync), Button("Rebase onto", RebaseAsync), Button("Continue…", () => ManageOperationAsync(GitOperationAction.Continue)), Button("Abort…", () => ManageOperationAsync(GitOperationAction.Abort)), Button("Skip…", () => ManageOperationAsync(GitOperationAction.Skip)))) },
            new TabItem { Header = "Remotes", Content = Panel(_remotes, Row(Button("Add…", AddRemoteAsync), Button("Edit URL…", EditRemoteAsync), Button("Remove…", RemoveRemoteAsync), Button("Fetch all", () => RunAsync("Fetch all", r => ExecuteAsync(r, "fetch", "--all", "--prune"))), Button("Pull…", PullAsync), Button("Push…", PushAsync), Button("Browse host", BrowseRemoteAsync))) },
            new TabItem { Header = "Stashes", Content = Panel(_stashes, Row(Button("Create…", StashAsync), Button("Apply", () => ApplyStashAsync(false)), Button("Pop", () => ApplyStashAsync(true)), Button("Delete…", DropStashAsync), Button("Show diff", ShowStashAsync))) },
            new TabItem { Header = "Worktrees", Content = Panel(_worktrees, Row(Button("Add…", AddWorktreeAsync), Button("Open", OpenWorktreeAsync), Button("Remove…", RemoveWorktreeAsync), Button("Prune", () => RunAsync("Prune worktrees", r => r.PruneWorktreesAsync(cancellationToken: Token))))) },
            new TabItem { Header = "Submodules", Content = Panel(_submodules, Row(Button("Add…", AddSubmoduleAsync), Button("Initialize / update", UpdateSubmodulesAndBuildAsync), Button("Sync URLs", () => RunAsync("Sync submodules", r => r.SyncSubmodulesAsync(cancellationToken: Token))), Button("Open", OpenSubmoduleAsync))) },
            new TabItem { Header = "Files", Content = Split(Panel(_tree, Row(_filePath, Button("Blame", BlameAsync), Button("File history", FileHistoryAsync), Button("Search…", SearchAsync))), _fileContent = new FileContentView(_fileView)) },
            new TabItem { Header = "Configuration", Content = Panel(_configuration, Row(Button("Set local…", () => SetConfigurationAsync(GitConfigScope.Local)), Button("Set global…", () => SetConfigurationAsync(GitConfigScope.Global)), Button("Unset…", UnsetConfigurationAsync), Button("Diff tool…", DiffToolAsync))) },
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
        _hunks.ItemTemplate = TextTemplate<GitPatchHunk>(hunk => hunk.Header);
        _stagedDiff.IsCheckedChanged += async (_, _) => await LoadSelectedDiffAsync();
        _changesContent = new FileContentView(_changesDiff, _changesDiffView);
        DockPanel panel = Panel(_changesContent, Row(_stagedDiff, Button("Refresh diff", LoadSelectedDiffAsync)),
            Row(Button("Stage selected lines", () => ApplyLinesAsync(false)), Button("Unstage selected lines", () => ApplyLinesAsync(true))),
            Row(Button("Stage selected hunks", () => ApplyHunksAsync(false)), Button("Unstage selected hunks", () => ApplyHunksAsync(true))), _hunks);
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
            await _backgroundFetch.UnregisterAsync();
            _repository = candidate;
            _navigationRevisionFilter = null;
            _historyQuery = new GitLogQuery { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000) };
            _historyWindow = _historyQuery.Count;
            _historyRevision.Text = null;
            if (candidate.IsBare) { SelectDetailsTab("DiffTab"); }
            _repositoryPath.Text = candidate.Path;
            Title = $"Git Extensions — {candidate.Path}";
            await RefreshDataAsync(candidate);
            List<string> recent = _preferences.RecentRepositories.Where(r => r != candidate.Path).Prepend(candidate.Path).Take(20).ToList();
            _preferences = _preferences with { RecentRepositories = recent };
            _recent.ItemsSource = recent;
            try { _preferencesStore.Save(_preferences); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { AppendOutput($"Unable to save preferences: {exception.Message}"); }
            await RegisterBackgroundFetchAsync(candidate);
        }, refresh: false, requireRepository: false);
        if (_repository == previous && previous is null) { _state.Text = "Could not open repository. See command output."; }
    }

    private Task RefreshAsync() => RunAsync("Refresh", RefreshDataAsync, refresh: false);

    private async Task RefreshDataAsync(GitRepository repository)
    {
        _refreshing = true;
        try
        {
            string? selectedPath = (_changes.SelectedItem as GitChange)?.Path;
            string? selectedHash = (_commits.SelectedItem as GitCommit)?.Hash;
            string? selectedReferenceName = (_references.SelectedItem as GitReference)?.Name;
            GitStatus status = await repository.GetStatusAsync(Token);
            _repositorySummary = $"{(repository.IsBare ? T("Bare repository") + "  ·  " : "")}{status.Branch ?? "detached"}  ·  {status.Changes.Count} changes  ·  ↑{status.Ahead} ↓{status.Behind}";
            _state.Text = _repositorySummary;
            _changes.ItemsSource = status.Changes;
            _changes.SelectedItem = status.Changes.FirstOrDefault(change => change.Path == selectedPath);
            IReadOnlyList<GitReference> references = await repository.GetReferencesAsync(Token);
            _references.ItemsSource = references;
            _references.SelectedItem = references.FirstOrDefault(reference => reference.Name == selectedReferenceName);
            if (_navigationRevisionFilter is string navigationFilter && _historyRevision.Text == navigationFilter && !references.Any(reference => reference.Name == navigationFilter))
            {
                _navigationRevisionFilter = null;
                _historyRevision.Text = null;
                _historyQuery = _historyQuery with { All = true, Revision = null };
                _historyWindow = _historyQuery.Count;
            }
            GitLogQuery query = _historyQuery with
            {
                Count = _historyWindow + 1,
                Skip = 0,
                Revision = string.IsNullOrWhiteSpace(_historyRevision.Text) ? (_historyQuery.All ? null : "HEAD") : _historyRevision.Text
            };
            IReadOnlyList<GitCommit> history = status.IsUnborn ? [] : await repository.GetLogAsync(query, Token);
            _historyHasMore = history.Count > _historyWindow && _historyWindow < 100000;
            _history = history.Take(_historyWindow).ToArray();
            if (_historyMore is not null) { _historyMore.IsVisible = _historyHasMore; }
            _historyGraph.Commits = _history;
            _historyGraph.References = references;
            _historyGraph.CurrentHead = status.Head;
            _commits.ItemsSource = _historyGraph.OrderedCommits;
            _commits.SelectedItem = _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == selectedHash) ?? _historyGraph.OrderedCommits.FirstOrDefault();
            if (_commits.SelectedItem is GitCommit selectedCommit)
            {
                await LoadCommitFilesAsync(repository, selectedCommit.Hash);
                UpdateCommitInfo(selectedCommit);
            }
            else
            {
                _revisionFiles.ItemsSource = null; _revisionChanges = null; _diffParent.IsVisible = false;
                _commitContent?.ClearImage(); _commitInfo.Text = null; _gpgInfo.Text = null;
                _diff.Text = "This repository has no commits yet.";
            }
            _remotes.ItemsSource = await repository.GetRemotesAsync(Token);
            _stashes.ItemsSource = await repository.GetStashesAsync(Token);
            _worktrees.ItemsSource = await repository.GetWorktreesAsync(Token);
            _submodules.ItemsSource = await repository.GetSubmodulesAsync(Token);
            UpdateBrowserNavigation(references, _stashes.ItemsSource.Cast<GitStash>().ToArray(), _submodules.ItemsSource.Cast<GitSubmodule>().ToArray(), _worktrees.ItemsSource.Cast<GitWorktree>().ToArray());
            _configuration.ItemsSource = (await repository.GetConfigAsync(cancellationToken: Token)).Select(entry => $"{entry.Key} = {entry.Value}").ToList();
            _tree.ItemsSource = status.IsUnborn ? [] : (await repository.GetTreeAsync(CurrentRevision, cancellationToken: Token)).Select(entry => entry.Path).ToArray();
            if (_changes.SelectedItem is GitChange change)
            {
                bool staged = change.IsStaged && (!change.IsModified || _stagedDiff.IsChecked == true);
                _stagedDiff.IsChecked = staged;
                await LoadChangedFileContentAsync(repository, change, staged);
                _selectedPatch = GitPatch.Parse(_changesDiff.Text ?? "").FirstOrDefault();
                _hunks.ItemsSource = _selectedPatch?.Hunks;
            }
            else { _changesContent?.ClearImage(); _changesDiff.Text = "Select a changed file to inspect its diff."; _selectedPatch = null; _hunks.ItemsSource = null; }
        }
        finally { _refreshing = false; }
    }

    private async Task RunAsync(string label, Func<GitRepository, Task> action, bool refresh = true, bool requireRepository = true)
    {
        if (_busy) { return; }
        if (requireRepository && _repository is null) { _state.Text = "Open a repository first"; return; }
        _busy = true;
        LastException = null;
        SetActionsEnabled(false);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
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
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (Button button in _actionButtons) { button.IsEnabled = enabled && !(_repository?.IsBare == true && button.Tag is true); }
        foreach (MenuItem menu in _actionMenus) { menu.IsEnabled = enabled && !(_repository?.IsBare == true && menu.Tag is true); }
        _repositoryPath.IsEnabled = enabled;
        _recent.IsEnabled = enabled;
        Control[] selectionControls = [_commits, _changes, _references, _remotes, _stashes, _worktrees, _submodules, _configuration, _tree, _navigation, _hunks, _stagedDiff, _historyRevision, _filePath, _tabs, _revisionFiles, _diffParent];
        foreach (Control control in selectionControls) { control.IsEnabled = enabled; }
        foreach (TabItem tab in _tabs.Items.OfType<TabItem>()) { if (tab.Name == "ChangesTab") { tab.IsEnabled = enabled && _repository?.IsBare != true; } }
    }

    private static bool RequiresWorktree(string label) => label.StartsWith("Stage ", StringComparison.Ordinal) || label.StartsWith("Unstage ", StringComparison.Ordinal)
        || label is "Commit" or "Commit…" or "Checkout" or "Checkout branch…" or "Checkout revision" or "Merge" or "Merge…" or "Rebase onto" or "Rebase…" or "Interactive rebase…"
            or "Reset…" or "Reset branch…" or "Cherry-pick" or "Revert" or "Revert commit" or "Discard…" or "Resolve…" or "Pull" or "Pull…";

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
