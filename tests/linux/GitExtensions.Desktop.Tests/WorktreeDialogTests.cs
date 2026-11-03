using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class WorktreeDialogTests
{
    [AvaloniaTest]
    public async Task ManagerKeepsTheFourOriginalColumnsCompactRowsAndProtectedActionsInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync();
        string normal = await AddLinkedAsync(f, "feature/λ", "linked with spaces λ");
        string locked = await AddLinkedAsync(f, "locked", "locked-tree");
        string missing = await AddLinkedAsync(f, "missing", "missing-tree");
        await f.GitAsync("worktree", "lock", "--reason", "Keep this worktree", "--", locked); Directory.Delete(missing, true);
        WorktreeDialog dialog = await ManagerAsync(f.Repository);
        try
        {
            Assert.That(dialog.Title, Is.EqualTo("Existing worktrees"));
            Assert.That(Items(dialog).Select(tree => tree.Path), Is.EquivalentTo(new[] { f.Repository.Path, normal, locked, missing }));
            Assert.That(Items(dialog).First().IsMain, Is.True);
            Assert.That(Field<Button>(dialog, "OpenSelectedWorktree").IsDefault, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name is "CloseWorktrees" or "CancelWorktrees"), Is.False);
            Assert.That(new[] { "WorktreePathHeader", "WorktreeTypeHeader", "WorktreeBranchHeader", "WorktreeShaHeader" }
                .Select(name => Field<TextBlock>(dialog, name).Text), Is.EqualTo(new[] { "Path", "Type", "Branch", "SHA-1" }));
            Select(dialog, f.Repository.Path); AssertActions(dialog, open: false, delete: false);
            Select(dialog, normal); AssertActions(dialog, open: true, delete: true);
            Select(dialog, missing); AssertActions(dialog, open: false, delete: false);
            Assert.That(Field<Button>(dialog, "PruneWorktrees").IsEnabled, Is.True);
            TextBlock missingCell = Field<ListBox>(dialog, "WorktreeList").GetVisualDescendants().OfType<TextBlock>()
                .Single(cell => cell.Name == "WorktreePath" && cell.Text == missing);
            Assert.That(missingCell.TextDecorations, Is.Not.Null.And.Not.Empty, "The original manager strikes through deleted paths.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                foreach (Size size in new[] { new Size(710, 361), new Size(dialog.MinWidth, dialog.MinHeight), new Size(1000, 480) })
                {
                    dialog.Width = size.Width; dialog.Height = size.Height; Pump(dialog);
                    AssertInside(dialog, Field<ScrollViewer>(dialog, "WorktreeTableScroll"),
                        Field<Button>(dialog, "DeleteSelectedWorktree"), Field<Button>(dialog, "OpenSelectedWorktree"),
                        Field<Button>(dialog, "PruneWorktrees"), Field<Button>(dialog, "CreateWorktree"));
                    AssertWorktreeViewport(dialog);
                    Assert.That(Field<ListBox>(dialog, "WorktreeList").GetVisualDescendants().OfType<ListBoxItem>()
                        .Where(row => row.IsEffectivelyVisible).Select(row => row.Bounds.Height), Is.All.EqualTo(22), "A wrapped SHA-1 must not silently expand Windows' compact rows.");
                    Capture(dialog, "worktrees-manager-" + theme + "-" + size.Width + "x" + size.Height + ".png");
                }
            }
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ALinkedCurrentWorktreeProtectsItselfAndAllowsOpeningButNeverDeletingTheMainWorktree()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "linked", "linked-current");
        WorktreeDialog dialog = await ManagerAsync(new GitRepository(Path.Combine(linked, ".")), linked);
        try
        {
            WorktreeSnapshot current = Items(dialog).Single(tree => tree.IsCurrent);
            Assert.That(current.Path, Is.EqualTo(linked)); Assert.That(current.IsMain, Is.False);
            Assert.That(((WorktreeSnapshot)Field<ListBox>(dialog, "WorktreeList").SelectedItem!).Path, Is.EqualTo(linked));
            AssertActions(dialog, open: false, delete: false);
            Select(dialog, f.Repository.Path); AssertActions(dialog, open: true, delete: false);
            Assert.That(Field<Button>(dialog, "PruneWorktrees").IsEnabled, Is.False);
            Assert.That(Field<ListBox>(dialog, "WorktreeList").IsKeyboardFocusWithin, Is.True);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ManagerColumnsResizeAndNativeCtrlCCopiesTheWholeSelectedPathBranchAndHash()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "topic/λ;$()'literal", "copy ' λ worktree");
        WorktreeDialog dialog = await ManagerAsync(f.Repository, linked);
        var clipboard = dialog.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            Grid headers = Field<Grid>(dialog, "WorktreeHeaders"); Thumb grip = Field<Thumb>(dialog, "ResizeWorktreePath");
            double width = headers.ColumnDefinitions[0].Width.Value;
            Point start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(start); dialog.MouseDown(start, MouseButton.Left); dialog.MouseMove(start + new Vector(35, 0)); dialog.MouseUp(start + new Vector(35, 0), MouseButton.Left); Pump(dialog);
            Assert.That(headers.ColumnDefinitions[0].Width.Value, Is.GreaterThan(width));
            ListBox rows = Field<ListBox>(dialog, "WorktreeList"); WorktreeSnapshot selected = (WorktreeSnapshot)rows.SelectedItem!;
            rows.Focus(); Press(dialog, PhysicalKey.C, RawInputModifiers.Control);
            string expected = string.Join('\t', selected.Path, "Branch", "topic/λ;$()'literal", selected.Head);
            Stopwatch watch = Stopwatch.StartNew(); string? copied = null;
            while (copied != expected && watch.Elapsed < TimeSpan.FromSeconds(5))
            { Dispatcher.UIThread.RunJobs(); copied = await ClipboardExtensions.TryGetTextAsync(clipboard); if (copied != expected) { await Task.Delay(10); } }
            Assert.That(copied, Is.EqualTo(expected));
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task CreateUsesANonEditableLocalBranchChoiceAndTheMainWorktreeForPortableDirectorySuggestions()
    {
        using Fixture f = await Fixture.CreateAsync();
        string linked = await AddLinkedAsync(f, "linked-current", "linked-current"); await f.GitAsync("branch", "topic/λ|literal");
        CreateWorktreeDialog dialog = await CreatorAsync(new GitRepository(linked), mainPath: f.Output("not-the-main-worktree"));
        try
        {
            ComboBox branches = Field<ComboBox>(dialog, "WorktreeExistingBranches");
            Assert.That(branches.IsEditable, Is.False);
            Assert.That(branches.Items.OfType<GitReference>().All(branch => branch.IsBranch && !branch.IsCurrent), Is.True);
            Assert.That(branches.Items.OfType<GitReference>().Select(branch => branch.ShortName), Is.EquivalentTo(new[] { "main", "topic/λ|literal" }));
            branches.SelectedItem = branches.Items.OfType<GitReference>().Single(branch => branch.ShortName == "topic/λ|literal"); Pump(dialog);
            Assert.That(Field<TextBox>(dialog, "WorktreeDirectory").Text, Is.EqualTo(f.Repository.Path + "_topic_λ_literal"));
            Assert.That(Field<RadioButton>(dialog, "CheckoutExistingWorktreeBranch").IsChecked, Is.True);
            Assert.That(Field<TextBox>(dialog, "WorktreeNewBranchName").IsEnabled, Is.False);
            Assert.That(branches.IsKeyboardFocusWithin, Is.True);
            Assert.That(Field<Button>(dialog, "CreateTheNewWorktree").IsDefault, Is.True);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                foreach (double width in new[] { 608d, 900d })
                {
                    dialog.Width = width; Pump(dialog);
                    AssertInside(dialog, branches, Field<TextBox>(dialog, "WorktreeNewBranchName"), Field<TextBox>(dialog, "WorktreeDirectory"),
                        Field<Button>(dialog, "BrowseWorktreeDirectory"), Field<Button>(dialog, "CreateTheNewWorktree"));
                    Assert.That(dialog.ClientSize.Height, Is.LessThanOrEqualTo(260), "The original creation dialog is compact, with one checkout group and one directory row.");
                    Capture(dialog, "worktree-create-existing-" + theme + "-" + width + ".png");
                }
            }
            Assert.That(dialog.CreatedWorktreePath, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task NoOtherLocalBranchSelectsNewBranchAndNormalizesOnBlurWhileRejectingDuplicateAndNonemptyDestinations()
    {
        using Fixture f = await Fixture.CreateAsync(); CreateWorktreeDialog dialog = await CreatorAsync(f.Repository);
        try
        {
            RadioButton existing = Field<RadioButton>(dialog, "CheckoutExistingWorktreeBranch"), fresh = Field<RadioButton>(dialog, "CreateNewWorktreeBranch");
            TextBox name = Field<TextBox>(dialog, "WorktreeNewBranchName"), directory = Field<TextBox>(dialog, "WorktreeDirectory");
            Button create = Field<Button>(dialog, "CreateTheNewWorktree");
            Assert.That(Field<ComboBox>(dialog, "WorktreeExistingBranches").ItemCount, Is.Zero);
            Assert.That(existing.IsEnabled, Is.False); Assert.That(fresh.IsChecked, Is.True);
            Assert.That(name.IsEnabled && name.IsKeyboardFocusWithin, Is.True); Assert.That(create.IsEnabled, Is.False);
            name.Text = "feature name"; directory.Focus(); Pump(dialog);
            Assert.That(name.Text, Is.EqualTo("feature_name")); Assert.That(directory.Text, Is.EqualTo(f.Repository.Path + "_feature_name")); Assert.That(create.IsEnabled, Is.True);
            name.Text = "main"; Pump(dialog); Assert.That(create.IsEnabled, Is.False);
            name.Text = "unique"; directory.Text = f.Repository.Path; Pump(dialog); Assert.That(create.IsEnabled, Is.False);
            directory.Text = " "; Pump(dialog); Assert.That(create.IsEnabled, Is.False);
            directory.Text = f.Output("empty-directory"); Directory.CreateDirectory(directory.Text); Pump(dialog); Assert.That(create.IsEnabled, Is.True);
            Assert.That((await f.Repository.GetReferencesAsync()).Select(branch => branch.ShortName), Is.EqualTo(new[] { "main" }));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task CreatingAnExistingBranchUsesTheOwnedProcessAndClosesOnlyAfterAcknowledgement()
    {
        using Fixture f = await Fixture.CreateAsync(); const string branch = "topic/λ;$()'literal"; await f.GitAsync("branch", branch);
        string destination = f.Output("created ' λ worktree"); new ProcessDialogPreferencesStore().Save(new(false));
        CreateWorktreeDialog dialog = await CreatorAsync(f.Repository);
        try
        {
            Field<ComboBox>(dialog, "WorktreeExistingBranches").SelectedItem = Field<ComboBox>(dialog, "WorktreeExistingBranches").Items.OfType<GitReference>().Single(item => item.ShortName == branch);
            Field<TextBox>(dialog, "WorktreeDirectory").Text = destination; Pointer(dialog, "CreateTheNewWorktree");
            GitProcessDialog process = await FinishedProcessAsync(dialog);
            Assert.That(process.Owner, Is.SameAs(dialog)); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString());
            Assert.That(dialog.IsVisible && dialog.IsBusy, Is.True); Assert.That(Directory.Exists(destination), Is.True);
            Assert.That(await GitTextAtAsync(destination, "symbolic-ref", "--short", "HEAD"), Is.EqualTo(branch));
            Pointer(process, "Ok"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.CreatedWorktreePath, Is.EqualTo(destination)); Assert.That(dialog.LastProcess?.Success, Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ManagerCreatesANewBranchThenSwitchNoKeepsTheCreationAndRestoresItsListFocus()
    {
        using Fixture f = await Fixture.CreateAsync(); string destination = f.Output("new feature tree"); new ProcessDialogPreferencesStore().Save(new(false));
        WorktreeDialog manager = await ManagerAsync(f.Repository);
        try
        {
            Pointer(manager, "CreateWorktree");
            await Until(() => manager.OwnedWindows.OfType<CreateWorktreeDialog>().Any(child => !child.IsBusy));
            CreateWorktreeDialog child = manager.OwnedWindows.OfType<CreateWorktreeDialog>().Single();
            TextBox name = Field<TextBox>(child, "WorktreeNewBranchName"); name.Text = "new feature"; name.Focus(); Press(child, PhysicalKey.Tab);
            Assert.That(name.Text, Is.EqualTo("new_feature"));
            Field<TextBox>(child, "WorktreeDirectory").Text = destination; Pointer(child, "CreateTheNewWorktree");
            GitProcessDialog process = await FinishedProcessAsync(child); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString()); Pointer(process, "Ok");
            Window question = await QuestionAsync(manager, "SwitchWorktreeConfirmation");
            Assert.That(child.IsVisible, Is.False, "The original create modal must close before offering the switch."); Assert.That(question.Owner, Is.SameAs(manager));
            Assert.That(Field<Button>(question, "ConfirmSwitchWorktree").IsDefault, Is.True); Pointer(question, "CancelSwitchWorktree"); await IdleAsync(manager);
            Assert.That(manager.IsVisible, Is.True); Assert.That(manager.RequestedWorktreePath, Is.Null); Assert.That(manager.HasRepositoryChanges, Is.True);
            Assert.That(Items(manager).Any(item => item.Path == destination && item.Exists), Is.True);
            Assert.That(await GitTextAtAsync(destination, "symbolic-ref", "--short", "HEAD"), Is.EqualTo("new_feature"));
            Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That(Field<ListBox>(manager, "WorktreeList").IsKeyboardFocusWithin, Is.True);
        }
        finally { await CloseAsync(manager); }
    }

    [AvaloniaTest]
    public async Task PruneRemovesOnlyMissingRegistrationsThroughTheProcessAndKeepsTheManagerOpen()
    {
        using Fixture f = await Fixture.CreateAsync(); string live = await AddLinkedAsync(f, "live", "live-tree"), missing = await AddLinkedAsync(f, "missing", "missing-tree");
        Directory.Delete(missing, true); new ProcessDialogPreferencesStore().Save(new(false)); WorktreeDialog dialog = await ManagerAsync(f.Repository, missing);
        try
        {
            Pointer(dialog, "PruneWorktrees"); GitProcessDialog process = await FinishedProcessAsync(dialog);
            Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString()); Pointer(process, "Ok"); await IdleAsync(dialog);
            Assert.That(Items(dialog).Select(tree => tree.Path), Is.EquivalentTo(new[] { f.Repository.Path, live }));
            Assert.That(Field<Button>(dialog, "PruneWorktrees").IsEnabled, Is.False); Assert.That(dialog.IsVisible && dialog.HasRepositoryChanges, Is.True);
            Assert.That(Directory.Exists(live), Is.True); Assert.That((await f.Repository.GetReferencesAsync()).Any(branch => branch.ShortName == "missing"), Is.True,
                "Pruning a missing worktree must preserve its branch.");
            Assert.That(Field<ListBox>(dialog, "WorktreeList").IsKeyboardFocusWithin, Is.True);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task DeleteDefaultsToNoAndRequiresExplicitDiscardAndLockOverrideBeforeRemovingTheRealWorktree()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "keep-branch", "locked-dirty-tree");
        await File.WriteAllTextAsync(Path.Combine(linked, "file.txt"), "modified content\n"); await File.WriteAllTextAsync(Path.Combine(linked, "untracked.txt"), "keep unless confirmed\n");
        await f.GitAsync("worktree", "lock", "--", linked); new ProcessDialogPreferencesStore().Save(new(false));
        WorktreeDialog dialog = await ManagerAsync(f.Repository, linked);
        try
        {
            Assert.That(Items(dialog).Single(tree => tree.Path == linked).IsLocked, Is.True, "An empty lock reason still means locked.");
            Pointer(dialog, "DeleteSelectedWorktree"); Window question = await QuestionAsync(dialog, "DeleteWorktreeConfirmation");
            Assert.That(Field<Button>(question, "CancelDeleteWorktree").IsDefault, Is.True);
            Assert.That(Field<CheckBox>(question, "DiscardWorktreeChanges").IsChecked, Is.Not.True);
            Assert.That(Field<CheckBox>(question, "OverrideWorktreeLock").IsVisible, Is.True);
            Assert.That(Field<CheckBox>(question, "OverrideWorktreeLock").IsChecked, Is.Not.True);
            Pointer(question, "CancelDeleteWorktree"); await IdleAsync(dialog);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(linked, "file.txt")), Is.EqualTo("modified content\n"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(linked, "untracked.txt")), Is.EqualTo("keep unless confirmed\n"));
            Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(Field<ListBox>(dialog, "WorktreeList").IsKeyboardFocusWithin, Is.True);
            Pointer(dialog, "DeleteSelectedWorktree"); question = await QuestionAsync(dialog, "DeleteWorktreeConfirmation");
            Assert.That(Field<TextBlock>(question, "WorktreeLockRemovalNotice").IsEffectivelyVisible, Is.True);
            PushProcessTestDriver.Pointer(Field<CheckBox>(question, "OverrideWorktreeLock"));
            Assert.That(Field<Button>(question, "ConfirmDeleteWorktree").IsEnabled, Is.False, "Overriding a lock alone must never authorize native double-force, which also discards changes.");
            Assert.That(Field<CheckBox>(question, "DiscardWorktreeChanges").IsChecked, Is.Not.True); Assert.That(Directory.Exists(linked), Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(linked, "file.txt")), Is.EqualTo("modified content\n"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(linked, "untracked.txt")), Is.EqualTo("keep unless confirmed\n"));
            PushProcessTestDriver.Pointer(Field<CheckBox>(question, "DiscardWorktreeChanges"));
            Assert.That(Field<Button>(question, "ConfirmDeleteWorktree").IsEnabled, Is.True);
            Capture(question, "worktree-delete-explicit-force-and-lock.png"); Pointer(question, "ConfirmDeleteWorktree");
            GitProcessDialog process = await FinishedProcessAsync(dialog); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString()); Pointer(process, "Ok"); await IdleAsync(dialog);
            Assert.That(Directory.Exists(linked), Is.False); Assert.That(Items(dialog).Select(tree => tree.Path), Is.EqualTo(new[] { f.Repository.Path }));
            Assert.That((await f.Repository.GetReferencesAsync()).Any(branch => branch.ShortName == "keep-branch"), Is.True);
            Assert.That(dialog.IsVisible && dialog.HasRepositoryChanges, Is.True);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ClosingTheManagerWhileDeleteConfirmationIsOpenDoesNotDeleteAnything()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "preserved", "preserved-tree");
        WorktreeDialog dialog = await ManagerAsync(f.Repository, linked);
        try
        {
            Pointer(dialog, "DeleteSelectedWorktree"); Window question = await QuestionAsync(dialog, "DeleteWorktreeConfirmation"); dialog.Close();
            await Until(() => !dialog.IsVisible && !dialog.IsBusy && !question.IsVisible);
            Assert.That(Directory.Exists(linked), Is.True); Assert.That((await f.Repository.GetWorktreesAsync()).Any(tree => tree.Path == linked), Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task MainWindowOpensTheChosenWorktreeOnlyAfterItsManagerModalHasClosed()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "opened", "opened-tree");
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
            MenuItem repository = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => Header(item.Header) == "Repository");
            MenuItem manage = repository.Items.OfType<MenuItem>().Single(item => item.Name == "manageWorktreeToolStripMenuItem");
            manage.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => main.OwnedWindows.OfType<WorktreeDialog>().Any(dialog => !dialog.IsBusy)); WorktreeDialog manager = main.OwnedWindows.OfType<WorktreeDialog>().Single();
            Select(manager, linked); Pointer(manager, "OpenSelectedWorktree"); Window question = await QuestionAsync(manager, "SwitchWorktreeConfirmation");
            Assert.That(Field<TextBox>(main, "RepositoryPath").Text, Is.EqualTo(f.Repository.Path));
            Pointer(question, "CancelSwitchWorktree"); await IdleAsync(manager);
            Assert.That(manager.IsVisible, Is.True); Assert.That(Field<ListBox>(manager, "WorktreeList").IsKeyboardFocusWithin, Is.True);
            bool closingBeforeSwitch = false; manager.Closing += (_, _) => closingBeforeSwitch = Field<TextBox>(main, "RepositoryPath").Text == f.Repository.Path;
            Field<ListBox>(manager, "WorktreeList").Focus(); Press(manager, PhysicalKey.Enter); question = await QuestionAsync(manager, "SwitchWorktreeConfirmation");
            Pointer(question, "ConfirmSwitchWorktree"); await Until(() => !manager.IsVisible && !main.IsBusy && Field<TextBox>(main, "RepositoryPath").Text == linked);
            Assert.That(closingBeforeSwitch, Is.True); Assert.That(manager.RequestedWorktreePath, Is.EqualTo(linked)); Assert.That(main.LastException, Is.Null);
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task SwitchNoCanPersistTheOriginalDoNotAskAgainChoiceWithoutOpeningThatTime()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "switchable", "switchable-tree");
        WorktreeDialog dialog = await ManagerAsync(f.Repository, linked);
        try
        {
            Pointer(dialog, "OpenSelectedWorktree"); Window question = await QuestionAsync(dialog, "SwitchWorktreeConfirmation");
            PushProcessTestDriver.Pointer(Field<CheckBox>(question, "WorktreeDontShowAgain")); Pointer(question, "CancelSwitchWorktree"); await IdleAsync(dialog);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.RequestedWorktreePath, Is.Null);
            Assert.That(new BrowseCommandPreferencesStore().Load().DontConfirmSwitchWorktree, Is.True);
            Pointer(dialog, "OpenSelectedWorktree"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.RequestedWorktreePath, Is.EqualTo(linked)); Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task NativeCreationFailureKeepsTheEditableDialogAndReturnsFocusToItsDirectory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A private native Git wrapper requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync(); await f.GitAsync("branch", "available");
        string wrapper = await WorktreeWrapperAsync(f, slow: false, f.Output("unused-actors"));
        string destination = f.Output("failed-destination"); new ProcessDialogPreferencesStore().Save(new(false));
        CreateWorktreeDialog dialog = await CreatorAsync(new GitRepository(f.Repository.Path, new GitRunner(wrapper)));
        try
        {
            Field<TextBox>(dialog, "WorktreeDirectory").Text = destination; Pointer(dialog, "CreateTheNewWorktree");
            GitProcessDialog process = await FinishedProcessAsync(dialog);
            Assert.That(process.Outcome?.Success, Is.False); Assert.That(process.Result?.ExitCode, Is.EqualTo(53));
            Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("fixture worktree creation failure"));
            Pointer(process, "Ok"); await IdleAsync(dialog);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.CreatedWorktreePath, Is.Null); Assert.That(Directory.Exists(destination), Is.False);
            Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Does.Contain("fixture worktree creation failure"));
            Assert.That(Field<TextBox>(dialog, "WorktreeDirectory").IsKeyboardFocusWithin, Is.True);
            Assert.That(Field<Button>(dialog, "CreateTheNewWorktree").IsEnabled, Is.True);
            Assert.That((await f.Repository.GetWorktreesAsync()).Count, Is.EqualTo(1));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task AbortAndOwnerCloseDrainTheActualCreationProcessesWithoutPublishingACreatedWorktree()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native Git actors and procfs require Linux."); return; }
        using Fixture f = await Fixture.CreateAsync(); await f.GitAsync("branch", "available"); new ProcessDialogPreferencesStore().Save(new(false));
        foreach (bool closeOwner in new[] { false, true })
        {
            string actors = f.Output("worktree-actors-" + closeOwner), destination = f.Output("cancelled-tree-" + closeOwner);
            string wrapper = await WorktreeWrapperAsync(f, slow: true, actors);
            CreateWorktreeDialog dialog = await CreatorAsync(new GitRepository(f.Repository.Path, new GitRunner(wrapper)));
            int[] pids = [];
            try
            {
                Field<TextBox>(dialog, "WorktreeDirectory").Text = destination; Pointer(dialog, "CreateTheNewWorktree");
                await Until(() => ReadActors(actors).Length == 2); pids = ReadActors(actors);
                GitProcessDialog process = PushProcessTestDriver.Windows(dialog).OfType<GitProcessDialog>().Single(window => window.IsVisible);
                Assert.That(process.IsRunning, Is.True); Assert.That(pids.All(ActorAlive), Is.True);
                if (closeOwner)
                {
                    dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy && !process.IsVisible);
                }
                else
                {
                    Pointer(process, "Abort"); await Until(() => !process.IsRunning);
                    Assert.That(process.Outcome?.WasCancelled, Is.True);
                    if (process.IsVisible) { Pointer(process, "Ok"); }
                    await IdleAsync(dialog);
                    Assert.That(dialog.IsVisible, Is.True); Assert.That(Field<TextBox>(dialog, "WorktreeDirectory").IsKeyboardFocusWithin, Is.True);
                }
                await Until(() => pids.All(pid => !ActorAlive(pid)));
                Assert.That(dialog.CreatedWorktreePath, Is.Null); Assert.That(Directory.Exists(destination), Is.False);
                Assert.That((await f.Repository.GetWorktreesAsync()).Count, Is.EqualTo(1));
                Assert.That((await f.Repository.GetReferencesAsync()).Where(branch => branch.IsBranch).Select(branch => branch.ShortName), Is.EquivalentTo(new[] { "main", "available" }));
            }
            finally
            {
                try { await CloseAsync(dialog); }
                finally
                {
                    // These PIDs come only from this fixture's private wrapper;
                    // cleanup is separate from the preceding cancellation proof.
                    foreach (int pid in pids.Where(ActorAlive))
                    {
                        try { using Process child = Process.GetProcessById(pid); child.Kill(entireProcessTree: true); }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<WorktreeSnapshot> Items(WorktreeDialog dialog) => Field<ListBox>(dialog, "WorktreeList").Items.OfType<WorktreeSnapshot>().ToArray();
    private static void Select(WorktreeDialog dialog, string path)
    { Field<ListBox>(dialog, "WorktreeList").SelectedItem = Items(dialog).Single(tree => tree.Path == path); Pump(dialog); }
    private static void AssertActions(WorktreeDialog dialog, bool open, bool delete)
    { Assert.That(Field<Button>(dialog, "OpenSelectedWorktree").IsEnabled, Is.EqualTo(open)); Assert.That(Field<Button>(dialog, "DeleteSelectedWorktree").IsEnabled, Is.EqualTo(delete)); }
    private static void AssertWorktreeViewport(WorktreeDialog dialog)
    {
        ScrollViewer viewport = Field<ScrollViewer>(dialog, "WorktreeTableScroll");
        ListBox rows = Field<ListBox>(dialog, "WorktreeList"); Grid headers = Field<Grid>(dialog, "WorktreeHeaders");
        ScrollContentPresenter clipper = viewport.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        Assert.That(clipper.ClipToBounds, Is.True, "The wide AllCells document must be clipped by its scrolling viewport.");
        AssertInside(dialog, clipper);
        Rect clip = Bounds(clipper), list = Bounds(rows);
        Assert.That(list.Top, Is.GreaterThanOrEqualTo(clip.Top)); Assert.That(list.Bottom, Is.LessThanOrEqualTo(clip.Bottom + .5));
        string[] columns = ["Path", "Type", "Branch", "Sha"];
        TextBlock[] firstRow = columns.Select(column => rows.GetVisualDescendants().OfType<TextBlock>().First(cell => cell.Name == "Worktree" + column)).ToArray();
        Grid row = firstRow[0].GetVisualAncestors().OfType<Grid>().First();
        TestContext.WriteLine($"Worktree alignment: theme={dialog.ActualThemeVariant}, client={dialog.ClientSize}, scroll={viewport.Offset}, headerBounds={Bounds(headers)}, rowBounds={Bounds(row)}");
        for (int column = 0; column < columns.Length; column++)
        {
            TextBlock header = Field<TextBlock>(dialog, "Worktree" + columns[column] + "Header");
            TestContext.WriteLine($"  {columns[column]}: headerX={Bounds(header).X:F3}, dataX={Bounds(firstRow[column]).X:F3}, delta={Bounds(firstRow[column]).X - Bounds(header).X:F3}, headerColumnActualWidth={headers.ColumnDefinitions[column].ActualWidth:F3}, rowColumnActualWidth={row.ColumnDefinitions[column].ActualWidth:F3}, declaredHeaderWidth={headers.ColumnDefinitions[column].Width}, declaredRowWidth={row.ColumnDefinitions[column].Width}");
        }
        foreach ((string kind, Control cell) in new[] { ("header", (Control)Field<TextBlock>(dialog, "WorktreePathHeader")), ("data", (Control)firstRow[0]) })
        {
            foreach (Control ancestor in cell.GetVisualAncestors().OfType<Control>().TakeWhile(control => control != dialog))
            {
                string insets = ancestor switch
                {
                    Border border => $", padding={border.Padding}, border={border.BorderThickness}",
                    TemplatedControl templated => $", padding={templated.Padding}, border={templated.BorderThickness}",
                    _ => ""
                };
                TestContext.WriteLine($"  {kind} ancestor {ancestor.GetType().Name}#{ancestor.Name}: bounds={Bounds(ancestor)}, margin={ancestor.Margin}{insets}");
            }
        }
        Assert.That(row.ColumnDefinitions.Select(column => column.Width.Value), Is.EqualTo(headers.ColumnDefinitions.Select(column => column.Width.Value)),
            "Header and data cells share the same four AllCells/resized column widths.");
        for (int column = 0; column < columns.Length; column++)
        {
            Assert.That(Bounds(firstRow[column]).X, Is.EqualTo(Bounds(Field<TextBlock>(dialog, "Worktree" + columns[column] + "Header")).X).Within(2),
                "Header/data alignment permits the ListBox's border, never an independently scrolling column.");
        }
        Vector previous = viewport.Offset; double available = viewport.Extent.Width - viewport.Viewport.Width;
        if (available > 1)
        {
            viewport.Offset = default; Pump(dialog);
            double headerX = Bounds(headers).X, rowX = Bounds(firstRow[0]).X;
            viewport.Offset = new Vector(Math.Min(80, available), 0); Pump(dialog);
            double distance = viewport.Offset.X;
            Assert.That(distance, Is.GreaterThan(0));
            Assert.That(Bounds(headers).X, Is.EqualTo(headerX - distance).Within(.5));
            Assert.That(Bounds(firstRow[0]).X, Is.EqualTo(rowX - distance).Within(.5), "The whole header and all rows must move together to reveal path suffixes.");
            viewport.Offset = previous; Pump(dialog);
        }
        Rect Bounds(Control control) => new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
    }
    private static async Task<string> AddLinkedAsync(Fixture f, string branch, string name)
    {
        string path = f.Output(name); await f.GitAsync("branch", branch); await f.GitAsync("worktree", "add", "--", path, branch); return path;
    }
    private static async Task<string> WorktreeWrapperAsync(Fixture f, bool slow, string actors)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        string path = f.Output("worktree-wrapper-" + Guid.NewGuid().ToString("N") + ".py");
        string behavior = slow
            ? "    child = subprocess.Popen(['/bin/sleep', '120'])\n"
                + TestSignalFile.Python(System.Text.Json.JsonSerializer.Serialize(actors), "str(os.getpid()) + ' ' + str(child.pid)", 4)
                + "    while True:\n        time.sleep(1)\n"
            : "    print('fixture worktree creation failure', file=sys.stderr, flush=True)\n    sys.exit(53)\n";
        await File.WriteAllTextAsync(path, "#!/usr/bin/python3\nimport os, subprocess, sys, time\nargs = sys.argv[1:]\n"
            + "if 'worktree' in args and args[args.index('worktree') + 1:args.index('worktree') + 2] == ['add'] and '-h' not in args:\n"
            + behavior + "os.execv('/usr/bin/git', ['/usr/bin/git', *args])\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static int[] ReadActors(string path)
    {
        try { return File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); }
        catch (Exception error) when (error is IOException or FormatException) { return []; }
    }
    private static bool ActorAlive(int pid)
    {
        try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); int end = stat.LastIndexOf(')'); return end < 0 || stat[end + 2] != 'Z'; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private static async Task<WorktreeDialog> ManagerAsync(GitRepository repository, string? selected = null)
    {
        WorktreeDialog dialog = new(repository, selected); dialog.Show(); await dialog.InitializeAsync(); Pump(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static async Task<CreateWorktreeDialog> CreatorAsync(GitRepository repository, string? mainPath = null, bool normalize = true)
    {
        CreateWorktreeDialog dialog = new(repository, mainPath, normalize); dialog.Show(); await dialog.InitializeAsync(); Pump(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static async Task<Window> QuestionAsync(Window owner, string name)
    {
        await Until(() => PushProcessTestDriver.Windows(owner).Any(window => window.IsVisible && window.Name == name));
        Window question = PushProcessTestDriver.Windows(owner).Single(window => window.IsVisible && window.Name == name); Pump(question); return question;
    }
    private static async Task<GitProcessDialog> FinishedProcessAsync(Window owner)
    {
        await Until(() => PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Any(process => process.IsVisible && !process.IsRunning));
        return PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Single(process => process.IsVisible && !process.IsRunning);
    }
    private static async Task IdleAsync(RepositoryDialog dialog) => await Until(() => !dialog.IsBusy);
    private static async Task<string> GitTextAtAsync(string path, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput.TrimEnd('\r', '\n');
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string Header(object? content) => content is AccessText access ? (access.Text ?? "").Replace("_", "", StringComparison.Ordinal) : content?.ToString() ?? "";
    private static void Pointer(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static async Task Until(Func<bool> condition, Func<string>? diagnostic = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The worktree UI did not settle. " + (condition() ? "" : diagnostic?.Invoke()));
    }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task CloseAsync(Window window)
    {
        foreach (Window child in window.OwnedWindows.ToArray()) { await CloseAsync(child); }
        if (window.IsVisible) { window.Close(); }
        await Until(() => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy)); Pump(window);
    }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static void AssertInside(Window window, params Control[] controls)
    {
        Pump(window);
        foreach (Control control in controls)
        {
            Assert.That(control.IsEffectivelyVisible, Is.True, control.Name); Assert.That(control.Bounds.Width, Is.GreaterThan(0), control.Name);
            Assert.That(control.Bounds.Height, Is.GreaterThan(0), control.Name); Point origin = control.TranslatePoint(default, window)!.Value;
            Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), control.Name); Assert.That(origin.Y, Is.GreaterThanOrEqualTo(-.5), control.Name);
            Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), control.Name);
            Assert.That(origin.Y + control.Bounds.Height, Is.LessThanOrEqualTo(window.ClientSize.Height + .5), control.Name);
        }
    }
}
