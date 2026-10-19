using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitExtensions.Linux.Desktop;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BuildServerCredentialsStoreTests
{
    private const string Provider = "TeamCity";
    private const string Endpoint = "https://ci.example.invalid/teamcity/";
    private const string Secret = "FAKE_CI_SECRET_do_not_log_012345";

    [Test]
    public void MissingStoreUsesXdgUserProfileAndNeverReadsOrCreatesRepositoryOrHostingPreferences()
    {
        using Profile profile = new(); string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", profile.Root);
            BuildServerCredentialsStore store = new();
            Assert.That(store.FilePath, Is.EqualTo(profile.Store.FilePath));
            Assert.That(store.FilePath, Is.EqualTo(Path.Combine(profile.Root, "gitextensions", "build-server-credentials.json")));
            Assert.That(store.FilePath, Is.Not.EqualTo(new PreferencesStore().FilePath));
            BuildServerCredentialsSnapshot missing = store.Load(Provider, new Uri(Endpoint));
            Assert.That(missing.Exists, Is.False); Assert.That(missing.Provider, Is.EqualTo(Provider)); Assert.That(missing.Endpoint, Is.EqualTo(Endpoint));
            Assert.That(Model(missing.Credentials), Is.EqualTo((BuildServerCredentialsType.Guest, (string?)null, (string?)null, (string?)null)));
            Assert.That(store.SaveChanges(missing, null), Is.SameAs(missing));
            Assert.That(Directory.Exists(profile.Root), Is.False);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); }
    }

    [TestCase(BuildServerCredentialsType.Guest)]
    [TestCase(BuildServerCredentialsType.UsernameAndPassword)]
    [TestCase(BuildServerCredentialsType.BearerToken)]
    public void OriginalModesAndInactiveLiteralValuesRoundTripWithClonedInputsOutputsAndSavedBaselines(BuildServerCredentialsType mode)
    {
        using Profile profile = new();
        BuildServerCredentials draft = Credentials(mode); var expected = Model(draft);
        BuildServerCredentialsSnapshot saved = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), draft);
        draft.Username = "caller changed user"; draft.Password = "caller changed password"; draft.BearerToken = "caller changed token";
        draft.BuildServerCredentialsType = BuildServerCredentialsType.Guest;
        Assert.That(saved.Exists, Is.True); Assert.That(Model(saved.Credentials), Is.EqualTo(expected));
        BuildServerCredentials output = saved.Credentials; output.Password = "mutated returned clone"; output.Username = null;
        Assert.That(saved.Credentials, Is.Not.SameAs(output)); Assert.That(Model(saved.Credentials), Is.EqualTo(expected));
        BuildServerCredentials second = saved.ToDraft(); second.BearerToken = "mutated second draft";
        Assert.That(Model(saved.ToDraft()), Is.EqualTo(expected));
        Assert.That(Model(profile.Store.Load(Provider, Endpoint).Credentials), Is.EqualTo(expected));
        Assert.That(saved.ToString(), Does.Not.Contain(Secret)); Assert.That(profile.Store.ToString(), Does.Not.Contain(Secret));
        profile.AssertNoTemporaryFiles();
    }

    [Test]
    public void FirstExplicitGuestChoiceIsRememberedAndSubsequentEqualSaveIsAnExactNoOp()
    {
        using Profile profile = new(); BuildServerCredentialsSnapshot baseline = profile.Store.Load(Provider, Endpoint);
        BuildServerCredentialsSnapshot saved = profile.Store.SaveChanges(baseline, baseline.Credentials);
        Assert.That(saved.Exists, Is.True); Assert.That(profile.Store.Load(Provider, Endpoint).Exists, Is.True);
        AssertExactNoOp(profile, saved, saved.Credentials);
    }

    [TestCase(false)] [TestCase(true)]
    public void AbandonedDraftAndNullCancelHaveNoFileOrInputSideEffectsEvenAfterAnExternalEdit(bool existing)
    {
        using Profile profile = new();
        if (existing) { profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.BearerToken)); }
        BuildServerCredentialsSnapshot baseline = profile.Store.Load(Provider, Endpoint);
        BuildServerCredentials abandoned = baseline.Credentials; abandoned.Password = "cancelled-" + Secret;
        if (existing) { profile.Write("{\"external\":\"keep exact invalid-for-next-read\",\"Providers\":null}\n"); }
        byte[]? bytes = existing ? File.ReadAllBytes(profile.Store.FilePath) : null;
        DateTime? modified = existing ? File.GetLastWriteTimeUtc(profile.Store.FilePath) : null;
        Assert.That(profile.Store.SaveChanges(baseline, null), Is.SameAs(baseline));
        Assert.That(baseline.Credentials.Password, Is.Not.EqualTo(abandoned.Password));
        if (!existing) { Assert.That(Directory.Exists(profile.Root), Is.False); }
        else
        {
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(profile.Store.FilePath), Is.EqualTo(modified)); profile.AssertNoTemporaryFiles();
        }
    }

    [Test]
    public void UnchangedSparseRawDocumentKeepsExactUnknownBytesAndTimestamp()
    {
        using Profile profile = new();
        profile.Write(" { \"future\" : [1, true, null], \"Providers\" : { \"TeamCity\" : { \"https://ci.example.invalid/teamcity/\" : { \"future\" : {\"k\":\"v\"} } } } } \n");
        BuildServerCredentialsSnapshot baseline = profile.Store.Load(Provider, Endpoint);
        Assert.That(baseline.Exists, Is.True); AssertExactNoOp(profile, baseline, baseline.Credentials);
    }

    [Test]
    public void SameChangeAlreadySavedByAnotherEditorIsAnExactNoOpRatherThanAConflictOrRewrite()
    {
        using Profile profile = new();
        BuildServerCredentialsSnapshot stale = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.Guest));
        BuildServerCredentials next = stale.Credentials; next.Password = "new-" + Secret;
        profile.Store.SaveChanges(stale, next);
        AssertExactNoOp(profile, stale, next);
    }

    [TestCase("https://CI.EXAMPLE.INVALID:443/teamcity", Endpoint)]
    [TestCase("http://CI.EXAMPLE.INVALID:80/teamcity/", "http://ci.example.invalid/teamcity/")]
    [TestCase("https://ci.example.invalid/", "https://ci.example.invalid")]
    public void EquivalentOriginAndBasePathSpellingsReachTheSameExplicitIdentity(string first, string equivalent)
    {
        using Profile profile = new();
        profile.Store.SaveChanges(profile.Store.Load(Provider, first), Credentials(BuildServerCredentialsType.BearerToken));
        BuildServerCredentialsSnapshot loaded = profile.Store.Load(Provider, equivalent);
        Assert.That(loaded.Exists, Is.True); Assert.That(loaded.Credentials.BearerToken, Is.EqualTo("token-" + Secret));
        Assert.That(loaded.Endpoint, Is.EqualTo(BuildServerCredentialsStore.NormalizeEndpoint(first)));
    }

    [TestCase("https://other.example.invalid/teamcity/")]
    [TestCase("http://ci.example.invalid/teamcity/")]
    [TestCase("https://ci.example.invalid:8443/teamcity/")]
    [TestCase("https://ci.example.invalid/other/")]
    [TestCase("https://ci.example.invalid/teamcity/child/")]
    [TestCase("https://ci.example.invalid/TeamCity/")]
    [TestCase("https://ci.example.invalid/teamcity//")]
    public void DifferentOriginPortTransportOrInstanceBasePathNeverInheritsAnotherInstancesCredentials(string other)
    {
        using Profile profile = new();
        profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.BearerToken));
        BuildServerCredentialsSnapshot missing = profile.Store.Load(Provider, other);
        Assert.That(missing.Exists, Is.False); Assert.That(missing.Credentials.BearerToken, Is.Null);
        Assert.That(profile.Store.Load("Jenkins", Endpoint).Exists, Is.False, "The provider is part of the credential identity.");
        Assert.That(profile.Store.Load("teamcity", Endpoint).Exists, Is.False, "Provider identities are explicit and ordinal.");
        Assert.That(profile.Store.Load(Provider, Endpoint).Credentials.BearerToken, Is.EqualTo("token-" + Secret));
    }

    [Test]
    public void NullAndEmptyFieldsAndInactiveModesArePreservedWithoutWritingHostingOrDistributedSettings()
    {
        using Profile profile = new();
        string preferences = Path.Combine(profile.Root, "gitextensions", "preferences.json"), distributed = Path.Combine(profile.Root, "repo", "GitExtensions.settings");
        Directory.CreateDirectory(Path.GetDirectoryName(preferences)!); Directory.CreateDirectory(Path.GetDirectoryName(distributed)!);
        File.WriteAllText(preferences, "{\"PluginSettings\":{\"future\":{\"keep\":\"hosting\"}}}\n");
        File.WriteAllText(distributed, "<dictionary><item key=\"BuildServer.Type\" value=\"TeamCity\" /></dictionary>\n");
        byte[] oldPreferences = File.ReadAllBytes(preferences), oldXml = File.ReadAllBytes(distributed);
        BuildServerCredentials draft = new() { BuildServerCredentialsType = BuildServerCredentialsType.Guest, Username = "", Password = null, BearerToken = Secret };
        BuildServerCredentialsSnapshot first = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), draft);
        Assert.That(Model(first.Credentials), Is.EqualTo(Model(draft)));
        BuildServerCredentials changed = first.Credentials; changed.BuildServerCredentialsType = BuildServerCredentialsType.BearerToken;
        BuildServerCredentialsSnapshot saved = profile.Store.SaveChanges(first, changed);
        Assert.That(Model(saved.Credentials), Is.EqualTo(Model(changed)));
        Assert.That(File.ReadAllBytes(preferences), Is.EqualTo(oldPreferences)); Assert.That(File.ReadAllBytes(distributed), Is.EqualTo(oldXml));
        Assert.That(File.ReadAllText(preferences), Does.Not.Contain(Secret)); Assert.That(File.ReadAllText(distributed), Does.Not.Contain(Secret));
    }

    [Test]
    public void ActualSavePreservesUnknownRootProviderEndpointAndEntryProperties()
    {
        using Profile profile = new();
        profile.Write("""
            {"Future":{"opaque":[1,true,null,{"literal":"Café Ω"}]},"Providers":{
              "Other":{"https://other.invalid/":{"Future":{"keep":42}}},
              "TeamCity":{"https://ci.example.invalid/other/":{"Password":"other-value","future":[1,2]},
                "https://ci.example.invalid/teamcity/":{"BuildServerCredentialsType":"Guest","Username":null,"future":{"nested":[false,"literal"]}}}}}
            """);
        using JsonDocument before = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        BuildServerCredentialsSnapshot baseline = profile.Store.Load(Provider, Endpoint); BuildServerCredentials draft = baseline.Credentials; draft.Password = Secret;
        profile.Store.SaveChanges(baseline, draft);
        using JsonDocument after = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        Assert.That(JsonElement.DeepEquals(before.RootElement.GetProperty("Future"), after.RootElement.GetProperty("Future")), Is.True);
        JsonElement beforeProviders = before.RootElement.GetProperty("Providers"), afterProviders = after.RootElement.GetProperty("Providers");
        Assert.That(JsonElement.DeepEquals(beforeProviders.GetProperty("Other"), afterProviders.GetProperty("Other")), Is.True);
        Assert.That(JsonElement.DeepEquals(beforeProviders.GetProperty(Provider).GetProperty("https://ci.example.invalid/other/"),
            afterProviders.GetProperty(Provider).GetProperty("https://ci.example.invalid/other/")), Is.True);
        Assert.That(JsonElement.DeepEquals(beforeProviders.GetProperty(Provider).GetProperty(Endpoint).GetProperty("future"),
            afterProviders.GetProperty(Provider).GetProperty(Endpoint).GetProperty("future")), Is.True);
        Assert.That(profile.Store.Load(Provider, Endpoint).Credentials.Username, Is.Null); profile.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task ConcurrentEditorsOfDifferentFieldsMergeWithoutDroppingEitherSavedValue()
    {
        using Profile profile = new(); BuildServerCredentialsStore other = new(profile.Root);
        BuildServerCredentialsSnapshot first = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.Guest));
        BuildServerCredentialsSnapshot second = other.Load(Provider, Endpoint);
        BuildServerCredentials a = first.Credentials, b = second.Credentials; a.Username = "new-user"; b.Password = "new-password-" + Secret;
        await Concurrent(() => profile.Store.SaveChanges(first, a), () => other.SaveChanges(second, b));
        BuildServerCredentials saved = profile.Store.Load(Provider, Endpoint).Credentials;
        Assert.That(saved.Username, Is.EqualTo(a.Username)); Assert.That(saved.Password, Is.EqualTo(b.Password)); Assert.That(saved.BearerToken, Is.EqualTo(first.Credentials.BearerToken));
        profile.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task ConcurrentFirstSavesOfDifferentInstancesAndProvidersKeepAllThreeEntries()
    {
        using Profile profile = new(); BuildServerCredentialsStore other = new(profile.Root);
        const string secondEndpoint = "https://ci.example.invalid/second/";
        BuildServerCredentialsSnapshot first = profile.Store.Load(Provider, Endpoint), second = other.Load(Provider, secondEndpoint), third = other.Load("OtherCI", Endpoint);
        await Concurrent(() => profile.Store.SaveChanges(first, Credentials(BuildServerCredentialsType.Guest)),
            () => other.SaveChanges(second, Credentials(BuildServerCredentialsType.BearerToken)),
            () => other.SaveChanges(third, Credentials(BuildServerCredentialsType.UsernameAndPassword)));
        Assert.That(profile.Store.Load(Provider, Endpoint).Credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.Guest));
        Assert.That(profile.Store.Load(Provider, secondEndpoint).Credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.BearerToken));
        Assert.That(profile.Store.Load("OtherCI", Endpoint).Credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.UsernameAndPassword));
        profile.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task ConcurrentEditorsOfTheSameSecretHaveOneWinnerAndARedactedConflict()
    {
        using Profile profile = new(); BuildServerCredentialsStore other = new(profile.Root);
        BuildServerCredentialsSnapshot first = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.Guest));
        BuildServerCredentialsSnapshot second = other.Load(Provider, Endpoint);
        BuildServerCredentials a = first.Credentials, b = second.Credentials; a.Password = "first-" + Secret; b.Password = "second-" + Secret;
        using ManualResetEventSlim start = new(false);
        Task<Exception?> saveA = Task.Run(() => Save(profile.Store, first, a)), saveB = Task.Run(() => Save(other, second, b));
        start.Set(); Exception?[] results = await Task.WhenAll(saveA, saveB).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(results.Count(error => error is null), Is.EqualTo(1)); Exception conflict = results.Single(error => error is not null)!;
        Assert.That(conflict, Is.TypeOf<InvalidOperationException>()); Assert.That(conflict.Message, Does.Contain("another window")); AssertSecretFree(conflict);
        Assert.That(profile.Store.Load(Provider, Endpoint).Credentials.Password, Is.EqualTo(results[0] is null ? a.Password : b.Password)); profile.AssertNoTemporaryFiles();
        Exception? Save(BuildServerCredentialsStore store, BuildServerCredentialsSnapshot baseline, BuildServerCredentials draft)
        { start.Wait(); try { store.SaveChanges(baseline, draft); return null; } catch (Exception error) { return error; } }
    }

    [Test]
    public void ExternallyDeletedCredentialsAreNotSilentlyRecreatedByAStaleChangedDraft()
    {
        using Profile profile = new();
        BuildServerCredentialsSnapshot baseline = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.BearerToken));
        BuildServerCredentials changed = baseline.Credentials; changed.Password = Secret + "-edited";
        profile.Write("{\"future\":\"external replacement\",\"Providers\":{}}\n"); byte[] bytes = File.ReadAllBytes(profile.Store.FilePath);
        Exception? error = Assert.Throws<InvalidOperationException>(() => profile.Store.SaveChanges(baseline, changed)); AssertSecretFree(error!);
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes)); profile.AssertNoTemporaryFiles();
    }

    [Test]
    public void AnExternalRawEditOfAnotherFieldAndUnknownPropertiesSurvivesTheStaleEditorSave()
    {
        using Profile profile = new();
        BuildServerCredentialsSnapshot baseline = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.Guest));
        JsonObject external = JsonNode.Parse(File.ReadAllBytes(profile.Store.FilePath))!.AsObject();
        external["Providers"]![Provider]![Endpoint]!["Username"] = "external-user";
        external["Future"] = new JsonObject { ["opaque"] = "external-literal" };
        profile.Write(external.ToJsonString());
        BuildServerCredentials draft = baseline.Credentials; draft.Password = "local-" + Secret;
        BuildServerCredentialsSnapshot saved = profile.Store.SaveChanges(baseline, draft);
        Assert.That(saved.Credentials.Username, Is.EqualTo("external-user")); Assert.That(saved.Credentials.Password, Is.EqualTo(draft.Password));
        using JsonDocument actual = JsonDocument.Parse(File.ReadAllBytes(profile.Store.FilePath));
        Assert.That(actual.RootElement.GetProperty("Future").GetProperty("opaque").GetString(), Is.EqualTo("external-literal")); profile.AssertNoTemporaryFiles();
    }

    [Test]
    public void AFirstGuestAcceptanceDoesNotOverwriteANewlySavedAuthenticatedMode()
    {
        using Profile profile = new();
        BuildServerCredentialsSnapshot absentGuest = profile.Store.Load(Provider, Endpoint);
        profile.Store.SaveChanges(absentGuest, Credentials(BuildServerCredentialsType.BearerToken));
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath);
        Exception? error = Assert.Throws<InvalidOperationException>(() => profile.Store.SaveChanges(absentGuest, absentGuest.Credentials)); AssertSecretFree(error!);
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes)); profile.AssertNoTemporaryFiles();
    }

    [TestCase("{broken-" + Secret)]
    [TestCase("[]")]
    [TestCase("{\"Providers\":null}")]
    [TestCase("{\"Providers\":{\"TeamCity\":null}}")]
    [TestCase("{\"Providers\":{\"TeamCity\":{\"https://ci.example.invalid/teamcity/\":null}}}")]
    [TestCase("{\"Providers\":{\"TeamCity\":{\"https://ci.example.invalid/teamcity/\":{\"Password\":42}}}}")]
    [TestCase("{\"Providers\":{\"TeamCity\":{\"https://ci.example.invalid/teamcity/\":{\"BuildServerCredentialsType\":\"999\"}}}}")]
    [TestCase("{\"Providers\":{},\"Providers\":{}}")]
    [TestCase("{\"Providers\":{\"TeamCity\":{\"https://ci.example.invalid/teamcity/\":{\"Password\":\"" + Secret + "\",\"Password\":\"other\"}}}}")]
    [TestCase("{\"future\":[{\"" + Secret + "\":1,\"" + Secret + "\":2}]}")]
    public void CorruptDuplicateOrInvalidKnownJsonIsRefusedWithoutRewritingOrLeakingPayload(string contents)
    {
        using Profile profile = new(); BuildServerCredentialsSnapshot baseline = profile.Store.Load(Provider, Endpoint);
        profile.Write(contents); byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); DateTime modified = File.GetLastWriteTimeUtc(profile.Store.FilePath);
        Exception? read = Assert.Throws<JsonException>(() => profile.Store.Load(Provider, Endpoint)); AssertSecretFree(read!);
        Exception? write = Assert.Throws<JsonException>(() => profile.Store.SaveChanges(baseline, Credentials(BuildServerCredentialsType.BearerToken))); AssertSecretFree(write!);
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes)); Assert.That(File.GetLastWriteTimeUtc(profile.Store.FilePath), Is.EqualTo(modified));
        profile.AssertNoTemporaryFiles();
    }

    [TestCase("https://user:" + Secret + "@ci.example.invalid/teamcity/")]
    [TestCase("https://ci.example.invalid/teamcity/?token=" + Secret)]
    [TestCase("https://ci.example.invalid/teamcity/#" + Secret)]
    [TestCase("file:///" + Secret)]
    [TestCase("not-an-endpoint-" + Secret)]
    public void InvalidEndpointIsRejectedBeforeAnyProfileAccessWithARedactedError(string endpoint)
    {
        using Profile profile = new();
        Exception? error = Assert.Throws<ArgumentException>(() => profile.Store.Load(Provider, endpoint)); AssertSecretFree(error!);
        Assert.That(Directory.Exists(profile.Root), Is.False);
    }

    [Test]
    public void InvalidModeAndCrossProfileBaselinesAreRejectedWithoutCreatingFilesOrLeakingDrafts()
    {
        using Profile first = new(); using Profile other = new();
        BuildServerCredentialsSnapshot baseline = first.Store.Load(Provider, Endpoint);
        BuildServerCredentials invalid = Credentials((BuildServerCredentialsType)999);
        Exception? mode = Assert.Throws<ArgumentException>(() => first.Store.SaveChanges(baseline, invalid)); AssertSecretFree(mode!);
        Exception? identity = Assert.Throws<ArgumentException>(() => other.Store.SaveChanges(baseline, Credentials(BuildServerCredentialsType.BearerToken))); AssertSecretFree(identity!);
        Assert.That(Directory.Exists(first.Root) || Directory.Exists(other.Root), Is.False);
    }

    [Test, Platform("Linux")]
    public void NewFilesAndReplacementWithCredentialsHaveOnlyUserReadWritePermissions()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux file-mode contract."); return; }
        using Profile profile = new();
        BuildServerCredentialsSnapshot baseline = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.BearerToken));
        UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(privateMode));
        File.SetUnixFileMode(profile.Store.FilePath, privateMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        BuildServerCredentials next = baseline.Credentials; next.Password = Secret + "-changed";
        profile.Store.SaveChanges(baseline, next); Assert.That(File.GetUnixFileMode(profile.Store.FilePath), Is.EqualTo(privateMode)); profile.AssertNoTemporaryFiles();
    }

    [Test]
    public void AnOccupiedPublicationLockRefusesOverwriteAndNeverDeletesAnotherWritersLock()
    {
        using Profile profile = new();
        BuildServerCredentialsSnapshot baseline = profile.Store.SaveChanges(profile.Store.Load(Provider, Endpoint), Credentials(BuildServerCredentialsType.Guest));
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); string lockPath = profile.Store.FilePath + ".lock";
        File.WriteAllText(lockPath, Secret);
        BuildServerCredentials next = baseline.Credentials; next.Password = Secret + "-next";
        Exception? error = Assert.Throws<IOException>(() => profile.Store.SaveChanges(baseline, next)); AssertSecretFree(error!);
        Assert.That(File.ReadAllText(lockPath), Is.EqualTo(Secret)); Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.Store.FilePath)!, "*.tmp"), Is.Empty);
    }

    private static BuildServerCredentials Credentials(BuildServerCredentialsType mode) => new()
    { BuildServerCredentialsType = mode, Username = " user Ω ", Password = " password-" + Secret + " ", BearerToken = "token-" + Secret };
    private static (BuildServerCredentialsType, string?, string?, string?) Model(IBuildServerCredentials credentials)
        => (credentials.BuildServerCredentialsType, credentials.Username, credentials.Password, credentials.BearerToken);
    private static void AssertSecretFree(Exception error)
    { Assert.That(error.ToString(), Does.Not.Contain(Secret)); Assert.That(error.InnerException, Is.Null); }
    private static void AssertExactNoOp(Profile profile, BuildServerCredentialsSnapshot baseline, BuildServerCredentials draft)
    {
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); DateTime modified = File.GetLastWriteTimeUtc(profile.Store.FilePath);
        profile.Store.SaveChanges(baseline, draft);
        Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes)); Assert.That(File.GetLastWriteTimeUtc(profile.Store.FilePath), Is.EqualTo(modified)); profile.AssertNoTemporaryFiles();
    }
    private static async Task Concurrent(params Action[] actions)
    {
        using ManualResetEventSlim start = new(false);
        Task[] tasks = actions.Select(action => Task.Run(() => { start.Wait(); action(); })).ToArray();
        start.Set(); await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }
    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-ci-credentials-store-" + Guid.NewGuid().ToString("N"));
        internal BuildServerCredentialsStore Store { get; }
        internal Profile() => Store = new(Root);
        internal void Write(string value)
        { Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!); File.WriteAllText(Store.FilePath, value, new UTF8Encoding(false)); }
        internal void AssertNoTemporaryFiles()
            => Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(Store.FilePath)!), Is.EqualTo(new[] { Store.FilePath }));
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
