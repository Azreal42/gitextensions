using System.Globalization;

namespace GitExtensions.Linux;

public sealed record GitCherryPickOptions(string Revision, bool AutoCommit = false, bool AddCommitReference = false, int? MainlineParent = null);
public sealed record GitCherryPickRevision(GitCommit Commit, IReadOnlyList<GitCommit> Parents);
public sealed record GitCherryPickResult(GitResult CommandResult, GitCherryPickSession Session, IReadOnlyList<string> ConflictedPaths)
{
    public bool Success => CommandResult.Success;
}

/// <summary>Recovery for one original cherry-pick dialog, including the index present before a --no-commit pick.</summary>
public sealed class GitCherryPickSession
{
    internal GitCherryPickSession(GitCherryPickOptions options, string root, string gitDirectory, string head, string? reference,
        byte[]? index, string? sharedIndex, byte[]? sharedBytes, IReadOnlyList<CherryPickSavedPath> files, IReadOnlyList<string> newDirectories,
        IReadOnlySet<string> untrackedBefore, IReadOnlyDictionary<string, byte[]?> metadata)
    {
        Options = options; Root = root; GitDirectory = gitDirectory; OriginalHead = head; Reference = reference;
        Index = index; SharedIndex = sharedIndex; SharedBytes = sharedBytes; Files = files; NewDirectories = newDirectories; UntrackedBefore = untrackedBefore; Metadata = metadata;
    }
    public GitCherryPickOptions Options { get; }
    public bool Completed { get; internal set; }
    public bool Aborted { get; internal set; }
    public bool IsRestoring => RecoveryPhase == "Restoring";
    internal string Root { get; }
    internal string GitDirectory { get; }
    internal string OriginalHead { get; }
    internal string? Reference { get; }
    internal byte[]? Index { get; }
    internal string? SharedIndex { get; }
    internal byte[]? SharedBytes { get; }
    internal IReadOnlyList<CherryPickSavedPath> Files { get; }
    internal IReadOnlyList<string> NewDirectories { get; }
    internal IReadOnlySet<string> UntrackedBefore { get; }
    internal IReadOnlyDictionary<string, byte[]?> Metadata { get; }
    internal string RecoveryId { get; set; } = Guid.NewGuid().ToString("N");
    internal string CommonDirectory { get; set; } = "";
    internal string RecoveryPhase { get; set; } = "Prepared";
    internal IReadOnlyList<CherryPickSavedObject> Objects { get; set; } = [];
    internal IReadOnlyList<CherryPickSavedDirectory> Directories { get; set; } = [];
}

internal sealed record CherryPickSavedPath(string Path, bool Exists, bool Directory, bool Gitlink, byte[]? Bytes, string? Link, UnixFileMode? Mode);

public sealed partial class GitRepository
{
    public async Task<GitCherryPickRevision> GetCherryPickRevisionAsync(string revision, CancellationToken cancellationToken = default)
    {
        string hash = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        GitCommit commit = (await GetLogAsync(1, hash, cancellationToken, all: false).ConfigureAwait(false)).Single();
        List<GitCommit> parents = [];
        foreach (string parent in commit.Parents)
        { parents.Add((await GetLogAsync(1, parent, cancellationToken, all: false).ConfigureAwait(false)).Single()); }
        return new(commit, parents);
    }

    /// <summary>Validate and freeze the complete selection before the first dialog can mutate it. Order is retained.</summary>
    public async Task<IReadOnlyList<GitCherryPickRevision>> PrepareCherryPickSelectionAsync(IEnumerable<string> revisions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        string[] selection = revisions.ToArray();
        if (selection.Length == 0) { throw new ArgumentException("Select at least one commit.", nameof(revisions)); }
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        List<GitCherryPickRevision> result = [];
        foreach (string revision in selection) { result.Add(await GetCherryPickRevisionAsync(revision, cancellationToken).ConfigureAwait(false)); }
        return result;
    }

    public async Task<GitCherryPickResult> CherryPickAsync(GitCherryPickOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
            GitCherryPickRevision revision = await GetCherryPickRevisionAsync(options.Revision, cancellationToken).ConfigureAwait(false);
            ValidateCherryPickParent(options, revision.Commit);
            GitRepositoryInfo info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream recoveryLock = AcquireCherryPickRecoveryLock(info);
            await RequireNoCherryPickRecoveryAsync(cancellationToken).ConfigureAwait(false);
            GitStatus before = await ReadCherryPickStatusAsync(cancellationToken).ConfigureAwait(false);
            if (before.IsUnborn) { throw new InvalidOperationException("Cherry-pick requires an existing HEAD."); }
            if (before.Changes.Any(change => change.IsConflict) || CherryPickOperationExists(info.GitDirectory))
            { throw new InvalidOperationException("Finish or abort the current repository operation before cherry-picking another commit."); }
            options = options with { Revision = revision.Commit.Hash };
            GitCherryPickSession session = await CaptureCherryPickSessionAsync(options, revision.Commit, info, before, cancellationToken).ConfigureAwait(false);
            try { await SaveCherryPickRecoveryAsync(session, cancellationToken).ConfigureAwait(false); }
            catch
            {
                string directory = CherryPickRecoveryDirectory(info.GitDirectory);
                if (Directory.Exists(directory)) { DeleteCherryPickRecoveryDirectory(directory); }
                throw; // The native command has not started.
            }
            List<string> arguments = ["cherry-pick"];
            if (!options.AutoCommit) { arguments.Add("--no-commit"); }
            if (options.AddCommitReference) { arguments.Add("-x"); }
            if (options.MainlineParent is int parent) { arguments.AddRange(["-m", parent.ToString(CultureInfo.InvariantCulture)]); }
            arguments.AddRange(["--", options.Revision]);
            GitResult result;
            try { result = await ExecuteAsync(arguments, cancellationToken, allowedExitCodes: [1], environment: CherryPickEditor).ConfigureAwait(false); }
            catch (OperationCanceledException cancelled)
            {
                try { await RestoreCherryPickSessionAsync(session).ConfigureAwait(false); await FinishCherryPickRecoveryAsync(session, "Aborted").ConfigureAwait(false); }
                catch (Exception restore) { throw new AggregateException("The interrupted cherry-pick could not restore its original repository state.", cancelled, restore); }
                throw;
            }
            IReadOnlyList<string> conflicts = await CherryPickConflictsAsync(CancellationToken.None).ConfigureAwait(false);
            session.Completed = result.Success;
            if (result.Success) { await FinishCherryPickRecoveryAsync(session, "Completed").ConfigureAwait(false); }
            else { session.RecoveryPhase = "Pending"; await SaveCherryPickRecoveryAsync(session, CancellationToken.None).ConfigureAwait(false); }
            if (!result.Success && conflicts.Count == 0 && !File.Exists(System.IO.Path.Combine(info.GitDirectory, "CHERRY_PICK_HEAD")))
            { throw new GitCommandException(arguments, result); }
            return new(result, session, conflicts);
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<GitCherryPickResult> ResumeCherryPickAsync(GitCherryPickSession session, GitOperationAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(action)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        if (!StringComparer.Ordinal.Equals(session.Root, Path) || session.Completed || session.Aborted)
        { throw new InvalidOperationException("This cherry-pick recovery session is not pending in this repository."); }
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream recoveryLock = AcquireCherryPickRecoveryLock(await GetInfoAsync(cancellationToken).ConfigureAwait(false));
            await ValidateCherryPickJournalSessionAsync(session, cancellationToken).ConfigureAwait(false);
            if (action == GitOperationAction.Continue && session.IsRestoring)
            { throw new InvalidOperationException("Restoration was interrupted. Finish Abort or Skip before starting another cherry-pick; Continue cannot commit a partially restored state."); }
            if (action is GitOperationAction.Abort or GitOperationAction.Skip)
            {
                await RestoreCherryPickSessionAsync(session).ConfigureAwait(false);
                session.Aborted = true;
                await FinishCherryPickRecoveryAsync(session, "Aborted").ConfigureAwait(false);
                return new(new GitResult(0, "", ""), session, []);
            }
            await ValidateCherryPickSessionIdentityAsync(session).ConfigureAwait(false);
            IReadOnlyList<string> conflicts = await CherryPickConflictsAsync(cancellationToken).ConfigureAwait(false);
            if (conflicts.Count != 0) { throw new InvalidOperationException("Resolve and add all conflicted files before continuing the cherry-pick."); }
            GitResult result;
            if (!session.Options.AutoCommit)
            {
                // A single -n conflict has no CHERRY_PICK_HEAD/sequencer. Its resolved index is the result.
                result = new(0, "", "");
            }
            else
            {
                try { result = await ExecuteAsync(["cherry-pick", "--continue"], cancellationToken, allowedExitCodes: [1], environment: CherryPickEditor).ConfigureAwait(false); }
                catch (OperationCanceledException cancelled)
                {
                    try { await RestoreCherryPickSessionAsync(session).ConfigureAwait(false); await FinishCherryPickRecoveryAsync(session, "Aborted").ConfigureAwait(false); }
                    catch (Exception restore) { throw new AggregateException("The interrupted cherry-pick could not restore its original repository state.", cancelled, restore); }
                    throw;
                }
            }
            session.Completed = result.Success;
            if (result.Success) { await FinishCherryPickRecoveryAsync(session, "Completed").ConfigureAwait(false); }
            return new(result, session, await CherryPickConflictsAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally { _mutationLock.Release(); }
    }

    public Task<IReadOnlyList<string>> GetCherryPickConflictsAsync(CancellationToken cancellationToken = default)
        => CherryPickConflictsAsync(cancellationToken);

    private static readonly IReadOnlyDictionary<string, string?> CherryPickEditor = new Dictionary<string, string?> { ["GIT_EDITOR"] = "true", ["GIT_SEQUENCE_EDITOR"] = "true" };
    private static readonly string[] CherryPickMetadata = ["CHERRY_PICK_HEAD", "MERGE_MSG", "AUTO_MERGE"];
    private static bool CherryPickOperationExists(string directory)
        => new[] { "CHERRY_PICK_HEAD", "MERGE_HEAD", "REVERT_HEAD" }.Any(name => File.Exists(System.IO.Path.Combine(directory, name)))
        || new[] { "sequencer", "rebase-merge", "rebase-apply" }.Any(name => Directory.Exists(System.IO.Path.Combine(directory, name)));
    private static void ValidateCherryPickParent(GitCherryPickOptions options, GitCommit revision)
    {
        if (revision.Parents.Count > 1)
        {
            if (options.MainlineParent is not int parent || parent < 1 || parent > revision.Parents.Count)
            { throw new ArgumentException("Select an existing parent of the merge commit.", nameof(options)); }
        }
        else if (options.MainlineParent is not null)
        { throw new ArgumentException("A parent can only be selected for a merge commit.", nameof(options)); }
    }
    private async Task<IReadOnlyList<string>> CherryPickConflictsAsync(CancellationToken token)
        => (await ReadCherryPickStatusAsync(token).ConfigureAwait(false)).Changes.Where(change => change.IsConflict).Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray();
    private async Task<GitStatus> ReadCherryPickStatusAsync(CancellationToken token)
        => GitOutputParser.ParseStatus((await ExecuteAsync(["status", "--porcelain=2", "--branch", "-z", "--untracked-files=all"], token,
            environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }).ConfigureAwait(false)).StandardOutput);

    private async Task<GitCherryPickSession> CaptureCherryPickSessionAsync(GitCherryPickOptions options, GitCommit commit, GitRepositoryInfo info, GitStatus before, CancellationToken token)
    {
        GitResult reference = await ExecuteAsync(["symbolic-ref", "--quiet", "HEAD"], token, allowedExitCodes: [1]).ConfigureAwait(false);
        EnsureCherryPickMetadataSafety(info.GitDirectory);
        foreach (string name in CherryPickMetadata.Append("index")) { EnsureCherryPickMetadataFileSafety(System.IO.Path.Combine(info.GitDirectory, name)); }
        string index = System.IO.Path.Combine(info.GitDirectory, "index");
        string indexLock = index + ".lock";
        await using FileStream snapshotLock = new(indexLock, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            List<string> names = ["diff-tree", "--no-commit-id", "--name-only", "--no-renames", "-r", "-z", "--root"];
            if (commit.Parents.Count > 0) { names.Add(commit.Parents[(options.MainlineParent ?? 1) - 1]); }
            names.AddRange([commit.Hash, "--"]);
            HashSet<string> paths = new((await ExecuteAsync(names, token).ConfigureAwait(false)).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            HashSet<string> sourcePaths = new(paths, StringComparer.Ordinal);
            if (commit.Parents.Count > 0)
            {
                // Inspect names only. A historical parent can differ from HEAD in millions of unrelated bytes.
                string parent = commit.Parents[(options.MainlineParent ?? 1) - 1];
                string[] changes = (await ExecuteAsync(["diff", "--cached", "--no-ext-diff", "--no-textconv", "--name-status", "--find-renames", "-z", parent, "--"], token).ConfigureAwait(false)).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                for (int cursor = 0; cursor < changes.Length;)
                {
                    string status = changes[cursor++];
                    if (cursor >= changes.Length) { throw new FormatException("Missing checkout diff pathname."); }
                    string original = changes[cursor++];
                    if (status[0] is not ('R' or 'C')) { continue; }
                    if (cursor >= changes.Length) { throw new FormatException("Missing checkout rename destination."); }
                    string destination = changes[cursor++];
                    if (sourcePaths.Contains(original) || sourcePaths.Contains(destination)) { paths.Add(original); paths.Add(destination); }
                    AddCherryPickDirectoryDestinations(paths, sourcePaths, original, destination);
                }
            }
            foreach (GitChange change in before.Changes.Where(change => !change.IsUntracked))
            { paths.Add(change.Path); if (change.OriginalPath is string original) { paths.Add(original); } }
            List<CherryPickSavedPath> files = [];
            HashSet<string> directories = new(StringComparer.Ordinal);
            Dictionary<string, CherryPickSavedDirectory> existingDirectories = new(StringComparer.Ordinal);
            foreach (string name in paths.Order(StringComparer.Ordinal))
            {
                GitResult indexed = await ExecuteAsync(["--literal-pathspecs", "ls-files", "--stage", "-z", "--", name], token).ConfigureAwait(false);
                bool gitlink = indexed.StandardOutput.StartsWith("160000 ", StringComparison.Ordinal)
                    || (await GetTreeAsync(commit.Hash, name, token).ConfigureAwait(false)).Any(entry => entry.Path == name && entry.IsSubmodule);
                EnsureCherryPickPathSafety(name, gitlink);
                string file = System.IO.Path.Combine(Path, ValidatePath(name));
                FileInfo details = new(file);
                string? link = details.LinkTarget;
                bool directory = Directory.Exists(file) && link is null;
                bool exists = details.Exists || directory || link is not null;
                byte[]? bytes = exists && !directory && link is null ? await File.ReadAllBytesAsync(file, token).ConfigureAwait(false) : null;
                UnixFileMode? mode = exists && !directory && link is null && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(file) : null;
                files.Add(new(name, exists, directory, gitlink, bytes, link, mode));
                for (string? parent = System.IO.Path.GetDirectoryName(file); parent is not null && parent != Path; parent = System.IO.Path.GetDirectoryName(parent))
                {
                    if (!Directory.Exists(parent)) { directories.Add(parent); }
                    else
                    {
                        string relative = System.IO.Path.GetRelativePath(Path, parent);
                        UnixFileMode? directoryMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(parent);
                        existingDirectories.TryAdd(relative, new(relative, directoryMode));
                    }
                }
            }
            string shared = (await ExecuteAsync(["rev-parse", "--shared-index-path"], token).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
            string? sharedIndex = shared.Length == 0 ? null : System.IO.Path.GetFullPath(shared, Path);
            if (sharedIndex is not null)
            {
                if (System.IO.Path.GetDirectoryName(sharedIndex) != info.GitDirectory || !System.IO.Path.GetFileName(sharedIndex).StartsWith("sharedindex.", StringComparison.Ordinal))
                { throw new IOException("The shared index must be stored in this worktree's Git metadata directory."); }
                EnsureCherryPickMetadataFileSafety(sharedIndex);
            }
            byte[]? sharedBytes = sharedIndex is null ? null : await File.ReadAllBytesAsync(sharedIndex, token).ConfigureAwait(false);
            Dictionary<string, byte[]?> metadata = new(StringComparer.Ordinal);
            foreach (string name in CherryPickMetadata)
            {
                string file = System.IO.Path.Combine(info.GitDirectory, name);
                metadata[name] = File.Exists(file) ? await File.ReadAllBytesAsync(file, token).ConfigureAwait(false) : null;
            }
            GitCherryPickSession session = new(options, Path, info.GitDirectory, before.Head!, reference.Success ? reference.StandardOutput.TrimEnd('\r', '\n') : null,
                File.Exists(index) ? await File.ReadAllBytesAsync(index, token).ConfigureAwait(false) : null, sharedIndex, sharedBytes,
                files, directories.OrderByDescending(name => name.Length).ToArray(),
                new HashSet<string>(before.Changes.Where(change => change.IsUntracked).Select(change => change.Path), StringComparer.Ordinal), metadata);
            session.CommonDirectory = info.CommonDirectory;
            session.Directories = existingDirectories.Values.OrderBy(directory => directory.Path.Length).ToArray();
            session.Objects = await CaptureCherryPickIndexObjectsAsync(session, token).ConfigureAwait(false);
            return session;
        }
        finally { await snapshotLock.DisposeAsync().ConfigureAwait(false); File.Delete(indexLock); }
    }

    private async Task RestoreCherryPickSessionAsync(GitCherryPickSession session)
    {
        await ValidateCherryPickSessionIdentityAsync(session).ConfigureAwait(false);
        EnsureCherryPickMetadataSafety(session.GitDirectory);
        foreach (CherryPickSavedPath saved in session.Files) { EnsureCherryPickPathSafety(saved.Path, saved.Gitlink); }
        foreach (string name in CherryPickMetadata.Append("index"))
        { EnsureCherryPickMetadataFileSafety(System.IO.Path.Combine(session.GitDirectory, name)); }
        if (session.SharedIndex is not null) { EnsureCherryPickMetadataFileSafety(session.SharedIndex); }
        if (session.SharedIndex is not null && File.Exists(session.SharedIndex)
            && !(await File.ReadAllBytesAsync(session.SharedIndex, CancellationToken.None).ConfigureAwait(false)).SequenceEqual(session.SharedBytes!))
        { throw new IOException("The shared index changed outside the cherry-pick; recovery cannot replace it."); }
        await ValidateCherryPickRecoveryCoverageAsync(session).ConfigureAwait(false);
        string index = System.IO.Path.Combine(session.GitDirectory, "index");
        string indexLock = index + ".lock";
        await using FileStream locked = new(indexLock, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bool ownsLock = true;
        try
        {
            await ValidateCherryPickSessionIdentityAsync(session).ConfigureAwait(false);
            await ValidateCherryPickRecoveryCoverageAsync(session).ConfigureAwait(false);
            session.RecoveryPhase = "Restoring";
            await SaveCherryPickRecoveryAsync(session, CancellationToken.None).ConfigureAwait(false);
            await RestoreCherryPickIndexObjectsAsync(session).ConfigureAwait(false);
            // --quit only clears the native sequencer: --abort would discard the index that existed before -n.
            if (File.Exists(System.IO.Path.Combine(session.GitDirectory, "CHERRY_PICK_HEAD")) || Directory.Exists(System.IO.Path.Combine(session.GitDirectory, "sequencer")))
            { await ExecuteAsync(["cherry-pick", "--quit"], CancellationToken.None).ConfigureAwait(false); }
            foreach (CherryPickSavedDirectory saved in session.Directories)
            { EnsureCherryPickPathSafety(saved.Path, allowDirectory: true); Directory.CreateDirectory(System.IO.Path.Combine(Path, saved.Path)); }
            foreach (CherryPickSavedPath saved in session.Files)
            {
                EnsureCherryPickPathSafety(saved.Path, saved.Gitlink);
                if (saved.Directory) { continue; } // Gitlink changes are index-only.
                string file = System.IO.Path.Combine(Path, saved.Path);
                if (File.Exists(file) || new FileInfo(file).LinkTarget is not null) { File.Delete(file); }
                if (!saved.Exists) { continue; }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                EnsureCherryPickPathSafety(saved.Path, saved.Gitlink);
                if (saved.Link is not null) { File.CreateSymbolicLink(file, saved.Link); }
                else
                {
                    await File.WriteAllBytesAsync(file, saved.Bytes!, CancellationToken.None).ConfigureAwait(false);
                    if (saved.Mode is UnixFileMode mode && !OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, mode); }
                }
            }
            foreach (string directory in session.NewDirectories)
            {
                EnsureCherryPickPathSafety(System.IO.Path.GetRelativePath(Path, directory), allowDirectory: true);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) { Directory.Delete(directory); }
            }
            foreach (CherryPickSavedDirectory saved in session.Directories)
            {
                EnsureCherryPickPathSafety(saved.Path, allowDirectory: true);
                if (saved.Mode is UnixFileMode mode && !OperatingSystem.IsWindows()) { File.SetUnixFileMode(System.IO.Path.Combine(Path, saved.Path), mode); }
            }
            foreach ((string name, byte[]? bytes) in session.Metadata)
            {
                string file = System.IO.Path.Combine(session.GitDirectory, name);
                EnsureCherryPickMetadataSafety(session.GitDirectory); EnsureCherryPickMetadataFileSafety(file);
                if (bytes is null) { File.Delete(file); }
                else { await File.WriteAllBytesAsync(file, bytes, CancellationToken.None).ConfigureAwait(false); }
            }
            if (session.SharedIndex is not null && !File.Exists(session.SharedIndex))
            {
                EnsureCherryPickMetadataSafety(session.GitDirectory); EnsureCherryPickMetadataFileSafety(session.SharedIndex);
                await using FileStream shared = new(session.SharedIndex, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await shared.WriteAsync(session.SharedBytes!, CancellationToken.None).ConfigureAwait(false);
            }
            if (session.Index is not null) { await locked.WriteAsync(session.Index, CancellationToken.None).ConfigureAwait(false); }
            await locked.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await locked.DisposeAsync().ConfigureAwait(false);
            EnsureCherryPickMetadataSafety(session.GitDirectory); EnsureCherryPickMetadataFileSafety(index);
            if (session.Index is null) { File.Delete(index); File.Delete(indexLock); }
            else { File.Move(indexLock, index, overwrite: true); }
            ownsLock = false;
        }
        finally { await locked.DisposeAsync().ConfigureAwait(false); if (ownsLock) { File.Delete(indexLock); } }
    }

    private async Task ValidateCherryPickSessionIdentityAsync(GitCherryPickSession session)
    {
        GitRepositoryInfo currentInfo = await DiscoverRepositoryAsync(Path, _runner, CancellationToken.None).ConfigureAwait(false);
        if (currentInfo.RootPath != session.Root || currentInfo.GitDirectory != session.GitDirectory || currentInfo.CommonDirectory != session.CommonDirectory)
        { throw new InvalidOperationException("The worktree or its Git metadata changed; recovery leaves the repository intact."); }
        await ValidateCherryPickNativeOwnershipAsync(session).ConfigureAwait(false);
        GitResult reference = await ExecuteAsync(["symbolic-ref", "--quiet", "HEAD"], CancellationToken.None, allowedExitCodes: [1]).ConfigureAwait(false);
        if ((reference.Success ? reference.StandardOutput.TrimEnd('\r', '\n') : null) != session.Reference)
        { throw new InvalidOperationException("The checked-out branch changed outside the cherry-pick; recovery cannot overwrite it."); }
        string current = await ResolveRevisionAsync("HEAD", CancellationToken.None).ConfigureAwait(false);
        if (current != session.OriginalHead)
        { throw new InvalidOperationException("HEAD changed during the cherry-pick; its ownership cannot be proven. Recovery leaves the current refs, index and files intact."); }
    }

    private async Task ValidateCherryPickRecoveryCoverageAsync(GitCherryPickSession session)
    {
        HashSet<string> covered = new(session.Files.Select(saved => saved.Path), StringComparer.Ordinal);
        GitStatus current = await ReadCherryPickStatusAsync(CancellationToken.None).ConfigureAwait(false);
        string[] unknown = current.Changes
            .Where(change => !(change.IsUntracked && session.UntrackedBefore.Contains(change.Path)))
            .SelectMany(change => change.OriginalPath is string original ? new[] { change.Path, original } : new[] { change.Path })
            .Where(path => !covered.Contains(path)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
        {
            string display = string.Join(", ", unknown.Select(path => path.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)));
            throw new InvalidOperationException("Cherry-pick changed paths outside its recovery snapshot: " + display
                + ". Recovery cannot safely restore them; the current refs, index, operation metadata and files have been left intact.");
        }
    }

    private void EnsureCherryPickPathSafety(string name, bool allowDirectory)
    {
        EnsureCherryPickMetadataSafety(Path);
        string relative = ValidatePath(name);
        if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new IOException("Cherry-pick recovery cannot leave its working tree."); }
        string[] components = relative.Split(System.IO.Path.DirectorySeparatorChar);
        if (components.Any(component => component.Equals(".git", StringComparison.OrdinalIgnoreCase))
            || new DirectoryInfo(Path).LinkTarget is not null)
        { throw new IOException("Cherry-pick cannot traverse repository metadata or a symbolic-link root."); }
        string current = Path;
        for (int index = 0; index < components.Length; index++)
        {
            current = System.IO.Path.Combine(current, components[index]);
            bool last = index == components.Length - 1;
            if (!last && (new DirectoryInfo(current).LinkTarget is not null || File.Exists(current)))
            { throw new IOException("Cherry-pick cannot traverse a symbolic link or non-directory parent."); }
            if (last && Directory.Exists(current) && new FileInfo(current).LinkTarget is null && !allowDirectory)
            { throw new IOException("Cherry-pick cannot replace a directory that is not a Git submodule."); }
        }
    }

    private static void EnsureCherryPickMetadataSafety(string directory)
    {
        for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
        {
            if (parent.LinkTarget is not null) { throw new IOException("Cherry-pick metadata has a symbolic-link parent."); }
        }
    }

    private static void EnsureCherryPickMetadataFileSafety(string file)
    {
        if (new FileInfo(file).LinkTarget is not null || Directory.Exists(file))
        { throw new IOException("Cherry-pick metadata was replaced by a symbolic link or directory."); }
    }

    private async Task<GitResult> CherryPickCommittedAsync(string revision, CancellationToken cancellationToken)
    {
        GitCherryPickResult result = await CherryPickAsync(new GitCherryPickOptions(revision, AutoCommit: true), cancellationToken).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(["cherry-pick", "--", result.Session.Options.Revision], result.CommandResult); }
        return result.CommandResult;
    }
}
