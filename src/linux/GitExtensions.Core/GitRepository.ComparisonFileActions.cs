namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>
    /// Reset files whose statuses/names are oriented away from the selected source. Callers
    /// partition real comparison pairs and invert statuses/names when resetting to Second.
    /// </summary>
    public Task<GitResult> RestoreComparisonFilesAsync(GitCompareRevision source, IReadOnlyList<GitRevisionFile> files,
        bool deleteNewFiles = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(files);
        if (!Enum.IsDefined(source.Kind)) { throw new ArgumentOutOfRangeException(nameof(source)); }
        if (source.Kind is GitCompareRevisionKind.WorkingTree or GitCompareRevisionKind.EmptyTree)
        { throw new ArgumentException("Choose a commit or index reset source.", nameof(source)); }
        GitRevisionFile[] selected = files.ToArray();
        if (selected.Length == 0) { throw new ArgumentException("Select files before resetting them.", nameof(files)); }
        foreach (GitRevisionFile file in selected)
        { ValidatePath(file.Path); if (file.OriginalPath is string old) { ValidatePath(old); } }
        if (source.Kind == GitCompareRevisionKind.Commit)
        {
            string[] paths = selected.SelectMany(file => file.Status == 'R' && file.OriginalPath is string old
                ? new[] { file.Path, old } : [file.Path]).Distinct(StringComparer.Ordinal).ToArray();
            return RestoreRevisionFilesAsync(source.Revision, paths, deleteNewFiles, cancellationToken);
        }
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            IReadOnlyList<ComparisonIndexEntry> entries = await ReadComparisonIndexAsync(token).ConfigureAwait(false);
            Dictionary<string, ComparisonIndexEntry> stageZero = entries.Where(entry => entry.Stage == 0)
                .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            HashSet<string> conflicts = entries.Where(entry => entry.Stage != 0).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            HashSet<string> restore = new(StringComparer.Ordinal), delete = new(StringComparer.Ordinal);
            foreach (GitRevisionFile file in selected)
            {
                token.ThrowIfCancellationRequested();
                if (deleteNewFiles && file.Status is 'A' or '?' or 'R')
                {
                    ValidateWorktreeFileDeletion(file.Path); delete.Add(file.Path); continue;
                }
                string path = file.Status == 'R' && file.OriginalPath is string old ? old : file.Path;
                if (conflicts.Contains(path)) { throw new InvalidOperationException("Cannot restore an unmerged index path: " + path); }
                if (!stageZero.TryGetValue(path, out ComparisonIndexEntry? entry))
                {
                    if (file.Status is 'A' or '?') { continue; }
                    throw new InvalidOperationException("The selected path is absent from the index: " + path);
                }
                ValidateWorktreeFileLocation(path);
                string full = System.IO.Path.Combine(Path, path);
                if (entry.Mode == "160000") { throw new InvalidOperationException("Use the submodule workflow for an indexed gitlink: " + path); }
                if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
                { throw new InvalidOperationException("A directory would be overwritten: " + path); }
                restore.Add(path);
            }
            if (restore.Overlaps(delete)) { throw new InvalidOperationException("Selected reset paths have conflicting restore/delete actions."); }
            GitResult result = new(0, "", "");
            if (restore.Count > 0)
            {
                // Without -u, checkout-index copies blobs without rewriting even index stat/extension bytes.
                result = await ExecuteAsync(["checkout-index", "--force", "--ignore-skip-worktree-bits", "-z", "--stdin"], token,
                    standardInput: string.Join('\0', restore) + '\0').ConfigureAwait(false);
            }
            foreach (string path in delete)
            {
                token.ThrowIfCancellationRequested(); ValidateWorktreeFileDeletion(path);
                File.Delete(System.IO.Path.Combine(Path, path));
            }
            return result;
        }, cancellationToken);
    }
}
