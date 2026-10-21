using GitExtensions.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly Dictionary<bool, FileStatusListContextMenuController> _fileMenuControllers = [];
    private IReadOnlyDictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>> _fileMenuMetadata = new Dictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>>(ReferenceEqualityComparer.Instance);
    private GitRepository? _fileMenuRefreshPending;
    private Task _fileMenuRefreshCompletion = Task.CompletedTask;
    private FileMenuOperations? _fileMenuOperations;
    private FileMenuAdditionalOperations? _fileMenuAdditionalOperations;
    private sealed record BrowseFileMenuIdentity(bool IsFileTree, GitCompareRevision? Source, GitRevisionDiffGroup[] Groups);

    private FileMenuSelection? GetFileMenuSelection(bool isFileTree)
    {
        if (_repository is not GitRepository repository) { return null; }
        TreeView tree = isFileTree ? _tree : _revisionFiles;
        FileStatusListView view = (isFileTree ? _fileTreeView : _revisionFilesView)!;
        TreeViewItem[] nodes = tree.SelectedItems?.OfType<TreeViewItem>().ToArray() ?? [];
        IEnumerable<object> tags = nodes.Length == 1 ? view.SelectedTags : nodes.Select(item => item.Tag).OfType<object>();
        FileMenuLeaf[] leaves;
        BrowseFileMenuIdentity identity;
        if (isFileTree)
        {
            if (_fileTreeRevision is not string revision) { return null; }
            GitCompareRevision source = _fileTreeSource ?? new(revision);
            GitCompareRevision? parent = source.Kind switch
            {
                GitCompareRevisionKind.Index when _historyGraph.CurrentHead is string head => new(head),
                GitCompareRevisionKind.WorkingTree => new("", GitCompareRevisionKind.Index),
                GitCompareRevisionKind.Commit when _history.FirstOrDefault(commit => commit.Hash == revision)?.Parents.FirstOrDefault() is string hash => new(hash),
                _ => null
            };
            leaves = tags.OfType<string>().Select(path =>
            {
                GitComparisonTreeEntry? entry = _fileTreeEntries.GetValueOrDefault(path);
                bool submodule = entry?.IsSubmodule == true;
                GitSubmoduleBrowseSelection? childSelection = null;
                string? childError = null;
                if (submodule)
                {
                    if (entry!.IsConflict) { childError = "The selected submodule index entry is conflicted. Refresh or resolve it before opening the comparison."; }
                    else if (source.Kind == GitCompareRevisionKind.WorkingTree)
                    {
                        string? indexed = entry.IndexStages.SingleOrDefault(stage => stage.Stage == 0 && stage.Mode == "160000")?.Hash;
                        if (indexed is null) { childError = "The selected submodule has no captured index gitlink. Refresh the file tree."; }
                        else { childSelection = new(new("", GitCompareRevisionKind.WorkingTree), new(indexed), indexed); }
                    }
                    else if (entry.Hash is { Length: > 0 } hash)
                    { childSelection = new(new(hash), IndexGitlink: source.Kind == GitCompareRevisionKind.Index ? hash : null); }
                    else { childError = "The selected submodule has no captured commit. Refresh the file tree."; }
                }
                return new FileMenuLeaf(new(path, ' '), parent, source, IsSubmodule: submodule, FirstIsParent: parent is not null)
                    { SubmoduleSelection = childSelection, SubmoduleSelectionError = childError };
            }).ToArray();
            identity = new(true, source, []);
        }
        else
        {
            RevisionDiffFileSelection[] selected = tags.OfType<RevisionDiffFileSelection>().ToArray();
            leaves = selected.Select(item =>
            {
                GitComparisonFileMetadata? metadata = _fileMenuMetadata.GetValueOrDefault(item.Group)?.FirstOrDefault(value => value.File == item.File);
                GitComparison? comparison = _revisionComparisons.GetValueOrDefault(item.Group);
                GitCompareRevision? first = comparison?.Base ?? (item.Group.BaseRevision is null ? null : new(item.Group.BaseRevision));
                GitCompareRevision second = comparison?.Compare ?? new(item.Group.Revision);
                return new FileMenuLeaf(item.File, first, second,
                    comparison ?? (first is null ? null : new(first, second, item.Group.Files, [])),
                    metadata?.Staged switch { GitFileStagedStatus.Index => FileMenuStage.Index, GitFileStagedStatus.WorkTree => FileMenuStage.WorkingTree, GitFileStagedStatus.Unknown => FileMenuStage.Unknown, _ => FileMenuStage.None },
                    IsTracked: metadata?.IsTracked == true, IsSubmodule: metadata?.IsSubmodule == true,
                    IsSkipWorktree: metadata?.IsSkipWorktree == true, IsAssumeUnchanged: metadata?.IsAssumeUnchanged == true,
                    FirstIsParent: comparison is not null ? comparison.Compare.Kind == GitCompareRevisionKind.Commit
                        ? comparison.Base.Kind == GitCompareRevisionKind.Commit && CommitForDiff(comparison.Compare.Revision)?.Parents.Contains(comparison.Base.Revision) == true
                        : comparison.Base == ArtificialFileMenuParent(comparison.Compare)
                        : item.Group.BaseRevision is string firstRevision && CommitForDiff(item.Group.Revision)?.Parents.Contains(firstRevision) == true)
                    { SubmoduleSelection = metadata?.SubmoduleSelection, SubmoduleSelectionError = metadata?.SubmoduleSelectionError };
            }).ToArray();
            GitRevisionDiffGroup[] groups = selected.Select(item => item.Group).Concat(nodes.Select(item => item.Tag).OfType<GitRevisionDiffGroup>()).Distinct().ToArray();
            identity = new(false, null, groups);
        }
        string[] copy = nodes.Select(item => item.Tag switch
        {
            string path => path,
            RevisionDiffFileSelection selected => selected.File.Path,
            FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } folder => folder.Key,
            _ => null
        }).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (copy.Length == 0) { copy = leaves.Select(leaf => leaf.File.Path).Distinct(StringComparer.Ordinal).ToArray(); }
        string? folder = view.SelectedFolderPath;
        if (nodes.Length == 1 && nodes[0].Items.Count > 0 && folder is null) { folder = ""; copy = ["."]; }
        bool displayOnly = identity.Groups.Any(group => group.Kind is GitRevisionDiffGroupKind.RangeDiff or GitRevisionDiffGroupKind.Combined);
        FileMenuLeaf? focused = view.FocusedSelectedTag is RevisionDiffFileSelection selectedLeaf
            ? leaves.FirstOrDefault(leaf => leaf.File == selectedLeaf.File && (leaf.Comparison is { } pair
                ? _revisionComparisons.GetValueOrDefault(selectedLeaf.Group) == pair
                    || pair.Compare.Revision == selectedLeaf.Group.Revision && pair.Base.Revision == selectedLeaf.Group.BaseRevision
                : leaf.Second.Revision == selectedLeaf.Group.Revision && leaf.First?.Revision == selectedLeaf.Group.BaseRevision))
            : view.FocusedSelectedTag is string focusedPath && isFileTree ? leaves.FirstOrDefault(leaf => leaf.File.Path == focusedPath)
            : leaves.FirstOrDefault();
        return new(repository, leaves, copy, folder, isFileTree, displayOnly, identity, focused);
    }

    private GitCompareRevision? ArtificialFileMenuParent(GitCompareRevision second) => second.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => new("", GitCompareRevisionKind.Index),
        GitCompareRevisionKind.Index when _historyGraph.CurrentHead is { } head => new(head),
        _ => null // Unknown/unborn is not permission to substitute the comparison's arbitrary base.
    };

    private async Task<IReadOnlyDictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>>> ReadFileMenuMetadataAsync(
        GitRepository repository, IReadOnlyList<GitRevisionDiffGroup> groups, CancellationToken token,
        IReadOnlyDictionary<GitRevisionDiffGroup, GitComparison>? comparisons = null)
    {
        Dictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>> metadata = new(ReferenceEqualityComparer.Instance);
        if (_fileMenuAdditionalOperations is not null) { await _fileMenuAdditionalOperations.RefreshAsync(repository, token); }
        foreach (GitRevisionDiffGroup group in groups)
        {
            if (group.Kind is GitRevisionDiffGroupKind.RangeDiff or GitRevisionDiffGroupKind.Combined || group.Files.Count == 0) { continue; }
            GitComparison comparison = comparisons?.GetValueOrDefault(group) ?? new(new(group.BaseRevision ?? group.Revision), new(group.Revision), group.Files, []);
            GitCompareRevision? parent = comparisons is not null ? ArtificialFileMenuParent(comparison.Compare) : null;
            metadata.Add(group, await repository.GetComparisonFileMetadataAsync(comparison, parent, token));
        }
        token.ThrowIfCancellationRequested(); return metadata;
    }

    private bool IsCurrentFileMenuSelection(FileMenuSelection selection)
    {
        if (_lifetime.IsCancellationRequested || !IsVisible || !ReferenceEquals(_repository, selection.Repository)
            || selection.HostIdentity is not BrowseFileMenuIdentity identity) { return false; }
        return identity.IsFileTree ? (_fileTreeSource ?? (_fileTreeRevision is string revision ? new(revision) : null)) == identity.Source
            : identity.Groups.All(group => _revisionDiffGroups.Any(current => ReferenceEquals(current, group)));
    }

    private (int Line, string? Encoding) FileMenuReadingPosition(FileMenuLeaf leaf)
    {
        if (_refreshing || _lifetime.IsCancellationRequested || _repository is not { } repository) { return (1, null); }
        bool tree = (_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab";
        if (!tree && (_tabs.SelectedItem as TabItem)?.Name != "DiffTab") { return (1, null); }
        FileMenuSelection? selection = GetFileMenuSelection(tree);
        if (selection is not { Leaves.Count: 1, Folder: null, IsDisplayOnly: false }
            || !IsCurrentFileMenuSelection(selection) || selection.Leaves[0].File != leaf.File
            || selection.Leaves[0].First != leaf.First || selection.Leaves[0].Second != leaf.Second) { return (1, null); }
        FileContentView? content;
        string identity;
        if (tree)
        {
            if (_treeRead.Pending != 0 || _treeFileRead.Pending != 0 || !ReferenceEquals(_fileTreeRepository, repository)
                || _fileTreeRevision is not string revision || (_fileTreeSource ?? new(revision)) != leaf.Second) { return (1, null); }
            content = _fileContent;
            identity = string.Join('\0', repository.Path, leaf.File.Path, revision, "blob");
        }
        else
        {
            if (_revisionRead.Pending != 0 || _revisionDiffRead.Pending != 0
                || _revisionFilesView?.FocusedSelectedTag is not RevisionDiffFileSelection selected
                || selected.File != leaf.File) { return (1, null); }
            content = _commitContent;
            identity = _revisionComparisons.TryGetValue(selected.Group, out GitComparison? comparison)
                ? string.Join("\0", repository.Path, selected.File.Path, comparison.Base.Kind, comparison.Base.Revision,
                    comparison.Compare.Kind, comparison.Compare.Revision, selected.File.OriginalPath)
                : string.Join("\0", repository.Path, selected.File.Path, selected.Group.Kind,
                    selected.Group.Revision, selected.Group.BaseRevision, selected.File.OriginalPath);
        }
        // The previous pixels remain visible during panel reads. They are not a position for the new selection.
        return content is { IsImage: false } && content.TextView.IsDocumentReady && content.TextView.DocumentIdentity == identity
            ? (content.TextView.CurrentFileLine, content.TextView.FileEncodingName ?? content.TextView.DefaultFileEncodingName) : (1, null);
    }

    private void ConfigureFileContextMenu(TreeView tree, bool isFileTree)
    {
        FileStatusListView view = (isFileTree ? _fileTreeView : _revisionFilesView)!;
        Func<string, Func<CancellationToken, Task>, bool, Task> run = RunFileMenuOperationAsync;
        _fileMenuOperations ??= new(this, run, IsCurrentFileMenuSelection, info => ExternalLauncher(info), _lifetime.Token, T, () => WorkflowOwner)
        {
            ReadingPosition = FileMenuReadingPosition
        };
        FileMenuAdditionalOperations additional = _fileMenuAdditionalOperations ??= new(this, run, IsCurrentFileMenuSelection, info => ExternalLauncher(info), _lifetime.Token, () => WorkflowOwner);
        FileMenuHost host = new()
        {
            CaptureSelection = () => GetFileMenuSelection(isFileTree), IsCurrent = IsCurrentFileMenuSelection,
            IsBusy = () => _busy || _refreshing,
            CreateAction = (category, name, label, icon, action, gesture) => OriginalAction(category, name, label, icon, async () =>
            {
                GitRepository? repository = _repository;
                Func<bool>? restoreFocus = view.CaptureSelectedRowFocus();
                try { await action(); }
                finally
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_lifetime.IsCancellationRequested || !IsVisible || !IsEffectivelyEnabled || _busy
                            || !ReferenceEquals(repository, _repository) || OwnedWindows.Any(window => window.IsVisible)
                            || !view.IsEffectivelyVisible || !view.IsEffectivelyEnabled) { return; }
                        IInputElement? focused = FocusManager?.GetFocusedElement();
                        if (focused is Visual input && focused is not MenuItem && !tree.IsKeyboardFocusWithin
                            && TopLevel.GetTopLevel(input) is { IsVisible: true }) { return; }
                        restoreFocus?.Invoke();
                    }, DispatcherPriority.Input);
                }
            }, gesture),
            ExecuteAsync = (command, selection) => ExecuteFileMenuAsync(command, selection, isFileTree),
            RunScriptAsync = (script, selection) => RunFileMenuOperationAsync(script.GetDisplayName(),
                async token => { await DesktopScriptExecutor.ExecuteAsync(WorkflowOwner, selection.Repository, script, token, selection); },
                changes: !script.RunInBackground),
            Capabilities = new(ShowInFileTree: !IsStandaloneDiff, FilterInGrid: !IsStandaloneDiff, Grep: !IsStandaloneDiff), GrepVisible = () => _fileGrepSettings.ShowSearchBox,
            GetRemembered = () => _repository is { } current ? additional.GetRemembered(current) : null,
            GetCustomDiffTools = () => _repository is { } current ? additional.GetCustomDiffTools(current) : []
        };
        FileStatusListContextMenuController controller = new(view, _translations, host);
        _fileMenuControllers[isFileTree] = controller;
        ContextMenu menu = new() { Name = isFileTree ? "FileTreeContextMenu" : "RevisionFilesContextMenu" };
        tree.ContextMenu = menu;
        tree.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(tree).Properties.IsRightButtonPressed) { return; }
            TreeViewItem? item = (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (item is null) { return; }
            if (!tree.SelectedItems!.Contains(item)) { tree.SelectedItem = item; }
            item.Focus();
        }, RoutingStrategies.Tunnel);
        menu.Opening += (_, _) => menu.ItemsSource = controller.BuildItems(GetFileMenuSelection(isFileTree));
        menu.AddHandler(InputElement.KeyDownEvent, async (_, args) => await HandleConfiguredShortcutsAsync(menu, args, allowAlt: true), RoutingStrategies.Tunnel);
        tree.AddHandler(InputElement.DoubleTappedEvent, async (_, args) =>
        {
            if (GetFileMenuSelection(isFileTree) is { Leaves.Count: 1, Folder: null } selection
                && (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.Tag is not FileStatusListNodeTag)
            { args.Handled = true; await ShowFileHistoryAsync(selection); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private async Task RunFileMenuOperationAsync(string label, Func<CancellationToken, Task> action, bool changes)
    {
        GitRepository? captured = _repository; bool attempted = false, refreshed = false;
        await RunAsync(label, async repository =>
        {
            Exception? original = null;
            try { attempted = true; MarkStandaloneMayHaveRepositoryChanges(changes); await action(Token); }
            catch (Exception error) { original = error; throw; }
            finally
            {
                if (changes && !Token.IsCancellationRequested && !_lifetime.IsCancellationRequested && IsVisible && ReferenceEquals(captured, _repository))
                {
                    try { await RefreshDataAsync(repository); refreshed = true; }
                    catch (Exception error) when (original is not null) { AppendOutput("Refresh after " + label + ": " + error.Message); }
                }
            }
        }, refresh: false);
        if (!changes || !attempted || refreshed || _lifetime.IsCancellationRequested || !IsVisible || !ReferenceEquals(captured, _repository)) { return; }
        _fileMenuRefreshPending = captured;
        if (_fileMenuRefreshCompletion.IsCompleted) { _fileMenuRefreshCompletion = RefreshAfterFileMenuAsync(); }
        await _fileMenuRefreshCompletion;
    }
    private async Task RefreshAfterFileMenuAsync()
    {
        try
        {
            while (_fileMenuRefreshPending is GitRepository repository && !_lifetime.IsCancellationRequested && IsVisible)
            {
                if (_busy) { await Task.Delay(10, _lifetime.Token); continue; }
                _fileMenuRefreshPending = null;
                if (!ReferenceEquals(repository, _repository)) { continue; }
                Exception? original = LastException; string status = _state.Text ?? "";
                await RunAsync("Refresh after file operation", current => RefreshDataAsync(current), refresh: false);
                if (original is not null && ReferenceEquals(repository, _repository)) { LastException = original; _state.Text = status; }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _fileMenuRefreshPending = null; }
    }

    private Task ExecuteFileMenuAsync(FileMenuCommand command, FileMenuSelection selection, bool isFileTree)
    {
        if (!IsCurrentFileMenuSelection(selection) || _busy) { return Task.CompletedTask; }
        if (FileMenuOperations.Handles(command)) { return _fileMenuOperations!.ExecuteAsync(command, selection); }
        return command switch
        {
            FileMenuCommand.FilterInGrid => FilterFileInHistoryAsync(selection),
            FileMenuCommand.ShowInFileTree => ShowSelectionInFileTreeAsync(selection),
            FileMenuCommand.FindInCommitFiles => isFileTree ? ShowFileGrepDialogAsync() : OpenFileGrepFromDiffAsync(),
            FileMenuCommand.ToggleFindInCommitFiles => ToggleFileGrepAsync(),
            _ => _fileMenuAdditionalOperations!.ExecuteAsync(command, selection)
        };
    }
    private Task ShowFileHistoryAsync(FileMenuSelection selection) => _fileMenuOperations!.ExecuteAsync(FileMenuCommand.FileHistory, selection);
    private Task FilterFileInHistoryAsync(FileMenuSelection selection)
    { _historyQuery = _historyQuery with { Path = selection.Folder ?? selection.Paths.Single(), Skip = 0 }; return RefreshAsync(); }
    private async Task ShowSelectionInFileTreeAsync(FileMenuSelection selection)
    {
        GitCompareRevision[] revisions = selection.Leaves.Select(leaf => leaf.Second).Distinct().ToArray();
        if (revisions.Length != 1) { throw new InvalidOperationException(T("Select files from one revision.")); }
        SelectDetailsTab("FileTreeTab");
        await ReadBrowserAsync(_treeRead, "File tree", async (repository, token) =>
        {
            if (!IsCurrentFileMenuSelection(selection)) { return; }
            await LoadComparisonFileTreeAsync(repository, revisions[0], token);
        });
        if (!IsCurrentFileMenuSelection(selection) || _fileTreeRepository != selection.Repository || _fileTreeSource != revisions[0]) { return; }
        if (selection.Folder is { Length: > 0 } folder)
        {
            IEnumerable<TreeViewItem> Descendants(IEnumerable<TreeViewItem> items)
            { foreach (TreeViewItem item in items) { yield return item; foreach (TreeViewItem child in Descendants(item.Items.OfType<TreeViewItem>())) { yield return child; } } }
            TreeViewItem? node = Descendants(_tree.Items.OfType<TreeViewItem>()).FirstOrDefault(item => item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } tag && tag.Key == folder);
            if (node is not null) { _tree.SelectedItem = node; }
        }
        else { _fileTreeView!.SelectTags(selection.Leaves.Select(leaf => (object)leaf.File.Path)); }
        TreeViewItem? target = selection.Folder is null && selection.FocusedLeaf is { } focused
            ? _fileTreeView!.FindItemByTag(focused.File.Path) : _tree.SelectedItem as TreeViewItem;
        // This action changes hosts: focus its selected target after the popup closes,
        // rather than restoring the old Diff row or focusing the tree before selection.
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetime.IsCancellationRequested || !IsVisible || !IsEffectivelyEnabled || _busy
                || !ReferenceEquals(_repository, selection.Repository) || _fileTreeSource != revisions[0]
                || (_tabs.SelectedItem as TabItem)?.Name != "FileTreeTab" || OwnedWindows.Any(window => window.IsVisible)
                || target is null || _tree.SelectedItems?.Contains(target) != true) { return; }
            _tree.UpdateLayout();
            if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled) { target.Focus(); target.BringIntoView(); }
        }, DispatcherPriority.Input);
    }
    private async Task OpenFileGrepFromDiffAsync() { FocusDetail("FileTreeTab"); await ShowFileGrepDialogAsync(); }
    private Task ToggleFileGrepAsync()
    { UpdateFileGrepSettings(_fileGrepSettings with { ShowSearchBox = !_fileGrepSettings.ShowSearchBox }); FocusDetail("FileTreeTab"); return Task.CompletedTask; }
    private async Task<bool> HandleFilePaneShortcutAsync(KeyEventArgs args)
    {
        bool tree = _tree.IsKeyboardFocusWithin || _fileContent?.IsKeyboardFocusWithin == true || _tree.ContextMenu?.IsOpen == true;
        bool diff = _revisionFiles.IsKeyboardFocusWithin || _commitContent?.IsKeyboardFocusWithin == true || _revisionFiles.ContextMenu?.IsOpen == true;
        if (!tree && !diff || IsFocusedTextEditingKey(args)) { return false; }
        HotkeyCommand? command = DesktopHotkeys.Match("BrowseDiff", args);
        if (diff && command?.Name is "GoToFirstParent" or "GoToLastParent")
        {
            if (!CanQuickSearch || !HasDirectRevisionParent()) { return false; }
            args.Handled = true;
            _revisionFiles.ContextMenu?.Close();
            await MoveHistoryRelativeAsync(command.Name == "GoToFirstParent" ? RelativeRevisionCommand.FirstParent : RelativeRevisionCommand.LastParent);
            return true;
        }
        return _fileMenuControllers.TryGetValue(tree, out FileStatusListContextMenuController? controller)
            && await controller.HandleShortcutAsync(args, hostOwnsInput: true);
    }
}
