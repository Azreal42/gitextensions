using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux;

public sealed record GitRevisionFileClassification(IReadOnlyList<GitRevisionFile> AToB,
    IReadOnlyList<GitRevisionFile> BaseToA, IReadOnlyList<GitRevisionFile> BaseToB);

/// <summary>Preserves FileStatusDiffCalculator's change provenance, independently of display direction.</summary>
public static class GitRevisionFileClassifier
{
    public static GitRevisionFileClassification Classify(IReadOnlyList<GitRevisionFile> aToB,
        IReadOnlyList<GitRevisionFile> baseToA, IReadOnlyList<GitRevisionFile> baseToB,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aToB);
        ArgumentNullException.ThrowIfNull(baseToA);
        ArgumentNullException.ThrowIfNull(baseToB);
        cancellationToken.ThrowIfCancellationRequested();
        NameEqualityComparer comparer = new(cancellationToken);
        // FileStatusDiffCalculator.cs:244-270. Exact moves/copies do not prove different content.
        GitRevisionFile[] different = [.. aToB.Where(file => !((file.Status is 'R' or 'C') && file.SimilarityPercent == 100))];
        GitRevisionFile[] same = [.. baseToB.Intersect(baseToA, comparer).Except(different, comparer)];
        GitRevisionFile[] onlyA = [.. baseToA.Except(baseToB, comparer)];
        GitRevisionFile[] onlyB = [.. baseToB.Except(baseToA, comparer)];
        return new(Apply(aToB), Apply(baseToA), Apply(baseToB));

        GitRevisionFile[] Apply(IReadOnlyList<GitRevisionFile> files) => [.. files.Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiffBranchStatus status = same.Any(other => comparer.Equals(other, file)) ? DiffBranchStatus.SameChange
                : onlyA.Any(other => comparer.Equals(other, file)) ? DiffBranchStatus.OnlyAChange
                : onlyB.Any(other => comparer.Equals(other, file)) ? DiffBranchStatus.OnlyBChange
                : DiffBranchStatus.UnequalChange;
            return file with { DiffStatus = status };
        })];
    }

    // GitItemStatusNameEqualityComparer.cs:22-31. This is pairwise matching, not a transitive identity.
    private sealed class NameEqualityComparer(CancellationToken cancellationToken) : EqualityComparer<GitRevisionFile>
    {
        public override bool Equals(GitRevisionFile? x, GitRevisionFile? y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (x is null || y is null) { return x is null && y is null; }
            return x.Path == y.Path
                || (!string.IsNullOrWhiteSpace(x.OriginalPath) && x.OriginalPath == y.Path)
                || (!string.IsNullOrWhiteSpace(y.OriginalPath) && x.Path == y.OriginalPath)
                || (!string.IsNullOrWhiteSpace(x.OriginalPath) && !string.IsNullOrWhiteSpace(y.OriginalPath) && x.OriginalPath == y.OriginalPath);
        }

        public override int GetHashCode(GitRevisionFile obj) => 0;
    }
}
