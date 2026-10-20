using System.Text.Json;
using System.Text.Json.Nodes;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppearanceViewIntentTests
{
    [TestCase("RelativeDate")]
    [TestCase("ShowAvatar")]
    [TestCase("Sort")]
    public void EachAppearanceIntentPreservesUnknownJsonAndAllFiveColors(string key)
    {
        using Scope f = new(); f.Seed();
        HistoryViewPreferences active = f.Store.Load(); AppearanceSettingsSession session = new(new(), active);
        AppearanceSettingsValues draft = key switch
        {
            "RelativeDate" => session.Initial with { RelativeDate = false },
            "ShowAvatar" => session.Initial with { ShowAvatar = false },
            _ => session.Initial
        };
        AppearanceSettingsPublication accepted = session.Save(draft, new(), active, key == "Sort" ? GitLogSort.Topology : active.Sort);
        JsonObject after = f.Read();
        JsonObject expected = (JsonObject)f.Initial.DeepClone();
        expected[key] = key == "Sort" ? JsonValue.Create((int)GitLogSort.Topology) : JsonValue.Create(false);
        Assert.That(JsonNode.DeepEquals(after, expected), Is.True, "Only one of the three Appearance keys may change.");
        Assert.That(ColorsSettingsValues.From(f.Store.Load()), Is.EqualTo(ColorsSettingsValues.From(active)));
        Assert.That(accepted.History.RelativeDate, Is.EqualTo(key != "RelativeDate"));
        Assert.That(accepted.History.ShowAvatar, Is.EqualTo(key != "ShowAvatar"));
        Assert.That(accepted.History.Sort, Is.EqualTo(key == "Sort" ? GitLogSort.Topology : active.Sort));
        f.NoTemporary();
    }

    [Test]
    public void AConflictingSortDoesNotOverwritePeerBytesOrAcknowledgeTheDraft()
    {
        using Scope f = new(); f.Seed(); HistoryViewPreferences active = f.Store.Load();
        AppearanceSettingsSession session = new(new(), active); AppearanceSettingsValues initial = session.Initial;
        JsonObject peer = f.Read(); peer["Sort"] = (int)GitLogSort.AuthorDate; f.Write(peer); byte[] before = f.Bytes();
        Assert.Throws<IOException>(() => session.Save(initial, new(), active, GitLogSort.Topology));
        Assert.That(f.Bytes(), Is.EqualTo(before)); Assert.That(session.Initial, Is.EqualTo(initial)); f.NoTemporary();
    }

    [TestCase("RelativeDate")]
    [TestCase("ShowAvatar")]
    public void ATemporaryBooleanViewDoesNotReplaceTheSavedCollisionBaseline(string key)
    {
        using Scope f = new(); f.Seed();
        HistoryViewPreferences temporary = f.Store.Load() with { RelativeDate = key != "RelativeDate", ShowAvatar = key != "ShowAvatar" };
        AppearanceSettingsSession session = new(new(), temporary);
        AppearanceSettingsValues draft = session.Initial with { RelativeDate = true, ShowAvatar = true };
        JsonObject peer = f.Read(); peer[key] = false; f.Write(peer); byte[] before = f.Bytes();
        Assert.Throws<IOException>(() => session.Save(draft, new(), temporary, temporary.Sort));
        Assert.That(f.Bytes(), Is.EqualTo(before)); Assert.That(session.Initial, Is.EqualTo(key == "RelativeDate"
            ? draft with { RelativeDate = false } : draft with { ShowAvatar = false })); f.NoTemporary();
    }

    [Test]
    public void AnUneditedAppearanceSortDoesNotRepairOrRewriteALaterMalformedProfile()
    {
        using Scope f = new(); f.Seed(); HistoryViewPreferences active = f.Store.Load();
        AppearanceSettingsSession session = new(new(), active);
        File.WriteAllText(f.Store.FilePath, "{malformed after opening"); byte[] before = f.Bytes();
        session.Save(session.Initial with { Theme = "Dark" }, new() { Theme = "Dark" }, active, active.Sort);
        Assert.That(f.Bytes(), Is.EqualTo(before)); f.NoTemporary();
    }

    [Test]
    public void CancelledOrForeignWriterIntentCannotPublishAnything()
    {
        using Scope f = new(); f.Seed(); HistoryViewPreferences baseline = f.Store.ReadAppearanceBaseline(); byte[] before = f.Bytes();
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.Store.SaveAppearance(baseline, baseline with { RelativeDate = false }, ["RelativeDate"], cancelled.Token));
        Assert.Throws<ArgumentException>(() => f.Store.SaveAppearance(baseline, baseline with { FillRefLabels = false }, ["FillRefLabels"], CancellationToken.None));
        Assert.That(f.Bytes(), Is.EqualTo(before)); f.NoTemporary();
    }

    [Test]
    public void AnAcknowledgedAppearanceChoiceIsNotReplayedOverALaterPeerSave()
    {
        using Scope f = new(); f.Seed(); HistoryViewPreferences active = f.Store.Load();
        AppearanceSettingsSession session = new(new(), active); AppearanceSettingsValues draft = session.Initial with { RelativeDate = false };
        AppearanceSettingsPublication saved = session.Save(draft, new(), active, active.Sort);
        JsonObject peer = f.Read(); peer["RelativeDate"] = true; peer["future"] = JsonNode.Parse("{\"peer\":23}"); f.Write(peer); byte[] before = f.Bytes();
        session.Save(draft, new(), saved.History, saved.History.Sort);
        Assert.That(f.Bytes(), Is.EqualTo(before)); Assert.That(session.Initial.RelativeDate, Is.False); f.NoTemporary();
    }

    [Test]
    public void AFailedAppearanceWriteRetainsItsIntentAndRetryMergesPeerColors()
    {
        using Scope f = new(); f.Seed(); HistoryViewPreferences active = f.Store.Load(); AppearanceSettingsSession session = new(new(), active);
        AppearanceSettingsValues initial = session.Initial, draft = initial with { RelativeDate = false };
        File.WriteAllText(f.Store.FilePath, "{broken after opening"); byte[] before = f.Bytes();
        Assert.Catch<JsonException>(() => session.Save(draft, new(), active));
        Assert.That(f.Bytes(), Is.EqualTo(before)); Assert.That(session.Initial, Is.EqualTo(initial));
        JsonObject repaired = (JsonObject)f.Initial.DeepClone(); repaired["FillRefLabels"] = false;
        repaired["future"] = JsonNode.Parse("{\"retry\":[null,31,\"Ω\"]}"); f.Write(repaired);
        session.Save(draft, new(), active);
        JsonObject expected = (JsonObject)repaired.DeepClone(); expected["RelativeDate"] = false;
        Assert.That(JsonNode.DeepEquals(f.Read(), expected), Is.True); Assert.That(session.Initial.RelativeDate, Is.False); f.NoTemporary();
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "appearance-view-intent-" + Guid.NewGuid().ToString("N"));
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        internal HistoryViewPreferencesStore Store { get; }
        internal JsonObject Initial { get; private set; } = new();
        internal Scope()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Store = new();
        }
        internal void Seed()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
            Initial = JsonNode.Parse("{\"RelativeDate\":true,\"ShowAvatar\":true,\"Sort\":3,\"DrawAlternateBackground\":false,\"DrawNonRelativesGray\":false,\"DrawNonRelativesTextGray\":true,\"HighlightAuthoredRevisions\":false,\"FillRefLabels\":true,\"ShowNotes\":true,\"future\":{\"literal\":[null,17,\"Ω\"]}}")!.AsObject();
            Write(Initial);
        }
        internal byte[] Bytes() => File.ReadAllBytes(Store.FilePath);
        internal JsonObject Read() => JsonNode.Parse(Bytes())!.AsObject();
        internal void Write(JsonObject document) => File.WriteAllText(Store.FilePath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        internal void NoTemporary() => Assert.That(Directory.GetFiles(Path.GetDirectoryName(Store.FilePath)!, "*.tmp"), Is.Empty);
        public void Dispose()
        { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
