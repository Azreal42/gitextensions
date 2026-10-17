namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>
/// The two independent return paths used by RevisionGrid's parent/child commands.
/// The caller supplies neighbours from its current graph and publishes selection synchronously.
/// </summary>
internal sealed class ParentChildNavigationHistory
{
    private enum NavigationDirection { Parent, Child }

    private readonly Action<GitCompareRevision> _select;
    private readonly Stack<GitCompareRevision> _childHistory = new();
    private readonly Stack<GitCompareRevision> _parentHistory = new();
    private NavigationDirection? _direction;

    internal ParentChildNavigationHistory(Action<GitCompareRevision> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        _select = select;
    }

    internal bool HasPreviousChild => _childHistory.Count > 0;
    internal bool HasPreviousParent => _parentHistory.Count > 0;

    internal bool NavigateParent(GitCompareRevision current, IReadOnlyList<GitCompareRevision> parents)
    {
        ValidateArguments(current, parents);
        return _parentHistory.TryPop(out GitCompareRevision? previous)
            ? Navigate(current, previous, NavigationDirection.Parent)
            : NavigateFirstParent(current, parents);
    }

    internal bool NavigateFirstParent(GitCompareRevision current, IReadOnlyList<GitCompareRevision> parents)
    {
        ValidateArguments(current, parents);
        return parents.Count > 0 && Navigate(current, parents[0], NavigationDirection.Parent);
    }

    internal bool NavigateLastParent(GitCompareRevision current, IReadOnlyList<GitCompareRevision> parents)
    {
        ValidateArguments(current, parents);
        return parents.Count > 0 && Navigate(current, parents[^1], NavigationDirection.Parent);
    }

    internal bool NavigateChild(GitCompareRevision current, IReadOnlyList<GitCompareRevision> children)
    {
        ValidateArguments(current, children);
        if (_childHistory.TryPop(out GitCompareRevision? previous))
        {
            return Navigate(current, previous, NavigationDirection.Child);
        }

        return children.Count > 0 && Navigate(current, children[0], NavigationDirection.Child);
    }

    internal void Clear()
    {
        _childHistory.Clear();
        _parentHistory.Clear();
    }

    internal void RevisionsSelectionChanged()
    {
        if (_direction is null) { Clear(); }
    }

    private bool Navigate(GitCompareRevision current, GitCompareRevision target, NavigationDirection direction)
    {
        ArgumentNullException.ThrowIfNull(target);
        NavigationDirection? previousDirection = _direction;
        _direction = direction;
        if (direction == NavigationDirection.Child) { _parentHistory.Push(current); }
        else { _childHistory.Push(current); }

        try
        {
            // Like Windows, record the attempted route before publishing selection.
            // A filtered target or a failed callback is not a transactional rollback.
            _select(target);
        }
        finally { _direction = previousDirection; }

        return true;
    }

    private static void ValidateArguments(GitCompareRevision current, IReadOnlyList<GitCompareRevision> neighbours)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(neighbours);
    }
}
