using Avalonia.Collections;
using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Task SelectNextForkPointAsDiffBaseAsync()
    {
        if (!CanQuickSearch) { return Task.CompletedTask; }
        GitCompareRevision[] revisions = ForkPointSelectedRevisions();
        if (revisions.Length == 0) { return Task.CompletedTask; }
        GitCompareRevision revision = revisions[^1];
        while (revision.Kind is GitCompareRevisionKind.WorkingTree or GitCompareRevisionKind.Index)
        {
            GitCompareRevision? parent = RelativeRevisionParents(revision).FirstOrDefault();
            if (parent is null) { return Task.CompletedTask; }
            revision = parent;
        }
        Dictionary<string, GitCommit> loaded = new(StringComparer.Ordinal);
        foreach (GitCommit commit in _history) { loaded.TryAdd(commit.Hash, commit); }
        if (!loaded.TryGetValue(revision.Revision, out GitCommit? current)) { return Task.CompletedTask; }
        HashSet<string> branchTips = _navigationReferences.Where(reference => (reference.IsBranch || reference.IsRemote) && reference.RevisionHash is not null)
            .Select(reference => reference.RevisionHash!).ToHashSet(StringComparer.Ordinal);
        // RevisionGridControl.SelectNextForkPointAsDiffBase: advance at least once,
        // then stop on a local/remote ref or a graph node with other than one child.
        do
        {
            string? parent = current.Parents.FirstOrDefault();
            GitCommit? previous = parent is null ? null : loaded.GetValueOrDefault(parent);
            if (previous is null) { break; }
            current = previous;
        }
        while (!branchTips.Contains(current.Hash)
            && RelativeRevisionChildren(new(current.Hash)).Count == 1);

        GitCompareRevision fork = new(current.Hash);
        // RevisionGridControl first selects the fork, then re-toggles all but
        // the old base (or the sole input). WinForms SelectedRows is newest first:
        // adding prepends; removing the last survivor is forbidden. CurrentCell
        // changes only when a toggle leaves one row, even if the fork is removed.
        List<GitCompareRevision> selection = [fork];
        GitCompareRevision currentCell = fork;
        foreach (GitCompareRevision selected in revisions.Take(Math.Max(1, revisions.Length - 1)))
        {
            int index = selection.IndexOf(selected);
            if (index < 0) { selection.Insert(0, selected); }
            else if (selection.Count > 1) { selection.RemoveAt(index); }
            if (selection.Count == 1) { currentCell = selection[0]; }
        }
        if (currentCell.Kind != GitCompareRevisionKind.Commit
            || !loaded.TryGetValue(currentCell.Revision, out GitCommit? focused)) { return Task.CompletedTask; }
        return ApplyLoadedHistorySelectionAsync(selection, focused);
    }

    private GitCompareRevision[] ForkPointSelectedRevisions() => SelectedHistoryRevisions();

    // The loaded graph owns rows; the existing comparison readers own endpoint IO.
    // Only the final selection is published, so no transient empty diff or global refresh is introduced.
    private Task ApplyLoadedHistorySelectionAsync(IReadOnlyList<GitCompareRevision> selection, GitCommit currentCell)
    {
        object[] rows = selection.Select(HistoryItem).OfType<object>().ToArray();
        if (rows.Length != selection.Count
            || !_commits.Items.OfType<GitCommit>().Any(commit => commit.Hash == currentCell.Hash)) { return Task.CompletedTask; }
        bool changed = !ForkPointSelectedRevisions().SequenceEqual(selection)
            || !(_commits.SelectedItems?.Cast<object>() ?? []).SequenceEqual(rows)
            || rows.Length > 0 && !ReferenceEquals(_commits.SelectedItem, rows[0]);
        GitCompareRevision primary = selection[0];
        if (changed)
        {
            CancelQuickSearchClipboard(); CancelRevisionReads();
            bool refreshing = _refreshing, moving = _movingRevisionNavigation;
            _refreshing = true; _movingRevisionNavigation = true;
            try
            {
                _browseInitialComparison = null; _selectedArtificialHistoryRevision = null; _quickSearchArtificialSelection = null;
                // The public setter synchronizes this ordered writable list in
                // one model batch. A raw model batch alone retains the old order
                // of surviving SelectedItems; its first Select owns the primary.
                _commits.SelectedItems = new AvaloniaList<object?>(rows);
                _commits.Selection.AnchorIndex = _commits.Items.IndexOf(currentCell);
                if (selection.Any(revision => revision.Kind != GitCompareRevisionKind.Commit))
                {
                    _browseInitialComparison = new(selection[^1], primary);
                    _selectedArtificialHistoryRevision = primary.Kind != GitCompareRevisionKind.Commit ? primary : null;
                }
                _historySelectionOrder.Clear();
                _historySelectionOrder.AddRange(rows.Reverse().Select(HistoryRevisionItems.Key));
                _quickSearchLastCommit = primary.Kind == GitCompareRevisionKind.Commit ? primary.Revision : null;
                _parentChildLatestCommit = _quickSearchLastCommit;
            }
            finally { _refreshing = refreshing; _movingRevisionNavigation = moving; }
            if (primary.Kind != GitCompareRevisionKind.Commit) { ClearCommitInformation(); _gpgInfo.Text = ""; }

        }
        _commits.ScrollIntoView(currentCell); _commits.UpdateLayout();
        _applyingQuickSearchSelection = true;
        try
        {
            // SetSelectedRevision starts at the fork. A collision that leaves
            // one survivor moves CurrentCell; otherwise it can stay unselected.
            if (_commits.ContainerFromItem(currentCell) is Control row) { row.Focus(); }
            else { _commits.Focus(); }
        }
        finally { _applyingQuickSearchSelection = false; }
        _commits.Selection.AnchorIndex = _commits.Items.IndexOf(currentCell);
        _quickSearchArtificialSelection = primary.Kind != GitCompareRevisionKind.Commit ? primary : null;

        RefreshRebaseMenu(); RefreshRevisionContextMenu(); RefreshHistoryMenuStates();
        OnGitHubActionsSelectionChanged(); UpdateHistoryPresentation();
        return !changed ? Task.CompletedTask : primary.Kind == GitCompareRevisionKind.Commit
            ? LoadSelectedRevisionDetailsAsync((GitCommit)rows[0]) : LoadVisibleRevisionPanelAsync();
    }
}
