using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptsPreferencesConcurrencyTests
{
    [Test]
    public void AnUnchangedDraftDoesNoIoEvenWhenTheProfileHasBecomeUnreadable()
    {
        using Profile f = new(); f.Store.Save(f.Initial);
        ScriptsPreferencesLoadResult baseline = f.Store.Load(); byte[] corruption = Encoding.UTF8.GetBytes("{keep exact late corruption");
        File.WriteAllBytes(f.File, corruption); int notifications = 0;
        void Saved(string path) { if (path == f.File) { notifications++; } }
        ScriptsPreferencesStore.Saved += Saved;
        try
        {
            Assert.That(f.Store.SaveChanges(baseline, baseline.Scripts), Is.SameAs(baseline));
            Assert.That(File.ReadAllBytes(f.File), Is.EqualTo(corruption)); Assert.That(notifications, Is.Zero);
            Assert.That(Directory.GetFiles(f.Directory), Is.EqualTo(new[] { f.File }));
        }
        finally { ScriptsPreferencesStore.Saved -= Saved; }
    }

    [TestCase("fields")]
    [TestCase("order")]
    [TestCase("identifier")]
    public void TwoSettingsBaselinesCannotSilentlyReplaceNewerKnownDefinitions(string change)
    {
        using Profile f = new(); f.Store.Save(f.Initial);
        ScriptsPreferencesLoadResult a = f.Store.Load(), b = new ScriptsPreferencesStore(f.File).Load();
        ScriptDefinition[] first = a.Scripts.ToArray();
        if (change == "fields") { first[0] = first[0] with { Name = "Saved by A", Enabled = true }; }
        else if (change == "order") { Array.Reverse(first); }
        else { first[0] = first[0] with { HotkeyCommandIdentifier = 9045 }; }
        f.Store.SaveChanges(a, first); byte[] latest = File.ReadAllBytes(f.File);
        ScriptDefinition[] second = b.Scripts.ToArray(); second[1] = second[1] with { Arguments = "B draft must not overwrite A" };
        Assert.Throws<IOException>(() => f.Store.SaveChanges(b, second));
        Assert.That(File.ReadAllBytes(f.File), Is.EqualTo(latest)); Assert.That(f.Store.Load().Scripts, Is.EqualTo(first));
        Assert.That(Directory.GetFiles(f.Directory), Is.EqualTo(new[] { f.File }));
    }

    [Test]
    public void ANewerUnknownJsonFieldIsPreservedWhenKnownScriptsStillMatchTheBaseline()
    {
        using Profile f = new(); f.Store.Save(f.Initial); ScriptsPreferencesLoadResult baseline = f.Store.Load();
        string xml = baseline.Configuration.OriginalText!;
        File.WriteAllText(f.File, JsonSerializer.Serialize(new { OwnScripts = xml, Future = new { Value = "Ω\r\n literal ", Items = new[] { 1, 3, 5 } }, FutureFlag = true }));
        ScriptDefinition[] draft = baseline.Scripts.ToArray(); draft[0] = draft[0] with { Arguments = " literal {UserInput:x=y} " };
        ScriptsPreferencesLoadResult saved = f.Store.SaveChanges(baseline, draft);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(f.File));
        Assert.That(json.RootElement.GetProperty("Future").GetProperty("Value").GetString(), Is.EqualTo("Ω\r\n literal "));
        Assert.That(json.RootElement.GetProperty("Future").GetProperty("Items").EnumerateArray().Select(x => x.GetInt32()), Is.EqualTo(new[] { 1, 3, 5 }));
        Assert.That(json.RootElement.GetProperty("FutureFlag").GetBoolean(), Is.True);
        Assert.That(saved.Scripts, Is.EqualTo(draft)); Assert.That(saved.OriginalBytes.ToArray(), Is.EqualTo(File.ReadAllBytes(f.File)));
    }

    [TestCase("json")]
    [TestCase("xml")]
    [TestCase("utf8")]
    public void DirtyDraftRefusesLateCorruptionWithoutPublishingOrChangingAnyByte(string kind)
    {
        using Profile f = new(); f.Store.Save(f.Initial); ScriptsPreferencesLoadResult baseline = f.Store.Load();
        byte[] broken = kind switch { "json" => Encoding.UTF8.GetBytes("{broken"), "xml" => JsonSerializer.SerializeToUtf8Bytes(new { OwnScripts = "<broken" }), _ => [0xff, 0xfe, 0x80] };
        File.WriteAllBytes(f.File, broken); int saved = 0;
        void Observe(string path) { if (path == f.File) { saved++; } }
        ScriptsPreferencesStore.Saved += Observe;
        try
        {
            ScriptDefinition[] draft = baseline.Scripts.ToArray(); draft[0] = draft[0] with { Name = "Changed" };
            Assert.Throws<InvalidDataException>(() => f.Store.SaveChanges(baseline, draft));
            Assert.That(File.ReadAllBytes(f.File), Is.EqualTo(broken)); Assert.That(saved, Is.Zero);
            Assert.That(Directory.GetFiles(f.Directory), Is.EqualTo(new[] { f.File }));
        }
        finally { ScriptsPreferencesStore.Saved -= Observe; }
    }

    [Test]
    public void AnUnreadableBaselineCannotBecomeAnImplicitReplacementByAddingAScript()
    {
        using Profile f = new(); byte[] raw = Encoding.UTF8.GetBytes("{not an empty list"); File.WriteAllBytes(f.File, raw);
        ScriptsPreferencesLoadResult baseline = f.Store.Load(); Assert.That(baseline.Error, Is.Not.Null);
        Assert.Throws<InvalidDataException>(() => f.Store.SaveChanges(baseline, [new() { Name = "Draft", HotkeyCommandIdentifier = 9001 }]));
        Assert.That(File.ReadAllBytes(f.File), Is.EqualTo(raw));
    }

    [Test]
    public void ARealIoFaultAfterOpeningKeepsTheIncompatibleFilesystemEntryAndNoTemporaryFile()
    {
        using Profile f = new(); f.Store.Save(f.Initial); ScriptsPreferencesLoadResult baseline = f.Store.Load();
        File.Delete(f.File); Directory.CreateDirectory(f.File); string sentinel = Path.Combine(f.File, "keep"); File.WriteAllText(sentinel, "literal Ω");
        ScriptDefinition[] draft = baseline.Scripts.ToArray(); draft[0] = draft[0] with { Command = "changed" };
        Assert.Throws<InvalidDataException>(() => f.Store.SaveChanges(baseline, draft));
        Assert.That(File.ReadAllText(sentinel), Is.EqualTo("literal Ω")); Assert.That(Directory.GetFiles(f.Directory), Is.Empty);
    }

    [Test]
    public void SuccessfulPublicationIsPrivateAndNotifiedOnlyAfterTheExactBytesAreInstalled()
    {
        using Profile f = new(); ScriptsPreferencesLoadResult baseline = f.Store.Load(); int calls = 0;
        void Saved(string path)
        {
            if (path != f.File) { return; }
            calls++; Assert.That(File.Exists(path), Is.True); Assert.That(f.Store.Load().Scripts, Is.EqualTo(f.Initial));
        }
        ScriptsPreferencesStore.Saved += Saved;
        try
        {
            ScriptsPreferencesLoadResult result = f.Store.SaveChanges(baseline, f.Initial);
            Assert.That(result.OriginalBytes.ToArray(), Is.EqualTo(File.ReadAllBytes(f.File))); Assert.That(calls, Is.EqualTo(1));
            if (!OperatingSystem.IsWindows()) { Assert.That(File.GetUnixFileMode(f.File), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite)); }
            Assert.That(Directory.GetFiles(f.Directory), Is.EqualTo(new[] { f.File }));
        }
        finally { ScriptsPreferencesStore.Saved -= Saved; }
    }

    [Test]
    public void UntouchedXmlExtensionsRemainByteExactButKnownEditsUseTheWindowsXmlRewriteContract()
    {
        using Profile f = new(); string xml = ScriptConfiguration.WriteXml(f.Initial).Replace("</ScriptInfo>", "<FutureValue>opaque</FutureValue></ScriptInfo>", StringComparison.Ordinal);
        File.WriteAllText(f.File, JsonSerializer.Serialize(new { OwnScripts = xml })); byte[] bytes = File.ReadAllBytes(f.File);
        ScriptsPreferencesLoadResult baseline = f.Store.Load(); Assert.That(baseline.Error, Is.Null);
        f.Store.SaveChanges(baseline, baseline.Scripts); Assert.That(File.ReadAllBytes(f.File), Is.EqualTo(bytes));
        ScriptDefinition[] draft = baseline.Scripts.ToArray(); draft[0] = draft[0] with { Name = "Known edit" };
        ScriptsPreferencesLoadResult saved = f.Store.SaveChanges(baseline, draft);
        Assert.That(saved.Configuration.OriginalText, Does.Not.Contain("FutureValue"), "Windows XmlSerializer also rewrites only the known ScriptInfo fields; no extension-preserving XML framework is claimed.");
    }

    private sealed class Profile : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "scripts-save-" + Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Directory, "scripts.json");
        internal ScriptsPreferencesStore Store { get; }
        internal ScriptDefinition[] Initial { get; } = [new() { HotkeyCommandIdentifier = 9001, Name = "&One", Command = "literal", Enabled = false }, new() { HotkeyCommandIdentifier = 9002, Name = "Two", Arguments = "" }];
        internal Profile() { System.IO.Directory.CreateDirectory(Directory); Store = new(File); }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); } }
    }
}
