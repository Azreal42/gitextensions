namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Finds a registered actual parent, including old-form modules and linked parent worktrees. Never discovers a parent as the child fallback.</summary>
    public async Task<GitSuperprojectSnapshot?> GetSuperprojectSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable execution = _runner.BeginOperation();
        GitRepositoryInfo child = await DiscoverRepositoryAsync(Path, _runner, cancellationToken).ConfigureAwait(false);
        if (child.IsBare) { return null; }
        GitRepository source = new(child.RootPath, _runner);
        // FormCommit/GitModule use actual ancestor registrations, not an arbitrary containing repository.
        // This also works when .git is a directory, where Git's superproject hint can be empty.
        for (DirectoryInfo? ancestor = Directory.GetParent(child.RootPath); ancestor is not null; ancestor = ancestor.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string modules = System.IO.Path.Combine(ancestor.FullName, ".gitmodules");
            string marker = System.IO.Path.Combine(ancestor.FullName, ".git");
            if (GitRulesIdentity.ReadPath(modules) is null || GitRulesIdentity.ReadPath(marker) is null) { continue; }
            string candidateRoot = ancestor.FullName;
            // Read explicit-file config from the known child cwd before opening a possibly invalid containing parent.
            (string? registration, _) = await source.ReadSuperprojectRegistrationAsync(modules,
                System.IO.Path.GetRelativePath(candidateRoot, child.RootPath), cancellationToken).ConfigureAwait(false);
            if (registration is null) { continue; }
            GitRepository parent = new(candidateRoot, _runner);
            GitSuperprojectSnapshot? snapshot = await parent.WithSubmodulesAsync((repository, info, token) =>
            {
                if (info.RootPath != candidateRoot) { return Task.FromResult<GitSuperprojectSnapshot?>(null); }
                return repository.ReadSuperprojectAssociationAsync(info, child, token);
            }, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null) { return snapshot; }
        }
        return null;
    }

    /// <summary>After a successful child commit/push, stage only its current gitlink using Windows' update-index --add operation.</summary>
    public async Task<GitSuperprojectStageResult> StageInSuperprojectAsync(GitSuperprojectSnapshot expected,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the snapshot's injected executable/environment, including the parent's selected index.
        GitRunner runner = expected.Binding.Environment.CreateRunner();
        using IDisposable execution = runner.BeginOperation();
        GitRepositoryInfo child = await DiscoverRepositoryAsync(Path, runner, cancellationToken).ConfigureAwait(false);
        if (child.RootPath != expected.ChildRoot)
        { throw new ArgumentException("The superproject association belongs to another child working tree.", nameof(expected)); }
        GitRepository parent = new(expected.SuperprojectRoot, runner);
        return await parent.WithSubmodulesAsync<GitSuperprojectStageResult>(async (repository, info, token) =>
        {
            GitSuperprojectSnapshot current = await repository.RequireSuperprojectAssociationAsync(info, child, expected, true, token).ConfigureAwait(false);
            string head = await repository.ReadSuperprojectChildHeadAsync(child, token).ConfigureAwait(false);
            // Do not compare an old child HEAD from before commit/rebase. Revalidate the relation and selected entry immediately before the native effect.
            _ = await repository.RequireSuperprojectAssociationAsync(info, child, current, true, token).ConfigureAwait(false);
            if (head != await repository.ReadSuperprojectChildHeadAsync(child, token).ConfigureAwait(false))
            { throw new InvalidOperationException("The child HEAD changed while preparing superproject staging. Refresh before continuing."); }
            token.ThrowIfCancellationRequested();
            GitResult result = await runner.RunAsync(info.RootPath,
                [.. GlobalOptions, "--literal-pathspecs", "update-index", "--add", "--", expected.SubmodulePath], token,
                outputReceived: outputReceived).ConfigureAwait(false);
            string? staged = null;
            try
            {
                RemovalIndexEntry[] entries = (await repository.ReadRemovalIndexAsync(token).ConfigureAwait(false))
                    .Where(entry => entry.Path == expected.SubmodulePath).ToArray();
                staged = entries.Length == 1 && entries[0].Mode == "160000" && entries[0].Stage == "0" ? entries[0].Hash : null;
                GitRepositoryInfo actualChild = await DiscoverRepositoryAsync(child.RootPath, runner, token).ConfigureAwait(false);
                GitSuperprojectSnapshot observed = await repository.RequireSuperprojectAssociationAsync(info, actualChild, expected, false, token).ConfigureAwait(false);
                bool verified = result.Success && staged == head && observed.IndexHead == head
                    && head == await repository.ReadSuperprojectChildHeadAsync(actualChild, token).ConfigureAwait(false);
                string? error = !result.Success ? SuperprojectNativeError(result) : verified ? null
                    : "Git did not leave the final child HEAD as one stage-0 gitlink. Refresh; any native effects were retained.";
                return new(result, verified, head, staged, error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or GitCommandException)
            {
                return new(result, false, head, staged, (!result.Success ? SuperprojectNativeError(result) + "\n" : "")
                    + "Superproject staging readback failed; any native effects were retained. " + error.Message);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitSuperprojectSnapshot> RequireSuperprojectAssociationAsync(GitRepositoryInfo parent, GitRepositoryInfo child,
        GitSuperprojectSnapshot expected, bool checkIndex, CancellationToken token)
    {
        GitSuperprojectSnapshot? current = await ReadSuperprojectAssociationAsync(parent, child, token).ConfigureAwait(false);
        if (current is null || current.ChildRoot != expected.ChildRoot || current.SuperprojectRoot != expected.SuperprojectRoot
            || current.SubmodulePath != expected.SubmodulePath || current.Binding.Child != expected.Binding.Child
            || current.Binding.Parent != expected.Binding.Parent || current.Binding.Registration != expected.Binding.Registration
            || checkIndex && current.Binding.IndexSignature != expected.Binding.IndexSignature)
        { throw new InvalidOperationException("The child registration, working tree or selected superproject gitlink changed. Refresh before staging."); }
        CheckSubmoduleNodes(expected.Binding.Nodes, current.Binding.Nodes);
        return current;
    }

    private async Task<GitSuperprojectSnapshot?> ReadSuperprojectAssociationAsync(GitRepositoryInfo parent, GitRepositoryInfo child, CancellationToken token)
    {
        string relative = System.IO.Path.GetRelativePath(parent.RootPath, child.RootPath);
        if (child.IsBare || relative == "." || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(relative)) { return null; }
        ValidateRemovalLocation(relative);
        string modulesPath = System.IO.Path.Combine(parent.RootPath, ".gitmodules");
        List<WorktreeNode> nodes = await ReadSuperprojectNodesAsync(parent, child, token).ConfigureAwait(false);
        (string? registration, WorktreeNode modules) = await ReadSuperprojectRegistrationAsync(modulesPath, relative, token).ConfigureAwait(false);
        if (registration is null) { return null; }
        string marker = System.IO.Path.Combine(child.RootPath, ".git");
        if (!File.Exists(marker) && !Directory.Exists(marker)) { return null; }
        GitRepositoryInfo actual = await DiscoverRepositoryAsync(child.RootPath, _runner, token).ConfigureAwait(false);
        if (actual != child || actual.IsBare || SameWorktreeNode(nodes.Single(node => node.LogicalPath == parent.CommonDirectory),
            await ReadWorktreeNodeAsync(actual.CommonDirectory, token).ConfigureAwait(false))) { return null; }
        RemovalIndexEntry[] index = await ReadRemovalIndexAsync(token).ConfigureAwait(false);
        RemovalIndexEntry[] selected = index.Where(entry => entry.Path == relative).ToArray();
        if (selected.Any(entry => entry.Mode != "160000") || index.Any(entry => entry.Path.StartsWith(relative + "/", StringComparison.Ordinal)))
        { throw new InvalidOperationException("The registered child conflicts with ordinary indexed files; only a gitlink may be staged."); }
        string signature = SubmoduleIndexSignature(selected);
        // Across a commit/push only the association and selected entry are stale guards. Other parent index entries and unrelated config may evolve.
        CheckSubmoduleNodes(nodes, await ReadSuperprojectNodesAsync(parent, child, token).ConfigureAwait(false));
        CheckSubmoduleNodes([modules], [await ReadWorktreeNodeAsync(modulesPath, token).ConfigureAwait(false)]);
        if (signature != SubmoduleIndexSignature((await ReadRemovalIndexAsync(token).ConfigureAwait(false)).Where(entry => entry.Path == relative)))
        { throw new InvalidOperationException("The selected parent gitlink changed while its association was read."); }
        string? indexed = selected.Length == 1 && selected[0].Stage == "0" ? selected[0].Hash : null;
        return new(child.RootPath, parent.RootPath, relative, indexed,
            new(child, parent, registration, signature, nodes.AsReadOnly(), _runner.ExecutionEnvironment));
    }

    private async Task<(string? Registration, WorktreeNode Modules)> ReadSuperprojectRegistrationAsync(string modulesPath, string relative, CancellationToken token)
    {
        WorktreeNode modules = await ReadWorktreeNodeAsync(modulesPath, token).ConfigureAwait(false);
        if (modules.Identity is null) { return (null, modules); }
        if (modules.Identity?.IsRegular != true || new FileInfo(modulesPath).LinkTarget is not null)
        { throw new InvalidOperationException("The superproject registration must be a regular .gitmodules file."); }
        GitBinaryResult keys = await RemovalCommandAsync(["config", "--no-includes", "--file", modulesPath, "--null", "--name-only", "--get-regexp", "^submodule\\..*\\.path$"], token, allowNoMatches: true).ConfigureAwait(false);
        List<string> registrations = [];
        foreach (string key in RemovalNullStrings(keys.StandardOutput).Distinct(StringComparer.Ordinal))
        {
            string[] paths = RemovalNullStrings((await RemovalCommandAsync(["config", "--no-includes", "--file", modulesPath, "--null", "--get-all", key], token).ConfigureAwait(false)).StandardOutput);
            if (!paths.Contains(relative, StringComparer.Ordinal)) { continue; }
            if (paths.Length != 1) { throw new InvalidOperationException("The child submodule registration contains multiple paths."); }
            registrations.Add(key);
        }
        if (registrations.Count > 1) { throw new InvalidOperationException("Multiple submodule registrations refer to this child."); }
        CheckSubmoduleNodes([modules], [await ReadWorktreeNodeAsync(modulesPath, token).ConfigureAwait(false)]);
        return (registrations.SingleOrDefault(), modules);
    }

    private static async Task<List<WorktreeNode>> ReadSuperprojectNodesAsync(GitRepositoryInfo parent, GitRepositoryInfo child, CancellationToken token)
    {
        List<WorktreeNode> nodes = [];
        foreach (string path in new[] { parent.RootPath, parent.GitDirectory, parent.CommonDirectory,
            System.IO.Path.Combine(parent.RootPath, ".git"), System.IO.Path.Combine(parent.GitDirectory, "gitdir"), System.IO.Path.Combine(parent.GitDirectory, "commondir"),
            child.RootPath, child.GitDirectory, child.CommonDirectory, System.IO.Path.Combine(child.RootPath, ".git"),
            System.IO.Path.Combine(child.GitDirectory, "gitdir"), System.IO.Path.Combine(child.GitDirectory, "commondir") }.Distinct(StringComparer.Ordinal))
        { nodes.Add(await ReadWorktreeNodeAsync(path, token).ConfigureAwait(false)); }
        return nodes;
    }

    private async Task<string> ReadSuperprojectChildHeadAsync(GitRepositoryInfo child, CancellationToken token)
    {
        string[] arguments = [.. GlobalOptions, "rev-parse", "--verify", "HEAD^{commit}"];
        GitResult result = await _runner.RunAsync(child.RootPath, arguments, token).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, result); }
        string head = ReadOutputLine(result.StandardOutput);
        if (head.Length is not (40 or 64) || !head.All(Uri.IsHexDigit))
        { throw new InvalidOperationException("Git did not return the actual child commit for superproject staging."); }
        return head;
    }

    private static string SuperprojectNativeError(GitResult result) => !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError
        : !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput : "Native superproject staging failed (" + result.ExitCode + ").";
}
