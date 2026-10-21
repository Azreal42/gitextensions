namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>
    /// Multi-selection groups in Windows caller order: B,A; B,BASE,A; or B,baseB,A,baseA.
    /// An empty selection returns no groups; a single revision must use GetRevisionDiffGroupsAsync
    /// or GetArtificialRevisionComparisonAsync so its parent/combined-diff contract stays explicit.
    /// Only range eligibility/series substitute the captured HEAD for an artificial tip.
    /// </summary>
    public async Task<IReadOnlyList<GitComparisonGroup>> GetComparisonGroupsAsync(IReadOnlyList<GitCompareRevision> revisions,
        string? capturedHead, bool showDiffForAllParents = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        cancellationToken.ThrowIfCancellationRequested();
        if (capturedHead is not null && !IsObjectId(capturedHead))
        { throw new ArgumentException("The captured HEAD must be a complete Git object ID, or null for an unborn repository.", nameof(capturedHead)); }
        GitCompareRevision[] selected = revisions.ToArray();
        foreach (GitCompareRevision revision in selected)
        {
            ArgumentNullException.ThrowIfNull(revision);
            if (!Enum.IsDefined(revision.Kind)) { throw new ArgumentOutOfRangeException(nameof(revisions), "Unknown comparison endpoint kind."); }
        }
        if (selected.Length == 0) { return []; }
        if (selected.Length == 1) { throw new ArgumentException("Select at least two revisions for multi-comparison groups.", nameof(revisions)); }
        using IDisposable operation = BeginOperation();
        if (selected.Length == 2)
        { return await GetComparisonGroupsAsync(selected[1], selected[0], capturedHead, showDiffForAllParents, cancellationToken).ConfigureAwait(false); }
        if (selected.All(revision => revision.Kind == GitCompareRevisionKind.Commit))
        {
            IReadOnlyList<GitRevisionDiffGroup> groups = await GetRevisionDiffGroupsAsync(selected.Select(revision => revision.Revision).ToArray(),
                showDiffForAllParents, cancellationToken).ConfigureAwait(false);
            return ProjectRevisionComparisonGroups(groups);
        }

        GitCompareRevision first = selected[showDiffForAllParents && selected.Length == 4 ? 2 : selected.Length - 1];
        GitComparison comparison = await GetComparisonAsync(first, selected[0], cancellationToken).ConfigureAwait(false);
        GitComparisonGroup ordinary = new(GitRevisionDiffGroupKind.Comparison, comparison);
        if (!showDiffForAllParents || selected.Length > 4) { return [ordinary]; }

        GitRangeDiffComparison? range = null;
        bool realBases = selected.Length == 3 ? selected[1].Kind == GitCompareRevisionKind.Commit
            : selected[1].Kind == GitCompareRevisionKind.Commit && selected[3].Kind == GitCompareRevisionKind.Commit;
        if (realBases && capturedHead is not null && selected.All(revision => revision.Kind != GitCompareRevisionKind.EmptyTree))
        {
            string head = await ResolveRevisionAsync(capturedHead, cancellationToken).ConfigureAwait(false);
            string[] realSeries = selected.Select(revision => revision.Kind == GitCompareRevisionKind.Commit ? revision.Revision : head).ToArray();
            range = await GetRangeDiffComparisonAsync(realSeries, cancellationToken).ConfigureAwait(false);
        }
        if (range is not null)
        { return await BuildAdvancedComparisonGroupsAsync(comparison, range, cancellationToken).ConfigureAwait(false); }

        List<GitComparisonGroup> result = [ordinary];
        foreach (GitCompareRevision revision in selected.Skip(1))
        {
            GitCompareRevision endpoint = await ResolveComparisonRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
            if (endpoint == comparison.Base || endpoint == comparison.Compare) { continue; }
            GitComparison additional = await GetComparisonAsync(endpoint, comparison.Compare, cancellationToken).ConfigureAwait(false);
            result.Add(new(GitRevisionDiffGroupKind.Comparison, additional));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
