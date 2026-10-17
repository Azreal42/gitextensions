namespace GitExtensions.Linux;

public enum GitFileStagedStatus { None, Unknown, Index, WorkTree }

/// <summary>Menu state is separate from diff identity and from the displayed A/B classification.</summary>
public sealed record GitComparisonFileMetadata(GitRevisionFile File, GitFileStagedStatus Staged,
    bool IsTracked, bool IsSubmodule, bool IsAssumeUnchanged, bool IsSkipWorktree);
