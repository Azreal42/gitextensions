namespace GitExtensions.Linux;

public sealed record GitIndexFileFlags(string Path, bool IsAssumeUnchanged, bool IsSkipWorktree);

public sealed partial class GitRepository
{
    public Task<GitResult> SetSkipWorktreeAsync(IReadOnlyList<string> paths, bool skipWorktree, CancellationToken cancellationToken = default)
        => SetIndexFileFlagAsync(paths, skipWorktree, skip: true, cancellationToken);

    public Task<GitResult> SetAssumeUnchangedAsync(IReadOnlyList<string> paths, bool assumeUnchanged, CancellationToken cancellationToken = default)
        => SetIndexFileFlagAsync(paths, assumeUnchanged, skip: false, cancellationToken);

    private Task<GitResult> SetIndexFileFlagAsync(IReadOnlyList<string> paths, bool enabled, bool skip, CancellationToken cancellationToken)
    {
        string[] selected = ValidatePaths(paths);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            Dictionary<string, GitIndexFileFlags> current = (await GetIndexFlagsAsync(token).ConfigureAwait(false))
                .ToDictionary(item => item.Path, StringComparer.Ordinal);
            foreach (string path in selected)
            { if (!current.ContainsKey(path)) { throw new InvalidOperationException("The selected path is not indexed: " + path); } }
            string[] changed = selected.Where(path => (skip ? current[path].IsSkipWorktree : current[path].IsAssumeUnchanged) != enabled).ToArray();
            if (changed.Length == 0) { return new GitResult(0, "", ""); }
            string option = skip ? enabled ? "--skip-worktree" : "--no-skip-worktree"
                : enabled ? "--assume-unchanged" : "--no-assume-unchanged";
            return await ExecuteAsync(["update-index", option, "-z", "--stdin"], token,
                standardInput: string.Join('\0', changed) + '\0').ConfigureAwait(false);
        }, cancellationToken);
    }

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
