using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Task SelectNextForkPointAsDiffBaseAsync()
    {
        if (!CanQuickSearch) { return Task.CompletedTask; }
        GitCompareRevision[] revisions = ForkPointSelectedRevisions();
        // Windows' >2 native SelectedRows ordering is not yet qualified by this bounded candidate.
        // Preserve that state instead of guessing an order or dropping additional endpoints.
        if (revisions.Length is 0 or > 2) { return Task.CompletedTask; }
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
        // For one/two inputs Windows re-toggles exactly the first input after
        // selecting fork. If it is the same row, the last row may not be deselected.
        GitCompareRevision[] selection = revisions[0] == fork ? [fork] : [revisions[0], fork];
        return ApplyLoadedHistorySelectionAsync(selection, current);
    }

    private GitCompareRevision[] ForkPointSelectedRevisions()
    {
        if (_browseInitialComparison is { } pair)
        { return pair.Selected == pair.First ? [pair.Selected] : [pair.Selected, pair.First]; }
        if (QuickSearchArtificialCount > 0)
        {
            int index = QuickSearchCurrentIndex();
            if (index is 0 or 1)
            { return [new("", index == 0 ? GitCompareRevisionKind.WorkingTree : GitCompareRevisionKind.Index)]; }
        }
        return SelectedRevisionDiffCommits().Select(commit => new GitCompareRevision(commit.Hash)).ToArray();
    }

    // The loaded graph owns rows; the existing comparison readers own endpoint IO.
    // Only the final selection is published, so no transient empty diff or global refresh is introduced.
    private Task ApplyLoadedHistorySelectionAsync(IReadOnlyList<GitCompareRevision> selection, GitCommit currentCell)
    {
        GitCommit[] rows = selection.Where(revision => revision.Kind == GitCompareRevisionKind.Commit)
            .Select(revision => _commits.Items.OfType<GitCommit>().FirstOrDefault(commit => commit.Hash == revision.Revision))
            .OfType<GitCommit>().ToArray();
        if (rows.Length != selection.Count(revision => revision.Kind == GitCompareRevisionKind.Commit)
            || !_commits.Items.OfType<GitCommit>().Any(commit => commit.Hash == currentCell.Hash)) { return Task.CompletedTask; }
        bool changed = !ForkPointSelectedRevisions().SequenceEqual(selection);
        GitCompareRevision primary = selection[0];
        if (changed)
        {
            CancelQuickSearchClipboard(); CancelRevisionReads();
            bool refreshing = _refreshing, moving = _movingRevisionNavigation;
            _refreshing = true; _movingRevisionNavigation = true;
            try
            {
                _browseInitialComparison = null; _selectedArtificialHistoryRevision = null; _quickSearchArtificialSelection = null;
                _commits.Selection.BeginBatchUpdate();
                try
                {
                    _commits.Selection.Clear();
                    foreach (GitCommit commit in rows) { _commits.Selection.Select(_commits.Items.IndexOf(commit)); }
                }
                finally { _commits.Selection.EndBatchUpdate(); }
                if (selection.Any(revision => revision.Kind != GitCompareRevisionKind.Commit))
                {
                    _browseInitialComparison = new(selection[^1], primary);
                    _selectedArtificialHistoryRevision = primary.Kind != GitCompareRevisionKind.Commit ? primary : null;
                }
                _historySelectionOrder.Clear();
                _historySelectionOrder.AddRange(rows.Reverse().Select(commit => commit.Hash));
                _quickSearchLastCommit = primary.Kind == GitCompareRevisionKind.Commit ? primary.Revision : null;
                _parentChildLatestCommit = _quickSearchLastCommit;
            }
            finally { _refreshing = refreshing; _movingRevisionNavigation = moving; }
            if (primary.Kind != GitCompareRevisionKind.Commit) { ClearCommitInformation(); _gpgInfo.Text = ""; }
            // Ordinary Browse hosts one artificial endpoint in Changes. A typed
            // two-endpoint comparison uses the existing shared revision Diff panel.
            if (UsesArtificialInitialComparison && (_tabs.SelectedItem as TabItem)?.Name == "ChangesTab")
            { SelectDetailsTab("DiffTab"); }
        }
        _commits.ScrollIntoView(currentCell); _commits.UpdateLayout();
        _applyingQuickSearchSelection = true;
        try
        {
            // SetSelectedRevision initially sets CurrentCell to the fork row;
            // re-toggling the original row does not move that keyboard anchor.
            if (_commits.ContainerFromItem(currentCell) is Control row) { row.Focus(); }
            else { _commits.Focus(); }
        }
        finally { _applyingQuickSearchSelection = false; }
        _quickSearchArtificialSelection = primary.Kind != GitCompareRevisionKind.Commit ? primary : null;
        _quickSearchReturnTab = null;
        RefreshRebaseMenu(); RefreshRevisionContextMenu(); RefreshHistoryMenuStates();
        OnGitHubActionsSelectionChanged(); UpdateHistoryPresentation();
        return !changed ? Task.CompletedTask : primary.Kind == GitCompareRevisionKind.Commit
            ? LoadSelectedRevisionDetailsAsync(rows[0]) : LoadVisibleRevisionPanelAsync();
    }
}
