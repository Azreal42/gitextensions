using Avalonia.Controls;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private ContinuousDiffScroll? _continuousScroll;
    internal Task ContinuousScrollCompletion => _continuousScroll?.Completion ?? Task.CompletedTask;

    private void InitializeContinuousScroll()
    {
        _continuousScroll = new(_commitContent!, () => !_busy && !_lifetime.IsCancellationRequested && IsVisible
            && IsEffectivelyEnabled && _revisionRead.Pending == 0 && _revisionDiffRead.Pending == 0
            && !OwnedWindows.Any(child => child.IsVisible), MoveRevisionDiffFileAsync,
            error => { LastException = error; _state.Text = error.Message; AppendOutput(error.Message); });
        Closed += (_, _) => _continuousScroll?.Dispose();
    }

    private async Task MoveRevisionDiffFileAsync(bool forward)
    {
        if (_repository is not { } repository || _revisionFilesView is null || _commitContent is null) { return; }
        // Match FileStatusList's logical tree order, including files under folded nodes; range/status roots are skipped.
        TreeViewItem[] files = FileStatusTreeItems(_revisionFiles).Where(item => item.Tag is RevisionDiffFileSelection).ToArray();
        if (files.Length == 0) { return; }
        int index = Array.IndexOf(files, _revisionFiles.SelectedItem as TreeViewItem);
        int nextIndex = index < 0 ? 0 : index + (forward ? 1 : -1);
        if (nextIndex < 0 || nextIndex >= files.Length) { return; }
        TreeViewItem next = files[nextIndex];
        if (next.Tag is not RevisionDiffFileSelection selection) { return; }
        string revision = RevisionSelectionIdentity;
        string expected = _revisionComparisons.TryGetValue(selection.Group, out GitComparison? pair)
            ? string.Join("\0", repository.Path, selection.File.Path, pair.Base.Kind, pair.Base.Revision, pair.Compare.Kind, pair.Compare.Revision, selection.File.OriginalPath)
            : string.Join("\0", repository.Path, selection.File.Path, selection.Group.Kind, selection.Group.Revision, selection.Group.BaseRevision, selection.File.OriginalPath);
        _loadingCommitFiles = true;
        try { _revisionFilesView.SelectTags([selection]); next.BringIntoView(); }
        finally { _loadingCommitFiles = false; }
        await ReadRevisionDiffAsync(next);
        if (_lifetime.IsCancellationRequested || !ReferenceEquals(repository, _repository) || revision != RevisionSelectionIdentity
            || !ReferenceEquals(_revisionFiles.SelectedItem, next) || _commitContent.TextView.DocumentIdentity != expected || LastException is not null) { return; }
        if (!_commitContent.IsImage) { _commitContent.TextView.LandAtBoundary(bottom: !forward); }
    }
}
