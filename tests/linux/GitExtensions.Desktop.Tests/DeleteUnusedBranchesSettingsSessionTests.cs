using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DeleteUnusedBranchesSettingsSessionTests
{
    private static DeleteUnusedBranchesSetting Setting(string name) => DeleteUnusedBranchesSettingsCatalog.Find(name);

    [Test]
    public void CatalogUsesTheOriginalNineSettingsInGetSettingsOrderWithoutExecutionParameters()
    {
        Assert.That(DeleteUnusedBranchesSettingsCatalog.Fields.Select(item => item.EngineKey), Is.EqualTo(new[]
        { "olderThanDays", "mergedInto", "remoteBranches", "remote", "useRegex", "pattern", "ignoreCase", "invertPattern", "includeUnmerged" }));
        Assert.That(DeleteUnusedBranchesSettingsCatalog.Fields.Select(item => item.DefaultValue), Is.EqualTo(new[]
        { "30", "HEAD", "false", "origin", "false", "/(feature|develop)/", "false", "false", "false" }));
        Assert.That(DeleteUnusedBranchesSettingsCatalog.Prefix, Is.EqualTo(new DeleteUnusedBranchesPlugin().Descriptor.Id + "."));
        Assert.That(DeleteUnusedBranchesSettingsCatalog.Fields.Count(item => item.Kind == DeleteUnusedBranchesSettingKind.Boolean), Is.EqualTo(5));
        Assert.That(DeleteUnusedBranchesSettingsCatalog.Fields.All(item => item.Key.StartsWith("dc3ca904-b9a5-4fe8-bf63-5b8ee9c2ddac.", StringComparison.Ordinal)), Is.True);
    }

    [TestCase("none")] [TestCase("worktree")] [TestCase("bare")]
    public async Task ScopeAvailabilityAndUntouchedSaveDoNotCreateOrMigrateFiles(string kind)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind, seed: false);
        DeleteUnusedBranchesSettingsSession session = new(f.Repository);
        await session.InitializeAsync(default);
        Assert.That(session.Loaded, Is.True); Assert.That(session.LoadError, Is.Null);
        Assert.That(session.InitialScope, Is.EqualTo(kind == "none" ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Effective));
        Assert.That(session.HasDistributed, Is.EqualTo(kind == "worktree"));
        Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Effective, Setting("_remoteName"), "peer"));
        if (kind == "none") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Local, Setting("_remoteName"), "peer")); }
        if (kind != "worktree") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Distributed, Setting("_remoteName"), "peer")); }
        await session.SaveFilesAsync(default);
        Assert.That(f.XmlPaths.Any(File.Exists), Is.False); Assert.That(session.HasChanges, Is.False);
        Assert.That(session.EngineValues()["olderThanDays"], Is.EqualTo("30"));
    }

    [Test]
    public async Task TheGuidNamespaceAcrossAllScopesPrecedesTheOldDescriptionNamespaceBeforeParsing()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DeleteUnusedBranchesSetting regex = Setting("_useRegexToFilterBranchesFlag"), remote = Setting("_remoteName");
        f.Write(GitDistributedSettingsScope.Global, (regex.Key, "true"), (remote.Key, "global-new"));
        f.Write(GitDistributedSettingsScope.Local, (regex.OldKey, "false"), (remote.OldKey, "local-old"));
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Assert.That(session.EngineValues()["useRegex"], Is.EqualTo("true"));
        Assert.That(session.EngineValues()["remote"], Is.EqualTo("global-new"));
        session.Set(GitDistributedSettingsScope.Local, regex, "future-boolean");
        Assert.That(session.Read(GitDistributedSettingsScope.Local, regex), Is.EqualTo("future-boolean"));
        Assert.That(session.EngineValues()["useRegex"], Is.EqualTo("false"), "Unknown local raw masks the valid lower GUID value before typed defaulting.");
        session.Set(GitDistributedSettingsScope.Local, regex, null);
        Assert.That(session.EngineValues()["useRegex"], Is.EqualTo("true"));
        session.Set(GitDistributedSettingsScope.Global, regex, null);
        Assert.That(session.EngineValues()["useRegex"], Is.EqualTo("false"), "Removing the new key reveals the untouched old compatibility key.");
    }

    [Test]
    public async Task LegacyJsonIsOnlyAFallbackAndExplicitXmlFalseDisablesItsHistoricalNonemptyRegex()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        Dictionary<string, string?> legacy = new() { ["olderThanDays"] = "45", ["pattern"] = "^legacy/", ["future-plugin-field"] = "λ untouched" };
        DeleteUnusedBranchesSettingsSession session = new(f.Repository, legacy); await session.InitializeAsync(default);
        Dictionary<string, byte[]> before = f.Material();
        Assert.That(session.EngineValues()["useRegex"], Is.EqualTo("true"));
        Assert.That(session.EngineValues()["olderThanDays"], Is.EqualTo("45"));
        await session.SaveFilesAsync(default); f.AssertMaterial(before);
        session.Set(GitDistributedSettingsScope.Global, Setting("_useRegexToFilterBranchesFlag"), "false");
        await session.SaveFilesAsync(default);
        DeleteUnusedBranchesSettingsSession reopened = new(f.Repository, legacy); await reopened.InitializeAsync(default);
        Assert.That(reopened.EngineValues()["useRegex"], Is.EqualTo("false"));
        Assert.That(reopened.EngineValues()["pattern"], Is.EqualTo("^legacy/"));
        Assert.That(reopened.EngineValues()["future-plugin-field"], Is.EqualTo("λ untouched"));
        Assert.That(legacy.ContainsKey("useRegex"), Is.False, "The fallback dictionary itself is never migrated or modified.");
    }

    [Test]
    public async Task UntouchedUnknownPluginValuesAndOtherProvidersKeepTheirExactBytes()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { f.Write(scope, (Setting("_daysOlderThan").Key, "2147483648"), (Setting("_regexInvertedFlag").Key, "future-bool"),
            (Setting("_regexFilter").Key, ""), ("BuildServer.future-private", "keep literal λ & <value>")); }
        Dictionary<string, byte[]> before = f.Material();
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { _ = session.Read(scope, Setting("_daysOlderThan")); _ = session.Read(scope, Setting("_regexInvertedFlag")); }
        await session.SaveFilesAsync(default); f.AssertMaterial(before); Assert.That(session.HasChanges, Is.False);
    }

    [Test]
    public async Task ASharedWorkspacePreservesEarlierDetailedWritesAndOnlyWritesTheEditedPluginKey()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DistributedSettingsWorkspace workspace = new(f.Repository);
        DetailedSettingsSession detailed = new(f.Repository, f.Desktop.Load(), workspace);
        DeleteUnusedBranchesSettingsSession plugin = new(f.Repository, workspace: workspace);
        await detailed.InitializeAsync(default); await plugin.InitializeAsync(default);
        plugin.Set(GitDistributedSettingsScope.Global, Setting("_regexFilter"), "^keep & <λ>$");
        detailed.Set(GitDistributedSettingsScope.Global, DetailedRepositorySettings.MergeLogCount, "41");
        await detailed.SaveFilesAsync(default); await plugin.SaveFilesAsync(default);
        GitXmlSettingsSnapshot saved = await f.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(saved.Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("41"));
        Assert.That(saved.Values[Setting("_regexFilter").Key], Is.EqualTo("^keep & <λ>$"));
        Assert.That(saved.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
        Assert.That(saved.Values.Keys.Count(key => key.StartsWith(DeleteUnusedBranchesSettingsCatalog.Prefix, StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(plugin.HasChanges, Is.False);
    }

    [TestCase(GitDistributedSettingsScope.Distributed)] [TestCase(GitDistributedSettingsScope.Global)]
    public async Task PartialSaveAcknowledgesCompletedFilesAndRetryDoesNotReplayThemOverAPeer(GitDistributedSettingsScope failure)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        DeleteUnusedBranchesSetting setting = Setting("_remoteName");
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { session.Set(scope, setting, "saved-" + scope); }
        using DetailedSettingsTestScope.BlockedFile block = new(f.Path(failure));
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
        GitDistributedSettingsScope[] accepted = failure == GitDistributedSettingsScope.Global
            ? [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed] : [GitDistributedSettingsScope.Local];
        Dictionary<string, byte[]> peerFiles = [];
        foreach (GitDistributedSettingsScope scope in accepted)
        {
            GitXmlSettingsSnapshot file = await f.ReadAsync(scope);
            Assert.That(file.Values[setting.Key], Is.EqualTo("saved-" + scope));
            GitXmlSettingsSnapshot peer = await GitXmlSettingsStore.SaveChangesAsync(file,
                new Dictionary<string, string?> { [setting.Key] = "peer-after-partial", ["peer.future"] = "keep" });
            peerFiles[peer.Path] = File.ReadAllBytes(peer.Path);
        }
        block.Restore(); await session.SaveFilesAsync(default);
        foreach ((string path, byte[] bytes) in peerFiles) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes)); }
        Assert.That((await f.ReadAsync(failure)).Values[setting.Key], Is.EqualTo("saved-" + failure));
        Assert.That(session.HasChanges, Is.False);
    }

    [Test]
    public async Task AnExternalEditIsAVisibleConflictAndDoesNotDiscardOrRebaseThePluginDraft()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        DeleteUnusedBranchesSetting setting = Setting("_remoteName"); session.Set(GitDistributedSettingsScope.Global, setting, "mine");
        GitXmlSettingsSnapshot before = await f.ReadAsync(GitDistributedSettingsScope.Global);
        await GitXmlSettingsStore.SaveChangesAsync(before, new Dictionary<string, string?> { [setting.Key] = "peer" });
        byte[] peer = File.ReadAllBytes(before.Path);
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
        Assert.That(session.Read(GitDistributedSettingsScope.Global, setting), Is.EqualTo("mine"));
        Assert.That(session.HasChanges, Is.True); Assert.That(File.ReadAllBytes(before.Path), Is.EqualTo(peer));
    }

    [Test]
    public async Task AnUnreadableUntouchedPluginPageDoesNotBlockAnotherSettingsPageOrRepairXml()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        File.WriteAllText(f.Path(GitDistributedSettingsScope.Global), "<not a valid dictionary Ω");
        byte[] before = File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global));
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Assert.That(session.LoadError, Is.Not.Null); Assert.That(session.Loaded, Is.False);
        await session.SaveFilesAsync(default);
        Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global)), Is.EqualTo(before));
        Assert.Throws<InvalidOperationException>(() => session.EngineValues());
    }

    [TestCase("0")] [TestCase("100000")]
    public async Task EditedAgeUsesTheActualPreviewAndEngineBounds(string days)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Global, Setting("_daysOlderThan"), days); await session.SaveFilesAsync(default);
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[Setting("_daysOlderThan").Key], Is.EqualTo(days));
    }

    [TestCase("-1")] [TestCase("100001")] [TestCase("2147483648")] [TestCase("not a number")]
    public async Task InvalidEditedAgeIsRetainedAsADraftAndCannotReachTheWriter(string days)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DeleteUnusedBranchesSettingsSession session = new(f.Repository); await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        session.Set(GitDistributedSettingsScope.Global, Setting("_daysOlderThan"), days);
        Assert.That(session.ValidateEdits(out GitDistributedSettingsScope scope), Is.False); Assert.That(scope, Is.EqualTo(GitDistributedSettingsScope.Global));
        Assert.CatchAsync<ArgumentException>(async () => await session.SaveFilesAsync(default));
        f.AssertMaterial(before); Assert.That(session.Read(scope, Setting("_daysOlderThan")), Is.EqualTo(days));
    }
}
