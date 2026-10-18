using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DetailedSettingsSessionTests
{
    [TestCase("none")] [TestCase("worktree")] [TestCase("bare")]
    public async Task AvailabilityAndInitialScopeMatchRepositoryTopologyWithoutCreatingSettingsFiles(string repositoryKind)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(repositoryKind, seed: false);
        DetailedSettingsSession session = await f.SessionAsync();
        Assert.That(session.Loaded, Is.True); Assert.That(session.LoadError, Is.Null);
        Assert.That(session.HasRepository, Is.EqualTo(repositoryKind != "none"));
        Assert.That(session.HasDistributed, Is.EqualTo(repositoryKind == "worktree"));
        Assert.That(session.InitialScope, Is.EqualTo(repositoryKind == "none" ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Effective));
        Assert.That(session.Read(GitDistributedSettingsScope.Effective), Is.EqualTo(new DetailedScopeValues(false, false, "20")));
        Assert.That(session.Read(GitDistributedSettingsScope.Global), Is.EqualTo(new DetailedScopeValues(null, null, "")));
        Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Effective, DetailedRepositorySettings.RemoteBranches, "true"));
        if (repositoryKind == "none") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, "true")); }
        if (repositoryKind != "worktree") { Assert.Throws<InvalidOperationException>(() => session.Set(GitDistributedSettingsScope.Distributed, DetailedRepositorySettings.RemoteBranches, "true")); }
        await session.SaveFilesAsync(default);
        Assert.That(f.XmlPaths.Any(File.Exists), Is.False, "Initialization and an untouched save must never create distributed settings files.");
    }

    [Test]
    public async Task EffectiveReadsResolveRawPrecedenceBeforeParsingAndUnsetsRevealTheNextScopeDraft()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        f.Write(GitDistributedSettingsScope.Global, (DetailedRepositorySettings.RemoteBranches, "true"), (DetailedRepositorySettings.AddMergeLog, "true"), (DetailedRepositorySettings.MergeLogCount, "99"));
        f.Write(GitDistributedSettingsScope.Distributed, (DetailedRepositorySettings.RemoteBranches, "false"), (DetailedRepositorySettings.MergeLogCount, "17"));
        f.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.RemoteBranches, "future-boolean"), (DetailedRepositorySettings.AddMergeLog, "FALSE"), (DetailedRepositorySettings.MergeLogCount, ""));
        DetailedSettingsSession session = await f.SessionAsync(); Dictionary<string, byte[]> material = f.Material();
        Assert.That(session.Read(GitDistributedSettingsScope.Local), Is.EqualTo(new DetailedScopeValues(null, false, "")));
        Assert.That(session.Read(GitDistributedSettingsScope.Effective), Is.EqualTo(new DetailedScopeValues(false, false, "20")), "Unknown or empty local raw values mask lower scopes before typed defaults apply.");
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, null);
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, null);
        Assert.That(session.Read(GitDistributedSettingsScope.Effective), Is.EqualTo(new DetailedScopeValues(false, false, "17")));
        session.Set(GitDistributedSettingsScope.Distributed, DetailedRepositorySettings.RemoteBranches, null);
        Assert.That(session.Read(GitDistributedSettingsScope.Effective).RemoteBranches, Is.True);
        f.AssertMaterial(material);
    }

    [Test]
    public async Task UntouchedUnknownRawValuesAndFutureKeysRemainByteIdenticalAfterSave()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { f.Write(scope, (DetailedRepositorySettings.RemoteBranches, "TrueFuture"), (DetailedRepositorySettings.AddMergeLog, ""), (DetailedRepositorySettings.MergeLogCount, "2147483648"), ("future.λ", "literal <value> & body\n")); }
        DetailedSettingsSession session = await f.SessionAsync(); Dictionary<string, byte[]> before = f.Material();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { Assert.That(session.Read(scope), Is.EqualTo(new DetailedScopeValues(null, null, ""))); }
        await session.SaveFilesAsync(default); f.AssertMaterial(before);
    }

    [Test]
    public async Task MultipleScopeDraftsChangeEffectiveReadsButOnlyTheirOwnFilesAreWrittenWhenSaved()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DetailedSettingsSession session = await f.SessionAsync();
        Dictionary<string, byte[]> before = f.Material();
        session.Set(GitDistributedSettingsScope.Global, DetailedRepositorySettings.MergeLogCount, "2147483647");
        session.Set(GitDistributedSettingsScope.Distributed, DetailedRepositorySettings.AddMergeLog, "true");
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, "true");
        Assert.That(session.Read(GitDistributedSettingsScope.Effective), Is.EqualTo(new DetailedScopeValues(true, false, "20")), "Seeded higher-scope raw false/20 still mask lower-scope drafts.");
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.AddMergeLog, null);
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, null);
        session.Set(GitDistributedSettingsScope.Distributed, DetailedRepositorySettings.MergeLogCount, null);
        Assert.That(session.Read(GitDistributedSettingsScope.Effective), Is.EqualTo(new DetailedScopeValues(true, true, "2147483647")));
        f.AssertMaterial(before); await session.SaveFilesAsync(default);
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Local)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Distributed)).Values[DetailedRepositorySettings.AddMergeLog], Is.EqualTo("true"));
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("2147483647"));
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { Assert.That((await f.ReadAsync(scope)).Values["future.λ"], Is.EqualTo("keep <literal> & \nbody")); }
    }

    [TestCase(GitDistributedSettingsScope.Distributed)] [TestCase(GitDistributedSettingsScope.Global)]
    public async Task PartialSaveAcknowledgesEachCompletedFileAndRetryNeverReplaysItOverAPeer(GitDistributedSettingsScope failure)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DetailedSettingsSession session = await f.SessionAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { session.Set(scope, DetailedRepositorySettings.RemoteBranches, "true"); }
        using DetailedSettingsTestScope.BlockedFile block = new(f.Path(failure));
        Exception? error = Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
        Assert.That(error, Is.Not.Null);
        GitDistributedSettingsScope[] accepted = failure == GitDistributedSettingsScope.Distributed ? [GitDistributedSettingsScope.Local] : [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed];
        Dictionary<string, byte[]> peerFiles = [];
        foreach (GitDistributedSettingsScope scope in accepted)
        {
            Assert.That((await f.ReadAsync(scope)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
            Assert.That(session.Read(scope).RemoteBranches, Is.True, "The completed write must be acknowledged before attempting the next file.");
            GitXmlSettingsSnapshot peer = await f.ReadAsync(scope);
            await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { [DetailedRepositorySettings.RemoteBranches] = "false", ["peer.after-partial"] = scope.ToString() });
            peerFiles[f.Path(scope)] = File.ReadAllBytes(f.Path(scope));
        }
        block.Restore(); await session.SaveFilesAsync(default);
        foreach ((string path, byte[] bytes) in peerFiles) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "Retry must not revisit an already completed destination."); }
        Assert.That((await f.ReadAsync(failure)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
        Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
    }

    [TestCase("Merge")] [TestCase("Diagonals")] [TestCase("Straighten")]
    public async Task AnAcceptedDesktopGraphIntentionIsNotReplayedAfterAPartialXmlFailure(string field)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DesktopPreferences initial = f.Desktop.Load(); DetailedSettingsSession session = await f.SessionAsync();
        session.Graph = DetailedSettingsTestScope.Edit(session.Graph, field, false);
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.MergeLogCount, "31");
        session.Set(GitDistributedSettingsScope.Global, DetailedRepositorySettings.RemoteBranches, "true");
        DesktopPreferences saved = session.MergeDesktop(initial); f.Desktop.Save(saved); session.DesktopSaved(saved);
        using DetailedSettingsTestScope.BlockedFile block = new(f.Path(GitDistributedSettingsScope.Global));
        Assert.CatchAsync<IOException>(async () => await session.SaveFilesAsync(default));
        DesktopPreferences peer = initial with { MergeGraphLanesHavingCommonParent = true, RenderGraphWithDiagonals = true, StraightenGraphDiagonals = true, Theme = "Dark" };
        f.Desktop.Save(peer); DesktopPreferences retry = session.MergeDesktop(peer);
        Assert.That(DetailedGraphSettings.From(retry), Is.EqualTo(DetailedGraphSettings.From(peer)), "An already saved intention is accepted even when a later XML destination failed.");
        f.Desktop.Save(retry); session.DesktopSaved(retry); block.Restore(); await session.SaveFilesAsync(default);
        Assert.That(session.Graph, Is.EqualTo(DetailedGraphSettings.From(peer)));
    }

    [TestCase("Merge")] [TestCase("Diagonals")] [TestCase("Straighten")]
    public async Task AnUnacceptedGraphDraftSurvivesADesktopReadFailureAndMergesOtherPeerFields(string field)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DetailedSettingsSession session = await f.SessionAsync();
        RevisionGraphOptions draft = DetailedSettingsTestScope.Edit(session.Graph, field, false); session.Graph = draft;
        File.WriteAllText(f.Desktop.FilePath, "{broken-desktop-profile");
        Assert.Throws<JsonException>(() => session.MergeDesktop(new())); Assert.That(session.Graph, Is.EqualTo(draft));
        DesktopPreferences peer = new() { MergeGraphLanesHavingCommonParent = false, RenderGraphWithDiagonals = false, StraightenGraphDiagonals = false, Theme = "Dark" };
        peer = DetailedSettingsTestScope.WithGraph(peer, DetailedSettingsTestScope.Edit(DetailedGraphSettings.From(peer), field, true));
        f.Desktop.Save(peer); DesktopPreferences merged = session.MergeDesktop(peer);
        Assert.That(DetailedGraphSettings.From(merged), Is.EqualTo(new RevisionGraphOptions(false, false, false)));
        Assert.That(merged.Theme, Is.EqualTo("Dark")); Assert.That(session.Graph, Is.EqualTo(draft));
    }

    [Test]
    public async Task RevertingADraftToItsRawBaselineDoesNotOverwriteALaterPeerValue()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); DetailedSettingsSession session = await f.SessionAsync();
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, "true");
        session.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, "false");
        GitXmlSettingsSnapshot peer = await f.ReadAsync(GitDistributedSettingsScope.Local);
        await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { [DetailedRepositorySettings.RemoteBranches] = "true" });
        byte[] afterPeer = File.ReadAllBytes(peer.Path); await session.SaveFilesAsync(default);
        Assert.That(File.ReadAllBytes(peer.Path), Is.EqualTo(afterPeer), "A reverted intention must not retain an obsolete write plan.");
    }
}

internal sealed class DetailedSettingsTestScope : IDisposable
{
    internal static readonly GitDistributedSettingsScope[] EditableScopes = [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global];
    internal GitFixture Git { get; }
    internal GitRepository? Repository { get; }
    internal PreferencesStore Desktop { get; }
    private readonly Dictionary<GitDistributedSettingsScope, string> _paths;
    private DetailedSettingsTestScope(GitFixture git, GitRepository? repository, Dictionary<GitDistributedSettingsScope, string> paths)
    { Git = git; Repository = repository; _paths = paths; Desktop = new(); Desktop.Save(new() { Language = "English" }); }
    internal IEnumerable<string> XmlPaths => _paths.Values;
    internal static async Task<DetailedSettingsTestScope> CreateAsync(string kind = "worktree", bool seed = true)
    {
        GitFixture git = await GitFixture.CreateAsync();
        try
        {
            GitRepository? repository = kind == "none" ? null : git.Repository;
            if (kind == "bare") { string bare = git.Output("bare.git"); await git.GitAsync("clone", "--bare", git.Repository.Path, bare); repository = new(bare); }
            Dictionary<GitDistributedSettingsScope, string> paths = new() { [GitDistributedSettingsScope.Global] = DetailedRepositorySettings.GlobalPath };
            if (repository is not null)
            {
                GitRepositoryInfo info = await repository.GetInfoAsync(); paths[GitDistributedSettingsScope.Local] = System.IO.Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName);
                if (!info.IsBare) { paths[GitDistributedSettingsScope.Distributed] = System.IO.Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName); }
            }
            DetailedSettingsTestScope result = new(git, repository, paths);
            if (seed) { foreach (GitDistributedSettingsScope scope in paths.Keys) { result.Write(scope, (DetailedRepositorySettings.RemoteBranches, "false"), (DetailedRepositorySettings.AddMergeLog, "false"), (DetailedRepositorySettings.MergeLogCount, "20"), ("future.λ", "keep <literal> & \nbody")); } }
            return result;
        }
        catch { git.Dispose(); throw; }
    }
    internal string Path(GitDistributedSettingsScope scope) => _paths[scope];
    internal void Write(GitDistributedSettingsScope scope, params (string Key, string Value)[] entries)
    {
        string path = Path(scope); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        XElement root = new("dictionary", new XComment("future extension formatting must survive untouched"), entries.Select(entry => new XElement("item", new XElement("key", new XElement("string", entry.Key)), new XElement("value", new XElement("string", entry.Value)))));
        File.WriteAllText(path, new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(), new UTF8Encoding(false));
    }
    internal async Task<DetailedSettingsSession> SessionAsync()
    { DetailedSettingsSession session = new(Repository, Desktop.Load()); await session.InitializeAsync(default); Assert.That(session.LoadError, Is.Null); return session; }
    internal Task<GitXmlSettingsSnapshot> ReadAsync(GitDistributedSettingsScope scope) => GitXmlSettingsStore.LoadAsync(Path(scope));
    internal Dictionary<string, byte[]> Material() => XmlPaths.Append(Desktop.FilePath).Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
    internal void AssertMaterial(IReadOnlyDictionary<string, byte[]> expected)
    { foreach ((string path, byte[] bytes) in expected) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), path); } }
    internal static RevisionGraphOptions Edit(RevisionGraphOptions options, string field, bool value) => field switch
    { "Merge" => options with { MergeGraphLanesHavingCommonParent = value }, "Diagonals" => options with { RenderGraphWithDiagonals = value }, "Straighten" => options with { StraightenGraphDiagonals = value }, _ => throw new ArgumentOutOfRangeException(nameof(field)) };
    internal static DesktopPreferences WithGraph(DesktopPreferences preferences, RevisionGraphOptions options) => preferences with
    { MergeGraphLanesHavingCommonParent = options.MergeGraphLanesHavingCommonParent, RenderGraphWithDiagonals = options.RenderGraphWithDiagonals, StraightenGraphDiagonals = options.StraightenGraphDiagonals };
    public void Dispose() => Git.Dispose();
    internal sealed class BlockedFile : IDisposable
    {
        private readonly string _path, _backup; private bool _restored;
        internal BlockedFile(string path) { _path = path; _backup = path + ".retained-original"; File.Move(path, _backup); Directory.CreateDirectory(path); }
        internal void Restore() { if (_restored) { return; } Directory.Delete(_path); File.Move(_backup, _path); _restored = true; }
        public void Dispose() => Restore();
    }
}
