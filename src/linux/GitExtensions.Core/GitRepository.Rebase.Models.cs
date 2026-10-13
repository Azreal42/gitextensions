namespace GitExtensions.Linux;

/// <summary>The original FormRebase options. BranchName is the upstream without a range, and the branch to rewrite with From/OnTo.</summary>
public sealed record GitRebaseOptions
{
    public string BranchName { get; init; } = "";
    public string? From { get; init; }
    public string? OnTo { get; init; }
    public bool Interactive { get; init; }
    public bool PreserveMerges { get; init; }
    public bool AutoSquash { get; init; }
    public bool AutoStash { get; init; }
    public bool IgnoreDate { get; init; }
    public bool CommitterDateIsAuthorDate { get; init; }
    public bool? UpdateRefs { get; init; }
}

public sealed record GitRebaseCapabilities(Version GitVersion, bool SupportRebaseMerges, bool SupportUpdateRefs,
    bool AutoSquash, bool AutoStash, bool UpdateRefs);
public enum GitRebaseBackend { None, Merge, Apply }
public enum GitRebaseStepStatus { Applied, Applying, Pending, Skipped }
public sealed record GitRebasePatch(string Action, string RawLine, string? CommitHash, GitCommit? Commit,
    GitRebaseStepStatus Status, string? PatchPath = null)
{
    public string Subject => Commit?.Subject ?? "";
    public string Body => Commit?.Body ?? "";
    public string Author => Commit?.AuthorName ?? "";
    public DateTimeOffset? AuthorDate => Commit?.AuthoredAt;
    public DateTimeOffset? CommitDate { get; init; }
}
public sealed record GitRebaseState(GitRebaseBackend Backend, string? OriginalHead, string? OnTo, string? HeadName,
    string? CurrentCommit, IReadOnlyList<GitRebasePatch> Steps, IReadOnlyList<GitConflictPath> Conflicts,
    string? TodoPath, string? TodoText)
{
    public GitRebaseOptions? Options { get; init; }
    public string? EncodingError { get; init; }
    public bool UsesLegacyPlan { get; init; }
    public bool IsActive => Backend != GitRebaseBackend.None;
    public bool HasConflicts => Conflicts.Count > 0;
    public IReadOnlyList<GitRebasePatch> TodoPatches => Steps;
    public bool CanContinue => IsActive && !HasConflicts && EncodingError is null;
    public bool CanEditTodo => Backend == GitRebaseBackend.Merge && TodoPath is not null && EncodingError is null && !UsesLegacyPlan;
    public string? Current => CurrentCommit;
}
