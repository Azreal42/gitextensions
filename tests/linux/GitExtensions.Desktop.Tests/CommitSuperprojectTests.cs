using System.Diagnostics;
using System.Globalization;
using System.Text;
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
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitSuperprojectTests
{
    [AvaloniaTest]
    public Task OriginalSuperprojectActionAndInlineBranchRenderInEnglishLight()
        => CaptureCommitAsync("en-US", ThemeVariant.Light, "commit-superproject-en-light.png");

    [AvaloniaTest]
    public Task OriginalSuperprojectActionAndInlineBranchRenderInFrenchDark()
        => CaptureCommitAsync("fr-FR", ThemeVariant.Dark, "commit-superproject-fr-dark.png");

    [AvaloniaTest]
    public async Task OriginalDefaultAndLegacyPreferencesShowTheOptionAndClicksPersistBeforeAnyCommit()
    {
        Assert.That(new CommitPreferences().StageInSuperprojectAfterCommit, Is.True);
        await using Fixture f = await Fixture.CreateAsync(legacyPreferences: true);
        CheckBox stage = Field<CheckBox>(f.Dialog, "StageInSuperproject");
        Assert.That(stage.IsEffectivelyVisible && stage.IsChecked == true, Is.True);
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.True);
        Panel actions = (Panel)stage.Parent!;
        Assert.That(actions.Children.IndexOf(stage), Is.EqualTo(actions.Children.IndexOf(Field<Button>(f.Dialog, "CommitAndPush")) + 1));
        Assert.That(actions.Children.IndexOf(Field<CheckBox>(f.Dialog, "Amend")), Is.EqualTo(actions.Children.IndexOf(stage) + 1));
        Pointer(stage);
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.False, "The checkbox saves immediately, without Commit or OK.");
        Assert.That(f.Dialog.HasCommitted, Is.False);
        await f.ReopenAsync();
        Assert.That(Field<CheckBox>(f.Dialog, "StageInSuperproject").IsChecked, Is.False);
        Pointer(Field<CheckBox>(f.Dialog, "StageInSuperproject"));
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.True);
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task FailedOptOutSaveRestoresTheEffectiveOptionAndShowsTheErrorWithoutCommitting()
    {
        await using Fixture f = await Fixture.CreateAsync();
        CheckBox stage = Field<CheckBox>(f.Dialog, "StageInSuperproject");
        Assert.That(stage.IsChecked, Is.True);
        byte[] originalPreferences = File.ReadAllBytes(f.Preferences.FilePath);
        File.Delete(f.Preferences.FilePath);
        Directory.CreateDirectory(f.Preferences.FilePath);
        try
        {
            Pointer(stage);
            Assert.That(f.Dialog.LastError, Is.Not.Null.And.Not.Empty);
            Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").IsVisible, Is.True);
            Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").Text, Does.Contain(f.Dialog.LastError!));
            Assert.That(stage.IsChecked, Is.True, "A failed save must not advertise a disabled option that will remain effective.");
            Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.True);
            Assert.That(f.Dialog.HasCommitted || f.Dialog.PushCompleted, Is.False);
            Assert.That(await f.HeadAsync(), Is.EqualTo(f.Initial));
            await f.AssertParentAsync(f.Initial);
        }
        finally
        {
            Directory.Delete(f.Preferences.FilePath);
            File.WriteAllBytes(f.Preferences.FilePath, originalPreferences);
        }
    }

    [AvaloniaTest]
    public async Task AStandaloneCheckoutDoesNotOfferTheOptionOrStageAnUnrelatedParent()
    {
        await using Fixture f = await Fixture.CreateAsync(standalone: true);
        Assert.That(Field<CheckBox>(f.Dialog, "StageInSuperproject").IsVisible, Is.False);
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "Commit"));
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True);
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task ACheckoutWithAnIndexGitlinkButNoRegistrationDoesNotInventASuperproject()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.CloseDialogAsync();
        await f.ParentGit("config", "-f", ".gitmodules", "--remove-section", "submodule.selected");
        await f.OpenAsync();
        Assert.That(Field<CheckBox>(f.Dialog, "StageInSuperproject").IsVisible, Is.False);
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "Commit"));
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That(await f.IndexGitlinkAsync(), Is.EqualTo(Fixture.Gitlink(f.Initial)));
        await f.AssertUnrelatedParentStateAsync(checkWorktree: false);
    }

    [AvaloniaTest]
    public async Task SuccessfulCommitStagesOnlyTheExactGitlinkBeforeAutomaticClosure()
    {
        await using Fixture f = await Fixture.CreateAsync(close: true);
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "Commit"));
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.IsVisible, Is.False, f.Dialog.LastError);
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True);
        Assert.That(f.Dialog.LastError, Is.Null);
        string committed = await f.HeadAsync();
        Assert.That(committed, Is.Not.EqualTo(f.Initial));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(f.Initial), "Commit alone does not publish the child or parent.");
        await f.AssertParentAsync(committed);
    }

    [AvaloniaTest]
    public async Task DisablingTheOptionKeepsTheParentIndexWhileTheChildCommitStillSucceeds()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Pointer(Field<CheckBox>(f.Dialog, "StageInSuperproject"));
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "Commit"));
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(await f.HeadAsync(), Is.Not.EqualTo(f.Initial));
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.False);
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task AnEmptyCommitStagesItsNewHeadRatherThanThePreviouslyIndexedCommit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Field<TextBox>(f.Dialog, "Message").Text = "Empty child commit";
        Pointer(Field<Button>(f.Dialog, "Commit"));
        await Until(() => f.Dialog.OwnedWindows.OfType<CommitEmptyDialog>().Any());
        CommitEmptyDialog confirmation = f.Dialog.OwnedWindows.OfType<CommitEmptyDialog>().Single();
        Pointer(Field<Button>(confirmation, "MakeEmpty"));
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        Assert.That(await f.ChildText("diff", "HEAD^", "HEAD", "--stat"), Is.Empty);
        await f.AssertParentAsync(await f.HeadAsync());
    }

    [AvaloniaTest]
    public async Task AmendingStagesTheRewrittenHeadWithoutMakingASecondChildOrParentCommit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.StageChangeAsync(); Pointer(Field<CheckBox>(f.Dialog, "Amend"));
        Pointer(Field<Button>(f.Dialog, "Commit"));
        await Until(() => f.Dialog.OwnedWindows.Any());
        Window confirmation = f.Dialog.OwnedWindows.Single();
        Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
        await f.FinishedCommitAsync();
        string amended = await f.HeadAsync();
        Assert.That(amended, Is.Not.EqualTo(f.Initial));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("1"));
        Assert.That(f.Dialog.LastError, Is.Null);
        await f.AssertParentAsync(amended);
    }

    [AvaloniaTest]
    public async Task RejectedPushNeverStagesTheSuccessfulChildCommitEvenWhenCommitCloses()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync(close: true);
        await f.WriteHookAsync("pre-push", "printf 'private superproject push refusal\\n' >&2\nexit 1\n");
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        PushDialog push = await f.RejectedPushAsync();
        string committed = await f.HeadAsync();
        Assert.That(f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
        Assert.That(push.LastError, Does.Contain("private superproject push refusal"));
        await f.AssertParentAsync(f.Initial);
        Escape(push);
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
        Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(f.Initial));
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task RetryingARejectedPushStagesOnceOnlyAfterItReallyPublishesWithoutReplayingCommit()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync();
        string hook = await f.WriteHookAsync("pre-push", "printf 'retry this private push\\n' >&2\nexit 1\n");
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        PushDialog push = await f.RejectedPushAsync();
        string committed = await f.HeadAsync();
        await f.AssertParentAsync(f.Initial);
        File.Delete(hook); Pointer(Field<Button>(push, "ExecutePush"));
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !push.IsVisible && !f.Dialog.IsBusy);
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        await f.AssertParentAsync(committed);
    }

    [AvaloniaTest]
    public async Task PushRecoveryWithRebaseStagesTheFinalRewrittenHeadAfterThePullAcknowledgment()
    {
        await using Fixture f = await Fixture.CreateAsync(close: true);
        await f.AdvanceRemoteAsync();
        string remote = await f.RemoteHeadAsync();
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => PushProcessTestDriver.Windows(f.Dialog).Any(window => window.Name == "PushRejectedConfirmation"));
        string beforeRebase = await f.HeadAsync();
        await f.AssertParentAsync(f.Initial);
        Window rejection = PushProcessTestDriver.Windows(f.Dialog).Single(window => window.Name == "PushRejectedConfirmation");
        Pointer(Field<Button>(rejection, "PushChoice2"));
        await Until(() => PushProcessTestDriver.Windows(f.Dialog).OfType<PullDialog>()
            .Any(pull => pull.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning)));
        PullDialog pull = PushProcessTestDriver.Windows(f.Dialog).OfType<PullDialog>().Single();
        GitProcessDialog process = pull.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(process.Outcome!.Success && process.Result!.Success, Is.True);
        Assert.That(f.Dialog.IsBusy && f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(remote));
        await f.AssertParentAsync(f.Initial);
        Pointer(Field<Button>(process, "Ok"));
        await f.FinishedCommitAsync();
        string rebased = await f.HeadAsync();
        Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That(f.Dialog.PushCompleted, Is.True);
        Assert.That(rebased, Is.Not.EqualTo(beforeRebase));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(rebased));
        Assert.That(await f.ChildText("rev-parse", "HEAD^"), Is.EqualTo(remote));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("3"));
        await f.AssertParentAsync(rebased);
    }

    [AvaloniaTest]
    public async Task CancellingDuringPushDoesNotStageOrUndoTheChildCommit()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync();
        string started = f.Output("push-started");
        await f.WriteHookAsync("pre-push", "printf started > " + Quote(started) + "\nsleep 30\n");
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => File.Exists(started));
        string committed = await f.HeadAsync();
        Assert.That(committed, Is.Not.EqualTo(f.Initial));
        await f.AssertParentAsync(f.Initial);
        f.Dialog.Close();
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
        Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(f.Initial));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        Assert.That(f.Dialog.OwnedWindows, Is.Empty);
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task ThePersistedOptionIsReadAfterPushSoAnotherCommitWindowCanDisableStaging()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync();
        string started = f.Output("option-push-started"), release = f.Output("option-push-release");
        await f.BlockPushAsync(started, release);
        CommitDialog other = await f.OpenAdditionalAsync();
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => File.Exists(started));
        Assert.That(Field<CheckBox>(other, "StageInSuperproject").IsChecked, Is.True);
        Pointer(Field<CheckBox>(other, "StageInSuperproject"));
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.False);
        await f.CloseAsync(other);
        File.WriteAllText(release, "release\n");
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(await f.HeadAsync()));
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task CorruptedPreferencesDuringPushCannotSilentlyReenableASavedOptOut()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync(close: true);
        Pointer(Field<CheckBox>(f.Dialog, "StageInSuperproject"));
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.False);
        string started = f.Output("corrupt-preferences-push-started"), release = f.Output("corrupt-preferences-push-release");
        await f.BlockPushAsync(started, release);
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => File.Exists(started));
        string committed = await f.HeadAsync();
        Assert.That(committed, Is.Not.EqualTo(f.Initial));
        Assert.That(f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.False);
        byte[] corruptPreferences = Encoding.UTF8.GetBytes("{\"StageInSuperprojectAfterCommit\": false, private interrupted profile\n");
        File.WriteAllBytes(f.Preferences.FilePath, corruptPreferences);
        File.WriteAllText(release, "release\n");
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True);
        Assert.That(f.Dialog.IsVisible, Is.True, "The unreadable opt-out must bypass automatic close after the successful push.");
        Assert.That(f.Dialog.LastError, Is.Not.Null.And.Not.Empty);
        Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").IsVisible, Is.True);
        Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").Text, Does.Contain(f.Dialog.LastError!));
        Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        Assert.That(File.ReadAllBytes(f.Preferences.FilePath), Is.EqualTo(corruptPreferences), "Do not replace corrupt user preferences with defaults.");
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task APreferencesPathThatBecomesADirectoryDuringPushCannotSilentlyReenableASavedOptOut()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync(close: true);
        Pointer(Field<CheckBox>(f.Dialog, "StageInSuperproject"));
        Assert.That(f.Preferences.Load().StageInSuperprojectAfterCommit, Is.False);
        string started = f.Output("unreadable-preferences-push-started"), release = f.Output("unreadable-preferences-push-release");
        await f.BlockPushAsync(started, release);
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        string committed;
        try
        {
            await Until(() => File.Exists(started));
            committed = await f.HeadAsync();
            Assert.That(committed, Is.Not.EqualTo(f.Initial));
            Assert.That(f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
            File.Delete(f.Preferences.FilePath); Directory.CreateDirectory(f.Preferences.FilePath);
        }
        finally { File.WriteAllText(release, "release\n"); }
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True);
        Assert.That(f.Dialog.IsVisible, Is.True, "A profile IO error must bypass automatic close after the successful push.");
        Assert.That(f.Dialog.LastError, Is.Not.Null.And.Not.Empty);
        Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").IsVisible, Is.True);
        Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").Text, Does.Contain(f.Dialog.LastError!));
        Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        Assert.That(Directory.Exists(f.Preferences.FilePath), Is.True);
        Assert.That(Directory.EnumerateFileSystemEntries(f.Preferences.FilePath), Is.Empty, "Do not replace unreadable preferences with defaults.");
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task ParentIndexLockShowsThePartialFailureAndRefreshDoesNotReplayCommitOrStaging()
    {
        await using Fixture f = await Fixture.CreateAsync(close: true);
        string indexLock = Path.Combine(f.ParentPath, ".git", "index.lock");
        byte[] lockBytes = Encoding.UTF8.GetBytes("private parent lock\n");
        File.WriteAllBytes(indexLock, lockBytes);
        try
        {
            await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "Commit"));
            await f.FinishedCommitAsync();
            Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True);
            Assert.That(f.Dialog.IsVisible, Is.True, "The parent error must bypass automatic close.");
            Assert.That(f.Dialog.LastError, Is.Not.Null.And.Not.Empty);
            Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").IsVisible, Is.True);
            Assert.That(Field<TextBox>(f.Dialog, "Message").Text, Is.Empty, "The successful commit already consumed the draft.");
            Assert.That(File.ReadAllBytes(indexLock), Is.EqualTo(lockBytes), "A user-owned parent lock is not deleted.");
            string committed = await f.HeadAsync();
            Assert.That(committed, Is.Not.EqualTo(f.Initial));
            await f.AssertParentAsync(f.Initial);
            File.Delete(indexLock);
            await f.Dialog.RefreshAsync();
            await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy);
            Assert.That(f.Dialog.IsVisible, Is.True);
            Assert.That(f.Dialog.HasCommitted, Is.True);
            Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
            Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
            await f.AssertParentAsync(f.Initial);
        }
        finally { File.Delete(indexLock); }
    }

    [AvaloniaTest]
    public async Task RegistrationChangedDuringPushIsRejectedAfterPublishingWithoutClosingOrReplayingCommit()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync(close: true);
        string started = f.Output("registration-push-started"), release = f.Output("registration-push-release");
        await f.BlockPushAsync(started, release);
        await f.StageChangeAsync(); Pointer(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => File.Exists(started));
        string committed = await f.HeadAsync();
        await f.ParentGit("config", "-f", ".gitmodules", "submodule.selected.path", "replacement λ");
        File.WriteAllText(release, "release\n");
        await f.FinishedCommitAsync();
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True);
        Assert.That(f.Dialog.IsVisible, Is.True, "Registration failure must bypass automatic close after the successful push.");
        Assert.That(f.Dialog.LastError, Is.Not.Null.And.Not.Empty);
        Assert.That(Field<TextBox>(f.Dialog, "CommitOutput").IsVisible, Is.True);
        Assert.That(await f.HeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.RemoteHeadAsync(), Is.EqualTo(committed));
        Assert.That(await f.ChildText("rev-list", "--count", "HEAD"), Is.EqualTo("2"));
        Assert.That(await f.IndexGitlinkAsync(), Is.EqualTo(Fixture.Gitlink(f.Initial)));
        await f.AssertUnrelatedParentStateAsync(checkWorktree: false);
        Assert.That(await f.ParentText("config", "-f", ".gitmodules", "submodule.selected.path"), Is.EqualTo("replacement λ"));
    }

    [AvaloniaTest]
    public async Task ARejectedNativeCommitLeavesBothTheChildIndexAndParentGitlinkUnconsumed()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync(close: true);
        await f.WriteHookAsync("pre-commit", "printf 'private child commit refusal\\n' >&2\nexit 1\n");
        await f.StageChangeAsync();
        string index = await f.ChildText("ls-files", "--stage", "-z");
        Pointer(Field<Button>(f.Dialog, "Commit"));
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy && f.Dialog.LastError is not null);
        Assert.That(f.Dialog.HasCommitted || f.Dialog.PushCompleted, Is.False);
        Assert.That(f.Dialog.LastError, Does.Contain("private child commit refusal"));
        Assert.That(f.Dialog.IsVisible, Is.True);
        Assert.That(await f.HeadAsync(), Is.EqualTo(f.Initial));
        Assert.That(await f.ChildText("ls-files", "--stage", "-z"), Is.EqualTo(index));
        Assert.That(Field<TextBox>(f.Dialog, "Message").Text, Is.EqualTo(Fixture.Message));
        await f.AssertParentAsync(f.Initial);
    }

    [AvaloniaTest]
    public async Task ABeforeCommitVetoPreservesItsOwnEffectsButNeverStagesOrCreatesAChildCommit()
    {
        RequireExecutableHooks();
        await using Fixture f = await Fixture.CreateAsync(close: true);
        string marker = f.Output("before-commit-ran");
        string script = f.Output("before-commit.sh");
        File.WriteAllText(script, "#!/bin/sh\nprintf before > " + Quote(marker) + "\ngit update-ref refs/heads/veto-effect HEAD\nexit 7\n");
        new ScriptsPreferencesStore().Save([new ScriptDefinition
        {
            Name = "Private superproject veto", Command = "/bin/sh", Arguments = "\"" + script + "\"", OnEvent = ScriptEvent.BeforeCommit
        }]);
        await f.StageChangeAsync();
        string index = await f.ChildText("ls-files", "--stage", "-z");
        Pointer(Field<Button>(f.Dialog, "Commit"));
        await Until(() => File.Exists(marker));
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy);
        Assert.That(f.Dialog.HasCommitted || f.Dialog.PushCompleted, Is.False);
        Assert.That(f.Dialog.ScriptMayHaveChangedRepository, Is.True);
        Assert.That(f.Dialog.IsVisible, Is.True);
        Assert.That(await f.HeadAsync(), Is.EqualTo(f.Initial));
        Assert.That(await f.ChildText("rev-parse", "refs/heads/veto-effect"), Is.EqualTo(f.Initial));
        Assert.That(await f.ChildText("ls-files", "--stage", "-z"), Is.EqualTo(index));
        await f.AssertParentAsync(f.Initial);
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
    private static void RequireExecutableHooks()
    { if (!OperatingSystem.IsLinux()) { Assert.Ignore("Executable Git hooks require Linux."); } }
    private static void Escape(Window window)
    { window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Superproject commit workflow did not settle.");
    }
    private static async Task CaptureCommitAsync(string culture, ThemeVariant theme, string fileName)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using Fixture f = await Fixture.CreateAsync();
            f.Dialog.RequestedThemeVariant = theme;
            f.Dialog.Width = 1100;
            await f.StageChangeAsync();
            for (int frame = 0; frame < 10; frame++)
            { Dispatcher.UIThread.RunJobs(); f.Dialog.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            CheckBox stage = Field<CheckBox>(f.Dialog, "StageInSuperproject");
            Assert.That(stage.IsEffectivelyVisible && stage.IsChecked == true, Is.True);
            Menu commitToolbar = Field<Menu>(f.Dialog, "CommitToolbar");
            CompactMenuToolbar overflow = CompactMenuToolbar.For(commitToolbar)!;
            MenuItem create = overflow.CanonicalItems.Single(item => item.Name == "CommitCreateBranch");
            Control createPresentation = create;
            if (overflow.IsOverflowed(create))
            {
                Pointer(overflow.Overflow); createPresentation = overflow.ProxyFor(create);
                Assert.That(overflow.Overflow.IsSubMenuOpen, Is.True);
            }
            TopLevel presentationRoot = TopLevel.GetTopLevel(createPresentation)!;
            Point createPoint = createPresentation.TranslatePoint(new Point(createPresentation.Bounds.Width / 2, createPresentation.Bounds.Height / 2), presentationRoot)!.Value;
            Visual? createHit = presentationRoot.InputHitTest(createPoint) as Visual;
            Assert.That(createPresentation.IsEffectivelyVisible && createPresentation.IsEffectivelyEnabled
                && new Rect(default, presentationRoot.ClientSize).Contains(createPoint)
                && createHit?.GetSelfAndVisualAncestors().Contains(createPresentation) == true, Is.True,
                "Create branch must be reachable by a real pointer either inline or in the opened overflow menu.");
            overflow.Overflow.Close(); Dispatcher.UIThread.RunJobs();
            Assert.That(f.Dialog.ActualThemeVariant, Is.EqualTo(theme));
            using var rendered = f.Dialog.CaptureRenderedFrame();
            Assert.That(rendered, Is.Not.Null);
            string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName); rendered!.Save(path); TestContext.AddTestAttachment(path);
            AssertCommitActionGeometry(f.Dialog, stage);
            await f.AssertParentAsync(f.Initial);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static void AssertCommitActionGeometry(CommitDialog window, CheckBox stage)
    {
        Rect Bounds(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
        static void Inside(Rect outer, Rect inner, string description)
        {
            Assert.That(inner.Left, Is.GreaterThanOrEqualTo(outer.Left - .5), description + " left");
            Assert.That(inner.Top, Is.GreaterThanOrEqualTo(outer.Top - .5), description + " top");
            Assert.That(inner.Right, Is.LessThanOrEqualTo(outer.Right + .5), description + " right");
            Assert.That(inner.Bottom, Is.LessThanOrEqualTo(outer.Bottom + .5), description + " bottom");
        }
        Assert.That(window.ClientSize.Width, Is.EqualTo(1100).Within(.5));
        TextBlock caption = stage.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == stage.Content?.ToString());
        Assert.That(caption.IsEffectivelyVisible, Is.True);
        Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, "The complete translated checkbox caption must remain visible.");
        Assert.That(caption.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(caption.Bounds.Width + .5), "The checkbox must have enough actual text width for its complete caption.");
        Assert.That(caption.TextLayout.Height, Is.LessThanOrEqualTo(caption.Bounds.Height + .5), "The translated caption must not be vertically clipped.");
        Inside(Bounds(stage), Bounds(caption), "Checkbox caption inside its control");
        Panel actions = (Panel)stage.Parent!;
        Rect actionsBounds = Bounds(actions), containerBounds = Bounds((Control)actions.Parent!);
        Inside(new Rect(default, window.ClientSize), actionsBounds, "Action panel inside the window");
        Inside(containerBounds, actionsBounds, "Action panel inside the editor region");
        foreach (Control action in actions.Children.Where(action => action.IsEffectivelyVisible))
        { Inside(actionsBounds, Bounds(action), "Visible action " + action.Name); }
        TextBox message = Field<TextBox>(window, "Message");
        Rect messageBounds = Bounds(message);
        Assert.That(message.IsEffectivelyVisible && message.IsEffectivelyEnabled, Is.True);
        Assert.That(messageBounds.Width, Is.GreaterThanOrEqualTo(240), "The translated actions must leave a useful editor width at 1100px.");
        Assert.That(messageBounds.Height, Is.GreaterThanOrEqualTo(120), "The commit message must retain a useful multiline editing area.");
        Assert.That(actionsBounds.Right, Is.LessThanOrEqualTo(messageBounds.Left), "The action panel must not overlap the message editor.");
        Inside(containerBounds, messageBounds, "Message editor inside its region");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string ModulePath = "modules/child [x]* λ";
        internal const string Message = "Committed child for superproject";
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly GitRunner _runner = new();
        private readonly List<CommitDialog> _dialogs = [];
        private readonly List<string> _releaseFiles = [];
        private readonly Dictionary<string, byte[]> _parentWorktree = [];
        private string _otherIndex = "", _otherFlags = "", _parentHead = "";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-commit-superproject-ui-" + Guid.NewGuid().ToString("N"));
        internal string ParentPath => Path.Combine(Root, "parent λ");
        private string Remote => Path.Combine(Root, "child remote λ.git");
        internal GitRepository Repository { get; private set; } = null!;
        internal CommitDialog Dialog { get; private set; } = null!;
        internal CommitPreferencesStore Preferences { get; private set; } = null!;
        internal string Initial { get; private set; } = "";
        internal string Output(string name) => Path.Combine(Root, name);
        internal static string Gitlink(string head) => "160000 " + head + " 0\t" + ModulePath + "\0";

        internal static async Task<Fixture> CreateAsync(bool close = false, bool legacyPreferences = false, bool standalone = false)
        {
            Fixture f = new();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", f.Output("config"));
            try
            {
                Directory.CreateDirectory(f.Root);
                await f.GitAt(f.Root, "init", "--bare", "-b", "main", f.Remote);
                string seed = f.Output("standalone child"); Directory.CreateDirectory(seed);
                await f.GitAt(seed, "init", "-b", "main"); await f.IdentityAsync(seed);
                File.WriteAllText(Path.Combine(seed, "base.txt"), "base\n");
                await f.GitAt(seed, "add", "."); await f.GitAt(seed, "commit", "-m", "Initial child");
                await f.GitAt(seed, "remote", "add", "origin", f.Remote); await f.GitAt(seed, "push", "-u", "origin", "main");
                Directory.CreateDirectory(f.ParentPath);
                await f.GitAt(f.ParentPath, "init", "-b", "main"); await f.IdentityAsync(f.ParentPath);
                foreach (string name in new[] { "parent-staged.txt", "parent-worktree.txt", "parent-assume.txt", "parent-skip.txt" })
                { File.WriteAllText(Path.Combine(f.ParentPath, name), "original " + name + "\n"); }
                await f.ParentGit("-c", "protocol.file.allow=always", "submodule", "add", "--name", "selected", "--", f.Remote, ModulePath);
                await f.ParentGit("add", "."); await f.ParentGit("commit", "-m", "Initial parent with child");
                string child = Path.Combine(f.ParentPath, ModulePath); await f.IdentityAsync(child);
                f.Repository = new(standalone ? seed : child);
                string hooks = await f.ChildText("rev-parse", "--path-format=absolute", "--git-path", "hooks");
                await f.GitAt(f.Repository.Path, "config", "core.hooksPath", hooks);
                f.Initial = await f.HeadAsync();
                File.WriteAllText(Path.Combine(f.ParentPath, "parent-staged.txt"), "already staged parent bytes\n");
                await f.ParentGit("add", "--", "parent-staged.txt");
                File.WriteAllText(Path.Combine(f.ParentPath, "parent-staged.txt"), "later unstaged parent bytes\n");
                File.WriteAllText(Path.Combine(f.ParentPath, "parent-worktree.txt"), "unstaged parent work\n");
                File.WriteAllText(Path.Combine(f.ParentPath, "parent-untracked.txt"), "private untracked parent bytes\n");
                await f.ParentGit("update-index", "--assume-unchanged", "--", "parent-assume.txt");
                await f.ParentGit("update-index", "--skip-worktree", "--", "parent-skip.txt");
                f._parentHead = await f.ParentText("rev-parse", "HEAD");
                f._otherIndex = await f.OtherIndexAsync(); f._otherFlags = await f.OtherFlagsAsync();
                foreach (string name in new[] { ".gitmodules", "parent-staged.txt", "parent-worktree.txt", "parent-assume.txt", "parent-skip.txt", "parent-untracked.txt" })
                { f._parentWorktree[name] = File.ReadAllBytes(Path.Combine(f.ParentPath, name)); }
                f.Preferences = new();
                f.Preferences.Save(new() { CloseAfterEachCommit = close, CloseWhenAllChangesCommitted = close });
                if (legacyPreferences)
                { File.WriteAllText(f.Preferences.FilePath, "{\"CloseAfterEachCommit\":false,\"CloseWhenAllChangesCommitted\":false}"); }
                await f.OpenAsync(); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task IdentityAsync(string path)
        {
            await GitAt(path, "config", "user.name", "Commit Superproject UI");
            await GitAt(path, "config", "user.email", "commit-superproject@example.invalid");
            await GitAt(path, "config", "commit.gpgsign", "false");
            await GitAt(path, "config", "core.hooksPath", Output("no-hooks"));
        }
        internal async Task OpenAsync() => Dialog = await OpenAdditionalAsync();
        internal async Task<CommitDialog> OpenAdditionalAsync()
        {
            CommitDialog dialog = new(Repository, Preferences) { RequestedThemeVariant = ThemeVariant.Light };
            _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.That(dialog.LastError, Is.Null); return dialog;
        }
        internal async Task ReopenAsync() { await CloseDialogAsync(); await OpenAsync(); }
        internal Task CloseDialogAsync() => CloseAsync(Dialog);
        internal async Task CloseAsync(CommitDialog dialog)
        {
            CloseTree(dialog);
            await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && !dialog.IsVisible);
        }
        internal async Task StageChangeAsync()
        {
            File.WriteAllText(Path.Combine(Repository.Path, "local.txt"), "local child change\n");
            await GitAt(Repository.Path, "add", "--", "local.txt");
            await Dialog.RefreshAsync(); await PushProcessTestDriver.UntilAsync(Dialog, () => !Dialog.IsBusy);
            Field<TextBox>(Dialog, "Message").Text = Message; Dispatcher.UIThread.RunJobs();
        }
        internal Task FinishedCommitAsync() => PushProcessTestDriver.UntilAsync(Dialog, () => Dialog.HasCommitted && !Dialog.IsBusy,
            "Child commit and optional push/staging finished", DescribeAsync);
        internal async Task<PushDialog> RejectedPushAsync()
        {
            await PushProcessTestDriver.UntilAsync(Dialog, () => Dialog.OwnedWindows.OfType<PushDialog>().Any(push => push.IsReady && !push.IsBusy),
                "Rejected push waits for the user", DescribeAsync);
            return Dialog.OwnedWindows.OfType<PushDialog>().Single();
        }
        internal async Task AdvanceRemoteAsync()
        {
            string peer = Output("peer"); await GitAt(Root, "clone", Remote, peer); await IdentityAsync(peer);
            File.WriteAllText(Path.Combine(peer, "remote.txt"), "remote change\n");
            await GitAt(peer, "add", "."); await GitAt(peer, "commit", "-m", "Remote change"); await GitAt(peer, "push");
        }
        internal async Task<string> WriteHookAsync(string name, string body)
        {
            string hooks = await ChildText("rev-parse", "--path-format=absolute", "--git-path", "hooks");
            Directory.CreateDirectory(hooks); string path = Path.Combine(hooks, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return path;
        }
        internal Task BlockPushAsync(string started, string release)
        {
            _releaseFiles.Add(release);
            return WriteHookAsync("pre-push", "printf started > " + Quote(started) + "\nwhile test ! -f " + Quote(release) + "; do sleep 0.05; done\n");
        }
        internal async Task AssertParentAsync(string expectedHead)
        {
            Assert.That(await IndexGitlinkAsync(), Is.EqualTo(Gitlink(expectedHead)), "Only the selected stage-zero gitlink follows the final child HEAD.");
            await AssertUnrelatedParentStateAsync();
        }
        internal async Task AssertUnrelatedParentStateAsync(bool checkWorktree = true)
        {
            Assert.That(await ParentText("rev-parse", "HEAD"), Is.EqualTo(_parentHead), "The parent is staged, never committed.");
            Assert.That(await OtherIndexAsync(), Is.EqualTo(_otherIndex), "All other index entries keep their original blobs, modes and stages.");
            Assert.That(await OtherFlagsAsync(), Is.EqualTo(_otherFlags), "Unrelated assume-unchanged and skip-worktree flags survive.");
            foreach ((string name, byte[] bytes) in _parentWorktree.Where(item => checkWorktree || item.Key != ".gitmodules"))
            { Assert.That(File.ReadAllBytes(Path.Combine(ParentPath, name)), Is.EqualTo(bytes), "Parent worktree: " + name); }
        }
        internal async Task<string> IndexGitlinkAsync() => (await ParentGit("--literal-pathspecs", "ls-files", "--stage", "-z", "--", ModulePath)).StandardOutput;
        private async Task<string> OtherIndexAsync() => WithoutGitlink((await ParentGit("ls-files", "--stage", "-z")).StandardOutput, "\t" + ModulePath);
        private async Task<string> OtherFlagsAsync() => WithoutGitlink((await ParentGit("ls-files", "-v", "-z")).StandardOutput, " " + ModulePath);
        private static string WithoutGitlink(string text, string suffix) => string.Join('\0', text.Split('\0').Where(entry => !entry.EndsWith(suffix, StringComparison.Ordinal)));
        internal async Task<string> ChildText(params string[] arguments) => (await GitAt(Repository.Path, arguments)).StandardOutput.TrimEnd('\r', '\n');
        internal async Task<string> ParentText(params string[] arguments) => (await ParentGit(arguments)).StandardOutput.TrimEnd('\r', '\n');
        internal Task<string> HeadAsync() => ChildText("rev-parse", "HEAD");
        internal async Task<string> RemoteHeadAsync() => (await GitAt(Remote, "rev-parse", "refs/heads/main")).StandardOutput.Trim();
        internal Task<GitResult> ParentGit(params string[] arguments) => GitAt(ParentPath, arguments);
        private async Task<GitResult> GitAt(string path, params string[] arguments)
        {
            GitResult result = await _runner.RunAsync(path, arguments);
            Assert.That(result.Success, Is.True, string.Join(" ", arguments) + "\n" + result.StandardError); return result;
        }
        private async Task<string> DescribeAsync() => $"Commit visible={Dialog.IsVisible}, busy={Dialog.IsBusy}, committed={Dialog.HasCommitted}, push={Dialog.PushCompleted}, error={Dialog.LastError}\n"
            + "Child HEAD=" + await HeadAsync() + "\nRemote HEAD=" + await RemoteHeadAsync() + "\nParent gitlink=" + await IndexGitlinkAsync();
        private static void CloseTree(Window window)
        { foreach (Window child in window.OwnedWindows.ToArray()) { CloseTree(child); } if (window.IsVisible) { window.Close(); } }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (CommitDialog dialog in _dialogs.AsEnumerable().Reverse()) { CloseTree(dialog); }
                foreach (string release in _releaseFiles) { File.WriteAllText(release, "release during fixture cleanup\n"); }
                foreach (CommitDialog dialog in _dialogs.AsEnumerable().Reverse())
                { await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && !dialog.IsVisible); }
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg);
                if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
            }
        }
    }
}
