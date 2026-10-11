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
    private readonly TextBlock _state = new() { Text = "Open a repository to begin", VerticalAlignment = VerticalAlignment.Center };
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

    internal bool IsBusy => _busy;

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
                    _diff.Text = await repository.GetCommitDiffAsync(commit.Hash, cancellationToken: Token);
                    UpdateCommitInfo(commit);
                    _tree.ItemsSource = (await repository.GetTreeAsync(commit.Hash, cancellationToken: Token)).Select(entry => entry.Path).ToArray();
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
        Closed += (_, _) => { _lifetime.Cancel(); _operation?.Cancel(); };
        KeyDown += HandleShortcuts;
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
            new TabItem { Header = "Submodules", Content = Panel(_submodules, Row(Button("Add…", AddSubmoduleAsync), Button("Initialize / update", () => RunAsync("Update submodules", r => r.UpdateSubmodulesAsync(cancellationToken: Token))), Button("Sync URLs", () => RunAsync("Sync submodules", r => r.SyncSubmodulesAsync(cancellationToken: Token))), Button("Open", OpenSubmoduleAsync))) },
            new TabItem { Header = "Files", Content = Split(Panel(_tree, Row(_filePath, Button("Blame", BlameAsync), Button("File history", FileHistoryAsync), Button("Search…", SearchAsync))), _fileView) },
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
        DockPanel panel = Panel(_changesDiffView, Row(_stagedDiff, Button("Refresh diff", LoadSelectedDiffAsync)),
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
        Button button = new() { Content = T(label) };
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
            GitRepository candidate = new(path);
            await candidate.GetStatusAsync(Token);
            _repository = candidate;
            _repositoryPath.Text = candidate.Path;
            Title = $"Git Extensions — {candidate.Path}";
            await RefreshDataAsync(candidate);
            List<string> recent = _preferences.RecentRepositories.Where(r => r != candidate.Path).Prepend(candidate.Path).Take(20).ToList();
            _preferences = _preferences with { RecentRepositories = recent };
            _recent.ItemsSource = recent;
            try { _preferencesStore.Save(_preferences); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { AppendOutput($"Unable to save preferences: {exception.Message}"); }
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
            _repositorySummary = $"{status.Branch ?? "detached"}  ·  {status.Changes.Count} changes  ·  ↑{status.Ahead} ↓{status.Behind}";
            _state.Text = _repositorySummary;
            _changes.ItemsSource = status.Changes;
            _changes.SelectedItem = status.Changes.FirstOrDefault(change => change.Path == selectedPath);
            IReadOnlyList<GitReference> references = await repository.GetReferencesAsync(Token);
            _references.ItemsSource = references;
            _references.SelectedItem = references.FirstOrDefault(reference => reference.Name == selectedReferenceName);
            _history = status.IsUnborn ? [] : await repository.GetLogAsync(Math.Clamp(_preferences.HistoryLimit, 1, 5000), string.IsNullOrWhiteSpace(_historyRevision.Text) ? null : _historyRevision.Text, Token);
            _historyGraph.Commits = _history;
            _historyGraph.References = references;
            _historyGraph.CurrentHead = status.Head;
            _commits.ItemsSource = _historyGraph.OrderedCommits;
            _commits.SelectedItem = _historyGraph.OrderedCommits.FirstOrDefault(commit => commit.Hash == selectedHash) ?? _historyGraph.OrderedCommits.FirstOrDefault();
            if (_commits.SelectedItem is GitCommit selectedCommit)
            {
                _diff.Text = await repository.GetCommitDiffAsync(selectedCommit.Hash, cancellationToken: Token);
                UpdateCommitInfo(selectedCommit);
            }
            else { _diff.Text = "This repository has no commits yet."; }
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
                _changesDiff.Text = await repository.GetDiffAsync(change.Path, staged: staged, cancellationToken: Token);
                _selectedPatch = GitPatch.Parse(_changesDiff.Text).FirstOrDefault();
                _hunks.ItemsSource = _selectedPatch?.Hunks;
            }
            else { _changesDiff.Text = "Select a changed file to inspect its diff."; _selectedPatch = null; _hunks.ItemsSource = null; }
        }
        finally { _refreshing = false; }
    }

    private async Task RunAsync(string label, Func<GitRepository, Task> action, bool refresh = true, bool requireRepository = true)
    {
        if (_busy) { return; }
        if (requireRepository && _repository is null) { _state.Text = "Open a repository first"; return; }
        _busy = true;
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
        foreach (Button button in _actionButtons) { button.IsEnabled = enabled; }
        foreach (MenuItem menu in _actionMenus) { menu.IsEnabled = enabled; }
        _repositoryPath.IsEnabled = enabled;
        _recent.IsEnabled = enabled;
    }

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

    private async void HandleShortcuts(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) { return; }
        if (e.Key == Key.O) { e.Handled = true; await ChooseRepositoryAsync(); }
        else if (e.Key == Key.R) { e.Handled = true; await RefreshAsync(); }
        else if (e.Key == Key.Enter && (_tabs.SelectedItem as TabItem)?.Name == "ChangesTab") { e.Handled = true; await CommitAsync(); }
    }
}
