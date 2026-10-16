namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<GitSubmoduleConflictSnapshot> GetSubmoduleConflictAsync(string path, CancellationToken cancellationToken = default)
        => WithSubmodulesAsync((repository, info, token) => repository.ReadSubmoduleConflictAsync(info, path, token), cancellationToken);

    /// <summary>Refreshes only Current. The original conflict stages, index and repository identities must still match.</summary>
    public Task<GitSubmoduleConflictSnapshot> RefreshSubmoduleConflictAsync(GitSubmoduleConflictSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithSubmodulesAsync((repository, info, token) => repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: true, token), cancellationToken);
    }

    public Task<GitRepository> OpenSubmoduleConflictRepositoryAsync(GitSubmoduleConflictSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithSubmodulesAsync(async (repository, info, token) =>
        {
            GitSubmoduleConflictSnapshot current = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
            return repository.SubmoduleConflictChild(current);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<GitReference>> GetSubmoduleConflictBranchesAsync(GitSubmoduleConflictSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithSubmodulesAsync(async (repository, info, token) =>
        {
            GitSubmoduleConflictSnapshot current = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
            return await repository.ReadSubmoduleConflictBranchesAsync(current, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Uses the shared checkout engine after revalidating the exact containing branch and all selected conflict guards.</summary>
    public Task<GitCheckoutResult> CheckoutSubmoduleConflictBranchAsync(GitSubmoduleConflictSnapshot expected, GitReference branch,
        GitCheckoutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(branch); ArgumentNullException.ThrowIfNull(options);
        if (options.Revision != branch.Name || branch.SymbolicTarget is not null
            || !(branch.IsBranch && options.Kind == GitCheckoutKind.LocalBranch || branch.IsRemote && options.Kind == GitCheckoutKind.RemoteBranch))
        { throw new ArgumentException("Checkout must target the exact selected local or remote containing branch.", nameof(options)); }
        return WithSubmodulesAsync(async (repository, info, token) =>
        {
            GitSubmoduleConflictSnapshot current = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
            GitRepository child = repository.SubmoduleConflictChild(current);
            SemaphoreSlim shared = SubmoduleConflictLock(current);
            await shared.WaitAsync(token).ConfigureAwait(false);
            try
            {
                current = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
                GitReference? target = (await repository.ReadSubmoduleConflictBranchesAsync(current, token).ConfigureAwait(false))
                    .SingleOrDefault(reference => reference.Name == branch.Name && reference.Hash == branch.Hash);
                if (target is null) { throw new InvalidOperationException("The selected branch changed or no longer contains both conflict sides. Reload the containing branches."); }
                _ = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                GitCheckoutResult result = await child.CheckoutAsync(options, token).ConfigureAwait(false);
                if (await repository.ReadSubmoduleConflictHeadAsync(current.Binding.ModuleInfo!, token).ConfigureAwait(false) != branch.Hash)
                { throw new InvalidOperationException("The branch moved during checkout. Git's checkout effects were retained; refresh Current before staging."); }
                return result;
            }
            finally { shared.Release(); }
        }, cancellationToken);
    }

    /// <summary>Runs one native literal add and verifies the resulting gitlink. Never commits or continues the parent operation.</summary>
    public Task<GitSubmoduleConflictStageResult> StageSubmoduleConflictCurrentAsync(GitSubmoduleConflictSnapshot expected,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithSubmodulesAsync(async (repository, info, token) =>
        {
            GitSubmoduleConflictSnapshot current = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
            _ = repository.SubmoduleConflictChild(current);
            if (!current.CanStageCurrent) { throw new InvalidOperationException(current.ValidationError ?? "Check out a valid child commit before staging."); }
            SemaphoreSlim shared = SubmoduleConflictLock(current);
            await shared.WaitAsync(token).ConfigureAwait(false);
            try
            {
                current = await repository.ValidateSubmoduleConflictAsync(info, expected, refreshCurrent: false, token).ConfigureAwait(false);
                if (!current.CanStageCurrent) { throw new InvalidOperationException(current.ValidationError ?? "The child commit is no longer available."); }
                token.ThrowIfCancellationRequested();
                GitResult result = await _runner.RunAsync(info.RootPath, [.. GlobalOptions, "--literal-pathspecs", "add", "--", current.Path], token,
                    outputReceived: outputReceived).ConfigureAwait(false);
                RemovalIndexEntry[] after = (await repository.ReadRemovalIndexAsync(token).ConfigureAwait(false)).Where(entry => entry.Path == current.Path).ToArray();
                string? staged = after.Length == 1 && after[0].Mode == "160000" && after[0].Stage == "0" ? after[0].Hash : null;
                bool resolved = result.Success && staged == current.Current;
                return new GitSubmoduleConflictStageResult(result, resolved, staged, resolved ? null
                    : "Git did not leave the confirmed current commit as one resolved stage-0 gitlink. Refresh the conflict; any native effects were retained.");
            }
            finally { shared.Release(); }
        }, cancellationToken);
    }

    private GitRepository SubmoduleConflictChild(GitSubmoduleConflictSnapshot current)
    {
        if (!current.CanOpen || current.Binding.ModuleInfo is not GitRepositoryInfo child)
        { throw new InvalidOperationException(current.ValidationError ?? "The selected path has no initialized distinct child repository."); }
        return new GitRepository(child.RootPath, _runner) { Info = child };
    }

    private static SemaphoreSlim SubmoduleConflictLock(GitSubmoduleConflictSnapshot current)
        => RemoteSettingsLocks.GetOrAdd(GitRulesFiles.Resolve(current.Binding.ModuleInfo!.CommonDirectory).Path, _ => new(1, 1));

    private async Task<IReadOnlyList<GitReference>> ReadSubmoduleConflictBranchesAsync(GitSubmoduleConflictSnapshot current, CancellationToken token)
    {
        GitRepository child = SubmoduleConflictChild(current);
        if (!current.CanCheckoutBranch) { return []; }
        Dictionary<string, string> local = await ContainsAsync(current.Local!).ConfigureAwait(false);
        Dictionary<string, string> remote = await ContainsAsync(current.Remote!).ConfigureAwait(false);
        return (await child.GetReferencesAsync(token).ConfigureAwait(false)).Where(reference => (reference.IsBranch || reference.IsRemote)
            && reference.SymbolicTarget is null && local.TryGetValue(reference.Name, out string? first) && first == reference.Hash
            && remote.TryGetValue(reference.Name, out string? second) && second == reference.Hash).ToArray();

        async Task<Dictionary<string, string>> ContainsAsync(string hash)
        {
            GitResult contains = await child.ExecuteAsync(["for-each-ref", "--contains=" + hash, "--format=%(refname)%00%(objectname)", "refs/heads/", "refs/remotes/"], token).ConfigureAwait(false);
            return contains.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\0'))
                .ToDictionary(fields => fields[0], fields => fields[1], StringComparer.Ordinal);
        }
    }
}
