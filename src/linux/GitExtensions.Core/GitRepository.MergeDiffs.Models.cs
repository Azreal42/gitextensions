namespace GitExtensions.Linux;

public sealed record GitCombinedDiff(string Revision, IReadOnlyList<string> Parents, IReadOnlyList<GitRevisionFile> Files);
public sealed record GitCombinedDiffOptions(bool OmitUninterestingDiff = false, GitComparisonDiffOptions? DiffOptions = null,
    bool UseHistogram = false, bool UseGitColoring = false);
public enum GitRevisionDiffGroupKind { Parent, Combined, Comparison, RangeDiff, BaseToSecond, BaseToFirst }
public sealed record GitRevisionDiffGroup(GitRevisionDiffGroupKind Kind, string Revision, string? BaseRevision,
    IReadOnlyList<GitRevisionFile> Files, GitCombinedDiff? CombinedDiff = null, GitRangeDiffComparison? RangeDiff = null);
