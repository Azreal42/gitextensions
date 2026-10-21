using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private GitCompareRevision? _selectedArtificialHistoryRevision;

    private void ClearArtificialHistorySelection()
    {
        _selectedArtificialHistoryRevision = null;
        UpdateArtificialHistorySelectionAppearance();
    }

    private void UpdateArtificialHistorySelectionAppearance() => UpdateHistoryPresentation();

    private Task ReadArtificialHistoryRevisionAsync(GitCompareRevision source)
        => ReadBrowserAsync(_revisionRead, "Artificial revision diff", (repository, token) => LoadArtificialHistoryRevisionAsync(repository, source, token));

    private async Task LoadArtificialHistoryRevisionAsync(GitRepository repository, GitCompareRevision source, CancellationToken token)
    {
        if (source.Kind is not (GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree))
        { throw new ArgumentException("An artificial history revision must be the index or working directory.", nameof(source)); }
        string identity = RevisionSelectionIdentity;
        object? previous = CaptureRevisionFileSelection();
        GitCompareRevision parent = source.Kind == GitCompareRevisionKind.WorkingTree ? new("", GitCompareRevisionKind.Index)
            : _historyGraph.CurrentHead is { } head ? new(head) : new("", GitCompareRevisionKind.EmptyTree);
        GitComparison comparison = await repository.GetComparisonAsync(parent, source, token);
        await ShowArtificialHistoryComparisonAsync(repository, comparison, identity, previous, GitRevisionDiffGroupKind.Parent, token);
    }

    private async Task LoadArtificialHistoryComparisonAsync(GitRepository repository, GitCompareRevision first,
        GitCompareRevision selected, CancellationToken token)
    {
        string identity = RevisionSelectionIdentity;
        object? previous = CaptureRevisionFileSelection();
        GitCompareRevision[] selection = SelectedHistoryRevisions();
        if (selection.Length < 2) { selection = [selected, first]; }
        _revisionDiffPreferences = _revisionDiffPreferencesStore.Load();
        IReadOnlyList<GitComparisonGroup> groups = await repository.GetComparisonGroupsAsync(selection, _historyGraph.CurrentHead,
            _revisionDiffPreferences.ShowDiffForAllParents, token);
        await ShowArtificialHistoryComparisonsAsync(repository, groups, identity, previous, token);
    }

    private async Task ShowArtificialHistoryComparisonAsync(GitRepository repository, GitComparison comparison,
        string identity, object? previous, GitRevisionDiffGroupKind kind, CancellationToken token)
    {
        await ShowArtificialHistoryComparisonsAsync(repository, [new(kind, comparison)], identity, previous, token);
    }

    private async Task ShowArtificialHistoryComparisonsAsync(GitRepository repository, IReadOnlyList<GitComparisonGroup> source,
        string identity, object? previous, CancellationToken token)
    {
        List<GitRevisionDiffGroup> groups = [];
        Dictionary<GitRevisionDiffGroup, GitComparison> comparisons = new(ReferenceEqualityComparer.Instance);
        foreach (GitComparisonGroup entry in source)
        {
            GitComparison comparison = entry.Comparison;
            GitRevisionDiffGroup group = new(entry.Kind,
                comparison.Compare.Kind == GitCompareRevisionKind.Commit ? comparison.Compare.Revision : comparison.Compare.Kind.ToString(),
                comparison.Base.Kind == GitCompareRevisionKind.Commit ? comparison.Base.Revision : comparison.Base.Kind.ToString(),
                comparison.Files, RangeDiff: entry.RangeDiff);
            groups.Add(group); comparisons.Add(group, comparison);
        }
        var metadata = await ReadFileMenuMetadataAsync(repository, groups, token, comparisons);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(repository, _repository) || identity != RevisionSelectionIdentity) { return; }
        _revisionComparisons = comparisons; _fileMenuMetadata = metadata;
        TreeViewItem? selected = SetRevisionFileGroups(groups, previous);
        _loadedDiffSelection = identity;
        if (selected is null) { ClearRevisionPreview(); }
        else if (_busy) { await ShowRevisionDiffItemAsync(repository, selected, token); }
        else { await ReadRevisionDiffAsync(selected, token); }
    }

}
