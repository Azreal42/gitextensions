namespace GitExtensions.Linux;

public sealed record GitIndexFileFlags(string Path, bool IsAssumeUnchanged, bool IsSkipWorktree);

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<GitIndexFileFlags>> GetIndexFlagsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        string output = (await ExecuteAsync(["ls-files", "-v", "-z", "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        if (output.Length > 0 && output[^1] != '\0') { throw new FormatException("Incomplete NUL-terminated Git index flags."); }
        Dictionary<string, GitIndexFileFlags> files = new(StringComparer.Ordinal);
        foreach (string field in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (field.Length < 3 || field[1] != ' ') { throw new FormatException("Invalid Git index flag record."); }
            string path = field[2..];
            bool assumeUnchanged = field[0] is >= 'a' and <= 'z';
            bool skipWorktree = field[0] is 'S' or 's';
            files.TryGetValue(path, out GitIndexFileFlags? previous);
            files[path] = new(path, assumeUnchanged || previous?.IsAssumeUnchanged == true,
                skipWorktree || previous?.IsSkipWorktree == true);
        }
        return files.Values.ToArray();
    }
}
