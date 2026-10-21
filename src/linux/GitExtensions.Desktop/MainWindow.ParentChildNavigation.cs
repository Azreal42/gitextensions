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
                .FirstOrDefault(row => row.FindAncestorOfType<ListBox>() == _commits)?.DataContext is { } item ? HistoryRevisionItems.Key(item) : null;
            RefreshHistoryMenuStates();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private GitCompareRevision? CurrentRelativeRevision()
        => _parentChildLatestCommit is { } key && HistoryItem(key) is { } item ? HistoryRevisionItems.Revision(item)
            : PrimaryHistoryItem is { } selected ? HistoryRevisionItems.Revision(selected) : null;

    private IReadOnlyList<GitCompareRevision> RelativeRevisionParents(GitCompareRevision current) => current.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => [new("", GitCompareRevisionKind.Index)],
        GitCompareRevisionKind.Index => _historyGraph.CurrentHead is { Length: > 0 } head ? [new(head)] : [],
        _ => _history.FirstOrDefault(commit => commit.Hash == current.Revision)?.Parents.Select(hash => new GitCompareRevision(hash)).ToArray() ?? []
    };

    private IReadOnlyList<GitCompareRevision> RelativeRevisionChildren(GitCompareRevision current)
    {
        object? row = HistoryItem(current);
        if (row is null) { return []; }
        return _historyGraph.GetChildren(HistoryRevisionItems.Key(row)).Select(HistoryItem).OfType<object>()
            .Select(HistoryRevisionItems.Revision).ToArray();
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
        object? row = HistoryItem(target);
        return row is not null ? ApplyQuickSearchAsync(_commits.Items.IndexOf(row))
            : target.Kind == GitCompareRevisionKind.Commit ? ShowFilteredRevisionErrorAsync(target.Revision) : Task.CompletedTask;
    }
}
