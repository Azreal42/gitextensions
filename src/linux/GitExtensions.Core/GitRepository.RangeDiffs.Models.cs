using GitCommands.Settings;

namespace GitExtensions.Linux;

public sealed record GitRangeDiffSeries(string BaseRevision, string TipRevision, int CommitCount);
public sealed record GitRangeDiffComparison(GitRangeDiffSeries First, GitRangeDiffSeries Second, string CommonAncestor, int SelectionCount);
public sealed record GitRangeDiffOptions(int ContextLines = 3, IgnoreWhitespaceKind IgnoreWhitespace = IgnoreWhitespaceKind.None,
    bool TreatAllFilesAsText = false, bool UseHistogram = false, bool UseGitColoring = false,
    IReadOnlyList<string>? Paths = null, int? CreationFactor = null, bool ShowEntireFile = false);
