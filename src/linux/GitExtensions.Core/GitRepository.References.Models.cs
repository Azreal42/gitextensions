using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Tag;

namespace GitExtensions.Linux;

public sealed record GitCreateBranchOptions(string Name, string Revision = "HEAD", bool Checkout = true, bool Orphan = false, bool ClearWorkingTree = false);
public enum GitCheckoutKind { LocalBranch, RemoteBranch, Revision }
public sealed record GitCheckoutOptions(string Revision, GitCheckoutKind Kind = GitCheckoutKind.LocalBranch,
    CheckoutNewBranchMode NewBranchMode = CheckoutNewBranchMode.DontCreate, string? LocalBranch = null,
    LocalChangesAction Changes = LocalChangesAction.DontChange, bool IncludeUntrackedInStash = true, bool ReapplyStash = false);
public sealed record GitCheckoutResult(GitResult Result, string? CreatedStash);
public sealed record GitBranchDeletionPreview(GitReference Reference, bool IsMerged, GitWorktree? Worktree, bool IsMainWorktree);
public sealed record GitDeleteBranchesOptions(IReadOnlyList<string> Names, bool Force = false, bool RemoveLinkedWorktrees = false, bool ForceWorktreeRemoval = false);
public sealed record GitCreateTagOptions(string Name, string Revision = "HEAD", TagOperation Operation = TagOperation.Lightweight,
    string Message = "", string? SigningKey = null, bool Force = false, string? PushRemote = null);
