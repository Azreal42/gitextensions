using System.Text.Json;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class CommitMessagePreferencesStoreTests
{
    [Test]
    public void DefaultsAreTenIndependentEmptySlotsAndTheSixHistoricalDefaultsWithoutAnyWrite()
    {
        using Profile p = new(); var actual = p.Store.Load();
        Assert.That(actual.Templates, Has.Count.EqualTo(10)); Assert.That(actual.Templates.All(t => t == new CommitMessageTemplate()), Is.True);
        Assert.That(actual.MaxFirstLineLength, Is.Zero); Assert.That(actual.MaxLineLength, Is.Zero); Assert.That(actual.AutoWrap, Is.True);
        Assert.That(actual.IndentAfterFirstLine, Is.True); Assert.That(actual.SecondLineMustBeEmpty, Is.False); Assert.That(actual.ValidationRegex, Is.Empty);
        Assert.That(p.Store.SaveChanges(actual, actual).SameAs(actual), Is.True); Assert.That(Directory.Exists(p.Root), Is.False);
    }
    [TestCase(0)] [TestCase(1)] [TestCase(13)]
    public void MigrationExtendsShortListsWithoutTruncatingLongOnes(int count)
    {
        using Profile p = new(); p.Write(JsonSerializer.Serialize(new { Templates = Enumerable.Range(0, count).Select(i => new CommitMessageTemplate("Name " + i, "Text " + i, true)) }));
        var settings = p.Store.Load(); Assert.That(settings.Templates, Has.Count.EqualTo(Math.Max(10, count)));
        for (int i = 0; i < count; i++) { Assert.That(settings.Templates[i], Is.EqualTo(new CommitMessageTemplate("Name " + i, "Text " + i, true))); }
        byte[] bytes = File.ReadAllBytes(p.Store.FilePath); p.Store.SaveChanges(settings, settings); Assert.That(File.ReadAllBytes(p.Store.FilePath), Is.EqualTo(bytes));
    }
    [Test]
    public void AllOptionsAndDuplicateNamesAndEmptyTextRoundTripWithLiteralUnicodeAndNewlines()
    {
        using Profile p = new(); var baseline = p.Store.Load();
        var draft = baseline.WithTemplate(0, new("same name", "line\r\nλ {{literal}}\n", true)).WithTemplate(9, new("same name", ""))
            with { MaxFirstLineLength = 50, MaxLineLength = 72, AutoWrap = false, ValidationRegex = "^λ", IndentAfterFirstLine = false, SecondLineMustBeEmpty = true };
        Assert.That(p.Store.SaveChanges(baseline, draft).SameAs(draft), Is.True); Assert.That(p.Store.Load().SameAs(draft), Is.True);
    }
    [Test]
    public void TwoWindowsMergeDistinctSlotsAndFieldsAndPreserveUnknownRootAndSlotProperties()
    {
        using Profile p = new(); p.Write("{\"Future\":{\"v\":42},\"Templates\":[{\"Name\":\"old\",\"Text\":\"body\",\"FutureIcon\":\"raw\"}]}");
        var a = p.Store.Load(); var b = p.Store.Load();
        p.Store.SaveChanges(a, a.WithTemplate(0, a.Templates[0] with { Name = "new" }) with { MaxLineLength = 72 });
        var merged = p.Store.SaveChanges(b, b.WithTemplate(0, b.Templates[0] with { Text = "updated body" }).WithTemplate(9, new("last", "")));
        Assert.That(merged.Templates[0], Is.EqualTo(new CommitMessageTemplate("new", "updated body"))); Assert.That(merged.MaxLineLength, Is.EqualTo(72));
        using var json = JsonDocument.Parse(File.ReadAllBytes(p.Store.FilePath));
        Assert.That(json.RootElement.GetProperty("Future").GetProperty("v").GetInt32(), Is.EqualTo(42));
        Assert.That(json.RootElement.GetProperty("Templates")[0].GetProperty("FutureIcon").GetString(), Is.EqualTo("raw"));
    }
    [Test]
    public void ConflictingChangesToTheSameSlotFieldRefuseWithoutLosingTheFirstPublication()
    {
        using Profile p = new(); var baseline = p.Store.Load();
        p.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("first", ""))); byte[] before = File.ReadAllBytes(p.Store.FilePath);
        Assert.That(() => p.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("second", ""))), Throws.InvalidOperationException);
        Assert.That(File.ReadAllBytes(p.Store.FilePath), Is.EqualTo(before)); Assert.That(Directory.GetFiles(Path.GetDirectoryName(p.Store.FilePath)!), Has.Length.EqualTo(1));
    }
    [TestCase("{broken")]
    [TestCase("[]")]
    [TestCase("{\"Templates\":null}")]
    [TestCase("{\"Templates\":[null]}")]
    [TestCase("{\"Templates\":[{\"Text\":null}]}")]
    [TestCase("{\"MaxLineLength\":\"72\"}")]
    [TestCase("{\"AutoWrap\":null}")]
    [TestCase("{\"MaxLineLength\":-1}")]
    [TestCase("{\"Templates\":[],\"Templates\":[]}")]
    public void CorruptKnownDataIsNeverOverwrittenNotEvenByAnUnchangedDraft(string json)
    {
        using Profile p = new(); p.Write(json); byte[] bytes = File.ReadAllBytes(p.Store.FilePath);
        Assert.That(() => p.Store.Load(), Throws.InstanceOf<JsonException>());
        Assert.That(() => p.Store.SaveChanges(new(), new()), Throws.InstanceOf<JsonException>());
        Assert.That(File.ReadAllBytes(p.Store.FilePath), Is.EqualTo(bytes));
    }
    [Test, Platform("Linux")]
    public void AtomicPublicationIsPrivateAndAnActualPermissionFaultPreservesBytesAndMode()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux file modes required."); return; }
        using Profile p = new(); var baseline = p.Store.Load(); var saved = p.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("private", "body")));
        Assert.That(File.GetUnixFileMode(p.Store.FilePath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        string directory = Path.GetDirectoryName(p.Store.FilePath)!; var mode = File.GetUnixFileMode(directory); byte[] bytes = File.ReadAllBytes(p.Store.FilePath);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.That(() => p.Store.SaveChanges(saved, saved with { MaxLineLength = 72 }), Throws.InstanceOf<UnauthorizedAccessException>());
            Assert.That(File.ReadAllBytes(p.Store.FilePath), Is.EqualTo(bytes)); Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }
        finally { File.SetUnixFileMode(directory, mode); }
    }
    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "commit-template-profile-" + Guid.NewGuid().ToString("N"));
        internal CommitMessagePreferencesStore Store { get; }
        internal Profile() => Store = new(Root);
        internal void Write(string contents) { Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!); File.WriteAllText(Store.FilePath, contents); }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
