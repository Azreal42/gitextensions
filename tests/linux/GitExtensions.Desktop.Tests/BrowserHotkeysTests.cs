using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BrowserHotkeysTests
{
    [AvaloniaTest]
    public async Task SavingGoToCommitChangesBothMenusAndActualDispatchWithoutRebindingTheHistory()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        MainWindow main = f.Window; await Idle(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        object? source = history.ItemsSource; object? selected = history.SelectedItem; history.Focus();
        MenuItem navigate = Root(main, "NavigateMenu");
        MenuItem context = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionNavigate");
        MenuItem mainGo = Item(navigate, "NavigateGotoCommit"), contextGo = Item(context, "RevisionNavigateGotoCommit");
        byte[] index = await File.ReadAllBytesAsync(Path.Combine(f.Repository, ".git/index"));
        string head = (await f.Git.RunAsync(f.Repository, ["rev-parse", "HEAD"])).StandardOutput;
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone(); GitCommandLog.Clear();
        Set(bindings, "RevisionGrid", "GoToCommit", new(Key.G, KeyModifiers.Control | KeyModifiers.Alt)); Save(bindings);
        Assert.That(mainGo.InputGesture, Is.EqualTo(new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Alt)));
        Assert.That(contextGo.InputGesture, Is.EqualTo(mainGo.InputGesture));
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
        Press(main, PhysicalKey.G, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.That(main.OwnedWindows.Where(window => window.IsVisible), Is.Empty, "The old default must retire immediately.");
        Press(main, PhysicalKey.G, RawInputModifiers.Control | RawInputModifiers.Alt);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "GoToCommitDialog" && window.IsVisible), main);
        Window dialog = main.OwnedWindows.Single(window => window.Name == "GoToCommitDialog");
        Press(dialog, PhysicalKey.Escape); await Idle(main);
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Set(bindings, "RevisionGrid", "GoToCommit", null); Save(bindings);
        Assert.That(mainGo.InputGesture, Is.Null); Assert.That(contextGo.InputGesture, Is.Null);
        Press(main, PhysicalKey.G, RawInputModifiers.Control | RawInputModifiers.Alt); await Idle(main);
        Assert.That(main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
        AssertNoHistoryQuery();
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(f.Repository, ".git/index")), Is.EqualTo(index));
        Assert.That((await f.Git.RunAsync(f.Repository, ["rev-parse", "HEAD"])).StandardOutput, Is.EqualTo(head));
    }

    [AvaloniaTest]
    public async Task BrowseAndGridParentBindingsRemainIndependentAndBothUseTheLoadedMergeGraph()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true); await Idle(f.Window);
        ListBox history = Field<ListBox>(f.Window, "RevisionHistory");
        GitCommit merge = history.Items.OfType<GitCommit>().Single(commit => commit.Parents.Count == 2);
        object? source = history.ItemsSource; HotkeyBindings bindings = DesktopHotkeys.Current.Clone();
        Set(bindings, "Browse", "GoToParent", new(Key.F6));
        Set(bindings, "RevisionGrid", "GoToParent", new(Key.F9)); Save(bindings);
        Assert.That(Item(Root(f.Window, "NavigateMenu"), "NavigateGotoParentCommit").InputGesture, Is.EqualTo(new KeyGesture(Key.F9)));
        GitCommandLog.Clear();
        foreach (PhysicalKey key in new[] { PhysicalKey.F6, PhysicalKey.F9 })
        {
            history.SelectedItem = merge; history.Focus(); await Idle(f.Window);
            Press(f.Window, key); await Idle(f.Window);
            Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(merge.Parents[0]));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
        }
        history.SelectedItem = merge; await Idle(f.Window);
        Set(bindings, "Browse", "GoToParent", null); Save(bindings);
        Press(f.Window, PhysicalKey.F6); Press(f.Window, PhysicalKey.P, RawInputModifiers.Control); await Idle(f.Window);
        Assert.That(history.SelectedItem, Is.EqualTo(merge));
        Press(f.Window, PhysicalKey.F9); await Idle(f.Window);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(merge.Parents[0]));
        Assert.That(history.ItemsSource, Is.SameAs(source)); AssertNoHistoryQuery();
    }

    [AvaloniaTest]
    public async Task ToolbarMenuAndNewWindowReadTheSameSavedCommitGesture()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n"); await Idle(f.Window);
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone();
        Button toolbar = Field<Button>(f.Window, "BrowseCommit");
        MenuItem commit = AllMenuItems(f.Window).Single(item => item.Name == "BrowseCommit");
        object? source = Field<ListBox>(f.Window, "RevisionHistory").ItemsSource; GitCommandLog.Clear();
        Set(bindings, "Browse", "Commit", new(Key.F10, KeyModifiers.Control)); Save(bindings);
        Assert.That(commit.InputGesture, Is.EqualTo(new KeyGesture(Key.F10, KeyModifiers.Control)));
        Assert.That(ToolTip.GetTip(toolbar)?.ToString(), Does.Contain("Ctrl+F10"));
        Assert.That(Field<ListBox>(f.Window, "RevisionHistory").ItemsSource, Is.SameAs(source)); AssertNoHistoryQuery();
        MainWindow restored = new(); restored.Show();
        try
        {
            Assert.That(AllMenuItems(restored).Single(item => item.Name == "BrowseCommit").InputGesture, Is.EqualTo(commit.InputGesture));
            Assert.That(ToolTip.GetTip(Field<Button>(restored, "BrowseCommit")), Is.EqualTo(ToolTip.GetTip(toolbar)));
        }
        finally { restored.Close(); await Until(() => restored.WindowWorkCompletion.IsCompleted, f.Window); }
        Set(bindings, "Browse", "Commit", null); Save(bindings);
        Assert.That(commit.InputGesture, Is.Null); Assert.That(ToolTip.GetTip(toolbar)?.ToString(), Does.Not.Contain("("));
    }

    [AvaloniaTest]
    public async Task FocusedViewerWinsACollisionAndOrdinaryEditingKeysNeverReachTheRevisionGrid()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "changed\n", true); await Idle(f.Window);
        ListBox history = Field<ListBox>(f.Window, "RevisionHistory"); object? selected = history.SelectedItem;
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone();
        Set(bindings, "Browse", "FocusFilter", new(Key.F8));
        Set(bindings, "RevisionGrid", "GoToParent", new(Key.F8));
        Set(bindings, "FileViewer", "Find", new(Key.F8));
        Set(bindings, "RevisionGrid", "RenameRef", new(Key.F9));
        Set(bindings, "RevisionGrid", "GoToChild", new(Key.Up)); Save(bindings);
        TextBox repositoryPath = f.Window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Repository directory");
        PushProcessTestDriver.Pointer(repositoryPath); Press(f.Window, PhysicalKey.F9); await Idle(f.Window);
        Assert.That(f.Window.OwnedWindows.Where(window => window.IsVisible), Is.Empty, "A remapped reference rename must also leave the repository path editor alone.");
        history.Focus(); Press(f.Window, PhysicalKey.F8); Pump(f.Window);
        TextBox filter = Field<TextBox>(f.Window, "RevisionQuickFilter");
        Assert.That(filter.IsKeyboardFocusWithin, Is.True, "The Browse context precedes the fallback grid context.");
        Press(f.Window, PhysicalKey.ArrowUp); await Idle(f.Window);
        Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(filter.IsKeyboardFocusWithin, Is.True);
        TabControl tabs = Field<TabControl>(f.Window, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
        await Idle(f.Window); DiffView diff = f.Window.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible);
        diff.Editor.TextArea.Focus(); Press(f.Window, PhysicalKey.F8);
        await Until(() => f.Window.OwnedWindows.Any(window => window.IsVisible), f.Window);
        Assert.That(diff.Search.Dialog?.IsVisible, Is.True, "The focused child FileViewer receives its own command before Browse/Grid.");
        Press(diff.Search.Dialog!, PhysicalKey.Escape); await Idle(f.Window);
        Assert.That(history.SelectedItem, Is.SameAs(selected));
    }

    [AvaloniaTest]
    public async Task ComparingTheSelectedIndexUsesItsTypedEndpointAndFocusCommandsReachTheTrees()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "indexed\n");
        Assert.That((await f.Git.RunAsync(f.Repository, ["add", "--", "file.txt"])).Success, Is.True);
        await File.WriteAllTextAsync(Path.Combine(f.Repository, "file.txt"), "working\n");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ListBox history = Field<ListBox>(f.Window, "RevisionHistory"); history.Focus();
        Press(f.Window, PhysicalKey.Digit0, RawInputModifiers.Control);
        Assert.That(Field<TreeView>(f.Window, "RepositoryNavigation").IsKeyboardFocusWithin, Is.True);
        Press(f.Window, PhysicalKey.Digit4, RawInputModifiers.Control); await Idle(f.Window);
        Assert.That(f.Window.GetVisualDescendants().OfType<FileStatusListView>().Single(view => view.Name == "CommitFileStatusList").Tree.IsKeyboardFocusWithin, Is.True);
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone();
        Set(bindings, "RevisionGrid", "SelectAsBaseToCompare", new(Key.F9)); Save(bindings);
        PushProcessTestDriver.Pointer(Field<Button>(f.Window, "IndexRevision")); await Idle(f.Window);
        Press(f.Window, PhysicalKey.F9);
        Press(f.Window, PhysicalKey.D, RawInputModifiers.Control);
        await Until(() => f.Window.OwnedWindows.OfType<CompareRevisionsDialog>().Any(dialog => dialog.Comparison is not null), f.Window);
        CompareRevisionsDialog comparison = f.Window.OwnedWindows.OfType<CompareRevisionsDialog>().Single();
        Assert.That(comparison.Comparison!.Base.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Assert.That(comparison.Comparison.Compare.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(comparison.Comparison.Files.Select(file => file.Path), Is.EqualTo(new[] { "file.txt" }));
        Press(comparison, PhysicalKey.Escape); await Idle(f.Window);
        PushProcessTestDriver.Pointer(Field<Button>(f.Window, "WorkingDirectoryRevision")); await Idle(f.Window);
        Press(f.Window, PhysicalKey.R, RawInputModifiers.Control);
        await Until(() => f.Window.OwnedWindows.OfType<CompareRevisionsDialog>().Any(dialog => dialog.Comparison is not null), f.Window);
        comparison = f.Window.OwnedWindows.OfType<CompareRevisionsDialog>().Single();
        Assert.That(comparison.Comparison!.Base.Kind, Is.EqualTo(GitCompareRevisionKind.Index), "BASE stores the typed Index endpoint even after the selected destination changes.");
        Assert.That(comparison.Comparison.Compare.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Press(comparison, PhysicalKey.Escape); await Idle(f.Window);
        Assert.That((await f.Git.RunAsync(f.Repository, ["show", ":file.txt"])).StandardOutput, Is.EqualTo("indexed\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "file.txt")), Is.EqualTo("working\n"));
    }

    [AvaloniaTest]
    public async Task ResetFiltersKeepsReflogAndTheRememberedBranchExpression()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        async Task<string> Git(params string[] arguments)
        { GitResult result = await f.Git.RunAsync(f.Repository, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput.Trim(); }
        await Git("checkout", "--detach", "HEAD"); await Git("commit", "--allow-empty", "-m", "Reflog-only commit");
        string reflogOnly = await Git("rev-parse", "HEAD"); await Git("checkout", "main");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ListBox history = Field<ListBox>(f.Window, "RevisionHistory");
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Not.Contain(reflogOnly));
        MenuItem view = Root(f.Window, "ViewMenu");
        Item(view, "ViewShowReflogReferences").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); await Idle(f.Window);
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Contain(reflogOnly));
        ComboBox branches = Field<ComboBox>(f.Window, "BranchHistoryFilter");
        branches.Text = "feature"; branches.GetVisualDescendants().OfType<TextBox>().Single().Focus();
        Press(f.Window, PhysicalKey.Enter); await Idle(f.Window);
        string[] filtered = history.Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        Assert.That(filtered, Does.Contain(reflogOnly), "Like Windows/Git, --reflog retains its roots alongside the explicit branch expression.");
        history.Focus(); Press(f.Window, PhysicalKey.I, RawInputModifiers.Control | RawInputModifiers.Shift); await Idle(f.Window);
        Assert.That(Item(view, "ViewShowReflogReferences").IsChecked, Is.True);
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Contain(reflogOnly), "Reset must retain the active --reflog query, not just its checked menu.");
        Assert.That(branches.Text, Is.EqualTo("feature"));
        Item(view, "ViewShowFilteredBranches").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); await Idle(f.Window);
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(filtered));
    }

    [AvaloniaTest]
    public async Task CorruptConfigurationRetainsTheActiveSnapshotAndCannotBeSilentlyOverwritten()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n"); await Idle(f.Window);
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone(); Set(bindings, "RevisionGrid", "GoToCommit", new(Key.F9)); Save(bindings);
        HotkeyPreferencesStore store = new(); const string malformed = "{bad hotkeys"; await File.WriteAllTextAsync(store.FilePath, malformed);
        DesktopHotkeys.Reload();
        Assert.That(DesktopHotkeys.LoadError, Is.Not.Null);
        Assert.That(Item(Root(f.Window, "NavigateMenu"), "NavigateGotoCommit").InputGesture, Is.EqualTo(new KeyGesture(Key.F9)));
        Assert.Catch<System.Text.Json.JsonException>(() => store.Save(bindings));
        Assert.That(await File.ReadAllTextAsync(store.FilePath), Is.EqualTo(malformed));
    }

    private static void Set(HotkeyBindings bindings, string context, string command, KeyGesture? gesture)
        => bindings.Set(context, HotkeyCatalog.Find(context, command)!.Code, gesture);
    private static void Save(HotkeyBindings bindings) { new HotkeyPreferencesStore().Save(bindings); Dispatcher.UIThread.RunJobs(); }
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Root(MainWindow main, string name) => main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static MenuItem Item(MenuItem parent, string name) => parent.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static IEnumerable<MenuItem> AllMenuItems(MainWindow main)
    {
        static IEnumerable<MenuItem> Descend(MenuItem item) => new[] { item }.Concat(item.Items.OfType<MenuItem>().SelectMany(Descend));
        return main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().SelectMany(Descend);
    }
    private static void AssertNoHistoryQuery() => Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow main)
    { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted, main); await main.WindowWorkCompletion; Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Until(Func<bool> predicate, TopLevel root)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { Pump(root); await Task.Delay(10); }
        Assert.That(predicate(), Is.True, "The actual browser/modal operation did not settle.");
    }
}
