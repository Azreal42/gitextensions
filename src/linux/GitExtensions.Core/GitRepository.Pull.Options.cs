namespace GitExtensions.Linux;

/// <summary>Follow the remote's tagopt unless the user explicitly selects all/no tags.</summary>
public enum GitFetchTags { FollowTagOpt, None, All }
public enum GitPullMode { Merge, Rebase }

public sealed record GitFetchOptions(GitFetchTags Tags = GitFetchTags.FollowTagOpt,
    bool Unshallow = false, bool Prune = false, bool PruneTags = false);

/// <summary>AutoStash is the original save/pull/offer-to-pop workflow, not Git's --autostash.</summary>
public sealed record GitPullOptions(GitFetchTags Tags = GitFetchTags.FollowTagOpt,
    bool Unshallow = false, bool AutoStash = false, bool IncludeUntrackedInAutoStash = false);

/// <summary>A failed pull retains its saved changes and returns Git's output to its host.</summary>
public sealed record GitPullExecutionResult(GitResult Result, GitStash? AutoStash,
    IReadOnlyList<string> Arguments)
{
    public GitResult? StashResult { get; init; }
}
