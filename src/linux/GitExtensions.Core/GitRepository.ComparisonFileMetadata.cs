namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Literal paths with a stage-zero index entry; unmerged entries are deliberately excluded.</summary>
    public async Task<IReadOnlyList<string>> GetIndexFilePathsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadComparisonIndexAsync(cancellationToken).ConfigureAwait(false)).Where(entry => entry.Stage == 0)
            .Select(entry => entry.Path).ToArray();
    }

    /// <summary>
    /// Reads menu state without changing GitRevisionFile equality. The caller supplies the actual
    /// parent of the second endpoint; null is unknown/unborn, never a request to reread HEAD.
    /// </summary>
    public Task<IReadOnlyList<GitComparisonFileMetadata>> GetComparisonFileMetadataAsync(
        GitComparison comparison, GitCompareRevision? parentToSecond, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(comparison.Files);
        foreach (GitRevisionFile file in comparison.Files)
        { ValidatePath(file.Path); if (file.OriginalPath is string old) { ValidatePath(old); } }
        return WithMutationAsync<IReadOnlyList<GitComparisonFileMetadata>>(async token =>
        {
            token.ThrowIfCancellationRequested();
            GitCompareRevision first = await ResolveComparisonRevisionAsync(comparison.Base, token).ConfigureAwait(false);
            GitCompareRevision second = await ResolveComparisonRevisionAsync(comparison.Compare, token).ConfigureAwait(false);
            GitCompareRevision? parent = parentToSecond is null ? null : await ResolveComparisonRevisionAsync(parentToSecond, token).ConfigureAwait(false);
            GitFileStagedStatus staged = first.Kind == GitCompareRevisionKind.Index && second.Kind == GitCompareRevisionKind.WorkingTree
                ? GitFileStagedStatus.WorkTree
                : second.Kind == GitCompareRevisionKind.Index && parent is not null && first == parent
                    ? GitFileStagedStatus.Index
                    : first.Kind == GitCompareRevisionKind.Commit && second.Kind == GitCompareRevisionKind.Commit
                        ? GitFileStagedStatus.None : GitFileStagedStatus.Unknown;
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            IReadOnlyList<ComparisonIndexEntry> index = info.IsBare ? [] : await ReadComparisonIndexAsync(token).ConfigureAwait(false);
            Dictionary<string, GitIndexFileFlags> flags = info.IsBare ? [] : (await GetIndexFlagsAsync(token).ConfigureAwait(false))
                .ToDictionary(item => item.Path, StringComparer.Ordinal);
            HashSet<string> firstLinks = await ComparisonGitlinksAsync(first, index, token).ConfigureAwait(false);
            HashSet<string> secondLinks = await ComparisonGitlinksAsync(second, index, token).ConfigureAwait(false);
            HashSet<string> untracked = comparison.UntrackedPaths.ToHashSet(StringComparer.Ordinal);
            return comparison.Files.Select(file =>
            {
                token.ThrowIfCancellationRequested();
                flags.TryGetValue(file.Path, out GitIndexFileFlags? fileFlags);
                return new GitComparisonFileMetadata(file, staged, file.Status != '?' && !untracked.Contains(file.Path),
                    firstLinks.Contains(file.OriginalPath ?? file.Path) || secondLinks.Contains(file.Path),
                    fileFlags?.IsAssumeUnchanged == true, fileFlags?.IsSkipWorktree == true);
            }).ToArray();
        }, cancellationToken);
    }

    private async Task<HashSet<string>> ComparisonGitlinksAsync(GitCompareRevision revision,
        IReadOnlyList<ComparisonIndexEntry> index, CancellationToken token)
        => revision.Kind == GitCompareRevisionKind.Commit
            ? (await GetTreeAsync(revision.Revision, cancellationToken: token).ConfigureAwait(false))
                .Where(entry => entry.IsSubmodule).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal)
            : index.Where(entry => entry.Mode == "160000").Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);

    private sealed record ComparisonIndexEntry(string Path, string Mode, int Stage);

    private async Task<IReadOnlyList<ComparisonIndexEntry>> ReadComparisonIndexAsync(CancellationToken token)
    {
        string output = (await ExecuteAsync(["ls-files", "--stage", "-z", "--"], token).ConfigureAwait(false)).StandardOutput;
        if (output.Length > 0 && output[^1] != '\0') { throw new FormatException("Incomplete NUL-terminated index entries."); }
        List<ComparisonIndexEntry> result = [];
        foreach (string field in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            int tab = field.IndexOf('\t');
            string[] metadata = (tab < 0 ? "" : field[..tab]).Split(' ');
            if (tab < 0 || metadata is not [string mode, _, string stageText]
                || !int.TryParse(stageText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int stage)
                || stage is < 0 or > 3 || tab == field.Length - 1)
            { throw new FormatException("Invalid Git index entry."); }
            result.Add(new(field[(tab + 1)..], mode, stage));
        }
        return result;
    }
}
