using System.Text.Json.Nodes;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class BlameViewerSettingsSessionTests
{
    [TestCase(nameof(BlamePreferences.IgnoreWhitespace))]
    [TestCase(nameof(BlamePreferences.DetectMoves))]
    [TestCase(nameof(BlamePreferences.DetectCopies))]
    [TestCase(nameof(BlamePreferences.DisplayAuthorFirst))]
    [TestCase(nameof(BlamePreferences.ShowAuthor))]
    [TestCase(nameof(BlamePreferences.ShowAuthorDate))]
    [TestCase(nameof(BlamePreferences.ShowAuthorTime))]
    [TestCase(nameof(BlamePreferences.ShowLineNumbers))]
    [TestCase(nameof(BlamePreferences.ShowOriginalFilePath))]
    [TestCase(nameof(BlamePreferences.ShowAuthorAvatar))]
    public void EachOriginalPageIntentionRestartsFromTheSameProfileWithoutLosingHistogramOrUnknownFields(string name)
    {
        using Scope f = new(); f.Seed(); BlameViewerSettingsSession session = new(f.Store);
        bool wanted = !BlameViewerSettingsCatalog.Get(session.Initial, name);
        BlameSettingsSaveReceipt saved = session.Save(BlameViewerSettingsCatalog.With(session.Initial, name, wanted));
        BlamePreferences restarted = new BlameViewerSettingsSession(new(f.Root)).Initial;
        Assert.That(saved.Written, Is.True); Assert.That(saved.Fields, Is.EqualTo(new[] { name }));
        Assert.That(BlameViewerSettingsCatalog.Get(restarted, name), Is.EqualTo(wanted));
        Assert.That(restarted.UseHistogramDiffAlgorithm, Is.True); Assert.That(f.Document()["future"]!["token"]!.GetValue<string>(), Is.EqualTo("preserve Ω"));
        foreach (BlameViewerSetting field in BlameViewerSettingsCatalog.Fields.Where(field => field.Name != name))
        { Assert.That(BlameViewerSettingsCatalog.Get(restarted, field.Name), Is.EqualTo(BlameViewerSettingsCatalog.Get(new(), field.Name)), field.Name); }
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(f.Store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void OpeningCancellingOrApplyingAnUntouchedPageNeverCreatesDefaults()
    {
        using Scope f = new(); BlameViewerSettingsSession session = new(f.Store);
        Assert.That(session.LoadError, Is.Null); Assert.That(File.Exists(f.Store.FilePath), Is.False);
        Assert.That(session.Save(session.Initial).Written, Is.False); Assert.That(Directory.Exists(Path.GetDirectoryName(f.Store.FilePath)), Is.False);
    }

    [Test]
    public void IndependentAuthorAndDateFalseSurvivePageSaveLoadAndUnrelatedHistogramPublication()
    {
        using Scope f = new(); f.Seed(); BlameViewerSettingsSession session = new(f.Store);
        session.Save(session.Initial with { ShowAuthor = false, ShowAuthorDate = false });
        Assert.That(f.Store.Load().ShowAuthor, Is.False); Assert.That(f.Store.Load().ShowAuthorDate, Is.False);
        BlamePreferences current = f.Store.ReadSettings(); f.Store.SaveChanges(current, current with { UseHistogramDiffAlgorithm = false }, [nameof(BlamePreferences.UseHistogramDiffAlgorithm)]);
        Assert.That(f.Store.Load().ShowAuthorDate, Is.False, "General's eleventh preference must not infer another page checkbox.");
        Assert.That(f.Store.Load().UseHistogramDiffAlgorithm, Is.False); Assert.That(f.Document()["future"]!["token"]!.GetValue<string>(), Is.EqualTo("preserve Ω"));
    }

    [Test]
    public void TwoSettingsDraftsAndALaterViewerIntentionMergeOnlyTheirChangedFieldsWithoutReplay()
    {
        using Scope f = new(); f.Seed(); BlameViewerSettingsSession first = new(f.Store), second = new(f.Store);
        BlamePreferences oldViewer = f.Store.ReadSettings();
        first.Save(first.Initial with { ShowLineNumbers = true });
        second.Save(second.Initial with { ShowAuthor = false, ShowAuthorDate = false });
        f.Store.SaveChanges(oldViewer, oldViewer with { DisplayAuthorFirst = true }, BlameViewerSettingsCatalog.AllPreferenceNames);
        BlamePreferences current = f.Store.Load(); Assert.That(current.ShowLineNumbers, Is.True); Assert.That(current.ShowAuthor, Is.False);
        Assert.That(current.ShowAuthorDate, Is.False); Assert.That(current.DisplayAuthorFirst, Is.True); Assert.That(current.UseHistogramDiffAlgorithm, Is.True);
        f.Store.SaveChanges(current, current with { ShowLineNumbers = false }, [nameof(BlamePreferences.ShowLineNumbers)]);
        byte[] peer = File.ReadAllBytes(f.Store.FilePath); Assert.That(first.Save(first.Initial).Written, Is.False);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(peer), "An accepted Apply is no longer an intention on retry.");
        Assert.That(f.Document()["future"]!["token"]!.GetValue<string>(), Is.EqualTo("preserve Ω"));
    }

    [TestCase("{ invalid")]
    [TestCase("null")]
    [TestCase("{\"ShowAuthor\":null}")]
    [TestCase("{\"ShowAuthor\":\"false\"}")]
    [TestCase("{\"ShowAuthor\":true,\"ShowAuthor\":false}")]
    public void UnreadableProfilesAreVisibleAndUntouchedOrEditedSessionsCannotRepairThemImplicitly(string json)
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, json);
        byte[] before = File.ReadAllBytes(f.Store.FilePath); BlameViewerSettingsSession session = new(f.Store);
        Assert.That(session.LoadError, Is.Not.Null); Assert.That(session.Save(session.Initial).Written, Is.False);
        Assert.Throws<IOException>(() => session.Save(session.Initial with { ShowLineNumbers = true }));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(before)); Assert.That(session.Initial.ShowLineNumbers, Is.False);
    }

    [Test]
    public void AFailedPublicationKeepsItsDraftBaselineAndCanBeRetriedAfterOnlyTheFixtureBarrierIsRemoved()
    {
        using Scope f = new(); BlameViewerSettingsSession session = new(f.Store); BlamePreferences wanted = session.Initial with { DetectCopies = true };
        Directory.CreateDirectory(f.Store.FilePath);
        Exception error = Assert.Catch<Exception>(() => session.Save(wanted))!;
        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>()); Assert.That(session.Initial.DetectCopies, Is.False); Assert.That(Directory.Exists(f.Store.FilePath), Is.True);
        Directory.Delete(f.Store.FilePath); session.Save(wanted); Assert.That(new BlameViewerSettingsSession(f.Store).Initial.DetectCopies, Is.True);
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(f.Store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void CancellationBeforeSaveHasNoProfileOrAcknowledgedEffect()
    {
        using Scope f = new(); f.Seed(); byte[] before = File.ReadAllBytes(f.Store.FilePath); BlameViewerSettingsSession session = new(f.Store);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Save(session.Initial with { DetectCopies = true }, canceled.Token));
        Assert.That(session.Initial.DetectCopies, Is.False); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(before));
    }

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "blame-viewer-session-" + Guid.NewGuid().ToString("N"));
        internal BlamePreferencesStore Store { get; }
        internal Scope() { Directory.CreateDirectory(Root); Store = new(Root); }
        internal void Seed()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
            File.WriteAllText(Store.FilePath, "{\"UseHistogramDiffAlgorithm\":true,\"future\":{\"token\":\"preserve Ω\"}}");
        }
        internal JsonObject Document() => JsonNode.Parse(File.ReadAllBytes(Store.FilePath))!.AsObject();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
