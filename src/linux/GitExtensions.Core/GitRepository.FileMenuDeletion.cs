namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Delete selected working files, leaving the index intact. Only untracked directories may be removed recursively.</summary>
    public Task<GitResult> DeleteWorkingTreeFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        string[] selected = paths.Select(ValidatePath).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) { throw new ArgumentException("Select files before deleting them.", nameof(paths)); }
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            foreach (string path in selected)
            {
                ValidateWorktreeFileLocation(path);
                string full = System.IO.Path.Combine(Path, path);
                if (!Directory.Exists(full) || new DirectoryInfo(full).LinkTarget is not null) { continue; }
                string tracked = (await ExecuteAsync(["--literal-pathspecs", "ls-files", "-z", "--", path], token).ConfigureAwait(false)).StandardOutput;
                if (tracked.Length > 0) { throw new InvalidOperationException("A tracked directory cannot be deleted as a file: " + path); }
            }
            foreach (string path in selected.OrderByDescending(path => path.Length))
            {
                token.ThrowIfCancellationRequested();
                ValidateWorktreeFileLocation(path);
                string full = System.IO.Path.Combine(Path, path);
                if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null) { Directory.Delete(full, recursive: true); }
                else { File.Delete(full); }
            }
            return new GitResult(0, "", "");
        }, cancellationToken);
    }
}
