using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ConfirmationsFourPoliciesTests
{
    [AvaloniaTest]
    public async Task RealApplyReopenUsesThePositiveCheckoutMappingAndPreservesLegacyAndUnknownProfileKeys()
    {
        using PortableHelperSettingsFixture fixture = new();
        string profile = Profile("reference-preferences.json"); Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "{\"FuturePeer\":{\"keep\":[1,true,\"Ω\"]},\"ReapplyStash\":true}");
        (DesktopSettingsDialog settings, Task<bool> completion) = fixture.Open("Confirmations");
        CheckBox checkout = Field<CheckBox>(settings, "chkBranchCheckoutConfirmation");
        Assert.That(checkout.IsChecked, Is.False, "This one Windows policy is positive, unlike the other choices.");
        Set(settings, "chkBranchCheckoutConfirmation", true); Set(settings, "chkBranchDeleteUnmerged", false);
        Set(settings, "chkAutoPopStashAfterCheckout", false); Set(settings, "chkSecondAbortConfirmation", false);
        Click(settings, "ApplyDesktopSettings");
        await Until(settings, () => settings.Navigation.IsEnabled && fixture.Publications == 1);
        ConfirmationsSettingsValues accepted = new ConfirmationsSettingsSession().Initial;
        Assert.That(accepted.ConfirmBranchCheckout && accepted.DontConfirmDeleteUnmergedBranch && accepted.DontConfirmSecondAbortConfirmation, Is.True);
        Assert.That(accepted.AutoPopStashAfterCheckoutBranch, Is.True);
        ReferencePreferencesStore legacy = new(); legacy.Save(legacy.Load() with { AlwaysShowCheckoutDialog = true });
        JsonObject afterLegacy = JsonNode.Parse(File.ReadAllText(profile))!.AsObject();
        Assert.That(afterLegacy["FuturePeer"]!["keep"]![0]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(afterLegacy["FuturePeer"]!["keep"]![1]!.GetValue<bool>(), Is.True);
        Assert.That(afterLegacy["FuturePeer"]!["keep"]![2]!.GetValue<string>(), Is.EqualTo("Ω"));
        Assert.That(afterLegacy["ReapplyStash"]!.GetValue<bool>(), Is.True);
        Assert.That(new ConfirmationsSettingsSession().Initial, Is.EqualTo(accepted), "The six-key checkout writer must keep all confirmation fields.");
        byte[] bytes = File.ReadAllBytes(profile); Set(settings, "chkBranchCheckoutConfirmation", false);
        Click(settings, "CancelDesktopSettings"); Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(File.ReadAllBytes(profile), Is.EqualTo(bytes));
        (DesktopSettingsDialog reopened, Task<bool> reopenedCompletion) = fixture.Open("Confirmations");
        Assert.That(Field<CheckBox>(reopened, "chkBranchCheckoutConfirmation").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(reopened, "chkAutoPopStashAfterCheckout").IsChecked, Is.False);
        Click(reopened, "CancelDesktopSettings"); await reopenedCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [AvaloniaTest]
    public void NullableCheckoutIntentionRejectsAConflictingPeerWithoutRewritingAnyOfTheThirteenExistingChoices()
    {
        using PortableHelperSettingsFixture fixture = new();
        ConfirmationsSettingsSession opened = new(), peer = new();
        peer.Save(peer.Initial with { AutoPopStashAfterCheckoutBranch = false, DontConfirmAmend = true });
        string path = Profile("reference-preferences.json"); byte[] bytes = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => opened.Save(opened.Initial with { AutoPopStashAfterCheckoutBranch = true }));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmAmend, Is.True);
        Assert.That(new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task LeftPanelUsesOneRealDoubleClickAndThePositivePolicyOnlyWhenEnabled(bool confirm)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic");
        ConfirmationsSettingsSession preferences = new(); preferences.Save(preferences.Initial with { ConfirmBranchCheckout = confirm });
        MainWindow main = await scope.MainAsync(); string before = await scope.Head();
        DoubleClick(main, ReferenceNode(main, "refs/heads/topic"));
        if (confirm)
        {
            Window question = await Plain(main);
            Assert.That(question.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("topic", StringComparison.Ordinal) == true), Is.True);
            Assert.That(question.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault).Content, Is.EqualTo("Yes"));
            Answer(question, true);
        }
        await Until(main, () => !main.IsBusy && main.OwnedWindows.Count == 0);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("topic"));
        Assert.That(await scope.Head(), Is.EqualTo(before)); Assert.That(main.LastException, Is.Null);
        Assert.That(new ConfirmationsSettingsSession().Initial.ConfirmBranchCheckout, Is.EqualTo(confirm));
        TreeViewItem selected = ReferenceNode(main, "refs/heads/topic");
        PushProcessTestDriver.Pointer(((Control)selected.Header!).GetVisualDescendants().OfType<TextBlock>().Single());
        Assert.That(selected.IsKeyboardFocusWithin, Is.True, "The first click after the owned decision must focus the original navigation row.");
    }

    [AvaloniaTest]
    [TestCase("refs/heads/main", false)] [TestCase("refs/heads/main", true)]
    [TestCase("refs/remotes/origin/remote-topic", false)] [TestCase("refs/remotes/origin/remote-topic", true)]
    public async Task CurrentAndRemoteLeftPanelChoicesRespectNoOrWindowDismissWithoutChangingRefs(string reference, bool dismiss)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("update-ref", "refs/remotes/origin/remote-topic", "HEAD");
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with { ConfirmBranchCheckout = true });
        MainWindow main = await scope.MainAsync(); string refs = (await scope.Git.GitAsync("show-ref")).StandardOutput;
        Field<TextBox>(main, "RepositoryNavigationSearch").Text = reference.Contains("remotes", StringComparison.Ordinal) ? "remote-topic" : "main";
        PortableHelperSettingsFixture.Pump(main); DoubleClick(main, ReferenceNode(main, reference));
        Window prompt = await Plain(main); if (dismiss) { prompt.Close(); } else { Answer(prompt, false); }
        await Until(main, () => !main.IsBusy && main.OwnedWindows.Count == 0);
        Assert.That((await scope.Git.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That(Field<TextBox>(main, "RepositoryNavigationSearch").Text, Is.Not.Empty);
        Assert.That(new ConfirmationsSettingsSession().Initial.ConfirmBranchCheckout, Is.True);
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task FolderExpanderAndToolbarKeepTheirOwnGesturesWhenLeftPanelConfirmationIsEnabled()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "folder/topic");
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with { ConfirmBranchCheckout = true });
        MainWindow main = await scope.MainAsync();
        TreeViewItem folder = Navigation(main).Single(node => node.Tag is not GitReference && node.Items.OfType<TreeViewItem>().Any(child => child.Tag is GitReference branch && branch.ShortName == "folder/topic"));
        ToggleButton expander = folder.GetVisualDescendants().OfType<ToggleButton>().First();
        PushProcessTestDriver.Pointer(expander); PortableHelperSettingsFixture.Pump(main);
        Assert.That(main.OwnedWindows, Is.Empty); Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Button branchButton = Field<Button>(main, "BrowseCurrentBranch"); PushProcessTestDriver.Pointer(branchButton);
        MenuFlyout menu = (MenuFlyout)branchButton.Flyout!;
        await Until(main, () => menu.Items.OfType<MenuItem>().Any(item => item.Tag is GitReference branch && branch.ShortName == "folder/topic"));
        PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().Single(item => item.Tag is GitReference branch && branch.ShortName == "folder/topic"));
        await Until(main, () => !main.IsBusy && main.OwnedWindows.Count == 0);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("folder/topic"));
        Assert.That(main.LastException, Is.Null, "The toolbar must not gain the left-panel confirmation.");
    }

    [AvaloniaTest]
    public async Task RemoteYesOpensTheExistingPreselectedRemoteCheckoutWithoutASecondDecision()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("update-ref", "refs/remotes/origin/remote-topic", "HEAD");
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with { ConfirmBranchCheckout = true });
        MainWindow main = await scope.MainAsync(); DoubleClick(main, ReferenceNode(main, "refs/remotes/origin/remote-topic"));
        Answer(await Plain(main), true);
        await Until(main, () => main.OwnedWindows.OfType<CheckoutBranchDialog>().Any(dialog => !dialog.IsBusy));
        CheckoutBranchDialog checkout = main.OwnedWindows.OfType<CheckoutBranchDialog>().Single();
        Assert.That(checkout.ReadOptions().Revision, Is.EqualTo("refs/remotes/origin/remote-topic"));
        Assert.That(Field<RadioButton>(checkout, "CheckoutRemoteBranch").IsChecked, Is.True);
        Assert.That(checkout.OwnedWindows, Is.Empty); Click(checkout, "CancelCheckoutBranch");
        await Until(main, () => !main.IsBusy); Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task DeleteUnmergedRememberedNoKeepsTheBranchThenSuppressesOnlyItsWarningOnTheNextAttempt()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.UnmergedBranchAsync();
        DeleteBranchDialog dialog = scope.Track(new DeleteBranchDialog(scope.Git.Repository, ["topic"])); dialog.Show(); await dialog.InitializeAsync();
        Field<CheckBox>(dialog, "ForceDeleteBranches").IsChecked = true; Click(dialog, "DeleteBranches");
        Window question = await Suppressible(dialog);
        Assert.That(Field<Button>(question, "ConfirmationNo").IsDefault, Is.True);
        Set(question, "ConfirmationDontShowAgain", true); Click(question, "ConfirmationNo"); await Until(dialog, () => !dialog.IsBusy);
        Assert.That((await scope.Git.Repository.GetReferencesAsync()).Any(branch => branch.Name == "refs/heads/topic"), Is.True);
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmDeleteUnmergedBranch, Is.True);
        Click(dialog, "DeleteBranches"); await Until(dialog, () => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That((await scope.Git.Repository.GetReferencesAsync()).Any(branch => branch.Name == "refs/heads/topic"), Is.False);
        DeleteBranchDialog current = scope.Track(new DeleteBranchDialog(scope.Git.Repository, ["main"])); current.Show(); await current.InitializeAsync();
        Field<CheckBox>(current, "ForceDeleteBranches").IsChecked = true;
        Assert.That(Field<Button>(current, "DeleteBranches").IsEnabled, Is.False, "Suppression cannot bypass the current/main-worktree safety guard.");
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DetachedHeadUsesTheWindowsUnmergedWarningEvenForAnAncestorBranch(bool suppress)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic"); await scope.Git.GitAsync("switch", "--detach", "HEAD");
        ConfirmationsSettingsSession preferences = new(); preferences.Save(preferences.Initial with { DontConfirmDeleteUnmergedBranch = suppress });
        DeleteBranchDialog dialog = scope.Track(new DeleteBranchDialog(scope.Git.Repository, ["topic"])); dialog.Show(); await dialog.InitializeAsync();
        Field<CheckBox>(dialog, "ForceDeleteBranches").IsChecked = true; Click(dialog, "DeleteBranches");
        if (!suppress) { Window warning = await Suppressible(dialog); Assert.That(Field<Button>(warning, "ConfirmationNo").IsDefault, Is.True); Click(warning, "ConfirmationYes"); }
        await Until(dialog, () => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That((await scope.Git.Repository.GetReferencesAsync()).Any(branch => branch.Name == "refs/heads/topic"), Is.False);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).IsDetached, Is.True);
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmDeleteUnmergedBranch, Is.EqualTo(suppress));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DismissingOrClosingTheDeleteOwnerNeverRemembersOrDeletes(bool closeOwner)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.UnmergedBranchAsync();
        DeleteBranchDialog dialog = scope.Track(new DeleteBranchDialog(scope.Git.Repository, ["topic"])); dialog.Show(); await dialog.InitializeAsync();
        Field<CheckBox>(dialog, "ForceDeleteBranches").IsChecked = true; Click(dialog, "DeleteBranches");
        Window question = await Suppressible(dialog); Set(question, "ConfirmationDontShowAgain", true);
        if (closeOwner) { dialog.Close(); } else { question.Close(); }
        await Until(dialog, () => !dialog.IsBusy);
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmDeleteUnmergedBranch, Is.False);
        Assert.That((await scope.Git.Repository.GetReferencesAsync()).Any(branch => branch.Name == "refs/heads/topic"), Is.True);
        Assert.That(dialog.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task NullCheckoutPolicyAsksAfterSuccessAndRemembersTheExplicitPositiveOrNegativeDecision(bool answer)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic");
        await File.WriteAllTextAsync(scope.Git.File, "my staged checkout work\n"); await scope.Git.Repository.StageAllAsync();
        CheckoutBranchDialog dialog = await scope.CheckoutAsync(); Configure(scope.Git, (ScriptEvent.AfterCheckout, "after", ""));
        Click(dialog, "CheckoutBranch"); Window question = await Suppressible(dialog);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("topic"), "The question must follow native success.");
        Assert.That(ReadLog(scope.Git), Is.Empty, "AfterCheckout follows the stash decision.");
        Set(question, "ConfirmationDontShowAgain", true); Click(question, answer ? "ConfirmationYes" : "ConfirmationNo");
        await Until(dialog, () => !dialog.IsBusy);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch, Is.EqualTo(answer));
        Assert.That(ReadLog(scope.Git), Is.EqualTo(new[] { "after" }));
        Assert.That((await scope.Git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(answer ? "my staged checkout work\n" : "base\n"));
        Assert.That((await scope.Git.Repository.GetStashesAsync()).Count, Is.EqualTo(answer ? 0 : 1));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CheckoutQuestionDismissKeepsNullAndTheCapturedStashWhileOwnerCloseStopsAfter(bool closeOwner)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic");
        await File.WriteAllTextAsync(scope.Git.File, "kept stash\n");
        CheckoutBranchDialog dialog = await scope.CheckoutAsync(); Configure(scope.Git, (ScriptEvent.AfterCheckout, "after", ""));
        Click(dialog, "CheckoutBranch"); Window prompt = await Suppressible(dialog); Set(prompt, "ConfirmationDontShowAgain", true);
        if (closeOwner) { dialog.Close(); } else { prompt.Close(); }
        await Until(dialog, () => !dialog.IsBusy);
        Assert.That(new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch, Is.Null);
        Assert.That((await scope.Git.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(dialog.LastResult!.CreatedStash));
        Assert.That(await File.ReadAllTextAsync(scope.Git.File), Is.EqualTo("base\n"));
        Assert.That(ReadLog(scope.Git), Is.EqualTo(closeOwner ? Array.Empty<string>() : new[] { "after" }));
        Assert.That(dialog.Succeeded, Is.EqualTo(!closeOwner)); Assert.That(dialog.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task AppliedPolicyIsRereadByAnAlreadyOpenCheckoutAndPopsExactlyItsOwnOidBeforeAfterScripts()
    {
        await using Scope scope = await Scope.CreateAsync();
        await File.WriteAllTextAsync(scope.Git.PathInRepository("script.txt"), "base\n"); await scope.Git.GitAsync("add", "."); await scope.Git.GitAsync("commit", "-m", "Second file");
        await scope.Git.GitAsync("branch", "topic");
        await File.WriteAllTextAsync(scope.Git.File, "older\n"); await scope.Git.Repository.StashAsync("older");
        string older = (await scope.Git.Repository.GetStashesAsync()).Single().Hash;
        await File.WriteAllTextAsync(scope.Git.File, "staged exact work\n"); await scope.Git.Repository.StageAllAsync();
        string untracked = scope.Git.PathInRepository("untracked\nΩ.txt"); await File.WriteAllTextAsync(untracked, "untracked work\n");
        CheckoutBranchDialog dialog = await scope.CheckoutAsync();
        ConfirmationsSettingsSession applied = new(); applied.Save(applied.Initial with { AutoPopStashAfterCheckoutBranch = true });
        Configure(scope.Git, (ScriptEvent.BeforeCheckout, "before", "git rev-parse refs/stash > " + Quote(scope.Git.Output("captured")) + "\nprintf 'newer\\n' > script.txt\ngit stash push -m newer -- script.txt\ngit rev-parse refs/stash > " + Quote(scope.Git.Output("newer"))),
            (ScriptEvent.AfterCheckout, "after", "git show :file.txt > " + Quote(scope.Git.Output("after-index"))));
        Click(dialog, "CheckoutBranch"); await Until(dialog, () => !dialog.IsBusy);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        string captured = File.ReadAllText(scope.Git.Output("captured")).Trim(), newer = File.ReadAllText(scope.Git.Output("newer")).Trim();
        Assert.That(dialog.LastResult!.CreatedStash, Is.EqualTo(captured)); Assert.That(newer, Is.Not.EqualTo(captured));
        Assert.That((await scope.Git.Repository.GetStashesAsync()).Select(stash => stash.Hash), Is.EqualTo(new[] { newer, older }));
        Assert.That(await File.ReadAllTextAsync(scope.Git.File), Is.EqualTo("staged exact work\n"));
        Assert.That((await scope.Git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("staged exact work\n"));
        Assert.That(await File.ReadAllTextAsync(untracked), Is.EqualTo("untracked work\n"));
        Assert.That(File.ReadAllText(scope.Git.Output("after-index")), Is.EqualTo("staged exact work\n"));
        Assert.That(ReadLog(scope.Git), Is.EqualTo(new[] { "before", "after" }));
        Assert.That(dialog.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task VetoOrNativeFailureRetainsTheStashAndDoesNotAskOrRunAfter(bool veto)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic"); await File.WriteAllTextAsync(scope.Git.File, "saved work\n");
        CheckoutBranchDialog dialog = await scope.CheckoutAsync();
        Configure(scope.Git, (ScriptEvent.BeforeCheckout, "before", veto ? "exit 8" : "git branch -D topic"), (ScriptEvent.AfterCheckout, "after", ""));
        Click(dialog, "CheckoutBranch"); await Until(dialog, () => !dialog.IsBusy);
        Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.OwnedWindows, Is.Empty);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That((await scope.Git.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
        Assert.That(ReadLog(scope.Git), Is.EqualTo(new[] { "before" }));
        Assert.That(new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch, Is.Null);
    }

    [AvaloniaTest]
    public async Task ARemovedCapturedStashFailsClosedInsteadOfApplyingANewerTopStash()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic");
        await File.WriteAllTextAsync(scope.Git.File, "captured work\n");
        GitCheckoutPreparation preparation = await scope.Git.Repository.PrepareCheckoutAsync(new GitCheckoutOptions("topic", Changes: LocalChangesAction.Stash));
        string captured = preparation.CreatedStash!; Assert.That(captured, Is.Not.Null.And.Not.Empty);
        await scope.Git.GitAsync("stash", "drop", "stash@{0}");
        await File.WriteAllTextAsync(scope.Git.File, "unrelated newer work\n"); await scope.Git.Repository.StashAsync("newer");
        string newer = (await scope.Git.Repository.GetStashesAsync()).Single().Hash;
        Exception? failure = null;
        try { await scope.Git.Repository.ReapplyCheckoutStashAsync(captured); }
        catch (Exception error) { failure = error; }
        Assert.That(failure, Is.TypeOf<InvalidOperationException>());
        Assert.That((await scope.Git.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(newer));
        Assert.That(await File.ReadAllTextAsync(scope.Git.File), Is.EqualTo("base\n"));
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task ClosingDuringBeforeCheckoutDrainsThePrivateProcessAndNeverPopsOrRunsAfter()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("branch", "topic"); await File.WriteAllTextAsync(scope.Git.File, "saved before drain\n");
        CheckoutBranchDialog dialog = await scope.CheckoutAsync();
        string pidFile = scope.Git.Output("owned-pid"), ready = scope.Git.Output("ready");
        Configure(scope.Git, (ScriptEvent.BeforeCheckout, "before", "printf '%s' $$ > " + Quote(pidFile) + "\nprintf ready > " + Quote(ready) + "\nsleep 30"), (ScriptEvent.AfterCheckout, "after", ""));
        Click(dialog, "CheckoutBranch"); await Until(dialog, () => File.Exists(ready));
        int pid = int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture); string stat = File.ReadAllText($"/proc/{pid}/stat");
        string start = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19];
        dialog.Close(); await Until(dialog, () => !dialog.IsVisible && !dialog.IsBusy);
        bool sameProcess = false;
        try { string now = File.ReadAllText($"/proc/{pid}/stat"); sameProcess = now[(now.LastIndexOf(')') + 2)..].Split(' ')[19] == start; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        // A procfs task may vanish after open; .NET Unix exposes ESRCH as raw errno 3.
        catch (IOException error) when (OperatingSystem.IsLinux() && error.HResult == 3) { }
        Assert.That(sameProcess, Is.False, "The owned native shell identity must be gone before the checkout closes.");
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That((await scope.Git.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
        Assert.That(ReadLog(scope.Git), Is.EqualTo(new[] { "before" }));
        Assert.That(new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch, Is.Null);
    }

    [AvaloniaTest]
    public async Task ResetAlwaysAsksTheFirstQuestionAndRemembersOnlyTheSecondWarningThenActuallyResetsHard()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.ConflictAsync();
        ResolveConflictsDialog dialog = scope.Track(new ResolveConflictsDialog(scope.Git.Repository)); dialog.Show(); await dialog.InitializeAsync();
        string head = await scope.Head(), index = (await scope.Git.GitAsync("ls-files", "--unmerged")).StandardOutput;
        Click(dialog, "ResetConflicts"); Answer(await Plain(dialog), false); await Until(dialog, () => !dialog.IsBusy);
        Assert.That((await scope.Git.GitAsync("ls-files", "--unmerged")).StandardOutput, Is.EqualTo(index));
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmSecondAbortConfirmation, Is.False);
        Click(dialog, "ResetConflicts"); Answer(await Plain(dialog), true); Window warning = await Suppressible(dialog);
        Assert.That(Field<TextBlock>(warning, "ConfirmationMessage").Text, Does.Contain("DELETE all changes"));
        Set(warning, "ConfirmationDontShowAgain", true); Click(warning, "ConfirmationNo"); await Until(dialog, () => !dialog.IsBusy);
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmSecondAbortConfirmation, Is.True);
        Assert.That((await scope.Git.GitAsync("ls-files", "--unmerged")).StandardOutput, Is.EqualTo(index));
        Click(dialog, "ResetConflicts"); Window stillRequired = await Plain(dialog);
        Assert.That(dialog.OwnedWindows, Has.Count.EqualTo(1)); Answer(stillRequired, true);
        await Until(dialog, () => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(await scope.Head(), Is.EqualTo(head)); Assert.That(await scope.Git.Repository.GetConflictsAsync(), Is.Empty);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(File.Exists(scope.Git.PathInRepository(".git/MERGE_HEAD")), Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SecondResetWarningDismissOrOwnerCancellationDoesNotRememberOrReset(bool closeOwner)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.ConflictAsync();
        ResolveConflictsDialog dialog = scope.Track(new ResolveConflictsDialog(scope.Git.Repository)); dialog.Show(); await dialog.InitializeAsync();
        string index = (await scope.Git.GitAsync("ls-files", "--unmerged")).StandardOutput;
        Click(dialog, "ResetConflicts"); Answer(await Plain(dialog), true); Window second = await Suppressible(dialog);
        Set(second, "ConfirmationDontShowAgain", true); if (closeOwner) { dialog.Close(); } else { second.Close(); }
        await Until(dialog, () => !dialog.IsBusy);
        Assert.That(new ConfirmationsSettingsSession().Initial.DontConfirmSecondAbortConfirmation, Is.False);
        Assert.That((await scope.Git.GitAsync("ls-files", "--unmerged")).StandardOutput, Is.EqualTo(index));
        Assert.That(File.Exists(scope.Git.PathInRepository(".git/MERGE_HEAD")), Is.True);
        Assert.That(dialog.OwnedWindows, Is.Empty);
    }

    private static string Profile(string name) => Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, name);
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Click(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static void Set(Window window, string name, bool? value)
    { CheckBox box = Field<CheckBox>(window, name); box.BringIntoView(); PortableHelperSettingsFixture.Pump(window); box.IsChecked = value; }
    private static Task Until(Window window, Func<bool> condition) => PushProcessTestDriver.UntilAsync(window, condition);
    private static async Task<Window> Suppressible(Window owner)
    { await Until(owner, () => owner.OwnedWindows.Any(child => child.Name == "SuppressibleConfirmation")); return owner.OwnedWindows.Single(child => child.Name == "SuppressibleConfirmation"); }
    private static async Task<Window> Plain(Window owner)
    { await Until(owner, () => owner.OwnedWindows.Any(child => child.Name != "SuppressibleConfirmation" && child is not GitProcessDialog && child is not CheckoutBranchDialog)); return owner.OwnedWindows.Single(child => child.Name != "SuppressibleConfirmation" && child is not GitProcessDialog && child is not CheckoutBranchDialog); }
    private static void Answer(Window question, bool yes)
    { PushProcessTestDriver.Pointer(question.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == (yes ? "Yes" : "No"))); }
    private static IEnumerable<TreeViewItem> Navigation(MainWindow main)
    {
        TreeView tree = (TreeView)typeof(MainWindow).GetField("_navigation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        return Recurse(tree.Items.OfType<TreeViewItem>());
        static IEnumerable<TreeViewItem> Recurse(IEnumerable<TreeViewItem> items)
        { foreach (TreeViewItem item in items) { yield return item; foreach (TreeViewItem nested in Recurse(item.Items.OfType<TreeViewItem>())) { yield return nested; } } }
    }
    private static TreeViewItem ReferenceNode(MainWindow main, string name) => Navigation(main).Single(node => node.Tag is GitReference branch && branch.Name == name);
    private static void DoubleClick(Window owner, TreeViewItem item)
    {
        Control header = (Control)item.Header!; header.BringIntoView();
        // Publish the headless hit-test scene before the strict pointer check,
        // just as the shared Pointer driver does. Settle only before the pair.
        for (int frame = 0; frame < 10; frame++)
        {
            Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
        // IconLabel is a transparent horizontal StackPanel: its stretched centre
        // can be outside every rendered child. Aim at the real branch text.
        TextBlock label = header.GetVisualDescendants().OfType<TextBlock>().Single();
        Point? translated = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), owner);
        for (int index = 0; index < 2; index++)
        {
            Visual? hit = translated is Point position ? owner.InputHitTest(position) as Visual : null;
            string details = $"Double-click {index + 1}/2; ref={(item.Tag as GitReference)?.Name}; point={translated}\n"
                + $"owner visible/enabled={owner.IsVisible}/{owner.IsEnabled}, client={owner.ClientSize}, scaling={owner.RenderScaling}\n"
                + $"item bounds={item.Bounds}, visible/enabled/hit={item.IsEffectivelyVisible}/{item.IsEffectivelyEnabled}/{item.IsHitTestVisible}\n"
                + $"header bounds={header.Bounds}, desired={header.DesiredSize}, visible/enabled/hit={header.IsEffectivelyVisible}/{header.IsEffectivelyEnabled}/{header.IsHitTestVisible}, top={Identity(TopLevel.GetTopLevel(header))}\n"
                + $"label bounds={label.Bounds}, desired={label.DesiredSize}, visible/enabled/hit={label.IsEffectivelyVisible}/{label.IsEffectivelyEnabled}/{label.IsHitTestVisible}\n"
                + "hit=" + Identity(hit) + "; ancestors=" + string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(Identity) ?? []) + "\n"
                + "row ancestors=" + string.Join(" -> ", item.GetVisualAncestors().OfType<TreeViewItem>().Select(parent =>
                    Identity(parent) + $" expanded={parent.IsExpanded}, visible/enabled/hit={parent.IsEffectivelyVisible}/{parent.IsEffectivelyEnabled}/{parent.IsHitTestVisible}"));
            bool ready = translated is Point target && owner.IsVisible && owner.IsEnabled
                && ReferenceEquals(item.Header, header) && TopLevel.GetTopLevel(header) == owner
                && header.GetVisualAncestors().Contains(item) && item.IsEffectivelyVisible && item.IsEffectivelyEnabled
                && header.IsEffectivelyVisible && header.IsEffectivelyEnabled && label.IsEffectivelyVisible && label.IsEffectivelyEnabled
                && label.Bounds.Width > 0 && label.Bounds.Height > 0 && new Rect(default, owner.ClientSize).Contains(target);
            Assert.That(ready, Is.True, "The real branch header must remain attached and usable.\n" + details);
            Assert.That(hit?.GetSelfAndVisualAncestors().Contains(header), Is.True, "The double-click must hit the actual branch header.\n" + details);
            Point point = translated!.Value;
            owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); if (owner.IsVisible) { owner.MouseUp(point, MouseButton.Left); }
        }
        Dispatcher.UIThread.RunJobs();
        static string Identity(Visual? visual) => visual is null ? "<null>" : visual.GetType().Name + "/" + (visual as Control)?.Name;
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Configure(GitFixture fixture, params (ScriptEvent Event, string Marker, string Extra)[] entries)
    {
        new ProcessDialogPreferencesStore().Save(new(true)); List<ScriptDefinition> scripts = [];
        foreach (var entry in entries)
        {
            string file = fixture.Output("confirmation-event-" + scripts.Count + ".sh");
            File.WriteAllText(file, "#!/bin/sh\nset -eu\nprintf '%s\\n' " + Quote(entry.Marker) + " >> " + Quote(fixture.Output("confirmation-events.log")) + "\n" + entry.Extra + "\n");
            scripts.Add(new() { Name = entry.Marker, Command = "/bin/sh", Arguments = "\"" + file + "\"", OnEvent = entry.Event });
        }
        new ScriptsPreferencesStore().Save(scripts);
    }
    private static string[] ReadLog(GitFixture fixture) => File.Exists(fixture.Output("confirmation-events.log")) ? File.ReadAllLines(fixture.Output("confirmation-events.log")) : [];
    private sealed class Scope(GitFixture git) : IAsyncDisposable
    {
        internal GitFixture Git { get; } = git;
        private readonly List<Window> _windows = [];
        internal static async Task<Scope> CreateAsync() => new(await GitFixture.CreateAsync());
        internal T Track<T>(T window) where T : Window { _windows.Add(window); return window; }
        internal async Task<MainWindow> MainAsync()
        { MainWindow main = Track(new MainWindow { Width = 1440, Height = 900 }); main.Show(); await main.OpenRepositoryAsync(Git.Repository.Path); await Until(main, () => !main.IsBusy); return main; }
        internal Task<string> Head() => Git.Repository.ResolveRevisionAsync("HEAD");
        internal async Task<CheckoutBranchDialog> CheckoutAsync()
        {
            GitReference topic = (await Git.Repository.GetReferencesAsync()).Single(branch => branch.Name == "refs/heads/topic");
            CheckoutBranchDialog dialog = Track(new CheckoutBranchDialog(Git.Repository, topic)); dialog.Show(); await dialog.InitializeAsync();
            Field<RadioButton>(dialog, "CheckoutStashChanges").IsChecked = true;
            Field<CheckBox>(dialog, "CheckoutStashUntracked").IsChecked = true; return dialog;
        }
        internal async Task UnmergedBranchAsync()
        { await Git.GitAsync("switch", "-c", "topic"); await Git.ChangeAsync("topic\n", "Topic"); await Git.GitAsync("switch", "main"); }
        internal async Task ConflictAsync()
        {
            await UnmergedBranchAsync(); await Git.ChangeAsync("main\n", "Main");
            GitResult merge = await new GitRunner().RunAsync(Git.Repository.Path, ["merge", "--no-edit", "topic"]);
            Assert.That(merge.ExitCode, Is.EqualTo(1)); Assert.That(await Git.Repository.GetConflictsAsync(), Has.Count.EqualTo(1));
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    window.Close(); await Until(window, () => !window.IsVisible);
                    if (window is MainWindow main) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                }
            }
            finally { Git.Dispose(); }
        }
    }
}
