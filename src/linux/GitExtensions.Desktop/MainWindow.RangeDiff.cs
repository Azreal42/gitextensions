namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private GitCommit[] SelectedRevisionDiffCommits()
    {
        GitCommit[] selected = (_commits.SelectedItems?.OfType<GitCommit>() ?? [])
            .DistinctBy(commit => commit.Hash, StringComparer.Ordinal).ToArray();
        if (_commits.SelectedItem is not GitCommit primary) { return selected; }
        // RevisionGrid.GetSelectedRevisions preserves selection order unless a sort is
        // explicitly requested. BASE can precede A here even when the graph places it last.
        return [primary, .. selected.Where(commit => commit.Hash != primary.Hash)];
    }

    private async Task ShowSelectedRangeDiffAsync()
    {
        if (_repository is null || _busy) { return; }
        GitCommit[] selected = SelectedRevisionDiffCommits();
        if (selected.Length is < 2 or > 4)
        {
            _state.Text = T("Select two, three or four commits to compare their ranges.");
            return;
        }
        if (!_revisionDiffPreferences.ShowDiffForAllParents)
        {
            _state.Text = T("Enable file differences for all parents to compare commit ranges.");
            return;
        }

        // Reuse the same grouped diff and viewer as the original FileStatusList.
        // Selection contains real history hashes; neither worktree nor index is a series.
        SelectDetailsTab("DiffTab");
        if (SelectRevisionDiffGroup(GitRevisionDiffGroupKind.RangeDiff)) { return; }

        GitRepository repository = _repository;
        await RunAsync("Range diff", current => LoadCommitFilesAsync(current, CurrentRevision), refresh: false);
        // SelectionChanged ignores a busy browser, so select only after its load is idle.
        if (_lifetime.IsCancellationRequested || _repository != repository) { return; }
        if (!SelectRevisionDiffGroup(GitRevisionDiffGroupKind.RangeDiff))
        {
            _state.Text = T("The selected commits do not form two valid commit ranges.");
        }
    }
}
