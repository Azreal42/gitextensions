using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.RebaseDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RebaseUnreadableTodoTests
{
    [AvaloniaTest]
    public Task FirstOpenUnreadableTodoKeepsRealCp1252ConflictAndAbortLight()
        => CheckUnreadable("git-rebase-todo", dark: false, reload: false, resolved: false);

    [AvaloniaTest]
    public Task FirstOpenUnreadableDoneKeepsResolvedCp1252StateAndAbortDark()
        => CheckUnreadable("done", dark: true, reload: false, resolved: true);

    [AvaloniaTest]
    public Task PreviouslyReadableTodoReloadCannotRetainUnsafeActionsDark()
        => CheckUnreadable("git-rebase-todo", dark: true, reload: true, resolved: false);

    [AvaloniaTest]
    public Task PreviouslyReadableDoneReloadCannotRetainCommitOrContinueLight()
        => CheckUnreadable("done", dark: false, reload: true, resolved: true);

    private static async Task CheckUnreadable(string filename, bool dark, bool reload, bool resolved)
    {
        CultureInfo oldCulture = CultureInfo.CurrentUICulture;
        using OptionalLocksDisabled locks = new();
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true);
            await f.Git("config", "i18n.commitEncoding", "windows-1252");
            // Replace the fixture's first English topic object with an actual CP1252
            // object; the native todo generated later must still be strictly UTF8.
            await f.Git("reset", "--hard", f.Topic);
            await f.Repository.CommitAsync(new GitCommitOptions("Café € native instruction", Amend: true));
            f.Topic = await f.Text("rev-parse", "HEAD");
            f.Write("later.txt", "later native instruction\n"); await f.Git("add", "later.txt");
            await f.Repository.CommitAsync("Après € native instruction");
            f.Write("untracked [instruction].dat", "unchanged\0bytes\n");
            await f.Git("branch", "independent", "main"); await f.Git("tag", "instruction-base", "main");
            string originalHead = await f.Text("rev-parse", "HEAD"), originalIndex = await f.Text("ls-files", "--stage"), refs = await f.Text("show-ref");
            byte[] originalFile = System.IO.File.ReadAllBytes(f.File("base.txt")), untracked = System.IO.File.ReadAllBytes(f.File("untracked [instruction].dat"));
            await f.StartConflict();
            if (resolved)
            { f.Write("base.txt", "explicit native resolution\n"); await f.Git("add", "base.txt"); }
            RebaseDialog? dialog = reload ? await f.Show(dark) : null;
            GitRebaseState? previous = dialog?.State;
            if (reload)
            {
                Assert.That(dialog!.LastError, Is.Null);
                Assert.That(previous!.InstructionError, Is.Null);
                Assert.That(previous.Steps, Is.Not.Empty);
                Assert.That(Field<Button>(dialog, "CommitRebase").IsEnabled, Is.True);
                Assert.That(Field<Button>(dialog, "ContinueRebase").IsEnabled, Is.EqualTo(resolved));
            }
            string path = f.File(".git/rebase-merge/" + filename);
            byte[] actualInstructions = System.IO.File.ReadAllBytes(path);
            _ = new UTF8Encoding(false, true).GetString(actualInstructions);
            await System.IO.File.WriteAllBytesAsync(path, [.. actualInstructions, (byte)'\n', (byte)'#', (byte)' ', (byte)'C', (byte)'a', (byte)'f', 0xe9, (byte)'\n']);
            await f.Git("config", "i18n.commitEncoding", filename == "done" ? "utf-16" : "unknown-unreadable-todo-ui-codec");
            byte[] config = System.IO.File.ReadAllBytes(f.File(".git/config")), journal = System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json"));
            byte[] rawIndex = System.IO.File.ReadAllBytes(f.File(".git/index")), corrupt = System.IO.File.ReadAllBytes(path);
            string before = await f.Witness();
            if (dialog is null) { dialog = await f.Show(dark); }
            else { await dialog.InitializeAsync(); Pump(dialog); }
            Assert.That(dialog.LastError, Is.Null, "Unreadable native text is a recoverable state, not a failed initialization retaining old UI.");
            GitRebaseState state = dialog.State!;
            Assert.That(state, Is.Not.Null);
            if (previous is not null) { Assert.That(state, Is.Not.SameAs(previous)); }
            Assert.That(state.IsActive, Is.True); Assert.That(state.Backend, Is.EqualTo(GitRebaseBackend.Merge));
            Assert.That(state.CurrentCommit, Is.EqualTo(f.Topic)); Assert.That(state.OriginalHead, Is.EqualTo(originalHead));
            Assert.That(state.HeadName, Is.EqualTo("refs/heads/topic")); Assert.That(state.HasConflicts, Is.EqualTo(!resolved));
            Assert.That(state.EncodingName, Is.EqualTo("windows-1252")); Assert.That(state.TodoEncodingName, Is.EqualTo("utf-8"));
            Assert.That(state.Options?.BranchName, Is.EqualTo("main")); Assert.That(state.EncodingError, Is.Null); Assert.That(state.JournalError, Is.Null);
            Assert.That(state.InstructionError, Does.Contain(filename)); Assert.That(state.Steps, Is.Empty); Assert.That(state.TodoText, Is.Null); Assert.That(state.TodoPath, Is.Null);
            foreach (string action in new[] { "ContinueRebase", "SkipRebase", "EditRebaseTodo", "CommitRebase" })
            { Assert.That(Field<Button>(dialog, action).IsEnabled, Is.False, action); }
            Button abort = Field<Button>(dialog, "AbortRebase");
            Assert.That(abort.IsVisible && abort.IsEnabled, Is.True);
            Assert.That(Field<Button>(dialog, "ExecuteRebase").IsVisible, Is.False);
            Assert.That(Field<Button>(dialog, "PreserveRebaseJournal").IsVisible, Is.False, "A valid private journal is not damaged and cannot be moved during an active Git rebase.");
            TextBlock current = Field<TextBlock>(dialog, "RebaseCurrentCommit");
            Assert.That(current.IsVisible, Is.True); Assert.That(current.Text, Does.Contain(f.Topic));
            TextBlock notice = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => ToolTip.GetTip(text) as string == state.InstructionError);
            Assert.That(notice.IsVisible, Is.True); Assert.That(notice.Text, Does.Contain("cannot be read"));
            Assert.That(notice.Text, Does.Not.Contain("journal")); Assert.That(notice.Text, Does.Contain("Abort"));
            Pump(dialog);
            Assert.That(current.TranslatePoint(default, dialog)!.Value.Y, Is.GreaterThanOrEqualTo(0));
            Assert.That(Ready(abort), Is.True, "The actual recovery action must remain hit-testable on first load and reload.");
            Capture(dialog, "rebase-native-unreadable-" + filename + "-" + (reload ? "reload-" : "first-") + (dark ? "Dark" : "Light") + ".png");
            await dialog.ManageRebaseAsync(GitOperationAction.Continue);
            await dialog.ManageRebaseAsync(GitOperationAction.Skip);
            Assert.That(await f.Witness(), Is.EqualTo(before));
            Assert.That(System.IO.File.ReadAllBytes(path), Is.EqualTo(corrupt));
            Assert.That(System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json")), Is.EqualTo(journal));
            Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(rawIndex));
            int clicks = 0; abort.Click += (_, _) => clicks++;
            Click(abort); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
            Assert.That(clicks, Is.EqualTo(1)); Assert.That(dialog.Aborted, Is.True); Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.Warnings, Is.Empty, "Unreadable Git instructions do not invalidate a valid private journal after successful Abort.");
            Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(originalHead)); Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(originalIndex));
            Assert.That(await f.Text("show-ref"), Is.EqualTo(refs)); Assert.That(System.IO.File.ReadAllBytes(f.File("base.txt")), Is.EqualTo(originalFile));
            Assert.That(System.IO.File.ReadAllBytes(f.File("untracked [instruction].dat")), Is.EqualTo(untracked));
            Assert.That(System.IO.File.ReadAllBytes(f.File(".git/config")), Is.EqualTo(config));
            Assert.That(Directory.Exists(f.File(".git/rebase-merge")), Is.False);
            Assert.That(Directory.Exists(f.File(".git/gitextensions-rebase-native")), Is.False);
        }
        finally { CultureInfo.CurrentUICulture = oldCulture; }
    }

    private sealed class OptionalLocksDisabled : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        public OptionalLocksDisabled() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _before);
    }
}
