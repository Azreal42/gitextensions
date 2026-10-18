using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GitCommands;
using IOPath = System.IO.Path;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private const string DictionarySettingsFileName = "GitExtensions.settings";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DictionarySettingsLocks = new(StringComparer.Ordinal);

    /// <summary>Reads explicit repository XML overrides over the supplied XDG global value, without any write.</summary>
    public Task<GitDictionarySettingsSnapshot> ReadEffectiveDictionaryAsync(string globalDictionary,
        CancellationToken cancellationToken = default)
    {
        string global = GitDictionaryName.NormalizeDisabledIdentity(globalDictionary);
        return DictionarySettingsOperationAsync(info => ReadDictionarySettingsAsync(info, global, cancellationToken), cancellationToken);
    }

    /// <summary>Writes only the local dictionary key after checking the captured local and distributed files.</summary>
    public Task<GitDictionarySettingsSnapshot> WriteLocalDictionaryAsync(GitDictionarySettingsSnapshot expected,
        string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string dictionary = GitDictionaryName.Normalize(value);
        return DictionarySettingsOperationAsync(async info =>
        {
            if (info.RootPath != expected.RootPath || info.CommonDirectory != expected.CommonDirectory)
            { throw new ArgumentException("The dictionary settings snapshot belongs to another repository or worktree.", nameof(expected)); }
            GitDictionarySettingsSnapshot current = await ReadDictionarySettingsAsync(info, expected.GlobalDictionary, cancellationToken).ConfigureAwait(false);
            CheckDictionarySettingsFile(expected.Local, current.Local);
            if ((expected.Distributed is null) != (current.Distributed is null)) { throw DictionarySettingsChanged(); }
            if (expected.Distributed is not null) { CheckDictionarySettingsFile(expected.Distributed, current.Distributed!); }
            if (current.LocalDictionary == dictionary) { return current; }

            XmlSerializableDictionary<string, string> values = [];
            foreach ((string key, string existing) in current.Local.Values) { values.Add(key, existing); }
            values["dictionary"] = dictionary;
            // Finish all validation/serialization before touching permissions or file contents.
            byte[] bytes = SerializeDictionarySettings(values);
            cancellationToken.ThrowIfCancellationRequested();
            GitRulesIdentity? writtenIdentity;
            if (current.Local.Exists)
            {
                await WriteExistingDictionarySettingsAsync(current.Local, bytes, cancellationToken).ConfigureAwait(false);
                writtenIdentity = current.Local.Identity;
            }
            else
            {
                System.IO.Directory.CreateDirectory(IOPath.GetDirectoryName(current.Local.Location.Path)!);
                GitRulesFiles.CheckLocation(current.Local.Location, GitRulesFiles.Resolve(current.Local.Path), createdParents: true);
                cancellationToken.ThrowIfCancellationRequested();
                FileStreamOptions options = new()
                { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                await using FileStream file = new(current.Local.Location.Path, options);
                writtenIdentity = GitRulesIdentity.ReadHandle(file.SafeFileHandle);
                // The commit is owned and drained once it starts, even if its waiter requests cancellation.
                await file.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            GitDictionarySettingsFile saved = await ReadDictionarySettingsFileAsync(current.Local.Path, CancellationToken.None).ConfigureAwait(false);
            GitRulesFiles.CheckLocation(current.Local.Location, saved.Location, createdParents: !current.Local.Exists);
            if (saved.Identity != writtenIdentity || saved.Hash != GitRulesFiles.Hash(bytes)
                || current.Local.Exists && (saved.Mode != current.Local.Mode || saved.Attributes != current.Local.Attributes))
            { throw DictionarySettingsChanged(); }
            return new(info, expected.GlobalDictionary, saved, current.Distributed);
        }, cancellationToken);
    }

    private Task<GitDictionarySettingsSnapshot> DictionarySettingsOperationAsync(
        Func<GitRepositoryInfo, Task<GitDictionarySettingsSnapshot>> action, CancellationToken token)
        // XML, path/permission inspection and final serialization never run synchronously on the UI thread.
        => Task.Run(() => WithMutationAsync(async cancellationToken =>
        {
            GitRepositoryInfo info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
            string common = GitRulesFiles.Resolve(info.CommonDirectory).Path;
            SemaphoreSlim gate = DictionarySettingsLocks.GetOrAdd(common, static _ => new(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await action(info).ConfigureAwait(false); }
            finally { gate.Release(); }
        }, token), CancellationToken.None);

    private static async Task<GitDictionarySettingsSnapshot> ReadDictionarySettingsAsync(GitRepositoryInfo info,
        string global, CancellationToken token)
    {
        GitDictionarySettingsFile local = await ReadDictionarySettingsFileAsync(IOPath.Combine(info.CommonDirectory, DictionarySettingsFileName), token).ConfigureAwait(false);
        GitDictionarySettingsFile? distributed = info.IsBare ? null
            : await ReadDictionarySettingsFileAsync(IOPath.Combine(info.RootPath, DictionarySettingsFileName), token).ConfigureAwait(false);
        return new(info, global, local, distributed);
    }

    private static async Task<GitDictionarySettingsFile> ReadDictionarySettingsFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
        byte[] bytes = [];
        UnixFileMode? mode = null;
        FileAttributes attributes = FileAttributes.Normal;
        if (identity is not null)
        {
            if (!identity.Value.IsRegular) { throw new IOException("The dictionary settings path is not a regular file: " + path); }
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw DictionarySettingsChanged(); }
            using MemoryStream buffer = new();
            await file.CopyToAsync(buffer, token).ConfigureAwait(false); bytes = buffer.ToArray();
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file.SafeFileHandle);
            attributes = File.GetAttributes(file.SafeFileHandle);
        }
        token.ThrowIfCancellationRequested();
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        XmlSerializableDictionary<string, string> values = identity is null ? [] : ParseDictionarySettings(bytes, path);
        string? dictionary = values.TryGetValue("dictionary", out string? name) && name is not null ? GitDictionaryName.NormalizeDisabledIdentity(name) : null;
        return new(path, location, identity is not null, GitRulesFiles.Hash(bytes), identity, mode, attributes,
            new ReadOnlyDictionary<string, string>(values), dictionary);
    }

    private static XmlSerializableDictionary<string, string> ParseDictionarySettings(byte[] bytes, string path)
    {
        using MemoryStream input = new(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        { IgnoreWhitespace = true, CheckCharacters = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XDocument document = XDocument.Load(reader);
        XElement root = document.Root ?? throw new XmlException("Missing dictionary settings root: " + path);
        if (root.Name != "dictionary") { throw new XmlException("Expected the Windows dictionary settings format: " + path); }
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (XElement item in root.Elements())
        {
            if (item.Name != "item" || !item.Elements().Select(element => element.Name.ToString()).SequenceEqual(["key", "value"]))
            { throw new XmlException("Unexpected dictionary settings item: " + path); }
            foreach (XElement part in item.Elements())
            {
                if (part.Elements().Count() != 1 || part.Elements().Single().Name != "string")
                { throw new XmlException("Expected a Windows string key/value: " + path); }
            }
            if (!keys.Add(item.Element("key")!.Element("string")!.Value))
            { throw new XmlException("Duplicate dictionary settings key: " + path); }
        }
        XmlSerializableDictionary<string, string> values = [];
        using XmlReader original = document.CreateReader();
        values.ReadXml(original);
        return values;
    }

    private static byte[] SerializeDictionarySettings(XmlSerializableDictionary<string, string> values)
    {
        using MemoryStream buffer = new();
        using (XmlTextWriter writer = new(buffer, Encoding.UTF8) { Formatting = Formatting.Indented })
        {
            writer.WriteStartDocument(); writer.WriteStartElement("dictionary");
            values.WriteXml(writer); writer.WriteEndElement(); writer.Flush();
        }
        return buffer.ToArray();
    }

    private static void CheckDictionarySettingsFile(GitDictionarySettingsFile expected, GitDictionarySettingsFile current)
    {
        GitRulesFiles.CheckLocation(expected.Location, current.Location);
        if (expected.Exists != current.Exists || expected.Identity != current.Identity || expected.Hash != current.Hash
            || expected.Mode != current.Mode || expected.Attributes != current.Attributes)
        { throw DictionarySettingsChanged(); }
    }

    private static IOException DictionarySettingsChanged()
        => new("The repository dictionary settings, permissions or symbolic-link destination changed. Reload before saving.");

    private static async Task WriteExistingDictionarySettingsAsync(GitDictionarySettingsFile snapshot, byte[] bytes, CancellationToken token)
    {
        // Same verified-handle contract as the rules editor: preserve inode/hardlinks and followed links.
        // This is optimistic CAS, not an atomic transaction against a non-cooperating native writer.
        await using FileStream observed = new(snapshot.Location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (GitRulesIdentity.ReadHandle(observed.SafeFileHandle) != snapshot.Identity) { throw DictionarySettingsChanged(); }
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
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != snapshot.Identity) { throw DictionarySettingsChanged(); }
            using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false);
            if (GitRulesFiles.Hash(buffer.ToArray()) != snapshot.Hash) { throw DictionarySettingsChanged(); }
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
