using System.Text.Json.Nodes;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ColorsSettingsSessionTests
{
    [TestCase("DrawAlternateBackground", false)]
    [TestCase("DrawNonRelativesGray", false)]
    [TestCase("DrawNonRelativesTextGray", true)]
    [TestCase("HighlightAuthoredRevisions", false)]
    [TestCase("FillRefLabels", true)]
    public void EachChoiceUsesTheExistingViewProfileAndOnlyItsEditedKey(string key, bool wanted)
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!);
        File.WriteAllText(f.Store.FilePath, "{\"ShowGraph\":false,\"ShowNotes\":true,\"future\":{\"literal\":[null,17,\"Ω\"]}}");
        ColorsSettingsSession session = new(new(), f.Store);
        ColorsSettingsValues draft = key switch
        {
            "DrawAlternateBackground" => session.Initial with { DrawAlternateBackground = false },
            "DrawNonRelativesGray" => session.Initial with { DrawNonRelativesGray = false },
            "DrawNonRelativesTextGray" => session.Initial with { DrawNonRelativesTextGray = true },
            "HighlightAuthoredRevisions" => session.Initial with { HighlightAuthoredRevisions = false },
            "FillRefLabels" => session.Initial with { FillRefLabels = true },
            _ => throw new ArgumentException(key)
        };
        ColorsSettingsPublication publication = session.Save(draft);
        Assert.That(publication.Written, Is.True);
        JsonObject raw = JsonNode.Parse(File.ReadAllBytes(f.Store.FilePath))!.AsObject();
        Assert.That(raw[key]!.GetValue<bool>(), Is.EqualTo(wanted));
        Assert.That(raw.Select(pair => pair.Key), Is.EquivalentTo(new[] { "ShowGraph", "ShowNotes", "future", key }));
        Assert.That(JsonNode.DeepEquals(raw["future"], JsonNode.Parse("{\"literal\":[null,17,\"Ω\"]}")), Is.True);
        Assert.That(f.Store.Load().ShowGraph, Is.False); Assert.That(f.Store.Load().ShowNotes, Is.True);
        Assert.That(ColorsSettingsValues.From(f.Store.Load()).Get(key), Is.EqualTo(wanted));
        Assert.That(new ColorsSettingsSession(f.Store.Load(), f.Store).Initial.Get(key), Is.EqualTo(wanted));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(f.Store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void NoOpDoesNotCreateOrRepairAnUntouchedProfile()
    {
        using Scope f = new(); ColorsSettingsSession session = new(new(), f.Store);
        Assert.That(session.Save(session.Initial).Written, Is.False); Assert.That(File.Exists(f.Store.FilePath), Is.False);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, "{broken raw");
        byte[] raw = File.ReadAllBytes(f.Store.FilePath);
        Assert.That(session.Save(session.Initial).Written, Is.False); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(raw));
    }

    [Test]
    public void AnUntouchedTemporaryViewChoiceIsNotTurnedIntoASavedDefault()
    {
        using Scope f = new(); f.Store.Save(new() { ShowGraph = false });
        HistoryViewPreferences active = new() { DrawNonRelativesGray = false, ShowAuthor = false };
        ColorsSettingsSession session = new(active, f.Store);
        ColorsSettingsPublication saved = session.Save(session.Initial with { FillRefLabels = true }, active);
        Assert.That(f.Store.Load().DrawNonRelativesGray, Is.True, "Only Fill is an explicit Colors intention.");
        Assert.That(f.Store.Load().ShowGraph, Is.False); Assert.That(f.Store.Load().FillRefLabels, Is.True);
        Assert.That(saved.History.DrawNonRelativesGray, Is.False); Assert.That(saved.History.ShowAuthor, Is.False);
    }

    [Test]
    public void PeerColorsAndOtherViewFieldsAreMergedWithoutRepublishingTheOldSnapshot()
    {
        using Scope f = new(); f.Store.Save(new()); ColorsSettingsSession session = new(new(), f.Store);
        f.Store.Save(f.Store.Load() with { DrawNonRelativesGray = false, RelativeDate = false, ShowNotes = true, HiddenToolbarItems = ["peer"] });
        HistoryViewPreferences live = new() { ShowId = false };
        ColorsSettingsPublication saved = session.Save(session.Initial with { DrawAlternateBackground = false }, live);
        HistoryViewPreferences file = f.Store.Load();
        Assert.That(file.DrawNonRelativesGray, Is.False); Assert.That(file.RelativeDate, Is.False); Assert.That(file.ShowNotes, Is.True);
        Assert.That(file.HiddenToolbarItems, Is.EqualTo(new[] { "peer" })); Assert.That(file.DrawAlternateBackground, Is.False);
        Assert.That(saved.History.ShowId, Is.False); Assert.That(saved.History.DrawNonRelativesGray, Is.True,
            "Another window's saved default does not replace an unedited active View choice in this window.");
    }

    [Test]
    public void AnAlreadyPublishedDecisionIsAcknowledgedWithoutRewriteAndRetryDoesNotReplayIt()
    {
        using Scope f = new(); f.Store.Save(new()); ColorsSettingsSession session = new(new(), f.Store);
        File.WriteAllText(f.Store.FilePath, "{ \"FillRefLabels\": true, \"peer\": 17 }\n"); byte[] peer = File.ReadAllBytes(f.Store.FilePath);
        ColorsSettingsValues draft = session.Initial with { FillRefLabels = true };
        Assert.That(session.Save(draft).Written, Is.False); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(peer));
        File.WriteAllText(f.Store.FilePath, "{ \"FillRefLabels\": false, \"peer\": 18 }\n"); peer = File.ReadAllBytes(f.Store.FilePath);
        Assert.That(session.Save(draft).Written, Is.False); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(peer));
    }

    [TestCase(true)] [TestCase(false)]
    public void AppearanceAndColorsSaveIntoOneAuthorityInEitherOrder(bool colorsFirst)
    {
        using Scope f = new(useXdg: true); f.Store.Save(new());
        ColorsSettingsSession colors = new(new(), f.Store); AppearanceSettingsSession appearance = new(new(), new());
        void SaveColors() => colors.Save(colors.Initial with { DrawNonRelativesTextGray = true, FillRefLabels = true });
        void SaveAppearance() => appearance.Save(appearance.Initial with { RelativeDate = false, ShowAvatar = false }, new(), new());
        if (colorsFirst) { SaveColors(); SaveAppearance(); } else { SaveAppearance(); SaveColors(); }
        HistoryViewPreferences file = f.Store.Load();
        Assert.That(file.RelativeDate, Is.False); Assert.That(file.ShowAvatar, Is.False);
        Assert.That(file.DrawNonRelativesTextGray, Is.True); Assert.That(file.FillRefLabels, Is.True);
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(f.Store.FilePath)!).Select(Path.GetFileName), Is.EqualTo(new[] { "history-view-preferences.json" }));
    }

    [Test]
    public void AFailedWriteKeepsTheDraftAndCancellationCreatesNoFile()
    {
        using Scope f = new(); ColorsSettingsSession session = new(new(), f.Store);
        ColorsSettingsValues draft = session.Initial with { FillRefLabels = true };
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Save(draft, cancellationToken: cancellation.Token));
        Assert.That(File.Exists(f.Store.FilePath), Is.False); Assert.That(session.Initial.FillRefLabels, Is.False);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, "{not-json");
        byte[] raw = File.ReadAllBytes(f.Store.FilePath);
        Assert.Throws<System.Text.Json.JsonException>(() => session.Save(draft));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(raw)); Assert.That(draft.FillRefLabels, Is.True);
        Assert.That(session.Initial.FillRefLabels, Is.False);
    }

    [Test]
    public void ATemporaryGraphChoiceDoesNotHideAConflictingSavedDecisionAfterThePageOpened()
    {
        using Scope f = new(); f.Store.Save(new() { DrawNonRelativesGray = true });
        ColorsSettingsSession page = new(new() { DrawNonRelativesGray = false }, f.Store);
        f.Store.Save(f.Store.Load() with { DrawNonRelativesGray = false }); byte[] peer = File.ReadAllBytes(f.Store.FilePath);
        // The user changes temporary false to true, but a peer changed the saved true baseline to false meanwhile.
        Assert.Throws<IOException>(() => page.Save(page.Initial with { DrawNonRelativesGray = true }));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(peer)); Assert.That(page.Initial.DrawNonRelativesGray, Is.False);
    }

    [Test]
    public void AStaleSaveViewRetainsAllFiveNewerColorsDefaultsAndPreservesUnknownJson()
    {
        using Scope f = new(); f.Store.Save(new()); HistoryViewPreferences oldWindow = f.Store.Load() with { ShowNotes = true };
        ColorsSettingsSession colors = new(new(), f.Store);
        colors.Save(colors.Initial with { DrawAlternateBackground = false, DrawNonRelativesGray = false,
            DrawNonRelativesTextGray = true, HighlightAuthoredRevisions = false, FillRefLabels = true });
        JsonObject raw = JsonNode.Parse(File.ReadAllBytes(f.Store.FilePath))!.AsObject(); raw["future"] = "keep me";
        File.WriteAllText(f.Store.FilePath, raw.ToJsonString());
        HistoryViewPreferences accepted = f.Store.SaveCurrentViewDefaults(oldWindow, null, CancellationToken.None);
        Assert.That(ColorsSettingsValues.From(accepted), Is.EqualTo(new ColorsSettingsValues
        { DrawAlternateBackground = false, DrawNonRelativesGray = false, DrawNonRelativesTextGray = true, HighlightAuthoredRevisions = false, FillRefLabels = true }));
        Assert.That(accepted.ShowNotes, Is.True); Assert.That(JsonNode.Parse(File.ReadAllBytes(f.Store.FilePath))!["future"]!.GetValue<string>(), Is.EqualTo("keep me"));
    }

    [Test]
    public void SaveViewRejectsAConflictingSharedGraphIntentAndAnUnrelatedChoiceDoesNotConsumeIt()
    {
        using Scope f = new(); f.Store.Save(new() { DrawNonRelativesGray = false });
        HistoryGraphGrayIntent intent = new(false, false);
        f.Store.Save(f.Store.Load() with { DrawNonRelativesGray = true }); byte[] peer = File.ReadAllBytes(f.Store.FilePath);
        Assert.Throws<IOException>(() => f.Store.SaveCurrentViewDefaults(new() { DrawNonRelativesGray = false }, intent, CancellationToken.None));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(peer));
        Assert.Throws<IOException>(() => f.Store.SaveCurrentViewDefaults(new(), new(null, false), CancellationToken.None));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(peer));
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "colors-session-" + Guid.NewGuid().ToString("N"));
        private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly bool _xdg;
        internal HistoryViewPreferencesStore Store { get; }
        internal Scope(bool useXdg = false)
        {
            _xdg = useXdg; if (_xdg) { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); }
            Store = new(Path.Combine(_root, "gitextensions", "history-view-preferences.json"));
        }
        public void Dispose()
        { if (_xdg) { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old); } if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
