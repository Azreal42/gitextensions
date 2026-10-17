using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private enum RelativeRevisionCommand { Parent, FirstParent, LastParent, Child }
    private ParentChildNavigationHistory? _parentChildNavigation;
    private Task _parentChildSelectionWork = Task.CompletedTask;
    private string? _parentChildLatestCommit;
    private (GitRepository Repository, string Selection)? _parentChildFileFocus;

    private void InitializeParentChildNavigation()
    {
        // Windows LatestSelectedRevision is also assigned by MouseClick, even
        // when Ctrl+click has just deselected that row. QuickSearch instead uses
        // the most recently selected survivor; keep this pointer anchor distinct.
        _commits.AddHandler(PointerReleasedEvent, (_, args) =>
        {
            if (args.InitialPressMouseButton is not (MouseButton.Left or MouseButton.Right)) { return; }
            _parentChildLatestCommit = (args.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .FirstOrDefault(row => row.FindAncestorOfType<ListBox>() == _commits)?.DataContext is GitCommit commit ? commit.Hash : null;
            RefreshHistoryMenuStates();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private GitCompareRevision? CurrentRelativeRevision()
    {
        int index = QuickSearchCurrentIndex(), artificial = QuickSearchArtificialCount;
        if (index >= 0 && index < artificial) { return new("", index == 0 ? GitCompareRevisionKind.WorkingTree : GitCompareRevisionKind.Index); }
        return _parentChildLatestCommit is { } latest && _history.Any(commit => commit.Hash == latest) ? new(latest) : null;
    }

    private IReadOnlyList<GitCompareRevision> RelativeRevisionParents(GitCompareRevision current) => current.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => [new("", GitCompareRevisionKind.Index)],
        GitCompareRevisionKind.Index => _historyGraph.CurrentHead is { Length: > 0 } head ? [new(head)] : [],
        _ => _history.FirstOrDefault(commit => commit.Hash == current.Revision)?.Parents.Select(hash => new GitCompareRevision(hash)).ToArray() ?? []
    };

    private IReadOnlyList<GitCompareRevision> RelativeRevisionChildren(GitCompareRevision current)
    {
        if (current.Kind == GitCompareRevisionKind.WorkingTree) { return []; }
        if (current.Kind == GitCompareRevisionKind.Index) { return [new("", GitCompareRevisionKind.WorkingTree)]; }
        List<GitCompareRevision> children = [];
        IReadOnlyList<string> realChildren = _historyGraph.GetChildren(current.Revision);
        if (QuickSearchArtificialCount > 0 && _historyGraph.CurrentHead == current.Revision)
        {
            // Windows inserts artificial rows just before HEAD in the reader,
            // after edges already read and before any later child edges.
            HashSet<string> beforeHead = _history.TakeWhile(commit => commit.Hash != current.Revision).Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);
            children.AddRange(realChildren.Where(beforeHead.Contains).Select(hash => new GitCompareRevision(hash)));
            children.Add(new("", GitCompareRevisionKind.Index));
            children.AddRange(realChildren.Where(hash => !beforeHead.Contains(hash)).Select(hash => new GitCompareRevision(hash)));
        }
        else { children.AddRange(realChildren.Select(hash => new GitCompareRevision(hash))); }
        return children;
    }

    private bool HasRevisionParent() => CurrentRelativeRevision() is { } current
        && (_parentChildNavigation?.HasPreviousParent == true || RelativeRevisionParents(current).Count > 0);
    private bool HasRevisionChild() => CurrentRelativeRevision() is { } current
        && (_parentChildNavigation?.HasPreviousChild == true || RelativeRevisionChildren(current).Count > 0);
    private bool HasDirectRevisionParent() => CurrentRelativeRevision() is { } current && RelativeRevisionParents(current).Count > 0;

    private async Task MoveHistoryRelativeAsync(RelativeRevisionCommand command)
    {
        if (!CanQuickSearch || CurrentRelativeRevision() is not { } current) { return; }
        // FormBrowse restores the file list when Ctrl+P/N came from revisionDiff.
        bool restoreFiles = command is RelativeRevisionCommand.Parent or RelativeRevisionCommand.Child
            && (_revisionFiles.IsKeyboardFocusWithin || _commitContent?.IsKeyboardFocusWithin == true);
        GitRepository? repository = _repository;
        _parentChildNavigation ??= new(target => _parentChildSelectionWork = SelectParentChildDestinationAsync(target));
        _parentChildSelectionWork = Task.CompletedTask;
        bool moved = command switch
        {
            RelativeRevisionCommand.Parent => _parentChildNavigation.NavigateParent(current, RelativeRevisionParents(current)),
            RelativeRevisionCommand.FirstParent => _parentChildNavigation.NavigateFirstParent(current, RelativeRevisionParents(current)),
            RelativeRevisionCommand.LastParent => _parentChildNavigation.NavigateLastParent(current, RelativeRevisionParents(current)),
            _ => _parentChildNavigation.NavigateChild(current, RelativeRevisionChildren(current))
        };
        if (!moved) { return; }
        // Focus the stable list itself, so replacing its file rows cannot remove
        // the keyboard target during the asynchronous detail read.
        if (restoreFiles && repository is not null && !OwnedWindows.Any(window => window.IsVisible))
        {
            _revisionFiles.Focus();
            _parentChildFileFocus = (repository, RevisionSelectionIdentity);
            if (_revisionRead.Current is null) { RestoreParentChildFileRowFocus(); }
        }
        await _parentChildSelectionWork;
        if (restoreFiles && repository is not null && repository == _repository && !_lifetime.IsCancellationRequested && IsVisible
            && !OwnedWindows.Any(window => window.IsVisible)
            && (FocusManager?.GetFocusedElement() is null or Window || _commits.IsKeyboardFocusWithin))
        {
            _revisionFiles.Focus();
            _parentChildFileFocus = (repository, RevisionSelectionIdentity);
            if (_revisionRead.Current is null) { RestoreParentChildFileRowFocus(); }
        }
        RefreshHistoryMenuStates();
    }

    private void RestoreParentChildFileRowFocus()
    {
        var bookmark = _parentChildFileFocus;
        _parentChildFileFocus = null;
        if (bookmark is null || bookmark.Value.Repository != _repository || bookmark.Value.Selection != RevisionSelectionIdentity
            || _lifetime.IsCancellationRequested || !_revisionFiles.IsEffectivelyVisible || !_revisionFiles.IsEffectivelyEnabled
            || !ReferenceEquals(FocusManager?.GetFocusedElement(), _revisionFiles)) { return; }
        // TreeView routes arrows through its focused item. Keep the stable tree
        // during the read, then focus the final selected row at publication.
        _revisionFiles.UpdateLayout();
        if (_revisionFiles.SelectedItem is TreeViewItem row) { row.Focus(); }
    }

    private Task SelectParentChildDestinationAsync(GitCompareRevision target)
    {
        if (target.Kind != GitCompareRevisionKind.Commit)
        {
            if (QuickSearchArtificialCount == 0) { return Task.CompletedTask; }
            return ApplyQuickSearchAsync(target.Kind == GitCompareRevisionKind.WorkingTree ? 0 : 1);
        }
        GitCommit? commit = _commits.Items.OfType<GitCommit>().FirstOrDefault(row => row.Hash == target.Revision);
        return commit is null ? ShowFilteredRevisionErrorAsync(target.Revision)
            : ApplyQuickSearchAsync(QuickSearchArtificialCount + _commits.Items.IndexOf(commit));
    }
}
