using System.Text;
using System.Xml;
using System.Xml.Linq;
using GitCommands;
using GitExtensions.Linux;

internal static class DictionarySettingsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("dictionary XML hierarchy reads local distributed global without writing and preserves exact names", HierarchyAsync),
        ("dictionary local save interoperates with original Windows XML and preserves other keys index refs and modes", InteropAsync),
        ("dictionary malformed and unsupported XML is visible and never overwritten", InvalidXmlAsync),
        ("dictionary optimistic snapshots reject edits creation removal replacement and mode changes", ConflictsAsync),
        ("dictionary local writes preserve followed symlinks hardlinks and readonly target permissions", LinksAsync),
        ("dictionary linked worktrees share only local common settings while distributed overrides remain per root", WorktreesAsync),
        ("dictionary bare repositories foreign snapshots invalid names and cancellation retain real endpoints", ValidationAsync),
        ("dictionary concurrent repository instances refuse a stale overwrite rather than losing another local key", ConcurrentAsync),
    ];

    private static async Task HierarchyAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitDictionarySettingsSnapshot global = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        Check(global.Source == GitDictionarySettingSource.Global && global.EffectiveDictionary == "en-US"
            && !global.LocalExists && !global.DistributedExists, "Missing XML uses the supplied global without creating either file.");
        Check(!File.Exists(global.LocalPath) && !File.Exists(global.DistributedPath!), "A read has no filesystem publication.");
        WriteXml(global.DistributedPath!, ("dictionary", "fr-FR"), ("other", "kept"));
        byte[] distributed = File.ReadAllBytes(global.DistributedPath!);
        GitDictionarySettingsSnapshot inherited = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        Check(inherited.Source == GitDictionarySettingSource.Distributed && inherited.EffectiveDictionary == "fr-FR", "Distributed beats global.");
        WriteXml(global.LocalPath, ("dictionary", "NONE"));
        byte[] local = File.ReadAllBytes(global.LocalPath);
        GitDictionarySettingsSnapshot disabled = await f.Repository.ReadEffectiveDictionaryAsync("My dictionary λ");
        Check(disabled.LocalDictionary == "none" && disabled.EffectiveDictionary == "none"
            && disabled.Source == GitDictionarySettingSource.Local && disabled.GlobalDictionary == "My dictionary λ", "Only None is case-normalized; local disable wins over both lower scopes.");
        await f.Repository.WriteLocalDictionaryAsync(disabled, "None");
        Check(local.SequenceEqual(File.ReadAllBytes(global.LocalPath)) && distributed.SequenceEqual(File.ReadAllBytes(global.DistributedPath!)), "Identity-equivalent local save is a true byte-preserving no-op.");
        WriteXml(global.DistributedPath!, ("dictionary", "../masked-distributed"));
        GitDictionarySettingsSnapshot masked = await f.Repository.ReadEffectiveDictionaryAsync("../masked-global");
        Check(masked.EffectiveDictionary == "none" && masked.GlobalDictionary == "../masked-global"
            && masked.DistributedDictionary == "../masked-distributed", "Unselected identities remain opaque; only the effective ID is strictly validated.");
        File.Delete(global.LocalPath);
        await ExpectAsync<ArgumentException>(() => f.Repository.ReadEffectiveDictionaryAsync("en-US"));
        WriteXml(global.DistributedPath!, ("dictionary", "fr-FR"));
        WriteXml(global.LocalPath, ("Dictionary", "wrong-case-key"));
        Check((await f.Repository.ReadEffectiveDictionaryAsync("en-US")).EffectiveDictionary == "fr-FR", "The original XML key is exactly lowercase dictionary.");
    }

    private static async Task InteropAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitDictionarySettingsSnapshot first = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        WriteXml(first.LocalPath, ("dictionary", "en-US"), ("other<key>", "λ & <value>\nsecond line"), ("empty", ""));
        WriteXml(first.DistributedPath!, ("dictionary", "fr-FR"));
        byte[] index = File.ReadAllBytes(f.File(".git/index")), distributed = File.ReadAllBytes(first.DistributedPath!);
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(first.LocalPath, mode); }
        GitDictionarySettingsSnapshot expected = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        GitDictionarySettingsSnapshot saved = await f.Repository.WriteLocalDictionaryAsync(expected, "Custom λ");
        XmlSerializableDictionary<string, string> windows = [];
        using (XmlReader reader = XmlReader.Create(saved.LocalPath, new XmlReaderSettings { IgnoreWhitespace = true }))
        { windows.ReadXml(reader); }
        Check(saved.EffectiveDictionary == "Custom λ" && saved.Source == GitDictionarySettingSource.Local
            && windows["dictionary"] == "Custom λ" && windows["other<key>"] == "λ & <value>\nsecond line" && windows["empty"] == "", "Original Windows serializer reads all preserved escaped/unicode values.");
        Check(distributed.SequenceEqual(File.ReadAllBytes(first.DistributedPath!)) && index.SequenceEqual(File.ReadAllBytes(f.File(".git/index")))
            && head == await f.Repository.ResolveRevisionAsync("HEAD"), "Only local XML is changed; no implicit Git/config/index mutation.");
        if (!OperatingSystem.IsWindows()) { Check(File.GetUnixFileMode(saved.LocalPath) == mode, "Original permissions survive."); }
    }

    private static async Task InvalidXmlAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitDictionarySettingsSnapshot blank = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        foreach (string xml in new[] { "", "<dictionary>", "<wrong />", "<dictionary><unknown /></dictionary>",
            "<dictionary><item><key><string>dictionary</string></key></item></dictionary>",
            "<!DOCTYPE dictionary [<!ENTITY injected 'fr-FR'>]><dictionary>&injected;</dictionary>",
            "<dictionary><item><key><string>x</string></key><value><string>a</string></value></item><item><key><string>x</string></key><value><string>b</string></value></item></dictionary>" })
        {
            await File.WriteAllTextAsync(blank.LocalPath, xml); byte[] bytes = File.ReadAllBytes(blank.LocalPath);
            await ExpectAsync<XmlException>(() => f.Repository.ReadEffectiveDictionaryAsync("en-US"));
            await ExpectAsync<XmlException>(() => f.Repository.WriteLocalDictionaryAsync(blank, "fr-FR"));
            Check(bytes.SequenceEqual(File.ReadAllBytes(blank.LocalPath)), "Malformed XML is not silently replaced by defaults.");
        }
        WriteXml(blank.LocalPath, ("dictionary", "en-US")); await File.WriteAllTextAsync(blank.DistributedPath!, "<bad>");
        await ExpectAsync<XmlException>(() => f.Repository.ReadEffectiveDictionaryAsync("en-US"));
        Check(await File.ReadAllTextAsync(blank.DistributedPath!) == "<bad>", "A corrupt lower layer is visible even when local has a value.");
    }

    private static async Task ConflictsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitDictionarySettingsSnapshot snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        WriteXml(snapshot.LocalPath, ("dictionary", "fr-FR"));
        await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "en-GB"));
        snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        WriteXml(snapshot.LocalPath, ("dictionary", "fr-FR"), ("peer", "new value"));
        await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "en-GB"));
        snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        string replacement = snapshot.LocalPath + ".replacement"; File.Copy(snapshot.LocalPath, replacement); File.Move(replacement, snapshot.LocalPath, true);
        await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "en-GB"));
        snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US"); File.Delete(snapshot.LocalPath);
        await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "en-GB"));
        snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US"); WriteXml(snapshot.DistributedPath!, ("dictionary", "fr-FR"));
        await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "en-GB"));
        Check(!File.Exists(snapshot.LocalPath), "A stale distributed dependency cannot create the local file.");
        if (!OperatingSystem.IsWindows())
        {
            WriteXml(snapshot.LocalPath, ("dictionary", "en-US")); snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
            File.SetUnixFileMode(snapshot.LocalPath, UnixFileMode.UserRead);
            await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "en-GB"));
            Check(File.GetUnixFileMode(snapshot.LocalPath) == UnixFileMode.UserRead, "A changed permission snapshot is not restored over a peer's mode.");
        }
    }

    private static async Task LinksAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitDictionarySettingsSnapshot first = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        string target = System.IO.Path.Combine(f.Root, "explicit settings λ"), hard = System.IO.Path.Combine(f.Root, "hard settings");
        WriteXml(target, ("dictionary", "en-US"), ("peer", "preserved"));
        Check((await new GitRunner("ln").RunAsync(f.Repository.Path, ["--", target, hard])).Success, "Real hardlink fixture.");
        File.CreateSymbolicLink(first.LocalPath, target);
        const UnixFileMode readOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead;
        File.SetUnixFileMode(target, readOnly);
        GitDictionarySettingsSnapshot snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        await f.Repository.WriteLocalDictionaryAsync(snapshot, "fr-FR");
        Check(new FileInfo(first.LocalPath).LinkTarget == target && File.ReadAllBytes(hard).SequenceEqual(File.ReadAllBytes(target))
            && File.GetUnixFileMode(target) == readOnly && File.GetUnixFileMode(hard) == readOnly, "The followed target inode, hardlink, symlink and readonly mode survive.");
        snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        string other = System.IO.Path.Combine(f.Root, "other settings"); WriteXml(other, ("dictionary", "en-GB")); byte[] before = File.ReadAllBytes(target);
        File.Delete(first.LocalPath); File.CreateSymbolicLink(first.LocalPath, other);
        await ExpectAsync<IOException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "none"));
        Check(before.SequenceEqual(File.ReadAllBytes(target)) && (await f.Repository.ReadEffectiveDictionaryAsync("en-US")).EffectiveDictionary == "en-GB", "A retargeted link writes neither target.");
    }

    private static async Task WorktreesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = System.IO.Path.Combine(f.Root, "linked tree");
        await f.Repository.RunAsync(["worktree", "add", "-b", "dictionary-linked", "--", path]);
        GitRepository linked = await GitRepository.OpenAsync(path); GitRepositoryInfo info = await linked.GetInfoAsync();
        GitDictionarySettingsSnapshot one = await f.Repository.ReadEffectiveDictionaryAsync("en-US"), two = await linked.ReadEffectiveDictionaryAsync("en-US");
        Check(one.LocalPath == two.LocalPath && two.LocalPath == System.IO.Path.Combine(info.CommonDirectory, "GitExtensions.settings")
            && one.DistributedPath != two.DistributedPath && two.DistributedPath == System.IO.Path.Combine(path, "GitExtensions.settings"), "Common and working roots remain distinct real endpoints.");
        WriteXml(one.DistributedPath!, ("dictionary", "fr-FR")); WriteXml(two.DistributedPath!, ("dictionary", "de-DE"));
        Check((await f.Repository.ReadEffectiveDictionaryAsync("en-US")).EffectiveDictionary == "fr-FR"
            && (await linked.ReadEffectiveDictionaryAsync("en-US")).EffectiveDictionary == "de-DE", "Distributed choice is per worktree.");
        two = await linked.ReadEffectiveDictionaryAsync("en-US"); await linked.WriteLocalDictionaryAsync(two, "none");
        Check((await f.Repository.ReadEffectiveDictionaryAsync("en-US")).EffectiveDictionary == "none", "Commit local write is shared through GitCommonDirectory.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture foreign = await RepositoryFixture.CreateAsync();
        GitDictionarySettingsSnapshot snapshot = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        Check((await f.Repository.ReadEffectiveDictionaryAsync(null!)).EffectiveDictionary == "en-US", "An explicit null global identity uses the Windows default.");
        await ExpectAsync<ArgumentNullException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, null!));
        foreach (string value in new[] { "", " ", ".", "..", "dir/name", "dir\\name", "bad\0name" })
        {
            await ExpectAsync<ArgumentException>(() => f.Repository.ReadEffectiveDictionaryAsync(value));
            await ExpectAsync<ArgumentException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, value));
        }
        await ExpectAsync<ArgumentException>(() => foreign.Repository.WriteLocalDictionaryAsync(snapshot, "fr-FR"));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => f.Repository.ReadEffectiveDictionaryAsync("en-US", cancellation.Token));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.WriteLocalDictionaryAsync(snapshot, "fr-FR", cancellation.Token));
        Check(!File.Exists(snapshot.LocalPath), "Invalid input, foreign baselines and precancellation never create local settings.");
        GitRepository bare = await GitRepository.InitAsync(System.IO.Path.Combine(f.Root, "bare.git"), bare: true);
        GitRepositoryInfo info = await bare.GetInfoAsync(); GitDictionarySettingsSnapshot bareSettings = await bare.ReadEffectiveDictionaryAsync("en-US");
        Check(bareSettings.CommonDirectory == info.CommonDirectory && bareSettings.RootPath == info.RootPath
            && bareSettings.DistributedPath is null, "A bare repository has no fabricated distributed worktree.");
        await bare.WriteLocalDictionaryAsync(bareSettings, "fr-FR");
        Check((await bare.ReadEffectiveDictionaryAsync("en-US")).EffectiveDictionary == "fr-FR", "Bare local XML uses actual common directory.");
    }

    private static async Task ConcurrentAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository other = await GitRepository.OpenAsync(f.Repository.Path);
        GitDictionarySettingsSnapshot one = await f.Repository.ReadEffectiveDictionaryAsync("en-US"), two = await other.ReadEffectiveDictionaryAsync("en-US");
        Task<GitDictionarySettingsSnapshot> first = f.Repository.WriteLocalDictionaryAsync(one, "fr-FR"), second = other.WriteLocalDictionaryAsync(two, "de-DE");
        try { await Task.WhenAll(first, second); } catch (IOException) { }
        Check(first.IsCompletedSuccessfully != second.IsCompletedSuccessfully && (first.IsFaulted || second.IsFaulted), "Exactly one stale creation wins across repository instances.");
        GitDictionarySettingsSnapshot saved = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        Check(saved.EffectiveDictionary == (first.IsCompletedSuccessfully ? "fr-FR" : "de-DE"), "Final local value matches the actual successful writer.");
    }

    private static void WriteXml(string path, params (string Key, string Value)[] values)
        => new XDocument(new XElement("dictionary", values.Select(pair => new XElement("item",
            new XElement("key", new XElement("string", pair.Key)), new XElement("value", new XElement("string", pair.Value)))))).Save(path);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> ExpectAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
