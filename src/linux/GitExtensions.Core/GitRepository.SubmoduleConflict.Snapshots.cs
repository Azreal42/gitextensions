namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private async Task<GitSubmoduleConflictSnapshot> ReadSubmoduleConflictAsync(GitRepositoryInfo info, string path, CancellationToken token)
    {
        ValidateRemovalLocation(path);
        RemovalIndexEntry[] index = await ReadRemovalIndexAsync(token).ConfigureAwait(false);
        RemovalIndexEntry[] stages = index.Where(entry => entry.Path == path).ToArray();
        if (stages.Length == 0 || stages.Any(entry => entry.Mode != "160000" || entry.Stage is not ("1" or "2" or "3"))
            || stages.Select(entry => entry.Stage).Distinct(StringComparer.Ordinal).Count() != stages.Length)
        { throw new InvalidOperationException("The selected path is not an unresolved gitlink conflict. Refresh the conflicts list."); }

        List<WorktreeNode> source = [];
        foreach (string location in new[] { info.RootPath, info.CommonDirectory, info.GitDirectory,
            System.IO.Path.Combine(info.RootPath, ".git"), System.IO.Path.Combine(info.GitDirectory, "HEAD"),
            System.IO.Path.Combine(info.CommonDirectory, "config"), System.IO.Path.Combine(info.GitDirectory, "config.worktree") }.Distinct(StringComparer.Ordinal))
        { source.Add(await ReadWorktreeNodeAsync(location, token).ConfigureAwait(false)); }
        List<WorktreeNode> nodes = []; GitRepositoryInfo? child = null; string? current = null; string? error = null;
        try
        {
            string full = System.IO.Path.Combine(info.RootPath, path);
            nodes.Add(await ReadWorktreeNodeAsync(full, token).ConfigureAwait(false));
            if (nodes[0].Identity?.IsDirectory != true)
            { throw new InvalidOperationException("The submodule working tree is missing. Initialize its repository before resolving this gitlink; staging a deletion is not performed here."); }
            WorktreeNode marker = await ReadWorktreeNodeAsync(System.IO.Path.Combine(full, ".git"), token).ConfigureAwait(false); nodes.Add(marker);
            if (marker.Identity is null)
            { throw new InvalidOperationException("The submodule has no initialized Git working tree. The parent repository cannot be used in its place."); }
            child = await DiscoverRepositoryAsync(full, _runner, token).ConfigureAwait(false);
            WorktreeNode actualRoot = await ReadWorktreeNodeAsync(child.RootPath, token).ConfigureAwait(false);
            WorktreeNode common = await ReadWorktreeNodeAsync(child.CommonDirectory, token).ConfigureAwait(false);
            if (child.IsBare || !SameWorktreeNode(nodes[0], actualRoot)
                || SameWorktreeNode(common, source.Single(item => item.LogicalPath == info.CommonDirectory)))
            { throw new InvalidOperationException("The selected path does not contain its own distinct Git working tree."); }
            // Checkout's shared remote-branch flow may legitimately write tracking config.
            // Repository identity is guarded by its root, marker, administrative directory
            // and common-directory link; discovery independently revalidates core.worktree.
            foreach (string location in new[] { child.GitDirectory, child.CommonDirectory,
                System.IO.Path.Combine(child.GitDirectory, "commondir") }.Distinct(StringComparer.Ordinal))
            { nodes.Add(await ReadWorktreeNodeAsync(location, token).ConfigureAwait(false)); }
            current = await ReadSubmoduleConflictHeadAsync(child, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or GitCommandException)
        { error = exception.Message; child = null; }

        CheckSubmoduleNodes(source, await ReadSubmoduleNodesAsync(source.Select(node => node.LogicalPath), token).ConfigureAwait(false));
        CheckSubmoduleNodes(nodes, await ReadSubmoduleNodesAsync(nodes.Select(node => node.LogicalPath), token).ConfigureAwait(false));
        if (SubmoduleIndexSignature(index) != SubmoduleIndexSignature(await ReadRemovalIndexAsync(token).ConfigureAwait(false)))
        { throw new InvalidOperationException("The index changed while loading the gitlink conflict. Refresh before continuing."); }
        if (child is not null && current != await ReadSubmoduleConflictHeadAsync(child, token).ConfigureAwait(false))
        { throw new InvalidOperationException("The submodule checkout changed while loading. Refresh its current commit."); }
        return new(path, stages.SingleOrDefault(entry => entry.Stage == "1")?.Hash,
            stages.SingleOrDefault(entry => entry.Stage == "2")?.Hash, stages.SingleOrDefault(entry => entry.Stage == "3")?.Hash,
            current, error, new(info.RootPath, SubmoduleIndexSignature(index), source.ToArray(), nodes.ToArray(), child));
    }

    private async Task<string?> ReadSubmoduleConflictHeadAsync(GitRepositoryInfo child, CancellationToken token)
    {
        GitResult resolved = await _runner.RunAsync(child.RootPath, [.. GlobalOptions, "rev-parse", "--verify", "HEAD^{commit}"], token).ConfigureAwait(false);
        return resolved.Success ? ReadOutputLine(resolved.StandardOutput) : null;
    }

    private async Task<GitSubmoduleConflictSnapshot> ValidateSubmoduleConflictAsync(GitRepositoryInfo info,
        GitSubmoduleConflictSnapshot expected, bool refreshCurrent, CancellationToken token)
    {
        if (info.RootPath != expected.Binding.RepositoryRoot)
        { throw new ArgumentException("The selected gitlink conflict belongs to a different working tree.", nameof(expected)); }
        GitSubmoduleConflictSnapshot actual = await ReadSubmoduleConflictAsync(info, expected.Path, token).ConfigureAwait(false);
        if (actual.Binding.IndexSignature != expected.Binding.IndexSignature || actual.Base != expected.Base || actual.Local != expected.Local || actual.Remote != expected.Remote)
        { throw new InvalidOperationException("The conflict stages or index changed. Reopen the conflict before continuing."); }
        CheckSubmoduleNodes(expected.Binding.SourceNodes, actual.Binding.SourceNodes);
        CheckSubmoduleNodes(expected.Binding.ModuleNodes, actual.Binding.ModuleNodes);
        if (!refreshCurrent && actual.Current != expected.Current)
        { throw new InvalidOperationException("The submodule's current commit changed. Refresh Current before continuing."); }
        return actual;
    }
}
