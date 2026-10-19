namespace GitExtensions.Linux;

public interface IEditableFileStore
{
    Task<EditableFileSnapshot> ReadAsync(string fullPath, string defaultEncodingName = "utf-8", CancellationToken cancellationToken = default);
    Task<EditableFileSnapshot> SaveAsync(EditableFileSnapshot snapshot, string text, CancellationToken cancellationToken = default);
}

/// <summary>Existing regular files only. Optimistic identity/byte checks are not an interprocess transaction.</summary>
public sealed class EditableFileStore : IEditableFileStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Task<EditableFileSnapshot> ReadAsync(string fullPath, string defaultEncodingName = "utf-8", CancellationToken cancellationToken = default)
        => ReadCoreAsync(System.IO.Path.GetFullPath(fullPath), defaultEncodingName, cancellationToken);

    private static async Task<EditableFileSnapshot> ReadCoreAsync(string path, string encoding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity identity = GitRulesIdentity.ReadPath(location.Path) ?? throw new FileNotFoundException("Cannot open file:", path);
        if (!identity.IsRegular) { throw new IOException("The selected path is not a regular file: " + path); }
        await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw Changed(); }
        using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false);
        byte[] bytes = buffer.ToArray();
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file.SafeFileHandle);
        FileAttributes attributes = File.GetAttributes(file.SafeFileHandle);
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path)); token.ThrowIfCancellationRequested();
        return new(path, location, identity, bytes, GitFileEncodingCodec.DecodeFile(bytes, encoding), mode, attributes);
    }

    public async Task<EditableFileSnapshot> SaveAsync(EditableFileSnapshot snapshot, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.CanEdit) { throw new InvalidOperationException("This file cannot be saved as lossless text. Choose its actual encoding and reload it."); }
        if (text.Contains('\0')) { throw new ArgumentException("File text cannot contain NUL.", nameof(text)); }
        // Strict encoding finishes before opening writable handles or temporarily adjusting a readonly mode.
        byte[] bytes = [.. snapshot.Bom, .. GitFileEncodingCodec.GetEncoding(snapshot.EncodingName).GetBytes(text)];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool started = false;
        try
        {
            EditableFileSnapshot current = await ReadCoreAsync(snapshot.Path, snapshot.EncodingName, cancellationToken).ConfigureAwait(false);
            GitRulesFiles.CheckLocation(snapshot.Location, current.Location);
            if (snapshot.Identity != current.Identity || snapshot.ContentHash != current.ContentHash
                || snapshot.Mode != current.Mode || snapshot.Attributes != current.Attributes) { throw Changed(); }
            if (text == snapshot.Text) { return current; }
            EditableFileWriteBaseline baseline = new(snapshot.Path, snapshot.ResolvedPath, snapshot.Location,
                snapshot.Identity, snapshot.ContentHash, snapshot.Mode, snapshot.Attributes);
            await EditableFileIO.WriteExistingAsync(baseline, bytes, cancellationToken, Changed, () => started = true).ConfigureAwait(false);
            // The existing writer drains after its commit boundary. Verification must also finish after cancellation.
            EditableFileSnapshot saved = await ReadCoreAsync(snapshot.Path, snapshot.EncodingName, CancellationToken.None).ConfigureAwait(false);
            GitRulesFiles.CheckLocation(snapshot.Location, saved.Location);
            if (saved.Identity != snapshot.Identity || saved.ContentHash != GitRulesFiles.Hash(bytes)
                || saved.Mode != snapshot.Mode || saved.Attributes != snapshot.Attributes) { throw Changed(); }
            return saved;
        }
        catch (Exception error) when (started && error is not EditableFileWriteException)
        { throw new EditableFileWriteException(error); }
        finally { _gate.Release(); }
    }
    private static IOException Changed() => new("The file, its permissions, or its symbolic-link destination changed outside this editor. Reload it before saving.");
}

internal static class EditableFileIO
{
    internal static async Task WriteExistingAsync(EditableFileWriteBaseline snapshot, byte[] bytes, CancellationToken token,
        Func<IOException>? changed = null, Action? onWriteStarted = null)
    {
        Func<IOException> conflict = changed ?? (() => GitRulesFiles.Changed());
        await using FileStream observed = new(snapshot.ResolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (GitRulesIdentity.ReadHandle(observed.SafeFileHandle) != snapshot.Identity) { throw conflict(); }
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
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != snapshot.Identity) { throw conflict(); }
            using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false);
            if (GitRulesFiles.Hash(buffer.ToArray()) != snapshot.ContentHash) { throw conflict(); }
            GitRulesFiles.CheckLocation(snapshot.Location, GitRulesFiles.Resolve(snapshot.Path));
            token.ThrowIfCancellationRequested();
            onWriteStarted?.Invoke();
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
