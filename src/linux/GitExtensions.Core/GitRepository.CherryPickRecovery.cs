using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux;

public enum GitCherryPickRecoveryState { Absent, Pending, Invalid }

/// <summary>A managed pick can be recovered without its original repository or dialogue instance.</summary>
public sealed record GitCherryPickRecovery(GitCherryPickRecoveryState State, GitCherryPickSession? Session,
    IReadOnlyList<string> ConflictedPaths, string? Error)
{
    public bool CanContinue => State == GitCherryPickRecoveryState.Pending && Session is { IsRestoring: false } && ConflictedPaths.Count == 0;
    internal Exception? Cause { get; init; }
}

internal sealed record CherryPickSavedObject(string Hash, string Type, byte[] Bytes);
internal sealed record CherryPickSavedDirectory(string Path, UnixFileMode? Mode);
internal sealed record CherryPickRecoveryEnvelope(int Version, byte[] Payload, string Digest);
internal sealed record CherryPickRecoveryData(string Id, string Phase, GitCherryPickOptions Options, string Root, string GitDirectory,
    string CommonDirectory, string OriginalHead, string? Reference, byte[]? Index, string? SharedIndex, byte[]? SharedBytes,
    CherryPickSavedPath[] Files, string[] NewDirectories, string[] UntrackedBefore, Dictionary<string, byte[]?> Metadata,
    CherryPickSavedObject[] Objects, CherryPickSavedDirectory[] Directories);

public sealed partial class GitRepository
{
    private const string CherryPickRecoveryDirectoryName = "gitextensions-cherry-pick";
    private const string CherryPickRecoveryManifestName = "recovery.json";
    private static readonly UTF8Encoding CherryPickRecoveryUtf8 = new(false, true);
    private static string CherryPickRecoveryDirectory(string gitDirectory) => System.IO.Path.Combine(gitDirectory, CherryPickRecoveryDirectoryName);

    public async Task<GitCherryPickRecovery> GetCherryPickRecoveryAsync(CancellationToken cancellationToken = default)
    {
        GitRepositoryInfo info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        string directory = CherryPickRecoveryDirectory(info.GitDirectory);
        if (!Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is null && !File.Exists(directory))
        { return new(GitCherryPickRecoveryState.Absent, null, [], null); }
        try
        {
            EnsureCherryPickMetadataSafety(directory);
            if (File.Exists(directory)) { throw new IOException("The recovery directory is not a directory."); }
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            { throw new IOException("The recovery directory must be private (0700)."); }
            string manifest = System.IO.Path.Combine(directory, CherryPickRecoveryManifestName);
            EnsureCherryPickMetadataFileSafety(manifest);
            if (!File.Exists(manifest)) { throw new IOException("The recovery snapshot is incomplete; its manifest is missing."); }
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(manifest) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            { throw new IOException("The recovery snapshot must be private (0600)."); }
            byte[] bytes = await File.ReadAllBytesAsync(manifest, cancellationToken).ConfigureAwait(false);
            CherryPickRecoveryEnvelope envelope = JsonSerializer.Deserialize<CherryPickRecoveryEnvelope>(bytes) ?? throw new IOException("The recovery manifest is empty.");
            if (envelope.Version != 1 || envelope.Payload is null || envelope.Digest != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
            { throw new IOException("The recovery manifest version or checksum is invalid."); }
            CherryPickRecoveryData data = JsonSerializer.Deserialize<CherryPickRecoveryData>(envelope.Payload) ?? throw new IOException("The recovery snapshot is empty.");
            ValidateCherryPickRecoveryData(data, info);
            if (data.Phase is "Completed" or "Aborted") { return new(GitCherryPickRecoveryState.Absent, null, [], null); }
            GitCherryPickSession session = new(data.Options, data.Root, data.GitDirectory, data.OriginalHead, data.Reference, data.Index,
                data.SharedIndex, data.SharedBytes, data.Files, data.NewDirectories, new HashSet<string>(data.UntrackedBefore, StringComparer.Ordinal), data.Metadata)
            { RecoveryId = data.Id, RecoveryPhase = data.Phase, CommonDirectory = data.CommonDirectory, Objects = data.Objects, Directories = data.Directories };
            IReadOnlyList<string> conflicts = await CherryPickConflictsAsync(cancellationToken).ConfigureAwait(false);
            await ValidateCherryPickSessionIdentityAsync(session).ConfigureAwait(false);
            return new(GitCherryPickRecoveryState.Pending, session, conflicts, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or GitCommandException)
        {
            return new(GitCherryPickRecoveryState.Invalid, null, [], "Cherry-pick recovery cannot be loaded safely: " + exception.Message
                + " The saved recovery data and current repository have been left intact.") { Cause = exception };
        }
    }

    private void ValidateCherryPickRecoveryData(CherryPickRecoveryData data, GitRepositoryInfo info)
    {
        if (!Guid.TryParseExact(data.Id, "N", out _) || data.Phase is not ("Prepared" or "Pending" or "Restoring" or "Completed" or "Aborted")
            || data.Root != Path || data.GitDirectory != info.GitDirectory || data.CommonDirectory != info.CommonDirectory
            || !IsCherryPickObjectId(data.OriginalHead) || data.Options is null || !IsCherryPickObjectId(data.Options.Revision)
            || data.Files is null || data.NewDirectories is null || data.UntrackedBefore is null || data.Metadata is null || data.Objects is null || data.Directories is null
            || data.Metadata.Count != CherryPickMetadata.Length || CherryPickMetadata.Any(name => !data.Metadata.ContainsKey(name)))
        { throw new IOException("The recovery snapshot does not describe this worktree or is incomplete."); }
        if ((data.SharedIndex is null) != (data.SharedBytes is null)
            || data.SharedIndex is not null && (System.IO.Path.GetDirectoryName(data.SharedIndex) != info.GitDirectory || !System.IO.Path.GetFileName(data.SharedIndex).StartsWith("sharedindex.", StringComparison.Ordinal)))
        { throw new IOException("The saved shared index is invalid."); }
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (CherryPickSavedPath saved in data.Files)
        {
            if (saved is null || !CherryPickSavedPathIsRelative(saved.Path) || !paths.Add(saved.Path)
                || saved.Exists && !saved.Directory && saved.Link is null && saved.Bytes is null)
            { throw new IOException("The saved file preimages are incomplete."); }
            EnsureCherryPickPathSafety(saved.Path, saved.Gitlink);
        }
        foreach (string directory in data.NewDirectories)
        {
            string relative = System.IO.Path.GetRelativePath(Path, directory);
            if (!CherryPickSavedPathIsRelative(relative) || System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, ValidatePath(relative))) != directory)
            { throw new IOException("The saved parent directory is outside this worktree."); }
            EnsureCherryPickPathSafety(relative, allowDirectory: true);
        }
        foreach (CherryPickSavedDirectory directory in data.Directories)
        { if (directory is null || !CherryPickSavedPathIsRelative(directory.Path)) { throw new IOException("The saved parent directory is missing or invalid."); } EnsureCherryPickPathSafety(directory.Path, allowDirectory: true); }
        foreach (CherryPickSavedObject saved in data.Objects)
        {
            if (saved is null || saved.Bytes is null || saved.Type is not ("blob" or "tree") || !IsCherryPickObjectId(saved.Hash) || CherryPickObjectHash(saved.Type, saved.Bytes, saved.Hash.Length) != saved.Hash)
            { throw new IOException("A saved index object is missing or has an invalid checksum."); }
        }
    }

    private bool CherryPickSavedPathIsRelative(string path)
    {
        string relative = ValidatePath(path);
        return !System.IO.Path.IsPathRooted(path) && relative is not ("." or "..") && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && (path == relative || OperatingSystem.IsWindows() && path == relative.Replace('\\', '/'));
    }

    private static bool IsCherryPickObjectId(string? value) => value is { Length: 40 or 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string CherryPickObjectHash(string type, byte[] bytes, int length)
    {
        byte[] header = Encoding.ASCII.GetBytes(type + " " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        using IncrementalHash hash = IncrementalHash.CreateHash(length == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(header); hash.AppendData(bytes); return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static FileStream AcquireCherryPickRecoveryLock(GitRepositoryInfo info)
    {
        EnsureCherryPickMetadataSafety(info.GitDirectory);
        string path = System.IO.Path.Combine(info.GitDirectory, CherryPickRecoveryDirectoryName + ".lock");
        EnsureCherryPickMetadataFileSafety(path);
        // An OS lease survives as an empty administrative file, but its lock is
        // released by process death. A CreateNew marker alone would strand recovery.
        FileStream stream = new(path, CherryPickPrivateFileOptions(FileMode.OpenOrCreate));
        try { if (!OperatingSystem.IsMacOS()) { stream.Lock(0, 1); } return stream; }
        catch { stream.Dispose(); throw; }
    }

    private async Task RequireNoCherryPickRecoveryAsync(CancellationToken token)
    {
        GitCherryPickRecovery recovery = await GetCherryPickRecoveryAsync(token).ConfigureAwait(false);
        if (recovery.State != GitCherryPickRecoveryState.Absent)
        { throw new InvalidOperationException(recovery.Error ?? "Reopen the pending cherry-pick recovery before starting another pick."); }
        string directory = CherryPickRecoveryDirectory((await GetInfoAsync(token).ConfigureAwait(false)).GitDirectory);
        if (Directory.Exists(directory)) { DeleteCherryPickRecoveryDirectory(directory); }
    }

    private async Task ValidateCherryPickJournalSessionAsync(GitCherryPickSession session, CancellationToken token)
    {
        GitCherryPickRecovery current = await GetCherryPickRecoveryAsync(token).ConfigureAwait(false);
        if (current.Cause is Exception cause) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw(); }
        if (current.State != GitCherryPickRecoveryState.Pending || current.Session?.RecoveryId != session.RecoveryId)
        { throw new InvalidOperationException(current.Error ?? "This recovery no longer owns the pending cherry-pick."); }
    }

    private static void CreateCherryPickPrivateDirectory(string directory)
    {
        EnsureCherryPickMetadataSafety(directory);
        if (File.Exists(directory)) { throw new IOException("A file replaced the recovery directory."); }
        if (Directory.Exists(directory) && !OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        { throw new IOException("The recovery directory must remain private (0700)."); }
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private static FileStreamOptions CherryPickPrivateFileOptions(FileMode mode)
    {
        FileStreamOptions options = new() { Mode = mode, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return options;
    }

    private async Task SaveCherryPickRecoveryAsync(GitCherryPickSession session, CancellationToken token)
    {
        string directory = CherryPickRecoveryDirectory(session.GitDirectory); CreateCherryPickPrivateDirectory(directory);
        CherryPickRecoveryData data = new(session.RecoveryId, session.RecoveryPhase, session.Options, session.Root, session.GitDirectory, session.CommonDirectory,
            session.OriginalHead, session.Reference, session.Index, session.SharedIndex, session.SharedBytes, session.Files.ToArray(), session.NewDirectories.ToArray(),
            session.UntrackedBefore.ToArray(), new Dictionary<string, byte[]?>(session.Metadata, StringComparer.Ordinal), session.Objects.ToArray(), session.Directories.ToArray());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new CherryPickRecoveryEnvelope(1, payload, Convert.ToHexString(SHA256.HashData(payload))));
        string manifest = System.IO.Path.Combine(directory, CherryPickRecoveryManifestName), temporary = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        EnsureCherryPickMetadataFileSafety(manifest);
        try
        {
            await using (FileStream stream = new(temporary, CherryPickPrivateFileOptions(FileMode.CreateNew)))
            { await stream.WriteAsync(bytes, token).ConfigureAwait(false); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); EnsureCherryPickMetadataSafety(directory); EnsureCherryPickMetadataFileSafety(manifest);
            File.Move(temporary, manifest, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private async Task FinishCherryPickRecoveryAsync(GitCherryPickSession session, string phase)
    {
        session.RecoveryPhase = phase;
        await SaveCherryPickRecoveryAsync(session, CancellationToken.None).ConfigureAwait(false);
        DeleteCherryPickRecoveryDirectory(CherryPickRecoveryDirectory(session.GitDirectory));
    }

    private static void DeleteCherryPickRecoveryDirectory(string directory)
    {
        EnsureCherryPickMetadataSafety(directory);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { EnsureCherryPickMetadataFileSafety(file); EnsureCherryPickMetadataSafety(System.IO.Path.GetDirectoryName(file)!); }
        foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)) { EnsureCherryPickMetadataSafety(child); }
        Directory.Delete(directory, recursive: true);
    }

    private async Task ValidateCherryPickNativeOwnershipAsync(GitCherryPickSession session)
    {
        string directory = session.GitDirectory;
        EnsureCherryPickMetadataSafety(directory);
        if (new[] { "MERGE_HEAD", "REVERT_HEAD" }.Any(name => File.Exists(System.IO.Path.Combine(directory, name)))
            || new[] { "sequencer", "rebase-merge", "rebase-apply" }.Any(name => Directory.Exists(System.IO.Path.Combine(directory, name))))
        { throw new InvalidOperationException("Another repository operation owns the current index; cherry-pick recovery cannot replace it."); }
        string marker = System.IO.Path.Combine(directory, "CHERRY_PICK_HEAD"); EnsureCherryPickMetadataFileSafety(marker);
        if (File.Exists(marker) && (await File.ReadAllTextAsync(marker).ConfigureAwait(false)).TrimEnd('\r', '\n') != session.Options.Revision)
        { throw new InvalidOperationException("CHERRY_PICK_HEAD belongs to another pick; the current repository has been left intact."); }
        if (session.Options.AutoCommit && session.RecoveryPhase == "Pending" && !File.Exists(marker))
        { throw new InvalidOperationException("The native cherry-pick marker disappeared; ownership of this recovery cannot be proven."); }
    }

    private async Task<IReadOnlyList<CherryPickSavedObject>> CaptureCherryPickIndexObjectsAsync(GitCherryPickSession session, CancellationToken token)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-cherry-index-" + Guid.NewGuid().ToString("N"));
        CreateCherryPickPrivateDirectory(directory);
        try
        {
            string copiedIndex = System.IO.Path.Combine(directory, "index"), objects = System.IO.Path.Combine(directory, "objects");
            CreateCherryPickPrivateDirectory(objects);
            if (session.Index is not null) { await File.WriteAllBytesAsync(copiedIndex, session.Index, token).ConfigureAwait(false); }
            Dictionary<string, string?> environment = new() { ["GIT_INDEX_FILE"] = copiedIndex, ["GIT_OBJECT_DIRECTORY"] = objects,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = QuoteCherryPickAlternate(System.IO.Path.Combine(session.CommonDirectory, "objects")), ["GIT_OPTIONAL_LOCKS"] = "0" };
            await ExecuteAsync(["update-index", "--no-split-index"], token, environment: environment).ConfigureAwait(false);
            string tree = (await ExecuteAsync(["write-tree"], token, environment: environment).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
            string headTree = (await ExecuteAsync(["rev-parse", "HEAD^{tree}"], token).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
            HashSet<string> existing = new((await ReadCherryPickTreeObjectsAsync(headTree, token, null).ConfigureAwait(false)).Select(item => item.Hash), StringComparer.Ordinal) { headTree };
            List<(string Hash, string Type)> original = [.. await ReadCherryPickTreeObjectsAsync(tree, token, environment).ConfigureAwait(false), (tree, "tree")];
            List<CherryPickSavedObject> saved = [];
            foreach ((string hash, string type) in original.Distinct().Where(item => !existing.Contains(item.Hash)))
            {
                byte[] bytes = (await CherryPickBinaryAsync(["cat-file", type, hash], token, environment: environment).ConfigureAwait(false)).StandardOutput;
                saved.Add(new(hash, type, bytes));
            }
            return saved;
        }
        finally { DeleteCherryPickRecoveryDirectory(directory); }
    }

    private async Task<IReadOnlyList<(string Hash, string Type)>> ReadCherryPickTreeObjectsAsync(string tree, CancellationToken token, IReadOnlyDictionary<string, string?>? environment)
    {
        string[] entries = CherryPickRecoveryUtf8.GetString((await CherryPickBinaryAsync(["ls-tree", "-r", "-t", "-z", tree], token, environment: environment).ConfigureAwait(false)).StandardOutput).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<(string, string)> objects = [];
        foreach (string entry in entries)
        {
            int tab = entry.IndexOf('\t'); if (tab < 0) { throw new FormatException("Invalid original index tree record."); }
            string[] fields = entry[..tab].Split(' ');
            if (fields.Length != 3) { throw new FormatException("Invalid original index tree object."); }
            if (fields[1] is "tree" or "blob") { objects.Add((fields[2], fields[1])); }
        }
        return objects;
    }

    private async Task RestoreCherryPickIndexObjectsAsync(GitCherryPickSession session)
    {
        foreach (CherryPickSavedObject saved in session.Objects)
        {
            if (CherryPickObjectHash(saved.Type, saved.Bytes, saved.Hash.Length) != saved.Hash) { throw new IOException("The original index object checksum is invalid."); }
            GitResult present = await ExecuteAsync(["cat-file", "-e", saved.Hash], CancellationToken.None, allowedExitCodes: [1, 128]).ConfigureAwait(false);
            if (present.Success) { continue; }
            string restored = CherryPickRecoveryUtf8.GetString((await CherryPickBinaryAsync(["hash-object", "-w", "-t", saved.Type, "--stdin"], CancellationToken.None, saved.Bytes).ConfigureAwait(false)).StandardOutput).TrimEnd('\r', '\n');
            if (restored != saved.Hash) { throw new IOException("Git could not restore the original index object identity."); }
        }
    }

    private async Task<GitBinaryResult> CherryPickBinaryAsync(IReadOnlyList<string> arguments, CancellationToken token, byte[]? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token, input, environment).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, new(result.ExitCode, "", result.StandardError)); }
        return result;
    }

    private static string QuoteCherryPickAlternate(string path)
    {
        StringBuilder quoted = new("\"");
        foreach (byte character in Encoding.UTF8.GetBytes(path))
        {
            if (character is (byte)'\\' or (byte)'\"') { quoted.Append('\\').Append((char)character); }
            else if (character < 32 || character > 126) { quoted.Append('\\').Append(Convert.ToString(character, 8).PadLeft(3, '0')); }
            else { quoted.Append((char)character); }
        }
        return quoted.Append('"').ToString();
    }

    private static void AddCherryPickDirectoryDestinations(HashSet<string> paths, IReadOnlySet<string> sourcePaths, string original, string destination)
    {
        // Keep every plausible mapping; Git decides directory moves using all its
        // detected file renames. A pick adding old/new.txt need not touch old/seed.txt.
        string? sourceDirectory = CherryPickGitParent(original);
        string? destinationDirectory = CherryPickGitParent(destination);
        while (!string.IsNullOrEmpty(sourceDirectory))
        {
            if (sourceDirectory != destinationDirectory)
            {
                string prefix = sourceDirectory + '/';
                foreach (string source in sourcePaths.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    string candidate = string.IsNullOrEmpty(destinationDirectory) ? source[prefix.Length..] : destinationDirectory + '/' + source[prefix.Length..];
                    paths.Add(candidate);
                }
            }
            sourceDirectory = CherryPickGitParent(sourceDirectory);
            destinationDirectory = string.IsNullOrEmpty(destinationDirectory) ? null : CherryPickGitParent(destinationDirectory);
        }
    }
    private static string? CherryPickGitParent(string path)
    { int separator = path.LastIndexOf('/'); return separator >= 0 ? path[..separator] : null; }
}
