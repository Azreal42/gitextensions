using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RebaseDialogTests
{
    [AvaloniaTest] public Task OriginalOptionsAndGeometryLight() => OriginalOptionsAndGeometry(false);
    [AvaloniaTest] public Task OriginalOptionsAndGeometryDark() => OriginalOptionsAndGeometry(true);
    private async Task OriginalOptionsAndGeometry(bool dark)
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create();
        await f.Git("config", "rebase.autosquash", "true"); await f.Git("config", "rebase.autostash", "true"); await f.Git("config", "rebase.updaterefs", "true");
        await f.Git("update-ref", "refs/remotes/origin/main", f.Main); await f.Git("tag", "release", f.Base);
        string before = await f.Witness();
        RebaseDialog dialog = await f.Show(dark, interactive: true);
        Assert.That(Field<TextBlock>(dialog, "RebaseCurrentBranch").Text, Is.EqualTo("topic"));
        ComboBox onto = Field<ComboBox>(dialog, "RebaseOn");
        Assert.That(onto.IsEditable, Is.True); Assert.That(onto.Items.Cast<string>(), Is.EquivalentTo(new[] { "main", "topic", "origin/main", "release" }));
        Assert.That(Editor(onto).Text, Is.EqualTo("main"));
        Assert.That(Field<ComboBox>(dialog, "RebaseRangeTo").Items.Cast<string>(), Is.EquivalentTo(new[] { "main", "topic" }));
        Assert.That(Field<CheckBox>(dialog, "RebaseAutoStash").IsChecked, Is.False, "Git Extensions remembers a separate false-by-default choice.");
        Assert.That(Field<CheckBox>(dialog, "RebaseAutosquash").IsChecked, Is.True); Assert.That(dialog.ReadOptions().UpdateRefs, Is.Null);
        Assert.That(Field<Image>(dialog, "OriginalCommandHelpImage").Bounds.Size, Is.EqualTo(new Size(289, 373)));
        Capture(dialog, "rebase-options-" + (dark ? "dark" : "light") + ".png");
        Click(Field<CheckBox>(dialog, "RebaseIgnoreDate"));
        Assert.That(Field<CheckBox>(dialog, "RebaseCommitterDateIsAuthorDate").IsEnabled, Is.False);
        Assert.That(Field<CheckBox>(dialog, "RebaseInteractive").IsEnabled, Is.False);
        Assert.That(Field<CheckBox>(dialog, "RebasePreserveMerges").IsEnabled, Is.False);
        Assert.That(Field<CheckBox>(dialog, "RebaseAutosquash").IsEnabled, Is.False);
        Click(Field<CheckBox>(dialog, "RebaseIgnoreDate")); Click(Field<CheckBox>(dialog, "RebaseCommitterDateIsAuthorDate"));
        Assert.That(Field<CheckBox>(dialog, "RebaseIgnoreDate").IsEnabled, Is.False);
        Click(Field<CheckBox>(dialog, "RebaseCommitterDateIsAuthorDate"));
        Click(Field<CheckBox>(dialog, "RebaseUpdateRefs")); Assert.That(dialog.ReadOptions().UpdateRefs, Is.False);
        Assert.That(await f.Witness(), Is.EqualTo(before), "Opening and editing options must never write Git/config/index/worktree.");
    }

    [AvaloniaTest]
    public async Task RangeFromIsExcludedAndToNamesTheActualLocalBranchWithOnlyAutoStashRemembered()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create();
        new RebasePreferencesStore().Save(new(DontConfirmRebase: true));
        RebaseDialog dialog = await f.Show(initialFrom: f.Base, initialTo: "topic");
        Assert.That(dialog.ReadOptions().BranchName, Is.EqualTo("topic")); Assert.That(dialog.ReadOptions().From, Is.EqualTo(f.Base)); Assert.That(dialog.ReadOptions().OnTo, Is.EqualTo("main"));
        Assert.That(Field<TextBox>(dialog, "RebaseRangeFrom").IsEnabled, Is.True);
        Click(Field<CheckBox>(dialog, "RebaseAutoStash")); Click(Field<Button>(dialog, "ExecuteRebase")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.Aborted, Is.False);
        Assert.That(await f.Text("branch", "--show-current"), Is.EqualTo("topic"));
        Assert.That(await f.Text("rev-parse", "HEAD~2"), Is.EqualTo(f.Main));
        Assert.That(await f.Text("show", "HEAD:later.txt"), Is.EqualTo("later")); Assert.That(await f.Text("show", "HEAD:topic.txt"), Is.EqualTo("topic"));
        RebasePreferences saved = new RebasePreferencesStore().Load(); Assert.That(saved.AutoStash, Is.True); Assert.That(saved.DontConfirmRebase, Is.True);
        RebaseDialog reopened = await f.Show(); Assert.That(Field<CheckBox>(reopened, "RebaseAutoStash").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(reopened, "RebaseInteractive").IsChecked, Is.False); Assert.That(Field<CheckBox>(reopened, "RebaseSpecificRange").IsChecked, Is.False);
    }

    [AvaloniaTest]
    public async Task InvalidReferenceKeepsDialogAndEveryGitPreimageAndNeverReportsSuccess()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(); RebaseDialog dialog = await f.Show(); string before = await f.Witness();
        ComboBox onto = Field<ComboBox>(dialog, "RebaseOn"); TextBox editor = Editor(onto); Click(editor); editor.SelectAll(); dialog.KeyTextInput("absent-ui-reference");
        Assert.That(onto.Text, Is.EqualTo("absent-ui-reference")); Click(Field<Button>(dialog, "ExecuteRebase")); await Until(() => !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.Succeeded || dialog.Aborted, Is.False); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task CancelAndNewRepositoryInstanceRediscoverNativeConflictAndFrozenOptions()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); RebaseDialog dialog = await f.Show(interactive: true);
        Click(Field<CheckBox>(dialog, "RebasePreserveMerges")); Click(Field<CheckBox>(dialog, "RebaseAutosquash"));
        GitRebaseOptions original = dialog.ReadOptions(); Click(Field<Button>(dialog, "ExecuteRebase"));
        await Until(() => !dialog.IsBusy && dialog.State?.IsActive == true);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastError, Is.Not.Null); Assert.That(dialog.State!.HasConflicts, Is.True);
        Assert.That(Field<Button>(dialog, "ResolveRebaseConflicts").IsDefault, Is.True); Assert.That(Field<Button>(dialog, "ContinueRebase").IsVisible, Is.False);
        Assert.That(dialog.State.Steps.Any(patch => patch.Status == GitRebaseStepStatus.Applying && patch.CommitHash == f.Topic), Is.True);
        string beforeClose = await f.Witness(); string journal = System.IO.File.ReadAllText(f.File(".git/gitextensions-rebase-native/state.json"));
        await Until(() => Ready(Field<Button>(dialog, "CancelRebase"))); Click(Field<Button>(dialog, "CancelRebase"));
        Assert.That(dialog.Succeeded || dialog.Aborted, Is.False); Assert.That(await f.Witness(), Is.EqualTo(beforeClose));
        RebaseDialog reopened = await f.Show(dark: true);
        Assert.That(reopened.State!.Options, Is.EqualTo(original)); Assert.That(reopened.State.IsActive, Is.True);
        Assert.That(Field<CheckBox>(reopened, "RebaseInteractive").IsChecked, Is.True); Assert.That(Field<CheckBox>(reopened, "RebasePreserveMerges").IsChecked, Is.True);
        Assert.That(Field<ComboBox>(reopened, "RebaseOn").IsEnabled, Is.False); Assert.That(Field<TextBox>(reopened, "RebaseRangeFrom").IsEnabled, Is.False);
        Assert.That(System.IO.File.ReadAllText(f.File(".git/gitextensions-rebase-native/state.json")), Is.EqualTo(journal)); Assert.That(await f.Witness(), Is.EqualTo(beforeClose));
        Capture(reopened, "rebase-pending-reopened-dark.png");
    }

    [AvaloniaTest]
    public async Task AddFilesDryRunThenStageChangesDefaultToContinueAndNativeContinueCompletes()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict(); RebaseDialog dialog = await f.Show();
        f.Write("base.txt", "resolved café\n"); string index = await f.Text("ls-files", "--stage");
        Click(Field<Button>(dialog, "AddRebaseFiles")); await Until(() => dialog.OwnedWindows.OfType<RebaseAddFilesDialog>().Any());
        RebaseAddFilesDialog add = dialog.OwnedWindows.OfType<RebaseAddFilesDialog>().Single(); Pump(add);
        Field<TextBox>(add, "RebaseAddFilesFilter").Text = "\"base.txt\"";
        await Until(() => Ready(Field<Button>(add, "RebaseShowFiles"))); Click(Field<Button>(add, "RebaseShowFiles")); await Until(() => !add.IsBusy && Field<TextBox>(add, "RebaseAddFilesOutput").IsVisible);
        Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(index)); Assert.That(Field<TextBox>(add, "RebaseAddFilesOutput").Text, Does.Contain("base.txt"));
        Click(Field<Button>(add, "RebaseAddFiles")); await Until(() => !add.IsVisible && !dialog.IsBusy && dialog.State?.HasConflicts == false);
        Assert.That(Field<Button>(dialog, "ContinueRebase").IsDefault, Is.True); Assert.That(Field<Button>(dialog, "ResolveRebaseConflicts").IsVisible, Is.False);
        Assert.That(await f.Text("show", ":base.txt"), Is.EqualTo("resolved café"));
        Click(Field<Button>(dialog, "ContinueRebase")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.Aborted, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD~2"), Is.EqualTo(f.Main)); Assert.That(await f.Text("show", "HEAD:base.txt"), Is.EqualTo("resolved café"));
        Assert.That(Directory.Exists(f.File(".git/gitextensions-rebase-native")), Is.False); Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task AbortRestoresInitialBranchIndexAndUntrackedBytesWithoutFalseSuccess()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); f.Write("untracked λ [1].dat", "kept\0bytes\n");
        string head = await f.Text("rev-parse", "HEAD"), index = await f.Text("ls-files", "--stage"); byte[] bytes = System.IO.File.ReadAllBytes(f.File("untracked λ [1].dat"));
        await f.StartConflict(); RebaseDialog dialog = await f.Show();
        Click(Field<Button>(dialog, "AbortRebase")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Aborted, Is.True, dialog.LastError); Assert.That(dialog.Succeeded, Is.False); Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(head));
        Assert.That(await f.Text("branch", "--show-current"), Is.EqualTo("topic")); Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(index));
        Assert.That(System.IO.File.ReadAllBytes(f.File("untracked λ [1].dat")), Is.EqualTo(bytes)); Assert.That((await f.Repository.GetRebaseStateAsync()).IsActive, Is.False);
    }

    [AvaloniaTest] public Task ExternalMergeWithUnknownEncodingRemainsAbortable() => ExternalInvalidEncodingRemainsAbortable("merge", "unknown-rebase-ui-codec");
    [AvaloniaTest] public Task ExternalMergeWithUtf16EncodingRemainsAbortable() => ExternalInvalidEncodingRemainsAbortable("merge", "utf-16");
    [AvaloniaTest] public Task ExternalApplyWithUnknownEncodingRemainsAbortable() => ExternalInvalidEncodingRemainsAbortable("apply", "unknown-rebase-ui-codec");
    [AvaloniaTest] public Task ExternalApplyWithUtf16EncodingRemainsAbortable() => ExternalInvalidEncodingRemainsAbortable("apply", "utf-16");
    private async Task ExternalInvalidEncodingRemainsAbortable(string backend, string codec)
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true);
        f.Write("untracked [codec].dat", "kept\0bytes\n");
        string originalHead = await f.Text("rev-parse", "HEAD"), originalIndex = await f.Text("ls-files", "--stage");
        byte[] untracked = System.IO.File.ReadAllBytes(f.File("untracked [codec].dat"));
        GitResult conflict = await new GitRunner().RunAsync(f.Root, ["-c", "rebase.backend=" + backend, "rebase", "main"]);
        Assert.That(conflict.Success, Is.False);
        await f.Git("config", "i18n.commitEncoding", codec);
        byte[] config = System.IO.File.ReadAllBytes(f.File(".git/config"));
        string pending = await f.Witness();
        RebaseDialog dialog = await f.Show(dark: backend == "apply");
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.State!.IsActive, Is.True);
        Assert.That(dialog.State.Backend, Is.EqualTo(backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply));
        Assert.That(dialog.State.CurrentCommit, Is.EqualTo(f.Topic)); Assert.That(dialog.State.HasConflicts, Is.True);
        Assert.That(dialog.State.EncodingError, Does.Contain(codec)); Assert.That(dialog.State.Steps, Is.Empty);
        Assert.That(dialog.State.TodoText, Is.Null, "Unknown todo bytes must not acquire a guessed decoding.");
        Assert.That(Field<TextBlock>(dialog, "RebaseEncodingNotice").IsVisible, Is.True);
        Assert.That(Field<TextBlock>(dialog, "RebaseEncodingNotice").Text, Does.Contain("Abort"));
        foreach (string action in new[] { "ContinueRebase", "EditRebaseTodo", "SkipRebase", "CommitRebase" })
        { Assert.That(Field<Button>(dialog, action).IsEnabled, Is.False, action); }
        Assert.That(Field<Button>(dialog, "AbortRebase").IsEnabled, Is.True);
        Assert.That(Field<Button>(dialog, "AbortRebase").IsDefault, Is.False, "A codec error must not make Abort the Enter action.");
        Assert.That(await f.Witness(), Is.EqualTo(pending), "Reading an unreadable external session must preserve its exact preimage.");
        Capture(dialog, "rebase-external-" + backend + "-" + (codec == "utf-16" ? "utf16" : "unknown") + ".png");
        Click(Field<Button>(dialog, "AbortRebase")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Aborted, Is.True, dialog.LastError); Assert.That(dialog.Succeeded, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(originalHead)); Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(originalIndex));
        Assert.That(await f.Text("branch", "--show-current"), Is.EqualTo("topic"));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/config")), Is.EqualTo(config));
        Assert.That(System.IO.File.ReadAllBytes(f.File("untracked [codec].dat")), Is.EqualTo(untracked));
        Assert.That((await f.Repository.GetRebaseStateAsync()).IsActive, Is.False);
    }

    [AvaloniaTest] public Task NativeJournalWithUnknownEncodingKeepsReadableStateAndAllowsAbort() => NativeJournalEncodingChanged("unknown-rebase-ui-codec");
    [AvaloniaTest] public Task NativeJournalWithUtf16EncodingKeepsReadableStateAndAllowsAbort() => NativeJournalEncodingChanged("utf-16");
    private async Task NativeJournalEncodingChanged(string codec)
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true);
        string originalHead = await f.Text("rev-parse", "HEAD"), originalIndex = await f.Text("ls-files", "--stage");
        await f.StartConflict(); f.Write("base.txt", "resolved café\n"); await f.Git("add", "base.txt");
        await f.Git("config", "i18n.commitEncoding", codec);
        byte[] config = System.IO.File.ReadAllBytes(f.File(".git/config")); string pending = await f.Witness();
        byte[] journal = System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json"));
        RebaseDialog dialog = await f.Show();
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.State!.EncodingError, Is.Null, "The saved codec still identifies the native todo bytes.");
        Assert.That(dialog.State.CurrentCommit, Is.EqualTo(f.Topic)); Assert.That(dialog.State.Steps.Any(patch => patch.CommitHash == f.Topic), Is.True);
        Assert.That(Field<Button>(dialog, "ContinueRebase").IsEnabled, Is.True);
        Click(Field<Button>(dialog, "ContinueRebase")); await Until(() => !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.LastError, Does.Contain(codec)); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(dialog.Succeeded || dialog.Aborted, Is.False); Assert.That(dialog.State!.IsActive, Is.True);
        Assert.That(await f.Witness(), Is.EqualTo(pending), "Continue must reject the changed codec before writing Git state.");
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json")), Is.EqualTo(journal));
        Click(Field<Button>(dialog, "AbortRebase")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Aborted, Is.True, dialog.LastError); Assert.That(dialog.Succeeded, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(originalHead)); Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(originalIndex));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/config")), Is.EqualTo(config));
        Assert.That(Directory.Exists(f.File(".git/gitextensions-rebase-native")), Is.False);
    }

    [AvaloniaTest]
    public async Task SkipDropsOnlyCurrentlyApplyingCommitAndKeepsTheFollowingNativeTodo()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict(); RebaseDialog dialog = await f.Show();
        Assert.That(dialog.State!.CurrentCommit, Is.EqualTo(f.Topic)); Click(Field<Button>(dialog, "SkipRebase")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.Aborted, Is.False); Assert.That(await f.Text("rev-parse", "HEAD~"), Is.EqualTo(f.Main));
        Assert.That(await f.Text("show", "HEAD:base.txt"), Is.EqualTo("ours")); Assert.That(await f.Text("show", "HEAD:later.txt"), Is.EqualTo("later"));
    }

    [AvaloniaTest]
    public async Task ExternalApplyRecoveryHasRealPatchMetadataAndNoEditableTodo()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true);
        GitResult conflict = await new GitRunner().RunAsync(f.Root, ["-c", "rebase.backend=apply", "rebase", "main"]); Assert.That(conflict.Success, Is.False);
        RebaseDialog dialog = await f.Show(); Assert.That(dialog.State!.Backend, Is.EqualTo(GitRebaseBackend.Apply)); Assert.That(dialog.State.Options, Is.Null);
        GitRebasePatch current = dialog.State.Steps.Single(patch => patch.Status == GitRebaseStepStatus.Applying);
        Assert.That(current.CommitHash, Is.EqualTo(f.Topic)); Assert.That(current.Author, Is.EqualTo("Rebase UI")); Assert.That(current.Subject, Is.EqualTo("Topic"));
        Assert.That(System.IO.File.Exists(current.PatchPath), Is.True); Assert.That(Field<Button>(dialog, "EditRebaseTodo").IsEnabled, Is.False);
        ListBox patches = Field<ListBox>(dialog, "RebasePatches");
        Assert.That(patches.Focus(), Is.True, "The native patch grid must accept keyboard focus.");
        Assert.That(patches.IsKeyboardFocusWithin, Is.True, "Enter must originate in the patch grid, rather than the default conflict action.");
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => dialog.OwnedWindows.OfType<CompareRevisionsDialog>().Any()); CompareRevisionsDialog diff = dialog.OwnedWindows.OfType<CompareRevisionsDialog>().Single();
        await Until(() => !diff.IsBusy && diff.Comparison is not null); Assert.That(diff.Comparison!.Compare.Revision, Is.EqualTo(f.Topic)); diff.Close();
        Assert.That((await f.Repository.GetRebaseStateAsync()).IsActive, Is.True);
    }

    [AvaloniaTest]
    public async Task ResolverCallbackAndClosingOwnerNeverApplyADeferredContinue()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict(); bool? offeredCommit = null;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RebaseDialog dialog = await f.Show(callback: async (_, repo, offer, token) =>
        { Assert.That(repo.Path, Is.EqualTo(f.Root)); offeredCommit = offer; await gate.Task.WaitAsync(token); });
        string before = await f.Witness(); Click(Field<Button>(dialog, "ResolveRebaseConflicts")); await Until(() => offeredCommit is not null);
        Assert.That(offeredCommit, Is.False); dialog.Close(); gate.TrySetResult(); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.Succeeded || dialog.Aborted, Is.False); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ResolverThatStagesThenClosesPreservesRefreshSignalWithoutContinuingRebase()
    {
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true); await f.StartConflict();
        string head = await f.Text("rev-parse", "HEAD"), refs = await f.Text("show-ref");
        byte[] journal = System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json"));
        byte[] todo = System.IO.File.ReadAllBytes(f.File(".git/rebase-merge/git-rebase-todo"));
        RebaseDialog dialog = await f.Show(callback: async (owner, repo, offer, token) =>
        {
            Assert.That(offer, Is.False);
            f.Write("base.txt", "resolved before closing\n");
            await repo.StageAsync(["base.txt"], token);
            owner.Close();
        });
        Click(Field<Button>(dialog, "ResolveRebaseConflicts")); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.HasRepositoryChanges, Is.True, "Browse must refresh a resolution staged before the dialog closed.");
        Assert.That(dialog.Succeeded || dialog.Aborted, Is.False);
        GitRebaseState state = await f.Repository.GetRebaseStateAsync();
        Assert.That(state.IsActive, Is.True); Assert.That(state.HasConflicts, Is.False); Assert.That(state.CurrentCommit, Is.EqualTo(f.Topic));
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(head)); Assert.That(await f.Text("show-ref"), Is.EqualTo(refs));
        Assert.That(await f.Text("show", ":base.txt"), Is.EqualTo("resolved before closing"));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json")), Is.EqualTo(journal));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/rebase-merge/git-rebase-todo")), Is.EqualTo(todo));
    }

    [AvaloniaTest]
    public async Task FrenchOriginalColumnAndOptionKeysRemainReadable()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR"); using RebaseUiFixture f = await RebaseUiFixture.Create(); RebaseDialog dialog = await f.Show();
            Assert.That(Field<TextBlock>(dialog, "RebaseHeader3").Text, Is.EqualTo("Auteur")); Assert.That(Field<TextBlock>(dialog, "RebaseHeader5").Text, Is.EqualTo("Statut"));
            Assert.That((Field<CheckBox>(dialog, "RebaseInteractive").Content as Avalonia.Controls.Primitives.AccessText)?.Text, Does.Contain("intéractif").IgnoreCase);
            Assert.That(Editor(Field<ComboBox>(dialog, "RebaseOn")).Text, Is.EqualTo("main"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    internal static T Field<T>(Control host, string name) where T : Control => host.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal static TextBox Editor(ComboBox combo) => combo.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PART_EditableTextBox");
    internal static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    internal static bool Ready(Control control)
    {
        TopLevel? top = TopLevel.GetTopLevel(control); if (top is null) { return false; } Pump(top);
        Point? point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        return control.IsEnabled && control.IsEffectivelyVisible && point is Point center && new Rect(default, top.ClientSize).Contains(center)
            && (top.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(control) == true;
    }
    internal static void Click(Control control)
    {
        Assert.That(Ready(control), Is.True, control.Name); TopLevel top = TopLevel.GetTopLevel(control)!;
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(point); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Pump(top);
    }
    internal static async Task Until(Func<bool> condition)
    { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > end) { Assert.Fail("Timed out waiting for native rebase UI."); } await Task.Delay(10); } }
    internal static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = System.IO.Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

internal sealed class RebaseUiFixture : IDisposable
{
    private readonly Dictionary<string, string?> _old = new();
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-rebase-ui-" + Guid.NewGuid().ToString("N"));
    public Window Owner { get; } = new() { Width = 1100, Height = 700 };
    public List<Window> Windows { get; } = [];
    public string Base = "", Main = "", Topic = "";
    public GitRepository Repository => new(Root);
    private RebaseUiFixture()
    {
        foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Root + ".config"), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1") })
        { _old[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        Directory.CreateDirectory(Root);
    }
    public string File(string path) => System.IO.Path.Combine(Root, path);
    public void Write(string path, string text) => System.IO.File.WriteAllText(File(path), text);
    public async Task<GitResult> Git(params string[] args)
    { GitResult result = await new GitRunner().RunAsync(Root, args); if (!result.Success) { throw new GitCommandException(args, result); } return result; }
    public async Task<string> Text(params string[] args) => (await Git(args)).StandardOutput.TrimEnd('\r', '\n');
    public async Task<string> Witness()
    {
        _ = await Repository.GetStatusAsync();
        string head = await Text("rev-parse", "HEAD"), refs = await Text("show-ref"), index = await Text("ls-files", "--stage");
        Dictionary<string, string> files = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(File(".git") + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToDictionary(path => System.IO.Path.GetRelativePath(Root, path), path => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))));
        Dictionary<string, string> journal = Directory.Exists(File(".git/rebase-merge"))
            ? Directory.EnumerateFiles(File(".git/rebase-merge")).Order(StringComparer.Ordinal).ToDictionary(path => System.IO.Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))))
            : [];
        return JsonSerializer.Serialize(new { head, refs, index, files, journal, config = Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/config"))) });
    }
    public static async Task<RebaseUiFixture> Create(bool conflict = false)
    {
        RebaseUiFixture f = new(); await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Rebase UI"); await f.Git("config", "user.email", "rebase@example.invalid");
        f.Write("base.txt", "base\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Base"); f.Base = await f.Text("rev-parse", "HEAD");
        await f.Git("checkout", "-b", "topic"); f.Write(conflict ? "base.txt" : "topic.txt", conflict ? "theirs\n" : "topic\n");
        await f.Git("add", "."); await f.Git("commit", "-m", "Topic"); f.Topic = await f.Text("rev-parse", "HEAD");
        f.Write("later.txt", "later\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Later");
        await f.Git("checkout", "main"); f.Write(conflict ? "base.txt" : "main.txt", conflict ? "ours\n" : "main\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Main");
        f.Main = await f.Text("rev-parse", "HEAD"); await f.Git("checkout", "topic"); f.Owner.Show(); return f;
    }
    public async Task StartConflict()
    {
        GitCommandException? conflict = null;
        try { await Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }, "/usr/bin/true"); }
        catch (GitCommandException error) { conflict = error; }
        Assert.That(conflict, Is.Not.Null, "The native rebase fixture must genuinely stop on its merge conflict.");
    }
    public async Task<RebaseDialog> Show(bool dark = false, bool interactive = false, string? initialFrom = null, string? initialTo = null,
        Func<Window, GitRepository, bool, CancellationToken, Task>? callback = null)
    {
        RebaseDialog dialog = new(Repository, "main", initialFrom, initialTo, interactive, callback) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, EditorExecutable = "/usr/bin/true" };
        Windows.Add(dialog); await dialog.InitializeAsync(); _ = dialog.ShowDialog(Owner); RebaseDialogTests.Pump(dialog); return dialog;
    }
    public GitRebaseEditorRequest Request(GitRebaseEditorKind kind)
    {
        string? previous = Environment.GetEnvironmentVariable("GITEXTENSIONS_REBASE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("GITEXTENSIONS_REBASE_DIRECTORY", File(".git/gitextensions-rebase-native"));
            return GitRebaseEditor.ReadNativeEditorRequest([kind == GitRebaseEditorKind.Todo ? "--edit-rebase-todo" : "--edit-rebase-message",
                kind == GitRebaseEditorKind.Todo ? File(".git/rebase-merge/git-rebase-todo") : File(".git/COMMIT_EDITMSG")]);
        }
        finally { Environment.SetEnvironmentVariable("GITEXTENSIONS_REBASE_DIRECTORY", previous); }
    }
    public NativeRebaseEditorWindow ShowEditor(GitRebaseEditorRequest request, bool dark = false)
    { NativeRebaseEditorWindow editor = new(request) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Windows.Add(editor); editor.Show(Owner); RebaseDialogTests.Pump(editor); return editor; }
    public void Dispose()
    {
        foreach (Window window in Windows) { window.Close(); } Owner.Close(); Dispatcher.UIThread.RunJobs();
        foreach ((string name, string? value) in _old) { Environment.SetEnvironmentVariable(name, value); }
        if (Directory.Exists(Root)) { Directory.Delete(Root, true); } if (Directory.Exists(Root + ".config")) { Directory.Delete(Root + ".config", true); }
    }
}
