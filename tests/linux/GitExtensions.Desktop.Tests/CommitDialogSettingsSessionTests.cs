using System.Text.Json;
using System.Text.Json.Nodes;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitDialogSettingsSessionTests
{
    private static IEnumerable<string> Choices => CommitDialogSettingsCatalog.Fields.Select(choice => choice.Name);

    [TestCaseSource(nameof(Choices))]
    public void EachRealGlobalIntentionUsesItsExistingAuthorityAndRetainsNeighboursAndUnknownValues(string name)
    {
        using Scope f = new(); CommitDialogSetting choice = CommitDialogSettingsCatalog.Fields.Single(item => item.Name == name);
        Directory.CreateDirectory(f.Directory);
        foreach (string profile in new[] { CommitDialogSettingsCatalog.CommitProfile, CommitDialogSettingsCatalog.DesktopProfile })
        { File.WriteAllText(f.PathFor(profile), "{\"future\":{\"array\":[17,null,\"Ω\"]},\"DontConfirmAmend\":true,\"Dictionary\":\"none\",\"MarkIllFormedLinesInCommitMsg\":false}"); }
        CommitDialogSettingsSession session = new(f.Directory); object wanted = choice.IsNumber ? 11 : false;
        List<CommitDialogSettingsSaveReceipt> receipts = [];
        session.Save(session.Initial.With(name, wanted), receipts.Add);
        JsonObject json = JsonNode.Parse(File.ReadAllBytes(f.PathFor(choice.Profile)))!.AsObject();
        Assert.That(choice.IsNumber ? (object)json[name]!.GetValue<int>() : json[name]!.GetValue<bool>(), Is.EqualTo(wanted));
        Assert.That(JsonNode.DeepEquals(json["future"], JsonNode.Parse("{\"array\":[17,null,\"Ω\"]}")), Is.True);
        Assert.That(json["DontConfirmAmend"]!.GetValue<bool>(), Is.True); Assert.That(json["Dictionary"]!.GetValue<string>(), Is.EqualTo("none"));
        Assert.That(json["MarkIllFormedLinesInCommitMsg"]!.GetValue<bool>(), Is.False);
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { choice.Profile }));
        Assert.That(receipts.Single().Fields.Select(item => item.Name), Is.EqualTo(new[] { name }));
        Assert.That(new CommitDialogSettingsSession(f.Directory).Initial.Get(name), Is.EqualTo(wanted));
        byte[] bytes = File.ReadAllBytes(f.PathFor(choice.Profile)); session.Save(session.Initial, _ => Assert.Fail("No-op must not publish."));
        Assert.That(File.ReadAllBytes(f.PathFor(choice.Profile)), Is.EqualTo(bytes)); Assert.That(Directory.GetFiles(f.Directory, "*.tmp"), Is.Empty);
    }

    [Test]
    public void DefaultsAndNoOpKeepMissingProfilesMissingAndOldProfilesKeepAllThreeButtonsVisible()
    {
        using Scope f = new(); CommitDialogSettingsSession session = new(f.Directory);
        Assert.That(session.Initial, Is.EqualTo(new CommitDialogSettingsValues())); session.Save(session.Initial);
        Assert.That(Directory.Exists(f.Directory), Is.False);
        Directory.CreateDirectory(f.Directory); File.WriteAllText(f.PathFor(CommitDialogSettingsCatalog.CommitProfile), "{\"CloseAfterEachCommit\":false}");
        CommitPreferences read = new CommitPreferencesStore(f.Root).Load();
        Assert.That(read.ShowCommitAndPush && read.ShowResetWorkTreeChanges && read.ShowResetAllChanges, Is.True);
        Assert.That(read.CloseAfterEachCommit, Is.False);
    }

    [TestCase("{ broken Ω")]
    [TestCase("[]")]
    [TestCase("{\"ShowCommitAndPush\":null}")]
    [TestCase("{\"ShowCommitAndPush\":true,\"ShowCommitAndPush\":false}")]
    public void UnreadableUntouchedCommitProfileSurvivesDesktopSaveAndCannotBeRepairedByAnEditedCommitChoice(string raw)
    {
        using Scope f = new(); Directory.CreateDirectory(f.Directory); string path = f.PathFor(CommitDialogSettingsCatalog.CommitProfile);
        File.WriteAllText(path, raw); byte[] before = File.ReadAllBytes(path); CommitDialogSettingsSession session = new(f.Directory);
        Assert.That(session.LoadErrors.ContainsKey(CommitDialogSettingsCatalog.CommitProfile), Is.True);
        session.Save(session.Initial); session.Save(session.Initial with { ProvideAutocompletion = false });
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        Assert.Throws<IOException>(() => session.Save(session.Initial with { ShowResetAllChanges = false }));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public void ACompetingMessageCountFailsWithoutChangingEitherFileAndTheSameIntentionIsAcknowledgedWithoutAWrite()
    {
        using Scope f = new(); Directory.CreateDirectory(f.Directory); CommitDialogSettingsSession session = new(f.Directory);
        string path = f.PathFor(CommitDialogSettingsCatalog.CommitProfile);
        File.WriteAllText(path, "{ \"NumberOfPreviousMessages\": 7, \"peer\": true }\n"); byte[] peer = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => session.Save(session.Initial with { NumberOfPreviousMessages = 9 }));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(peer)); Assert.That(session.Initial.NumberOfPreviousMessages, Is.EqualTo(6));
        List<CommitDialogSettingsSaveReceipt> receipts = [];
        session.Save(session.Initial with { NumberOfPreviousMessages = 7 }, receipts.Add);
        Assert.That(receipts.Single().Written, Is.False); Assert.That(File.ReadAllBytes(path), Is.EqualTo(peer));
    }

    [Test]
    public void AnAcceptedCommitFileIsNeverReplayedAfterALaterDesktopFailureOrPeerEdit()
    {
        using Scope f = new(); Directory.CreateDirectory(f.Directory); CommitDialogSettingsSession session = new(f.Directory);
        string desktop = f.PathFor(CommitDialogSettingsCatalog.DesktopProfile); Directory.CreateDirectory(desktop);
        CommitDialogSettingsValues draft = session.Initial with { ShowResetAllChanges = false, ProvideAutocompletion = false };
        List<CommitDialogSettingsSaveReceipt> receipts = [];
        Assert.That(Assert.Catch(() => session.Save(draft, receipts.Add)), Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { CommitDialogSettingsCatalog.CommitProfile }));
        Assert.That(session.Initial.ShowResetAllChanges, Is.False);
        string commit = f.PathFor(CommitDialogSettingsCatalog.CommitProfile); File.WriteAllText(commit, "{ \"ShowResetAllChanges\": true, \"peer\": 13 }\n");
        byte[] peer = File.ReadAllBytes(commit); Directory.Delete(desktop); receipts.Clear(); session.Save(draft, receipts.Add);
        Assert.That(File.ReadAllBytes(commit), Is.EqualTo(peer));
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { CommitDialogSettingsCatalog.DesktopProfile }));
    }

    [Test]
    public void CancellationAfterACompletedFileAcknowledgesItAndNeverBeginsTheNextProfile()
    {
        using Scope f = new(); using CancellationTokenSource stop = new(); CommitDialogSettingsSession session = new(f.Directory);
        Assert.Throws<OperationCanceledException>(() => session.Save(session.Initial with { ShowCommitAndPush = false, ProvideAutocompletion = false },
            _ => stop.Cancel(), stop.Token));
        Assert.That(session.Initial.ShowCommitAndPush, Is.False);
        Assert.That(File.Exists(f.PathFor(CommitDialogSettingsCatalog.DesktopProfile)), Is.False);
    }

    [Test]
    public void TheExistingCommitOptionsWriterKeepsSavedVisibilityAndUnknownConfirmationAndAdvancedNeighbours()
    {
        using Scope f = new(); CommitDialogSettingsSession session = new(f.Directory);
        session.Save(session.Initial with { ShowResetWorkTreeChanges = false, ShowCommitAndPush = false });
        CommitPreferencesStore store = new(f.Root); JsonObject json = JsonNode.Parse(File.ReadAllBytes(store.FilePath))!.AsObject();
        json["future"] = JsonNode.Parse("{\"nested\":[true,17,null]}"); json["DontConfirmAmend"] = true; json["CommitAndPushForcedWhenAmend"] = true;
        File.WriteAllText(store.FilePath, json.ToJsonString());
        store.Save(store.Load() with { CloseAfterEachCommit = false, LastCommitMessage = "current draft Ω" });
        CommitPreferences read = store.Load(); Assert.That(read.ShowCommitAndPush || read.ShowResetWorkTreeChanges, Is.False);
        Assert.That(read.ShowResetAllChanges && read.DontConfirmAmend && read.CommitAndPushForcedWhenAmend, Is.True);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"], json["future"]), Is.True);
    }

    [Test]
    public void ActiveDesktopFallbackNeverBecomesTheDurableBaselineOrAnImplicitSave()
    {
        using Scope f = new(); CommitDialogSettingsSession session = new(f.Directory, new() { ProvideAutocompletion = false });
        Assert.That(session.Initial.ProvideAutocompletion, Is.False); session.Save(session.Initial);
        Assert.That(Directory.Exists(f.Directory), Is.False);
        Assert.That(session.MergeDesktop(new() { ProvideAutocompletion = false }).ProvideAutocompletion, Is.True);
        Assert.That(Directory.Exists(f.Directory), Is.False);
    }

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "commit-settings-session-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(Root, "gitextensions");
        internal string PathFor(string profile) => Path.Combine(Directory, profile);
        public void Dispose() { if (System.IO.Directory.Exists(Root)) { System.IO.Directory.Delete(Root, true); } }
    }
}
