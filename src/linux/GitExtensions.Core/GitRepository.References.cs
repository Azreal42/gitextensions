using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Tag;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<GitResult> CreateBranchAsync(GitCreateBranchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClearWorkingTree && !options.Orphan) { throw new ArgumentException("Clearing files is only available when creating an orphan branch.", nameof(options)); }
        if (options.Orphan && !options.Checkout) { throw new ArgumentException("An orphan branch must be checked out.", nameof(options)); }
        return WithMutationAsync(async token =>
        {
            await ValidateReferenceNameAsync(options.Name, branch: true, token).ConfigureAwait(false);
            GitStatus status = await GetStatusAsync(token).ConfigureAwait(false);
            if (options.Orphan)
            {
                await EnsureWorktreeAsync(token).ConfigureAwait(false);
                if ((await GetReferencesAsync(token).ConfigureAwait(false)).Any(reference => reference.Name == "refs/heads/" + options.Name))
                { throw new InvalidOperationException("The branch already exists."); }
                GitResult result = status.IsUnborn
                    ? await ExecuteAsync(["symbolic-ref", "HEAD", "refs/heads/" + options.Name], token).ConfigureAwait(false)
                    : await ExecuteAsync(["checkout", "--orphan", options.Name, await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false), "--"], token).ConfigureAwait(false);
                if (options.ClearWorkingTree) { await ExecuteAsync(["rm", "-r", "--force", "--ignore-unmatch", "--", "."], token).ConfigureAwait(false); }
                return result;
            }
            string revision = await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false);
            return await ExecuteAsync(options.Checkout ? ["switch", "--create", options.Name, "--", revision] : ["branch", "--", options.Name, revision], token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitCheckoutResult> CheckoutAsync(GitCheckoutOptions options, CancellationToken cancellationToken = default)
    {
        ValidateCheckoutOptions(options);
        return WithMutationAsync(async token =>
        {
            GitCheckoutPreparation preparation = await PrepareCheckoutCoreAsync(options, token).ConfigureAwait(false);
            GitCheckoutResult result = await CheckoutPreparedCoreAsync(preparation, token).ConfigureAwait(false);
            if (!result.Result.Success) { throw new GitCommandException(preparation.Arguments, result.Result); }
            return result;
        }, cancellationToken);
    }

    /// <summary>Validate and save the checkout stash before the host's BeforeCheckout scripts. No UI is called under the mutation lock.</summary>
    public Task<GitCheckoutPreparation> PrepareCheckoutAsync(GitCheckoutOptions options, CancellationToken cancellationToken = default)
    {
        ValidateCheckoutOptions(options);
        return WithMutationAsync(token => PrepareCheckoutCoreAsync(options, token), cancellationToken);
    }

    /// <summary>Execute a prepared checkout after its event scripts. Git still validates the named target and worktree on execution.</summary>
    public Task<GitCheckoutResult> CheckoutPreparedAsync(GitCheckoutPreparation preparation, CancellationToken cancellationToken = default)
        => WithMutationAsync(token => CheckoutPreparedCoreAsync(preparation, token), cancellationToken);

    private static void ValidateCheckoutOptions(GitCheckoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Kind) || !Enum.IsDefined(options.NewBranchMode) || !Enum.IsDefined(options.Changes)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if (options.Kind != GitCheckoutKind.RemoteBranch && options.NewBranchMode != CheckoutNewBranchMode.DontCreate)
        { throw new ArgumentException("Creating or resetting a local branch is available for remote checkout.", nameof(options)); }
        if (options.ReapplyStash && options.Changes != LocalChangesAction.Stash) { throw new ArgumentException("Stash reapplication requires the stash action.", nameof(options)); }
    }

    private async Task<GitCheckoutPreparation> PrepareCheckoutCoreAsync(GitCheckoutOptions options, CancellationToken token)
    {
        await EnsureWorktreeAsync(token).ConfigureAwait(false);
        string target;
        if (options.Kind == GitCheckoutKind.Revision) { target = await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false); }
        else
        {
            string prefix = options.Kind == GitCheckoutKind.LocalBranch ? "refs/heads/" : "refs/remotes/";
            target = options.Revision.StartsWith(prefix, StringComparison.Ordinal) ? options.Revision : prefix + ValidateValue(options.Revision);
            GitReference? reference = (await GetReferencesAsync(token).ConfigureAwait(false)).SingleOrDefault(reference => reference.Name == target && reference.SymbolicTarget is null);
            if (reference is null) { throw new ArgumentException("Select an existing branch.", nameof(options)); }
            if (options.Kind == GitCheckoutKind.LocalBranch) { target = reference.ShortName; }
        }
        List<string> arguments = ["switch"];
        if (options.Kind == GitCheckoutKind.Revision || options.Kind == GitCheckoutKind.RemoteBranch && options.NewBranchMode == CheckoutNewBranchMode.DontCreate) { arguments.Add("--detach"); }
        else if (options.Kind == GitCheckoutKind.RemoteBranch)
        {
            await ValidateReferenceNameAsync(options.LocalBranch ?? "", branch: true, token).ConfigureAwait(false);
            arguments.AddRange([options.NewBranchMode == CheckoutNewBranchMode.Reset ? "--force-create" : "--create", options.LocalBranch!, "--track"]);
        }
        else { arguments.Add("--no-guess"); }
        if (options.Changes == LocalChangesAction.Reset) { arguments.Add("--discard-changes"); }
        if (options.Changes == LocalChangesAction.Merge) { arguments.Add("--merge"); }
        arguments.AddRange(["--", target]);
        string? stash = null;
        GitStatus status = await GetStatusAsync(token).ConfigureAwait(false);
        if (options.Changes == LocalChangesAction.Stash && status.Changes.Any(change => options.IncludeUntrackedInStash || !change.IsUntracked))
        {
            List<string> save = ["stash", "push", "--message", "Git Extensions: checkout " + options.Revision];
            if (options.IncludeUntrackedInStash) { save.Add("--include-untracked"); }
            await ExecuteAsync(save, token).ConfigureAwait(false);
            stash = await ResolveRevisionAsync("refs/stash", token).ConfigureAwait(false);
        }
        return new GitCheckoutPreparation(this, options, arguments.ToArray(), stash);
    }

    private async Task<GitCheckoutResult> CheckoutPreparedCoreAsync(GitCheckoutPreparation preparation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!ReferenceEquals(preparation.Repository, this)) { throw new ArgumentException("The checkout preparation belongs to another repository.", nameof(preparation)); }
        GitResult result = preparation.Options.Kind == GitCheckoutKind.Revision
            ? await _runner.RunAsync(Path, [.. GlobalOptions, .. preparation.Arguments], token).ConfigureAwait(false)
            : await ExecuteAsync(preparation.Arguments, token).ConfigureAwait(false);
        if (result.Success && preparation.CreatedStash is not null && preparation.Options.ReapplyStash)
        {
            await ReapplyCheckoutStashCoreAsync(preparation.CreatedStash, token).ConfigureAwait(false);
        }
        return new GitCheckoutResult(result, preparation.CreatedStash);
    }

    /// <summary>Reapply only the stash captured by this checkout, including its index. Never substitutes the current top.</summary>
    public Task<GitResult> ReapplyCheckoutStashAsync(string createdStash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(createdStash);
        return WithMutationAsync(token => ReapplyCheckoutStashCoreAsync(createdStash, token), cancellationToken);
    }

    private async Task<GitResult> ReapplyCheckoutStashCoreAsync(string createdStash, CancellationToken token)
    {
        GitStash? saved = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault(entry => entry.Hash == createdStash);
        if (saved is null) { throw new InvalidOperationException("The checkout stash changed outside this operation; it was not reapplied."); }
        return await ExecuteAsync(["stash", "pop", "--index", "--", saved.Reference], token).ConfigureAwait(false);
    }

    /// <summary>Return a completed revision checkout's native false result; launch errors and cancellation still propagate.</summary>
    public Task<GitResult> CheckoutRevisionAttemptAsync(string revision, CancellationToken cancellationToken = default)
        => CheckoutRevisionAttemptAsync(revision, force: false, cancellationToken);

    /// <summary>Force is explicit for this attempt only; no branch or stash preference is read or written.</summary>
    public Task<GitResult> CheckoutRevisionAttemptAsync(string revision, bool force, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            List<string> arguments = [.. GlobalOptions, "switch", "--detach"];
            if (force) { arguments.Add("--force"); }
            arguments.AddRange(["--", ValidateValue(revision)]);
            return await _runner.RunAsync(Path, arguments, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<IReadOnlyList<GitBranchDeletionPreview>> GetBranchDeletionPreviewAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(ReadBranchDeletionPreviewAsync, cancellationToken);

    private async Task<IReadOnlyList<GitBranchDeletionPreview>> ReadBranchDeletionPreviewAsync(CancellationToken token)
    {
        GitStatus status = await GetStatusAsync(token).ConfigureAwait(false);
        IReadOnlyList<GitWorktree> worktrees = await GetWorktreesAsync(token).ConfigureAwait(false);
        HashSet<string> merged = [];
        if (!status.IsUnborn)
        {
            GitResult result = await ExecuteAsync(["for-each-ref", "--merged=HEAD", "--format=%(refname)", "refs/heads/"], token).ConfigureAwait(false);
            merged.UnionWith(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        return (await GetReferencesAsync(token).ConfigureAwait(false)).Where(reference => reference.IsBranch).Select(reference =>
        {
            GitWorktree? worktree = worktrees.FirstOrDefault(tree => tree.Branch == reference.Name);
            return new GitBranchDeletionPreview(reference, merged.Contains(reference.Name), worktree, worktree is not null && worktree == worktrees.FirstOrDefault());
        }).ToArray();
    }

    public Task<GitResult> DeleteBranchesAsync(GitDeleteBranchesOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ForceWorktreeRemoval && !options.RemoveLinkedWorktrees) { throw new ArgumentException("Forced worktree removal requires selecting linked-worktree removal.", nameof(options)); }
        string[] names = ReferenceNames(options.Names);
        return WithMutationAsync(async token =>
        {
            GitStatus status = await GetStatusAsync(token).ConfigureAwait(false);
            IReadOnlyList<GitBranchDeletionPreview> branches = await ReadBranchDeletionPreviewAsync(token).ConfigureAwait(false);
            GitBranchDeletionPreview[] selected = names.Select(name => branches.SingleOrDefault(branch => branch.Reference.ShortName == name)
                ?? throw new ArgumentException("The selected local branch does not exist: " + name)).ToArray();
            foreach (GitBranchDeletionPreview branch in selected)
            {
                if (branch.Reference.ShortName == status.Branch) { throw new InvalidOperationException("Cannot delete the current branch: " + status.Branch); }
                if (!options.Force && !branch.IsMerged) { throw new InvalidOperationException("The branch has not been merged into HEAD: " + branch.Reference.ShortName); }
                if (branch.Worktree is not null && (!options.RemoveLinkedWorktrees || branch.IsMainWorktree))
                { throw new InvalidOperationException("The branch is checked out in a worktree: " + branch.Worktree.Path); }
                if (branch.Worktree?.LockedReason is not null) { throw new InvalidOperationException("The branch's worktree is locked: " + branch.Worktree.Path); }
            }
            foreach (GitBranchDeletionPreview branch in selected.Where(branch => branch.Worktree is not null))
            {
                GitWorktree tree = branch.Worktree!;
                if (!Directory.Exists(tree.Path)) { await ExecuteAsync(["worktree", "remove", "--force", "--", tree.Path], token).ConfigureAwait(false); }
                else { await ExecuteAsync(options.ForceWorktreeRemoval ? ["worktree", "remove", "--force", "--", tree.Path] : ["worktree", "remove", "--", tree.Path], token).ConfigureAwait(false); }
            }
            return await ExecuteAsync(["branch", options.Force ? "-D" : "-d", "--", .. names], token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitResult> CreateTagAsync(GitCreateTagOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Operation)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if (options.Message.Contains('\0')) { throw new ArgumentException("A tag message cannot contain NUL.", nameof(options)); }
        if (options.PushRemote is not null) { ValidateValue(options.PushRemote); }
        if (options.Operation != TagOperation.Lightweight && string.IsNullOrWhiteSpace(options.Message)) { throw new ArgumentException("Enter a tag message.", nameof(options)); }
        return WithMutationAsync(async token =>
        {
            await ValidateReferenceNameAsync(options.Name, branch: false, token).ConfigureAwait(false);
            string revision = await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false);
            List<string> arguments = ["tag"];
            switch (options.Operation)
            {
                case TagOperation.Lightweight: arguments.Add("--no-sign"); break;
                case TagOperation.Annotate: arguments.AddRange(["--annotate", "--no-sign", "--file=-", "--cleanup=verbatim"]); break;
                case TagOperation.SignWithDefaultKey: arguments.AddRange(["--sign", "--file=-", "--cleanup=verbatim"]); break;
                case TagOperation.SignWithSpecificKey: arguments.AddRange(["--local-user=" + ValidateValue(options.SigningKey ?? ""), "--file=-", "--cleanup=verbatim"]); break;
            }
            if (options.Force) { arguments.Add("--force"); }
            arguments.AddRange(["--", options.Name, revision]);
            GitResult result = await ExecuteAsync(arguments, token, standardInput: options.Operation == TagOperation.Lightweight ? null : options.Message).ConfigureAwait(false);
            if (options.PushRemote is not null) { await PushReferenceTagAsync(options.PushRemote, options.Name, delete: false, token).ConfigureAwait(false); }
            return result;
        }, cancellationToken);
    }

    public Task<GitResult> DeleteTagsAsync(IEnumerable<string> names, string? remote = null, CancellationToken cancellationToken = default)
    {
        string[] selected = ReferenceNames(names);
        return WithMutationAsync(async token =>
        {
            IReadOnlyList<GitReference> references = await GetReferencesAsync(token).ConfigureAwait(false);
            foreach (string name in selected)
            {
                if (!references.Any(reference => reference.Name == "refs/tags/" + name)) { throw new ArgumentException("The selected tag does not exist: " + name); }
            }
            if (remote is not null) { ValidateValue(remote); }
            GitResult result = await ExecuteAsync(["tag", "--delete", "--", .. selected], token).ConfigureAwait(false);
            if (remote is not null)
            {
                foreach (string name in selected) { await PushReferenceTagAsync(remote, name, delete: true, token).ConfigureAwait(false); }
            }
            return result;
        }, cancellationToken);
    }

    /// <summary>Remote phase of create/delete tag, deliberately separate from the already completed local mutation.</summary>
    public Task<GitResult> PushTagReferenceAsync(string remote, string name, bool delete = false, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await ValidateReferenceNameAsync(name, branch: false, token).ConfigureAwait(false);
            return await PushReferenceTagAsync(remote, name, delete, token).ConfigureAwait(false);
        }, cancellationToken);

    private Task<GitResult> PushReferenceTagAsync(string remote, string name, bool delete, CancellationToken token)
        => ExecuteAsync(["push", "--", ValidateValue(remote), (delete ? "" : "refs/tags/" + name) + ":refs/tags/" + name], token);

    private async Task ValidateReferenceNameAsync(string name, bool branch, CancellationToken token)
    {
        ValidateValue(name);
        // Full ref validation avoids checkout shorthand expansion such as @{-1}.
        GitResult check = await ExecuteAsync(["check-ref-format", "refs/" + (branch ? "heads/" : "tags/") + name], token, allowedExitCodes: [1]).ConfigureAwait(false);
        if (!check.Success) { throw new ArgumentException("Invalid " + (branch ? "branch" : "tag") + " name: " + name, nameof(name)); }
        if (branch && name == "HEAD") { throw new ArgumentException("HEAD is not a valid branch name.", nameof(name)); }
    }

    private static string[] ReferenceNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] selected = names.Select(ValidateValue).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) { throw new ArgumentException("Select at least one reference.", nameof(names)); }
        return selected;
    }
}
