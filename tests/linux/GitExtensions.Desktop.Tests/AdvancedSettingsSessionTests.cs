using System.Text.Json;
using System.Text.Json.Nodes;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AdvancedSettingsSessionTests
{
    [TestCase("AlwaysShowCheckoutDialog", "reference-preferences.json")]
    [TestCase("UseDefaultCheckoutChanges", "reference-preferences.json")]
    [TestCase("CommitAndPushForcedWhenAmend", "commit-preferences.json")]
    public void MissingChoiceWritesOnlyItsExistingAuthorityAndKeepsForeignKeys(string name, string profile)
    {
        using Scope f = new(); f.Write(profile, "{\"foreign\":{\"literal\":[null,17,\"Ω\"]},\"peer\":false}");
        AdvancedSettingsSession session = f.Session(); List<AdvancedSettingsSaveReceipt> receipts = [];
        session.Save(session.Initial.With(name, true), receipts.Add);
        JsonObject document = f.Read(profile); Assert.That(document[name]!.GetValue<bool>(), Is.True);
        Assert.That(document.Select(item => item.Key), Is.EquivalentTo(new[] { name, "foreign", "peer" }));
        Assert.That(JsonNode.DeepEquals(document["foreign"], JsonNode.Parse("{\"literal\":[null,17,\"Ω\"]}")), Is.True);
        Assert.That(receipts.Single().Fields.Select(setting => setting.Name), Is.EqualTo(new[] { name }));
        Assert.That(receipts.Single().Written, Is.True); Assert.That(f.Session().Initial.Get(name), Is.EqualTo(true));
    }

    [TestCase("_")] [TestCase("-")] [TestCase("")]
    public void EveryOriginalSymbolIncludingNoneRoundTripsLiterally(string symbol)
    {
        using Scope f = new(); f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\".\",\"peer\":17}");
        AdvancedSettingsSession session = f.Session(); session.Save(session.Initial with { AutoNormaliseSymbol = symbol });
        Assert.That(f.Read("preferences.json")["AutoNormaliseSymbol"]!.GetValue<string>(), Is.EqualTo(symbol));
        Assert.That(f.Session().Initial.AutoNormaliseSymbol, Is.EqualTo(symbol)); Assert.That(f.Read("preferences.json")["peer"]!.GetValue<int>(), Is.EqualTo(17));
    }

    [Test]
    public void NoOpCreatesNothingAndDoesNotRepairAProfileThatBecameCorrupt()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session(); session.Save(session.Initial);
        Assert.That(Directory.Exists(f.Directory), Is.False);
        f.Write("reference-preferences.json", "{invalid literal"); byte[] raw = File.ReadAllBytes(f.PathFor("reference-preferences.json"));
        session.Save(session.Initial); Assert.That(File.ReadAllBytes(f.PathFor("reference-preferences.json")), Is.EqualTo(raw));
    }

    [Test]
    public void AForeignUneditedSymbolAndPeerWorkflowChoicesRemainIntact()
    {
        using Scope f = new(); f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\"foreign-token\"}");
        f.Write("reference-preferences.json", "{\"CheckoutChanges\":\"Stash\",\"ReapplyStash\":true,\"RemoteCheckoutMode\":\"DontCreate\"}");
        AdvancedSettingsSession session = f.Session();
        session.Save(session.Initial with { AlwaysShowAdvancedOptions = true, UseDefaultCheckoutChanges = true });
        Assert.That(f.Read("preferences.json")["AutoNormaliseSymbol"]!.GetValue<string>(), Is.EqualTo("foreign-token"));
        Assert.That(f.Read("reference-preferences.json")["CheckoutChanges"]!.GetValue<string>(), Is.EqualTo("Stash"));
        Assert.That(f.Read("reference-preferences.json")["ReapplyStash"]!.GetValue<bool>(), Is.True);
        Assert.That(f.Read("reference-preferences.json")["RemoteCheckoutMode"]!.GetValue<string>(), Is.EqualTo("DontCreate"));
    }

    [Test]
    public void AThirdSymbolDecisionConflictsInsteadOfSilentlyRebasingTheOpenDraft()
    {
        using Scope f = new(); f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\"_\"}"); AdvancedSettingsSession session = f.Session();
        f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\"\",\"peer\":17}"); byte[] peer = File.ReadAllBytes(f.PathFor("preferences.json"));
        Assert.Throws<IOException>(() => session.Save(session.Initial with { AutoNormaliseSymbol = "-" }));
        Assert.That(File.ReadAllBytes(f.PathFor("preferences.json")), Is.EqualTo(peer)); Assert.That(session.Initial.AutoNormaliseSymbol, Is.EqualTo("_"));
    }

    [Test]
    public void ACompletedProfileIsAcknowledgedBeforeLaterFailureAndRetryCannotReplayItOverAPeer()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session();
        AdvancedSettingsValues draft = session.Initial with { CommitAndPushForcedWhenAmend = true, AlwaysShowCheckoutDialog = true };
        Directory.CreateDirectory(f.PathFor("reference-preferences.json")); List<AdvancedSettingsSaveReceipt> receipts = [];
        Exception? error = Assert.Catch(() => session.Save(draft, receipts.Add));
        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(receipts.Single().Profile, Is.EqualTo("commit-preferences.json")); Assert.That(session.Initial.CommitAndPushForcedWhenAmend, Is.True);
        Assert.That(session.Initial.AlwaysShowCheckoutDialog, Is.False);
        f.Write("commit-preferences.json", "{\"CommitAndPushForcedWhenAmend\":false,\"LastCommitMessage\":\"peer draft\",\"DontConfirmAmend\":true}");
        byte[] peer = File.ReadAllBytes(f.PathFor("commit-preferences.json")); Directory.Delete(f.PathFor("reference-preferences.json"));
        session.Save(draft, receipts.Add);
        Assert.That(File.ReadAllBytes(f.PathFor("commit-preferences.json")), Is.EqualTo(peer));
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { "commit-preferences.json", "reference-preferences.json" }));
        Assert.That(f.Read("reference-preferences.json")["AlwaysShowCheckoutDialog"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public void InitialReadFailureDoesNotBlockAnUnrelatedProfileButNeverInventsAnEditableBaseline()
    {
        using Scope f = new(); f.Write("reference-preferences.json", "{broken"); AdvancedSettingsSession session = f.Session();
        Assert.That(session.LoadErrors.Keys, Does.Contain("reference-preferences.json"));
        session.Save(session.Initial with { CommitAndPushForcedWhenAmend = true });
        Assert.That(File.ReadAllText(f.PathFor("reference-preferences.json")), Is.EqualTo("{broken"));
        f.Write("reference-preferences.json", "{}");
        Assert.Throws<InvalidDataException>(() => session.Save(session.Initial with { AlwaysShowCheckoutDialog = true }));
        Assert.That(File.ReadAllText(f.PathFor("reference-preferences.json")), Is.EqualTo("{}"));
    }

    [Test]
    public void GeneralDesktopPublicationDefersAdvancedIntentionsWithoutRebasingTheirBaseline()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session();
        f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\"-\",\"AlwaysShowAdvancedOptions\":true}");
        DesktopPreferences merged = session.MergeDesktop(new() { AutoNormaliseSymbol = "", AlwaysShowAdvancedOptions = false, HistoryLimit = 123 });
        Assert.That(merged.AutoNormaliseSymbol, Is.EqualTo("-")); Assert.That(merged.AlwaysShowAdvancedOptions, Is.True); Assert.That(merged.HistoryLimit, Is.EqualTo(123));
        Assert.That(session.Initial.AutoNormaliseSymbol, Is.EqualTo("_"));
        Assert.Throws<IOException>(() => session.Save(session.Initial with { AutoNormaliseSymbol = "" }));
    }

    [Test]
    public void AnAlreadyDurableIntentIsAcknowledgedWithoutRewriteAndNotReplayedAfterPartialApply()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session();
        f.Write("commit-preferences.json", "{ \"CommitAndPushForcedWhenAmend\": true, \"peer\": 17 }\n"); byte[] raw = File.ReadAllBytes(f.PathFor("commit-preferences.json"));
        List<AdvancedSettingsSaveReceipt> receipts = []; AdvancedSettingsValues draft = session.Initial with { CommitAndPushForcedWhenAmend = true };
        session.Save(draft, receipts.Add); Assert.That(receipts.Single().Written, Is.False); Assert.That(File.ReadAllBytes(f.PathFor("commit-preferences.json")), Is.EqualTo(raw));
        f.Write("commit-preferences.json", "{\"CommitAndPushForcedWhenAmend\":false,\"peer\":18}"); raw = File.ReadAllBytes(f.PathFor("commit-preferences.json"));
        session.Save(draft); Assert.That(File.ReadAllBytes(f.PathFor("commit-preferences.json")), Is.EqualTo(raw));
    }

    [Test]
    public void CancellationBeforeSaveDoesNotCreateAnyProfileOrConsumeTheDraft()
    {
        using Scope f = new(); AdvancedSettingsSession session = f.Session(); using CancellationTokenSource stop = new(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Save(session.Initial with { CommitAndPushForcedWhenAmend = true }, cancellationToken: stop.Token));
        Assert.That(Directory.Exists(f.Directory), Is.False); Assert.That(session.Initial.CommitAndPushForcedWhenAmend, Is.False);
    }

    [Test]
    public void ANewNonCanonicalSymbolIsRejectedWhileAnUneditedForeignTokenIsPreserved()
    {
        using Scope f = new(); f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\"foreign\"}"); AdvancedSettingsSession session = f.Session();
        session.Save(session.Initial); byte[] before = File.ReadAllBytes(f.PathFor("preferences.json"));
        Assert.Throws<ArgumentException>(() => session.Save(session.Initial with { AutoNormaliseSymbol = "invented" }));
        Assert.That(File.ReadAllBytes(f.PathFor("preferences.json")), Is.EqualTo(before));
    }

    [TestCase("{broken raw")]
    [TestCase("[17,null]")]
    public void ExplicitTypedDesktopSaveKeepsItsHistoricalRepairBehaviorForInvalidOrNonObjectRaw(string raw)
    {
        using Scope f = new(); string config = System.IO.Path.Combine(f.Directory, "config");
        PreferencesStore store = new(config); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, raw); store.Save(new() { HistoryLimit = 123, AutoNormaliseSymbol = "" });
        Assert.That(store.Load().HistoryLimit, Is.EqualTo(123)); Assert.That(store.Load().AutoNormaliseSymbol, Is.Empty);
        Assert.That(JsonNode.Parse(File.ReadAllBytes(store.FilePath)), Is.InstanceOf<JsonObject>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ActiveDesktopValuesAreNotImplicitIntentionsAndCannotReplaceTheDurableSymbolConflictBaseline(bool peerChangedToActiveValue)
    {
        using Scope f = new();
        f.Write("preferences.json", "{\"AutoNormaliseBranchName\":true,\"AutoNormaliseSymbol\":\"_\",\"AlwaysShowAdvancedOptions\":false,\"peer\":17}");
        AdvancedSettingsSession session = new(f.Directory, new() { AutoNormaliseBranchName = false, AutoNormaliseSymbol = "-", AlwaysShowAdvancedOptions = true });
        Assert.That(session.Initial.AutoNormaliseBranchName, Is.False); Assert.That(session.Initial.AutoNormaliseSymbol, Is.EqualTo("-")); Assert.That(session.Initial.AlwaysShowAdvancedOptions, Is.True);
        byte[] original = File.ReadAllBytes(f.PathFor("preferences.json")); List<AdvancedSettingsSaveReceipt> receipts = [];
        session.Save(session.Initial, receipts.Add); Assert.That(receipts, Is.Empty); Assert.That(File.ReadAllBytes(f.PathFor("preferences.json")), Is.EqualTo(original));
        AdvancedSettingsValues draft = session.Initial with { AutoNormaliseSymbol = "" };
        if (peerChangedToActiveValue)
        {
            f.Write("preferences.json", "{\"AutoNormaliseSymbol\":\"-\",\"peer\":18}"); byte[] peer = File.ReadAllBytes(f.PathFor("preferences.json"));
            Assert.Throws<IOException>(() => session.Save(draft, receipts.Add));
            Assert.That(File.ReadAllBytes(f.PathFor("preferences.json")), Is.EqualTo(peer)); Assert.That(receipts, Is.Empty);
            Assert.That(session.Initial.AutoNormaliseSymbol, Is.EqualTo("-"));
        }
        else
        {
            session.Save(draft, receipts.Add);
            Assert.That(receipts.Single().Fields.Select(item => item.Name), Is.EqualTo(new[] { "AutoNormaliseSymbol" }));
            Assert.That(f.Read("preferences.json")["AutoNormaliseSymbol"]!.GetValue<string>(), Is.Empty);
            Assert.That(f.Read("preferences.json")["AutoNormaliseBranchName"]!.GetValue<bool>(), Is.True);
            Assert.That(f.Read("preferences.json")["AlwaysShowAdvancedOptions"]!.GetValue<bool>(), Is.False);
            Assert.That(f.Read("preferences.json")["peer"]!.GetValue<int>(), Is.EqualTo(17));
            Assert.That(session.Initial.AutoNormaliseBranchName, Is.False); Assert.That(session.Initial.AlwaysShowAdvancedOptions, Is.True);
        }
    }

    private sealed class Scope : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "advanced-session-" + Guid.NewGuid().ToString("N"));
        internal string PathFor(string name) => System.IO.Path.Combine(Directory, name);
        internal void Write(string name, string text) { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(PathFor(name), text); }
        internal JsonObject Read(string name) => JsonNode.Parse(File.ReadAllBytes(PathFor(name)))!.AsObject();
        internal AdvancedSettingsSession Session() => new(Directory);
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); } }
    }
}
