namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Runs native submodule add, retaining native failures and partial effects. Cancellation terminates and drains Git; it does not roll back its changes.</summary>
    public Task<GitResult> AddSubmoduleAsync(GitSubmoduleAddOptions options, CancellationToken cancellationToken = default,
        Action<GitProcessOutput>? outputReceived = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSubmoduleSource(options.Source);
        string? branch = options.Branch?.Trim();
        if (branch?.Contains('\0') == true) { throw new ArgumentException("A branch cannot contain NUL.", nameof(options)); }
        return WithMutationAsync(async token =>
        {
            GitRepositoryInfo info = await DiscoverRepositoryAsync(Path, _runner, token).ConfigureAwait(false);
            if (info.IsBare) { throw new InvalidOperationException("Adding a submodule requires a working tree."); }
            string common = GitRulesFiles.Resolve(info.CommonDirectory).Path;
            SemaphoreSlim shared = RemoteSettingsLocks.GetOrAdd(common, _ => new(1, 1));
            await shared.WaitAsync(token).ConfigureAwait(false);
            try
            {
                SubmoduleAddBinding binding = await ReadSubmoduleAddBindingAsync(info, options.Path, token).ConfigureAwait(false);
                List<string> arguments = [.. GlobalOptions, "submodule", "add"];
                if (options.Force) { arguments.Add("-f"); }
                if (!string.IsNullOrEmpty(branch)) { arguments.AddRange(["-b", branch]); }
                arguments.AddRange(["--", options.Source, binding.RelativePath]);
                GitRepositoryInfo current = await DiscoverRepositoryAsync(Path, _runner, token).ConfigureAwait(false);
                SubmoduleAddBinding rechecked = await ReadSubmoduleAddBindingAsync(current, options.Path, token).ConfigureAwait(false);
                if (info != current || binding.RelativePath != rechecked.RelativePath || binding.Nodes.Count != rechecked.Nodes.Count)
                { throw new InvalidOperationException("The repository or submodule destination changed. Try again after refreshing."); }
                for (int index = 0; index < binding.Nodes.Count; index++)
                {
                    WorktreeNode first = binding.Nodes[index], second = rechecked.Nodes[index];
                    if (first.LogicalPath != second.LogicalPath || first.Identity != second.Identity || first.ContentHash != second.ContentHash)
                    { throw new InvalidOperationException("The repository or submodule destination was replaced. Try again after refreshing."); }
                    GitRulesFiles.CheckLocation(first.Location, second.Location);
                }
                token.ThrowIfCancellationRequested();
                // Git owns cloning, registration, gitlink/.gitmodules staging and its conventional locks.
                // Do not introduce clone + hand-written registration or destructive compensating cleanup.
                return await _runner.RunAsync(info.RootPath, arguments, token, outputReceived: outputReceived).ConfigureAwait(false);
            }
            finally { shared.Release(); }
        }, cancellationToken);
    }

    private async Task<SubmoduleAddBinding> ReadSubmoduleAddBindingAsync(GitRepositoryInfo info, string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0')) { throw new ArgumentException("A submodule path cannot contain NUL.", nameof(path)); }
        string full = System.IO.Path.GetFullPath(path, info.RootPath);
        string relative = System.IO.Path.GetRelativePath(info.RootPath, full);
        if (relative == "." || !SubmoduleAddContained(info.RootPath, full)
            || relative.Split(System.IO.Path.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase))
            || relative.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Choose a submodule directory inside the working tree, outside its root and Git metadata.", nameof(path)); }
        List<WorktreeNode> nodes = [];
        foreach (string node in new[] { info.RootPath, info.CommonDirectory, info.GitDirectory, System.IO.Path.Combine(info.RootPath, ".git") }.Distinct(StringComparer.Ordinal))
        { nodes.Add(await ReadWorktreeNodeAsync(node, token).ConfigureAwait(false)); }
        GitRulesLocation root = GitRulesFiles.Resolve(info.RootPath), target = GitRulesFiles.Resolve(full);
        if (!SubmoduleAddContained(root.Path, target.Path) || target.Parts.Any(part => part.Identity?.IsLink == true && SubmoduleAddContained(root.Path, part.Path)))
        { throw new ArgumentException("A submodule destination cannot traverse a symbolic link or leave the working tree.", nameof(path)); }
        foreach (string metadata in new[] { info.CommonDirectory, info.GitDirectory })
        {
            string actual = GitRulesFiles.Resolve(metadata).Path;
            if (target.Path == actual || SubmoduleAddContained(actual, target.Path))
            { throw new ArgumentException("A submodule destination cannot be inside Git metadata.", nameof(path)); }
        }
        // Record every existing parent and the first missing component. Recheck before Git
        // starts; native Git still provides the final checks for external concurrent changes.
        string cursor = info.RootPath;
        foreach (string part in relative.Split(System.IO.Path.DirectorySeparatorChar))
        {
            cursor = System.IO.Path.Combine(cursor, part);
            WorktreeNode node = await ReadWorktreeNodeAsync(cursor, token).ConfigureAwait(false); nodes.Add(node);
            if (node.Identity is null) { break; }
            if (node.Identity?.IsDirectory != true) { throw new ArgumentException("The submodule destination or its parent is not a directory.", nameof(path)); }
        }
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            GitRepositoryInfo existing;
            try { existing = await DiscoverRepositoryAsync(full, _runner, token).ConfigureAwait(false); }
            catch (GitCommandException exception) { throw new ArgumentException("The destination contains files and is not a Git repository.", nameof(path), exception); }
            WorktreeNode existingRoot = await ReadWorktreeNodeAsync(existing.RootPath, token).ConfigureAwait(false);
            WorktreeNode destination = await ReadWorktreeNodeAsync(full, token).ConfigureAwait(false);
            if (existing.IsBare || !SameWorktreeNode(existingRoot, destination)
                || SameWorktreeNode(await ReadWorktreeNodeAsync(existing.CommonDirectory, token).ConfigureAwait(false), nodes[1]))
            { throw new ArgumentException("The destination must be a distinct Git working tree rooted at the selected path.", nameof(path)); }
            foreach (string node in new[] { System.IO.Path.Combine(full, ".git"), existing.GitDirectory, existing.CommonDirectory,
                System.IO.Path.Combine(existing.GitDirectory, "HEAD") }.Distinct(StringComparer.Ordinal))
            { nodes.Add(await ReadWorktreeNodeAsync(node, token).ConfigureAwait(false)); }
        }
        return new(relative, nodes);
    }

    private static bool SubmoduleAddContained(string root, string path)
    {
        string relative = System.IO.Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !System.IO.Path.IsPathRooted(relative);
    }
    private static void ValidateSubmoduleSource(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (source.Contains('\0')) { throw new ArgumentException("A submodule source cannot contain NUL.", nameof(source)); }
    }
    private sealed record SubmoduleAddBinding(string RelativePath, IReadOnlyList<WorktreeNode> Nodes);
}
