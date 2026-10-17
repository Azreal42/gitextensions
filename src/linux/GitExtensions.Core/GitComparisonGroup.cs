namespace GitExtensions.Linux;

/// <summary>A comparison group retains its own typed endpoints, even when range-diff uses captured real commit tips.</summary>
public sealed record GitComparisonGroup(GitRevisionDiffGroupKind Kind, GitComparison Comparison,
    GitRangeDiffComparison? RangeDiff = null);
