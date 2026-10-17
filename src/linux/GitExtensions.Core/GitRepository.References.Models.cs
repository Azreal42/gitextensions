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

/// <summary>A native checkout prepared before BeforeCheckout, including an exact stash checkpoint. Only its repository can execute it.</summary>
public sealed class GitCheckoutPreparation
{
    internal GitCheckoutPreparation(GitRepository repository, GitCheckoutOptions options, string[] arguments, string? createdStash)
    { Repository = repository; Options = options; Arguments = Array.AsReadOnly(arguments); CreatedStash = createdStash; }
    internal GitCheckoutPreparation(GitCheckoutPreparation preparation, GitRepository submoduleConflictParent)
    {
        Repository = preparation.Repository; Options = preparation.Options; Arguments = preparation.Arguments;
        CreatedStash = preparation.CreatedStash; SubmoduleConflictParent = submoduleConflictParent;
    }
    internal GitRepository Repository { get; }
    internal GitRepository? SubmoduleConflictParent { get; }
    internal IReadOnlyList<string> Arguments { get; }
    public GitCheckoutOptions Options { get; }
    public string? CreatedStash { get; }
}
