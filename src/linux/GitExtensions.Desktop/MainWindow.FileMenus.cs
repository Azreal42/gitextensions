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
    private bool _selectingFileContext;
    private bool _fileContextNeedsDetails;
    private readonly Dictionary<bool, FileStatusListContextMenuController> _fileMenuControllers = [];
    private IReadOnlyDictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>> _fileMenuMetadata = new Dictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>>(ReferenceEqualityComparer.Instance);
    private GitRepository? _fileMenuRefreshPending;
    private Task _fileMenuRefreshCompletion = Task.CompletedTask;
    private FileMenuOperations? _fileMenuOperations;
    private FileMenuAdditionalOperations? _fileMenuAdditionalOperations;
    private sealed record BrowseFileMenuIdentity(bool IsFileTree, string? Revision, GitRevisionDiffGroup[] Groups);

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
            string? parent = _history.FirstOrDefault(commit => commit.Hash == revision)?.Parents.FirstOrDefault();
            leaves = tags.OfType<string>().Select(path => new FileMenuLeaf(new(path, ' '), parent is null ? null : new(parent), new(revision),
                IsSubmodule: _fileTreeEntries.GetValueOrDefault(path)?.IsSubmodule == true, FirstIsParent: parent is not null)).ToArray();
            identity = new(true, revision, []);
        }
        else
        {
            RevisionDiffFileSelection[] selected = tags.OfType<RevisionDiffFileSelection>().ToArray();
            leaves = selected.Select(item =>
            {
                GitComparisonFileMetadata? metadata = _fileMenuMetadata.GetValueOrDefault(item.Group)?.FirstOrDefault(value => value.File == item.File);
                return new FileMenuLeaf(item.File, item.Group.BaseRevision is null ? null : new(item.Group.BaseRevision), new(item.Group.Revision),
                    item.Group.BaseRevision is null ? null : new(new(item.Group.BaseRevision), new(item.Group.Revision), item.Group.Files, []),
                    metadata?.Staged switch { GitFileStagedStatus.Index => FileMenuStage.Index, GitFileStagedStatus.WorkTree => FileMenuStage.WorkingTree, GitFileStagedStatus.Unknown => FileMenuStage.Unknown, _ => FileMenuStage.None },
                    IsTracked: metadata?.IsTracked == true, IsSubmodule: metadata?.IsSubmodule == true,
                    IsSkipWorktree: metadata?.IsSkipWorktree == true, IsAssumeUnchanged: metadata?.IsAssumeUnchanged == true,
                    FirstIsParent: item.Group.BaseRevision is string firstRevision && _history.FirstOrDefault(commit => commit.Hash == item.Group.Revision)?.Parents.Contains(firstRevision) == true);
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
        FileMenuLeaf? focused = tree.SelectedItem is TreeViewItem { Tag: RevisionDiffFileSelection selectedLeaf }
            ? leaves.FirstOrDefault(leaf => leaf.File == selectedLeaf.File && leaf.Second.Revision == selectedLeaf.Group.Revision && leaf.First?.Revision == selectedLeaf.Group.BaseRevision)
            : leaves.FirstOrDefault();
        return new(repository, leaves, copy, folder, isFileTree, displayOnly, identity, focused);
    }

    private async Task<IReadOnlyDictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>>> ReadFileMenuMetadataAsync(
        GitRepository repository, IReadOnlyList<GitRevisionDiffGroup> groups, CancellationToken token)
    {
        Dictionary<GitRevisionDiffGroup, IReadOnlyList<GitComparisonFileMetadata>> metadata = new(ReferenceEqualityComparer.Instance);
        if (_fileMenuAdditionalOperations is not null) { await _fileMenuAdditionalOperations.RefreshAsync(repository, token); }
        foreach (GitRevisionDiffGroup group in groups)
        {
            if (group.Kind is GitRevisionDiffGroupKind.RangeDiff or GitRevisionDiffGroupKind.Combined || group.Files.Count == 0) { continue; }
            GitComparison comparison = new(new(group.BaseRevision ?? group.Revision), new(group.Revision), group.Files, []);
            metadata.Add(group, await repository.GetComparisonFileMetadataAsync(comparison, null, token));
        }
        token.ThrowIfCancellationRequested(); return metadata;
    }

    private bool IsCurrentFileMenuSelection(FileMenuSelection selection)
    {
        if (_lifetime.IsCancellationRequested || !IsVisible || !ReferenceEquals(_repository, selection.Repository)
            || selection.HostIdentity is not BrowseFileMenuIdentity identity) { return false; }
        return identity.IsFileTree ? _fileTreeRevision == identity.Revision
            : identity.Groups.All(group => _revisionDiffGroups.Any(current => ReferenceEquals(current, group)));
    }

    private void ConfigureFileContextMenu(TreeView tree, bool isFileTree)
    {
        FileStatusListView view = (isFileTree ? _fileTreeView : _revisionFilesView)!;
        Func<string, Func<CancellationToken, Task>, bool, Task> run = RunFileMenuOperationAsync;
        _fileMenuOperations ??= new(this, run, IsCurrentFileMenuSelection, info => ExternalLauncher(info), _lifetime.Token, T, () => WorkflowOwner)
        {
            ReadingPosition = leaf =>
            {
                string identity = string.Join('\0', _repository?.Path, leaf.File.Path, leaf.Second.Revision, "blob");
                return _fileContent is { } content && content.TextView.IsDocumentReady && content.TextView.DocumentIdentity == identity
                    ? (content.TextView.CurrentFileLine, content.TextView.FileEncodingName) : (1, null);
            }
        };
        FileMenuAdditionalOperations additional = _fileMenuAdditionalOperations ??= new(this, run, IsCurrentFileMenuSelection, info => ExternalLauncher(info), _lifetime.Token, () => WorkflowOwner);
        FileMenuHost host = new()
        {
            CaptureSelection = () => GetFileMenuSelection(isFileTree), IsCurrent = IsCurrentFileMenuSelection,
            IsBusy = () => _busy || _refreshing,
            CreateAction = (category, name, label, icon, action, gesture) => OriginalAction(category, name, label, icon, action, gesture),
            ExecuteAsync = (command, selection) => ExecuteFileMenuAsync(command, selection, isFileTree),
            Capabilities = new(ShowInFileTree: true, FilterInGrid: true, Grep: true), GrepVisible = () => _fileGrepSettings.ShowSearchBox,
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
            _selectingFileContext = true;
            if (!tree.SelectedItems!.Contains(item)) { _fileContextNeedsDetails = true; tree.SelectedItem = item; }
            item.Focus();
        }, RoutingStrategies.Tunnel);
        menu.Opening += (_, _) =>
        {
            _selectingFileContext = true;
            menu.ItemsSource = controller.BuildItems(GetFileMenuSelection(isFileTree));
        };
        menu.Closed += (_, _) =>
        {
            _selectingFileContext = false;
            if (!_fileContextNeedsDetails) { return; }
            _fileContextNeedsDetails = false;
            Dispatcher.UIThread.Post(async () =>
            {
                if (_busy || _refreshing || _lifetime.IsCancellationRequested) { return; }
                if (isFileTree && SelectedTreePath is string path) { _filePath.Text = path; await ShowSelectedTreeFileAsync(path); }
                else if (isFileTree) { ShowSelectedTreeFolder(); }
                else if (!isFileTree && tree.SelectedItem is TreeViewItem selected) { await ReadRevisionDiffAsync(selected); }
            });
        };
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
            try { attempted = true; await action(Token); }
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
        string[] revisions = selection.Leaves.Select(leaf => leaf.Second).Where(side => side.Kind == GitCompareRevisionKind.Commit).Select(side => side.Revision).Distinct(StringComparer.Ordinal).ToArray();
        if (revisions.Length != 1) { throw new InvalidOperationException(T("Select files from one revision.")); }
        IReadOnlyList<GitTreeEntry> entries = await selection.Repository.GetTreeAsync(revisions[0], cancellationToken: _lifetime.Token);
        if (!IsCurrentFileMenuSelection(selection)) { return; }
        SetFileTreePaths(selection.Repository, revisions[0], entries);
        FocusDetail("FileTreeTab");
        if (selection.Folder is { Length: > 0 } folder)
        {
            IEnumerable<TreeViewItem> Descendants(IEnumerable<TreeViewItem> items)
            { foreach (TreeViewItem item in items) { yield return item; foreach (TreeViewItem child in Descendants(item.Items.OfType<TreeViewItem>())) { yield return child; } } }
            TreeViewItem? node = Descendants(_tree.Items.OfType<TreeViewItem>()).FirstOrDefault(item => item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } tag && tag.Key == folder);
            if (node is not null) { _tree.SelectedItem = node; }
        }
        else { _fileTreeView!.SelectTags(selection.Leaves.Select(leaf => (object)leaf.File.Path)); }
    }
    private async Task OpenFileGrepFromDiffAsync() { FocusDetail("FileTreeTab"); await ShowFileGrepDialogAsync(); }
    private Task ToggleFileGrepAsync()
    { UpdateFileGrepSettings(_fileGrepSettings with { ShowSearchBox = !_fileGrepSettings.ShowSearchBox }); FocusDetail("FileTreeTab"); return Task.CompletedTask; }
    private Task<bool> HandleFilePaneShortcutAsync(KeyEventArgs args)
    {
        bool tree = _tree.IsKeyboardFocusWithin;
        return (tree || _revisionFiles.IsKeyboardFocusWithin) && _fileMenuControllers.TryGetValue(tree, out FileStatusListContextMenuController? controller)
            ? controller.HandleShortcutAsync(args) : Task.FromResult(false);
    }
}
