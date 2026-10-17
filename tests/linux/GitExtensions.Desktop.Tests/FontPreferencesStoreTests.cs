using System.Text.Json;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FontPreferencesStoreTests
{
    [Test]
    public void DefaultsHaveFourIndependentPointSizesAndDoNotCreateAProfile()
    {
        using Profile profile = new(); FontPreferences fonts = profile.Store.Load();
        Assert.Multiple(() =>
        {
            Assert.That(fonts.Code, Is.EqualTo(new FontSpec("Consolas", 10)));
            Assert.That(fonts.Application, Is.EqualTo(new FontSpec("Segoe UI", 9)));
            Assert.That(fonts.Commit, Is.EqualTo(new FontSpec("Segoe UI", 9)));
            Assert.That(fonts.Monospace, Is.EqualTo(new FontSpec("Consolas", 9)));
            Assert.That(fonts.Code.SizeInDips, Is.EqualTo(40d / 3).Within(1e-12));
            Assert.That(fonts.Application.SizeInDips, Is.EqualTo(12));
            Assert.That(fonts.ShowEolMarkerAsGlyph, Is.False);
        });
        Assert.That(profile.Store.SaveChanges(fonts, fonts), Is.EqualTo(fonts));
        Assert.That(Directory.Exists(profile.Root), Is.False);
    }

    [Test]
    public void EveryRoleAndPrecisePointsStyleAndEolRoundTripIndependently()
    {
        using Profile profile = new(); FontPreferences baseline = profile.Store.Load();
        FontPreferences draft = new() { Code = new("literal missing code", 10.1234567890123, true), Application = new("application", 13, false, true),
            Commit = new("commit", 15, true, true), Monospace = new("mono", 17.125), ShowEolMarkerAsGlyph = true };
        Assert.That(profile.Store.SaveChanges(baseline, draft), Is.EqualTo(draft)); Assert.That(profile.Store.Load(), Is.EqualTo(draft));
        byte[] before = File.ReadAllBytes(profile.Store.FilePath); profile.Store.SaveChanges(draft, draft);
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before));
    }

    [Test]
    public void IndependentSettingsBaselinesMergeOnlyChangedRolesAndPreserveUnknownData()
    {
        using Profile profile = new(); profile.Write("{\"Future\":{\"answer\":42},\"Code\":{\"Family\":\"raw code\",\"FutureFace\":\"keep\"}}");
        FontPreferences first = profile.Store.Load(), second = profile.Store.Load();
        profile.Store.SaveChanges(first, first with { Code = first.Code with { SizeInPoints = 11 } });
        FontPreferences merged = profile.Store.SaveChanges(second, second with { Commit = new("raw commit", 14), ShowEolMarkerAsGlyph = true });
        Assert.That(merged.Code.SizeInPoints, Is.EqualTo(11)); Assert.That(merged.Commit.SizeInPoints, Is.EqualTo(14));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        Assert.That(json.RootElement.GetProperty("Code").GetProperty("FutureFace").GetString(), Is.EqualTo("keep"));
        Assert.That(json.RootElement.GetProperty("Future").GetProperty("answer").GetInt32(), Is.EqualTo(42));
        Assert.That(profile.Store.SaveChanges(second, second), Is.EqualTo(merged));
    }

    [TestCase("{broken")]
    [TestCase("[]")]
    [TestCase("{\"Code\":null}")]
    [TestCase("{\"Code\":{\"SizeInPoints\":0}}")]
    [TestCase("{\"Code\":{\"SizeInPoints\":1e400}}")]
    [TestCase("{\"Code\":{\"Family\":\"@vertical\"}}")]
    [TestCase("{\"Code\":{\"Bold\":\"true\"}}")]
    [TestCase("{\"Code\":{},\"Code\":{}}")]
    [TestCase("{\"ShowEolMarkerAsGlyph\":null}")]
    public void MalformedKnownPreferencesAreNotOverwrittenEvenWithAnUnchangedDraft(string contents)
    {
        using Profile profile = new(); profile.Write(contents); byte[] before = File.ReadAllBytes(profile.Store.FilePath);
        Assert.That(() => profile.Store.Load(), Throws.InstanceOf<JsonException>());
        Assert.That(() => profile.Store.SaveChanges(new(), new()), Throws.InstanceOf<JsonException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Has.Length.EqualTo(1));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.MaxValue)]
    [TestCase(-1d)]
    public void InvalidSizeAndOverflowAreRejectedBeforeAnyPublication(double points)
    {
        using Profile profile = new(); FontPreferences draft = new() { Code = new("Consolas", points) };
        Assert.That(() => profile.Store.SaveChanges(new(), draft), Throws.ArgumentException); Assert.That(Directory.Exists(profile.Root), Is.False);
    }

    [Test]
    public void VerticalAndNulFamilyNamesAreRejectedBeforePublication()
    {
        using Profile profile = new();
        foreach (string family in new[] { "@vertical", "name\0tail", " " })
        { Assert.That(() => profile.Store.SaveChanges(new(), new() { Application = new(family, 9) }), Throws.ArgumentException); }
        Assert.That(Directory.Exists(profile.Root), Is.False);
    }

    [Test, Platform("Linux")]
    public void RealDirectoryWriteFailureLeavesExactProfileAndModeAndNoTemporaryFile()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux file-mode semantics."); return; }
        using Profile profile = new(); FontPreferences before = profile.Store.SaveChanges(new(), new() { ShowEolMarkerAsGlyph = true });
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); string directory = Path.GetDirectoryName(profile.Store.FilePath)!;
        UnixFileMode mode = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.That(() => profile.Store.SaveChanges(before, before with { ShowEolMarkerAsGlyph = false }), Throws.TypeOf<UnauthorizedAccessException>());
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes)); Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }
        finally { File.SetUnixFileMode(directory, mode); }
        Assert.That(File.GetUnixFileMode(directory), Is.EqualTo(mode));
    }

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-font-store-" + Guid.NewGuid().ToString("N"));
        internal FontPreferencesStore Store { get; }
        internal Profile() => Store = new(Root);
        internal void Write(string contents) { Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!); File.WriteAllText(Store.FilePath, contents); }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
