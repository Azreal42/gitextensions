using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitHubHostingPreferencesStoreTests
{
    [Test]
    public void XdgAndExplicitStoresUseTheOriginalPluginGroupInTheSamePreferencesFile()
    {
        using Profile profile = new();
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", profile.Root);
            GitHubHostingPreferencesStore store = new();
            Assert.That(store.FilePath, Is.EqualTo(new PreferencesStore().FilePath));
            Assert.That(store.FilePath, Is.EqualTo(profile.Store.FilePath));
            Dictionary<string, string?> draft = new()
            {
                [GitHubHostingSettings.WebUrlKey] = "https://github.com",
                [GitHubHostingSettings.ApiUrlKey] = "https://api.github.com",
                [GitHubHostingSettings.CredentialsTypeKey] = "Guest",
                [GitHubHostingSettings.UsernameKey] = "remembered-user",
                [GitHubHostingSettings.PasswordKey] = "remembered-password",
                [GitHubHostingSettings.TokenKey] = "remembered-token"
            };
            store.SaveChanges(store.Load(), draft);
            Assert.That(new PreferencesStore(profile.Root).Load().PluginSettings[GitHubHostingSettings.PluginId], Is.EquivalentTo(draft));
            Assert.That(new GitHubHostingPreferencesStore(profile.Root).Load(), Is.EquivalentTo(draft));
            profile.AssertOnlyPreferencesFile();
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); }
    }

    [Test]
    public void SavingOneExplicitKeyPreservesUnknownRootOtherPluginAndGitHubValues()
    {
        using Profile profile = new();
        profile.Write("""
            {"Theme":"Dark","Future":{"array":[1,true,null,{"literal":"Café Ω"}]},
             "PluginSettings":{"other-plugin":{"secret":"other-local-value","future":null},
             "2ec3e1f0-ef37-413f-bea5-b8fe1f9c505c":{"future.λ":"keep <literal> &\nbody","future.null":null,"Username":"before"}}}
            """);
        using JsonDocument original = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        IReadOnlyDictionary<string, string?> baseline = profile.Store.Load();
        Dictionary<string, string?> draft = Copy(baseline); draft[GitHubHostingSettings.UsernameKey] = "after";
        IReadOnlyDictionary<string, string?> saved = profile.Store.SaveChanges(baseline, draft);
        using JsonDocument actual = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        foreach (string name in new[] { "Theme", "Future" })
        { Assert.That(JsonElement.DeepEquals(actual.RootElement.GetProperty(name), original.RootElement.GetProperty(name)), Is.True, name); }
        Assert.That(JsonElement.DeepEquals(actual.RootElement.GetProperty("PluginSettings").GetProperty("other-plugin"),
            original.RootElement.GetProperty("PluginSettings").GetProperty("other-plugin")), Is.True);
        Assert.That(saved["future.λ"], Is.EqualTo("keep <literal> &\nbody"));
        Assert.That(saved.ContainsKey("future.null"), Is.True); Assert.That(saved["future.null"], Is.Null);
        Assert.That(saved[GitHubHostingSettings.UsernameKey], Is.EqualTo("after"));
        Assert.That(profile.Store.Load(), Is.EquivalentTo(saved)); profile.AssertOnlyPreferencesFile();
    }

    [Test]
    public void RemovingAnExplicitKeyAndSavingAnExplicitNullHaveDifferentRoundTrips()
    {
        using Profile profile = new();
        Dictionary<string, string?> initial = new() { ["future.remove"] = "old", ["future.null"] = "old", ["future.keep"] = "same" };
        IReadOnlyDictionary<string, string?> baseline = profile.Store.SaveChanges(new Dictionary<string, string?>(), initial);
        Dictionary<string, string?> draft = Copy(baseline); draft.Remove("future.remove"); draft["future.null"] = null;
        profile.Store.SaveChanges(baseline, draft);
        using JsonDocument actual = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        JsonElement group = actual.RootElement.GetProperty("PluginSettings").GetProperty(GitHubHostingSettings.PluginId);
        Assert.That(group.TryGetProperty("future.remove", out _), Is.False);
        Assert.That(group.GetProperty("future.null").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(group.GetProperty("future.keep").GetString(), Is.EqualTo("same"));
        Assert.That(profile.Store.Load(), Is.EquivalentTo(draft)); profile.AssertOnlyPreferencesFile();
    }

    [Test]
    public async Task TwoConcurrentEditorsMergeDistinctChangedFieldsWithoutDroppingEitherDraft()
    {
        using Profile profile = new();
        profile.Write("{\"Theme\":\"Dark\",\"PluginSettings\":{\"2ec3e1f0-ef37-413f-bea5-b8fe1f9c505c\":{\"future\":\"kept\"}}}");
        GitHubHostingPreferencesStore other = new(profile.Root);
        IReadOnlyDictionary<string, string?> first = profile.Store.Load(), second = other.Load();
        Dictionary<string, string?> a = Copy(first), b = Copy(second);
        a[GitHubHostingSettings.WebUrlKey] = "https://enterprise.invalid";
        b[GitHubHostingSettings.ApiUrlKey] = "https://enterprise.invalid/api/v3";
        using ManualResetEventSlim start = new(false);
        Task saveA = Task.Run(() => { start.Wait(); profile.Store.SaveChanges(first, a); });
        Task saveB = Task.Run(() => { start.Wait(); other.SaveChanges(second, b); });
        start.Set(); await Task.WhenAll(saveA, saveB);
        IReadOnlyDictionary<string, string?> merged = profile.Store.Load();
        Assert.That(merged[GitHubHostingSettings.WebUrlKey], Is.EqualTo(a[GitHubHostingSettings.WebUrlKey]));
        Assert.That(merged[GitHubHostingSettings.ApiUrlKey], Is.EqualTo(b[GitHubHostingSettings.ApiUrlKey]));
        Assert.That(merged["future"], Is.EqualTo("kept"));
        Assert.That(new PreferencesStore(profile.Root).Load().Theme, Is.EqualTo("Dark")); profile.AssertOnlyPreferencesFile();
    }

    [Test]
    public async Task TwoConcurrentEditorsOfTheSameFieldHaveOneWinnerAndOneReadableConflict()
    {
        using Profile profile = new();
        GitHubHostingPreferencesStore other = new(profile.Root);
        IReadOnlyDictionary<string, string?> first = profile.Store.Load(), second = other.Load();
        Dictionary<string, string?> a = Copy(first), b = Copy(second);
        a[GitHubHostingSettings.WebUrlKey] = "https://first.invalid";
        b[GitHubHostingSettings.WebUrlKey] = "https://second.invalid";
        using ManualResetEventSlim start = new(false);
        Task<Exception?> saveA = Task.Run(() => Save(profile.Store, first, a));
        Task<Exception?> saveB = Task.Run(() => Save(other, second, b));
        start.Set(); Exception?[] results = await Task.WhenAll(saveA, saveB);
        Assert.That(results.Count(error => error is null), Is.EqualTo(1));
        Exception conflict = results.Single(error => error is not null)!;
        Assert.That(conflict, Is.TypeOf<InvalidOperationException>());
        Assert.That(conflict.Message, Does.Contain("another window"));
        Assert.That(profile.Store.Load()[GitHubHostingSettings.WebUrlKey], Is.EqualTo(results[0] is null
            ? a[GitHubHostingSettings.WebUrlKey] : b[GitHubHostingSettings.WebUrlKey]));
        profile.AssertOnlyPreferencesFile();

        Exception? Save(GitHubHostingPreferencesStore store, IReadOnlyDictionary<string, string?> baseline, Dictionary<string, string?> draft)
        {
            start.Wait();
            try { store.SaveChanges(baseline, draft); return null; }
            catch (Exception error) { return error; }
        }
    }

    [Test]
    public void StaleNoOpReturnsCurrentValuesAndPreservesTheirExactBytesAndTimestamp()
    {
        using Profile profile = new();
        profile.Write(" { \"Theme\" : \"Dark\", \"PluginSettings\" : { } } \n");
        IReadOnlyDictionary<string, string?> stale = profile.Store.Load();
        Dictionary<string, string?> changed = Copy(stale); changed[GitHubHostingSettings.WebUrlKey] = "https://saved.invalid";
        IReadOnlyDictionary<string, string?> current = profile.Store.SaveChanges(stale, changed);
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); DateTime modified = File.GetLastWriteTimeUtc(profile.Store.FilePath);
        Assert.That(profile.Store.SaveChanges(stale, stale), Is.EquivalentTo(current));
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(profile.Store.FilePath), Is.EqualTo(modified)); profile.AssertOnlyPreferencesFile();
    }

    [TestCase(false)] [TestCase(true)]
    public void LoadingAndAbandoningADraftAndANoOpNeverCreateOrRewriteAProfile(bool existing)
    {
        using Profile profile = new();
        if (existing) { profile.Write(" { \"Theme\" : \"Dark\", \"PluginSettings\" : { } } \n"); }
        byte[]? bytes = existing ? File.ReadAllBytes(profile.Store.FilePath) : null;
        DateTime? modified = existing ? File.GetLastWriteTimeUtc(profile.Store.FilePath) : null;
        IReadOnlyDictionary<string, string?> baseline = profile.Store.Load();
        Dictionary<string, string?> abandoned = Copy(baseline); abandoned[GitHubHostingSettings.TokenKey] = "cancelled-draft-only";
        Assert.That(profile.Store.Load(), Is.EquivalentTo(baseline), "A caller's abandoned draft has no persistence side effect.");
        profile.Store.SaveChanges(baseline, baseline);
        if (existing)
        {
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(profile.Store.FilePath), Is.EqualTo(modified)); profile.AssertOnlyPreferencesFile();
        }
        else { Assert.That(Directory.Exists(profile.Root), Is.False); }
    }

    [TestCase("{broken")]
    [TestCase("[]")]
    [TestCase("{\"PluginSettings\":null}")]
    [TestCase("{\"PluginSettings\":[]}")]
    [TestCase("{\"PluginSettings\":{\"2ec3e1f0-ef37-413f-bea5-b8fe1f9c505c\":null}}")]
    [TestCase("{\"PluginSettings\":{\"2ec3e1f0-ef37-413f-bea5-b8fe1f9c505c\":{\"Username\":42}}}")]
    [TestCase("{\"Theme\":\"Light\",\"Theme\":\"Dark\"}")]
    [TestCase("{\"PluginSettings\":{},\"PluginSettings\":{}}")]
    [TestCase("{\"PluginSettings\":{\"2ec3e1f0-ef37-413f-bea5-b8fe1f9c505c\":{\"Username\":\"one\",\"Username\":\"two\"}}}")]
    [TestCase("{\"Future\":[{\"key\":1,\"key\":2}]}")]
    public void CorruptOrDuplicateJsonIsNeverOverwrittenIncludingByAnUnchangedDraft(string contents)
    {
        using Profile profile = new(); profile.Write(contents);
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); DateTime modified = File.GetLastWriteTimeUtc(profile.Store.FilePath);
        Dictionary<string, string?> empty = new(), changed = new() { [GitHubHostingSettings.WebUrlKey] = "https://github.com" };
        Assert.That(() => profile.Store.Load(), Throws.InstanceOf<JsonException>());
        Assert.That(() => profile.Store.SaveChanges(empty, empty), Throws.InstanceOf<JsonException>());
        Assert.That(() => profile.Store.SaveChanges(empty, changed), Throws.InstanceOf<JsonException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(profile.Store.FilePath), Is.EqualTo(modified)); profile.AssertOnlyPreferencesFile();
    }

    [Test, Platform("Linux")]
    public void NewAndReplacedFilesContainingRememberedCredentialsHaveOnlyUserReadWritePermissions()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux file-mode contract."); return; }
        using Profile profile = new();
        Dictionary<string, string?> draft = new()
        {
            [GitHubHostingSettings.CredentialsTypeKey] = "Guest",
            [GitHubHostingSettings.PasswordKey] = "inactive-password",
            [GitHubHostingSettings.TokenKey] = "inactive-token"
        };
        IReadOnlyDictionary<string, string?> saved = profile.Store.SaveChanges(profile.Store.Load(), draft);
        UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(expected));
        File.SetUnixFileMode(profile.Store.FilePath, expected | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Dictionary<string, string?> changed = Copy(saved); changed[GitHubHostingSettings.PasswordKey] = "changed-inactive-password";
        profile.Store.SaveChanges(saved, changed);
        Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(expected));
        Assert.That(profile.Store.Load(), Is.EquivalentTo(changed)); profile.AssertOnlyPreferencesFile();
    }

    private static Dictionary<string, string?> Copy(IReadOnlyDictionary<string, string?> source) => new(source, StringComparer.Ordinal);

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-github-hosting-store-" + Guid.NewGuid().ToString("N"));
        internal GitHubHostingPreferencesStore Store { get; }
        internal Profile() => Store = new(Root);
        internal void Write(string contents)
        { Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!); File.WriteAllText(Store.FilePath, contents, new UTF8Encoding(false)); }
        internal void AssertOnlyPreferencesFile() => Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(Store.FilePath)!), Is.EqualTo(new[] { Store.FilePath }));
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
