using Avalonia;
using Avalonia.Controls;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly BrowseRequest _browseRequest;
    private bool _browseSelectionApplied;
    private GitFileHistoryGraph? _browseFileHistory;
    private string? _explicitFileHistoryDiffPath, _explicitFileHistoryTreePath;
    private string? _fileHistoryTrackedFilter;
    private GitCompareRevision? _fileTreeSource;
    private sealed record BrowseInitialComparison(GitCompareRevision First, GitCompareRevision Selected);
    private BrowseInitialComparison? _browseInitialComparison;
    private bool UsesArtificialInitialComparison => _browseInitialComparison is { } pair
        && (pair.First.Kind != GitCompareRevisionKind.Commit || pair.Selected.Kind != GitCompareRevisionKind.Commit);
    private bool IsFileHistoryBrowse => _browseRequest.PathFilter is not null;

    private void InitializeFileHistoryBrowse()
    {
        SetFileHistoryInlineBlameAvailable(IsFileHistoryBrowse);
        if (!IsFileHistoryBrowse) { return; }
        SetNavigationVisible(false);
        SelectDetailsTab("DiffTab");
        _commits.AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (!args.Handled && args.Key == Avalonia.Input.Key.Up && args.KeyModifiers == Avalonia.Input.KeyModifiers.None
                && _commits.IsKeyboardFocusWithin && _commits.SelectedIndex == 0 && !_busy)
            {
                args.Handled = true;
                Task selection = SelectArtificialHistoryRevisionAsync(staged: true);
                _indexRevision?.Focus(); await selection;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void SetNavigationVisible(bool visible)
    {
        if (_browserBody is null) { return; }
        _browserBody.ColumnDefinitions[0].Width = new GridLength(visible ? NavigationWidth : 0);
        _browserBody.ColumnDefinitions[1].Width = new GridLength(visible ? 4 : 0);
        _browserBody.Children[0].IsVisible = visible;
        _browserBody.Children[1].IsVisible = visible;
        if (_navigationPanel is not null) { _navigationPanel.IsVisible = visible; }
    }

    private sealed record BrowseHistoryRead(IReadOnlyList<GitCommit> Commits, GitFileHistoryGraph? Graph);

    private async Task<BrowseHistoryRead> ReadBrowseHistoryAsync(GitRepository repository, GitLogQuery query, bool unborn, CancellationToken token)
    {
        // Current branch is empty until its first commit; other refs remain readable in All/filtered scopes.
        if (unborn && (query.Revision == "HEAD" || query.BranchFilter?.Trim() == "HEAD")) { return new([], null); }
        if (string.IsNullOrEmpty(query.Path)) { return new(await repository.GetLogAsync(query, token), null); }
        bool directory = _browseRequest.PathFilter is { } filter && filter.Path == query.Path
            ? filter.Kind == BrowsePathKind.Directory
            : query.Path.EndsWith('/') || Directory.Exists(Path.Combine(repository.Path, query.Path));
        GitFileHistoryGraph graph = await repository.GetFileHistoryGraphAsync(query, new(IsDirectory: directory), token);
        return new(graph.Commits, graph);
    }

    private IEnumerable<string> FileHistoryFollowedPaths(string? revision)
    {
        if (revision is not null && _browseFileHistory?.PathByCommit.TryGetValue(revision, out string? oldPath) == true)
        { yield return oldPath; }
        if (_historyQuery.Path is string filter) { yield return filter.TrimEnd('/'); }
        if (_browseFileHistory is { } graph)
        { foreach (string path in graph.Paths) { yield return path; } }
    }

    private void RememberFileHistoryDiffSelection(TreeViewItem? item)
    {
        if (_historyQuery.Path is null) { return; }
        _explicitFileHistoryDiffPath = item?.Tag switch
        {
            RevisionDiffFileSelection selected => selected.File.Path,
            FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } folder => folder.Key,
            _ => _explicitFileHistoryDiffPath
        };
    }

    private TreeViewItem? FileHistoryDiffSelection(IReadOnlyList<TreeViewItem> nodes, GitRevisionDiffGroup? firstGroup)
    {
        if (_historyQuery.Path is null || firstGroup is null) { return null; }
        string firstKey = $"0:{firstGroup.Kind}:{firstGroup.BaseRevision}:{firstGroup.Revision}";
        foreach (string path in new[] { _explicitFileHistoryDiffPath }.OfType<string>()
            .Concat(FileHistoryFollowedPaths((_commits.SelectedItem as GitCommit)?.Hash)).Distinct(StringComparer.Ordinal))
        {
            TreeViewItem? item = nodes.FirstOrDefault(item => item.Tag switch
            {
                RevisionDiffFileSelection file => ReferenceEquals(file.Group, firstGroup) && file.File.Path == path,
                FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } folder => folder.GroupKey == firstKey && folder.Key == path,
                _ => false
            });
            if (item is not null) { return item; }
        }
        return null;
    }

    private void SelectFileHistoryTreeFallback()
    {
        if (_historyQuery.Path is null || _fileTreeView is null) { return; }
        foreach (string path in new[] { _explicitFileHistoryTreePath }.OfType<string>()
            .Concat(FileHistoryFollowedPaths(_fileTreeSource?.Revision)).Distinct(StringComparer.Ordinal))
        {
            if (_allFileTreePaths.Contains(path, StringComparer.Ordinal))
            { _fileTreeView.SelectTags([path]); return; }
            TreeViewItem? folder = FileStatusTreeItems(_tree).FirstOrDefault(item =>
                item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } node && node.Key == path);
            if (folder is not null) { _tree.SelectedItem = folder; return; }
        }
    }

    private async Task LoadComparisonFileTreeAsync(GitRepository repository, GitCompareRevision source, CancellationToken token)
    {
        var entries = await repository.GetComparisonTreeAsync(source, token);
        token.ThrowIfCancellationRequested();
        if (_repository != repository) { return; }
        SetComparisonFileTreePaths(repository, source, entries);
        if (!_fileGrepSettings.ShowSearchBox || _fileGrepSettings.Expression.Length == 0)
        {
            if (SelectedTreePath is string path) { _filePath.Text = path; await LoadTreeFileAsync(repository, path, token); }
            else { ShowSelectedTreeFolder(); }
        }
    }
}
