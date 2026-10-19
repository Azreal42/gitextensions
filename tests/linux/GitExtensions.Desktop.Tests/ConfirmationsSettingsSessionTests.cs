using GitExtensions.Extensibility.Git;
using System.Text.Json.Nodes;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ConfirmationsSettingsSessionTests
{
    private static IEnumerable<string> Choices => ConfirmationSettingsCatalog.Fields.Select(setting => setting.Name);

    [TestCaseSource(nameof(Choices))]
    public void EachChoiceWritesItsRealWorkflowKeyAndPreservesForeignRawValues(string name)
    {
        using Scope scope = new();
        ConfirmationSetting field = ConfirmationSettingsCatalog.Fields.Single(candidate => candidate.Name == name);
        string path = scope.PathFor(field.Profile);
        Directory.CreateDirectory(scope.Directory);
        File.WriteAllText(path, "{\n \"future\": {\"literal\":[null,\"unchanged Ω\",17]}, \"unrelated\":false\n}");
        ConfirmationsSettingsSession session = new(scope.Directory);
        Assert.That(session.LoadErrors, Is.Empty);
        List<ConfirmationsSaveReceipt> receipts = [];
        session.Save(session.Initial.With(name, true), receipts.Add);
        JsonObject json = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        Assert.That(json[field.Key]!.GetValue<bool>(), Is.True);
        Assert.That(JsonNode.DeepEquals(json["future"], JsonNode.Parse("{\"literal\":[null,\"unchanged Ω\",17]}")), Is.True);
        Assert.That(json["unrelated"]!.GetValue<bool>(), Is.False);
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { field.Profile }));
        Assert.That(receipts.Single().Written, Is.True);
        Assert.That(new ConfirmationsSettingsSession(scope.Directory).Initial.Get(name), Is.True);
        byte[] accepted = File.ReadAllBytes(path);
        session.Save(session.Initial, _ => Assert.Fail("No-op cannot publish a receipt."));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(accepted));
        Assert.That(System.IO.Directory.GetFiles(scope.Directory, "*.tmp"), Is.Empty);
    }

    [Test]
    public void DefaultsAndNoOpNeitherCreateAProfileNorReplaceUnreadableUntouchedProfiles()
    {
        using Scope scope = new(); ConfirmationsSettingsSession initial = new(scope.Directory);
        Assert.That(initial.Initial, Is.EqualTo(new ConfirmationsSettingsValues()));
        initial.Save(initial.Initial);
        Assert.That(Directory.Exists(scope.Directory), Is.False);
        Directory.CreateDirectory(scope.Directory);
        string bad = scope.PathFor("pull-preferences.json"); File.WriteAllText(bad, "{ broken literal");
        byte[] bytes = File.ReadAllBytes(bad); ConfirmationsSettingsSession session = new(scope.Directory);
        Assert.That(session.LoadErrors.Keys, Is.EquivalentTo(new[] { "pull-preferences.json" }));
        session.Save(session.Initial with { DontConfirmAmend = true });
        Assert.That(File.ReadAllBytes(bad), Is.EqualTo(bytes));
        Assert.Throws<System.Text.Json.JsonException>(() => session.Save(session.Initial with { DontConfirmFetchAndPruneAll = true }));
        Assert.That(File.ReadAllBytes(bad), Is.EqualTo(bytes));
    }

    [Test]
    public void NullableIntentRejectsACompetingDecisionWithoutOverwritingEitherDraftOrFile()
    {
        using Scope scope = new(); Directory.CreateDirectory(scope.Directory);
        string path = scope.PathFor("pull-preferences.json");
        File.WriteAllText(path, "{\"AutoPopStashAfterPull\":null,\"AutoStash\":true}");
        ConfirmationsSettingsSession session = new(scope.Directory);
        ConfirmationsSettingsValues draft = session.Initial with { AutoPopStashAfterPull = true };
        File.WriteAllText(path, "{ \"AutoPopStashAfterPull\":false, \"AutoStash\":true, \"peer\":23 }");
        byte[] peer = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => session.Save(draft));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(peer));
        Assert.That(session.Initial.AutoPopStashAfterPull, Is.Null);
        Assert.That(draft.AutoPopStashAfterPull, Is.True);
    }

    [Test]
    public void APeerWhoAlreadySavedTheSameDecisionIsAcknowledgedWithoutRewritingRawBytes()
    {
        using Scope scope = new(); Directory.CreateDirectory(scope.Directory);
        ConfirmationsSettingsSession session = new(scope.Directory);
        string path = scope.PathFor("commit-preferences.json");
        File.WriteAllText(path, "{ \"DontConfirmAmend\": true, \"peer\": \"format stays Ω\" }\n");
        byte[] peer = File.ReadAllBytes(path); List<ConfirmationsSaveReceipt> receipts = [];
        session.Save(session.Initial with { DontConfirmAmend = true }, receipts.Add);
        Assert.That(receipts.Single().Written, Is.False);
        Assert.That(session.Initial.DontConfirmAmend, Is.True);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(peer));
    }

    [Test]
    public void SuccessfulFileIsAccusedBeforeALaterFailureAndRetryDoesNotReplayItOverAPeerEdit()
    {
        using Scope scope = new(); Directory.CreateDirectory(scope.Directory);
        ConfirmationsSettingsSession session = new(scope.Directory);
        string failing = scope.PathFor("pull-preferences.json"); Directory.CreateDirectory(failing);
        ConfirmationsSettingsValues draft = session.Initial with { DontConfirmAmend = true, AutoPopStashAfterPull = false };
        List<ConfirmationsSaveReceipt> receipts = [];
        Exception? failure = Assert.Catch(() => session.Save(draft, receipts.Add));
        Assert.That(failure, Is.InstanceOf<UnauthorizedAccessException>().Or.InstanceOf<IOException>());
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { "commit-preferences.json" }));
        Assert.That(session.Initial.DontConfirmAmend, Is.True); Assert.That(session.Initial.AutoPopStashAfterPull, Is.Null);
        string commit = scope.PathFor("commit-preferences.json");
        File.WriteAllText(commit, "{ \"DontConfirmAmend\": false, \"peer\":true }"); byte[] peer = File.ReadAllBytes(commit);
        Directory.Delete(failing); receipts.Clear();
        session.Save(draft, receipts.Add);
        Assert.That(receipts.Select(receipt => receipt.Profile), Is.EqualTo(new[] { "pull-preferences.json" }));
        Assert.That(File.ReadAllBytes(commit), Is.EqualTo(peer), "The acknowledged first intention must never be replayed by retry.");
        Assert.That(new ConfirmationsSettingsSession(scope.Directory).Initial.AutoPopStashAfterPull, Is.False);
    }

    [Test]
    public void CancellationBetweenReceiptsKeepsTheFirstFileAndDoesNotBeginTheNext()
    {
        using Scope scope = new(); using CancellationTokenSource cancellation = new();
        ConfirmationsSettingsSession session = new(scope.Directory); List<ConfirmationsSaveReceipt> receipts = [];
        Assert.Throws<OperationCanceledException>(() => session.Save(session.Initial with
            { DontConfirmAmend = true, DontConfirmUndoLastCommit = true }, receipt => { receipts.Add(receipt); cancellation.Cancel(); }, cancellation.Token));
        Assert.That(receipts.Count, Is.EqualTo(1)); Assert.That(session.Initial.DontConfirmAmend, Is.True);
        Assert.That(File.Exists(scope.PathFor("commit-preferences.json")), Is.True);
        Assert.That(File.Exists(scope.PathFor("browse-command-preferences.json")), Is.False);
    }

    [Test]
    public void RealGeneralAndConfirmationsSavesSharePullAndSubmoduleKeysWithoutConflictsOrPriorityLoss()
    {
        using Scope scope = new(useAsXdg: true);
        PullPreferencesStore pull = new(); SubmoduleCheckoutPreferencesStore submodules = new();
        pull.Save(new()); submodules.SaveStrict(new(false, null));
        DesktopPreferences desktop = new(); GeneralSettingsSession general = new(desktop, new());
        ConfirmationsSettingsSession confirmations = new();
        GeneralSettingsPublication publication = general.Save(general.Initial with
        { DefaultPullAction = GitPullAction.FetchAll, IncludeUntrackedFilesInAutoStash = true, UpdateSubmodulesOnCheckout = true }, desktop);
        confirmations.Save(confirmations.Initial with
        { DontConfirmFetchAndPruneAll = true, AutoPopStashAfterPull = false, DontConfirmUpdateSubmodulesOnCheckout = false });
        Assert.That(pull.Load(), Is.EqualTo(new PullPreferences(DefaultAction: GitPullAction.FetchAll,
            DontConfirmFetchAndPruneAll: true, AutoPopStashAfterPull: false, IncludeUntrackedFilesInAutoStash: true)));
        Assert.That(submodules.Load(), Is.EqualTo(new SubmoduleCheckoutPreferences(true, false)));
        Assert.That(submodules.Load().Decision, Is.True, "General has the original explicit priority over the remembered confirmation.");
        // A later Apply from General must not republish its old confirmation snapshot.
        general.Save(publication.Values with { UpdateSubmodulesOnCheckout = null, IncludeUntrackedFilesInAutoStash = false }, publication.Desktop);
        Assert.That(submodules.Load(), Is.EqualTo(new SubmoduleCheckoutPreferences(null, false)));
        Assert.That(submodules.Load().Decision, Is.False);
        Assert.That(pull.Load().DontConfirmFetchAndPruneAll, Is.True);
        Assert.That(pull.Load().AutoPopStashAfterPull, Is.False);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "confirmations-session-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly bool _xdg;
        internal string Directory => System.IO.Path.Combine(_root, "gitextensions");
        internal string PathFor(string profile) => System.IO.Path.Combine(Directory, profile);
        internal Scope(bool useAsXdg = false)
        { _xdg = useAsXdg; if (_xdg) { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); } }
        public void Dispose()
        { if (_xdg) { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldXdg); } if (System.IO.Directory.Exists(_root)) { System.IO.Directory.Delete(_root, true); } }
    }
}
