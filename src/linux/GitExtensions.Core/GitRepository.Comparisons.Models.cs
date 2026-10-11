using GitCommands.Settings;

namespace GitExtensions.Linux;

public enum GitCompareRevisionKind { Commit, Index, WorkingTree }
public sealed record GitCompareRevision(string Revision = "HEAD", GitCompareRevisionKind Kind = GitCompareRevisionKind.Commit);
public sealed record GitComparison(GitCompareRevision Base, GitCompareRevision Compare,
    IReadOnlyList<GitRevisionFile> Files, IReadOnlyList<string> UntrackedPaths);
public sealed record GitComparisonDiffOptions(IgnoreWhitespaceKind IgnoreWhitespace = IgnoreWhitespaceKind.None,
    int ContextLines = 3, bool ShowEntireFile = false, bool TreatAllFilesAsText = false);
public sealed record GitComparisonCommitCounts(int BaseOnly, int CompareOnly);
