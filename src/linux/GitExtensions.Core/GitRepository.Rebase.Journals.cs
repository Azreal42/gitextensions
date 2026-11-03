using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private static readonly string[] RebaseJournalNames = ["gitextensions-rebase", GitRebaseEditor.NativeStateDirectoryName];
    private sealed record RebaseJournalInspection(NativeRebaseEditorState? Native, RebaseEditorState? Legacy,
        IReadOnlyList<string> Paths, string? Error);

    private static bool RebaseJournalEntryExists(string path)
        => new FileInfo(path).LinkTarget is not null || File.Exists(path) || Directory.Exists(path);

    private static RebaseJournalInspection InspectRebaseJournals(string gitDirectory)
    {
        NativeRebaseEditorState? native = null;
        RebaseEditorState? legacy = null;
        List<string> paths = [], errors = [];
        foreach (string name in RebaseJournalNames)
        {
            string directory = System.IO.Path.Combine(gitDirectory, name);
            if (!RebaseJournalEntryExists(directory)) { continue; }
            paths.Add(directory);
            try
            {
                ValidateRebaseJournalEntries(directory);
                if (name == GitRebaseEditor.NativeStateDirectoryName)
                {
                    NativeRebaseEditorState state = GitRebaseEditor.ReadNativeState(directory);
                    if (!GitRebaseEditor.SamePath(state.GitDirectory, gitDirectory) || state.SkippedCommits is null
                        || state.SkippedCommits.Any(hash => hash is null || !IsRebaseHash(hash)))
                    { throw new IOException("The native rebase journal has an invalid worktree identity or skipped-commit list."); }
                    // Older non-UTF8 native scripts can be mixed or recoded by Git's
                    // cumulative pretty formatter. A live Apply backend needs no todo;
                    // once absent, keep the undeclared journal rather than infer its origin.
                    bool activeApply = Directory.Exists(System.IO.Path.Combine(gitDirectory, "rebase-apply"))
                        && !Directory.Exists(System.IO.Path.Combine(gitDirectory, "rebase-merge"));
                    if (GitRebaseEditor.NativeTodoEncodingName(state) is null && !activeApply)
                    { throw new NotSupportedException("This older native journal does not declare a reliable todo codec. Its bytes are preserved; Abort remains available while Git is active."); }
                    native = state;
                }
                else
                {
                    RebaseEditorState state = GitRebaseEditor.ReadState(directory);
                    if (!GitRebaseEditor.SamePath(state.DonePath, System.IO.Path.Combine(gitDirectory, "rebase-merge", "done")) || state.Messages is null
                        || state.Messages.Any(message => !IsRebaseHash(message.Key) || message.Value is null))
                    { throw new IOException("The legacy rebase journal has an invalid worktree identity or saved-message list."); }
                    _ = GitRebaseEditor.GetStrictEncoding(state.EncodingName);
                    legacy = state;
                }
            }
            catch (Exception error) when (IsRebaseJournalError(error))
            { errors.Add(directory + ": " + error.Message); }
        }
        if (native is not null && legacy is not null)
        { errors.Add("Native and legacy rebase journals both exist; their session identity is ambiguous."); }
        return new(native, legacy, paths, errors.Count == 0 ? null : string.Join("\n", errors));
    }

    private static bool IsRebaseJournalError(Exception error)
        => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or NotSupportedException or FormatException or JsonException or GitCommandException;

    private static void ValidateRebaseJournalEntries(string directory)
    {
        EnsureRebaseMetadataSafety(directory);
        if (!Directory.Exists(directory)) { throw new IOException("The rebase journal is not a directory."); }
        string statePath = System.IO.Path.Combine(directory, "state.json");
        EnsureRebaseMetadataFileSafety(statePath);
        if (!File.Exists(statePath)) { throw new IOException("The rebase journal has no state.json; its ownership cannot be verified."); }
        bool legacy = System.IO.Path.GetFileName(directory) == "gitextensions-rebase";
        foreach (string file in Directory.GetFileSystemEntries(directory))
        {
            EnsureRebaseMetadataFileSafety(file);
            string filename = System.IO.Path.GetFileName(file);
            if (filename != "state.json" && !(legacy && filename == "todo") && !IsRebaseJournalTemporary(filename))
            { throw new IOException("Unknown rebase journal contents have been preserved: " + filename); }
        }
    }

    private static bool IsRebaseJournalTemporary(string filename)
        => filename.StartsWith("state-", StringComparison.Ordinal) && filename.EndsWith(".tmp", StringComparison.Ordinal)
            && Guid.TryParseExact(filename[6..^4], "N", out _);

    private async Task<IReadOnlyList<string>> CleanupRebaseJournalsWithWarningsAsync(RebaseLease lease)
    {
        if (HasRebaseBackend(lease)) { return []; }
        RebaseJournalInspection journals = InspectRebaseJournals(lease.GitDirectory);
        if (journals.Error is not null) { return ["Rebase journal preserved: " + journals.Error]; }
        try { await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false); return []; }
        catch (Exception error) when (IsRebaseJournalError(error))
        { return ["Rebase journal cleanup was incomplete; remaining metadata has been preserved: " + error.Message]; }
    }

    /// <summary>Explicitly preserve orphaned journal entries without interpreting or following their contents.</summary>
    public Task<IReadOnlyList<GitRebasePreservedJournal>> PreserveOrphanedRebaseJournalsAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync<IReadOnlyList<GitRebasePreservedJournal>>(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            using RebaseLease lease = await AcquireRebaseLeaseAsync(token).ConfigureAwait(false);
            if (HasRebaseBackend(lease)) { throw new InvalidOperationException("An active Git rebase must be completed or aborted before preserving its journal."); }
            // Validate only the trusted parent. A canonical entry may itself be a file,
            // directory or dangling link: the rename preserves that entry, never its target.
            EnsureRebaseMetadataSafety(lease.GitDirectory);
            string[] paths = RebaseJournalNames.Select(name => System.IO.Path.Combine(lease.GitDirectory, name))
                .Where(RebaseJournalEntryExists).ToArray();
            if (paths.Length == 0) { return []; }
            token.ThrowIfCancellationRequested();
            List<GitRebasePreservedJournal> preserved = [];
            foreach (string path in paths)
            {
                EnsureRebaseMetadataSafety(lease.GitDirectory);
                if (HasRebaseBackend(lease)) { throw new IOException("A Git rebase became active; its remaining journal has been preserved in place."); }
                string destination = path + "-preserved-" + Guid.NewGuid().ToString("N");
                // The native rename moves the entry itself, including dangling links,
                // and atomically refuses an independently created destination.
                if (!TryRenameRebaseJournalEntry(path, destination))
                {
                    int errno = Marshal.GetLastPInvokeError();
                    string completed = string.Join("; ", preserved.Select(entry => entry.PreservedPath));
                    throw new IOException("Unable to preserve rebase journal " + path + ". Already preserved: " + completed,
                        new Win32Exception(errno));
                }
                preserved.Add(new(path, destination));
            }
            return preserved;
        }, cancellationToken);

    private static bool TryRenameRebaseJournalEntry(string path, string destination)
    {
        if (OperatingSystem.IsLinux()) { return RenameRebaseJournalEntryLinux(-100, path, -100, destination, 1) == 0; } // RENAME_NOREPLACE
        if (OperatingSystem.IsMacOS()) { return RenameRebaseJournalEntryMac(path, destination, 4) == 0; } // RENAME_EXCL
        if (OperatingSystem.IsWindows())
        {
            // No REPLACE_EXISTING or COPY_ALLOWED: rename on the same volume only.
            return RenameRebaseJournalEntryWindows(WindowsPath(path), WindowsPath(destination), 0);
        }
        throw new PlatformNotSupportedException("Rebase journal preservation requires Windows, Linux or macOS.");

        static string WindowsPath(string value)
        {
            string full = System.IO.Path.GetFullPath(value);
            return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
                : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        }
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameRebaseJournalEntryLinux(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameRebaseJournalEntryMac([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RenameRebaseJournalEntryWindows(string oldPath, string newPath, uint flags);
}
