using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitEnvironmentPreferencesTests
{
    private const string ValidProfile = """{"Version":1,"GitExecutable":"","ToolsDirectory":"","CustomHomeDirectory":null,"CheckAtStartup":true}""";

    [Test]
    public void MissingProfileReturnsDefaultsAndAnUnchangedSaveDoesNotPublishThem()
    {
        using Profile profile = new();
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        Assert.Multiple(() =>
        {
            Assert.That(baseline.Preferences, Is.EqualTo(new GitEnvironmentPreferences()));
            Assert.That(baseline.Existed, Is.False);
            Assert.That(baseline.OriginalBytes.IsEmpty, Is.True);
            Assert.That(baseline.ProfilePath, Is.EqualTo(Path.Combine(profile.Root, "gitextensions", "git-environment.json")));
            Assert.That(baseline.Error, Is.Null);
        });

        GitEnvironmentPreferencesLoadResult unchanged = profile.Store.SaveChanges(baseline, baseline.Preferences);
        Assert.That(unchanged.Preferences, Is.EqualTo(baseline.Preferences));
        Assert.That(unchanged.Existed, Is.False);
        Assert.That(Directory.Exists(profile.Root), Is.False);
    }

    [TestCase(null, true)]
    [TestCase("", false)]
    [TestCase(" ~/maison é漢字/$HOME;$(literal)\nfin ", false)]
    public void LiteralPathsAndEveryHomeChoiceRoundTripWithoutChangingTheProcessEnvironment(string? home, bool checkAtStartup)
    {
        string environment = EnvironmentFingerprint();
        using Profile profile = new();
        GitEnvironmentPreferences draft = new()
        {
            GitExecutable = " ~/bin/git é漢字;$(literal) \"quoted\"\\tool\nfin ",
            ToolsDirectory = " ../outils é漢字/$PATH:\"literal\" ",
            CustomHomeDirectory = home,
            CheckAtStartup = checkAtStartup
        };

        GitEnvironmentPreferencesLoadResult saved = profile.Store.SaveChanges(profile.Store.Load(), draft);
        GitEnvironmentPreferencesLoadResult loaded = new GitEnvironmentPreferencesStore(profile.Root).Load();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Preferences, Is.EqualTo(draft));
            Assert.That(loaded.Preferences, Is.EqualTo(draft));
            Assert.That(loaded.Existed, Is.True);
            Assert.That(loaded.Error, Is.Null);
            Assert.That(loaded.OriginalBytes.ToArray(), Is.EqualTo(File.ReadAllBytes(profile.Store.FilePath)));
            Assert.That(EnvironmentFingerprint(), Is.EqualTo(environment), "Loading and saving application choices must not change inherited environment variables.");
        });
        using JsonDocument json = JsonDocument.Parse(loaded.OriginalBytes);
        Assert.That(json.RootElement.GetProperty("Version").GetInt32(), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
    }

    [Test]
    public void NoOpPreservesExactBytesAndAnEditPreservesUnknownJsonProperties()
    {
        using Profile profile = new();
        profile.Write("  {\r\n\"Future\": {\"enabled\":true,\"values\":[null,17,\"é漢字\"],\"nested\":{\"keep\":\"literal\"}},\r\n"
            + "\"gitExecutable\":\"case-sensitive future property\",\r\n"
            + "\"Version\":1,\"GitExecutable\":\"literal git\",\"ToolsDirectory\":\"\",\"CustomHomeDirectory\":null,\"CheckAtStartup\":true }\r\n");
        byte[] original = File.ReadAllBytes(profile.Store.FilePath);
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();

        GitEnvironmentPreferencesLoadResult unchanged = profile.Store.SaveChanges(baseline, baseline.Preferences);
        Assert.That(unchanged.OriginalBytes.ToArray(), Is.EqualTo(original));
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(original));

        GitEnvironmentPreferences draft = baseline.Preferences with { ToolsDirectory = "literal tools é漢字" };
        profile.Store.SaveChanges(baseline, draft);
        using JsonDocument before = JsonDocument.Parse(original);
        using JsonDocument after = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        Assert.That(JsonSerializer.Serialize(after.RootElement.GetProperty("Future")),
            Is.EqualTo(JsonSerializer.Serialize(before.RootElement.GetProperty("Future"))));
        Assert.That(after.RootElement.GetProperty("gitExecutable").GetString(), Is.EqualTo("case-sensitive future property"));
        Assert.That(profile.Store.Load().Preferences, Is.EqualTo(draft));
    }

    [TestCaseSource(nameof(InvalidProfiles))]
    public void InvalidOrDuplicateProfilesAreReportedAndNeverOverwritten(string contents)
    {
        using Profile profile = new();
        profile.Write(contents);
        AssertInvalidProfileIsPreserved(profile, typeof(JsonException));
    }

    [Test]
    public void InvalidUtf8IsReportedAndItsOriginalBytesAreNeverOverwritten()
    {
        using Profile profile = new();
        profile.Write([0x7B, 0x22, 0xC3, 0x28, 0x22, 0x7D]);
        AssertInvalidProfileIsPreserved(profile, typeof(DecoderFallbackException));
    }

    [Test]
    public void EncodedNulPathIsReportedAndItsOriginalBytesAreNeverOverwritten()
    {
        using Profile profile = new();
        profile.Write(ValidProfile.Replace("\"GitExecutable\":\"\"", "\"GitExecutable\":\"bad\\u0000tail\"", StringComparison.Ordinal));
        AssertInvalidProfileIsPreserved(profile, typeof(ArgumentException));
    }

    [TestCase("git")]
    [TestCase("tools")]
    [TestCase("home")]
    [TestCase("null-git")]
    [TestCase("null-tools")]
    public void InvalidDraftCannotCreateOrOverwriteAProfile(string field)
    {
        foreach (bool existing in new[] { false, true })
        {
            using Profile profile = new();
            if (existing) { profile.Write(ValidProfile); }
            GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
            byte[]? original = existing ? File.ReadAllBytes(profile.Store.FilePath) : null;
            GitEnvironmentPreferences draft = field switch
            {
                "git" => baseline.Preferences with { GitExecutable = "bad\0tail" },
                "tools" => baseline.Preferences with { ToolsDirectory = "bad\0tail" },
                "home" => baseline.Preferences with { CustomHomeDirectory = "bad\0tail" },
                "null-git" => baseline.Preferences with { GitExecutable = null! },
                "null-tools" => baseline.Preferences with { ToolsDirectory = null! },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };

            Assert.That(() => profile.Store.SaveChanges(baseline, draft), Throws.InstanceOf<ArgumentException>());
            if (existing)
            {
                Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(original));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
            }
            else { Assert.That(Directory.Exists(profile.Root), Is.False); }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BaselineFromAnotherProfileCannotPublishEvenAnUnchangedDraft(bool edited)
    {
        using Profile source = new(), target = new();
        source.Write(ValidProfile);
        target.Write(ValidProfile.Replace("\"ToolsDirectory\":\"\"", "\"ToolsDirectory\":\"target tools\"", StringComparison.Ordinal));
        GitEnvironmentPreferencesLoadResult foreign = source.Store.Load();
        byte[] sourceBytes = File.ReadAllBytes(source.Store.FilePath), targetBytes = File.ReadAllBytes(target.Store.FilePath);
        GitEnvironmentPreferences draft = edited ? foreign.Preferences with { GitExecutable = "foreign git" } : foreign.Preferences;

        Assert.That(() => target.Store.SaveChanges(foreign, draft), Throws.InstanceOf<ArgumentException>());
        Assert.That(File.ReadAllBytes(source.Store.FilePath), Is.EqualTo(sourceBytes));
        Assert.That(File.ReadAllBytes(target.Store.FilePath), Is.EqualTo(targetBytes));
    }

    [Test]
    public void EditedStaleDraftCannotOverwriteAChangePublishedByAnotherWindow()
    {
        using Profile profile = new();
        profile.Write(ValidProfile);
        GitEnvironmentPreferencesLoadResult older = profile.Store.Load(), otherWindow = profile.Store.Load();
        GitEnvironmentPreferences published = otherWindow.Preferences with { ToolsDirectory = "newer tools" };
        profile.Store.SaveChanges(otherWindow, published);
        byte[] newerBytes = File.ReadAllBytes(profile.Store.FilePath);

        Assert.That(() => profile.Store.SaveChanges(older, older.Preferences with { GitExecutable = "older draft git" }),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(newerBytes));
        Assert.That(profile.Store.Load().Preferences, Is.EqualTo(published));
    }

    [Test]
    public void AnUnchangedOlderWindowReturnsTheLatestProfileWithoutUndoingIt()
    {
        using Profile profile = new();
        GitEnvironmentPreferencesLoadResult older = profile.Store.Load();
        GitEnvironmentPreferences published = new() { GitExecutable = "newer git", CustomHomeDirectory = "literal home", CheckAtStartup = false };
        profile.Store.SaveChanges(profile.Store.Load(), published);
        byte[] newerBytes = File.ReadAllBytes(profile.Store.FilePath);

        GitEnvironmentPreferencesLoadResult result = profile.Store.SaveChanges(older, older.Preferences);
        Assert.That(result.Preferences, Is.EqualTo(published));
        Assert.That(result.OriginalBytes.ToArray(), Is.EqualTo(newerBytes));
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(newerBytes));
    }

    [Test]
    public void FormattingOnlyExternalChangesStillVetoAnEditedStaleDraft()
    {
        using Profile profile = new();
        profile.Write(ValidProfile);
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        profile.Write(ValidProfile + "\r\n");
        byte[] newerBytes = File.ReadAllBytes(profile.Store.FilePath);

        Assert.That(() => profile.Store.SaveChanges(baseline, baseline.Preferences with { CheckAtStartup = false }),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(newerBytes));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExternalCreationOrDeletionVetoesAnEditedDraft(bool originallyExisted)
    {
        using Profile profile = new();
        if (originallyExisted) { profile.Write(ValidProfile); }
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        if (originallyExisted) { File.Delete(profile.Store.FilePath); }
        else { profile.Write(ValidProfile); }

        Assert.That(() => profile.Store.SaveChanges(baseline, baseline.Preferences with { GitExecutable = "stale git" }),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(File.Exists(profile.Store.FilePath), Is.EqualTo(!originallyExisted));
        if (!originallyExisted) { Assert.That(File.ReadAllText(profile.Store.FilePath), Is.EqualTo(ValidProfile)); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AProfileThatBecomesUnreadableCannotBeOverwrittenEvenByANoOp(bool edited)
    {
        using Profile profile = new();
        profile.Write(ValidProfile);
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        profile.Write("{external invalid profile");
        byte[] invalidBytes = File.ReadAllBytes(profile.Store.FilePath);
        GitEnvironmentPreferences draft = edited ? baseline.Preferences with { ToolsDirectory = "new tools" } : baseline.Preferences;

        Assert.That(() => profile.Store.SaveChanges(baseline, draft), Throws.TypeOf<InvalidDataException>());
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(invalidBytes));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
    }

    [Test]
    public void ARealParentPathIoFailureDoesNotPublishOrReplaceTheBlockingFile()
    {
        using Profile profile = new();
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        byte[] witness = Encoding.UTF8.GetBytes("a real file prevents creation of the profile directory\n");
        File.WriteAllBytes(profile.Root, witness);

        Assert.That(() => profile.Store.SaveChanges(baseline, baseline.Preferences with { GitExecutable = "literal git" }),
            Throws.InstanceOf<IOException>());
        Assert.That(File.ReadAllBytes(profile.Root), Is.EqualTo(witness));
        Assert.That(Directory.Exists(profile.Root), Is.False);
        Assert.That(File.Exists(profile.Store.FilePath), Is.False);
    }

    [Test]
    public void ADirectoryAtTheProfilePathIsReportedAndCannotBeReplacedByDefaults()
    {
        using Profile profile = new();
        Directory.CreateDirectory(profile.Store.FilePath);
        string witnessPath = Path.Combine(profile.Store.FilePath, "unrelated-file");
        byte[] witness = Encoding.UTF8.GetBytes("keep the existing directory and its content\n");
        File.WriteAllBytes(witnessPath, witness);

        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        Assert.Multiple(() =>
        {
            Assert.That(baseline.Existed, Is.True);
            Assert.That(baseline.Preferences, Is.EqualTo(new GitEnvironmentPreferences()));
            Assert.That(baseline.Error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        });
        foreach (GitEnvironmentPreferences draft in new[] { baseline.Preferences, baseline.Preferences with { GitExecutable = "new git" } })
        { Assert.That(() => profile.Store.SaveChanges(baseline, draft), Throws.TypeOf<InvalidDataException>()); }
        Assert.That(Directory.Exists(profile.Store.FilePath), Is.True);
        Assert.That(File.ReadAllBytes(witnessPath), Is.EqualTo(witness));
        Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
    }

    [Test, Platform("Linux")]
    public void ARealDirectoryPermissionFailurePreservesTheProfileAndLeavesNoTemporaryFile()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux file-mode semantics."); return; }
        using Profile profile = new();
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.SaveChanges(profile.Store.Load(), new() { GitExecutable = "original git" });
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath);
        string directory = Path.GetDirectoryName(profile.Store.FilePath)!;
        UnixFileMode directoryMode = File.GetUnixFileMode(directory), profileMode = File.GetUnixFileMode(profile.Store.FilePath);
        string probe = Path.Combine(directory, "permission-probe");
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            bool denied = false;
            try { using FileStream stream = File.Create(probe); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied)
            {
                File.Delete(probe);
                Assert.Ignore("This process bypasses directory write permissions; the separate parent-path test covers a real IO failure.");
            }

            Assert.That(() => profile.Store.SaveChanges(baseline, baseline.Preferences with { ToolsDirectory = "new tools" }),
                Throws.TypeOf<UnauthorizedAccessException>());
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
            Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(profileMode));
            Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { profile.Store.FilePath }));
        }
        finally { File.SetUnixFileMode(directory, directoryMode); }
    }

    [Test, Platform("Linux")]
    public void NewProfilesArePrivateAndReplacingAnExistingProfilePreservesItsMode()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux file-mode semantics."); return; }
        using Profile profile = new();
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.SaveChanges(profile.Store.Load(), new() { ToolsDirectory = "original tools" });
        string directory = Path.GetDirectoryName(profile.Store.FilePath)!;
        Assert.That(File.GetUnixFileMode(directory), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

        UnixFileMode existingMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(profile.Store.FilePath, existingMode);
        GitEnvironmentPreferences draft = baseline.Preferences with { CheckAtStartup = false };
        profile.Store.SaveChanges(baseline, draft);
        Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(existingMode));
        Assert.That(profile.Store.Load().Preferences, Is.EqualTo(draft));
    }

    private static IEnumerable<TestCaseData> InvalidProfiles()
    {
        foreach ((string name, string contents) in new[]
        {
            ("malformed", "{broken"),
            ("non-object", "[]"),
            ("missing-fields", "{}"),
            ("unsupported-version", ValidProfile.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal)),
            ("string-version", ValidProfile.Replace("\"Version\":1", "\"Version\":\"1\"", StringComparison.Ordinal)),
            ("null-git", ValidProfile.Replace("\"GitExecutable\":\"\"", "\"GitExecutable\":null", StringComparison.Ordinal)),
            ("numeric-tools", ValidProfile.Replace("\"ToolsDirectory\":\"\"", "\"ToolsDirectory\":42", StringComparison.Ordinal)),
            ("boolean-home", ValidProfile.Replace("\"CustomHomeDirectory\":null", "\"CustomHomeDirectory\":false", StringComparison.Ordinal)),
            ("string-startup-check", ValidProfile.Replace("\"CheckAtStartup\":true", "\"CheckAtStartup\":\"true\"", StringComparison.Ordinal)),
            ("duplicate-known", ValidProfile.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal)),
            ("duplicate-unknown", ValidProfile.Replace("\"Version\":1", "\"Version\":1,\"Future\":1,\"Future\":2", StringComparison.Ordinal))
        })
        { yield return new TestCaseData(contents).SetName(nameof(InvalidOrDuplicateProfilesAreReportedAndNeverOverwritten) + "_" + name); }
    }

    private static void AssertInvalidProfileIsPreserved(Profile profile, Type errorType)
    {
        byte[] original = File.ReadAllBytes(profile.Store.FilePath);
        GitEnvironmentPreferencesLoadResult baseline = profile.Store.Load();
        Assert.Multiple(() =>
        {
            Assert.That(baseline.Existed, Is.True);
            Assert.That(baseline.Preferences, Is.EqualTo(new GitEnvironmentPreferences()));
            Assert.That(baseline.OriginalBytes.ToArray(), Is.EqualTo(original));
            Assert.That(baseline.Error, Is.InstanceOf(errorType));
        });
        foreach (GitEnvironmentPreferences draft in new[] { baseline.Preferences, baseline.Preferences with { GitExecutable = "new git" } })
        { Assert.That(() => profile.Store.SaveChanges(baseline, draft), Throws.TypeOf<InvalidDataException>()); }
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!), Is.EqualTo(new[] { profile.Store.FilePath }));
    }

    private static string EnvironmentFingerprint()
    {
        SortedDictionary<string, string?> values = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) { values.Add((string)entry.Key, (string?)entry.Value); }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    }

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-git-environment-profile-" + Guid.NewGuid().ToString("N"));
        internal GitEnvironmentPreferencesStore Store { get; }
        internal Profile() => Store = new(Root);
        internal void Write(string contents) => Write(Encoding.UTF8.GetBytes(contents));
        internal void Write(byte[] contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
            File.WriteAllBytes(Store.FilePath, contents);
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
            else if (File.Exists(Root)) { File.Delete(Root); }
        }
    }
}
