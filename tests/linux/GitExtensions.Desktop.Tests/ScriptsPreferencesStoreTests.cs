using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ScriptsPreferencesStoreTests
{
    [Test]
    public void MissingGlobalLinuxProfileDoesNotCreateOrImportScriptsFromOtherSettings()
    {
        using TemporaryProfile temporary = new();
        string? originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temporary.Directory);
            string mainPreferences = Path.Combine(temporary.Directory, "gitextensions", "preferences.json");
            Directory.CreateDirectory(Path.GetDirectoryName(mainPreferences)!);
            byte[] otherSettings = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                OwnScripts = ScriptConfiguration.WriteXml([new ScriptDefinition { Name = "Never imported", Command = "windows-only.exe", Enabled = true }]),
                Theme = "Dark"
            }));
            File.WriteAllBytes(mainPreferences, otherSettings);
            ScriptsPreferencesStore store = new();
            ScriptsPreferencesLoadResult loaded = store.Load();
            Assert.That(store.FilePath, Is.EqualTo(Path.Combine(temporary.Directory, "gitextensions", "scripts.json")));
            Assert.That(loaded.Existed, Is.False);
            Assert.That(loaded.Error, Is.Null);
            Assert.That(loaded.Configuration.Source, Is.EqualTo(ScriptConfigurationSource.Defaults));
            Assert.That(loaded.Scripts.All(script => !script.Enabled), Is.True);
            Assert.That(loaded.Scripts.Any(script => script.Name == "Never imported"), Is.False);
            Assert.That(File.Exists(store.FilePath), Is.False, "A read cannot create scripts or copy another profile.");
            Assert.That(File.ReadAllBytes(mainPreferences), Is.EqualTo(otherSettings));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(mainPreferences)!), Is.EqualTo(new[] { mainPreferences }));
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg); }
    }

    [Test]
    public void GlobalStoreRoundTripsEveryWindowsPropertyAndOrderWithoutTouchingBrowsePreferences()
    {
        using TemporaryProfile temporary = new();
        ScriptsPreferencesStore store = new(temporary.File);
        string browseFile = Path.Combine(temporary.Directory, "preferences.json");
        byte[] browseBytes = Encoding.UTF8.GetBytes("{\"Theme\":\"Dark\",\"RecentRepositories\":[\"literal repo\"]}");
        File.WriteAllBytes(browseFile, browseBytes);
        ScriptDefinition first = new()
        {
            Enabled = false, Name = "&Café\nΩ", Command = "/literal exe", Arguments = "{{WorkingDir}}\r\n{UserInput:message=x}",
            AddToRevisionGridContextMenu = true, OnEvent = ScriptEvent.BeforeFetch, AskConfirmation = true,
            RunInBackground = true, IsPowerShell = true, HotkeyCommandIdentifier = 9023, Icon = "Console", IconFilePath = " leading.ico "
        };
        ScriptDefinition second = new() { Name = null, Command = "", Arguments = null, Icon = "", IconFilePath = null, HotkeyCommandIdentifier = 9017 };
        store.Save([first, second]);
        ScriptsPreferencesLoadResult loaded = new ScriptsPreferencesStore(temporary.File).Load();
        Assert.That(loaded.Existed, Is.True);
        Assert.That(loaded.Error, Is.Null);
        Assert.That(loaded.Scripts, Is.EqualTo(new[] { first, second }));
        Assert.That(loaded.Configuration.Source, Is.EqualTo(ScriptConfigurationSource.Xml));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
        Assert.That(json.RootElement.GetProperty("OwnScripts").GetString(), Is.EqualTo(loaded.Configuration.OriginalText));
        Assert.That(loaded.OriginalBytes.ToArray(), Is.EqualTo(File.ReadAllBytes(store.FilePath)));
        Assert.That(File.ReadAllBytes(browseFile), Is.EqualTo(browseBytes));
        Assert.That(Directory.GetFiles(temporary.Directory, "*.tmp"), Is.Empty);
    }

    [Test]
    public void AnExplicitEmptyListStaysEmptyAcrossStoresInsteadOfRecreatingDisabledExamples()
    {
        using TemporaryProfile temporary = new();
        ScriptsPreferencesStore store = new(temporary.File);
        Assert.That(store.Load().Configuration.Source, Is.EqualTo(ScriptConfigurationSource.Defaults));
        store.Save([]);
        ScriptsPreferencesLoadResult loaded = new ScriptsPreferencesStore(temporary.File).Load();
        Assert.That(loaded.Scripts, Is.Empty);
        Assert.That(loaded.Configuration.Source, Is.EqualTo(ScriptConfigurationSource.Xml));
        Assert.That(loaded.Configuration.OriginalText, Does.Contain("ArrayOfScriptInfo"));
        Assert.That(File.Exists(temporary.File), Is.True);
    }

    [Test]
    public void XmlAndLegacyImportsAreExplicitParsedPreviewsWithoutAnyProfileWrite()
    {
        using TemporaryProfile temporary = new();
        ScriptsPreferencesStore store = new(temporary.File);
        store.Save([new() { Name = "Current", Command = "git", HotkeyCommandIdentifier = 9009, Enabled = false }]);
        byte[] original = File.ReadAllBytes(temporary.File);
        string xml = ScriptConfiguration.WriteXml([new() { Name = "XML imported", Command = "git", HotkeyCommandIdentifier = 9020 }]);
        ScriptConfigurationResult importedXml = store.ParseImport(xml);
        ScriptConfigurationResult importedLegacy = store.ParseImport(" leading name <_PARAM_SEPARATOR_>literal.exe<_PARAM_SEPARATOR_> literal args <_PARAM_SEPARATOR_>yes");
        Assert.That(importedXml.Source, Is.EqualTo(ScriptConfigurationSource.Xml));
        Assert.That(importedXml.Scripts.Single().Name, Is.EqualTo("XML imported"));
        Assert.That(importedLegacy.Source, Is.EqualTo(ScriptConfigurationSource.Legacy));
        Assert.That(importedLegacy.Scripts.Single().Name, Is.EqualTo(" leading name "));
        Assert.That(importedLegacy.Scripts.Single().Arguments, Is.EqualTo(" literal args "));
        Assert.That(importedLegacy.Scripts.Single().Enabled, Is.True, "Legacy imports preserve the original field default; preview does not enable it in the profile.");
        Assert.That(File.ReadAllBytes(temporary.File), Is.EqualTo(original));
        Assert.That(store.Load().Scripts.Single().Name, Is.EqualTo("Current"));
        Assert.That(Directory.GetFiles(temporary.Directory), Is.EqualTo(new[] { temporary.File }));
    }

    [Test]
    public void CorruptJsonUtf8XmlAndLegacyRemainExactAndRefuseImplicitSave()
    {
        using TemporaryProfile temporary = new();
        ScriptsPreferencesStore store = new(temporary.File);
        byte[][] corruptions =
        [
            Encoding.UTF8.GetBytes("{broken"),
            [0x7b, 0x22, 0x80, 0xff, 0x22, 0x7d],
            JsonSerializer.SerializeToUtf8Bytes(new { OwnScripts = "<ArrayOfScriptInfo><ScriptInfo>" }),
            JsonSerializer.SerializeToUtf8Bytes(new { OwnScripts = "name<_PARAM_SEPARATOR_>incomplete" })
        ];
        foreach (byte[] bytes in corruptions)
        {
            File.WriteAllBytes(temporary.File, bytes);
            ScriptsPreferencesLoadResult loaded = store.Load();
            Assert.That(loaded.Error, Is.Not.Null);
            Assert.That(loaded.Existed, Is.True);
            Assert.That(loaded.Scripts, Is.Empty);
            Assert.That(loaded.OriginalBytes.ToArray(), Is.EqualTo(bytes), "Error reporting must retain bytes, including invalid UTF8.");
            Assert.Throws<InvalidDataException>(() => store.Save([]));
            Assert.That(File.ReadAllBytes(temporary.File), Is.EqualTo(bytes), "No damaged file becomes an empty successful save.");
            Assert.That(Directory.GetFiles(temporary.Directory), Is.EqualTo(new[] { temporary.File }));
        }
    }

    [Test]
    public void ReplacingAnUnreadableProfileRequiresAnExplicitCallerDecision()
    {
        using TemporaryProfile temporary = new();
        byte[] damaged = [0xff, 0xfe, 0x00];
        File.WriteAllBytes(temporary.File, damaged);
        ScriptsPreferencesStore store = new(temporary.File);
        ScriptsPreferencesLoadResult preimage = store.Load();
        Assert.Throws<InvalidDataException>(() => store.Save([]));
        Assert.That(File.ReadAllBytes(temporary.File), Is.EqualTo(damaged));
        ScriptDefinition repaired = new() { Name = "Reviewed replacement", Command = "git", Arguments = "status", Enabled = false, HotkeyCommandIdentifier = 9015 };
        store.Save([repaired], replaceUnreadable: true);
        Assert.That(store.Load().Error, Is.Null);
        Assert.That(store.Load().Scripts, Is.EqualTo(new[] { repaired }));
        Assert.That(preimage.OriginalBytes.ToArray(), Is.EqualTo(damaged), "The diagnostic preimage remains usable after the explicit replacement.");
        Assert.That(Directory.GetFiles(temporary.Directory, "*.tmp"), Is.Empty);
    }

    [Test]
    public void InvalidModelFailsBeforeWritingAnyPartOfAnExistingProfile()
    {
        using TemporaryProfile temporary = new();
        ScriptsPreferencesStore store = new(temporary.File);
        store.Save([new() { Name = "Kept", Command = "git", Enabled = false }]);
        byte[] before = File.ReadAllBytes(temporary.File);
        Assert.Throws<ArgumentException>(() => store.Save([new() { Command = "git", OnEvent = (ScriptEvent)237 }]));
        Assert.That(File.ReadAllBytes(temporary.File), Is.EqualTo(before));
        Assert.That(store.Load().Scripts.Single().Name, Is.EqualTo("Kept"));
        Assert.That(Directory.GetFiles(temporary.Directory), Is.EqualTo(new[] { temporary.File }));
    }

    [Test]
    public void ABlockedProfileParentReportsIoFailureWithoutReplacingAnyExistingFile()
    {
        using TemporaryProfile temporary = new();
        string blocker = Path.Combine(temporary.Directory, "blocked");
        byte[] content = Encoding.UTF8.GetBytes("literal preexisting file Ω");
        File.WriteAllBytes(blocker, content);
        ScriptsPreferencesStore store = new(Path.Combine(blocker, "scripts.json"));
        Assert.Throws<IOException>(() => store.Save([new() { Name = "Not saved", Command = "git" }], replaceUnreadable: true));
        Assert.That(File.ReadAllBytes(blocker), Is.EqualTo(content));
        Assert.That(Directory.GetFiles(temporary.Directory), Is.EqualTo(new[] { blocker }));
        Assert.That(File.Exists(store.FilePath), Is.False);
    }

    private sealed class TemporaryProfile : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-script-profile-" + Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Directory, "scripts.json");
        internal TemporaryProfile() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
