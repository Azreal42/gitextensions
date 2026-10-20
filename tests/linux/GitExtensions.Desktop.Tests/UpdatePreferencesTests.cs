using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class UpdatePreferencesTests
{
    [Test]
    public void MissingKeysUseTheWindowsWeeklyAndReleaseCandidateDefaultsWithoutCreatingAProfile()
    {
        using Scope f = new(); DesktopPreferences desktop = f.Store.Load(); AdvancedSettingsSession session = f.Session();
        Assert.That(desktop.CheckForUpdates, Is.True); Assert.That(desktop.CheckForReleaseCandidates, Is.False);
        Assert.That(session.Initial.CheckForUpdates, Is.True); Assert.That(session.Initial.CheckForReleaseCandidates, Is.False);
        session.Save(session.Initial); Assert.That(Directory.Exists(f.Directory), Is.False);
    }

    [TestCase("CheckForUpdates", false)]
    [TestCase("CheckForReleaseCandidates", true)]
    public void AChangedBooleanWritesOnlyItsDesktopKeyAndPreservesForeignData(string name, bool desired)
    {
        using Scope f = new(); f.Write("{\"future\":{\"literal\":[17,null,\"Ω\"]},\"peer\":false}");
        AdvancedSettingsSession session = f.Session(); List<AdvancedSettingsSaveReceipt> receipts = [];
        session.Save(session.Initial.With(name, desired), receipts.Add);
        Assert.That(f.Read()[name]!.GetValue<bool>(), Is.EqualTo(desired));
        Assert.That(f.Read().Select(item => item.Key), Is.EquivalentTo(new[] { name, "future", "peer" }));
        Assert.That(JsonNode.DeepEquals(f.Read()["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
        Assert.That(receipts.Single().Profile, Is.EqualTo("preferences.json")); Assert.That(receipts.Single().Written, Is.True);
        Assert.That(receipts.Single().Fields.Select(setting => setting.Name), Is.EqualTo(new[] { name }));
        Assert.That(f.Session().Initial.Get(name), Is.EqualTo(desired));
    }

    [Test]
    public void BothFlagsHaveOneDurableReceiptAndOnlyAcknowledgedFlagsUpdateTheLiveDesktop()
    {
        using Scope f = new(); f.Write("{\"CheckForUpdates\":true,\"CheckForReleaseCandidates\":false,\"Theme\":\"Dark\",\"future\":[null,17]}");
        AdvancedSettingsSession session = f.Session(); List<AdvancedSettingsSaveReceipt> receipts = [];
        AdvancedSettingsValues desired = session.Initial with { CheckForUpdates = false, CheckForReleaseCandidates = true };
        session.Save(desired, receipts.Add); AdvancedSettingsSaveReceipt receipt = receipts.Single();
        Assert.That(receipt.Profile, Is.EqualTo("preferences.json")); Assert.That(receipt.Written, Is.True);
        Assert.That(receipt.Fields.Select(setting => setting.Name), Is.EqualTo(new[] { "CheckForUpdates", "CheckForReleaseCandidates" }));
        DesktopPreferences live = AdvancedSettingsValues.ApplyDesktop(new() { HistoryLimit = 123 }, receipt.Values, receipt.Fields);
        Assert.That(live.CheckForUpdates, Is.False); Assert.That(live.CheckForReleaseCandidates, Is.True); Assert.That(live.HistoryLimit, Is.EqualTo(123));
        AdvancedSetting weeklyOnly = receipt.Fields.Single(setting => setting.Name == "CheckForUpdates");
        DesktopPreferences partial = AdvancedSettingsValues.ApplyDesktop(new(), receipt.Values, [weeklyOnly]);
        Assert.That(partial.CheckForUpdates, Is.False); Assert.That(partial.CheckForReleaseCandidates, Is.False, "An unacknowledged draft must not leak into the active desktop.");
        Assert.That(f.Store.Load().CheckForUpdates, Is.False); Assert.That(f.Store.Load().CheckForReleaseCandidates, Is.True);
        Assert.That(f.Store.Load().Theme, Is.EqualTo("Dark")); Assert.That(JsonNode.DeepEquals(f.Read()["future"], JsonNode.Parse("[null,17]")), Is.True);
    }

    [Test]
    public void GeneralPublicationObservesPeerUpdateFlagsWithoutRebasingAnAdvancedDraft()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session();
        f.Write("{\"CheckForUpdates\":false,\"CheckForReleaseCandidates\":true,\"future\":[17,null]}");
        DesktopPreferences merged = session.MergeDesktop(new() { Theme = "Dark", HistoryLimit = 123 });
        Assert.That(merged.CheckForUpdates, Is.False); Assert.That(merged.CheckForReleaseCandidates, Is.True);
        Assert.That(merged.Theme, Is.EqualTo("Dark")); Assert.That(merged.HistoryLimit, Is.EqualTo(123));
        Assert.That(session.Initial.CheckForUpdates, Is.True); Assert.That(session.Initial.CheckForReleaseCandidates, Is.False);
        f.Store.Save(merged); Assert.That(JsonNode.DeepEquals(f.Read()["future"], JsonNode.Parse("[17,null]")), Is.True);
        Assert.That(f.Store.Load().CheckForUpdates, Is.False); Assert.That(f.Store.Load().CheckForReleaseCandidates, Is.True);
    }

    [TestCase("CheckForUpdates", false, true)]
    [TestCase("CheckForReleaseCandidates", true, false)]
    public void ActiveValuesAreVisibleWithoutWritingAndDoNotReplaceTheDurableConflictBaseline(string name, bool active, bool desired)
    {
        using Scope f = new(); f.Write(new JsonObject { [name] = desired, ["future"] = 17 }.ToJsonString());
        DesktopPreferences desktop = name == "CheckForUpdates" ? new() { CheckForUpdates = active } : new() { CheckForReleaseCandidates = active };
        AdvancedSettingsSession session = new(f.Directory, desktop); Assert.That(session.Initial.Get(name), Is.EqualTo(active));
        byte[] before = f.Bytes; session.Save(session.Initial); Assert.That(f.Bytes, Is.EqualTo(before));
        f.Write(new JsonObject { [name] = active, ["future"] = 18 }.ToJsonString()); before = f.Bytes;
        Assert.Throws<IOException>(() => session.Save(session.Initial.With(name, desired)));
        Assert.That(f.Bytes, Is.EqualTo(before)); Assert.That(session.Initial.Get(name), Is.EqualTo(active));
    }

    [TestCase("CheckForUpdates", false)]
    [TestCase("CheckForReleaseCandidates", true)]
    public void AnUnreadableBooleanNeverGetsRepairedByAnAdvancedEditEvenAfterTheFileIsReplaced(string name, bool desired)
    {
        using Scope f = new(); f.Write(new JsonObject { [name] = "invalid boolean", ["future"] = 17 }.ToJsonString());
        AdvancedSettingsSession session = f.Session(); Assert.That(session.LoadErrors.Keys, Does.Contain("preferences.json"));
        session.Save(session.Initial with { AlwaysShowCheckoutDialog = true });
        Assert.That(f.Read()[name]!.GetValue<string>(), Is.EqualTo("invalid boolean"));
        f.Write("{ \"future\": [17,null] }\n"); byte[] before = f.Bytes;
        Assert.Throws<InvalidDataException>(() => session.Save(session.Initial.With(name, desired)));
        Assert.That(f.Bytes, Is.EqualTo(before)); Assert.That(f.Session().LoadErrors, Is.Empty);
    }

    [Test]
    public void AlreadyPublishedBooleanPairIsAcknowledgedWithoutRewriteAndCannotBeReplayedOverALaterPeer()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session();
        AdvancedSettingsValues desired = session.Initial with { CheckForUpdates = false, CheckForReleaseCandidates = true };
        f.Write("{ \"CheckForUpdates\": false, \"CheckForReleaseCandidates\": true, \"future\": 17 }\n"); byte[] before = f.Bytes;
        List<AdvancedSettingsSaveReceipt> receipts = []; session.Save(desired, receipts.Add);
        Assert.That(receipts.Single().Written, Is.False); Assert.That(receipts.Single().Fields.Count, Is.EqualTo(2)); Assert.That(f.Bytes, Is.EqualTo(before));
        f.Write("{ \"CheckForUpdates\": true, \"CheckForReleaseCandidates\": false, \"future\": 18 }\n"); before = f.Bytes;
        session.Save(desired, receipts.Add); Assert.That(receipts.Count, Is.EqualTo(1)); Assert.That(f.Bytes, Is.EqualTo(before));
    }

    [Test]
    public void CancellationBeforePublishingBothFlagsCreatesNothingAndKeepsTheOriginalBaseline()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session(); using CancellationTokenSource stop = new(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Save(session.Initial with { CheckForUpdates = false, CheckForReleaseCandidates = true }, cancellationToken: stop.Token));
        Assert.That(Directory.Exists(f.Directory), Is.False); Assert.That(session.Initial.CheckForUpdates, Is.True); Assert.That(session.Initial.CheckForReleaseCandidates, Is.False);
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task RealUpdateChoicesCancelApplyAndReopenWithoutImplicitPublication(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark); PreferencesStore store = new();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"CheckForUpdates\":true,\"CheckForReleaseCandidates\":false,\"future\":[null,17,\"Ω\"]}");
        byte[] before = File.ReadAllBytes(store.FilePath);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced", initial: store.Load());
        Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkCheckForUpdates").IsChecked, Is.True);
        Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkCheckForRCVersions").IsChecked, Is.False);
        Assert.That(ToolTip.GetTip(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkCheckForRCVersions")),
            Is.EqualTo(f.Original("AdvancedSettingsPage", "chkCheckForRCVersions.LinuxToolTipText")));
        Choose(settings, false, true); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before));
        PortableHelperSettingsFixture.Capture(settings, "updates-preferences-" + language + (dark ? "-dark.png" : "-light.png"));
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before)); Assert.That(f.Publications, Is.Zero);
        (settings, completion) = f.Open("Advanced", initial: store.Load()); Choose(settings, false, true);
        await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        Assert.That(store.Load().CheckForUpdates, Is.False); Assert.That(store.Load().CheckForReleaseCandidates, Is.True);
        Assert.That(settings.Result.CheckForUpdates, Is.False); Assert.That(settings.Result.CheckForReleaseCandidates, Is.True);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"], JsonNode.Parse("[null,17,\"Ω\"]")), Is.True);
        byte[] saved = File.ReadAllBytes(store.FilePath); Choose(settings, true, false);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved));
        (settings, completion) = f.Open("Advanced", initial: store.Load());
        Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkCheckForUpdates").IsChecked, Is.False);
        Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkCheckForRCVersions").IsChecked, Is.True);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved)); Assert.That(f.Publications, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task GeneralTypedWriteThenAdvancedReceiptPreservesBothUpdateChoicesAndForeignKeys()
    {
        using PortableHelperSettingsFixture f = new(); PreferencesStore store = new();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"CheckForUpdates\":true,\"CheckForReleaseCandidates\":false,\"future\":{\"literal\":[17,null,\"Ω\"]}}");
        AdvancedSettingsSession advanced = new(initialDesktop: store.Load()); int publications = 0;
        DesktopSettingsDialog settings = new(store.Load(), f.Translations, false, fileHistoryPreferences: new(), hotkeys: new(), fonts: new(), scripts: f.Scripts.Load(),
            publishSettings: dialog =>
            {
                publications++; DesktopPreferences general = advanced.MergeDesktop(dialog.Result);
                Assert.That(general.CheckForUpdates, Is.True); Assert.That(general.CheckForReleaseCandidates, Is.False);
                store.Save(general); return Task.CompletedTask;
            }, initialPage: "General", advancedSettings: advanced);
        f.Track(settings); Task<bool> completion = settings.ShowDialog<bool>(f.Owner); PortableHelperSettingsFixture.Pump(settings);
        AdvancedSettingsUiDriver.Set(settings, "chkStartWithRecentWorkingDir", true);
        SettingsNavigationTestDriver.Select(settings, "Advanced"); Choose(settings, false, true);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(AdvancedSettingsUiDriver.Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
        Assert.That(store.Load().StartWithRecentWorkingDir, Is.True); Assert.That(store.Load().CheckForUpdates, Is.False); Assert.That(store.Load().CheckForReleaseCandidates, Is.True);
        Assert.That(settings.Result.CheckForUpdates, Is.False); Assert.That(settings.Result.CheckForReleaseCandidates, Is.True);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
        byte[] saved = File.ReadAllBytes(store.FilePath);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved));
    }

    private static void Choose(Window settings, bool weekly, bool releaseCandidates)
    {
        AdvancedSettingsUiDriver.Set(settings, "chkCheckForUpdates", weekly);
        AdvancedSettingsUiDriver.Set(settings, "chkCheckForRCVersions", releaseCandidates);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "update-preferences-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.GetDirectoryName(Store.FilePath)!;
        internal PreferencesStore Store => new(_root);
        internal byte[] Bytes => File.ReadAllBytes(Store.FilePath);
        internal void Write(string text) { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Store.FilePath, text); }
        internal JsonObject Read() => JsonNode.Parse(Bytes)!.AsObject();
        internal AdvancedSettingsSession Session() => new(Directory);
        public void Dispose() { if (System.IO.Directory.Exists(_root)) { System.IO.Directory.Delete(_root, true); } }
    }
}
