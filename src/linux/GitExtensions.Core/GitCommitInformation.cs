namespace GitExtensions.Linux;

public sealed record GitCommitIdentity(string Name, string Email, DateTimeOffset Date);

/// <summary>Dedicated commit details; independent of the graph's lightweight GitCommit reader.</summary>
public sealed record GitCommitDetails(string Hash, string TreeHash, IReadOnlyList<string> Parents,
    GitCommitIdentity Author, GitCommitIdentity Committer, string Subject, string Body, string? Notes)
{
    /// <summary>Exact %B message; Subject/Body retain their independent %s/%b projections.</summary>
    public string Message { get; init; } = Subject + (Body.Length == 0 ? "" : "\n\n" + Body);
}

[Flags]
public enum GitCommitReferenceScope
{
    None = 0,
    LocalBranches = 1,
    RemoteBranches = 2,
    Tags = 4,
    All = LocalBranches | RemoteBranches | Tags
}

public enum GitCommitReferenceKind { LocalBranch, RemoteBranch, Tag }

/// <summary>Full names and object identities remain authoritative even when display names collide.</summary>
public sealed record GitCommitReference(GitCommitReferenceKind Kind, string FullName, string ShortName,
    string ObjectHash, string? RevisionHash, string? SymbolicTarget, bool IsCurrent, DateTimeOffset? TaggerDate);

/// <summary>A detached HEAD is not a branch reference. ContainsSelectedCommit includes equality.</summary>
public sealed record GitDetachedHeadInfo(string Hash, bool ContainsSelectedCommit);

public sealed record GitCommitContainment(string CommitHash, string? CurrentBranchFullName,
    GitDetachedHeadInfo? DetachedHead, IReadOnlyList<GitCommitReference> References);

public sealed record GitAnnotatedTagMessage(string FullName, string ObjectHash, string RevisionHash, string Message);
public sealed record GitCommitDescription(string CommitHash, string TagName, int? CommitDistance, string Raw);
