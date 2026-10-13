namespace GitExtensions.Linux;

public sealed record GitResetOtherBranchOptions(string Branch, string Revision, bool Force = false);
public sealed record GitResetBranchCandidate(GitReference Reference, bool IsFastForward);
public sealed record GitResetOtherBranchPreview(GitCommit Commit, string? CurrentBranch,
    IReadOnlyList<GitResetBranchCandidate> Branches, string? SuggestedBranch);

public sealed partial class GitRepository
{
    /// <summary>Local branches other than the checked-out branch and branches already at the destination.</summary>
    public async Task<GitResetOtherBranchPreview> GetResetOtherBranchPreviewAsync(string revision, CancellationToken cancellationToken = default)
    {
        string hash = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        GitCommit commit = (await GetLogAsync(1, hash, cancellationToken, all: false).ConfigureAwait(false)).Single();
        GitStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GitReference> references = await GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        GitReference[] remote = references.Where(reference => reference.IsRemote && reference.Hash == hash && reference.SymbolicTarget is null).ToArray();
        List<GitResetBranchCandidate> branches = [];
        foreach (GitReference reference in references.Where(reference => reference.IsBranch && !reference.IsCurrent && reference.Hash != hash)
            .OrderByDescending(reference => remote.Any(remoteRef => reference.Upstream == remoteRef.ShortName))
            .ThenByDescending(reference => remote.Any(remoteRef => reference.ShortName == remoteRef.ShortName[(remoteRef.ShortName.IndexOf('/') + 1)..]))
            .ThenBy(reference => reference.ShortName, StringComparer.Ordinal))
        {
            GitResult ancestor = await ExecuteAsync(["merge-base", "--is-ancestor", reference.Hash, hash], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
            branches.Add(new(reference, ancestor.Success));
        }
        GitResetBranchCandidate[] matching = remote.Length == 1
            ? branches.Where(branch => branch.Reference.Upstream == remote[0].ShortName
                || branch.Reference.ShortName == remote[0].ShortName[(remote[0].ShortName.IndexOf('/') + 1)..]).ToArray() : [];
        return new(commit, status.Branch, branches, matching.Length == 1 ? matching[0].Reference.ShortName : null);
    }

    /// <summary>Move exactly one existing local branch. No checkout, worktree or index is changed.</summary>
    public Task<GitResult> ResetOtherBranchAsync(GitResetOtherBranchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WithMutationAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            await ValidateReferenceNameAsync(options.Branch, branch: true, token).ConfigureAwait(false);
            string name = "refs/heads/" + options.Branch;
            GitReference reference = (await GetReferencesAsync(token).ConfigureAwait(false))
                .SingleOrDefault(reference => reference.Name == name && reference.SymbolicTarget is null)
                ?? throw new ArgumentException("Select an existing local branch.", nameof(options));
            if (reference.IsCurrent) { throw new InvalidOperationException("Use reset current branch for the checked-out branch."); }
            if ((await GetWorktreesAsync(token).ConfigureAwait(false)).Any(tree => tree.Branch == name && !tree.IsBare))
            { throw new InvalidOperationException("The branch is checked out in a worktree: " + options.Branch); }
            string hash = await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false);
            if (!options.Force)
            {
                GitResult ancestor = await ExecuteAsync(["merge-base", "--is-ancestor", reference.Hash, hash], token, allowedExitCodes: [1]).ConfigureAwait(false);
                if (!ancestor.Success) { throw new InvalidOperationException("A non-fast-forward reset requires Force reset."); }
            }
            // Compare-and-swap protects a branch changed externally after validation.
            return await ExecuteAsync(["update-ref", name, hash, reference.Hash], token).ConfigureAwait(false);
        }, cancellationToken);
    }
}
