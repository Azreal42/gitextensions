namespace GitExtensions.Linux;

public sealed record WorktreeCreateOptions(string Path, string Revision = "HEAD", string? NewBranch = null);

public sealed class WorktreeSnapshot
{
    internal WorktreeSnapshot(GitWorktree value, bool main, bool current, WorktreeBinding binding, string? error)
    { Value = value; IsMain = main; IsCurrent = current; Binding = binding; ValidationError = error; }
    public string Path => Value.Path;
    public string? Head => Value.Head;
    public string? Branch => Value.Branch;
    public bool IsBare => Value.IsBare;
    public bool IsDetached => Value.IsDetached;
    public bool IsMain { get; }
    public bool IsCurrent { get; }
    public bool Exists => Binding.Root.Identity?.IsDirectory == true;
    public bool IsLocked => LockedReason is not null;
    public string? LockedReason => Value.LockedReason;
    public string? PrunableReason => Value.PrunableReason;
    public string? ValidationError { get; }
    public bool CanOpen => Exists && !IsCurrent && ValidationError is null;
    public bool CanRemove => Exists && !IsMain && !IsCurrent && !IsBare && ValidationError is null;
    internal GitWorktree Value { get; }
    internal WorktreeBinding Binding { get; }
}

public sealed record WorktreeOperationResult(GitResult Result, WorktreeSnapshot? Worktree, bool UsesRelativePaths = false,
    bool EnabledRelativeWorktreeExtension = false);

public sealed class WorktreeOperationException : InvalidOperationException
{
    internal WorktreeOperationException(string message, bool changed, GitResult? result = null, Exception? inner = null)
        : base(message, inner) { HasRepositoryChanges = changed; Result = result; }
    public bool HasRepositoryChanges { get; }
    public GitResult? Result { get; }
}

internal sealed record WorktreeNode(string LogicalPath, GitRulesLocation Location, GitRulesIdentity? Identity, string? ContentHash)
{
    internal string Path => Location.Path;
}
internal sealed record WorktreeBinding(WorktreeNode Root, WorktreeNode Common, WorktreeNode? Marker,
    WorktreeNode? Admin, WorktreeNode? Backlink, WorktreeNode? CommonLink, WorktreeNode? Head);
