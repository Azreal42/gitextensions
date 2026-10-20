namespace GitExtensions.Linux;

public enum GitFileStagedStatus { None, Unknown, Index, WorkTree }

/// <summary>Menu state is separate from diff identity and from the displayed A/B classification.</summary>
public sealed record GitComparisonFileMetadata(GitRevisionFile File, GitFileStagedStatus Staged,
    bool IsTracked, bool IsSubmodule, bool IsAssumeUnchanged, bool IsSkipWorktree)
{
    public GitSubmoduleBrowseSelection? SubmoduleSelection { get; init; }
    public string? SubmoduleSelectionError { get; init; }
}

/// <summary>Child-repository endpoints captured from one superproject leaf; its index points to a child commit.</summary>
public sealed record GitSubmoduleBrowseSelection(GitCompareRevision Selected, GitCompareRevision? First = null,
    string? IndexGitlink = null);
