using System.Globalization;
using System.Text.Json;
using GitCommands;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Avatars;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AvatarSettingsPublicationTests
{
    [TestCase("Provider")]
    [TestCase("Fallback")]
    [TestCase("Template")]
    [TestCase("CacheDays")]
    [TestCase("ShowCommitInfo")]
    public async Task EachAvatarIntentionMergesTheOtherFourPeerValuesAndAnUneditedSecondApplyKeepsNewPeers(string field)
    {
        using AvatarSettingsTestProfile p = new(); using OfflineAvatarFixture f = new();
        DesktopPreferences initial = p.Desktop.Load(); HistoryViewPreferences active = p.History.Load();
        AppearanceSettingsSession appearance = new(initial, active); GeneralSettingsSession general = new(initial);
        AppearanceSettingsValues draft = Edit(appearance.Initial, field);
        DesktopPreferences peer = Peer(initial); if (field == "ShowCommitInfo") { peer = peer with { ShowAuthorAvatarInCommitInfo = true }; }
        p.Desktop.Save(peer); byte[] historyBefore = File.ReadAllBytes(p.History.FilePath), desktopBefore = File.ReadAllBytes(p.Desktop.FilePath);
        await using AvatarService service = f.Create(AvatarPreferences.From(initial)); long initialEpoch = service.Snapshot.Epoch;
        DesktopPreferences merged = appearance.MergeDesktop(draft, initial);
        Assert.That(File.ReadAllBytes(p.Desktop.FilePath), Is.EqualTo(desktopBefore)); Assert.That(service.Snapshot.Epoch, Is.EqualTo(initialEpoch));
        DesktopPreferences expected = Edit(peer, field); List<Task> mutations = []; List<DesktopPreferences> callbacks = [];
        GeneralSettingsPublication desktop = general.Save(general.Initial, merged, accepted =>
        {
            callbacks.Add(accepted); Assert.That(JsonSerializer.Serialize(p.Desktop.Load()), Is.EqualTo(JsonSerializer.Serialize(accepted)), "The callback follows the actual successful desktop write.");
            appearance.DesktopSaved(draft); mutations.Add(service.ApplySavedPolicy(AvatarPreferences.From(accepted)).Completion);
        });
        AppearanceSettingsPublication publication = appearance.Save(draft, desktop.Desktop, active);
        await Task.WhenAll(mutations).WaitAsync(OfflineAvatars.Limit);
        AssertAvatarFields(p.Desktop.Load(), expected); AssertAvatarPolicy(service.Snapshot.Preferences, expected);
        Assert.That(callbacks, Has.Count.EqualTo(1)); Assert.That(callbacks[0], Is.SameAs(desktop.Desktop));
        Assert.That(publication.Values.Theme, Is.EqualTo(peer.Theme)); Assert.That(publication.Values.ShowRepoCurrentBranch, Is.EqualTo(peer.ShowRepoCurrentBranch));
        Assert.That(File.ReadAllBytes(p.History.FilePath), Is.EqualTo(historyBefore));
        AppearanceSettingsValues acceptedDraft = publication.Values;
        DesktopPreferences newer = initial with { Theme = "System", AvatarProvider = AvatarProvider.None,
            AvatarFallbackType = AvatarFallbackType.Robohash, CustomAvatarTemplate = "https://new-peer.invalid/{email}",
            AvatarImageCacheDays = 67, ShowAuthorAvatarInCommitInfo = true };
        p.Desktop.Save(newer); mutations.Clear();
        GeneralSettingsPublication second = general.Save(general.Initial, appearance.MergeDesktop(acceptedDraft, desktop.Desktop), saved =>
        { appearance.DesktopSaved(acceptedDraft); mutations.Add(service.ApplySavedPolicy(AvatarPreferences.From(saved)).Completion); });
        appearance.Save(acceptedDraft, second.Desktop, active); await Task.WhenAll(mutations).WaitAsync(OfflineAvatars.Limit);
        AssertAvatarFields(p.Desktop.Load(), newer); AssertAvatarPolicy(service.Snapshot.Preferences, newer);
        Assert.That(service.Snapshot.CapturedDiskCacheDays, Is.EqualTo(13));
    }

    [Test]
    public async Task AFailedDesktopWriteCannotAcknowledgeTheDraftMutatePolicyOrDeleteCache()
    {
        using AvatarSettingsTestProfile p = new(); using OfflineAvatarFixture f = new(); DesktopPreferences initial = p.Desktop.Load();
        AppearanceSettingsSession appearance = new(initial, p.History.Load()); GeneralSettingsSession general = new(initial);
        AppearanceSettingsValues draft = Edit(appearance.Initial, "Provider"); await using AvatarService service = f.Create(AvatarPreferences.From(initial));
        AvatarPolicySnapshot before = service.Snapshot; int callbacks = 0;
        File.Delete(p.Desktop.FilePath); Directory.CreateDirectory(p.Desktop.FilePath);
        try
        {
            Assert.Catch<IOException>(() => general.Save(general.Initial, appearance.MergeDesktop(draft, initial), saved =>
            { callbacks++; appearance.DesktopSaved(draft); service.ApplySavedPolicy(AvatarPreferences.From(saved)); }));
            Assert.That(callbacks, Is.Zero); Assert.That(service.Snapshot, Is.EqualTo(before)); Assert.That(f.Disk.Clears, Is.Zero);
            Assert.That(appearance.Initial.AvatarProvider, Is.EqualTo(AvatarProvider.None));
        }
        finally { Directory.Delete(p.Desktop.FilePath); p.Desktop.Save(initial); }
        DesktopPreferences peer = Peer(initial); p.Desktop.Save(peer);
        DesktopPreferences retry = appearance.MergeDesktop(draft, initial);
        Assert.That(retry.AvatarProvider, Is.EqualTo(AvatarProvider.Custom), "The failed write did not acknowledge this still-pending intention.");
        Assert.That(retry.AvatarFallbackType, Is.EqualTo(peer.AvatarFallbackType)); Assert.That(callbacks, Is.Zero);
    }

    [TestCase("Clone")]
    [TestCase("History")]
    public async Task ARealPartialDesktopPublicationAppliesTheSavedPolicyThenRetryPreservesNewAvatarPeers(string failedProfile)
    {
        using AvatarSettingsTestProfile p = new(); using OfflineAvatarFixture f = new(); DesktopPreferences initial = p.Desktop.Load();
        HistoryViewPreferences active = p.History.Load(); AppearanceSettingsSession appearance = new(initial, active); GeneralSettingsSession general = new(initial);
        AppearanceSettingsValues original = appearance.Initial, draft = original with { AvatarProvider = AvatarProvider.Custom,
            AvatarFallbackType = AvatarFallbackType.Retro, CustomAvatarTemplate = "https://draft.invalid/{email}", AvatarImageCacheDays = 31,
            ShowAuthorAvatarInCommitInfo = false, RelativeDate = false };
        GeneralSettingsValues generalDraft = failedProfile == "Clone" ? general.Initial with { DefaultCloneDestinationPath = "/tmp/avatar-draft-clone" } : general.Initial;
        string path = failedProfile == "Clone" ? p.Clone.FilePath : p.History.FilePath; byte[] savedProfile = File.ReadAllBytes(path);
        File.WriteAllText(path, "{broken-avatar-partial-profile");
        await using AvatarService service = f.Create(AvatarPreferences.From(initial)); List<Task> mutations = []; List<DesktopPreferences> callbacks = [];
        void DesktopSaved(DesktopPreferences saved)
        {
            callbacks.Add(saved); Assert.That(JsonSerializer.Serialize(p.Desktop.Load()), Is.EqualTo(JsonSerializer.Serialize(saved))); appearance.DesktopSaved(draft);
            mutations.Add(service.ApplySavedPolicy(AvatarPreferences.From(saved)).Completion);
        }
        try
        {
            Assert.Catch<System.Text.Json.JsonException>(() =>
            {
                GeneralSettingsPublication desktop = general.Save(generalDraft, appearance.MergeDesktop(draft, initial), DesktopSaved);
                appearance.Save(draft, desktop.Desktop, active);
            });
            await Task.WhenAll(mutations).WaitAsync(OfflineAvatars.Limit);
            Assert.That(callbacks, Has.Count.EqualTo(1)); AssertAvatarPolicy(service.Snapshot.Preferences, p.Desktop.Load());
            Assert.That(service.Snapshot.Preferences.Provider, Is.EqualTo(AvatarProvider.Custom));
            Assert.That(appearance.Initial, Is.EqualTo(original), "Failure does not accept the still-visible page draft.");
            Assert.That(File.ReadAllText(path), Is.EqualTo("{broken-avatar-partial-profile"));
            // Another Settings window writes after this window's completed desktop publication.
            DesktopPreferences peer = Peer(initial) with { AvatarImageCacheDays = 73 }; p.Desktop.Save(peer);
            File.WriteAllBytes(path, savedProfile); mutations.Clear();
            GeneralSettingsPublication retry = general.Save(generalDraft, appearance.MergeDesktop(draft, initial), DesktopSaved);
            AppearanceSettingsPublication publication = appearance.Save(draft, retry.Desktop, active);
            await Task.WhenAll(mutations).WaitAsync(OfflineAvatars.Limit);
            AssertAvatarFields(retry.Desktop, peer); AssertAvatarFields(p.Desktop.Load(), peer); AssertAvatarPolicy(service.Snapshot.Preferences, peer);
            Assert.That(publication.Values.AvatarImageCacheDays, Is.EqualTo(73)); Assert.That(publication.History.RelativeDate, Is.False);
            Assert.That(callbacks, Has.Count.EqualTo(2)); Assert.That(service.Snapshot.CapturedDiskCacheDays, Is.EqualTo(13));
        }
        finally { File.WriteAllBytes(path, savedProfile); }
    }

    [Test]
    public async Task SavedPresentationAndCacheDaysKeepTheEpochWhileAProviderChangeRetiresItAndUsesTheSavedPeerTemplate()
    {
        using AvatarSettingsTestProfile p = new(); using OfflineAvatarFixture f = new(); DesktopPreferences initial = p.Desktop.Load();
        AppearanceSettingsSession appearance = new(initial, p.History.Load()); GeneralSettingsSession general = new(initial);
        await using AvatarService service = f.Create(AvatarPreferences.From(initial)); long epoch = service.Snapshot.Epoch;
        AppearanceSettingsValues presentation = appearance.Initial with { ShowAuthorAvatarInCommitInfo = false, AvatarImageCacheDays = 91 };
        AvatarMutation? mutation = null;
        GeneralSettingsPublication first = general.Save(general.Initial, appearance.MergeDesktop(presentation, initial), saved =>
        { appearance.DesktopSaved(presentation); mutation = service.ApplySavedPolicy(AvatarPreferences.From(saved)); });
        appearance.Save(presentation, first.Desktop, p.History.Load()); await mutation!.Completion.WaitAsync(OfflineAvatars.Limit);
        Assert.That(service.Snapshot.Epoch, Is.EqualTo(epoch)); Assert.That(service.Snapshot.CapturedDiskCacheDays, Is.EqualTo(13));
        Assert.That(service.Snapshot.Preferences.CacheDays, Is.EqualTo(91)); Assert.That(service.Snapshot.Preferences.ShowCommitInfo, Is.False); Assert.That(f.Disk.Clears, Is.Zero);
        DesktopPreferences peer = p.Desktop.Load() with { CustomAvatarTemplate = "https://actual-peer.invalid/{email}" }; p.Desktop.Save(peer);
        AppearanceSettingsValues provider = appearance.Initial with { AvatarProvider = AvatarProvider.Custom };
        GeneralSettingsPublication second = general.Save(general.Initial, appearance.MergeDesktop(provider, first.Desktop), saved =>
        { appearance.DesktopSaved(provider); mutation = service.ApplySavedPolicy(AvatarPreferences.From(saved)); });
        appearance.Save(provider, second.Desktop, p.History.Load()); await mutation!.Completion.WaitAsync(OfflineAvatars.Limit);
        Assert.That(service.Snapshot.Epoch, Is.EqualTo(epoch + 1)); Assert.That(f.Disk.Clears, Is.EqualTo(1));
        Assert.That(service.Snapshot.Preferences.CustomTemplate, Is.EqualTo(peer.CustomAvatarTemplate));
    }

    [Test]
    public async Task InvalidAuthorImageDraftsCannotWriteOrRegisterRuntimePublication()
    {
        using AvatarSettingsTestProfile p = new(); using OfflineAvatarFixture f = new(); DesktopPreferences initial = p.Desktop.Load();
        AppearanceSettingsSession appearance = new(initial, p.History.Load()); GeneralSettingsSession general = new(initial);
        await using AvatarService service = f.Create(AvatarPreferences.From(initial)); AvatarPolicySnapshot policy = service.Snapshot;
        byte[] before = File.ReadAllBytes(p.Desktop.FilePath); int callbacks = 0;
        foreach (AppearanceSettingsValues invalid in new[] { appearance.Initial with { AvatarImageCacheDays = 401 },
            appearance.Initial with { AvatarProvider = (AvatarProvider)999 }, appearance.Initial with { AvatarFallbackType = (AvatarFallbackType)999 } })
        {
            Assert.Throws<ArgumentException>(() => general.Save(general.Initial, appearance.MergeDesktop(invalid, initial), saved =>
            { callbacks++; appearance.DesktopSaved(invalid); service.ApplySavedPolicy(AvatarPreferences.From(saved)); }));
            Assert.That(File.ReadAllBytes(p.Desktop.FilePath), Is.EqualTo(before)); Assert.That(service.Snapshot, Is.EqualTo(policy));
        }
        Assert.That(callbacks, Is.Zero); Assert.That(f.Disk.Clears, Is.Zero);
    }

    private static AppearanceSettingsValues Edit(AppearanceSettingsValues value, string field) => field switch
    {
        "Provider" => value with { AvatarProvider = AvatarProvider.Custom }, "Fallback" => value with { AvatarFallbackType = AvatarFallbackType.Retro },
        "Template" => value with { CustomAvatarTemplate = "https://explicit-draft.invalid/{email}" }, "CacheDays" => value with { AvatarImageCacheDays = 31 },
        "ShowCommitInfo" => value with { ShowAuthorAvatarInCommitInfo = false }, _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static DesktopPreferences Edit(DesktopPreferences value, string field) => field switch
    {
        "Provider" => value with { AvatarProvider = AvatarProvider.Custom }, "Fallback" => value with { AvatarFallbackType = AvatarFallbackType.Retro },
        "Template" => value with { CustomAvatarTemplate = "https://explicit-draft.invalid/{email}" }, "CacheDays" => value with { AvatarImageCacheDays = 31 },
        "ShowCommitInfo" => value with { ShowAuthorAvatarInCommitInfo = false }, _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static DesktopPreferences Peer(DesktopPreferences initial) => initial with
    { Theme = "Dark", ShowRepoCurrentBranch = false, AvatarProvider = AvatarProvider.Default, AvatarFallbackType = AvatarFallbackType.Wavatar,
        CustomAvatarTemplate = "https://peer.invalid/{name}", AvatarImageCacheDays = 47, ShowAuthorAvatarInCommitInfo = false };
    private static void AssertAvatarFields(DesktopPreferences actual, DesktopPreferences expected)
    {
        Assert.That(actual.AvatarProvider, Is.EqualTo(expected.AvatarProvider)); Assert.That(actual.AvatarFallbackType, Is.EqualTo(expected.AvatarFallbackType));
        Assert.That(actual.CustomAvatarTemplate, Is.EqualTo(expected.CustomAvatarTemplate)); Assert.That(actual.AvatarImageCacheDays, Is.EqualTo(expected.AvatarImageCacheDays));
        Assert.That(actual.ShowAuthorAvatarInCommitInfo, Is.EqualTo(expected.ShowAuthorAvatarInCommitInfo));
    }
    private static void AssertAvatarPolicy(AvatarPreferences actual, DesktopPreferences expected)
    {
        Assert.That(actual.Provider, Is.EqualTo(expected.AvatarProvider)); Assert.That(actual.Fallback, Is.EqualTo(expected.AvatarFallbackType));
        Assert.That(actual.CustomTemplate, Is.EqualTo(expected.CustomAvatarTemplate)); Assert.That(actual.CacheDays, Is.EqualTo(expected.AvatarImageCacheDays));
        Assert.That(actual.ShowCommitInfo, Is.EqualTo(expected.ShowAuthorAvatarInCommitInfo));
    }
}

internal sealed class AvatarSettingsTestProfile : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-avatar-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), _cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    internal PreferencesStore Desktop { get; } internal HistoryViewPreferencesStore History { get; } internal ClonePreferencesStore Clone { get; }
    internal AvatarSettingsTestProfile()
    {
        Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_root, "cache"));
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); Desktop = new(); History = new(); Clone = new();
        Desktop.Save(new() { Language = "English" }); History.Save(new()); Clone.SaveStrict(new("/tmp/avatar-prior-clone", false));
    }
    internal string Output(string name) => Path.Combine(_root, name);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config); Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _cache);
        CultureInfo.CurrentUICulture = _culture; try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
