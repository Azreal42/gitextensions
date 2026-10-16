namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<IReadOnlyList<WorktreeSnapshot>> GetWorktreeSnapshotsAsync(CancellationToken cancellationToken = default)
        => WithWorktreesAsync((info, token) => ReadWorktreeSnapshotsAsync(info, token), cancellationToken);

    private Task<T> WithWorktreesAsync<T>(Func<GitRepositoryInfo, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            GitRepositoryInfo info = await DiscoverRepositoryAsync(Path, _runner, token).ConfigureAwait(false);
            string common = GitRulesFiles.Resolve(info.CommonDirectory).Path;
            SemaphoreSlim shared = RemoteSettingsLocks.GetOrAdd(common, _ => new(1, 1));
            await shared.WaitAsync(token).ConfigureAwait(false);
            try { return await operation(info, token).ConfigureAwait(false); }
            finally { shared.Release(); }
        }, cancellationToken);

    private async Task<IReadOnlyList<WorktreeSnapshot>> ReadWorktreeSnapshotsAsync(GitRepositoryInfo source, CancellationToken token)
    {
        IReadOnlyList<GitWorktree> listed = await GetWorktreesAsync(token).ConfigureAwait(false);
        WorktreeNode common = await ReadWorktreeNodeAsync(source.CommonDirectory, token).ConfigureAwait(false);
        WorktreeNode current = await ReadWorktreeNodeAsync(source.RootPath, token).ConfigureAwait(false);
        List<WorktreeSnapshot> snapshots = [];
        for (int index = 0; index < listed.Count; index++)
        {
            GitWorktree tree = listed[index];
            WorktreeNode root = await ReadWorktreeNodeAsync(tree.Path, token).ConfigureAwait(false);
            bool isCurrent = SameWorktreeNode(root, current);
            WorktreeNode? marker = null, admin = null, backlink = null, commonLink = null, head = null;
            string? error = null;
            if (root.Identity is not null)
            {
                try
                {
                    if (!root.Identity.Value.IsDirectory) { throw new IOException("The worktree path is not a directory."); }
                    GitRepositoryInfo actual = await DiscoverRepositoryAsync(tree.Path, _runner, token).ConfigureAwait(false);
                    if (!SameWorktreeNode(common, await ReadWorktreeNodeAsync(actual.CommonDirectory, token).ConfigureAwait(false))
                        || !SameWorktreeNode(root, await ReadWorktreeNodeAsync(actual.RootPath, token).ConfigureAwait(false)) || actual.IsBare != tree.IsBare)
                    { throw new IOException("The directory belongs to a different Git repository or worktree."); }
                    admin = await ReadWorktreeNodeAsync(actual.GitDirectory, token).ConfigureAwait(false);
                    head = await ReadWorktreeNodeAsync(System.IO.Path.Combine(actual.GitDirectory, "HEAD"), token).ConfigureAwait(false);
                    if (!tree.IsBare) { marker = await ReadWorktreeNodeAsync(System.IO.Path.Combine(tree.Path, ".git"), token).ConfigureAwait(false); }
                    if (index != 0)
                    {
                        if (marker?.Identity?.IsRegular != true || admin.Identity?.IsDirectory != true || SameWorktreeNode(admin, common))
                        { throw new IOException("The linked worktree's .git registration was replaced or is invalid."); }
                        backlink = await ReadWorktreeNodeAsync(System.IO.Path.Combine(actual.GitDirectory, "gitdir"), token).ConfigureAwait(false);
                        commonLink = await ReadWorktreeNodeAsync(System.IO.Path.Combine(actual.GitDirectory, "commondir"), token).ConfigureAwait(false);
                        string pointer = ReadOutputLine(await File.ReadAllTextAsync(backlink.Path, token).ConfigureAwait(false));
                        string target = System.IO.Path.GetFullPath(pointer, actual.GitDirectory);
                        if (!SameWorktreeNode(marker, await ReadWorktreeNodeAsync(target, token).ConfigureAwait(false)))
                        { throw new IOException("The administrative backlink points to a different worktree."); }
                    }
                }
                catch (Exception exception) when (exception is IOException or GitCommandException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
                { error = exception.Message; }
            }
            else { error = "The worktree directory no longer exists. Prune its stale registration instead."; }
            snapshots.Add(new(tree, index == 0, isCurrent, new(root, common, marker, admin, backlink, commonLink, head), error));
        }
        return snapshots.AsReadOnly();
    }

    public Task<string> ValidateWorktreeOpenAsync(WorktreeSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithWorktreesAsync(async (info, token) =>
        {
            WorktreeSnapshot actual = await ValidateWorktreeSnapshotAsync(info, expected, token).ConfigureAwait(false);
            if (!actual.CanOpen) { throw new WorktreeOperationException(actual.ValidationError ?? "This worktree is already open or cannot be opened.", false); }
            return actual.Path;
        }, cancellationToken);
    }

    private async Task<WorktreeSnapshot> ValidateWorktreeSnapshotAsync(GitRepositoryInfo info, WorktreeSnapshot expected, CancellationToken token)
    {
        WorktreeSnapshot? current = (await ReadWorktreeSnapshotsAsync(info, token).ConfigureAwait(false))
            .SingleOrDefault(tree => tree.Path == expected.Path);
        if (current is null || current.Value != expected.Value || current.IsMain != expected.IsMain || current.IsCurrent != expected.IsCurrent)
        { throw new WorktreeOperationException("The selected worktree changed after it was selected. Refresh the list before continuing.", false); }
        CheckWorktreeBinding(expected.Binding, current.Binding);
        return current;
    }

    private static async Task<WorktreeNode> ReadWorktreeNodeAsync(string path, CancellationToken token)
    {
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
        string? hash = null;
        if (identity?.IsRegular == true)
        {
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw new IOException("The worktree file was replaced while reading it."); }
            using MemoryStream bytes = new(); await file.CopyToAsync(bytes, token).ConfigureAwait(false); hash = GitRulesFiles.Hash(bytes.ToArray());
        }
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        return new(path, location, identity, hash);
    }
    private static bool SameWorktreeNode(WorktreeNode first, WorktreeNode second)
        => first.Identity is not null && first.Identity == second.Identity;
    private static void CheckWorktreeBinding(WorktreeBinding first, WorktreeBinding second)
    {
        Check(first.Root, second.Root); Check(first.Common, second.Common); Check(first.Marker, second.Marker);
        Check(first.Admin, second.Admin); Check(first.Backlink, second.Backlink); Check(first.CommonLink, second.CommonLink); Check(first.Head, second.Head);
        static void Check(WorktreeNode? a, WorktreeNode? b)
        {
            if (a is null && b is null) { return; }
            if (a is null || b is null || a.Identity != b.Identity || a.ContentHash != b.ContentHash || a.Path != b.Path)
            { throw new WorktreeOperationException("The worktree directory or Git registration was replaced. Refresh the list before continuing.", false); }
            try { GitRulesFiles.CheckLocation(a.Location, b.Location); }
            catch (IOException exception) { throw new WorktreeOperationException(exception.Message, false, inner: exception); }
        }
    }
}
