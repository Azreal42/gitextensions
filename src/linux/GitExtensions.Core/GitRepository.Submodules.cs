namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<IReadOnlyList<GitSubmoduleSnapshot>> GetSubmoduleSnapshotsAsync(CancellationToken cancellationToken = default)
        => WithSubmodulesAsync((repository, info, token) => repository.ReadSubmoduleSnapshotsAsync(info, token), cancellationToken);

    /// <summary>Updates only the selected gitlink and its descendants. Git retains its normal dirty-checkout and update-strategy checks.</summary>
    public Task<GitResult> UpdateSubmoduleAsync(GitSubmoduleSnapshot selected, CancellationToken cancellationToken = default,
        Action<GitProcessOutput>? outputReceived = null)
        => RunSelectedSubmoduleAsync(selected, update: true, cancellationToken, outputReceived);

    /// <summary>Synchronizes only the selected module's URL. Windows FormSubmodules does not request recursive synchronization.</summary>
    public Task<GitResult> SyncSubmoduleAsync(GitSubmoduleSnapshot selected, CancellationToken cancellationToken = default,
        Action<GitProcessOutput>? outputReceived = null)
        => RunSelectedSubmoduleAsync(selected, update: false, cancellationToken, outputReceived);

    public async Task<string> ValidateSubmoduleOpenAsync(GitSubmoduleSnapshot selected, CancellationToken cancellationToken = default)
        => (await OpenSubmoduleRepositoryAsync(selected, cancellationToken).ConfigureAwait(false)).Path;

    /// <summary>Opens an exact, revalidated module root while preserving the caller's Git runner and authentication behavior.</summary>
    public Task<GitRepository> OpenSubmoduleRepositoryAsync(GitSubmoduleSnapshot selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return WithSubmodulesAsync(async (repository, info, token) =>
        {
            GitSubmoduleSnapshot current = await repository.ValidateSubmoduleSnapshotAsync(info, selected, token).ConfigureAwait(false);
            if (!current.CanOpen || current.Binding.ModuleInfo is not GitRepositoryInfo module)
            { throw new InvalidOperationException(current.ValidationError ?? "Initialize this submodule before opening its repository."); }
            return new GitRepository(module.RootPath, _runner) { Info = module };
        }, cancellationToken);
    }

    private Task<GitResult> RunSelectedSubmoduleAsync(GitSubmoduleSnapshot selected, bool update, CancellationToken cancellationToken,
        Action<GitProcessOutput>? outputReceived)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return WithSubmodulesAsync(async (repository, info, token) =>
        {
            GitSubmoduleSnapshot current = await repository.ValidateSubmoduleSnapshotAsync(info, selected, token).ConfigureAwait(false);
            if (!(update ? current.CanUpdate : current.CanSync))
            { throw new InvalidOperationException(current.ValidationError ?? "The selected submodule cannot be operated on safely."); }
            string[] arguments = update ? ["submodule", "update", "--init", "--recursive", "--", current.Path]
                : ["submodule", "sync", "--", current.Path];
            token.ThrowIfCancellationRequested();
            // No --force, deinit, recursive removal or compensating rollback. Git's actual
            // result/output and any effects preceding failure/cancellation remain observable.
            return await _runner.RunAsync(info.RootPath, [.. GlobalOptions, "--literal-pathspecs", .. arguments], token,
                outputReceived: outputReceived).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task<T> WithSubmodulesAsync<T>(Func<GitRepository, GitRepositoryInfo, CancellationToken, Task<T>> action, CancellationToken token)
        => WithWorktreesAsync(async (info, cancellation) =>
        {
            if (info.IsBare) { throw new InvalidOperationException("Managing submodules requires a working tree."); }
            GitRepository repository = Path == info.RootPath ? this : new GitRepository(info.RootPath, _runner) { Info = info };
            return await action(repository, info, cancellation).ConfigureAwait(false);
        }, token);

    private async Task<GitSubmoduleSnapshot> ValidateSubmoduleSnapshotAsync(GitRepositoryInfo info, GitSubmoduleSnapshot expected, CancellationToken token)
    {
        if (expected.Binding.RepositoryRoot != info.RootPath)
        { throw new ArgumentException("The selected submodule belongs to another working tree.", nameof(expected)); }
        GitSubmoduleSnapshot? current = (await ReadSubmoduleSnapshotsAsync(info, token).ConfigureAwait(false))
            .SingleOrDefault(item => item.Name == expected.Name && item.Path == expected.Path);
        if (current is null || current.Binding.IndexSignature != expected.Binding.IndexSignature
            || current.Binding.ConfigurationRevision != expected.Binding.ConfigurationRevision || current.Head != expected.Head
            || current.Code != expected.Code)
        { throw new InvalidOperationException("The submodule registration, indexed commit or repository changed. Refresh the list before continuing."); }
        CheckSubmoduleNodes(expected.Binding.SourceNodes, current.Binding.SourceNodes);
        CheckSubmoduleNodes(expected.Binding.ModuleNodes, current.Binding.ModuleNodes);
        return current;
    }

    private static void CheckSubmoduleNodes(IReadOnlyList<WorktreeNode> expected, IReadOnlyList<WorktreeNode> current)
    {
        if (expected.Count != current.Count) { throw new InvalidOperationException("The submodule's working tree or Git registration was replaced. Refresh the list."); }
        for (int index = 0; index < expected.Count; index++)
        {
            WorktreeNode a = expected[index], b = current[index];
            if (a.LogicalPath != b.LogicalPath || a.Identity != b.Identity || a.ContentHash != b.ContentHash)
            { throw new InvalidOperationException("The submodule's working tree or Git registration was replaced. Refresh the list."); }
            GitRulesFiles.CheckLocation(a.Location, b.Location);
        }
    }
}
