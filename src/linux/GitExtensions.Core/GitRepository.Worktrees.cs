namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<WorktreeOperationResult> CreateWorktreeAsync(WorktreeCreateOptions options, CancellationToken cancellationToken = default,
        Action<GitProcessOutput>? outputReceived = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        if (options.Path.Contains('\0')) { throw new ArgumentException("A worktree path cannot contain NUL.", nameof(options)); }
        return WithWorktreesAsync<WorktreeOperationResult>(async (info, token) =>
        {
            IReadOnlyList<WorktreeSnapshot> before = await ReadWorktreeSnapshotsAsync(info, token).ConfigureAwait(false);
            WorktreeSnapshot source = before.SingleOrDefault(tree => tree.IsCurrent && tree.ValidationError is null)
                ?? throw new WorktreeOperationException("The source repository's worktree registration is invalid.", false);
            string target = System.IO.Path.GetFullPath(options.Path, info.RootPath);
            WorktreeNode destination = await ReadWorktreeNodeAsync(target, token).ConfigureAwait(false);
            string common = source.Binding.Common.Path;
            if (WorktreeContains(common, destination.Path) || before.Any(tree => SameWorktreeNode(tree.Binding.Root, destination)))
            { throw new WorktreeOperationException("Choose an empty directory outside Git's metadata and existing worktree roots.", false); }
            ValidateWorktreeDestination(destination);
            string revision = ValidateValue(options.Revision);
            if (options.NewBranch is not null)
            {
                await ValidateReferenceNameAsync(options.NewBranch, branch: true, token).ConfigureAwait(false);
                if ((await GetReferencesAsync(token).ConfigureAwait(false)).Any(reference => reference.Name == "refs/heads/" + options.NewBranch))
                { throw new WorktreeOperationException("The new branch already exists. Select it as an existing branch instead.", false); }
            }
            else if (revision.StartsWith("refs/heads/", StringComparison.Ordinal)) { revision = revision[11..]; }
            // Native -b refuses races; -B would reset an existing branch and is never used.
            if (options.NewBranch is null && before.Any(tree => !tree.IsBare && tree.Branch == "refs/heads/" + revision))
            { throw new WorktreeOperationException("This branch is already checked out in another worktree.", false); }
            GitResult configured = await ExecuteAsync(["config", "--bool", "--get", "worktree.useRelativePaths"], token, allowedExitCodes: [1]).ConfigureAwait(false);
            bool requestRelative = configured.ExitCode == 1 || ReadOutputLine(configured.StandardOutput) == "true";
            bool supportsRelative = await SupportsRelativeWorktreesAsync(token).ConfigureAwait(false);
            bool extensionBefore = await RelativeWorktreeExtensionAsync(token).ConfigureAwait(false);
            List<string> arguments = [];
            if (configured.ExitCode == 1) { arguments.AddRange(["-c", "worktree.useRelativePaths=true"]); }
            arguments.AddRange(["worktree", "add"]);
            if (options.NewBranch is not null) { arguments.AddRange(["-b", options.NewBranch]); }
            arguments.AddRange(["--", target]);
            // An omitted HEAD lets native Git create an unborn worktree where its version supports that operation.
            if (revision != "HEAD" || await HasHeadAsync(token).ConfigureAwait(false)) { arguments.Add(revision); }
            await ValidateWorktreeSnapshotAsync(info, source, token).ConfigureAwait(false);
            WorktreeNode immediate = await ReadWorktreeNodeAsync(target, token).ConfigureAwait(false);
            CheckWorktreeDestination(destination, immediate); ValidateWorktreeDestination(immediate);
            token.ThrowIfCancellationRequested();
            GitResult result;
            try { result = await _runner.RunAsync(Path, [.. GlobalOptions, .. arguments], token, outputReceived: outputReceived).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { throw new WorktreeOperationException("Worktree creation was interrupted. Git may already have created its branch, directory or registration; refresh before continuing.", true, inner: exception); }
            try
            {
                IReadOnlyList<WorktreeSnapshot> after = await ReadWorktreeSnapshotsAsync(info, CancellationToken.None).ConfigureAwait(false);
                WorktreeSnapshot? created = after.FirstOrDefault(tree => tree.Binding.Root.Path == destination.Path);
                bool relative = false;
                if (created?.Binding.Marker?.Identity?.IsRegular == true && supportsRelative && requestRelative)
                {
                    string marker = ReadOutputLine(await File.ReadAllTextAsync(created.Binding.Marker.Path).ConfigureAwait(false));
                    relative = marker.StartsWith("gitdir: ", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(marker[8..]);
                }
                return new(result, created, relative, !extensionBefore && await RelativeWorktreeExtensionAsync(CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or GitCommandException or UnauthorizedAccessException or InvalidOperationException)
            { throw new WorktreeOperationException("Git finished creating the worktree, but its resulting state could not be read. Refresh the repository.", true, result, exception); }
        }, cancellationToken);
    }

    public Task<WorktreeOperationResult> DeleteWorktreeAsync(WorktreeSnapshot expected, bool discardChanges = false, bool overrideLock = false,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithWorktreesAsync<WorktreeOperationResult>(async (info, token) =>
        {
            WorktreeSnapshot actual = await ValidateWorktreeSnapshotAsync(info, expected, token).ConfigureAwait(false);
            if (!actual.CanRemove)
            { throw new WorktreeOperationException(actual.ValidationError ?? "The main, bare or currently open worktree cannot be removed.", false); }
            if (actual.IsLocked && !overrideLock)
            { throw new WorktreeOperationException("This worktree is locked. Explicitly confirm overriding its lock before removing it.", false); }
            if (actual.IsLocked && !discardChanges)
            { throw new WorktreeOperationException("Git requires double force to remove a locked worktree, and double force can discard changes without an atomic cleanliness check. Explicitly confirm both overriding the lock and discarding changes before removal.", false); }
            IReadOnlyList<WorktreeSnapshot> all = await ReadWorktreeSnapshotsAsync(info, token).ConfigureAwait(false);
            if (WorktreeContains(actual.Binding.Root.Path, actual.Binding.Common.Path)
                || all.Any(tree => tree.Path != actual.Path && WorktreeContains(actual.Binding.Root.Path, tree.Binding.Root.Path)))
            { throw new WorktreeOperationException("This worktree contains another registered worktree or the repository's shared metadata.", false); }
            await ValidateWorktreeSnapshotAsync(info, expected, token).ConfigureAwait(false);
            List<string> arguments = ["worktree", "remove"];
            if (discardChanges || actual.IsLocked && overrideLock) { arguments.Add("--force"); }
            if (actual.IsLocked && overrideLock) { arguments.Add("--force"); }
            arguments.AddRange(["--", actual.Path]); token.ThrowIfCancellationRequested();
            GitResult result;
            try { result = await _runner.RunAsync(Path, [.. GlobalOptions, .. arguments], token, outputReceived: outputReceived).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { throw new WorktreeOperationException("Worktree removal was interrupted. Some native changes may already have taken effect; refresh the registrations and directory.", true, inner: exception); }
            try
            {
                WorktreeSnapshot? remaining = (await ReadWorktreeSnapshotsAsync(info, CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(tree => tree.Path == actual.Path);
                return new(result, remaining);
            }
            catch (Exception exception) when (exception is IOException or GitCommandException or UnauthorizedAccessException or InvalidOperationException)
            { throw new WorktreeOperationException("Git finished removing the worktree, but its resulting state could not be read. Refresh the repository.", true, result, exception); }
        }, cancellationToken);
    }

    public Task<GitResult> PruneWorktreesAsync(CancellationToken cancellationToken, Action<GitProcessOutput>? outputReceived)
        => WithWorktreesAsync(async (info, token) =>
        {
            IReadOnlyList<WorktreeSnapshot> all = await ReadWorktreeSnapshotsAsync(info, token).ConfigureAwait(false);
            WorktreeSnapshot current = all.SingleOrDefault(tree => tree.IsCurrent && tree.ValidationError is null)
                ?? throw new WorktreeOperationException("The source worktree registration is invalid.", false);
            await ValidateWorktreeSnapshotAsync(info, current, token).ConfigureAwait(false);
            GitResult result;
            try { result = await _runner.RunAsync(Path, [.. GlobalOptions, "worktree", "prune", "--verbose"], token, outputReceived: outputReceived).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { throw new WorktreeOperationException("Pruning was interrupted. Native Git may already have removed stale administrative entries; refresh the list.", true, inner: exception); }
            return result;
        }, cancellationToken);

    private static bool WorktreeContains(string directory, string path)
    {
        string relative = System.IO.Path.GetRelativePath(directory, path);
        return relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative);
    }
    private static void ValidateWorktreeDestination(WorktreeNode node)
    {
        if (node.Identity is null) { return; }
        if (!node.Identity.Value.IsDirectory || Directory.EnumerateFileSystemEntries(node.Path).Any())
        { throw new WorktreeOperationException("Choose a new directory or an existing empty directory for the worktree.", false); }
    }
    private static void CheckWorktreeDestination(WorktreeNode before, WorktreeNode after)
    {
        if (before.Identity != after.Identity || before.Path != after.Path) { throw new WorktreeOperationException("The worktree destination was replaced during validation.", false); }
        try { GitRulesFiles.CheckLocation(before.Location, after.Location); }
        catch (IOException exception) { throw new WorktreeOperationException(exception.Message, false, inner: exception); }
    }
    private async Task<bool> SupportsRelativeWorktreesAsync(CancellationToken token)
    {
        GitResult help = await ExecuteAsync(["worktree", "add", "-h"], token, allowedExitCodes: [129]).ConfigureAwait(false);
        string text = help.StandardOutput + help.StandardError;
        return text.Contains("--[no-]relative-paths", StringComparison.Ordinal) || text.Contains("--relative-paths", StringComparison.Ordinal);
    }
    private async Task<bool> RelativeWorktreeExtensionAsync(CancellationToken token)
    {
        GitResult result = await ExecuteAsync(["config", "--bool", "--get", "extensions.relativeWorktrees"], token, allowedExitCodes: [1]).ConfigureAwait(false);
        return result.Success && ReadOutputLine(result.StandardOutput) == "true";
    }
}
