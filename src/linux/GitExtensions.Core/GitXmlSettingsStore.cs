using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GitCommands;
using IOPath = System.IO.Path;

namespace GitExtensions.Linux;

/// <summary>Shared guarded IO for Windows string-dictionary XML, without interpreting any business key.</summary>
public static class GitXmlSettingsStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<GitRulesIdentity, SemaphoreSlim> IdentityLocks = new();

    /// <summary>Read and retain unknown, empty and nil values without creating files. IO runs off the calling thread.</summary>
    public static Task<GitXmlSettingsSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = IOPath.GetFullPath(path);
        return Task.Run(() => WithFileLockAsync(fullPath, null, () => ReadAsync(fullPath, cancellationToken), cancellationToken), CancellationToken.None);
    }

    /// <summary>Apply only supplied keys after checking the exact baseline; null removes a key. A no-op preserves the original bytes.</summary>
    public static Task<GitXmlSettingsSnapshot> SaveChangesAsync(GitXmlSettingsSnapshot expected,
        IReadOnlyDictionary<string, string?> intentions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(intentions);
        Dictionary<string, string?> edits = new(intentions, StringComparer.Ordinal);
        return Task.Run(() => WithFileLockAsync(expected.Path, expected.Location.Path, async () =>
        {
            GitXmlSettingsSnapshot current = await ReadAsync(expected.Path, cancellationToken).ConfigureAwait(false);
            CheckSnapshot(expected, current);
            XmlSerializableDictionary<string, string?> values = [];
            foreach ((string key, string? value) in current.Values) { values.Add(key, value); }
            bool changed = false;
            foreach ((string key, string? value) in edits)
            {
                if (value is null) { changed |= values.Remove(key); }
                else if (!values.TryGetValue(key, out string? before) || before != value) { values[key] = value; changed = true; }
            }
            if (!changed) { return current; }
            // Complete parsing, validation and serialization before touching permissions or bytes.
            byte[] bytes = Serialize(values);
            cancellationToken.ThrowIfCancellationRequested();
            GitRulesIdentity? writtenIdentity;
            if (current.Exists)
            {
                await WriteExistingAsync(current, bytes, cancellationToken).ConfigureAwait(false);
                writtenIdentity = current.Identity;
            }
            else
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(current.Location.Path)!);
                GitRulesFiles.CheckLocation(current.Location, GitRulesFiles.Resolve(current.Path), createdParents: true);
                cancellationToken.ThrowIfCancellationRequested();
                FileStreamOptions options = new()
                { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                await using FileStream file = new(current.Location.Path, options);
                writtenIdentity = GitRulesIdentity.ReadHandle(file.SafeFileHandle);
                // Once creation/writing starts, own and drain the effect before returning, including on cancellation.
                await file.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            GitXmlSettingsSnapshot saved = await ReadAsync(current.Path, CancellationToken.None).ConfigureAwait(false);
            GitRulesFiles.CheckLocation(current.Location, saved.Location, createdParents: !current.Exists);
            if (saved.Identity != writtenIdentity || saved.Hash != GitRulesFiles.Hash(bytes)
                || current.Exists && (saved.Mode != current.Mode || saved.Attributes != current.Attributes))
            { throw Changed(); }
            return saved;
        }, cancellationToken), CancellationToken.None);
    }

    private static async Task<T> WithFileLockAsync<T>(string path, string? expectedTarget, Func<Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string target = expectedTarget ?? GitRulesFiles.Resolve(path).Path;
        SemaphoreSlim pathGate = PathLocks.GetOrAdd(target, static _ => new(1, 1));
        await pathGate.WaitAsync(token).ConfigureAwait(false);
        SemaphoreSlim? identityGate = null;
        bool identityHeld = false;
        try
        {
            // Canonical-path locks coordinate creation and followed links; inode locks also coordinate existing hardlink aliases.
            GitRulesIdentity? identity = GitRulesIdentity.ReadPath(target);
            if (identity?.IsRegular == true)
            {
                identityGate = IdentityLocks.GetOrAdd(identity.Value, static _ => new(1, 1));
                await identityGate.WaitAsync(token).ConfigureAwait(false);
                identityHeld = true;
            }
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (identityHeld) { identityGate!.Release(); }
            pathGate.Release();
        }
    }

    private static async Task<GitXmlSettingsSnapshot> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
        byte[] bytes = [];
        UnixFileMode? mode = null;
        FileAttributes attributes = FileAttributes.Normal;
        if (identity is not null)
        {
            if (!identity.Value.IsRegular) { throw new IOException("The XML settings path is not a regular file: " + path); }
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw Changed(); }
            using MemoryStream buffer = new();
            await file.CopyToAsync(buffer, token).ConfigureAwait(false); bytes = buffer.ToArray();
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file.SafeFileHandle);
            attributes = File.GetAttributes(file.SafeFileHandle);
        }
        token.ThrowIfCancellationRequested();
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        XmlSerializableDictionary<string, string?> values = identity is null ? [] : Parse(bytes, path);
        return new(path, location, identity is not null, GitRulesFiles.Hash(bytes), identity, mode, attributes,
            new ReadOnlyDictionary<string, string?>(values));
    }

    private static XmlSerializableDictionary<string, string?> Parse(byte[] bytes, string path)
    {
        using MemoryStream input = new(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        { IgnoreWhitespace = true, CheckCharacters = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XDocument document = XDocument.Load(reader);
        XElement root = document.Root ?? throw new XmlException("Missing XML settings root: " + path);
        if (root.Name != "dictionary") { throw new XmlException("Expected the Windows string-dictionary XML settings format: " + path); }
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (XElement item in root.Elements())
        {
            if (item.Name != "item" || !item.Elements().Select(element => element.Name.ToString()).SequenceEqual(["key", "value"]))
            { throw new XmlException("Unexpected XML settings item: " + path); }
            foreach (XElement part in item.Elements())
            {
                if (part.Elements().Count() != 1 || part.Elements().Single().Name != "string")
                { throw new XmlException("Expected a Windows string key/value: " + path); }
            }
            if (!keys.Add(item.Element("key")!.Element("string")!.Value))
            { throw new XmlException("Duplicate XML settings key: " + path); }
        }
        XmlSerializableDictionary<string, string?> values = [];
        using XmlReader original = document.CreateReader(); values.ReadXml(original);
        return values;
    }

    private static byte[] Serialize(XmlSerializableDictionary<string, string?> values)
    {
        using MemoryStream buffer = new();
        using (XmlTextWriter writer = new(buffer, Encoding.UTF8) { Formatting = Formatting.Indented })
        {
            writer.WriteStartDocument(); writer.WriteStartElement("dictionary");
            values.WriteXml(writer); writer.WriteEndElement(); writer.Flush();
        }
        return buffer.ToArray();
    }

    internal static void CheckSnapshot(GitXmlSettingsSnapshot expected, GitXmlSettingsSnapshot current)
    {
        if (expected.Path != current.Path) { throw Changed(); }
        GitRulesFiles.CheckLocation(expected.Location, current.Location);
        if (expected.Exists != current.Exists || expected.Identity != current.Identity || expected.Hash != current.Hash
            || expected.Mode != current.Mode || expected.Attributes != current.Attributes)
        { throw Changed(); }
    }

    private static IOException Changed()
        => new("The XML settings, permissions or symbolic-link destination changed. Reload before saving.");

    private static async Task WriteExistingAsync(GitXmlSettingsSnapshot snapshot, byte[] bytes, CancellationToken token)
    {
        // Extracted from the dictionary writer: preserve inode/hardlinks/followed links. Optimistic CAS, not interprocess atomicity.
        await using FileStream observed = new(snapshot.Location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (GitRulesIdentity.ReadHandle(observed.SafeFileHandle) != snapshot.Identity) { throw Changed(); }
        bool changedMode = false;
        try
        {
            FileStream OpenWritable() => new(snapshot.Location.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
            FileStream writable;
            try { writable = OpenWritable(); }
            catch (UnauthorizedAccessException)
            {
                if (!OperatingSystem.IsWindows() && snapshot.Mode is UnixFileMode mode && !mode.HasFlag(UnixFileMode.UserWrite))
                { File.SetUnixFileMode(observed.SafeFileHandle, mode | UnixFileMode.UserWrite); changedMode = true; }
                else if (OperatingSystem.IsWindows() && snapshot.Attributes.HasFlag(FileAttributes.ReadOnly))
                { File.SetAttributes(observed.SafeFileHandle, snapshot.Attributes & ~FileAttributes.ReadOnly); changedMode = true; }
                else { throw; }
                writable = OpenWritable();
            }
            await using FileStream file = writable;
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != snapshot.Identity) { throw Changed(); }
            using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false);
            if (GitRulesFiles.Hash(buffer.ToArray()) != snapshot.Hash) { throw Changed(); }
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
