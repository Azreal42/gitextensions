namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private Task<GitResult> StartSimpleRebaseAsync(string revision, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            using RebaseLease lease = await AcquireRebaseLeaseAsync(token).ConfigureAwait(false);
            await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false);
            try { return await ExecuteAsync(["rebase", "--", revision], token).ConfigureAwait(false); }
            finally { await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false); }
        }, cancellationToken);

    private sealed class RebaseLease(string gitDirectory, FileStream stream) : IDisposable
    {
        internal string GitDirectory { get; } = gitDirectory;
        public void Dispose() => stream.Dispose();
    }

    private async Task<RebaseLease> AcquireRebaseLeaseAsync(CancellationToken token)
    {
        GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
        string path = System.IO.Path.Combine(info.GitDirectory, "gitextensions-rebase.lock");
        EnsureRebaseMetadataSafety(info.GitDirectory);
        EnsureRebaseMetadataFileSafety(path);
        token.ThrowIfCancellationRequested();
        FileStreamOptions options = new() { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        FileStream stream = new(path, options);
        try
        {
            if (!OperatingSystem.IsMacOS()) { stream.Lock(0, 1); }
            // Leave the administrative file in place: unlinking a lock permits
            // another instance to lock a new inode while the old lease is live.
            return new(info.GitDirectory, stream);
        }
        catch { stream.Dispose(); throw; }
    }

    private static bool HasRebaseBackend(RebaseLease lease)
        => Directory.Exists(System.IO.Path.Combine(lease.GitDirectory, "rebase-merge"))
            || Directory.Exists(System.IO.Path.Combine(lease.GitDirectory, "rebase-apply"));

    private async Task CleanupObsoleteRebaseJournalsAsync(RebaseLease lease)
    {
        if (HasRebaseBackend(lease)) { return; }
        RebaseJournalInspection journals = InspectRebaseJournals(lease.GitDirectory);
        if (journals.Error is not null) { throw new IOException("An invalid rebase journal has been preserved: " + journals.Error); }
        foreach (string name in new[] { "gitextensions-rebase", GitRebaseEditor.NativeStateDirectoryName })
        { await CleanupRebaseStateAsync(System.IO.Path.Combine(lease.GitDirectory, name), lease).ConfigureAwait(false); }
    }

    private static Task CleanupRebaseStateAsync(string directory, RebaseLease lease, bool createdByThisCommand = false)
    {
        string name = System.IO.Path.GetFileName(directory);
        if (!GitRebaseEditor.SamePath(System.IO.Path.GetDirectoryName(directory), lease.GitDirectory)
            || name is not ("gitextensions-rebase" or GitRebaseEditor.NativeStateDirectoryName))
        { throw new IOException("The rebase journal is outside its leased worktree."); }
        EnsureRebaseMetadataSafety(directory);
        if (File.Exists(directory)) { throw new IOException("A file replaced the rebase journal directory."); }
        if (!Directory.Exists(directory) || HasRebaseBackend(lease)) { return Task.CompletedTask; }
        string statePath = System.IO.Path.Combine(directory, "state.json");
        EnsureRebaseMetadataFileSafety(statePath);
        if (File.Exists(statePath))
        {
            if (name == GitRebaseEditor.NativeStateDirectoryName)
            {
                NativeRebaseEditorState state = GitRebaseEditor.ReadNativeState(directory);
                if (!GitRebaseEditor.SamePath(state.GitDirectory, lease.GitDirectory))
                { throw new IOException("The native rebase journal belongs to another worktree."); }
            }
            else
            {
                RebaseEditorState state = GitRebaseEditor.ReadState(directory);
                if (!GitRebaseEditor.SamePath(state.DonePath, System.IO.Path.Combine(lease.GitDirectory, "rebase-merge", "done")))
                { throw new IOException("The legacy rebase journal belongs to another worktree."); }
                _ = GitRebaseEditor.GetStrictEncoding(state.EncodingName);
            }
        }
        else if (!createdByThisCommand)
        { throw new IOException("An incomplete rebase journal has been preserved; its ownership cannot be verified."); }
        string[] files = Directory.GetFileSystemEntries(directory);
        foreach (string file in files)
        {
            EnsureRebaseMetadataFileSafety(file);
            string filename = System.IO.Path.GetFileName(file);
            bool temporary = filename.StartsWith("state-", StringComparison.Ordinal)
                && filename.EndsWith(".tmp", StringComparison.Ordinal) && Guid.TryParseExact(filename[6..^4], "N", out _);
            if (filename != "state.json" && !(name == "gitextensions-rebase" && filename == "todo") && !temporary)
            { throw new IOException("Unknown rebase journal contents have been preserved: " + filename); }
        }
        // All metadata is validated before the first deletion, and the lease
        // remains held until Git and its editor have exited or been cancelled.
        foreach (string file in files)
        {
            EnsureRebaseMetadataSafety(directory); EnsureRebaseMetadataFileSafety(file);
            if (HasRebaseBackend(lease)) { throw new IOException("A rebase became active; its journal has been preserved."); }
            File.Delete(file);
        }
        EnsureRebaseMetadataSafety(directory);
        Directory.Delete(directory, recursive: false);
        return Task.CompletedTask;
    }

    private static void EnsureRebaseMetadataSafety(string directory)
    {
        for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
        { if (parent.LinkTarget is not null) { throw new IOException("Rebase metadata has a symbolic-link parent."); } }
    }

    private static void EnsureRebaseMetadataFileSafety(string file)
    {
        if (new FileInfo(file).LinkTarget is not null || Directory.Exists(file))
        { throw new IOException("Rebase metadata was replaced by a symbolic link or directory."); }
    }

    private static void CreatePrivateRebaseDirectory(string directory)
    {
        EnsureRebaseMetadataSafety(directory);
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
}
