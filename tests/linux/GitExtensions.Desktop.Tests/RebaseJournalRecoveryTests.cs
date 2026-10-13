using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.RebaseDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RebaseJournalRecoveryTests
{
    [AvaloniaTest] public Task TruncatedNativeMergeKeepsRealConflictAndAbortLight() => DamagedActive("merge", "truncated", false);
    [AvaloniaTest] public Task MissingNativeApplyStateKeepsRealConflictAndAbortDark() => DamagedActive("apply", "missing", true);
    [AvaloniaTest] public Task InvalidSavedCodecDoesNotGuessMessagesOrEnableContinue() => DamagedActive("merge", "codec", false, resolved: true);
    [AvaloniaTest] public Task LongJournalDiagnosticKeepsCurrentHashNoticeAndAbortInViewportDark() => DamagedActive("merge", "long-codec", true);
    [AvaloniaTest] public Task OldCp1252NativeJournalWithoutTodoCodecKeepsExactBytesAndAbortDark() => DamagedActive("merge", "todo-codec-provenance", true, cp1252: true);
    [AvaloniaTest] public Task TruncatedLegacyMergeKeepsRealConflictAndAbortDark() => DamagedActive("merge", "truncated", true, legacy: true);

    private async Task DamagedActive(string backend, string damage, bool dark, bool resolved = false, bool legacy = false, bool cp1252 = false)
    {
        using OptionalLocksDisabled locks = new();
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true);
        f.Write("untracked journal [1].dat", "unchanged\0bytes\n");
        string initialHead = await f.Text("rev-parse", "HEAD"), initialIndex = await f.Text("ls-files", "--stage");
        string initialFiles = Worktree(f), config = Hash(f.File(".git/config"));
        await f.Git("config", "rebase.backend", backend);
        if (cp1252) { await f.Git("config", "i18n.commitEncoding", "windows-1252"); }
        config = Hash(f.File(".git/config"));
        if (legacy)
        {
            GitCommandException? stopped = null;
            try { await f.Repository.InteractiveRebaseAsync(await f.Repository.GetRebasePlanAsync("main"), "/usr/bin/true"); }
            catch (GitCommandException error) { stopped = error; }
            Assert.That(stopped, Is.Not.Null, "The legacy fixture must genuinely stop at a conflict.");
        }
        else { await f.StartConflict(); }
        if (resolved) { f.Write("base.txt", "resolved journal fixture\n"); await f.Git("add", "base.txt"); }
        string journal = f.File(".git/" + (legacy ? "gitextensions-rebase" : "gitextensions-rebase-native"));
        Damage(journal, damage);
        string journalBefore = Files(journal), pending = await f.Witness(), indexBefore = Hash(f.File(".git/index"));
        RebaseDialog dialog = await f.Show(dark);
        Assert.That(dialog.LastError, Is.Null);
        Assert.That(dialog.State!.IsActive, Is.True);
        Assert.That(dialog.State.Backend, Is.EqualTo(backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply));
        Assert.That(dialog.State.CurrentCommit, Is.EqualTo(f.Topic));
        Assert.That(dialog.State.HasConflicts, Is.EqualTo(!resolved));
        Assert.That(dialog.State.JournalError, Is.Not.Empty);
        Assert.That(dialog.State.Options, Is.Null);
        Assert.That(dialog.State.Steps, Is.Empty);
        Assert.That(dialog.State.TodoText, Is.Null, "Damaged metadata must never supply a guessed codec or rewritten todo.");
        TextBlock identity = Field<TextBlock>(dialog, "RebaseCurrentCommit"), notice = Field<TextBlock>(dialog, "RebaseJournalNotice");
        Assert.That(identity.IsVisible, Is.True);
        Assert.That(identity.Text, Does.Contain(f.Topic), "The actual current Git identity must remain visible without decoding a todo.");
        Assert.That(notice.IsVisible, Is.True);
        Assert.That(notice.Text, Does.Contain("Abort"));
        Assert.That(notice.Text, Does.Not.Contain(f.Root), "Technical journal details must not displace the recovery actions.");
        Assert.That(ToolTip.GetTip(notice), Is.EqualTo(dialog.State.JournalError));
        Pump(dialog);
        ScrollViewer details = Field<ScrollViewer>(dialog, "RebaseJournalDetails");
        Assert.That(details.Bounds.Height, Is.LessThanOrEqualTo(74));
        Assert.That(identity.TranslatePoint(default, dialog)!.Value.Y, Is.GreaterThanOrEqualTo(0));
        Assert.That(details.TranslatePoint(new Point(0, details.Bounds.Height), dialog)!.Value.Y, Is.LessThanOrEqualTo(dialog.ClientSize.Height));
        foreach (string action in new[] { "ContinueRebase", "EditRebaseTodo", "SkipRebase", "CommitRebase" })
        { Assert.That(Field<Button>(dialog, action).IsEnabled, Is.False, action); }
        Assert.That(Field<Button>(dialog, "AbortRebase").IsEnabled, Is.True);
        Assert.That(Field<Button>(dialog, "AbortRebase").IsDefault, Is.False);
        Assert.That(Field<Button>(dialog, "PreserveRebaseJournal").IsVisible, Is.False, "An active Git backend must never allow moving its journal.");
        Assert.That(await f.Witness(), Is.EqualTo(pending));
        Assert.That(Hash(f.File(".git/index")), Is.EqualTo(indexBefore));
        Assert.That(Files(journal), Is.EqualTo(journalBefore));
        Capture(dialog, "rebase-damaged-" + (legacy ? "legacy-" : "native-") + backend + "-" + damage + ".png");
        Click(Field<Button>(dialog, "AbortRebase"));
        await Until(() => !dialog.IsBusy && dialog.Aborted);
        Assert.That(dialog.LastError, Is.Null, "A preserved journal must not mask Git's successful Abort.");
        Assert.That(dialog.Succeeded, Is.False);
        Assert.That(dialog.IsVisible, Is.True, "Keep the warning and recovery action visible after successful Abort.");
        Assert.That(dialog.Warnings, Is.Not.Empty);
        Assert.That(dialog.State!.IsActive, Is.False);
        Assert.That(Field<TextBlock>(dialog, "RebaseCurrentCommit").IsVisible, Is.False);
        Assert.That(dialog.State.HasRecoverableJournal, Is.True);
        Assert.That(Field<Button>(dialog, "PreserveRebaseJournal").IsVisible, Is.True);
        Assert.That(Field<Button>(dialog, "ExecuteRebase").IsEnabled, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(initialHead));
        Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(initialIndex));
        Assert.That(await f.Text("branch", "--show-current"), Is.EqualTo("topic"));
        Assert.That(Worktree(f), Is.EqualTo(initialFiles));
        Assert.That(Hash(f.File(".git/config")), Is.EqualTo(config));
        Assert.That(Files(journal), Is.EqualTo(journalBefore));
    }

    [AvaloniaTest]
    public async Task DamageAfterOpeningDoesNotMaskAbortSuccessOrHideItsWarning()
    {
        using OptionalLocksDisabled locks = new();
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true);
        string head = await f.Text("rev-parse", "HEAD");
        await f.StartConflict(); RebaseDialog dialog = await f.Show();
        Assert.That(dialog.State!.JournalError, Is.Null);
        string journal = f.File(".git/gitextensions-rebase-native"); Damage(journal, "truncated");
        string before = Files(journal);
        Click(Field<Button>(dialog, "AbortRebase")); await Until(() => !dialog.IsBusy && dialog.Aborted);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.Succeeded, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(head));
        Assert.That(dialog.Warnings, Is.Not.Empty);
        Assert.That(Field<TextBlock>(dialog, "RebaseJournalNotice").IsVisible, Is.True);
        Assert.That(Field<Button>(dialog, "PreserveRebaseJournal").IsEnabled, Is.True);
        Assert.That(Files(journal), Is.EqualTo(before));
        Click(Field<Button>(dialog, "CancelRebase"));
        Assert.That(dialog.Aborted, Is.True, "Closing the warning must retain the already completed Abort outcome.");
    }

    [AvaloniaTest]
    public async Task OrphanJournalIsPreservedByOneExplicitClickThenNewRebaseWorks()
    {
        using OptionalLocksDisabled locks = new();
        using RebaseUiFixture f = await RebaseUiFixture.Create();
        string journal = f.File(".git/gitextensions-rebase-native"); Directory.CreateDirectory(journal);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(journal, "state.json"), [0x7b, 0xff, 0x00]);
        System.IO.File.WriteAllText(System.IO.Path.Combine(journal, "unknown-message.txt"), "preserve this unknown message\n");
        string journalBefore = Files(journal), gitBefore = await f.Witness(), indexBefore = Hash(f.File(".git/index"));
        RebaseDialog dialog = await f.Show(dark: true);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.State!.IsActive, Is.False);
        Assert.That(dialog.State.HasRecoverableJournal, Is.True);
        Button preserve = Field<Button>(dialog, "PreserveRebaseJournal");
        Assert.That(preserve.IsEnabled && preserve.IsVisible, Is.True); Assert.That(preserve.IsDefault, Is.False);
        Assert.That(Field<Button>(dialog, "ExecuteRebase").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "ExecuteRebase").IsDefault, Is.False);
        Assert.That(await f.Witness(), Is.EqualTo(gitBefore)); Assert.That(Files(journal), Is.EqualTo(journalBefore));
        int clicks = 0; preserve.Click += (_, _) => clicks++;
        Click(preserve); await Until(() => !dialog.IsBusy && dialog.State?.HasRecoverableJournal == false);
        Assert.That(clicks, Is.EqualTo(1)); Assert.That(dialog.LastError, Is.Null);
        Assert.That(Directory.Exists(journal), Is.False);
        string saved = Directory.GetDirectories(f.File(".git"), "gitextensions-rebase-native-preserved-*").Single();
        Assert.That(Files(saved), Is.EqualTo(journalBefore));
        Assert.That(await f.Witness(), Is.EqualTo(gitBefore)); Assert.That(Hash(f.File(".git/index")), Is.EqualTo(indexBefore));
        Assert.That(Field<TextBlock>(dialog, "RebaseJournalNotice").Text, Does.Contain(saved));
        Assert.That(Field<Button>(dialog, "ExecuteRebase").IsEnabled, Is.True);
        Click(Field<Button>(dialog, "ExecuteRebase")); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.Aborted, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD~2"), Is.EqualTo(f.Main));
        Assert.That(Files(saved), Is.EqualTo(journalBefore), "A later valid rebase must not clean the preserved evidence.");
    }

    [AvaloniaTest]
    public async Task CancelOrphanRecoveryKeepsGitAndEveryUnknownJournalByte()
    {
        using OptionalLocksDisabled locks = new();
        using RebaseUiFixture f = await RebaseUiFixture.Create();
        string journal = f.File(".git/gitextensions-rebase-native"); Directory.CreateDirectory(journal);
        System.IO.File.WriteAllText(System.IO.Path.Combine(journal, "unknown-message.txt"), "kept without state.json\n");
        string gitBefore = await f.Witness(), journalBefore = Files(journal), indexBefore = Hash(f.File(".git/index"));
        RebaseDialog dialog = await f.Show();
        Assert.That(dialog.State!.HasRecoverableJournal, Is.True);
        Click(Field<Button>(dialog, "CancelRebase"));
        Assert.That(dialog.Succeeded || dialog.Aborted, Is.False);
        Assert.That(await f.Witness(), Is.EqualTo(gitBefore)); Assert.That(Files(journal), Is.EqualTo(journalBefore));
        Assert.That(Hash(f.File(".git/index")), Is.EqualTo(indexBefore));
        Assert.That(Directory.GetDirectories(f.File(".git"), "*-preserved-*").Length, Is.Zero);
    }

    private static void Damage(string directory, string kind)
    {
        string state = System.IO.Path.Combine(directory, "state.json");
        if (kind == "missing") { System.IO.File.Delete(state); }
        else if (kind == "todo-codec-provenance")
        {
            JsonObject value = JsonNode.Parse(System.IO.File.ReadAllText(state))!.AsObject();
            value.Remove("TodoEncodingName");
            System.IO.File.WriteAllText(state, value.ToJsonString());
        }
        else if (kind is "codec" or "long-codec")
        {
            JsonObject value = JsonNode.Parse(System.IO.File.ReadAllText(state))!.AsObject();
            value["EncodingName"] = kind == "long-codec" ? "unsupported-rebase-codec-" + new string('x', 2048) : "utf-16";
            System.IO.File.WriteAllText(state, value.ToJsonString());
        }
        else { System.IO.File.WriteAllText(state, "{\"RepositoryPath\":"); }
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(file)));
    private static string Files(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => System.IO.Path.GetRelativePath(root, path) + " " + Hash(path)));
    private static string Worktree(RebaseUiFixture f) => string.Join("\n", Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories)
        .Where(path => !path.StartsWith(f.File(".git") + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => System.IO.Path.GetRelativePath(f.Root, path) + " " + Hash(path)));
    private sealed class OptionalLocksDisabled : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        public OptionalLocksDisabled() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _before);
    }
}
