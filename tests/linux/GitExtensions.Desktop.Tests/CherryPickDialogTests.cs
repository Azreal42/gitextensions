using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CherryPickDialogTests
{
    [AvaloniaTest]
    public async Task OriginalDefaultsApplyWithoutCommitAndRememberOptionsOnlyAfterAcceptance()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "picked\n");
        CherryPickDialog dialog = await f.ShowAsync(pick);
        Assert.That(dialog.ReadOptions(), Is.EqualTo(new GitCherryPickOptions(pick)));
        Assert.That(Field<StackPanel>(dialog, "CherryPickParentPanel").IsVisible, Is.False);
        Assert.That(Field<CheckBox>(dialog, "CherryPickAutoCommit").IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<CheckBox>(dialog, "CherryPickAutoCommit").Height, Is.EqualTo(24));
        Assert.That(Field<Button>(dialog, "ExecuteCherryPick").IsDefault, Is.True);
        Pointer(Field<CheckBox>(dialog, "CherryPickAddReference")); Pointer(Field<Button>(dialog, "ExecuteCherryPick"));
        await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Applied && dialog.SequenceAccepted, Is.True, dialog.LastError); Assert.That(await f.Head(), Is.EqualTo(f.Initial));
        Assert.That(await f.Text("show", ":picked.txt"), Is.EqualTo("picked"));
        Assert.That(new CherryPickPreferencesStore().Load(), Is.EqualTo(new CherryPickPreferences(false, true)));
        CherryPickDialog later = await f.ShowAsync(pick); Pointer(Field<CheckBox>(later, "CherryPickAutoCommit")); Pointer(Field<CheckBox>(later, "CherryPickAddReference"));
        Press(later, PhysicalKey.Escape); await Until(() => !later.IsVisible);
        Assert.That(new CherryPickPreferencesStore().Load(), Is.EqualTo(new CherryPickPreferences(false, true)), "Escape must not persist changed options.");
    }
    [AvaloniaTest]
    public async Task KeyboardOptionAndDefaultEnterProduceAutomaticOriginReferencedCommit()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "picked\n"); CherryPickDialog dialog = await f.ShowAsync(pick);
        Assert.That(Field<CheckBox>(dialog, "CherryPickAutoCommit").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Space); Press(dialog, PhysicalKey.Tab); Press(dialog, PhysicalKey.Space);
        Assert.That(dialog.ReadOptions(), Is.EqualTo(new GitCherryPickOptions(pick, true, true)), "Keyboard Space/Tab must select both original options before Enter.");
        Press(dialog, PhysicalKey.Enter);
        await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Applied, Is.True, dialog.LastError); Assert.That(await f.Head(), Is.Not.EqualTo(f.Initial));
        Assert.That(await f.Text("show", "-s", "--format=%B", "HEAD"), Does.Contain("(cherry picked from commit " + pick + ")"));
        Assert.That(new CherryPickPreferencesStore().Load(), Is.EqualTo(new CherryPickPreferences(true, true)));
    }
    [AvaloniaTest]
    public async Task MergeParentRowsUsePointerSelectionAndResetForAChosenRevision()
    {
        await using Fixture f = await Fixture.CreateAsync(); string merge = await f.MergeAsync(); CherryPickDialog dialog = await f.ShowAsync(merge);
        ListBox parents = Field<ListBox>(dialog, "CherryPickParents"); Assert.That(dialog.ReadOptions().MainlineParent, Is.EqualTo(1));
        Assert.That(parents.IsKeyboardFocusWithin, Is.True); Pointer((Control)parents.ContainerFromIndex(1)!);
        Assert.That(dialog.ReadOptions().MainlineParent, Is.EqualTo(2)); parents.SelectedIndex = -1;
        Assert.Throws<InvalidOperationException>(() => dialog.ReadOptions());
        Pointer(Field<Button>(dialog, "ChooseCherryPickRevision")); await Until(() => dialog.OwnedWindows.OfType<CherryPickRevisionPicker>().Any(window => !window.IsBusy));
        CherryPickRevisionPicker picker = dialog.OwnedWindows.OfType<CherryPickRevisionPicker>().Single(); ListBox revisions = Field<ListBox>(picker, "CherryPickChooseRevision");
        Assert.That((revisions.SelectedItem as GitCommit)?.Hash, Is.EqualTo(merge), "Original current revision must be preselected.");
        Pointer(Field<Button>(picker, "ChooseCherryPickCommit")); await Until(() => !picker.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.ReadOptions().MainlineParent, Is.EqualTo(1), "Choosing a revision creates its own first-parent selection.");
        await Until(() => { Pump(dialog); return parents.ContainerFromIndex(1) is Control; });
        Pointer((Control)parents.ContainerFromIndex(1)!); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Applied, Is.True, dialog.LastError); Assert.That(File.Exists(f.File("left.txt")), Is.True); Assert.That(File.Exists(f.File("right.txt")), Is.False);
    }
    [AvaloniaTest]
    public async Task RevisionChooserActuallyReplacesTheSelectedCommitAndNullSelectionStartsWithOriginalFocus()
    {
        await using Fixture f = await Fixture.CreateAsync(); string first = await f.TopicAsync("first.txt", "first\n"); await f.Git("checkout", "topic"); await f.Commit("second.txt", "second\n", "Second selection"); string second = await f.Head(); await f.Git("checkout", "main");
        CherryPickDialog dialog = await f.ShowAsync(null); Assert.That(Field<Button>(dialog, "ExecuteCherryPick").IsEnabled, Is.False); Assert.That(Field<CheckBox>(dialog, "CherryPickAutoCommit").IsKeyboardFocusWithin, Is.True);
        Pointer(Field<Button>(dialog, "ChooseCherryPickRevision")); await Until(() => dialog.OwnedWindows.OfType<CherryPickRevisionPicker>().Any(window => !window.IsBusy));
        CherryPickRevisionPicker picker = dialog.OwnedWindows.OfType<CherryPickRevisionPicker>().Single(); ListBox list = Field<ListBox>(picker, "CherryPickChooseRevision");
        int row = list.Items.OfType<GitCommit>().Select((commit, index) => (commit, index)).Single(value => value.commit.Hash == first).index;
        Pointer((Control)list.ContainerFromIndex(row)!); Pointer(Field<Button>(picker, "ChooseCherryPickCommit")); await Until(() => !picker.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Revision, Is.EqualTo(first)); Assert.That(dialog.Revision, Is.Not.EqualTo(second)); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(File.Exists(f.File("first.txt")), Is.True); Assert.That(File.Exists(f.File("second.txt")), Is.False);
    }
    [AvaloniaTest]
    public async Task BrowseRealMultiSelectionContextMenuUsesOldestRowFirstCopiesOptionsAndStopsAtSecondAbort()
    {
        await using Fixture f = await Fixture.CreateAsync(); string first = await f.TopicAsync("first.txt", "first\n"); await f.Git("checkout", "topic"); await f.Commit("second.txt", "second\n", "Second"); string second = await f.Head(); await f.Git("checkout", "main");
        MainWindow main = new() { Width = 1280, Height = 752, RequestedThemeVariant = ThemeVariant.Dark }; f.Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy); Pump(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory"); GitCommit[] rows = history.Items.OfType<GitCommit>().ToArray(); int firstIndex = Array.FindIndex(rows, commit => commit.Hash == first), secondIndex = Array.FindIndex(rows, commit => commit.Hash == second);
        Assert.That(firstIndex, Is.GreaterThan(secondIndex), "Fixture must have the original newest-first grid display.");
        Pointer((Control)history.ContainerFromIndex(secondIndex)!); Pointer((Control)history.ContainerFromIndex(firstIndex)!, RawInputModifiers.Control); await Until(() => !main.IsBusy);
        Assert.That(history.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { first, second }));
        Pointer((Control)history.ContainerFromIndex(firstIndex)!, button: MouseButton.Right); Pump(main);
        ContextMenu menu = history.ContextMenu!; Assert.That(menu.IsOpen, Is.True, "The actual Browse context menu must open through a right pointer click.");
        MenuItem action = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Cherry-pick"); Pointer(action);
        await Until(() => main.OwnedWindows.OfType<CherryPickDialog>().Any(window => window.Revision == first && !window.IsBusy)); CherryPickDialog one = main.OwnedWindows.OfType<CherryPickDialog>().Single();
        Assert.That(one.ActualThemeVariant, Is.EqualTo(main.ActualThemeVariant)); Pointer(Field<CheckBox>(one, "CherryPickAddReference")); Pointer(Field<Button>(one, "ExecuteCherryPick"));
        await Until(() => main.OwnedWindows.OfType<CherryPickDialog>().Any(window => window.Revision == second && !window.IsBusy)); CherryPickDialog two = main.OwnedWindows.OfType<CherryPickDialog>().Single();
        Assert.That(two, Is.Not.SameAs(one)); Assert.That(two.ReadOptions(), Is.EqualTo(new GitCherryPickOptions(second, AddCommitReference: true)), "Only the two original checkboxes must transfer to the next commit's dialog.");
        Pointer(Field<Button>(two, "CancelCherryPick")); await Until(() => !main.IsBusy && !main.OwnedWindows.Any());
        Assert.That(await f.Head(), Is.EqualTo(f.Initial)); Assert.That(File.ReadAllText(f.File("first.txt")), Is.EqualTo("first\n")); Assert.That(await f.Text("show", ":first.txt"), Is.EqualTo("first"));
        Assert.That(File.Exists(f.File("second.txt")), Is.False, "Aborting the second dialogue stops the sequence while retaining the first pick."); Assert.That(main.LastException, Is.Null);
    }
    [AvaloniaTest]
    public async Task CopyOptionsCopiesOnlyCheckboxesAndEachMergeRetainsItsOwnParent()
    {
        await using Fixture f = await Fixture.CreateAsync(); string merge = await f.MergeAsync(); CherryPickDialog first = await f.ShowAsync(merge), next = await f.ShowAsync(merge);
        Pointer(Field<CheckBox>(first, "CherryPickAutoCommit")); Pointer(Field<CheckBox>(first, "CherryPickAddReference")); Pointer((Control)Field<ListBox>(first, "CherryPickParents").ContainerFromIndex(1)!);
        next.CopyOptions(first); Assert.That(next.ReadOptions(), Is.EqualTo(new GitCherryPickOptions(merge, true, true, 1))); Assert.That(first.ReadOptions().MainlineParent, Is.EqualTo(2));
    }
    [AvaloniaTest]
    public async Task NoCommitConflictEditorAndContinuePreserveOriginalHeadAndPreexistingIndex()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); string head = await f.Head(); f.Write("prior.txt", "prior\n"); await f.Git("add", "prior.txt");
        CherryPickDialog dialog = await f.ShowAsync(pick); dialog.RequestedThemeVariant = ThemeVariant.Dark; Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsBusy && Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible);
        Assert.That(dialog.LastError, Is.Null); Assert.That(Field<Button>(dialog, "ContinueCherryPick").IsEnabled, Is.False); Assert.That(File.Exists(f.Metadata("CHERRY_PICK_HEAD")), Is.False);
        Pointer(Field<Button>(dialog, "ResolveCherryPickConflicts")); await Until(() => dialog.OwnedWindows.OfType<PatchConflictDialog>().Any(window => !window.IsBusy)); PatchConflictDialog editor = dialog.OwnedWindows.OfType<PatchConflictDialog>().Single();
        Assert.That(editor.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark)); TextBox text = Field<TextBox>(editor, "PatchConflictText"); Pointer(text); Press(editor, PhysicalKey.A, RawInputModifiers.Control); editor.KeyTextInput("resolved λ\n"); Pointer(Field<Button>(editor, "SavePatchResolution"));
        await Until(() => !editor.IsVisible && !dialog.IsBusy && Field<Button>(dialog, "ContinueCherryPick").IsEnabled); Pointer(Field<Button>(dialog, "ContinueCherryPick")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Applied, Is.True, dialog.LastError); Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(await f.Text("show", ":base.txt"), Is.EqualTo("resolved λ")); Assert.That(await f.Text("show", ":prior.txt"), Is.EqualTo("prior"));
    }
    [AvaloniaTest]
    public async Task AutomaticConflictStagesResolutionThenContinuesNativeCommit()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); string head = await f.Head(); CherryPickDialog dialog = await f.ShowAsync(pick); Pointer(Field<CheckBox>(dialog, "CherryPickAutoCommit")); Pointer(Field<CheckBox>(dialog, "CherryPickAddReference"));
        Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsBusy && Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible);
        Assert.That(File.Exists(f.Metadata("CHERRY_PICK_HEAD")), Is.True); f.Write("base.txt", "resolved automatic\n"); await f.Git("add", "base.txt"); Pointer(Field<Button>(dialog, "RefreshCherryPickConflicts")); await Until(() => !dialog.IsBusy && Field<Button>(dialog, "ContinueCherryPick").IsEnabled);
        Pointer(Field<Button>(dialog, "ContinueCherryPick")); await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(await f.Head(), Is.Not.EqualTo(head)); Assert.That(await f.Text("show", "HEAD:base.txt"), Is.EqualTo("resolved automatic")); Assert.That(await f.Text("show", "-s", "--format=%B", "HEAD"), Does.Contain(pick)); Assert.That(File.Exists(f.Metadata("CHERRY_PICK_HEAD")), Is.False);
    }
    [AvaloniaTest]
    public async Task ClosingPendingDialoguePreservesGitConflictAndExplicitSkipRestoresTheSavedIndex()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); f.Write("prior.txt", "prior\n"); await f.Git("add", "prior.txt"); string head = await f.Head(); byte[] index = File.ReadAllBytes(f.Metadata("index"));
        CherryPickDialog dialog = await f.ShowAsync(pick);
        int pickClicks = 0, cancelClicks = 0;
        Field<Button>(dialog, "ExecuteCherryPick").AddHandler(Button.ClickEvent, (_, _) => pickClicks++, RoutingStrategies.Tunnel, handledEventsToo: true);
        Field<Button>(dialog, "CancelCherryPick").AddHandler(Button.ClickEvent, (_, _) => cancelClicks++, RoutingStrategies.Tunnel, handledEventsToo: true);
        string State() => $"visible={dialog.IsVisible}, busy={dialog.IsBusy}, recovery={Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible}, pickClicks={pickClicks}, cancelClicks={cancelClicks}, error={dialog.LastError}, exception={dialog.LastException}";
        Pointer(Field<Button>(dialog, "ExecuteCherryPick"));
        await Until(() => !dialog.IsBusy && Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible, () => "First pick recovery: " + State());
        Button cancel = Field<Button>(dialog, "CancelCherryPick");
        TestContext.WriteLine("Before cancellation layout: " + PointerTarget(cancel).Description);
        await Until(() => { Pump(dialog); return PointerTarget(cancel).Ready; }, () => "Cancel pointer target: " + PointerTarget(cancel).Description + "; " + State());
        TestContext.WriteLine("Cancellation pointer target: " + PointerTarget(cancel).Description);
        Pointer(cancel);
        await Until(() => !dialog.IsVisible, () => "Cancel pending dialogue: " + State());
        Assert.That((await f.Repository.GetStatusAsync()).Changes.Any(change => change.IsConflict), Is.True, "Original dialogue Abort cancels the dialogue, retaining native pending resolution."); Assert.That(await f.Head(), Is.EqualTo(head));
        await f.Git("reset", "--hard", head); f.Write("prior.txt", "prior\n"); await f.Git("add", "prior.txt"); index = File.ReadAllBytes(f.Metadata("index"));
        dialog = await f.ShowAsync(pick); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsBusy && Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible);
        Button skip = Field<Button>(dialog, "SkipCherryPick");
        TestContext.WriteLine("Before skip layout: " + PointerTarget(skip).Description);
        await Until(() => { Pump(dialog); return PointerTarget(skip).Ready; }, () => "Skip pointer target: " + PointerTarget(skip).Description + "; " + State());
        TestContext.WriteLine("Skip pointer target: " + PointerTarget(skip).Description);
        Pointer(skip); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Skipped && dialog.SequenceAccepted, Is.True); Assert.That(File.ReadAllBytes(f.Metadata("index")), Is.EqualTo(index)); Assert.That(File.ReadAllText(f.File("base.txt")), Is.EqualTo("ours\n")); Assert.That(await f.Head(), Is.EqualTo(head));
    }
    [AvaloniaTest]
    public async Task RenamedConflictAbortAndSkipRestoreActualTargetAndThePreexistingIndex()
    {
        foreach (bool skip in new[] { false, true })
        {
            await using Fixture f = await Fixture.CreateAsync(); await f.Commit("original.txt", "line1\nline2\nline3\n", "Rename base");
            string pick = await f.TopicAsync("original.txt", "line1\ntheirs\nline3\n"); await f.Git("mv", "original.txt", "renamed.txt"); await f.Git("commit", "-m", "Rename checkout"); await f.Commit("renamed.txt", "line1\nours\nline3\n", "Checkout change");
            f.Write("prior.txt", "preexisting staged\n"); await f.Git("add", "prior.txt"); f.Write("prior.txt", "preexisting worktree\n");
            byte[] index = File.ReadAllBytes(f.Metadata("index")); string head = await f.Head();
            CherryPickDialog dialog = await f.ShowAsync(pick); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsBusy && Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible);
            Assert.That(Field<ListBox>(dialog, "CherryPickConflictFiles").SelectedItem, Is.EqualTo("renamed.txt"));
            Pointer(Field<Button>(dialog, skip ? "SkipCherryPick" : "AbortCherryPickOperation"));
            if (!skip)
            {
                await Until(() => dialog.OwnedWindows.Any()); Window confirmation = dialog.OwnedWindows.Single(); Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
            }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.Skipped, Is.EqualTo(skip)); Assert.That(File.ReadAllText(f.File("renamed.txt")), Is.EqualTo("line1\nours\nline3\n")); Assert.That(File.Exists(f.File("original.txt")), Is.False);
            Assert.That(File.ReadAllBytes(f.Metadata("index")), Is.EqualTo(index)); Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(await f.Text("show", ":prior.txt"), Is.EqualTo("preexisting staged")); Assert.That(File.ReadAllText(f.File("prior.txt")), Is.EqualTo("preexisting worktree\n"));
        }
    }
    [AvaloniaTest]
    public async Task ExplicitAbortRequiresConfirmationThenRestoresPendingGitStateWithoutSavingOptions()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); string head = await f.Head(); byte[] index = File.ReadAllBytes(f.Metadata("index")); CherryPickDialog dialog = await f.ShowAsync(pick); Pointer(Field<CheckBox>(dialog, "CherryPickAutoCommit"));
        Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsBusy && Field<StackPanel>(dialog, "CherryPickRecovery").IsVisible); Pointer(Field<Button>(dialog, "AbortCherryPickOperation")); await Until(() => dialog.OwnedWindows.Any()); Window confirmation = dialog.OwnedWindows.Single();
        Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel)); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(File.ReadAllBytes(f.Metadata("index")), Is.EqualTo(index)); Assert.That(File.ReadAllText(f.File("base.txt")), Is.EqualTo("ours\n")); Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(File.Exists(f.Metadata("CHERRY_PICK_HEAD")), Is.False); Assert.That(new CherryPickPreferencesStore().Load(), Is.EqualTo(new CherryPickPreferences()));
    }
    [AvaloniaTest]
    public async Task SequencePrevalidationRejectsBadLaterRevisionAndCancellationStopsAVisibleModal()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "picked\n"); Window owner = new() { Width = 700, Height = 500 }; f.Windows.Add(owner); owner.Show();
        GitCommandException? invalid = null;
        try { await CherryPickSequence.ShowAsync(owner, f.Repository, [pick, "missing"]); } catch (GitCommandException error) { invalid = error; }
        Assert.That(invalid, Is.Not.Null); Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(await f.Head(), Is.EqualTo(f.Initial)); Assert.That(File.Exists(f.File("picked.txt")), Is.False);
        using CancellationTokenSource cancelled = new(); Task<bool> sequence = CherryPickSequence.ShowAsync(owner, f.Repository, [pick], cancelled.Token); await Until(() => owner.OwnedWindows.OfType<CherryPickDialog>().Any()); cancelled.Cancel();
        await Until(() => sequence.IsCompleted); Assert.That(await sequence, Is.False); Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(await f.Head(), Is.EqualTo(f.Initial));
    }
    [AvaloniaTest]
    public async Task SuccessfulPickReusesOriginalRememberedSubmoduleUpdateDecision()
    {
        await using Fixture f = await Fixture.CreateAsync(); string module = Path.Combine(f.Root, "module"); Directory.CreateDirectory(module);
        GitRunner runner = new();
        foreach (string[] arguments in new[] { new[] { "init", "-b", "main" }, ["config", "user.name", "Module Test"], ["config", "user.email", "module@example.invalid"], ["config", "commit.gpgsign", "false"], ["config", "core.hooksPath", "/dev/null"] })
        { Assert.That((await runner.RunAsync(module, arguments)).Success, Is.True); }
        await File.WriteAllTextAsync(Path.Combine(module, "data.txt"), "module\n"); Assert.That((await runner.RunAsync(module, ["add", "."])).Success, Is.True); Assert.That((await runner.RunAsync(module, ["commit", "-m", "Module"])).Success, Is.True);
        await f.Git("-c", "protocol.file.allow=always", "submodule", "add", module, "child"); await f.Git("commit", "-m", "Register module"); f.Initial = await f.Head(); string pick = await f.TopicAsync("picked.txt", "picked\n");
        CherryPickDialog dialog = await f.ShowAsync(pick); dialog.RequestedThemeVariant = ThemeVariant.Dark; Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any()); CheckoutSubmodulePrompt prompt = dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single();
        Assert.That(prompt.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark)); Pointer(Field<CheckBox>(prompt, "RememberSubmoduleCheckout")); Pointer(Field<Button>(prompt, "SkipCheckoutSubmodules")); await Until(() => !dialog.IsVisible);
        Assert.That(dialog.Applied, Is.True); Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.False); Assert.That(await f.Head(), Is.EqualTo(f.Initial));
        await f.Git("reset", "--hard", f.Initial); dialog = await f.ShowAsync(pick); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsVisible);
        Assert.That(dialog.OwnedWindows, Is.Empty, "Remembered No must suppress the next submodule prompt exactly as the original workflow.");
        await f.Git("reset", "--hard", f.Initial); new SubmoduleCheckoutPreferencesStore().Remember(true);
        int callbacks = 0; Window? callbackOwner = null;
        dialog = new CherryPickDialog(f.Repository, pick, afterSubmoduleUpdateAsync: (owner, _) => { callbacks++; callbackOwner = owner; return Task.CompletedTask; });
        f.Windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync(); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => !dialog.IsVisible);
        Assert.That(dialog.Applied, Is.True, dialog.LastError); Assert.That(callbacks, Is.EqualTo(1), "AutoCompile callback follows successful native update only."); Assert.That(callbackOwner, Is.SameAs(dialog));
        await f.Git("reset", "--hard", f.Initial); File.Delete(new SubmoduleCheckoutPreferencesStore().FilePath);
        CherryPickPreferences saved = new CherryPickPreferencesStore().Load(); dialog = await f.ShowAsync(pick);
        Pointer(Field<CheckBox>(dialog, "CherryPickAutoCommit")); Pointer(Field<CheckBox>(dialog, "CherryPickAddReference")); Pointer(Field<Button>(dialog, "ExecuteCherryPick"));
        await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any());
        prompt = dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single(); Window? escapeRecipient = null;
        List<string> trace = [];
        prompt.AddHandler(InputElement.KeyDownEvent, (_, args) => { if (args.Key == Key.Escape) { escapeRecipient = prompt; trace.Add("Escape routed to focused submodule prompt"); } }, RoutingStrategies.Tunnel, handledEventsToo: true);
        dialog.AddHandler(InputElement.KeyDownEvent, (_, args) => { if (args.Key == Key.Escape) { escapeRecipient = dialog; trace.Add("Escape routed to parent dialogue"); } }, RoutingStrategies.Tunnel, handledEventsToo: true);
        dialog.Closing += (_, _) => trace.Add("parent Closing accepted=" + dialog.SequenceAccepted);
        prompt.Closed += (_, _) => trace.Add("prompt Closed");
        Press(dialog, PhysicalKey.Escape); await Until(() => !dialog.IsVisible && !dialog.OwnedWindows.Any());
        string proof = Path.Combine(TestContext.CurrentContext.WorkDirectory, "owner-close-trace-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(proof, System.Text.Json.JsonSerializer.Serialize(trace)); TestContext.AddTestAttachment(proof);
        Assert.That(escapeRecipient, Is.SameAs(dialog), "This Escape really requests parent closure, not child cancellation.");
        Assert.That(dialog.SequenceAccepted, Is.False); Assert.That(new CherryPickPreferencesStore().Load(), Is.EqualTo(saved), "Parent Escape with default CT must not persist options after closing.");
        await f.Git("reset", "--hard", f.Initial); saved = new CherryPickPreferencesStore().Load(); dialog = await f.ShowAsync(pick);
        Pointer(Field<CheckBox>(dialog, "CherryPickAddReference")); Pointer(Field<Button>(dialog, "ExecuteCherryPick")); await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any());
        prompt = dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single(); dialog.Closing += (_, _) => trace.Add("explicit owner Closing accepted=" + dialog.SequenceAccepted); prompt.Closed += (_, _) => trace.Add("explicit owner prompt Closed");
        dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.OwnedWindows.Any());
        await File.WriteAllTextAsync(proof, System.Text.Json.JsonSerializer.Serialize(trace));
        Assert.That(dialog.Applied, Is.True, "The already successful Git pick survives closing its owner during the optional prompt.");
        Assert.That(dialog.SequenceAccepted, Is.False); Assert.That(new CherryPickPreferencesStore().Load(), Is.EqualTo(saved), "Owner close with default CT must not accept or persist options after closing.");
    }
    [AvaloniaTest]
    public async Task ContextualFrenchAccessKeysOriginalIconAndOwnerPalettesRenderOnAllChildWindows()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; ThemeVariant? appTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR"); Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            await using Fixture f = await Fixture.CreateAsync(); string merge = await f.MergeAsync();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Window owner = new() { Width = 700, Height = 500, RequestedThemeVariant = theme }; f.Windows.Add(owner); owner.Show(); Task<bool> sequence = CherryPickSequence.ShowAsync(owner, f.Repository, [merge]);
                await Until(() => owner.OwnedWindows.OfType<CherryPickDialog>().Any()); CherryPickDialog dialog = owner.OwnedWindows.OfType<CherryPickDialog>().Single(); Pump(dialog);
                Assert.That(dialog.ActualThemeVariant, Is.EqualTo(theme)); Assert.That(dialog.Title, Is.EqualTo("Cherry picker le commit"));
                Assert.That(((AccessText)Field<CheckBox>(dialog, "CherryPickAutoCommit").Content!).Text, Is.EqualTo("Créer _automatiquement un commit")); Assert.That(((AccessText)Field<CheckBox>(dialog, "CherryPickAddReference").Content!).Text, Is.EqualTo("A_jouter une référence de commit au message de commit")); Assert.That(((AccessText)Field<Button>(dialog, "CancelCherryPick").Content!).Text, Is.EqualTo("A_bandon"));
                Assert.That(Field<Button>(dialog, "ChooseCherryPickRevision").Content, Is.TypeOf<Image>()); Assert.That(((Image)Field<Button>(dialog, "ChooseCherryPickRevision").Content!).Tag, Is.EqualTo("SelectRevision.png")); Capture(dialog, "cherry-pick-fr-" + theme.Key + ".png");
                Pointer(Field<Button>(dialog, "ChooseCherryPickRevision")); await Until(() => dialog.OwnedWindows.OfType<CherryPickRevisionPicker>().Any()); CherryPickRevisionPicker picker = dialog.OwnedWindows.OfType<CherryPickRevisionPicker>().Single(); Assert.That(picker.ActualThemeVariant, Is.EqualTo(theme));
                Pointer(Field<Button>(picker, "CancelCherryPickChoice")); await Until(() => !picker.IsVisible); Pointer(Field<Button>(dialog, "CancelCherryPick")); await Until(() => sequence.IsCompleted); Assert.That(await sequence, Is.False); owner.Close();
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; Application.Current!.RequestedThemeVariant = appTheme; }
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Control control, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; top.UpdateLayout(); Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(center); top.MouseDown(center, button, modifiers); top.MouseUp(center, button, modifiers); Dispatcher.UIThread.RunJobs();
    }
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window is not Window native || native.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static (bool Ready, string Description) PointerTarget(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; top.UpdateLayout();
        Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        Visual? hit = center is Point point ? top.InputHitTest(point) as Visual : null;
        bool contained = center is Point target && new Rect(default, top.ClientSize).Contains(target);
        bool ready = contained && control.IsEffectivelyVisible && control.IsEnabled && hit?.GetSelfAndVisualAncestors().Contains(control) == true;
        return (ready, $"name={control.Name}, bounds={control.Bounds}, point={center}, client={top.ClientSize}, contained={contained}, hit={hit?.GetType().Name}, ready={ready}");
    }
    private static async Task Until(Func<bool> condition, Func<string>? diagnostic = null)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } bool reached = condition(); Assert.That(reached, Is.True, reached ? null : "Timed out waiting for real Cherry-pick UI/Git. " + diagnostic?.Invoke()); }
    private static void Capture(TopLevel window, string name)
    { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Path.GetFullPath("artifacts/linux/screenshots"); Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"); private readonly GitRunner _runner = new(); public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-cherry-ui-" + Guid.NewGuid().ToString("N")); public GitRepository Repository => new(Path.Combine(Root, "repo")); public string Initial { get; set; } = ""; public List<Window> Windows { get; } = [];
        public string File(string path) => Path.Combine(Repository.Path, path); public string Metadata(string path) => Path.Combine(Repository.Path, ".git", path);
        public void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(File(path))!); System.IO.File.WriteAllText(File(path), text); }
        public static async Task<Fixture> CreateAsync()
        { Fixture f = new(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(f.Root, "config")); Directory.CreateDirectory(f.Repository.Path); await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Cherry UI"); await f.Git("config", "user.email", "cherry-ui@example.invalid"); await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null"); await f.Commit("base.txt", "base\n", "Initial"); f.Initial = await f.Head(); return f; }
        public async Task<GitResult> Git(params string[] args)
        { GitResult result = await _runner.RunAsync(Repository.Path, args, environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> Text(params string[] args) => (await Git(args)).StandardOutput.TrimEnd('\r', '\n'); public Task<string> Head() => Text("rev-parse", "HEAD");
        public async Task Commit(string path, string text, string message) { Write(path, text); await Git("add", "--", path); await Git("commit", "-m", message); }
        public async Task<string> TopicAsync(string path, string text)
        { await Git("checkout", "-b", "topic"); await Commit(path, text, "Topic"); string hash = await Head(); await Git("checkout", "main"); return hash; }
        public async Task<string> ConflictAsync() { string pick = await TopicAsync("base.txt", "theirs\n"); await Commit("base.txt", "ours\n", "Checkout change"); return pick; }
        public async Task<string> MergeAsync()
        { await Git("checkout", "-b", "left"); await Commit("left.txt", "left\n", "Left parent"); await Git("checkout", "-b", "right", Initial); await Commit("right.txt", "right\n", "Right parent"); await Git("checkout", "left"); await Git("merge", "--no-ff", "right", "-m", "Merge"); string merge = await Head(); await Git("checkout", "main"); return merge; }
        public async Task<CherryPickDialog> ShowAsync(string? revision)
        { CherryPickDialog dialog = new(Repository, revision); Windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Pump(dialog); return dialog; }
        public async ValueTask DisposeAsync()
        { foreach (Window window in Windows.AsEnumerable().Reverse()) { foreach (Window child in window.OwnedWindows.ToArray()) { foreach (Window nested in child.OwnedWindows.ToArray()) { nested.Close(); } child.Close(); } window.Close(); } Stopwatch wait = Stopwatch.StartNew(); while (Windows.OfType<RepositoryDialog>().Any(window => window.IsBusy) && wait.Elapsed < TimeSpan.FromSeconds(8)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } foreach (Window window in Windows) { window.Close(); } Dispatcher.UIThread.RunJobs(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
