using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FocusedRevisionMenuTests
{
    [AvaloniaTest]
    [TestCase(false, RawInputModifiers.None, true)]
    [TestCase(true, RawInputModifiers.None, false)]
    [TestCase(false, RawInputModifiers.Shift, false)]
    [TestCase(true, RawInputModifiers.Shift, false)]
    [TestCase(false, RawInputModifiers.Control, true)]
    [TestCase(true, RawInputModifiers.Control, true)]
    [TestCase(false, RawInputModifiers.Control | RawInputModifiers.Shift, true)]
    [TestCase(true, RawInputModifiers.Control | RawInputModifiers.Shift, true)]
    public async Task ActualBadgePointerUsesTheOriginalModifierPrecedenceAndFiltersBothMenuShapes(bool always, RawInputModifiers modifiers, bool focused)
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); SaveGlobal(always);
        MainWindow main = f.Window; string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        ContextMenu menu = OpenBadge(main, "refs/heads/shared", modifiers);
        Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "RevisionOtherActions"), Is.EqualTo(focused));
        foreach (string group in new[] { "RevisionCheckoutBranch", "RevisionRenameBranch", "RevisionDeleteBranch", "RevisionPushBranch", "RevisionMergeBranch" })
        { Assert.That(RefNames(Item(menu, group)), Is.EqualTo(new[] { "refs/heads/shared" }), group); }
        Assert.That(Item(menu, "RevisionSelectInLeftPanel").Items, Is.Empty);
        Assert.That(((GitReference)Item(menu, "RevisionSelectInLeftPanel").Tag!).Name, Is.EqualTo("refs/heads/shared"));
        Assert.That(Item(menu, "RevisionDeleteTag").IsVisible, Is.False);
        Assert.That(CopyRefNames(menu), Is.EqualTo(new[] { "shared" }));
        if (focused)
        {
            string[] advanced = Item(menu, "RevisionOtherActions").Items.OfType<MenuItem>().Select(item => item.Name!).ToArray();
            Assert.That(advanced, Does.Contain("RevisionResetAnother").And.Contain("RevisionCreateTag").And.Contain("RevisionCheckout")
                .And.Contain("HistoryAdvancedMenu").And.Contain("RevisionNavigate").And.Contain("RevisionView"));
            Assert.That(Item(menu, "RevisionOtherActions").Items.OfType<Control>().Where(item => item.IsVisible)
                .Select(item => item is Separator ? "|" : item.Name), Is.EqualTo(new[]
                { "RevisionResetAnother", "RevisionCreateTag", "|", "RevisionCheckout", "RevisionRevert", "RevisionCherryPick", "RevisionArchive", "HistoryAdvancedMenu", "|", "RevisionNavigate", "RevisionView" }));
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "RevisionResetAnother"), Is.False);
        }
        AssertCleanSeparators(menu.Items.Cast<object>());
        menu.Close(); await Idle(main);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("refs/tags/shared")]
    [TestCase("refs/tags/light-target")]
    public async Task TagBadgesUseTheirDereferencedCommitButKeepTheirOwnUnambiguousRefIdentity(string name)
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); string before = await Witness(f);
        ContextMenu menu = OpenBadge(f.Window, name);
        Assert.That(RefNames(Item(menu, "RevisionDeleteTag")), Is.EqualTo(new[] { name }));
        Assert.That(RefNames(Item(menu, "RevisionMergeBranch")), Is.EqualTo(new[] { name }));
        Assert.That(Item(menu, "RevisionCheckoutBranch").IsVisible, Is.False);
        Assert.That(Item(menu, "RevisionPushBranch").IsVisible, Is.False);
        Assert.That(Item(menu, "RevisionRenameBranch").IsVisible, Is.False);
        Assert.That(Item(menu, "RevisionDeleteBranch").IsVisible, Is.False);
        Assert.That(CopyRefNames(menu), Is.EqualTo(new[] { name[10..] }));
        AssertCleanSeparators(menu.Items.Cast<object>());
        GitReference tag = (GitReference)Item(menu, "RevisionDeleteTag").Items.OfType<MenuItem>().Single().Tag!;
        Assert.That(tag.RevisionHash, Is.EqualTo(((GitCommit)History(f.Window).SelectedItem!).Hash));
        if (name == "refs/tags/shared") { Assert.That(tag.Hash, Is.Not.EqualTo(tag.RevisionHash), "This must be a real annotated tag, not a lightweight stand-in."); }
        menu.Close(); await Idle(f.Window); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheCurrentBranchKeepsDisabledDeleteVisibleAndSymbolicReferencesAreNeverTargets()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create();
        await Git(f, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ContextMenu menu = OpenBadge(f.Window, "refs/heads/main");
        Assert.That(Item(menu, "RevisionDeleteBranch").IsVisible, Is.True);
        Assert.That(Item(menu, "RevisionDeleteBranch").IsEnabled, Is.False);
        Assert.That(Item(menu, "HistoryRebaseImmediate").IsEnabled, Is.False);
        Assert.That(Item(menu, "HistoryRebaseAdvanced").IsEnabled, Is.False);
        Assert.That(RefNames(Item(menu, "RevisionPushBranch")), Is.EqualTo(new[] { "refs/heads/main" }));
        menu.Close(); await Idle(f.Window);
        GitCommit current = History(f.Window).Items.OfType<GitCommit>().Single(commit => commit.Subject == "Merge feature branch");
        menu = OpenSubject(f.Window, current);
        foreach (MenuItem group in menu.Items.OfType<MenuItem>())
        { Assert.That(RefNames(group), Does.Not.Contain("refs/remotes/origin/HEAD")); }
        menu.Close(); await Idle(f.Window);
    }

    [AvaloniaTest]
    public async Task FullMenuOutsideABadgeStillShowsDisabledDeleteForTheOnlyCurrentBranch()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Idle(f.Window); string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        GitCommit current = History(f.Window).Items.OfType<GitCommit>().Single();
        ContextMenu menu = OpenSubject(f.Window, current); AssertFullUnfilteredShapeOnly(menu);
        Assert.That(Item(menu, "RevisionDeleteBranch").Items, Is.Empty);
        Assert.That(Item(menu, "RevisionDeleteBranch").IsVisible, Is.True);
        Assert.That(Item(menu, "RevisionDeleteBranch").IsEnabled, Is.False);
        AssertCleanSeparators(menu.Items.Cast<object>()); menu.Close(); await Idle(f.Window);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("RevisionMergeBranch", "refs/heads/shared", "CancelMerge")]
    [TestCase("RevisionMergeBranch", "refs/tags/shared", "CancelMerge")]
    [TestCase("RevisionPushBranch", "refs/heads/shared", "CancelPush")]
    [TestCase("RevisionDeleteTag", "refs/tags/shared", "CancelDeleteTag")]
    public async Task ActualSubmenuClicksOpenTheSharedDialogForOnlyTheClickedRefAndCancelKeepsGit(string group, string reference, string cancel)
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); SaveGlobal(false);
        string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        MainWindow main = f.Window; ContextMenu menu = OpenBadge(main, reference);
        MenuItem parent = Item(menu, group); PushProcessTestDriver.Pointer(parent);
        await Until(() => parent.IsSubMenuOpen); MenuItem entry = parent.Items.OfType<MenuItem>().Single();
        Assert.That(((GitReference)entry.Tag!).Name, Is.EqualTo(reference)); PushProcessTestDriver.Pointer(entry);
        await Until(() => main.OwnedWindows.OfType<RepositoryDialog>().Any(dialog => !dialog.IsBusy));
        RepositoryDialog dialog = main.OwnedWindows.OfType<RepositoryDialog>().Single();
        Assert.That(menu.IsOpen, Is.False);
        if (dialog is MergeBranchDialog merge) { Assert.That(merge.ReadOptions().Revision, Is.EqualTo(reference)); }
        else if (dialog is PushDialog push)
        {
            Assert.That(Field<AutoCompleteBox>(push, "PushBranch").Text, Is.EqualTo("shared"));
            Assert.That(Field<ComboBox>(push, "PushRemote").SelectedItem, Is.EqualTo("origin"));
            Assert.That(push.Attempted || push.Succeeded, Is.False);
        }
        else
        { Assert.That(((GitReference)Field<ComboBox>(dialog, "DeleteTagSelection").SelectedItem!).Name, Is.EqualTo(reference)); }
        PushProcessTestDriver.Pointer(Field<Button>(dialog, cancel)); await Until(() => !dialog.IsVisible && !main.IsBusy); await Idle(main);
        Assert.That(History(main).IsKeyboardFocusWithin, Is.True);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
        GitCommit[] rows = History(main).Items.OfType<GitCommit>().ToArray(); int selected = Array.FindIndex(rows, row => row.Hash == ((GitCommit)History(main).SelectedItem!).Hash);
        Assert.That(selected, Is.GreaterThan(0)); Press(main, PhysicalKey.ArrowUp);
        await Until(() => (History(main).SelectedItem as GitCommit)?.Hash == rows[selected - 1].Hash);
    }

    [AvaloniaTest]
    public async Task RebaseOwnsTheFullClickedTagTargetAfterPopupClosureAndAnAsynchronousRefreshBeforeClick()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        ContextMenu menu = OpenBadge(f.Window, "refs/tags/shared"); MenuItem advanced = Item(menu, "HistoryRebaseAdvanced");
        menu.Close();
        object? beforeRefresh = History(f.Window).ItemsSource;
        long lastCommandIdBeforeRefresh = GitCommandLog.Snapshot().LastOrDefault()?.Id ?? 0;
        Press(f.Window, PhysicalKey.F5);
        await Until(() => GitCommandLog.Snapshot().Any(entry => entry.Id > lastCommandIdBeforeRefresh && entry.Command == "git log")
            && !f.Window.IsBusy && f.Window.WindowWorkCompletion.IsCompleted);
        await f.Window.WindowWorkCompletion; await Idle(f.Window);
        Assert.That(History(f.Window).ItemsSource, Is.SameAs(beforeRefresh));
        Assert.That(menu.IsOpen, Is.False);
        advanced.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => f.Window.OwnedWindows.OfType<RebaseDialog>().Any(dialog => !dialog.IsBusy && dialog.State is not null));
        RebaseDialog rebase = f.Window.OwnedWindows.OfType<RebaseDialog>().Single();
        Assert.That(rebase.ReadOptions().BranchName, Is.EqualTo("refs/tags/shared")); Assert.That(rebase.HasRepositoryChanges, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(rebase, "CancelRebase")); await Idle(f.Window);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task SelectInLeftPanelIsDirectForOneRefAndRevealsTheTreeWithoutFilteringOrReplacingTheCommits()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        ContextMenu menu = OpenBadge(f.Window, "refs/tags/shared"); ListBox history = History(f.Window);
        object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray(); object selected = history.SelectedItem!;
        MenuItem select = Item(menu, "RevisionSelectInLeftPanel"); Assert.That(select.Items, Is.Empty);
        PushProcessTestDriver.Pointer(select); await Until(() => !menu.IsOpen); Pump(f.Window);
        TreeView tree = f.Window.GetVisualDescendants().OfType<TreeView>().Single(view => view.Name == "RepositoryNavigation");
        Assert.That(((TreeViewItem)tree.SelectedItem!).Tag, Is.TypeOf<GitReference>());
        Assert.That(((GitReference)((TreeViewItem)tree.SelectedItem!).Tag!).Name, Is.EqualTo("refs/tags/shared"));
        Assert.That(tree.IsKeyboardFocusWithin, Is.True, "The explicit tree action has priority over the menu's ordinary return to the grid.");
        AssertHistoryIdentity(history, source, rows, selected);
        TreeViewItem target = (TreeViewItem)tree.SelectedItem!;
        TreeViewItem parent = tree.Items.OfType<TreeViewItem>().Single(node => node.Items.OfType<TreeViewItem>().Contains(target));
        Press(f.Window, PhysicalKey.ArrowLeft); await Idle(f.Window);
        Assert.That(tree.SelectedItem, Is.SameAs(parent)); Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        AssertHistoryIdentity(history, source, rows, selected);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task SelectInLeftPanelFromTheUnfilteredSubmenuKeepsTreeFocusAndTheFirstArrowWithoutRebuildingHistory()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create();
        string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles(); MainWindow main = f.Window;
        ListBox history = History(main); GitCommit commit = history.Items.OfType<GitCommit>().Single(row => row.Subject == "Feature branch change");
        ContextMenu menu = OpenSubject(main, commit); MenuItem select = Item(menu, "RevisionSelectInLeftPanel");
        Assert.That(select.Items.OfType<MenuItem>().Count(), Is.GreaterThan(1));
        object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray(); object selected = history.SelectedItem!;
        PushProcessTestDriver.Pointer(select); await Until(() => select.IsSubMenuOpen);
        MenuItem entry = select.Items.OfType<MenuItem>().Single(item => item.Tag is GitReference reference && reference.Name == "refs/tags/shared");
        PushProcessTestDriver.Pointer(entry); await Until(() => !menu.IsOpen); Pump(main);
        TreeView tree = Field<TreeView>(main, "RepositoryNavigation"); TreeViewItem target = (TreeViewItem)tree.SelectedItem!;
        Assert.That(((GitReference)target.Tag!).Name, Is.EqualTo("refs/tags/shared")); Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        AssertHistoryIdentity(history, source, rows, selected);
        TreeViewItem parent = tree.Items.OfType<TreeViewItem>().Single(node => node.Items.OfType<TreeViewItem>().Contains(target));
        Press(main, PhysicalKey.ArrowLeft); await Idle(main);
        Assert.That(tree.SelectedItem, Is.SameAs(parent)); Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        AssertHistoryIdentity(history, source, rows, selected);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task EscapeRetainsMultiSelectionAndTheNextOutsideOrKeyboardOpeningHasNoOldRefFilter()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); MainWindow main = f.Window; ListBox history = History(main);
        GitCommit target = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Feature branch change");
        GitCommit other = history.Items.OfType<GitCommit>().First(); history.SelectedItem = other; history.SelectedItems!.Add(target); await Idle(main);
        ContextMenu menu = OpenBadge(main, "refs/heads/shared");
        Assert.That(history.SelectedItems.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { other.Hash, target.Hash }));
        TopLevel popup = TopLevel.GetTopLevel(menu.Items.OfType<MenuItem>().First(item => item.IsVisible))!;
        Press(popup, PhysicalKey.Escape); await Until(() => !menu.IsOpen); await Idle(main);
        Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(history.SelectedItems.Count, Is.EqualTo(2));
        menu = OpenSubject(main, target); AssertFullUnfiltered(menu);
        menu.Close(); await Idle(main); history.Focus(); Press(main, PhysicalKey.F10, RawInputModifiers.Shift);
        await Until(() => menu.IsOpen); AssertFullUnfiltered(menu); menu.Close(); await Idle(main);
        string otherRepository = Path.Combine(Path.GetDirectoryName(f.Repository)!, "focused-second.git");
        await Git(f, "clone", "--bare", f.Repository, otherRepository);
        await main.OpenRepositoryAsync(otherRepository); await Idle(main);
        history = History(main); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == target.Subject); await Idle(main);
        menu = history.ContextMenu!; menu.Open(history); Pump(main); AssertFullUnfiltered(menu); menu.Close();
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ShiftF10UsesTheRealFocusedHistoryAndOpensExactlyOnceWithOrWithoutANativeGesture(bool nativeGesture)
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); MainWindow main = f.Window;
        ListBox history = History(main); ContextMenu menu = history.ContextMenu!;
        var gestures = main.PlatformSettings!.HotkeyConfiguration.OpenContextMenu;
        KeyGesture[] previous = gestures.ToArray(); KeyGesture contextGesture = new(Key.F10, KeyModifiers.Shift);
        gestures.Clear(); foreach (KeyGesture gesture in previous.Where(gesture => !gesture.Equals(contextGesture))) { gestures.Add(gesture); }
        if (nativeGesture) { gestures.Add(contextGesture); }
        int openings = 0, requests = 0; menu.Opening += (_, _) => openings++;
        history.AddHandler(Control.ContextRequestedEvent, (_, _) => requests++, RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            GitCommit target = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Feature branch change");
            history.SelectedItem = target; await Idle(main); object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray();
            string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles(); history.Focus();
            Press(main, PhysicalKey.F10, RawInputModifiers.Shift); await Until(() => menu.IsOpen);
            Assert.That(openings, Is.EqualTo(1)); Assert.That(requests, Is.EqualTo(1)); AssertFullUnfiltered(menu);
            AssertHistoryIdentity(history, source, rows, target);
            Press(TopLevel.GetTopLevel(menu)!, PhysicalKey.Escape); await Until(() => !menu.IsOpen); await Idle(main);
            Assert.That(history.IsKeyboardFocusWithin, Is.True); SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
        }
        finally { menu.Close(); gestures.Clear(); foreach (KeyGesture gesture in previous) { gestures.Add(gesture); } }
    }

    [AvaloniaTest]
    public async Task AConfiguredShiftF10KeyDownCommandRetainsPrecedenceOverTheContextGesture()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); MainWindow main = f.Window;
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone();
        bindings.Set("RevisionGrid", HotkeyCatalog.Find("RevisionGrid", "GoToCommit")!.Code, new(Key.F10, KeyModifiers.Shift));
        new HotkeyPreferencesStore().Save(bindings); Pump(main);
        ListBox history = History(main); ContextMenu menu = history.ContextMenu!; int openings = 0; menu.Opening += (_, _) => openings++;
        string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles(); history.Focus();
        Press(main, PhysicalKey.F10, RawInputModifiers.Shift);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "GoToCommitDialog" && window.IsVisible));
        Assert.That(menu.IsOpen, Is.False); Assert.That(openings, Is.Zero);
        Window dialog = main.OwnedWindows.Single(window => window.Name == "GoToCommitDialog");
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !dialog.IsVisible && !main.IsBusy && history.IsKeyboardFocusWithin);
        Assert.That(main.IsVisible && main.IsEffectivelyEnabled, Is.True);
        main.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(main); await Idle(main);
        Assert.That(history.IsKeyboardFocusWithin, Is.True); SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ShiftF10RequestsTheActualFocusedFileTreeInsteadOfTheRevisionMenu()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); MainWindow main = f.Window;
        TabControl tabs = Field<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "FileTreeTab"); await Idle(main);
        TreeView tree = Field<TreeView>(main, "CommitFileTree"); tree.SelectedItem = tree.Items.OfType<TreeViewItem>().First(); await Idle(main); tree.Focus(); Pump(main);
        string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        Press(main, PhysicalKey.F10, RawInputModifiers.Shift); await Until(() => tree.ContextMenu!.IsOpen);
        Assert.That(History(main).ContextMenu!.IsOpen, Is.False); Assert.That(tree.ContextMenu!.Name, Is.EqualTo("FileTreeContextMenu"));
        Press(TopLevel.GetTopLevel(tree.ContextMenu)!, PhysicalKey.Escape); await Until(() => !tree.ContextMenu.IsOpen); await Idle(main);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task CopyUsesTheClickedRefAndScriptsAndLateContributorsSurviveBothShapesAndDynamicRefresh()
    {
        using OptionalLocksDisabled locks = new(); using Fixture f = await Create(); MainWindow main = f.Window;
        ScriptDefinition script = new() { Name = "focused script", Command = "fixture-never-launched", Enabled = true, HotkeyCommandIdentifier = 9801, AddToRevisionGridContextMenu = true };
        new ScriptsPreferencesStore().Save([script]); Pump(main);
        ContextMenu menu = History(main).ContextMenu!; MenuItem late = new() { Name = "LateFocusedContributor", Header = "Late contribution" };
        menu.ItemsSource = menu.Items.Cast<object>().Append(late).ToArray();
        string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        menu = OpenBadge(main, "refs/heads/shared"); AssertContributors(menu, late, 1);
        MenuItem copy = Item(menu, "RevisionCopy"); PushProcessTestDriver.Pointer(copy); await Until(() => copy.IsSubMenuOpen);
        MenuItem branch = copy.Items.OfType<MenuItem>().Single(item => item.Icon is Image { Tag: "Branch.png" });
        string? clipboard = await ClipboardExtensions.TryGetTextAsync(main.Clipboard!);
        try { PushProcessTestDriver.Pointer(branch); await UntilAsync(async () => await ClipboardExtensions.TryGetTextAsync(main.Clipboard!) == "shared"); }
        finally { await main.Clipboard!.SetTextAsync(clipboard ?? ""); }
        await Idle(main); SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
        menu = OpenBadge(main, "refs/heads/shared");
        new ScriptsPreferencesStore().Save([script with { Name = "updated script" }, script with { Name = "second script", HotkeyCommandIdentifier = 9802 }]);
        await Until(() => Flatten(menu.Items.OfType<MenuItem>()).Count(item => item.Name?.StartsWith("UserScript_", StringComparison.Ordinal) == true) == 2);
        AssertContributors(menu, late, 2); menu.Close(); await Idle(main);
        menu = OpenBadge(main, "refs/heads/shared", RawInputModifiers.Shift); AssertContributors(menu, late, 2); AssertFullUnfilteredShapeOnly(menu);
        Assert.That(RefNames(Item(menu, "RevisionRenameBranch")), Is.EqualTo(new[] { "refs/heads/shared" })); menu.Close(); await Idle(main);
        menu = OpenBadge(main, "refs/heads/shared"); AssertContributors(menu, late, 2); menu.Close(); await Idle(main);
    }

    private static async Task<Fixture> Create()
    {
        Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        await Git(f, "branch", "shared", "feature"); await Git(f, "branch", "second-target", "feature");
        await Git(f, "tag", "-a", "shared", "feature", "-m", "Annotated target"); await Git(f, "tag", "light-target", "feature");
        await Git(f, "remote", "add", "origin", f.Repository); await Git(f, "config", "branch.shared.remote", "origin"); await Git(f, "config", "branch.shared.merge", "refs/heads/remote-shared");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window); return f;
    }
    private static async Task Git(Fixture f, params string[] arguments)
    { GitResult result = await f.Git.RunAsync(f.Repository, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
    private static void SaveGlobal(bool value)
    {
        string path = new PreferencesStore().FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"AlwaysShowAdvancedOptions\":" + (value ? "true" : "false") + ",\"CheckForUpdates\":false,\"future\":[17,null,\"Ω\"]}");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "command-help-preferences.json"), "{\"Pull\":true,\"MergeBranches\":false,\"Rebase\":true}\n");
    }
    private static ContextMenu OpenBadge(MainWindow main, string name, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        ListBox history = History(main); Pump(main);
        Control badge = history.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionReference" && control.Tag is GitReference reference && reference.Name == name);
        Pointer(main, badge, MouseButton.Right, modifiers); Assert.That(history.ContextMenu!.IsOpen, Is.True, "A real right click must open the menu without a programmatic repair."); return history.ContextMenu;
    }
    private static ContextMenu OpenSubject(MainWindow main, GitCommit target)
    {
        ListBoxItem row = History(main).GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is GitCommit commit && commit.Hash == target.Hash);
        Control subject = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionSubject");
        Pointer(main, subject, MouseButton.Right); Assert.That(History(main).ContextMenu!.IsOpen, Is.True); return History(main).ContextMenu!;
    }
    private static void Pointer(TopLevel top, Control control, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Pump(top); Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0, Is.True, control.Name);
        Assert.That((top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, control.Name);
        top.MouseMove(point); top.MouseDown(point, button, modifiers); top.MouseUp(point, button, modifiers); Pump(top);
    }
    private static void AssertFullUnfiltered(ContextMenu menu)
    { AssertFullUnfilteredShapeOnly(menu); Assert.That(RefNames(Item(menu, "RevisionRenameBranch")), Is.EquivalentTo(new[] { "refs/heads/feature", "refs/heads/shared", "refs/heads/second-target" })); Assert.That(RefNames(Item(menu, "RevisionDeleteTag")), Is.EquivalentTo(new[] { "refs/tags/shared", "refs/tags/light-target" })); }
    private static void AssertFullUnfilteredShapeOnly(ContextMenu menu) => Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "RevisionOtherActions"), Is.False);
    private static void AssertContributors(ContextMenu menu, MenuItem late, int scripts)
    {
        Assert.That(menu.Items.Cast<object>().Count(item => ReferenceEquals(item, late)), Is.EqualTo(1));
        MenuItem[] all = Flatten(menu.Items.OfType<MenuItem>()).ToArray();
        Assert.That(all.Count(item => item.Name?.StartsWith("UserScript_", StringComparison.Ordinal) == true), Is.EqualTo(scripts));
        Assert.That(all.Select(item => item.Name).Where(name => name?.StartsWith("UserScript_", StringComparison.Ordinal) == true).Distinct().Count(), Is.EqualTo(scripts));
        if (menu.Items.OfType<MenuItem>().Any(item => item.Name == "RevisionOtherActions")) { Assert.That(menu.Items.Cast<object>().Last(), Is.SameAs(Item(menu, "RevisionOtherActions"))); }
        else
        {
            object[] root = menu.Items.Cast<object>().ToArray();
            int firstCi = Array.FindIndex(root, item => item is MenuItem { Name: "ShowBuildPage" or "BuildServerPullRequestPage" });
            int lastScript = Array.FindLastIndex(root, item => item is MenuItem script && script.Name?.StartsWith("UserScript_", StringComparison.Ordinal) == true);
            Assert.That(firstCi, Is.GreaterThan(lastScript), "Windows places the Scripts contribution before the retained CI/PR command objects, even while those commands are hidden.");
        }
    }
    private static void AssertHistoryIdentity(ListBox history, object? source, object[] rows, object selected)
    {
        Assert.That(history.ItemsSource, Is.SameAs(source), "Revealing a ref must retain the actual source, not just equal GitCommit records.");
        Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(history.Items.Count, Is.EqualTo(rows.Length));
        for (int index = 0; index < rows.Length; index++) { Assert.That(history.Items[index], Is.SameAs(rows[index])); }
    }
    private static string[] RefNames(MenuItem item) => item.Items.OfType<MenuItem>().Where(entry => entry.Tag is GitReference).Select(entry => ((GitReference)entry.Tag!).Name).ToArray();
    private static string[] CopyRefNames(ContextMenu menu) => Item(menu, "RevisionCopy").Items.OfType<MenuItem>().Where(item => item.Icon is Image { Tag: "Branch.png" or "Tag.png" }).Select(item => (string)item.Tag!).ToArray();
    private static void AssertCleanSeparators(IEnumerable<object> source)
    {
        object[] visible = source.Where(item => item is not Control { IsVisible: false }).ToArray();
        Assert.That(visible.FirstOrDefault(), Is.Not.TypeOf<Separator>()); Assert.That(visible.LastOrDefault(), Is.Not.TypeOf<Separator>());
        for (int index = 1; index < visible.Length; index++) { Assert.That(visible[index - 1] is Separator && visible[index] is Separator, Is.False); }
    }
    private static async Task<string> Witness(Fixture f)
    {
        GitResult refs = await f.Git.RunAsync(f.Repository, ["show-ref"]), status = await f.Git.RunAsync(f.Repository, ["status", "--porcelain=v2"]);
        return JsonSerializer.Serialize(new { refs = refs.StandardOutput, status = status.StandardOutput,
            index = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Repository, ".git/index")))),
            config = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Repository, ".git/config")))),
            files = Directory.EnumerateFiles(f.Repository, "*", SearchOption.AllDirectories).Where(path => !path.StartsWith(Path.Combine(f.Repository, ".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(f.Repository, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))) });
    }
    private static Dictionary<string, byte[]> Profiles()
    { string root = Path.GetDirectoryName(new PreferencesStore().FilePath)!; return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes) : []; }
    private static void SameProfiles(Dictionary<string, byte[]> expected)
    { Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys)); foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); } }
    private static ListBox History(MainWindow main) => Field<ListBox>(main, "RevisionHistory");
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> source) => source.SelectMany(item => new[] { item }.Concat(Flatten(item.Items.OfType<MenuItem>())));
    private static MenuItem Item(ContextMenu menu, string name) => Flatten(menu.Items.OfType<MenuItem>()).Single(item => item.Name == name);
    private static void Pump(TopLevel top) { top.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); top.UpdateLayout(); }
    private static void Press(TopLevel top, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { top.KeyPressQwerty(key, modifiers); top.KeyReleaseQwerty(key, modifiers); Pump(top); }
    private static async Task Idle(MainWindow main) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20)); await Until(() => !main.IsBusy); Pump(main); }
    private static async Task Until(Func<bool> predicate) { DateTime limit = DateTime.UtcNow.AddSeconds(15); while (!predicate() && DateTime.UtcNow < limit) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); } Assert.That(predicate(), Is.True); }
    private static async Task UntilAsync(Func<Task<bool>> predicate) { DateTime limit = DateTime.UtcNow.AddSeconds(15); while (!await predicate() && DateTime.UtcNow < limit) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); } Assert.That(await predicate(), Is.True); }
    private sealed class OptionalLocksDisabled : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        internal OptionalLocksDisabled() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _old);
    }
}
