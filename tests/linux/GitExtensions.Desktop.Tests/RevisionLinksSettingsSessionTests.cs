using GitCommands.ExternalLinks;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionLinksSettingsSessionTests
{
    [TestCase("none")] [TestCase("worktree")] [TestCase("bare")]
    public async Task ScopeAvailabilityAndUntouchedSaveDoNotCreateFiles(string kind)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind, seed: false);
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Assert.That(session.Loaded, Is.True); Assert.That(session.LoadError, Is.Null);
        Assert.That(session.InitialScope, Is.EqualTo(kind == "none" ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Effective));
        Assert.That(session.HasDistributed, Is.EqualTo(kind == "worktree")); Assert.That(session.Read(GitDistributedSettingsScope.Effective), Is.Empty);
        Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Effective, []));
        if (kind != "worktree") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Distributed, [])); }
        await session.SaveFilesAsync(default); Assert.That(f.XmlPaths.Any(File.Exists), Is.False);
    }

    [Test]
    public async Task UnreadableXmlIsVisibleButAnUntouchedPageDoesNotBlockOtherSettingsSaves()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        File.WriteAllText(f.Path(GitDistributedSettingsScope.Local), "<broken outer XML");
        byte[] before = File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Local));
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Assert.That(session.Loaded, Is.False); Assert.That(session.LoadError, Is.Not.Null);
        await session.SaveFilesAsync(default); Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Local)), Is.EqualTo(before));
    }

    [Test]
    public async Task SameNameDefinitionsAreAdditiveAndDisabledLocalDoesNotMaskInheritedDefinitions()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { ExternalLinkDefinition model = Definition("same", scope.ToString()); model.Enabled = scope != GitDistributedSettingsScope.Local; f.Write(scope, (GitRevisionLinkCodec.SettingName, GitRevisionLinkCodec.Encode([model])!)); }
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        Assert.That(session.Read(GitDistributedSettingsScope.Effective).Select(model => model.LinkFormats.Single().Format), Is.EqualTo(new[] { "Local", "Distributed", "Global" }));
        Assert.That(session.Read(GitDistributedSettingsScope.Effective).Select(model => model.Enabled), Is.EqualTo(new[] { false, true, true }));
        session.Set(GitDistributedSettingsScope.Local, []); Assert.That(session.Read(GitDistributedSettingsScope.Effective).Count, Is.EqualTo(2)); f.AssertMaterial(before);
    }

    [TestCase("<broken inner XML")] [TestCase("<?xml version=\"1.0\"?><ArrayOfGitExtLinkDef><FutureOpaque attr=\"keep\" /></ArrayOfGitExtLinkDef>")]
    public async Task UntouchedMalformedAndUnknownRawInnerValuesRemainByteIdentical(string inner)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        f.Write(GitDistributedSettingsScope.Global, (GitRevisionLinkCodec.SettingName, inner), ("future", "retain <&>"));
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        _ = session.Read(GitDistributedSettingsScope.Effective); _ = session.Read(GitDistributedSettingsScope.Global);
        await session.SaveFilesAsync(default); f.AssertMaterial(before);
    }

    [Test]
    public async Task MutableCallersAndCancelledScopeDraftsCannotModifyDiskOrAnotherRead()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        ExternalLinkDefinition model = Definition("captured", "https://example.invalid/{0}"); session.Set(GitDistributedSettingsScope.Local, [model]); model.Name = "caller mutation"; model.LinkFormats.Clear();
        var first = session.Read(GitDistributedSettingsScope.Local); first[0].Name = "second mutation";
        Assert.That(session.Read(GitDistributedSettingsScope.Local).Single().Name, Is.EqualTo("captured"));
        Assert.That(session.Read(GitDistributedSettingsScope.Local).Single().LinkFormats.Count, Is.EqualTo(1)); f.AssertMaterial(before);
        RevisionLinksSettingsSession reopened = new(f.Repository); await reopened.InitializeAsync(default); Assert.That(reopened.Read(GitDistributedSettingsScope.Local), Is.Empty);
    }

    [TestCase(GitDistributedSettingsScope.Distributed)] [TestCase(GitDistributedSettingsScope.Global)]
    public async Task PartialSaveAcknowledgesEarlierFilesAndRetryDoesNotReplayThem(GitDistributedSettingsScope failure)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { session.Set(scope, [Definition(scope.ToString(), scope.ToString())]); }
        List<string> publications = []; void Saved(string path) => publications.Add(path); RevisionLinksSettingsPublication.Saved += Saved;
        try
        {
            using DetailedSettingsTestScope.BlockedFile blocked = new(f.Path(failure));
            Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
            Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Local)).Values.ContainsKey(GitRevisionLinkCodec.SettingName), Is.True);
            Assert.That(publications, Does.Contain(f.Path(GitDistributedSettingsScope.Local)).And.Not.Contain(f.Path(failure)));
            GitXmlSettingsSnapshot local = await f.ReadAsync(GitDistributedSettingsScope.Local);
            await GitXmlSettingsStore.SaveChangesAsync(local, new Dictionary<string, string?> { ["peer.after-local-save"] = "keep" });
            byte[] peer = File.ReadAllBytes(local.Path); blocked.Restore(); await session.SaveFilesAsync(default);
            Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(peer)); Assert.That(publications.Count(path => path == local.Path), Is.EqualTo(1));
            Assert.That(session.Read(GitDistributedSettingsScope.Effective).Count, Is.EqualTo(3));
        }
        finally { RevisionLinksSettingsPublication.Saved -= Saved; }
    }

    [Test]
    public async Task ExternalPeerWriteIsRefusedWithoutSilentRereadAndDraftRemainsAvailable()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Global, [Definition("draft", "draft")]);
        GitXmlSettingsSnapshot before = await f.ReadAsync(GitDistributedSettingsScope.Global);
        await GitXmlSettingsStore.SaveChangesAsync(before, new Dictionary<string, string?> { ["external"] = "peer" }); byte[] peer = File.ReadAllBytes(before.Path);
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
        Assert.That(File.ReadAllBytes(before.Path), Is.EqualTo(peer)); Assert.That(session.Read(GitDistributedSettingsScope.Global).Single().Name, Is.EqualTo("draft"));
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default)); Assert.That(File.ReadAllBytes(before.Path), Is.EqualTo(peer));
    }

    [Test]
    public async Task SharedWorkspaceAllowsDetailedThenLinksThenCiWithoutReplayingTheirAcceptedKeys()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DistributedSettingsWorkspace workspace = new(f.Repository);
        DetailedSettingsSession detailed = new(f.Repository, f.Desktop.Load(), workspace);
        RevisionLinksSettingsSession links = new(f.Repository, workspace);
        GitHubActionsSettingsSession ci = new(f.Repository, workspace);
        await detailed.InitializeAsync(default); await links.InitializeAsync(default); await ci.InitializeAsync(default);
        detailed.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, "43");
        links.Set(GitDistributedSettingsScope.Local, [Definition("links", "target")]);
        ci.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.ProviderKey, "future-provider");
        await detailed.SaveFilesAsync(default); await links.SaveFilesAsync(default); await ci.SaveFilesAsync(default);
        GitXmlSettingsSnapshot saved = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(saved.Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("43"));
        Assert.That(saved.Values[GitHubActionsSettings.ProviderKey], Is.EqualTo("future-provider"));
        Assert.That(GitRevisionLinkCodec.Decode(saved.Values[GitRevisionLinkCodec.SettingName]).Definitions.Single().Name, Is.EqualTo("links"));
    }

    [Test]
    public async Task RevertingSemanticDraftPreservesAnUntouchedRawDocumentAndPeerEdit()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); string inner = GitRevisionLinkCodec.Encode([Definition("original", "uri")])!;
        f.Write(GitDistributedSettingsScope.Global, (GitRevisionLinkCodec.SettingName, inner), ("future", "opaque"));
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        var original = session.Read(GitDistributedSettingsScope.Global); session.Set(GitDistributedSettingsScope.Global, [Definition("draft", "other")]); session.Set(GitDistributedSettingsScope.Global, original);
        GitXmlSettingsSnapshot before = await f.ReadAsync(GitDistributedSettingsScope.Global);
        await GitXmlSettingsStore.SaveChangesAsync(before, new Dictionary<string, string?> { ["peer"] = "later" }); byte[] peer = File.ReadAllBytes(before.Path);
        await session.SaveFilesAsync(default); Assert.That(File.ReadAllBytes(before.Path), Is.EqualTo(peer));
    }

    internal static ExternalLinkDefinition Definition(string name, string uri) => new()
    {
        Name = name, Enabled = true, SearchPattern = "#(\\d+)", SearchInParts = { ExternalLinkDefinition.RevisionPart.Message },
        LinkFormats = { new() { Caption = "Issue #{0}", Format = uri } }
    };
}
