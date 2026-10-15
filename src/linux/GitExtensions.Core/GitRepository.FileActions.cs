namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Windows file reset semantics, including its explicit delete-new-files choice.</summary>
    public Task<GitResult> RestoreRevisionFilesAsync(string revision, IReadOnlyList<string> paths, bool deleteNewFiles = false, CancellationToken cancellationToken = default)
    {
        string[] selected = paths.Select(ValidatePath).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) { throw new ArgumentException("Select files before resetting them.", nameof(paths)); }
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            string source = await ResolveRevisionAsync(revision, token).ConfigureAwait(false);
            Dictionary<string, GitTreeEntry> snapshot = (await GetTreeAsync(source, cancellationToken: token).ConfigureAwait(false)).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            string[] present = selected.Where(snapshot.ContainsKey).ToArray();
            string[] missing = selected.Except(present, StringComparer.Ordinal).ToArray();
            HashSet<string> indexed = (await ExecuteAsync(["--literal-pathspecs", "ls-files", "-z", "--", .. selected], token).ConfigureAwait(false))
                .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (string path in present)
            {
                ValidateWorktreeFileLocation(path);
                string full = System.IO.Path.Combine(Path, path);
                if (!indexed.Contains(path) && (File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null))
                { throw new InvalidOperationException("An untracked path would be overwritten: " + path); }
                if (!snapshot[path].IsSubmodule && Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
                { throw new InvalidOperationException("A directory would be overwritten: " + path); }
            }
            if (deleteNewFiles) { foreach (string path in missing) { ValidateWorktreeFileDeletion(path); } }
            GitResult result = await ExecuteAsync(["--literal-pathspecs", "reset", "--quiet", "HEAD", "--", .. selected], token).ConfigureAwait(false);
            if (present.Length > 0) { result = await ExecuteAsync(["--literal-pathspecs", "restore", "--source=" + source, "--staged", "--worktree", "--", .. present], token).ConfigureAwait(false); }
            if (deleteNewFiles)
            { foreach (string path in missing) { token.ThrowIfCancellationRequested(); ValidateWorktreeFileDeletion(path); File.Delete(System.IO.Path.Combine(Path, path)); } }
            return result;
        }, cancellationToken);
    }
    public Task<GitResult> MoveTrackedFileAsync(string path, string destination, CancellationToken cancellationToken = default)
    {
        string source = ValidatePath(path), target = ValidatePath(destination);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            ValidateWorktreeFileLocation(source); ValidateWorktreeFileLocation(target);
            return await ExecuteAsync(["--literal-pathspecs", "mv", "--", source, target], token).ConfigureAwait(false);
        }, cancellationToken);
    }
    public Task<GitResult> StopTrackingFileAsync(string path, CancellationToken cancellationToken = default)
    {
        string selected = ValidatePath(path);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            return await ExecuteAsync(["--literal-pathspecs", "rm", "--cached", "--", selected], token).ConfigureAwait(false);
        }, cancellationToken);
    }
}
