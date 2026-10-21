using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ProxySwitcherSettingsSessionTests
{
    [Test]
    public void FourOriginalStringSettingsKeepTheirKeysOrderDefaultsAndMaskedPasswordKind()
    {
        PluginScopedSettingsDefinition d = ProxySwitcherSettingsCatalog.Definition;
        Assert.That(d.Id.ToString(), Is.EqualTo("c2a1c7a4-d519-4bd1-859b-6ce7db9325fb"));
        Assert.That(d.Fields.Select(field => field.Name), Is.EqualTo(new[] { "Username", "Password", "HTTP proxy", "HTTP proxy port" }));
        Assert.That(d.Fields.Select(field => field.DefaultValue), Is.EqualTo(new[] { "", "", "", "8080" }));
        Assert.That(d.Fields.Select(field => field.Kind), Is.EqualTo(new[] { PluginScopedSettingKind.Text, PluginScopedSettingKind.Password, PluginScopedSettingKind.Text, PluginScopedSettingKind.Text }));
        Assert.That(d.Fields.Select(d.Key), Is.EqualTo(d.Fields.Select(field => d.Id + "." + field.Name)));
        Assert.That(d.Fields.Select(d.OldKey), Is.EqualTo(d.Fields.Select(field => "Proxy Switcher" + field.Name)));
    }

    [TestCase("none")] [TestCase("worktree")] [TestCase("bare")]
    public async Task ScopeAvailabilityAndUntouchedSaveMatchTheSharedXmlContract(string kind)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind, seed: false);
        ProxySwitcherSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> before = f.Material();
        Assert.That(session.InitialScope, Is.EqualTo(kind == "none" ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Effective));
        Assert.That(session.HasDistributed, Is.EqualTo(kind == "worktree"));
        Assert.That(session.RuntimeValues()["host"], Is.Empty); Assert.That(session.RuntimeValues()["port"], Is.EqualTo("8080"));
        Assert.That(session.RuntimeValues()["username"], Is.Empty); Assert.That(session.RuntimeValues()["password"], Is.Empty);
        Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Effective, session.Definition.Find("Password"), "draft"));
        await session.SaveFilesAsync(default); Assert.That(f.Material(), Is.EqualTo(before)); Assert.That(f.XmlPaths.Any(File.Exists), Is.False);
    }

    [Test]
    public async Task OnlyNonsecretLegacyDefaultsFallbackWithoutMigratingOrImportingInlineCredentials()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        Dictionary<string, string?> legacy = new() { ["host"] = "http://old-user:old-secret@proxy.invalid/path", ["port"] = "3128", ["username"] = "explicit-user", ["password"] = "never-import", ["peer"] = "keep" };
        ProxySwitcherSettingsSession session = new(f.Repository, legacy); await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        IReadOnlyDictionary<string, string?> runtime = session.RuntimeValues();
        Assert.That(runtime["host"], Is.EqualTo("http://proxy.invalid/path")); Assert.That(runtime["port"], Is.EqualTo("3128"));
        Assert.That(runtime["username"], Is.EqualTo("explicit-user")); Assert.That(runtime["password"], Is.Empty);
        Assert.That(runtime.Keys, Is.EquivalentTo(new[] { "host", "port", "username", "password" }));
        await session.SaveFilesAsync(default); Assert.That(f.Material(), Is.EqualTo(before)); Assert.That(legacy["password"], Is.EqualTo("never-import"));
    }

    [Test]
    public async Task ModernGuidResolvesAcrossAllScopesBeforeOldPrefixAndEmptyPasswordMasksLowerValues()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); PluginScopedSettingsDefinition d = ProxySwitcherSettingsCatalog.Definition;
        PluginScopedSetting password = d.Find("Password"), host = d.Find("HttpProxy");
        f.Write(GitDistributedSettingsScope.Local, (d.OldKey(host), "old-local"), (d.Key(password), ""));
        f.Write(GitDistributedSettingsScope.Global, (d.Key(host), "modern-global"), (d.Key(password), "lower-secret"));
        ProxySwitcherSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Assert.That(session.RuntimeValues()["host"], Is.EqualTo("modern-global")); Assert.That(session.RuntimeValues()["password"], Is.Empty);
        session.Set(GitDistributedSettingsScope.Local, password, null);
        Assert.That(session.Read(GitDistributedSettingsScope.Effective, password), Is.EqualTo("lower-secret"));
        Assert.That(session.RuntimeValues()["password"], Is.Empty, "Runtime must not consume an unsaved unset intention.");
        await session.SaveFilesAsync(default); Assert.That(session.RuntimeValues()["password"], Is.EqualTo("lower-secret"));
    }

    [TestCase(GitDistributedSettingsScope.Local)] [TestCase(GitDistributedSettingsScope.Distributed)] [TestCase(GitDistributedSettingsScope.Global)]
    public async Task ExplicitStringEditsIncludingPasswordPersistOnlyInTheirChosenFileAndBecomeRuntimeAfterAcceptance(GitDistributedSettingsScope scope)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); ProxySwitcherSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> before = f.Material(); string[] values = ["alice", "accepted-secret", "proxy.invalid", "future-port"];
        for (int i = 0; i < values.Length; i++) { session.Set(scope, session.Definition.Fields[i], values[i]); }
        Assert.That(session.RuntimeValues()["password"], Is.Empty); Assert.That(session.ValidateEdits(out _, out _, out _), Is.True, "The original four settings are strings; Apply must not run BuildProxy.");
        await session.SaveFilesAsync(default); GitXmlSettingsSnapshot accepted = await f.ReadAsync(scope);
        foreach ((PluginScopedSetting field, int index) in session.Definition.Fields.Select((field, index) => (field, index)))
        { Assert.That(accepted.Values[session.Definition.Key(field)], Is.EqualTo(values[index])); }
        foreach ((string path, byte[] bytes) in before.Where(pair => pair.Key != f.Path(scope))) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes)); }
        Assert.That(session.RuntimeValues()["password"], Is.EqualTo("accepted-secret")); Assert.That(session.RuntimeValues()["port"], Is.EqualTo("future-port"));
    }

    [Test]
    public async Task PartialSaveAcknowledgesThePasswordFileAndRetryCannotOverwriteItsLaterPeerEdit()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); ProxySwitcherSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Local, session.Definition.Find("Password"), "accepted-local");
        session.Set(GitDistributedSettingsScope.Global, session.Definition.Find("HttpProxy"), "global.invalid");
        using DetailedSettingsTestScope.BlockedFile block = new(f.Path(GitDistributedSettingsScope.Global));
        Assert.That(async () => await session.SaveFilesAsync(default), Throws.InstanceOf<IOException>());
        GitXmlSettingsSnapshot local = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(local.Values[session.Definition.Key(session.Definition.Find("Password"))], Is.EqualTo("accepted-local"));
        await GitXmlSettingsStore.SaveChangesAsync(local, new Dictionary<string, string?> { ["peer.later"] = "keep" });
        byte[] peer = File.ReadAllBytes(local.Path); block.Restore(); await session.SaveFilesAsync(default);
        Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(peer)); Assert.That(session.HasChanges, Is.False);
    }
}
