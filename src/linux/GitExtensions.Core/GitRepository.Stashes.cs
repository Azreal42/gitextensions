using System.Text;

namespace GitExtensions.Linux;

public sealed record GitStashSaveOptions(string? Message = null, bool IncludeUntracked = false,
    bool KeepIndex = false, IReadOnlyList<string>? Paths = null, bool StagedOnly = false);
public sealed record GitStashChanges(GitRevisionChanges Tracked, GitRevisionChanges? Untracked, string Message);

public sealed partial class GitRepository
{
    public async Task<bool> SupportsStashStagedAsync(CancellationToken cancellationToken = default)
        => await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false) >= new Version(2, 35);

    public Task<GitResult> StashAsync(GitStashSaveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string[] paths = options.Paths?.Select(ValidatePath).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (options.Paths is not null && paths.Length == 0) { throw new ArgumentException("Select files before stashing selected changes.", nameof(options)); }
        if (options.StagedOnly && (options.IncludeUntracked || options.KeepIndex || paths.Length != 0))
        { throw new ArgumentException("Stash staged cannot be combined with keep index, untracked files or selected paths.", nameof(options)); }
        List<string> arguments = ["stash", "push"];
        if (options.IncludeUntracked) { arguments.Add("--include-untracked"); }
        if (options.KeepIndex) { arguments.Add("--keep-index"); }
        if (options.StagedOnly) { arguments.Add("--staged"); }
        if (!string.IsNullOrWhiteSpace(options.Message)) { arguments.AddRange(["--message", options.Message.Trim()]); }
        // Do not enable global literal-pathspecs: stash's untracked cleanup uses
        // internal magic pathspecs. Only the user's selected paths are literal.
        if (paths.Length > 0) { arguments.AddRange(["--", ..paths.Select(path => ":(literal)" + path)]); }
        return MutateAsync(arguments, cancellationToken);
    }

    public async Task<GitStashChanges> GetStashChangesAsync(GitStash stash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stash);
        GitCommit commit = (await GetLogAsync(1, stash.Hash, cancellationToken, all: false).ConfigureAwait(false)).Single();
        if (commit.Parents.Count < 2) { throw new ArgumentException("Select a stash commit.", nameof(stash)); }
        GitRevisionChanges tracked = await GetRevisionChangesAsync(commit.Hash, commit.Parents[0], cancellationToken).ConfigureAwait(false);
        GitRevisionChanges? untracked = commit.Parents.Count > 2
            ? await GetRevisionChangesAsync(commit.Parents[2], cancellationToken: cancellationToken).ConfigureAwait(false) : null;
        string message = (await ExecuteAsync(["show", "--no-patch", "--encoding=UTF-8", "--format=%B", commit.Hash, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\n');
        return new(tracked, untracked, message);
    }

    public Task<GitResult> ApplyStashAsync(GitStash stash, bool pop = false, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await CheckStashSelectionAsync(stash, token).ConfigureAwait(false);
            // Windows apply/pop do not pass --index. Keep index only affects save.
            return await ExecuteAsync(["stash", pop ? "pop" : "apply", pop ? stash.Reference : stash.Hash], token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitResult> PopLatestStashAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            GitStash? stash = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault();
            if (stash is null) { throw new InvalidOperationException("There are no stashes."); }
            return await ExecuteAsync(["stash", "pop", stash.Reference], token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitResult> DropStashAsync(GitStash stash, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await CheckStashSelectionAsync(stash, token).ConfigureAwait(false);
            return await ExecuteAsync(["stash", "drop", stash.Reference], token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Cherry-picks selected stash file changes into the index and worktree, using the Windows viewer's three-way apply.</summary>
    public Task<GitResult> ApplyStashChangesAsync(GitStash stash, IReadOnlyList<GitRevisionFile> files,
        CancellationToken cancellationToken = default)
    {
        GitRevisionFile[] selection = CopyStashFileSelection(files);
        return WithMutationAsync(async token =>
        {
            SelectedStashFile[] selected = await ReadSelectedStashFilesAsync(stash, selection, token).ConfigureAwait(false);
            using MemoryStream patch = new();
            foreach (IGrouping<GitRevisionChanges, SelectedStashFile> group in selected.GroupBy(file => file.Revisions))
            {
                string[] paths = StashFilePaths(group.Select(file => file.File));
                GitRevisionChanges changes = group.Key;
                List<string> arguments = changes.BaseRevision is null
                    ? ["--literal-pathspecs", "diff-tree", "--root", "-r", "--no-commit-id", "--patch"]
                    : ["--literal-pathspecs", "diff", "--find-renames", "--find-copies"];
                arguments.AddRange(["--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", "--no-relative", "--submodule=short",
                    "--src-prefix=a/", "--dst-prefix=b/", "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= "]);
                if (changes.BaseRevision is not null) { arguments.Add(changes.BaseRevision); }
                arguments.AddRange([changes.Revision, "--", .. paths]);
                GitBinaryResult diff = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token).ConfigureAwait(false);
                if (!diff.Success) { throw StashBinaryError(arguments, diff); }
                await patch.WriteAsync(diff.StandardOutput, token).ConfigureAwait(false);
            }
            if (patch.Length == 0) { throw new InvalidOperationException("The selected stash files have no changes to apply."); }
            // Preserve canonical Git bytes, including binary patches and non-UTF8 text.
            // A three-way conflict intentionally leaves the index unresolved; it never drops the stash.
            string[] apply = ["apply", "--3way", "--index", "--whitespace=nowarn", "-"];
            GitBinaryResult applied = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. apply], token, patch.ToArray()).ConfigureAwait(false);
            if (!applied.Success) { throw StashBinaryError(apply, applied); }
            return new GitResult(applied.ExitCode, Encoding.UTF8.GetString(applied.StandardOutput), applied.StandardError);
        }, cancellationToken);
    }

    /// <summary>Resets selected paths to the stash or its first parent, following the Windows reset confirmation's delete-new-files option.</summary>
    public Task<GitResult> RestoreStashFilesAsync(GitStash stash, IReadOnlyList<GitRevisionFile> files,
        bool toParent = false, bool deleteNewFiles = false, CancellationToken cancellationToken = default)
    {
        GitRevisionFile[] selection = CopyStashFileSelection(files);
        return WithMutationAsync(async token =>
        {
            SelectedStashFile[] selected = await ReadSelectedStashFilesAsync(stash, selection, token).ConfigureAwait(false);
            if (!await HasHeadAsync(token).ConfigureAwait(false)) { throw new InvalidOperationException("Resetting stash files requires a current HEAD commit."); }
            string[] paths = StashFilePaths(selected.Select(file => file.File));
            Dictionary<string, string> sources = new(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, GitTreeEntry>> snapshots = new(StringComparer.Ordinal);
            HashSet<string> submodules = new(StringComparer.Ordinal);
            foreach (SelectedStashFile file in selected)
            {
                string? source = toParent ? file.Revisions.BaseRevision : file.Revisions.Revision;
                if (source is null) { continue; } // Untracked files have no parent snapshot.
                if (!snapshots.TryGetValue(source, out Dictionary<string, GitTreeEntry>? available))
                {
                    available = (await GetTreeAsync(source, cancellationToken: token).ConfigureAwait(false)).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
                    snapshots.Add(source, available);
                }
                foreach (string path in StashFilePaths([file.File]).Where(available.ContainsKey))
                {
                    sources.Add(path, source);
                    if (available[path].Type == "commit") { submodules.Add(path); }
                }
            }
            string[] missing = paths.Where(path => !sources.ContainsKey(path)).ToArray();
            HashSet<string> indexed = (await ExecuteAsync(["ls-files", "-z", "--", .. paths], token).ConfigureAwait(false))
                .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            // Validate every collision/deletion before resetting any selected index entry.
            foreach (string path in sources.Keys)
            {
                ValidateWorktreeFileLocation(path);
                string fullPath = System.IO.Path.Combine(Path, path);
                if (!indexed.Contains(path) && (File.Exists(fullPath) || Directory.Exists(fullPath) || new FileInfo(fullPath).LinkTarget is not null))
                { throw new InvalidOperationException("An untracked path would be overwritten: " + path); }
                if (!submodules.Contains(path) && Directory.Exists(fullPath) && new DirectoryInfo(fullPath).LinkTarget is null)
                { throw new InvalidOperationException("A directory would be overwritten: " + path); }
            }
            if (deleteNewFiles)
            {
                foreach (string path in missing) { ValidateWorktreeFileDeletion(path); }
            }
            // Windows first unstages selected paths towards HEAD, then checks out existing
            // snapshot files. Optional deletion affects the worktree, not the HEAD index entry.
            GitResult result = await ExecuteAsync(["reset", "--quiet", "HEAD", "--", .. paths], token).ConfigureAwait(false);
            foreach (IGrouping<string, KeyValuePair<string, string>> group in sources.GroupBy(entry => entry.Value))
            {
                result = await ExecuteAsync(["restore", "--source=" + group.Key, "--staged", "--worktree", "--", .. group.Select(entry => entry.Key)], token).ConfigureAwait(false);
            }
            if (deleteNewFiles)
            {
                foreach (string path in missing)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateWorktreeFileDeletion(path);
                    File.Delete(System.IO.Path.Combine(Path, path));
                }
            }
            return result;
        }, cancellationToken);
    }

    private sealed record SelectedStashFile(GitRevisionFile File, GitRevisionChanges Revisions);

    private GitRevisionFile[] CopyStashFileSelection(IReadOnlyList<GitRevisionFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) { throw new ArgumentException("Select stash files before applying or resetting them.", nameof(files)); }
        return files.Select(file =>
        {
            ArgumentNullException.ThrowIfNull(file);
            return file with { Path = ValidatePath(file.Path), OriginalPath = file.OriginalPath is null ? null : ValidatePath(file.OriginalPath) };
        }).DistinctBy(file => (file.Path, file.Status, file.OriginalPath)).ToArray();
    }

    private async Task<SelectedStashFile[]> ReadSelectedStashFilesAsync(GitStash stash, GitRevisionFile[] selection, CancellationToken token)
    {
        await CheckStashSelectionAsync(stash, token).ConfigureAwait(false);
        await EnsureWorktreeAsync(token).ConfigureAwait(false);
        GitStashChanges changes = await GetStashChangesAsync(stash, token).ConfigureAwait(false);
        SelectedStashFile[] available = changes.Tracked.Files.Select(file => new SelectedStashFile(file, changes.Tracked))
            .Concat(changes.Untracked is null ? [] : changes.Untracked.Files.Select(file => new SelectedStashFile(file, changes.Untracked))).ToArray();
        SelectedStashFile[] selected = selection.Select(file => available.SingleOrDefault(candidate => SameStashFile(candidate.File, file))
            ?? throw new ArgumentException("A selected file does not belong to this stash: " + file.Path, nameof(selection))).ToArray();
        HashSet<string> touched = StashFilePaths(selected.Select(file => file.File)).ToHashSet(StringComparer.Ordinal);
        if (available.Any(file => !selected.Contains(file) && StashFilePaths([file.File]).Any(touched.Contains)))
        { throw new ArgumentException("Select every stash change affecting the same pathname.", nameof(selection)); }
        return selected;
    }

    private static bool SameStashFile(GitRevisionFile first, GitRevisionFile second)
        => first.Path == second.Path && first.Status == second.Status && first.OriginalPath == second.OriginalPath;

    private static string[] StashFilePaths(IEnumerable<GitRevisionFile> files)
        => files.SelectMany(file => file.Status == 'R' && file.OriginalPath is not null ? new[] { file.OriginalPath, file.Path } : [file.Path])
            .Distinct(StringComparer.Ordinal).ToArray();

    private void ValidateWorktreeFileDeletion(string path)
    {
        ValidateWorktreeFileLocation(path);
        string fullPath = System.IO.Path.Combine(Path, path);
        if (Directory.Exists(fullPath) && new DirectoryInfo(fullPath).LinkTarget is null)
        { throw new InvalidOperationException("Resetting files cannot delete a directory: " + path); }
    }

    private void ValidateWorktreeFileLocation(string path)
    {
        string fullPath = System.IO.Path.Combine(Path, path);
        for (string? parent = System.IO.Path.GetDirectoryName(fullPath); parent is not null && parent != Path; parent = System.IO.Path.GetDirectoryName(parent))
        {
            if (new DirectoryInfo(parent).LinkTarget is not null) { throw new InvalidOperationException("A selected path is beneath a symbolic link: " + path); }
        }
    }

    private static GitCommandException StashBinaryError(IReadOnlyList<string> arguments, GitBinaryResult result)
        => new(arguments, new GitResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput), result.StandardError));

    private async Task CheckStashSelectionAsync(GitStash stash, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stash);
        GitStash? current = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault(entry => entry.Reference == stash.Reference);
        if (current?.Hash != stash.Hash) { throw new InvalidOperationException("The stash list changed. Refresh and select the stash again."); }
    }
}
