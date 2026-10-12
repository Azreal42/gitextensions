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
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class StashMenuTests
{
    [AvaloniaTest]
    public async Task OriginalSplitMenuHasShortcutsAlignedArrowAndBothDialogModesWithoutMutatingGit()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("saved\n"); MainWindow main = await f.ShowAsync();
        string before = await f.SnapshotAsync();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; Button dropdown = Find<Button>(main, "BrowseStashDropdown"); Click(dropdown);
            MenuFlyout menu = (MenuFlyout)dropdown.Flyout!;
            Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[] { "StashMenuSave", "StashMenuStaged", "StashMenuPop", "StashMenuManage", "StashMenuCreate" }));
            Assert.That(menu.Items.OfType<Separator>().Count(), Is.EqualTo(1));
            Assert.That(Item(menu, "Save").InputGesture, Is.EqualTo(new KeyGesture(Key.Up, KeyModifiers.Control | KeyModifiers.Alt)));
            Assert.That(Item(menu, "Pop").InputGesture, Is.EqualTo(new KeyGesture(Key.Down, KeyModifiers.Control | KeyModifiers.Alt)));
            MenuFlyoutPresenter popup = Item(menu, "Save").GetVisualAncestors().OfType<MenuFlyoutPresenter>().First();
            Assert.That(popup.PointToScreen(default).X, Is.EqualTo(Find<Button>(main, "BrowseStash").PointToScreen(default).X).Within(2));
            PathIcon arrow = Find<PathIcon>(main, "BrowseStashDropdownArrow"), pull = Find<PathIcon>(main, "BrowsePullDropdownArrow");
            Assert.That(arrow.PointToScreen(default).Y, Is.EqualTo(pull.PointToScreen(default).Y));
            Capture(TopLevel.GetTopLevel(Item(menu, "Save"))!, "stash-menu-" + theme + ".png"); menu.Hide();
            foreach (bool manage in new[] { true, false })
            {
                StashDialog dialog = await Open(f, manage); Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.EqualTo(manage ? 1 : 0));
                Assert.That(Find<Button>(dialog, "ApplyStash").IsEnabled, Is.EqualTo(manage));
                Assert.That(Find<TextBox>(dialog, "StashMessage").IsReadOnly, Is.EqualTo(manage));
                Capture(dialog, "stash-dialog-" + (manage ? "manage-" : "create-") + theme + ".png"); await Close(f, dialog);
            }
        }
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task QuickStashAndPopUseActualShortcutsAndLatestStashIgnoringSelectedGraphAndSavedKeepIndex()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("older\n"); MainWindow main = await f.ShowAsync();
        new StashPreferencesStore().Save(new(KeepIndex: true, IncludeUntracked: false));
        await f.WriteAsync("seed.txt", "latest staged\n"); await f.Repository.StageAsync(["seed.txt"]); await f.WriteAsync("untracked.txt", "leave\n");
        Press(main, PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Alt); await Idle(main);
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(2));
        Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("seed\n")); Assert.That(await f.ReadAsync("untracked.txt"), Is.EqualTo("leave\n"));
        Assert.That(main.OwnedWindows, Is.Empty, "Quick stash does not open a message prompt.");
        // The menu is a separate popup input root; its shortcuts must work too.
        Button dropdown = Find<Button>(main, "BrowseStashDropdown"); Click(dropdown); MenuFlyout menu = (MenuFlyout)dropdown.Flyout!;
        Press(TopLevel.GetTopLevel(Item(menu, "Pop"))!, PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Alt); await Idle(main);
        Assert.That(menu.IsOpen, Is.False); Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("latest staged\n"));
        Assert.That((await f.Repository.GetStatusAsync()).Changes.Single(change => change.Path == "seed.txt").IndexStatus, Is.EqualTo('.'));
        Assert.That((await f.Repository.GetStashesAsync()).Single().Subject, Does.Contain("older"));
    }

    [AvaloniaTest]
    public async Task CreateDialogSavesTypedMessageSelectedLiteralPathsAndUntrackedFilesWithRealButtons()
    {
        await using Fixture f = await Fixture.CreateAsync(); const string path = "file[1]* λ\n雪.txt", adjacent = "file1 λ\n雪.txt";
        await f.WriteAsync(path, "base\n"); await f.WriteAsync(adjacent, "adjacent base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Paths");
        await f.WriteAsync(path, "selected\n"); await f.WriteAsync(adjacent, "leave\n"); await f.WriteAsync("new.txt", "new\n"); await f.ShowAsync();
        StashDialog dialog = await Open(f, false); FileStatusListView files = Find<FileStatusListView>(dialog, "StashFilesView");
        files.SelectTags(files.Entries.Where(entry => entry.Path == path || entry.Path == "new.txt").Select(entry => entry.Tag)); await Idle(dialog);
        TextBox message = Find<TextBox>(dialog, "StashMessage"); message.Focus(); dialog.KeyTextInput("Stash message λ\nsecond line"); Pump(dialog);
        Click(Find<CheckBox>(dialog, "StashIncludeUntracked")); Click(Find<Button>(dialog, "StashSelected")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(await f.ReadAsync(path), Is.EqualTo("base\n")); Assert.That(await f.ReadAsync(adjacent), Is.EqualTo("leave\n"));
        Assert.That(File.Exists(f.File("new.txt")), Is.False);
        GitStash stash = (await f.Repository.GetStashesAsync()).Single(); GitStashChanges changes = await f.Repository.GetStashChangesAsync(stash);
        Assert.That(changes.Message, Does.Contain("Stash message λ\nsecond line")); Assert.That(changes.Tracked.Files.Single().Path, Is.EqualTo(path));
        await Close(f, dialog); Assert.That(new StashPreferencesStore().Load().IncludeUntracked, Is.True);
        StashDialog reopened = await Open(f, false); Assert.That(Find<CheckBox>(reopened, "StashIncludeUntracked").IsChecked, Is.True);
    }

    [AvaloniaTest]
    public async Task KeepIndexChoicePersistsAndAllButtonPreservesTheActualIndex()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("other.txt", "base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Other base");
        await f.WriteAsync("seed.txt", "staged\n"); await f.Repository.StageAsync(["seed.txt"]); await f.WriteAsync("other.txt", "unstaged\n"); await f.ShowAsync();
        StashDialog dialog = await Open(f, false); Click(Find<CheckBox>(dialog, "StashKeepIndex")); Click(Find<Button>(dialog, "StashAll")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("staged\n")); Assert.That(await f.ReadAsync("other.txt"), Is.EqualTo("base\n"));
        Assert.That((await f.Repository.GetStatusAsync()).Changes.Single().IsStaged, Is.True);
        await Close(f, dialog); Assert.That(new StashPreferencesStore().Load().KeepIndex, Is.True);
        StashDialog reopened = await Open(f, false); Assert.That(Find<CheckBox>(reopened, "StashKeepIndex").IsChecked, Is.True);
    }

    [AvaloniaTest]
    public async Task ManagePreviewIncludesUntrackedAndApplyRetainsStashWithoutRestoringIndex()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("seed.txt", "preview\n"); await f.Repository.StageAsync(["seed.txt"]);
        await f.WriteAsync("untracked λ.txt", "untracked preview\n"); await f.Repository.StashAsync(new GitStashSaveOptions("preview message", true)); await f.ShowAsync();
        string before = await f.SnapshotAsync(); StashDialog dialog = await Open(f, true);
        FileStatusListView files = Find<FileStatusListView>(dialog, "StashFilesView"); Assert.That(files.Entries.Select(entry => entry.Path), Is.EquivalentTo(new[] { "seed.txt", "untracked λ.txt" }));
        Assert.That(Find<FileContentView>(dialog, "StashDiff").TextView.Text, Does.Contain("+preview"));
        files.SelectTags(files.Entries.Where(entry => entry.Path == "untracked λ.txt").Select(entry => entry.Tag)); await Idle(dialog);
        Assert.That(Find<FileContentView>(dialog, "StashDiff").TextView.Text, Does.Contain("+untracked preview"));
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Click(Find<Button>(dialog, "ApplyStash")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
        Assert.That(await f.ReadAsync("untracked λ.txt"), Is.EqualTo("untracked preview\n")); Assert.That((await f.Repository.GetStatusAsync()).Changes.Single(change => change.Path == "seed.txt").IsStaged, Is.False);
        Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.Zero);
    }

    [AvaloniaTest]
    public async Task DropDefaultNoCancelsAndConfirmedDeletionRefreshesToTheAdjacentStash()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("old\n"); await f.SaveAsync("new\n"); await f.ShowAsync();
        StashDialog dialog = await Open(f, true); string before = await f.SnapshotAsync(); Click(Find<Button>(dialog, "DropStash"));
        await Until(() => dialog.OwnedWindows.Any()); Window confirmation = dialog.OwnedWindows.Single();
        Assert.That(Find<Button>(confirmation, "CancelStashDrop").IsDefault, Is.True); Click(Find<Button>(confirmation, "CancelStashDrop")); await Idle(dialog);
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Click(Find<Button>(dialog, "DropStash")); await Until(() => dialog.OwnedWindows.Any());
        confirmation = dialog.OwnedWindows.Single(); Click(Find<CheckBox>(confirmation, "StashDropDontShowAgain")); Click(Find<Button>(confirmation, "ConfirmStashDrop")); await Idle(dialog);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Subject, Does.Contain("old")); Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.EqualTo(1));
        Assert.That(new StashPreferencesStore().Load().DontConfirmDrop, Is.True); Click(Find<Button>(dialog, "DropStash")); await Idle(dialog);
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.Zero); Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(Find<Button>(dialog, "DropStash").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task StagedMenuOnlySavesStagedFilesAndBareRepositoryDisablesTheWholeSplitControl()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("other.txt", "base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Base");
        await f.WriteAsync("seed.txt", "staged\n"); await f.Repository.StageAsync(["seed.txt"]); await f.WriteAsync("other.txt", "unstaged\n"); MainWindow main = await f.ShowAsync();
        Click(Find<Button>(main, "BrowseStashDropdown")); Click(Item((MenuFlyout)Find<Button>(main, "BrowseStashDropdown").Flyout!, "Staged")); await Idle(main);
        Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("seed\n")); Assert.That(await f.ReadAsync("other.txt"), Is.EqualTo("unstaged\n"));
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true); await main.OpenRepositoryAsync(bare.Path); await Idle(main);
        Assert.That(Find<Button>(main, "BrowseStash").IsEnabled, Is.False); Assert.That(Find<Button>(main, "BrowseStashDropdown").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task ConflictingQuickPopKeepsStashAndItsResolutionWindowStagesTheRealResolution()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("stash side\n"); await f.WriteAsync("seed.txt", "checkout side\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Conflict");
        MainWindow main = await f.ShowAsync(); Press(main, PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Alt);
        await Until(() => main.OwnedWindows.OfType<StashDialog>().Any(dialog => !dialog.IsBusy)); StashDialog dialog = main.OwnedWindows.OfType<StashDialog>().Single(); await Idle(dialog);
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1)); Assert.That((await f.Repository.GetStatusAsync()).Changes.Single().IsConflict, Is.True);
        Click(Find<Button>(dialog, "ResolveStashConflict")); await Until(() => dialog.OwnedWindows.OfType<PatchConflictDialog>().Any(child => !child.IsBusy));
        PatchConflictDialog resolution = dialog.OwnedWindows.OfType<PatchConflictDialog>().Single(); TextBox text = Find<TextBox>(resolution, "PatchConflictText");
        text.Focus(); text.SelectAll(); resolution.KeyTextInput("resolved λ\n"); Pump(resolution); Click(Find<Button>(resolution, "SavePatchResolution")); await Idle(resolution);
        Assert.That(resolution.LastError, Is.Null); Assert.That((await f.Repository.GetStatusAsync()).Changes.Single().IsConflict, Is.False);
        Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("resolved λ\n")); Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
        resolution.Close(); await Idle(dialog);
    }

    [AvaloniaTest]
    public async Task EmptyStashListHasDisabledManageActionsAndKeyboardRefreshAndNavigationMatchOriginal()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.ShowAsync(); StashDialog dialog = await Open(f, true);
        Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.Zero); Assert.That(Find<Button>(dialog, "ApplyStash").IsEnabled, Is.False);
        Assert.That(Find<TextBlock>(dialog, "OperationStatus").Text, Is.EqualTo("There are no stashes."));
        await f.SaveAsync("external first\n"); await f.SaveAsync("external second\n"); Press(dialog, PhysicalKey.F5, RawInputModifiers.None); await Idle(dialog);
        Assert.That(Find<ComboBox>(dialog, "StashSelection").ItemCount, Is.EqualTo(3));
        Press(dialog, PhysicalKey.P, RawInputModifiers.Control); await Idle(dialog); Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.EqualTo(1));
        Press(dialog, PhysicalKey.P, RawInputModifiers.Control); await Idle(dialog); Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.EqualTo(2));
        Press(dialog, PhysicalKey.N, RawInputModifiers.Control); await Idle(dialog); Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedIndex, Is.EqualTo(1));
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); f.Main!.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !dialog.IsVisible); await Idle(f.Main!);
    }

    [AvaloniaTest]
    public async Task SelectedRenameFollowsNativeGitPathspecSemanticsAndKeepsOtherWorkingChanges()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("other.txt", "other base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Other base");
        await f.Repository.RunAsync(["mv", "--", "seed.txt", "renamed λ.txt"]); await f.WriteAsync("other.txt", "leave\n"); await f.ShowAsync();
        StashDialog dialog = await Open(f, false); FileStatusListView files = Find<FileStatusListView>(dialog, "StashFilesView");
        files.SelectTags(files.Entries.Where(entry => entry.Path == "renamed λ.txt").Select(entry => entry.Tag)); await Idle(dialog);
        Click(Find<Button>(dialog, "StashSelected")); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        // Like Windows' stash push -- <selected new name>, Git retains the
        // staged deletion of the original name while stashing the full rename.
        Assert.That(File.Exists(f.File("renamed λ.txt")), Is.False);
        Assert.That((await f.Repository.GetStatusAsync()).Changes.Single(change => change.Path == "seed.txt").IndexStatus, Is.EqualTo('D'));
        Assert.That(await f.ReadAsync("other.txt"), Is.EqualTo("leave\n"));
        GitStashChanges changes = await f.Repository.GetStashChangesAsync((await f.Repository.GetStashesAsync()).Single());
        Assert.That(changes.Tracked.Files.Single().OriginalPath, Is.EqualTo("seed.txt"));
    }

    [AvaloniaTest]
    public async Task EscapeClearsViewerSelectionBeforeClosingAndDoesNotChangeGit()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("viewer selection\n"); await f.ShowAsync(); string before = await f.SnapshotAsync();
        StashDialog dialog = await Open(f, true); DiffView diff = Find<FileContentView>(dialog, "StashDiff").TextView;
        diff.Select(diff.Text.IndexOf("viewer selection", StringComparison.Ordinal), 6); diff.Editor.TextArea.Focus(); Pump(dialog);
        Press(dialog, PhysicalKey.Escape, RawInputModifiers.None); Assert.That(dialog.IsVisible, Is.True); Assert.That(diff.SelectionLength, Is.Zero);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); f.Main!.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !dialog.IsVisible); await Idle(f.Main!); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task FrenchStashControlsUseTheOriginalCatalogueAndBothThemesKeepEveryActionVisible()
    {
        await using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        await f.WriteAsync("seed.txt", "français\n"); await f.ShowAsync(); StashDialog dialog = await Open(f, false);
        Assert.That(((AccessText)Find<CheckBox>(dialog, "StashKeepIndex").Content!).Text, Does.Contain("index"));
        Assert.That(((AccessText)Find<Button>(dialog, "StashAll").Content!).Text, Is.Not.EqualTo("S_tash all changes"));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; Pump(dialog);
            foreach (string name in new[] { "StashAll", "StashSelected", "DropStash", "ApplyStash" })
            {
                Button button = Find<Button>(dialog, name); Assert.That(button.IsEffectivelyVisible, Is.True);
                Point point = button.TranslatePoint(default, dialog)!.Value; Assert.That(point.Y + button.Bounds.Height, Is.LessThan(dialog.Bounds.Height));
            }
            Capture(dialog, "stash-french-" + theme + ".png");
        }
    }

    [AvaloniaTest]
    public async Task ClosingDuringStashCancelsOwnedGitAndKeepsTheOriginalFile()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux process cancellation fixture."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("seed.txt", "pending\n"); MainWindow main = await f.ShowAsync();
        string gate = f.File("slow-git"), marker = f.File("started.pid");
        await File.WriteAllTextAsync(gate, "#!/bin/sh\nfor task_arg do\n case \"$task_arg\" in push) printf '%s\\n' \"$$\" > '" + marker + "'; sleep 60 & wait ;; esac\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(gate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        StashDialog dialog = new(new GitRepository(f.Root, new GitRunner(gate)), manage: false); Task shown = dialog.ShowDialog(main); await dialog.InitializeAsync();
        Click(Find<Button>(dialog, "StashAll")); await Until(() => File.Exists(marker)); dialog.Close(); await shown;
        Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("pending\n")); Assert.That(await f.Repository.GetStashesAsync(), Is.Empty);
        int pid = int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture); Assert.That(Directory.Exists("/proc/" + pid), Is.False);
    }

    [AvaloniaTest]
    public async Task FileContextMenuUsesOriginalCommandsAndOnlyAppearsForSavedStashFiles()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("saved\n"); await f.ShowAsync();
        StashDialog dialog = await Open(f, true); TreeView tree = Find<TreeView>(dialog, "StashFiles");
        ContextMenu menu = tree.ContextMenu!; string before = await f.SnapshotAsync();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; Pump(dialog); menu.Open(tree); Pump(dialog);
            MenuItem reset = menu.Items.OfType<MenuItem>().Single(item => item.Name == "StashResetFiles");
            MenuItem pick = menu.Items.OfType<MenuItem>().Single(item => item.Name == "StashCherryPickChanges");
            Assert.That(pick.IsEnabled && reset.IsEnabled, Is.True);
            Assert.That(pick.Icon, Is.TypeOf<Image>()); Assert.That(reset.Icon, Is.TypeOf<Image>());
            Assert.That(reset.Items.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[] { "StashResetToSelected", "StashResetToParent" }));
            MenuItem parent = reset.Items.OfType<MenuItem>().Last(); Assert.That(parent.InputGesture, Is.EqualTo(new KeyGesture(Key.R)));
            Assert.That(parent.FontWeight, Is.EqualTo(Avalonia.Media.FontWeight.Bold));
            Capture(TopLevel.GetTopLevel(pick)!, "stash-file-menu-" + theme + ".png"); menu.Close();
        }
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
        Find<ComboBox>(dialog, "StashSelection").SelectedIndex = 0; await Idle(dialog); menu.Open(tree); Pump(dialog);
        Assert.That(menu.Items.OfType<MenuItem>().All(item => !item.IsVisible), Is.True); menu.Close();
    }

    [AvaloniaTest]
    public async Task CherryPickUsesOnlyTheDisplayedFileAndStagesItWithoutDroppingTheStash()
    {
        await using Fixture f = await Fixture.CreateAsync(); const string path = "file[1]* λ.txt", adjacent = "file1 λ.txt";
        await f.WriteAsync(path, "base\n"); await f.WriteAsync(adjacent, "adjacent base\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Literal files");
        await f.WriteAsync(path, "saved literal\n"); await f.WriteAsync(adjacent, "saved adjacent\n");
        await f.Repository.StashAsync(new GitStashSaveOptions("both")); GitStash stash = (await f.Repository.GetStashesAsync()).Single();
        await f.ShowAsync(); StashDialog dialog = await Open(f, true);
        FileStatusListView files = Find<FileStatusListView>(dialog, "StashFilesView"); files.SelectTags(files.Entries.Select(entry => entry.Tag)); await Idle(dialog);
        string displayed = files.SelectedEntries.First().Path;
        string[] selectedPaths = files.SelectedEntries.Select(entry => entry.Path).ToArray();
        object? selectedStash = Find<ComboBox>(dialog, "StashSelection").SelectedItem;
        ContextMenu menu = Find<TreeView>(dialog, "StashFiles").ContextMenu!; menu.Open(files.Tree); Pump(dialog);
        Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "StashCherryPickChanges")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null);
        Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedItem, Is.SameAs(selectedStash));
        Assert.That(files.SelectedEntries.Select(entry => entry.Path), Is.EqualTo(selectedPaths));
        Assert.That(await f.ReadAsync(displayed), Is.EqualTo(displayed == path ? "saved literal\n" : "saved adjacent\n"));
        string other = displayed == path ? adjacent : path;
        Assert.That(await f.ReadAsync(other), Is.EqualTo(other == path ? "base\n" : "adjacent base\n"));
        GitStatus status = await f.Repository.GetStatusAsync(); Assert.That(status.Changes.Single().Path, Is.EqualTo(displayed)); Assert.That(status.Changes.Single().IsStaged, Is.True);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(stash.Hash));
        TreeViewItem selectedFile = files.Tree.SelectedItems!.OfType<TreeViewItem>().First();
        string parentHash = await f.Repository.ResolveRevisionAsync(stash.Hash + "^");
        Assert.That(selectedFile.Focus(), Is.True); Pump(dialog); Press(dialog, PhysicalKey.R, RawInputModifiers.None);
        ResetChangesDialog confirmation = await ResetPrompt(dialog);
        Assert.That(Find<TextBox>(confirmation, "ResetChangesMessage").Text, Does.Contain(parentHash));
        Click(Find<Button>(confirmation, "ConfirmResetChanges")); await Idle(dialog);
        Assert.That(Find<ComboBox>(dialog, "StashSelection").SelectedItem, Is.SameAs(selectedStash));
        Assert.That(files.SelectedEntries.Select(entry => entry.Path), Is.EqualTo(selectedPaths));
        Assert.That(await f.ReadAsync(path), Is.EqualTo("base\n"));
        Assert.That(await f.ReadAsync(adjacent), Is.EqualTo("adjacent base\n"));
        Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(stash.Hash));
    }

    [AvaloniaTest]
    public async Task ResetConfirmationDefaultsToCancelThenRestoresOnlySelectedSnapshotFiles()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("saved\n"); GitStash stash = (await f.Repository.GetStashesAsync()).Single();
        await f.WriteAsync("seed.txt", "local staged\n"); await f.Repository.StageAsync(["seed.txt"]); await f.WriteAsync("seed.txt", "local unstaged\n");
        await f.WriteAsync("unrelated.txt", "unrelated\n"); await f.ShowAsync(); StashDialog dialog = await Open(f, true);
        string before = await f.SnapshotAsync();
        OpenReset(dialog, parent: false); ResetChangesDialog confirmation = await ResetPrompt(dialog);
        Assert.That(Find<Button>(confirmation, "CancelResetChanges").IsDefault, Is.True);
        Assert.That(Find<CheckBox>(confirmation, "ResetDeleteNewFiles").IsEnabled, Is.False);
        Assert.That(Find<TextBox>(confirmation, "ResetChangesMessage").Text, Does.Contain(stash.Hash));
        Capture(confirmation, "stash-reset-confirmation.png"); confirmation.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Idle(dialog);
        Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("local unstaged\n"));
        OpenReset(dialog, parent: false); confirmation = await ResetPrompt(dialog); Click(Find<Button>(confirmation, "ConfirmResetChanges")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("saved\n"));
        Assert.That((await f.Repository.RunAsync(["show", ":seed.txt"])).StandardOutput, Is.EqualTo("saved\n"));
        Assert.That(await f.ReadAsync("unrelated.txt"), Is.EqualTo("unrelated\n")); Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(stash.Hash));
    }

    [AvaloniaTest]
    public async Task ResetToParentOfANewFileRequiresDeletionAndPreservesTheSavedStash()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("new.txt", "saved new\n"); await f.Repository.StageAsync(["new.txt"]);
        await f.Repository.StashAsync(new GitStashSaveOptions("new file")); GitStash stash = (await f.Repository.GetStashesAsync()).Single();
        await f.WriteAsync("new.txt", "local new\n"); await f.ShowAsync(); StashDialog dialog = await Open(f, true);
        FileStatusListView files = Find<FileStatusListView>(dialog, "StashFilesView"); files.SelectTags(files.Entries.Where(entry => entry.Path == "new.txt").Select(entry => entry.Tag)); await Idle(dialog);
        OpenReset(dialog, parent: true); ResetChangesDialog confirmation = await ResetPrompt(dialog);
        CheckBox delete = Find<CheckBox>(confirmation, "ResetDeleteNewFiles"); Assert.That(delete.IsChecked, Is.True); Assert.That(delete.IsEnabled, Is.False);
        Click(Find<Button>(confirmation, "ConfirmResetChanges")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(File.Exists(f.File("new.txt")), Is.False); Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(stash.Hash));
    }

    [AvaloniaTest]
    public async Task ClickingAStashFileKeepsKeyboardFocusThroughDiffLoadingAndAllowsResetWithoutRefocusing()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.WriteAsync("other.txt", "other base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Two files");
        await f.WriteAsync("seed.txt", "seed saved\n"); await f.WriteAsync("other.txt", "other saved\n");
        await f.Repository.StashAsync(new GitStashSaveOptions("Two saved files"));
        await f.ShowAsync(); StashDialog dialog = await Open(f, true);
        FileStatusListView files = Find<FileStatusListView>(dialog, "StashFilesView");
        FileStatusListEntry target = files.Entries.Single(entry => entry.Path != files.SelectedEntries.First().Path);
        TreeViewItem item = files.FindItemByTag(target.Tag)!; Click(item); await Idle(dialog);
        Assert.That(files.SelectedEntries.Single().Path, Is.EqualTo(target.Path));
        Assert.That(files.Tree.IsKeyboardFocusWithin, Is.True, "Loading a clicked file must retain the list's keyboard focus.");
        string before = await f.SnapshotAsync(); Press(dialog, PhysicalKey.R, RawInputModifiers.None);
        ResetChangesDialog confirmation = await ResetPrompt(dialog);
        Click(Find<Button>(confirmation, "CancelResetChanges")); await Idle(dialog);
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task FileResetShortcutCannotMutateAStashThatDisappearedWhileConfirmationWasOpen()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SaveAsync("saved\n"); GitStash stash = (await f.Repository.GetStashesAsync()).Single();
        await f.ShowAsync(); StashDialog dialog = await Open(f, true); TreeView tree = Find<TreeView>(dialog, "StashFiles");
        TreeViewItem selected = tree.SelectedItems!.OfType<TreeViewItem>().Single(); Assert.That(selected.Focus(), Is.True); Pump(dialog);
        Press(dialog, PhysicalKey.R, RawInputModifiers.None);
        ResetChangesDialog confirmation = await ResetPrompt(dialog); await f.Repository.DropStashAsync(stash);
        string before = await f.SnapshotAsync(); Click(Find<Button>(confirmation, "ConfirmResetChanges")); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Not.Null); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(await f.ReadAsync("seed.txt"), Is.EqualTo("seed\n"));
        Assert.That((await f.Repository.GetStashesAsync()), Is.Empty);
    }

    private static void OpenReset(StashDialog dialog, bool parent)
    {
        TreeView tree = Find<TreeView>(dialog, "StashFiles"); ContextMenu menu = tree.ContextMenu!; menu.Open(tree); Pump(dialog);
        MenuItem reset = menu.Items.OfType<MenuItem>().Single(item => item.Name == "StashResetFiles"); reset.IsSubMenuOpen = true; Pump(dialog);
        Click(reset.Items.OfType<MenuItem>().Single(item => item.Name == (parent ? "StashResetToParent" : "StashResetToSelected")));
    }
    private static async Task<ResetChangesDialog> ResetPrompt(StashDialog dialog)
    { await Until(() => dialog.OwnedWindows.OfType<ResetChangesDialog>().Any()); ResetChangesDialog confirmation = dialog.OwnedWindows.OfType<ResetChangesDialog>().Single(); Pump(confirmation); return confirmation; }

    private static MenuItem Item(MenuFlyout menu, string suffix) => menu.Items.OfType<MenuItem>().Single(item => item.Name == "StashMenu" + suffix);
    private static T Find<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers)
    { root.KeyPressQwerty(key, modifiers); root.KeyReleaseQwerty(key, modifiers); Pump(root); }
    private static void Click(Control item)
    {
        TopLevel root = TopLevel.GetTopLevel(item)!; Pump(root); Assert.That(item.IsEffectivelyVisible && item.IsEffectivelyEnabled, Is.True, item.Name);
        Point p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
        root.MouseMove(p); root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump(root);
    }
    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "Stash UI did not reach the expected state.");
    }
    private static async Task Idle(RepositoryDialog dialog) { await Task.Delay(30); await Until(() => !dialog.IsBusy); Pump(dialog); }
    private static async Task Idle(MainWindow main) { await Task.Delay(30); await Until(() => !main.IsBusy); Pump(main); }
    private static async Task<StashDialog> Open(Fixture f, bool manage)
    {
        if (manage) { Click(Find<Button>(f.Main!, "BrowseStash")); }
        else { Button arrow = Find<Button>(f.Main!, "BrowseStashDropdown"); Click(arrow); Click(Item((MenuFlyout)arrow.Flyout!, "Create")); }
        await Until(() => f.Main!.OwnedWindows.OfType<StashDialog>().Any(dialog => !dialog.IsBusy));
        StashDialog dialog = f.Main!.OwnedWindows.OfType<StashDialog>().Single(); await Idle(dialog); return dialog;
    }
    private static async Task Close(Fixture f, StashDialog dialog) { dialog.Close(); await Until(() => !dialog.IsVisible); await Idle(f.Main!); }
    private static void Capture(TopLevel root, string filename)
    {
        Pump(root); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = root.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, filename); frame!.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-stash-ui-" + Guid.NewGuid().ToString("N"));
        internal string File(string path) => Path.Combine(Root, path);
        internal GitRepository Repository => new(Root);
        internal MainWindow? Main { get; private set; }
        private readonly Dictionary<string, string?> _environment = new();
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private Fixture()
        {
            foreach ((string key, string value) in new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = Root + ".config", ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" })
            { _environment[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(); GitRepository r = await GitRepository.InitAsync(f.Root);
            await r.SetConfigAsync("user.name", "Stash Tester"); await r.SetConfigAsync("user.email", "stash@example.invalid");
            await r.SetConfigAsync("commit.gpgsign", "false"); await r.SetConfigAsync("core.hooksPath", "/dev/null");
            await f.WriteAsync("seed.txt", "seed\n"); await r.StageAsync(["seed.txt"]); await r.CommitAsync("Initial"); return f;
        }
        internal async Task<MainWindow> ShowAsync()
        { Main = new MainWindow { Width = 1280, Height = 752 }; Main.Show(); await Main.OpenRepositoryAsync(Root); await Idle(Main); return Main; }
        internal Task WriteAsync(string path, string text) => System.IO.File.WriteAllTextAsync(File(path), text);
        internal Task<string> ReadAsync(string path) => System.IO.File.ReadAllTextAsync(File(path));
        internal async Task SaveAsync(string text) { await WriteAsync("seed.txt", text); await Repository.StashAsync(new GitStashSaveOptions(text.Trim())); }
        internal async Task<string> SnapshotAsync() => (await Repository.RunAsync(["status", "--porcelain=v2", "-z"])).StandardOutput
            + (await Repository.RunAsync(["ls-files", "--stage", "-z"])).StandardOutput + (await Repository.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"])).StandardOutput;
        public async ValueTask DisposeAsync()
        {
            if (Main is not null)
            {
                foreach (Window child in Main.OwnedWindows.ToArray()) { foreach (Window nested in child.OwnedWindows.ToArray()) { nested.Close(); } child.Close(); }
                await Idle(Main); Main.Close();
            }
            foreach ((string key, string? value) in _environment) { Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = _culture; Directory.Delete(Root, true);
            if (Directory.Exists(Root + ".config")) { Directory.Delete(Root + ".config", true); }
        }
    }
}
