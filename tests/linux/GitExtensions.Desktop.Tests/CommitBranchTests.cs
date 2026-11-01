using System.Security.Cryptography;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitBranchTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task TrackingStatusUsesFetchRemoteAndMergeRatherThanPushSettingsAndKeepsTheClassicToolbar(bool dark)
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Git.GitAsync("remote", "add", "origin", f.Git.Output("remote.git"));
        await f.Git.GitAsync("config", "branch.main.remote", "origin");
        await f.Git.GitAsync("config", "branch.main.merge", "refs/heads/review/λ");
        await f.Git.GitAsync("config", "branch.main.pushRemote", "different-push");
        await f.Git.GitAsync("config", "remote.pushDefault", "global-push");
        CommitDialog dialog = await f.OpenAsync(dark: dark);
        Assert.That(Field<TextBlock>(dialog, "CommitBranchName").Text, Is.EqualTo("main →"));
        Assert.That(RemoteText(dialog), Is.EqualTo("origin/review/λ"));
        Assert.That(dialog.Title, Is.EqualTo("Commit to main (" + f.Git.Repository.Path + ")"));
        MenuItem create = CreateItem(dialog);
        Assert.That(create.Items.Count, Is.Zero, "Create branch is an action, not a checkout dropdown.");
        Image branchIcon = create.GetVisualDescendants().OfType<Image>().Single();
        Assert.That(branchIcon.Source, Is.Not.Null);
        // Compact ToolStrip commands can overflow with the host's actual font.
        // Check the rendered icon of the action the user can really see.
        MenuItem visibleCreate = RevealCreateItem(dialog);
        Image visibleIcon = visibleCreate.GetVisualDescendants().OfType<Image>().Single();
        Assert.That(visibleIcon.Source, Is.SameAs(branchIcon.Source));
        Assert.That(visibleIcon.Bounds.Size, Is.EqualTo(new Size(16, 16)));
        CompactMenuToolbar toolbar = CompactMenuToolbar.For(Field<Menu>(dialog, "CommitToolbar"))!;
        if (toolbar.Overflow.IsSubMenuOpen) { Press(dialog, PhysicalKey.Escape); }
        Assert.That(Field<Button>(dialog, "CommitBranchRemote").IsEffectivelyVisible, Is.True);
        Button link = Field<Button>(dialog, "CommitBranchRemote");
        dialog.MouseMove(new Point(0, 0)); Dispatcher.UIThread.RunJobs();
        TextBlock label = Field<TextBlock>(dialog, "CommitBranchRemoteText");
        Assert.That(label.TextDecorations, Is.Null.Or.Empty, "Windows remoteNameLabel underlines only on hover.");
        Point position = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseMove(position); Dispatcher.UIThread.RunJobs();
        Assert.That(label.TextDecorations, Is.EqualTo(Avalonia.Media.TextDecorations.Underline));
        Assert.That(link.Template, Is.Not.Null); Assert.That(link.IsKeyboardFocusWithin, Is.False);
        dialog.MouseMove(new Point(0, 0)); Dispatcher.UIThread.RunJobs();
        Assert.That(label.TextDecorations, Is.Null.Or.Empty);
        Capture(dialog, "classic-commit-branch-" + (dark ? "dark" : "light") + ".png");
    }

    [AvaloniaTest]
    [TestCase("none", "(remote not configured)")]
    [TestCase("sorted", "alpha/main (untracked)")]
    [TestCase("origin", "origin/main (untracked)")]
    [TestCase("local", "./another/local")]
    public async Task UntrackedAndLocalTrackingDestinationsFollowWindowsFallbacks(string setup, string expected)
    {
        await using Scope f = await Scope.CreateAsync();
        if (setup is "sorted" or "origin")
        {
            await f.Git.GitAsync("remote", "add", "zulu", f.Git.Output("z.git"));
            await f.Git.GitAsync("remote", "add", "alpha", f.Git.Output("a.git"));
            if (setup == "origin") { await f.Git.GitAsync("remote", "add", "origin", f.Git.Output("o.git")); }
        }
        if (setup == "local")
        {
            await f.Git.GitAsync("config", "branch.main.remote", ".");
            await f.Git.GitAsync("config", "branch.main.merge", "refs/heads/another/local");
        }
        CommitDialog dialog = await f.OpenAsync();
        Assert.That(RemoteText(dialog), Is.EqualTo(expected));
        Assert.That(Field<TextBlock>(dialog, "CommitBranchName").Text, Is.EqualTo("main →"));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DetachedAndUnbornStatusHasNoInventedRemoteLink(bool unborn)
    {
        await using Scope f = await Scope.CreateAsync(unborn: unborn);
        if (!unborn) { await f.Git.GitAsync("switch", "--detach", "HEAD"); }
        await f.Git.GitAsync("remote", "add", "origin", f.Git.Output("o.git"));
        CommitDialog dialog = await f.OpenAsync();
        Assert.That(Field<TextBlock>(dialog, "CommitBranchName").Text, Is.EqualTo(unborn ? "main" : "(no branch)"));
        Assert.That(Field<Button>(dialog, "CommitBranchRemote").IsVisible, Is.False);
        Assert.That(dialog.Title, Is.EqualTo("Commit"));
    }

    [AvaloniaTest]
    public async Task RemoteLinkSelectsTheCurrentLocalBranchAndRefreshesOnlyStatusWithoutLosingDraftDiffOrSelections()
    {
        await using Scope f = await Scope.CreateAsync(); await f.StageAsync();
        await f.Git.GitAsync("remote", "add", "origin", f.Git.Output("remote.git"));
        CommitDialog dialog = await f.OpenAsync();
        TreeView staged = Field<TreeView>(dialog, "Staged"); FileStatusTestHelpers.Select(staged, "file.txt"); await Idle(dialog);
        TextBox editor = Field<TextBox>(dialog, "Message"); editor.Text = "Draft λ\n\nKeep this body";
        // Setting CaretIndex last collapses the native selection. Establish the
        // caret first, then the range whose preservation is the actual contract.
        editor.CaretIndex = 7; editor.SelectionStart = 2; editor.SelectionEnd = 7;
        string selection = SelectedPath(staged), source = Field<DiffView>(dialog, "SelectedDiff").Text;
        string? identity = Field<DiffView>(dialog, "SelectedDiff").DocumentIdentity;
        Assert.That((editor.SelectionStart, editor.SelectionEnd, editor.CaretIndex), Is.EqualTo((2, 7, 7)), "The edited selection must really exist immediately before opening Remotes.");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CommitBranchRemote"));
        await Until(() => dialog.OwnedWindows.OfType<RemotesDialog>().Any());
        RemotesDialog remotes = dialog.OwnedWindows.OfType<RemotesDialog>().Single(); await Until(() => !remotes.IsBusy && Field<TextBox>(remotes, "TrackingLocalBranch").Text == "main");
        Assert.That(Field<TabControl>(remotes, "RemotesTabs").SelectedIndex, Is.EqualTo(1));
        Assert.That((Field<ListBox>(remotes, "BranchTrackingList").SelectedItem as GitBranchTrackingSnapshot)?.LocalBranch, Is.EqualTo("main"));
        // An external tracking edit while this owned window is open must be reread on return.
        await f.Git.GitAsync("config", "branch.main.remote", "origin");
        await f.Git.GitAsync("config", "branch.main.merge", "refs/heads/changed-destination");
        remotes.Close(); await Idle(dialog);
        Assert.That(RemoteText(dialog), Is.EqualTo("origin/changed-destination"));
        Assert.That(editor.Text, Is.EqualTo("Draft λ\n\nKeep this body"));
        Assert.That((editor.SelectionStart, editor.SelectionEnd, editor.CaretIndex), Is.EqualTo((2, 7, 7)));
        Assert.That(SelectedPath(staged), Is.EqualTo(selection));
        Assert.That(Field<DiffView>(dialog, "SelectedDiff").Text, Is.EqualTo(source));
        Assert.That(Field<DiffView>(dialog, "SelectedDiff").DocumentIdentity, Is.EqualTo(identity));
        Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task TheVisibleCreateActionUsesTheSharedDialogAndOptionalCheckoutWithoutLosingTheEditedMessage()
    {
        await using Scope f = await Scope.CreateAsync(); await f.StageAsync(); CommitDialog dialog = await f.OpenAsync();
        TextBox editor = Field<TextBox>(dialog, "Message"); editor.Text = "Draft survives branch creation";
        string initial = await f.Git.Repository.ResolveRevisionAsync("HEAD"); string index = await f.IndexAsync();
        PushProcessTestDriver.Pointer(RevealCreateItem(dialog));
        CreateBranchDialog child = await CreateChild(dialog); await Until(() => !child.IsBusy);
        Assert.That(Field<TextBox>(child, "CreateBranchName").Text, Is.EqualTo("main"));
        Assert.That(Field<TextBox>(child, "CreateBranchRevision").Text, Is.EqualTo("HEAD"));
        Assert.That(Field<CheckBox>(child, "CheckoutAfterCreate").IsChecked, Is.True);
        Field<TextBox>(child, "CreateBranchName").Text = "topic/λ";
        Field<CheckBox>(child, "CheckoutAfterCreate").IsChecked = false;
        Click(child, "CreateBranch"); await Idle(dialog);
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("refs/heads/topic/λ"), Is.EqualTo(initial));
        Assert.That((await f.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That(editor.Text, Is.EqualTo("Draft survives branch creation")); Assert.That(await f.IndexAsync(), Is.EqualTo(index));
        Assert.That(Field<TextBlock>(dialog, "CommitBranchName").Text, Is.EqualTo("main →"));
        Assert.That(dialog.HasCommitted, Is.False); Assert.That(dialog.HasRepositoryChanges, Is.True);
    }

    [AvaloniaTest]
    [TestCase("local", "main")]
    [TestCase("remote", "folder/λ")]
    [TestCase("tag", "release/λ")]
    [TestCase("none", "")]
    [TestCase("explicit", "explicit/λ")]
    public async Task TheSharedCreateDialogSuggestsTheFirstNonTagThenTagAtTheSelectedRevision(string setup, string expected)
    {
        await using Scope f = await Scope.CreateAsync();
        if (setup != "local") { await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.Git.GitAsync("branch", "-D", "main"); }
        if (setup == "remote")
        {
            await f.Git.GitAsync("remote", "add", "origin", f.Git.Output("remote.git"));
            // `remote add` rejects a prefix-overlapping name. Git configuration
            // can still contain it, and the reader must select the longest owner.
            await f.Git.GitAsync("config", "remote.origin/nested.url", f.Git.Output("nested.git"));
            await f.Git.GitAsync("config", "remote.origin/nested.fetch", "+refs/heads/*:refs/remotes/origin/nested/*");
            await f.Git.GitAsync("update-ref", "refs/remotes/origin/nested/folder/λ", "HEAD");
            Assert.That((await f.Git.Repository.GetRemotesAsync()).Select(remote => remote.Name), Is.EquivalentTo(new[] { "origin", "origin/nested" }));
        }
        if (setup is "local" or "tag") { await f.Git.GitAsync("tag", "-a", "release/λ", "-m", "Annotated release"); }
        CreateBranchDialog child = new(f.Git.Repository, name: setup == "explicit" ? expected : null); f.Track(child); child.Show(); await child.InitializeAsync();
        Assert.That(Field<TextBox>(child, "CreateBranchName").Text, Is.EqualTo(expected));
        Assert.That(child.ReadOptions().Checkout, Is.True); Assert.That(child.ReadOptions().Orphan, Is.False); Assert.That(child.ReadOptions().ClearWorkingTree, Is.False);
        child.Close(); Assert.That(child.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task TheSharedCreateDialogPreservesInputAlreadyTypedBeforeItsAsynchronousInitialization()
    {
        await using Scope f = await Scope.CreateAsync(); CreateBranchDialog child = new(f.Git.Repository); f.Track(child); child.Show();
        Field<TextBox>(child, "CreateBranchName").Text = "typed/before-Git-returned"; await child.InitializeAsync();
        Assert.That(Field<TextBox>(child, "CreateBranchName").Text, Is.EqualTo("typed/before-Git-returned"));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DetachedCancelOrEscapeStopsBeforeCommitAndKeepsTheMessageIndexAndReferences(bool escape)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        Assert.That(new CommitPreferences().DontConfirmCommitIfNoBranch, Is.False);
        if (escape) { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR"); }
        CommitDialog dialog = await f.OpenAsync(dark: escape); Field<TextBox>(dialog, "Message").Text = "Keep detached draft";
        string material = await f.MaterialAsync(); string beforeMessage = f.Store.Load().LastCommitMessage;
        Click(dialog, "Commit"); Window prompt = await BranchPrompt(dialog);
        Assert.That(prompt.Title, Is.EqualTo(GitExtensions.Linux.Desktop.Localization.TranslationCatalog.LoadDefault().Translate("Not on a branch")));
        Assert.That(Field<Button>(prompt, "DetachedCancel").IsDefault, Is.True);
        Assert.That(Field<Button>(prompt, "DetachedCancel").IsFocused, Is.True);
        Assert.That(prompt.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().Fill, Is.EqualTo(Avalonia.Media.Brushes.Red));
        foreach (string name in new[] { "DetachedCheckout", "DetachedCreate", "DetachedContinue" })
        {
            Button action = Field<Button>(prompt, name);
            Assert.That(action.Bounds.Height, Is.GreaterThanOrEqualTo(48));
            Assert.That(action.GetVisualDescendants().OfType<PathIcon>().Count(), Is.EqualTo(1));
        }
        Capture(prompt, "classic-detached-branch-" + (escape ? "fr-dark" : "en-light") + ".png");
        if (escape) { Press(prompt, PhysicalKey.Escape, releaseOn: dialog); } else { Click(prompt, "DetachedCancel"); }
        await Idle(dialog);
        Assert.That(dialog.HasCommitted, Is.False); Assert.That(dialog.LastError, Is.Null);
        Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("Keep detached draft"));
        Assert.That(f.Store.Load().LastCommitMessage, Is.EqualTo(beforeMessage)); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase("Create")] [TestCase("Checkout")]
    public async Task CancellingEitherDetachedChildDoesNotContinueTheCommit(string choice)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        CommitDialog dialog = await f.OpenAsync(); Field<TextBox>(dialog, "Message").Text = "Do not commit after cancelling child"; string material = await f.MaterialAsync();
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "Detached" + choice);
        await Until(() => dialog.OwnedWindows.Any(window => window is CreateBranchDialog or CheckoutBranchDialog));
        Window child = dialog.OwnedWindows.Single(window => window is CreateBranchDialog or CheckoutBranchDialog);
        await Until(() => child is RepositoryDialog repositoryDialog && !repositoryDialog.IsBusy);
        Press(child, PhysicalKey.Escape, releaseOn: dialog); await Idle(dialog);
        Assert.That(dialog.HasCommitted, Is.False); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(material)); Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("Do not commit after cancelling child"));
    }

    [AvaloniaTest]
    [TestCase("Continue")] [TestCase("Checkout")] [TestCase("Create")]
    public async Task ADetachedExplicitChoiceContinuesExactlyOneCommitOnTheChosenBranch(string choice)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        CommitDialog dialog = await f.OpenAsync(); Field<TextBox>(dialog, "Message").Text = "Detached choice " + choice;
        string original = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "Detached" + choice);
        if (choice == "Create")
        {
            CreateBranchDialog child = await CreateChild(dialog); await Until(() => !child.IsBusy);
            Field<TextBox>(child, "CreateBranchName").Text = "recovered/λ"; Click(child, "CreateBranch");
        }
        else if (choice == "Checkout")
        {
            await Until(() => dialog.OwnedWindows.OfType<CheckoutBranchDialog>().Any());
            CheckoutBranchDialog child = dialog.OwnedWindows.OfType<CheckoutBranchDialog>().Single(); await Until(() => !child.IsBusy);
            ComboBox choices = Field<ComboBox>(child, "CheckoutBranchSelection"); choices.SelectedItem = choices.Items.OfType<GitReference>().Single(reference => reference.ShortName == "main");
            Click(child, "CheckoutBranch");
        }
        await Idle(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastError); Assert.That(dialog.HasCommitted, Is.True);
        GitStatus status = await f.Git.Repository.GetStatusAsync();
        Assert.That(status.Branch, Is.EqualTo(choice == "Continue" ? "(detached)" : choice == "Checkout" ? "main" : "recovered/λ"));
        GitCommit latest = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        Assert.That(latest.Subject, Is.EqualTo("Detached choice " + choice)); Assert.That(latest.Parents, Is.EqualTo(new[] { original }));
        Assert.That(status.Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task TargetedDetachedCreateReceivesTheEditedCommitRatherThanTheCurrentHead()
    {
        await using Scope f = await Scope.CreateAsync(); GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        await f.Git.ChangeAsync("later\n", "Later head"); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        CommitDialog dialog = await f.OpenAsync(TargetedCommitKind.Fixup, edited); string head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "DetachedCreate"); CreateBranchDialog child = await CreateChild(dialog); await Until(() => !child.IsBusy);
        Assert.That(Field<TextBox>(child, "CreateBranchRevision").Text, Is.EqualTo(edited.Hash));
        Assert.That(edited.Hash, Is.Not.EqualTo(head)); child.Close(); await Idle(dialog); Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task TargetedDetachedCheckoutOnlyOffersBranchesContainingTheEditedCommit()
    {
        await using Scope f = await Scope.CreateAsync(); string first = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        await f.Git.GitAsync("branch", "too-old", first); await f.Git.ChangeAsync("later\n", "Edited commit");
        GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync(); CommitDialog dialog = await f.OpenAsync(TargetedCommitKind.Fixup, edited);
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "DetachedCheckout");
        await Until(() => dialog.OwnedWindows.OfType<CheckoutBranchDialog>().Any()); CheckoutBranchDialog child = dialog.OwnedWindows.OfType<CheckoutBranchDialog>().Single(); await Until(() => !child.IsBusy);
        ComboBox choices = Field<ComboBox>(child, "CheckoutBranchSelection");
        Assert.That(choices.Items.OfType<GitReference>().Select(reference => reference.ShortName), Is.EqualTo(new[] { "main" }));
        Click(child, "CheckoutBranch"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.HasCommitted, Is.True);
        Assert.That((await f.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That((await f.Git.Repository.GetLogAsync(1, all: false)).Single().Subject, Is.EqualTo("fixup! Edited commit"));
    }

    [AvaloniaTest]
    public async Task TheDisabledConfirmationPreferencePersistsAndSkipsOnlyTheDetachedPrompt()
    {
        await using Scope f = await Scope.CreateAsync(); f.Store.Save(f.Store.Load() with { DontConfirmCommitIfNoBranch = true });
        Assert.That(new CommitPreferencesStore().Load().DontConfirmCommitIfNoBranch, Is.True);
        await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync(); CommitDialog dialog = await f.OpenAsync();
        Field<TextBox>(dialog, "Message").Text = "Explicit disabled confirmation"; Click(dialog, "Commit"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.HasCommitted, Is.True); Assert.That((await f.Git.Repository.GetStatusAsync()).IsDetached, Is.True);
    }

    [AvaloniaTest]
    public async Task GitAmApplyingMarkerDoesNotSuppressTheDetachedHeadWarning()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        string applying = f.Git.PathInRepository(".git/rebase-apply"); Directory.CreateDirectory(applying); await File.WriteAllTextAsync(Path.Combine(applying, "applying"), "");
        CommitDialog dialog = await f.OpenAsync(); Field<TextBox>(dialog, "Message").Text = "Detached while applying a patch"; string head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); Window prompt = await BranchPrompt(dialog); Click(prompt, "DetachedCancel"); await Idle(dialog);
        Assert.That(dialog.HasCommitted, Is.False); Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
    }

    [AvaloniaTest]
    public async Task AnActualPausedRebaseDoesNotOpenTheDetachedHeadWarning()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The controlled rebase editor uses the Linux sed command."); }
        await using Scope f = await Scope.CreateAsync(); await f.Git.ChangeAsync("second\n", "Second commit");
        string editor = f.Git.Output("edit-first-command"), ran = f.Git.Output("sequence-editor-ran");
        await File.WriteAllTextAsync(editor, "#!/bin/sh\nprintf ran > " + ShellQuote(ran) + "\nsed -i '1s/^pick /edit /' \"$1\"\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(editor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        // GitRunner supplies its noninteractive sequence editor before applying
        // this explicit per-command override; an ambient variable is insufficient.
        GitResult paused = await new GitRunner().RunAsync(f.Git.Repository.Path, ["rebase", "-i", "HEAD~1"],
            environment: new Dictionary<string, string?> { ["GIT_SEQUENCE_EDITOR"] = ShellQuote(editor) });
        Assert.That(paused.Success, Is.True, paused.StandardError);
        Assert.That(await File.ReadAllTextAsync(ran), Is.EqualTo("ran"), "Git must have executed the controlled sequence editor.");
        Assert.That((await f.Git.Repository.GetStatusAsync()).IsDetached, Is.True);
        GitRebaseState state = await f.Git.Repository.GetRebaseStateAsync();
        Assert.That(state.IsActive, Is.True); Assert.That(state.Backend, Is.EqualTo(GitRebaseBackend.Merge));
        Assert.That(state.CurrentCommit, Is.EqualTo(await f.Git.Repository.ResolveRevisionAsync("HEAD")));
        await f.StageAsync(); CommitDialog dialog = await f.OpenAsync(); Field<TextBox>(dialog, "Message").Text = "Commit within paused rebase";
        Click(dialog, "Commit"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.HasCommitted, Is.True);
    }

    [AvaloniaTest]
    [TestCase(true, TargetedCommitKind.Fixup)]
    [TestCase(false, TargetedCommitKind.Fixup)]
    [TestCase(false, TargetedCommitKind.Amend)]
    public async Task TheUniqueContainingLocalBranchUsesTheSharedDefaultActionForDirtyPreferenceOrCleanEmptyCommit(bool staged, TargetedCommitKind kind)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD");
        GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        if (staged) { await f.StageAsync(); }
        new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: staged));
        CommitDialog dialog = await f.OpenAsync(kind, edited);
        Click(dialog, "Commit");
        if (!staged)
        {
            await Until(() => dialog.OwnedWindows.OfType<CommitEmptyDialog>().Any());
            Click(dialog.OwnedWindows.OfType<CommitEmptyDialog>().Single(), "MakeEmpty");
        }
        Click(await BranchPrompt(dialog), "DetachedCheckout");
        // No test action selects or accepts a checkout picker. A regression that
        // always opens it cannot reach this completed commit.
        await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.HasCommitted, Is.True);
        GitStatus status = await f.Git.Repository.GetStatusAsync();
        Assert.That(status.Branch, Is.EqualTo("main")); Assert.That(status.Changes, Is.Empty);
        GitCommit latest = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        Assert.That(latest.Parents, Is.EqualTo(new[] { edited.Hash }));
        Assert.That(latest.Subject, Is.EqualTo((kind == TargetedCommitKind.Amend ? "amend! " : "fixup! ") + edited.Subject));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AlwaysShowCheckoutForcesTheSameContainingBranchPickerEvenWhenTheDefaultWouldBeEligible(bool staged)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD");
        GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        if (staged) { await f.StageAsync(); }
        new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: true, AlwaysShowCheckoutDialog: true));
        CommitDialog dialog = await f.OpenAsync(TargetedCommitKind.Fixup, edited); Click(dialog, "Commit");
        if (!staged)
        {
            await Until(() => dialog.OwnedWindows.OfType<CommitEmptyDialog>().Any());
            Click(dialog.OwnedWindows.OfType<CommitEmptyDialog>().Single(), "MakeEmpty");
        }
        Click(await BranchPrompt(dialog), "DetachedCheckout"); CheckoutBranchDialog checkout = await CheckoutPicker(dialog);
        Assert.That(checkout.CanExecuteDefault, Is.False); Assert.That(checkout.ReadOptions().Revision, Is.EqualTo("refs/heads/main"));
        Assert.That(dialog.HasCommitted, Is.False); checkout.Close(); await Idle(dialog); Assert.That(dialog.HasCommitted, Is.False);
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(edited.Hash));
    }

    [AvaloniaTest]
    public async Task NonTargetedCheckoutDoesNotGuessASingleLocalBranchEvenWhenDefaultPreferenceIsEnabled()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: true));
        CommitDialog dialog = await f.OpenAsync(); Field<TextBox>(dialog, "Message").Text = "An explicit picker is needed"; string head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "DetachedCheckout"); CheckoutBranchDialog checkout = await CheckoutPicker(dialog);
        Assert.That(checkout.CanExecuteDefault, Is.False); Assert.That(dialog.HasCommitted, Is.False);
        checkout.Close(); await Idle(dialog); Assert.That(dialog.HasCommitted, Is.False); Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
    }

    [AvaloniaTest]
    public async Task AnExistingConstraintCannotExecuteTheDefaultActionWithoutTheNewExplicitOptIn()
    {
        await using Scope f = await Scope.CreateAsync();
        new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: true));
        CheckoutBranchConstraint constraint = new(token => f.Git.Repository.GetReferencesAsync(token),
            (_, options, token) => f.Git.Repository.CheckoutAsync(options, token));
        Assert.That(constraint.AllowDefaultAction, Is.False);
        CheckoutBranchDialog checkout = new(f.Git.Repository, constraint: constraint); f.Track(checkout); checkout.Show(); await checkout.InitializeAsync();
        Assert.That(checkout.LastError, Is.Null); Assert.That(checkout.CanExecuteDefault, Is.False);
        Assert.That(checkout.ReadOptions().Revision, Is.EqualTo("refs/heads/main"));
    }

    [AvaloniaTest]
    public async Task FailedDefaultCheckoutShowsItsOriginalPickerAndDoesNotContinueTheCommit()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD");
        await f.Git.GitAsync("worktree", "add", f.Git.Output("other checkout"), "main"); await f.StageAsync();
        GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single(); new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: true));
        CommitDialog dialog = await f.OpenAsync(TargetedCommitKind.Fixup, edited); string material = await f.MaterialAsync();
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "DetachedCheckout"); CheckoutBranchDialog checkout = await CheckoutPicker(dialog);
        Assert.That(checkout.LastError, Is.Not.Null.And.Not.Empty); Assert.That(checkout.LastError, Does.Contain("main"));
        Assert.That(checkout.ReadOptions().Revision, Is.EqualTo("refs/heads/main")); Assert.That(checkout.Succeeded, Is.False); Assert.That(dialog.HasCommitted, Is.False);
        checkout.Close(); await Idle(dialog); Assert.That(dialog.HasCommitted, Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DefaultCheckoutScriptVetoOrRefusalUsesTheVisibleOwnerAndStopsWithoutAPickerOrBeforeCommit(bool refuseConfirmation)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync();
        GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single(); new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: true));
        new ProcessDialogPreferencesStore().Save(new(true));
        string before = f.Git.Output("checkout-script-ran"), commit = f.Git.Output("commit-script-ran");
        string checkoutScript = f.Git.Output("before-checkout.sh"), commitScript = f.Git.Output("before-commit.sh");
        await File.WriteAllTextAsync(checkoutScript, "#!/bin/sh\nprintf ran > " + ShellQuote(before) + "\nexit 8\n");
        await File.WriteAllTextAsync(commitScript, "#!/bin/sh\nprintf ran > " + ShellQuote(commit) + "\n");
        new ScriptsPreferencesStore().Save(new ScriptDefinition[]
        {
            new() { Name = "Direct checkout veto", Command = "/bin/sh", Arguments = "\"" + checkoutScript + "\"", OnEvent = ScriptEvent.BeforeCheckout, AskConfirmation = refuseConfirmation },
            new() { Name = "Must not reach commit", Command = "/bin/sh", Arguments = "\"" + commitScript + "\"", OnEvent = ScriptEvent.BeforeCommit }
        });
        CommitDialog dialog = await f.OpenAsync(TargetedCommitKind.Fixup, edited); string material = await f.MaterialAsync();
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "DetachedCheckout");
        if (refuseConfirmation)
        {
            await Until(() => Descendants(dialog).Any(window => window.Title == "Script")); Window confirmation = Descendants(dialog).Single(window => window.Title == "Script");
            Assert.That(confirmation.Owner, Is.SameAs(dialog), "A default checkout has no visible picker to own its script question.");
            PushProcessTestDriver.Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "No")));
        }
        await DefaultCommitStopped(dialog);
        Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(dialog.HasCommitted, Is.False); Assert.That(File.Exists(before), Is.EqualTo(!refuseConfirmation));
        Assert.That(File.Exists(commit), Is.False); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task DefaultCheckoutRevalidatesContainmentAfterBeforeCheckoutScriptsChangedTheAdvertisedBranch()
    {
        await using Scope f = await Scope.CreateAsync(); string old = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        await f.Git.ChangeAsync("later\n", "Edited commit"); GitCommit edited = (await f.Git.Repository.GetLogAsync(1, all: false)).Single();
        await f.Git.GitAsync("switch", "--detach", "HEAD"); await f.StageAsync(); new ReferencePreferencesStore().Save(new(UseDefaultCheckoutChanges: true));
        new ProcessDialogPreferencesStore().Save(new(true)); string script = f.Git.Output("move-containing-branch.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ngit branch -f main " + ShellQuote(old) + "\n");
        new ScriptsPreferencesStore().Save(new[] { new ScriptDefinition { Name = "Move containing branch", Command = "/bin/sh", Arguments = "\"" + script + "\"", OnEvent = ScriptEvent.BeforeCheckout } });
        CommitDialog dialog = await f.OpenAsync(TargetedCommitKind.Fixup, edited); string index = await f.IndexAsync();
        Click(dialog, "Commit"); Click(await BranchPrompt(dialog), "DetachedCheckout"); CheckoutBranchDialog checkout = await CheckoutPicker(dialog, acknowledgeProcesses: true);
        Assert.That(checkout.LastError, Does.Contain("no longer contains")); Assert.That(checkout.Succeeded, Is.False); Assert.That(dialog.HasCommitted, Is.False);
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(edited.Hash)); Assert.That(await f.Git.Repository.ResolveRevisionAsync("main"), Is.EqualTo(old));
        Assert.That(await f.IndexAsync(), Is.EqualTo(index)); checkout.Close(); await Idle(dialog); Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task HiddenDefaultCheckoutOwnsBothDestructiveConfirmationsWithTheVisibleWorkflowWindow(bool resetRemote)
    {
        await using Scope f = await Scope.CreateAsync(); await f.StageAsync();
        GitReference reference;
        if (resetRemote)
        {
            await f.Git.GitAsync("remote", "add", "origin", f.Git.Output("remote.git"));
            await f.Git.GitAsync("update-ref", "refs/remotes/origin/topic", "HEAD");
            reference = (await f.Git.Repository.GetReferencesAsync()).Single(value => value.Name == "refs/remotes/origin/topic");
        }
        else { reference = (await f.Git.Repository.GetReferencesAsync()).Single(value => value.Name == "refs/heads/main"); }
        Window owner = new() { Width = 800, Height = 600 }; f.Track(owner); owner.Show();
        CheckoutBranchDialog checkout = new(f.Git.Repository, reference); f.Track(checkout); checkout.Show(); await checkout.InitializeAsync();
        if (resetRemote)
        {
            Field<ComboBox>(checkout, "RemoteCheckoutMode").SelectedIndex = 2;
            Field<TextBox>(checkout, "CheckoutLocalName").Text = "main";
        }
        else { Field<RadioButton>(checkout, "CheckoutDiscardChanges").IsChecked = true; }
        checkout.Hide(); string material = await f.MaterialAsync();
        Task execution = checkout.ExecuteDefaultAsync(owner, CancellationToken.None);
        await Until(() => owner.OwnedWindows.Any(window => window.IsVisible)); Window confirmation = owner.OwnedWindows.Single(window => window.IsVisible);
        Assert.That(confirmation.Owner, Is.SameAs(owner)); Assert.That(confirmation.Title, Is.EqualTo(resetRemote ? "Reset branch" : "Discard local changes"));
        Assert.That(checkout.IsVisible, Is.False);
        PushProcessTestDriver.Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")));
        await execution; Assert.That(checkout.Succeeded, Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase(false, "en-US")] [TestCase(true, "fr-FR")]
    public async Task ToolbarOverflowPreservesCanonicalCommandsFullCaptionsAndPinnedOptionsAcrossResize(bool dark, string culture)
    {
        await using Scope f = await Scope.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CommitDialog dialog = await f.OpenAsync(dark: dark); Field<TextBox>(dialog, "Message").Text = "Preserve this draft";
        Menu toolbar = Field<Menu>(dialog, "CommitToolbar"); CompactMenuToolbar overflow = CompactMenuToolbar.For(toolbar)!;
        Assert.That(overflow, Is.Not.Null); MenuItem[] canonical = overflow.CanonicalItems.ToArray();
        Assert.That(canonical.Select(item => item.Name), Is.EqualTo(new[] { "CommitMessageHistory", "Options", "commitTemplatesToolStripMenuItem", "CommitCreateBranch" }));
        int iteration = 0;
        foreach (double width in new[] { 1100d, 860d, 1600d, 860d })
        {
            dialog.Width = width; PumpToolbar(dialog); Assert.That(toolbar.Items.OfType<MenuItem>().Take(4), Is.EqualTo(canonical));
            AssertToolbarGeometry(dialog, toolbar, overflow);
            TestContext.WriteLine($"Window={width}; toolbar={toolbar.Bounds.Width}; natural headers=" + string.Join(", ", canonical.Select(item => item.Name + ":visible=" + item.IsVisible + ":width=" + item.Bounds.Width + ":overflow=" + overflow.IsOverflowed(item))));
            if (width == 1600) { Assert.That(overflow.Overflow.IsVisible, Is.False); Assert.That(canonical.All(item => item.IsVisible), Is.True); }
            if (iteration++ < 2) { Capture(dialog, "classic-commit-toolbar-" + culture + "-" + (dark ? "dark-" : "light-") + width + ".png"); }
        }
        MenuItem create = canonical[3]; Assert.That(overflow.IsOverflowed(create), Is.True);
        PushProcessTestDriver.Pointer(overflow.Overflow); MenuItem proxy = overflow.ProxyFor(create);
        Assert.That(overflow.Overflow.Items.OfType<MenuItem>(), Does.Contain(proxy));
        Assert.That(proxy.Tag, Is.SameAs(create)); Assert.That(HeaderText(proxy.Header), Is.EqualTo(HeaderText(create.Header)));
        string head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        PushProcessTestDriver.Pointer(proxy); CreateBranchDialog child = await CreateChild(dialog); await Until(() => !child.IsBusy);
        Field<TextBox>(child, "CreateBranchName").Text = "overflow/created"; Field<CheckBox>(child, "CheckoutAfterCreate").IsChecked = false;
        Click(child, "CreateBranch"); await Idle(dialog);
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("refs/heads/overflow/created"), Is.EqualTo(head));
        Assert.That((await f.Git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main")); Assert.That(dialog.LastError, Is.Null);
        Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("Preserve this draft")); Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task ToolbarOverflowCreateProxyUsesTheSameCommandThroughNativeEnterAndCancel()
    {
        await using Scope f = await Scope.CreateAsync(); CommitDialog dialog = await f.OpenAsync(); dialog.Width = 860; PumpToolbar(dialog);
        CompactMenuToolbar overflow = CompactMenuToolbar.For(Field<Menu>(dialog, "CommitToolbar"))!; MenuItem create = overflow.CanonicalItems.Single(item => item.Name == "CommitCreateBranch");
        Assert.That(overflow.IsOverflowed(create), Is.True); string material = await f.MaterialAsync();
        PushProcessTestDriver.Pointer(overflow.Overflow); MenuItem proxy = overflow.ProxyFor(create);
        overflow.Overflow.SelectedItem = proxy; proxy.Focus(NavigationMethod.Directional); Press(dialog, PhysicalKey.Enter);
        CreateBranchDialog child = await CreateChild(dialog); await Until(() => !child.IsBusy);
        Assert.That(Field<TextBox>(child, "CreateBranchName").Text, Is.EqualTo("main"));
        Press(child, PhysicalKey.Escape, releaseOn: dialog); await Idle(dialog); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
        Assert.That(dialog.LastError, Is.Null); Assert.That(overflow.Overflow.IsSubMenuOpen, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ConventionalShortcutWhileTemplatesOverflowKeepsStationaryPointerSelectionEnterUndoAndEscape(bool withScope)
    {
        await using Scope f = await Scope.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CommitMessagePreferencesStore preferences = new(); var baseline = preferences.Load(); preferences.SaveChanges(baseline, baseline.WithTemplate(0, new("Native template", "Never apply this body")));
        CommitDialog dialog = await f.OpenAsync(dark: true); dialog.Width = 860; PumpToolbar(dialog); TextBox message = Field<TextBox>(dialog, "Message"); message.Text = "subject\n\nbody λ";
        Menu toolbar = Field<Menu>(dialog, "CommitToolbar"); CompactMenuToolbar overflow = CompactMenuToolbar.For(toolbar)!;
        MenuItem templates = overflow.CanonicalItems.Single(item => item.Name == "commitTemplatesToolStripMenuItem");
        Assert.That(overflow.IsOverflowed(templates), Is.True); string material = await f.MaterialAsync();
        PushProcessTestDriver.Pointer(overflow.Overflow);
        MenuItem priorPointerTarget = overflow.ProxyFor(overflow.CanonicalItems.Single(item => item.Name == "CommitCreateBranch"));
        HoverToolbarItem(priorPointerTarget);
        // No refocus or pointer repair: the real pointer stays on the old More entry.
        Press(dialog, PhysicalKey.T, RawInputModifiers.Control | (withScope ? RawInputModifiers.Shift : RawInputModifiers.None));
        Assert.That(toolbar.SelectedItem, Is.SameAs(templates)); Assert.That(overflow.Overflow.IsSubMenuOpen, Is.False);
        Assert.That(templates.IsSubMenuOpen, Is.True); MenuItem conventional = templates.Items.OfType<MenuItem>().Single(item => item.Name == "ConventionalCommits");
        MenuItem feat = conventional.Items.OfType<MenuItem>().Single(item => item.Name == "Conventional_feat");
        Assert.That(templates.SelectedItem, Is.SameAs(conventional)); Assert.That(conventional.SelectedItem, Is.SameAs(feat));
        Assert.That(feat.IsFocused, Is.True); Assert.That(message.Text, Is.EqualTo("subject\n\nbody λ"));
        Press(dialog, PhysicalKey.Enter); Assert.That(message.Text, Is.EqualTo(withScope ? "feat(): subject\n\nbody λ" : "feat: subject\n\nbody λ")); Assert.That(message.IsFocused, Is.True);
        Press(dialog, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(message.Text, Is.EqualTo("subject\n\nbody λ"));
        Press(dialog, PhysicalKey.T, RawInputModifiers.Control); Press(dialog, PhysicalKey.Escape);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
        Press(dialog, PhysicalKey.T, RawInputModifiers.Control); dialog.Width = 1600; PumpToolbar(dialog);
        Assert.That(templates.IsSubMenuOpen, Is.False); Assert.That(overflow.IsOverflowed(templates), Is.False); Assert.That(dialog.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task OverflowTemplatesProxyOpensTheCanonicalTreeAndTheSharedSettingsWindow()
    {
        await using Scope f = await Scope.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CommitDialog dialog = await f.OpenAsync(); dialog.Width = 860; PumpToolbar(dialog); string material = await f.MaterialAsync();
        Menu toolbar = Field<Menu>(dialog, "CommitToolbar"); CompactMenuToolbar overflow = CompactMenuToolbar.For(toolbar)!;
        MenuItem templates = overflow.CanonicalItems.Single(item => item.Name == "commitTemplatesToolStripMenuItem"); Assert.That(overflow.IsOverflowed(templates), Is.True);
        PushProcessTestDriver.Pointer(overflow.Overflow); PushProcessTestDriver.Pointer(overflow.ProxyFor(templates));
        await Until(() => templates.IsSubMenuOpen); Assert.That(toolbar.SelectedItem, Is.SameAs(templates));
        MenuItem settings = templates.Items.OfType<MenuItem>().Single(item => item.Name == "CommitMessageSettings");
        Assert.That(settings.Parent, Is.SameAs(templates), "Overflow must open the one canonical tree, not a copy of its commands or editors.");
        PushProcessTestDriver.Pointer(settings); await Until(() => dialog.OwnedWindows.OfType<CommitMessageSettingsDialog>().Any());
        CommitMessageSettingsDialog child = dialog.OwnedWindows.OfType<CommitMessageSettingsDialog>().Single();
        Press(child, PhysicalKey.Escape, releaseOn: dialog); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    private static void PumpToolbar(TopLevel window)
    { for (int frame = 0; frame < 10; frame++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static string? HeaderText(object? value)
    {
        if (value is not Control control) { return value?.ToString(); }
        return control.GetSelfAndVisualDescendants().OfType<AccessText>().FirstOrDefault()?.Text
            ?? control.GetSelfAndVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;
    }
    private static void HoverToolbarItem(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control)!; PumpToolbar(root);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        Visual? hit = root.InputHitTest(point) as Visual;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && new Rect(default, root.ClientSize).Contains(point)
            && hit?.GetSelfAndVisualAncestors().Contains(control) == true, Is.True);
        root.MouseMove(point); Dispatcher.UIThread.RunJobs();
    }
    private static void AssertToolbarGeometry(Window window, Menu menu, CompactMenuToolbar overflow)
    {
        Rect Bounds(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
        Rect outer = Bounds(menu); MenuItem pinned = overflow.CanonicalItems.Single(item => item.Name == "Options");
        Assert.That(Bounds(pinned).Right, Is.EqualTo(outer.Right).Within(.5)); Assert.That(menu.Bounds.Height, Is.EqualTo(28));
        foreach (MenuItem item in overflow.CanonicalItems.Where(item => !overflow.IsOverflowed(item)).Concat(overflow.Overflow.IsVisible ? new[] { overflow.Overflow } : Array.Empty<MenuItem>()))
        {
            Rect inner = Bounds(item); Assert.That(inner.Width, Is.GreaterThan(0));
            Assert.That(inner.Left, Is.GreaterThanOrEqualTo(outer.Left - .5), item.Name); Assert.That(inner.Right, Is.LessThanOrEqualTo(outer.Right + .5), item.Name);
            Assert.That(inner.Top, Is.GreaterThanOrEqualTo(outer.Top - .5)); Assert.That(inner.Bottom, Is.LessThanOrEqualTo(outer.Bottom + .5));
            if (item.Header is Control header)
            {
                foreach (TextBlock text in header.GetSelfAndVisualDescendants().OfType<TextBlock>())
                { Assert.That(text.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, item.Name + " caption must be complete."); Assert.That(text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(text.Bounds.Width + .5)); }
            }
        }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static IEnumerable<Window> Descendants(Window owner)
    { foreach (Window child in owner.OwnedWindows.ToArray()) { yield return child; foreach (Window nested in Descendants(child)) { yield return nested; } } }
    private static async Task DefaultCommitStopped(CommitDialog owner)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (owner.IsBusy || owner.OwnedWindows.Count != 0)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (GitProcessDialog process in Descendants(owner).OfType<GitProcessDialog>().Where(window => !window.IsRunning && window.IsVisible).ToArray())
            { Click(process, "Ok"); }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<CheckoutBranchDialog> CheckoutPicker(CommitDialog owner, bool acknowledgeProcesses = false)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!owner.OwnedWindows.OfType<CheckoutBranchDialog>().Any(window => window.IsVisible && !window.IsBusy))
        {
            Dispatcher.UIThread.RunJobs();
            if (acknowledgeProcesses)
            {
                foreach (GitProcessDialog process in Descendants(owner).OfType<GitProcessDialog>().Where(window => !window.IsRunning && window.IsVisible).ToArray())
                { Click(process, "Ok"); }
            }
            await Task.Delay(10, timeout.Token);
        }
        return owner.OwnedWindows.OfType<CheckoutBranchDialog>().Single(window => window.IsVisible);
    }

    private static MenuItem RevealCreateItem(CommitDialog dialog)
    {
        PumpToolbar(dialog);
        MenuItem canonical = CreateItem(dialog);
        CompactMenuToolbar toolbar = CompactMenuToolbar.For(Field<Menu>(dialog, "CommitToolbar"))!;
        if (!toolbar.IsOverflowed(canonical))
        {
            Assert.That(canonical.IsEffectivelyVisible, Is.True);
            return canonical;
        }
        Assert.That(canonical.IsVisible, Is.False);
        PushProcessTestDriver.Pointer(toolbar.Overflow);
        MenuItem proxy = toolbar.ProxyFor(canonical);
        PumpToolbar(TopLevel.GetTopLevel(proxy)!);
        Assert.That(proxy.IsEffectivelyVisible, Is.True);
        Assert.That(proxy.Tag, Is.SameAs(canonical));
        Assert.That(HeaderText(proxy.Header), Is.EqualTo(HeaderText(canonical.Header)));
        return proxy;
    }
    private static MenuItem CreateItem(CommitDialog dialog) => Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "CommitCreateBranch");
    private static string? RemoteText(CommitDialog dialog) => Field<Button>(dialog, "CommitBranchRemote").Content as string;
    private static string SelectedPath(TreeView tree) => (tree.SelectedItem as TreeViewItem)?.Tag is GitChange change ? change.Path : "";
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static async Task<CreateBranchDialog> CreateChild(CommitDialog parent) { await Until(() => parent.OwnedWindows.OfType<CreateBranchDialog>().Any()); return parent.OwnedWindows.OfType<CreateBranchDialog>().Single(); }
    private static async Task<Window> BranchPrompt(CommitDialog parent)
    {
        await Until(() => parent.OwnedWindows.Any(window => window.Name == "CommitBranchChoice"));
        Window prompt = parent.OwnedWindows.Single(window => window.Name == "CommitBranchChoice");
        Dispatcher.UIThread.RunJobs(); prompt.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); return prompt;
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None, Window? releaseOn = null)
    { window.KeyPressQwerty(key, mods); (releaseOn ?? window).KeyReleaseQwerty(key, mods); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition) { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)); while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); } }
    private static Task Idle(CommitDialog dialog) => Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0);
    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Scope(GitFixture git) : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        internal GitFixture Git => git;
        internal CommitPreferencesStore Store { get; } = new();
        internal static async Task<Scope> CreateAsync(bool unborn = false)
        {
            GitFixture git = await GitFixture.CreateAsync(unborn); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
            new CommitPreferencesStore().Save(new() { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false, SelectStagedOnMessageFocus = false }); return new(git);
        }
        internal void Track(Window window) => _windows.Add(window);
        internal async Task StageAsync() { await File.AppendAllTextAsync(git.File, "staged addition λ\n"); await git.Repository.StageAllAsync(); }
        internal async Task<CommitDialog> OpenAsync(TargetedCommitKind kind = TargetedCommitKind.Normal, GitCommit? edited = null, bool dark = false)
        {
            CommitDialog dialog = new(git.Repository, Store, initialKind: kind, targetCommit: edited) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Track(dialog); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            Assert.That(dialog.LastError, Is.Null); return dialog;
        }
        internal async Task<string> IndexAsync() => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(git.PathInRepository(".git/index"))));
        internal async Task<string> MaterialAsync() => (await git.GitAsync("show-ref")).StandardOutput + await git.Repository.ResolveRevisionAsync("HEAD") + await IndexAsync() + await File.ReadAllTextAsync(git.File);
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
                    window.Close(); await Until(() => !window.IsVisible && (window is not CommitDialog dialog || !dialog.IsBusy));
                }
            }
            finally { git.Dispose(); }
        }
    }
}
