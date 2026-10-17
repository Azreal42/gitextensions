namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>
    /// FormDiff's ordered A-to-B and common-BASE groups. The caller captures HEAD once;
    /// null means that capture had no HEAD, and never requests a new checkout lookup.
    /// </summary>
    public async Task<IReadOnlyList<GitComparisonGroup>> GetComparisonGroupsAsync(GitCompareRevision from, GitCompareRevision to,
        string? capturedHead, bool showDiffForAllParents = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        cancellationToken.ThrowIfCancellationRequested();
        if (capturedHead is not null && !IsObjectId(capturedHead))
        { throw new ArgumentException("The captured HEAD must be a complete Git object ID, or null for an unborn repository.", nameof(capturedHead)); }

        GitCompareRevision first = await ResolveComparisonRevisionAsync(from, cancellationToken).ConfigureAwait(false);
        GitCompareRevision second = await ResolveComparisonRevisionAsync(to, cancellationToken).ConfigureAwait(false);
        if (first.Kind == GitCompareRevisionKind.Commit && second.Kind == GitCompareRevisionKind.Commit)
        {
            IReadOnlyList<GitRevisionDiffGroup> groups = await GetRevisionDiffGroupsAsync(
                [second.Revision, first.Revision], showDiffForAllParents, cancellationToken).ConfigureAwait(false);
            return groups.Select(group => new GitComparisonGroup(group.Kind,
                new(new(group.BaseRevision!), new(group.Revision), group.Files, []), group.RangeDiff)).ToArray();
        }

        GitComparison comparison = await GetComparisonAsync(first, second, cancellationToken).ConfigureAwait(false);
        GitComparisonGroup ordinary = new(GitRevisionDiffGroupKind.Comparison, comparison);
        if (!showDiffForAllParents || capturedHead is null) { return [ordinary]; }

        string head = await ResolveRevisionAsync(capturedHead, cancellationToken).ConfigureAwait(false);
        string firstTip = first.Kind == GitCompareRevisionKind.Commit ? first.Revision : head;
        string secondTip = second.Kind == GitCompareRevisionKind.Commit ? second.Revision : head;
        // FileStatusDiffCalculator substitutes the opening HEAD for artificial tips
        // only when finding BASE and computing the real committed series for range-diff.
        GitRangeDiffComparison? range = await GetRangeDiffComparisonAsync([secondTip, firstTip], cancellationToken).ConfigureAwait(false);
        if (range is null) { return [ordinary]; }

        GitCompareRevision common = new(range.CommonAncestor);
        GitComparison baseToSecond = await GetComparisonAsync(common, second, cancellationToken).ConfigureAwait(false);
        GitComparison baseToFirst = await GetComparisonAsync(common, first, cancellationToken).ConfigureAwait(false);
        GitRevisionFileClassification classification = GitRevisionFileClassifier.Classify(
            comparison.Files, baseToFirst.Files, baseToSecond.Files, cancellationToken);
        List<GitComparisonGroup> result = [ordinary with { Comparison = comparison with { Files = classification.AToB } }];
        if (await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false) >= new Version(2, 19))
        {
            // The range row itself has no file leaves. Its context remains the actual
            // A/B endpoints; its series metadata deliberately contains only real commits.
            result.Add(new(GitRevisionDiffGroupKind.RangeDiff,
                new(comparison.Base, comparison.Compare, [], []), range));
        }
        result.Add(new(GitRevisionDiffGroupKind.BaseToSecond, baseToSecond with { Files = classification.BaseToB }));
        result.Add(new(GitRevisionDiffGroupKind.BaseToFirst, baseToFirst with { Files = classification.BaseToA }));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
