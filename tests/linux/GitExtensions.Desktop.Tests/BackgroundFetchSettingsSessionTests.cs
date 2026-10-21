using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BackgroundFetchSettingsSessionTests
{
    private static PluginScopedSetting Field(string name) => BackgroundFetchSettingsCatalog.Find(name);
    private static string Key(string name) => BackgroundFetchSettingsCatalog.Definition.Key(Field(name));

    [Test]
    public void TheFiveOriginalBindingsKeepTheirNamesOrderDefaultsAndNonpersistentWarning()
    {
        PluginScopedSettingsDefinition definition = BackgroundFetchSettingsCatalog.Definition;
        Assert.That(definition.Id, Is.EqualTo(BackgroundFetchService.Descriptor.Id));
        Assert.That(definition.Fields.Select(field => field.RuntimeKey), Is.EqualTo(new[] { "arguments", "interval", "autoRefresh", "submodules", "immediate" }));
        Assert.That(definition.Fields.Select(field => field.DefaultValue), Is.EqualTo(new[] { "fetch --all", "0", "false", "false", "false" }));
        Assert.That(definition.Fields.Select(field => field.Name), Is.EqualTo(BackgroundFetchService.Descriptor.Parameters.Select(field => field.Label)));
        Assert.That(definition.Key(Field("_gitCommand")), Is.EqualTo("d19a7905-8aad-4271-aca9-817669b94a1d.Arguments of git command to run"));
        Assert.That(definition.OldKey(Field("_gitCommand")), Is.EqualTo("Periodic background fetchArguments of git command to run"));
        Assert.That(definition.Warning, Is.EqualTo(new PluginScopedWarning("_warningForceWithLease", BackgroundFetchSettingsCatalog.Warning, 500)));
        Assert.That(definition.Fields.Any(field => field.Name == "PseudoSetting"), Is.False);
    }

    [TestCase("none")] [TestCase("worktree")] [TestCase("bare")]
    public async Task ScopesAndUntouchedSaveNeverCreateOrMigrateFiles(string kind)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind, seed: false);
        BackgroundFetchSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Assert.That(session.Loaded, Is.True); Assert.That(session.HasDistributed, Is.EqualTo(kind == "worktree"));
        Assert.That(session.InitialScope, Is.EqualTo(kind == "none" ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Effective));
        Assert.That(session.EffectiveOptions(), Is.EqualTo(new BackgroundFetchOptions()));
        Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Effective, Field("_gitCommand"), "fetch"));
        if (kind == "none") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Local, Field("_gitCommand"), "fetch")); }
        if (kind != "worktree") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Distributed, Field("_gitCommand"), "fetch")); }
        await session.SaveFilesAsync(default); Assert.That(f.XmlPaths.Any(File.Exists), Is.False);
    }

    [Test]
    public async Task GuidLookupAcrossAllScopesPrecedesLegacyPrefixAndJsonWithoutRewritingEither()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        PluginScopedSettingsDefinition definition = BackgroundFetchSettingsCatalog.Definition;
        f.Write(GitDistributedSettingsScope.Global, (Key("_gitCommand"), "global-modern"), ("foreign", "keep"));
        f.Write(GitDistributedSettingsScope.Local, (definition.OldKey(Field("_gitCommand")), "local-old"));
        BackgroundFetchSettingsSession session = new(f.Repository, new Dictionary<string, string?> { ["arguments"] = "json", ["future-option"] = "preserved" });
        await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        Assert.That(session.Read(GitDistributedSettingsScope.Local, Field("_gitCommand")), Is.EqualTo("local-old"));
        Assert.That(session.EffectiveOptions().Arguments, Is.EqualTo("global-modern"));
        session.Set(GitDistributedSettingsScope.Global, Field("_gitCommand"), null);
        Assert.That(session.Read(GitDistributedSettingsScope.Effective, Field("_gitCommand")), Is.EqualTo("local-old"));
        Assert.That(session.EffectiveOptions().Arguments, Is.EqualTo("global-modern"), "Runtime values must exclude an unsaved scope intention.");
        await session.SaveFilesAsync(default);
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Local)).Values[definition.OldKey(Field("_gitCommand"))], Is.EqualTo("local-old"));
        Assert.That(session.EffectiveOptions().Arguments, Is.EqualTo("local-old"));
        Assert.That(session.EffectiveValues()["future-option"], Is.EqualTo("preserved"));
        Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Local)), Is.EqualTo(before[f.Path(GitDistributedSettingsScope.Local)]));
    }

    [Test]
    public async Task UntouchedUnknownAndNegativeRawValuesRemainExactWhileRealEditsUseInt32AndExistingCommandParser()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        f.Write(GitDistributedSettingsScope.Global, (Key("_fetchInterval"), "-1"), (Key("_autoRefresh"), "future-bool"), ("future.plugin", "keep λ"));
        BackgroundFetchSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> before = f.Material(); Assert.That(session.EffectiveOptions().FetchEverySeconds, Is.EqualTo(-1));
        Assert.That(session.Read(GitDistributedSettingsScope.Global, Field("_autoRefresh")), Is.EqualTo("future-bool"));
        Assert.That(session.EffectiveOptions().AutoRefresh, Is.False); await session.SaveFilesAsync(default); f.AssertMaterial(before);
        session.Set(GitDistributedSettingsScope.Global, Field("_fetchInterval"), "-2");
        Assert.That(session.ValidateEdits(out _, out string? invalid, out _), Is.False); Assert.That(invalid, Is.EqualTo("_fetchInterval"));
        session.Set(GitDistributedSettingsScope.Global, Field("_fetchInterval"), "40000");
        session.Set(GitDistributedSettingsScope.Global, Field("_gitCommand"), "fetch 'unterminated");
        Assert.That(session.ValidateEdits(out _, out invalid, out _), Is.False); Assert.That(invalid, Is.EqualTo("_gitCommand"));
        session.Set(GitDistributedSettingsScope.Global, Field("_gitCommand"), "config --local test.literal \"a b;$x\"");
        Assert.That(session.ValidateEdits(out _, out _, out _), Is.True);
        await session.SaveFilesAsync(default);
        Assert.That(session.EffectiveOptions().FetchEverySeconds, Is.EqualTo(40000), "The old PluginValues.Integer cap must not constrain the original Int32 setting.");
        Assert.That(session.EffectiveOptions().GetCommandArguments(), Is.EqualTo(new[] { "config", "--local", "test.literal", "a b;$x" }));
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[Key("_autoRefresh")], Is.EqualTo("future-bool"));
    }

    [Test]
    public async Task ASharedWorkspacePreservesOtherScopedPagesAndUnknownKeysInTheSameFile()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DistributedSettingsWorkspace workspace = new(f.Repository);
        BackgroundFetchSettingsSession fetch = new(f.Repository, workspace: workspace);
        DeleteUnusedBranchesSettingsSession obsolete = new(f.Repository, workspace: workspace);
        DetailedSettingsSession detailed = new(f.Repository, f.Desktop.Load(), workspace);
        await fetch.InitializeAsync(default); await obsolete.InitializeAsync(default); await detailed.InitializeAsync(default);
        fetch.Set(GitDistributedSettingsScope.Global, Field("_autoRefresh"), "true");
        DeleteUnusedBranchesSetting remote = DeleteUnusedBranchesSettingsCatalog.Find("_remoteName");
        obsolete.Set(GitDistributedSettingsScope.Global, remote, "kept-remote");
        detailed.Set(GitDistributedSettingsScope.Global, DetailedRepositorySettings.MergeLogCount, "31");
        await detailed.SaveFilesAsync(default); await obsolete.SaveFilesAsync(default); await fetch.SaveFilesAsync(default);
        GitXmlSettingsSnapshot xml = await f.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(xml.Values[Key("_autoRefresh")], Is.EqualTo("true")); Assert.That(xml.Values[remote.Key], Is.EqualTo("kept-remote"));
        Assert.That(xml.Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("31")); Assert.That(xml.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
    }

    [Test]
    public async Task PartialFileFailurePublishesOnlyAcceptedValuesAndRetryDoesNotReplayThemOverAPeer()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        BackgroundFetchSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Local, Field("_autoRefresh"), "true");
        session.Set(GitDistributedSettingsScope.Global, Field("_gitCommand"), "config --local test.pending not-yet-saved");
        List<PluginScopedSettingsSaveReceipt> published = [];
        using DetailedSettingsTestScope.BlockedFile block = new(f.Path(GitDistributedSettingsScope.Global));
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default, receipt => { published.Add(receipt); return Task.CompletedTask; }));
        Assert.That(published, Has.Count.EqualTo(1));
        Assert.That(published[0].Scope, Is.EqualTo(GitDistributedSettingsScope.Local));
        Assert.That(published[0].EffectiveValues["arguments"], Is.EqualTo("fetch --all"));
        Assert.That(published[0].EffectiveValues["autoRefresh"], Is.EqualTo("true"));
        GitXmlSettingsSnapshot peer = await f.ReadAsync(GitDistributedSettingsScope.Local);
        await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { [Key("_autoRefresh")] = "false", ["peer.after-accepted"] = "keep" });
        byte[] peerBytes = File.ReadAllBytes(peer.Path); block.Restore();
        await session.SaveFilesAsync(default, receipt => { published.Add(receipt); return Task.CompletedTask; });
        Assert.That(File.ReadAllBytes(peer.Path), Is.EqualTo(peerBytes)); Assert.That(published, Has.Count.EqualTo(2));
        Assert.That(published[1].EffectiveValues["arguments"], Is.EqualTo("config --local test.pending not-yet-saved"));
        Assert.That(session.HasChanges, Is.False);
    }

    [Test]
    public async Task PublicationFailureKeepsAReceiptButNeverReplaysAnAcknowledgedFileOnRetry()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        BackgroundFetchSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Global, Field("_autoRefresh"), "true");
        Assert.ThrowsAsync<IOException>(async () => await session.SaveFilesAsync(default, _ => throw new IOException("Fixture publication failed")));
        Assert.That(session.HasPendingPublication, Is.True); Assert.That(session.HasChanges, Is.True);
        GitXmlSettingsSnapshot peer = await f.ReadAsync(GitDistributedSettingsScope.Global);
        await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { ["peer.after-publication"] = "keep" });
        byte[] bytes = File.ReadAllBytes(peer.Path); int publications = 0;
        await session.SaveFilesAsync(default, receipt => { publications++; Assert.That(receipt.EffectiveValues["autoRefresh"], Is.EqualTo("true")); return Task.CompletedTask; });
        Assert.That(publications, Is.EqualTo(1)); Assert.That(File.ReadAllBytes(peer.Path), Is.EqualTo(bytes)); Assert.That(session.HasChanges, Is.False);
    }

    [Test]
    public async Task AnExplicitCorrectionCanSupersedeAFailedPublicationWithoutRetryingTheOldCommandFirst()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        BackgroundFetchSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Global, Field("_gitCommand"), "fixture-old");
        Assert.ThrowsAsync<InvalidOperationException>(async () => await session.SaveFilesAsync(default, _ => throw new InvalidOperationException("Fixture host failed")));
        session.Set(GitDistributedSettingsScope.Global, Field("_gitCommand"), "fixture-corrected"); List<string?> commands = [];
        await session.SaveFilesAsync(default, receipt => { commands.Add(receipt.EffectiveValues["arguments"]); return Task.CompletedTask; });
        Assert.That(commands, Is.EqualTo(new[] { "fixture-corrected" })); Assert.That(session.HasChanges, Is.False);
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[Key("_gitCommand")], Is.EqualTo("fixture-corrected"));
    }

    [Test]
    public async Task UnreadableUntouchedXmlIsVisibleButCannotBlockAnotherPagesNoopSave()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        File.WriteAllText(f.Path(GitDistributedSettingsScope.Global), "<broken");
        BackgroundFetchSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        byte[] bytes = File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global));
        Assert.That(session.Loaded, Is.False); Assert.That(session.LoadError, Is.Not.Null); await session.SaveFilesAsync(default);
        Assert.Throws<InvalidOperationException>(() => session.EffectiveOptions()); Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Global)), Is.EqualTo(bytes));
    }
}
