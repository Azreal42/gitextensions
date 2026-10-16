using System.Text;
using IOPath = System.IO.Path;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<GitRulesFileSnapshot> GetRulesFileSnapshotAsync(GitRulesFileKind kind, string directory = "", string? encodingName = null,
        CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            (string path, string relative, string root) = await RulesFilePathAsync(kind, directory, token).ConfigureAwait(false);
            return await ReadRulesFileAsync(kind, relative, root, path, encodingName, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitRulesFileSnapshot> SaveRulesFileAsync(GitRulesFileSnapshot snapshot, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) { throw new ArgumentException("Rules file text cannot contain NUL.", nameof(text)); }
        // Encode the complete edit before any permission or filesystem mutation.
        bool createEmptyFile = !snapshot.Exists && snapshot.Kind is GitRulesFileKind.Attributes or GitRulesFileKind.MailMap;
        string content = (text != snapshot.Text || createEmptyFile) && !GitRulesFiles.EndsInNewLine(text) ? text + snapshot.NewLine : text;
        byte[] bytes = [.. snapshot.Bom, .. GitFileEncodingCodec.GetEncoding(snapshot.EncodingName).GetBytes(content)];
        return WithMutationAsync(async token =>
        {
            (string path, _, string root) = await RulesFilePathAsync(snapshot.Kind, snapshot.Directory, token).ConfigureAwait(false);
            if (path != snapshot.Path || root != snapshot.RepositoryRoot) { throw new ArgumentException("The rules file snapshot belongs to another repository.", nameof(snapshot)); }
            GitRulesFileSnapshot current = await ReadRulesFileAsync(snapshot.Kind, snapshot.Directory, root, path, snapshot.EncodingName, token).ConfigureAwait(false);
            CheckRulesSnapshot(snapshot, current);
            if (text == snapshot.Text && !createEmptyFile) { return current; }
            token.ThrowIfCancellationRequested();
            GitRulesIdentity? writtenIdentity = snapshot.Identity;
            if (snapshot.Exists) { await WriteExistingRulesFileAsync(snapshot, bytes, token).ConfigureAwait(false); }
            else
            {
                // CreateNew refuses a file that appeared since the read; no overwrite fallback is allowed.
                System.IO.Directory.CreateDirectory(IOPath.GetDirectoryName(snapshot.ResolvedPath)!);
                GitRulesFiles.CheckLocation(snapshot.Location, GitRulesFiles.Resolve(path), createdParents: true);
                token.ThrowIfCancellationRequested();
                await using FileStream file = new(snapshot.ResolvedPath, new FileStreamOptions
                { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous });
                writtenIdentity = GitRulesIdentity.ReadHandle(file.SafeFileHandle);
                await file.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // Once commit starts it is drained; cancellation cannot report an uncompleted write as harmless.
            GitRulesFileSnapshot saved = await ReadRulesFileAsync(snapshot.Kind, snapshot.Directory, root, path, snapshot.EncodingName, CancellationToken.None).ConfigureAwait(false);
            GitRulesFiles.CheckLocation(snapshot.Location, saved.Location, createdParents: !snapshot.Exists);
            if (saved.Identity != writtenIdentity || saved.ContentHash != GitRulesFiles.Hash(bytes)) { throw GitRulesFiles.Changed(); }
            return saved;
        }, cancellationToken);
    }

    private async Task<(string Path, string Directory, string Root)> RulesFilePathAsync(GitRulesFileKind kind, string directory, CancellationToken token)
    {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        ArgumentNullException.ThrowIfNull(directory);
        await EnsureWorktreeAsync(token).ConfigureAwait(false);
        string root = (await GetInfoAsync(token).ConfigureAwait(false)).RootPath;
        string full = IOPath.GetFullPath(directory.Length == 0 ? "." : directory, root);
        string relative = IOPath.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || IOPath.IsPathRooted(relative)
            || relative.Split(IOPath.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        { throw new ArgumentException("Choose a rules directory inside the working tree.", nameof(directory)); }
        if (relative == ".") { relative = ""; }
        if (relative.Length != 0 && kind is GitRulesFileKind.LocalExclude or GitRulesFileKind.MailMap)
        { throw new ArgumentException("Local exclusions and mailmap are edited at repository scope.", nameof(directory)); }
        string path = kind == GitRulesFileKind.LocalExclude ? await GetGitPathAsync("info/exclude", token).ConfigureAwait(false)
            : IOPath.Combine(full, kind == GitRulesFileKind.Ignore ? ".gitignore" : kind == GitRulesFileKind.Attributes ? ".gitattributes" : ".mailmap");
        return (path, relative, root);
    }

    private async Task<GitRulesFileSnapshot> ReadRulesFileAsync(GitRulesFileKind kind, string directory, string root, string path, string? encodingName, CancellationToken token)
    {
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? pathIdentity = GitRulesIdentity.ReadPath(location.Path);
        byte[] bytes = [];
        GitRulesIdentity? identity = null;
        UnixFileMode? mode = null;
        FileAttributes attributes = FileAttributes.Normal;
        if (pathIdentity is not null)
        {
            if (!pathIdentity.Value.IsRegular) { throw new IOException("The rules path is not a regular file: " + path); }
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            identity = GitRulesIdentity.ReadHandle(file.SafeFileHandle);
            if (identity != pathIdentity) { throw GitRulesFiles.Changed(); }
            using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false); bytes = buffer.ToArray();
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file.SafeFileHandle);
            attributes = File.GetAttributes(file.SafeFileHandle);
        }
        token.ThrowIfCancellationRequested();
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        string codec = encodingName ?? (identity is null ? "utf-8" : await GetFileEncodingNameAsync(token).ConfigureAwait(false));
        GitDecodedFile decoded = GitFileEncodingCodec.DecodeFile(bytes, codec);
        if (!decoded.IsLossless || decoded.IsBinary) { throw new DecoderFallbackException("The rules file cannot be decoded without losing bytes. Choose its actual file encoding and reload it."); }
        return new(kind, directory, root, path, location, identity is not null, GitRulesFiles.Hash(bytes), decoded.Text,
            decoded.EncodingName, GitRulesFiles.Bom(bytes), GitRulesFiles.NewLine(decoded.Text), identity, mode, attributes);
    }

    private static void CheckRulesSnapshot(GitRulesFileSnapshot expected, GitRulesFileSnapshot actual)
    {
        GitRulesFiles.CheckLocation(expected.Location, actual.Location);
        if (expected.Exists != actual.Exists || expected.Identity != actual.Identity || expected.ContentHash != actual.ContentHash
            || expected.Mode != actual.Mode || expected.Attributes != actual.Attributes) { throw GitRulesFiles.Changed(); }
    }

    private static async Task WriteExistingRulesFileAsync(GitRulesFileSnapshot snapshot, byte[] bytes, CancellationToken token)
    {
        await using FileStream observed = new(snapshot.ResolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (GitRulesIdentity.ReadHandle(observed.SafeFileHandle) != snapshot.Identity) { throw GitRulesFiles.Changed(); }
        bool changedMode = false;
        try
        {
            FileStream OpenWritable() => new(snapshot.ResolvedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
            FileStream writable;
            try { writable = OpenWritable(); }
            catch (UnauthorizedAccessException)
            {
                // Group/ACL access can already permit writing without ownership: do not require chmod first.
                if (!OperatingSystem.IsWindows() && snapshot.Mode is UnixFileMode mode && !mode.HasFlag(UnixFileMode.UserWrite))
                { File.SetUnixFileMode(observed.SafeFileHandle, mode | UnixFileMode.UserWrite); changedMode = true; }
                else if (OperatingSystem.IsWindows() && snapshot.Attributes.HasFlag(FileAttributes.ReadOnly))
                { File.SetAttributes(observed.SafeFileHandle, snapshot.Attributes & ~FileAttributes.ReadOnly); changedMode = true; }
                else { throw; }
                writable = OpenWritable();
            }
            await using FileStream file = writable;
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != snapshot.Identity) { throw GitRulesFiles.Changed(); }
            using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false);
            if (GitRulesFiles.Hash(buffer.ToArray()) != snapshot.ContentHash) { throw GitRulesFiles.Changed(); }
            GitRulesFiles.CheckLocation(snapshot.Location, GitRulesFiles.Resolve(snapshot.Path));
            token.ThrowIfCancellationRequested();
            file.Position = 0;
            await file.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            file.SetLength(bytes.Length);
            await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            GitRulesFiles.CheckLocation(snapshot.Location, GitRulesFiles.Resolve(snapshot.Path));
        }
        finally
        {
            if (changedMode)
            {
                if (!OperatingSystem.IsWindows() && snapshot.Mode is UnixFileMode mode) { File.SetUnixFileMode(observed.SafeFileHandle, mode); }
                else if (OperatingSystem.IsWindows()) { File.SetAttributes(observed.SafeFileHandle, snapshot.Attributes); }
            }
        }
    }
}
