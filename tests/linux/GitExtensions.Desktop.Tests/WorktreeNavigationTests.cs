using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class WorktreeNavigationTests
{
    [AvaloniaTest]
    public async Task WorktreeSplitAppearsOnlyWithMultipleWorktreesWhileTheRepositoryManagerRemainsAccessible()
    {
        using Fixture f = await Fixture.CreateAsync(); MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            Grid split = Field<Grid>(main, "BrowseWorktreesControl");
            MenuItem manage = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>()
                .SelectMany(menu => menu.Items.OfType<MenuItem>()).Single(item => item.Name == "manageWorktreeToolStripMenuItem");
            Assert.That((await f.Repository.GetWorktreesAsync()).Count, Is.EqualTo(1));
            Assert.That(split.IsVisible, Is.False, "Windows UpdateWorktreeToolStripVisibility hides the split when only the main worktree exists.");
            Assert.That(manage.IsVisible && manage.IsEnabled, Is.True);

            string linked = await AddLinkedAsync(f, "external", "visibility-linked");
            await main.OpenRepositoryAsync(f.Repository.Path); await IdleAsync(main); Pump(main);
            Assert.That(split.IsVisible && split.IsEffectivelyVisible, Is.True);
            Assert.That(Nodes(main).Count(node => node.Tag is GitWorktree), Is.EqualTo(2));

            Directory.Delete(linked, true); await f.GitAsync("worktree", "prune");
            await main.OpenRepositoryAsync(f.Repository.Path); await IdleAsync(main); Pump(main);
            Assert.That((await f.Repository.GetWorktreesAsync()).Count, Is.EqualTo(1)); Assert.That(split.IsVisible, Is.False);
            Assert.That(manage.IsVisible && manage.IsEnabled, Is.True);
            Field<TreeView>(main, "RepositoryNavigation").Focus(); Press(main, PhysicalKey.W, RawInputModifiers.Control | RawInputModifiers.Alt);
            WorktreeDialog manager = await OwnedAsync<WorktreeDialog>(main);
            Assert.That(Field<ListBox>(manager, "WorktreeList").Items.OfType<WorktreeSnapshot>().Select(item => item.Path), Is.EqualTo(new[] { f.Repository.Path }));
            manager.Close(); await IdleAsync(main); Assert.That(main.LastException, Is.Null);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.ShortName == "external"), Is.True);
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task SingleWorktreeClickSelectsItsCommitAndKeepsTreeFocusWhileCurrentMainAndMissingActionsStayProtected()
    {
        using Fixture f = await Fixture.CreateAsync(); string first = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.ChangeAsync("later\n", "Later main commit");
        string linked = await AddLinkedAsync(f, "linked", "linked-tree", first), missing = await AddLinkedAsync(f, "missing", "missing-tree"); Directory.Delete(missing, true);
        MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            TreeView tree = Field<TreeView>(main, "RepositoryNavigation"); TreeViewItem linkedNode = Node(main, linked);
            Control label = (Control)linkedNode.Header!; Point point = label.TranslatePoint(new Point(28, 9), main)!.Value;
            main.MouseMove(point); main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            await Until(() => !main.IsBusy && Field<ListBox>(main, "RevisionHistory").SelectedItem is GitCommit commit && commit.Hash == first);
            for (int frame = 0; frame < 4; frame++) { Pump(main); await Task.Delay(10); }
            Assert.That((tree.SelectedItem as TreeViewItem)?.Tag, Is.TypeOf<GitWorktree>());
            Assert.That(((GitWorktree)((TreeViewItem)tree.SelectedItem!).Tag!).Path, Is.EqualTo(linked));
            Assert.That(tree.IsKeyboardFocusWithin, Is.True); Assert.That(Field<ListBox>(main, "RevisionHistory").IsKeyboardFocusWithin, Is.False);
            Assert.That(PathField(main).Text, Is.EqualTo(f.Repository.Path)); Assert.That(main.OwnedWindows, Is.Empty, "A single worktree click selects history without offering a switch.");
            AssertNodeActions(Node(main, f.Repository.Path), open: false, delete: false);
            AssertNodeActions(linkedNode, open: true, delete: true); AssertNodeActions(Node(main, missing), open: false, delete: false);
            TextBlock currentLabel = ((StackPanel)Node(main, f.Repository.Path).Header!).Children.OfType<TextBlock>().Single();
            Assert.That(currentLabel.FontWeight, Is.EqualTo(FontWeight.Bold));
            Assert.That(((StackPanel)Node(main, missing).Header!).Children.OfType<TextBlock>().Single().Opacity, Is.LessThan(1));
            await main.OpenRepositoryAsync(linked); await IdleAsync(main);
            AssertNodeActions(Node(main, f.Repository.Path), open: true, delete: false);
            AssertNodeActions(Node(main, linked), open: false, delete: false);
            Assert.That(main.LastException, Is.Null);
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task RootAndNodeMenusUseTheSharedDialogsAndRealPruneWhileCtrlAltWOpensTheSameManager()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "linked", "menu-linked"), missing = await AddLinkedAsync(f, "missing", "menu-missing");
        Directory.Delete(missing, true); new ProcessDialogPreferencesStore().Save(new(false)); MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            TreeViewItem root = WorktreeRoot(main); ContextMenu menu = OpenContext(root, main);
            Assert.That(menu.Name, Is.EqualTo("WorktreeRootContextMenu"));
            Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[] { "mnubtnCreateWorktreeFromRootNode", "mnubtnPruneWorktreesFromRootNode", "mnubtnManageWorktreesFromRootNode" }));
            Click(Item(menu.Items, "mnubtnCreateWorktreeFromRootNode"));
            CreateWorktreeDialog create = await OwnedAsync<CreateWorktreeDialog>(main);
            Assert.That(create.Title, Is.EqualTo("Create a new worktree")); Assert.That(create.CreatedWorktreePath, Is.Null); create.Close(); await IdleAsync(main);
            menu = OpenContext(WorktreeRoot(main), main); Click(Item(menu.Items, "mnubtnManageWorktreesFromRootNode"));
            WorktreeDialog manage = await OwnedAsync<WorktreeDialog>(main); Assert.That(manage.Title, Is.EqualTo("Existing worktrees")); manage.Close(); await IdleAsync(main);
            menu = OpenContext(Node(main, linked), main); Click(Item(menu.Items, "mnubtnOpenWorktree"));
            Window question = await QuestionAsync(main, "SwitchWorktreeConfirmation"); Click(Field<Button>(question, "CancelSwitchWorktree")); await IdleAsync(main);
            Assert.That(PathField(main).Text, Is.EqualTo(f.Repository.Path));
            menu = OpenContext(Node(main, linked), main); Click(Item(menu.Items, "mnubtnDeleteWorktree"));
            question = await QuestionAsync(main, "DeleteWorktreeConfirmation"); Assert.That(Field<Button>(question, "CancelDeleteWorktree").IsDefault, Is.True);
            Click(Field<Button>(question, "CancelDeleteWorktree")); await IdleAsync(main); Assert.That(Directory.Exists(linked), Is.True);
            menu = OpenContext(WorktreeRoot(main), main); Click(Item(menu.Items, "mnubtnPruneWorktreesFromRootNode"));
            GitProcessDialog process = await FinishedProcessAsync(main); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString()); Click(Field<Button>(process, "Ok")); await IdleAsync(main);
            Assert.That(Nodes(main).Where(node => node.Tag is GitWorktree).Select(node => ((GitWorktree)node.Tag!).Path), Is.EquivalentTo(new[] { f.Repository.Path, linked }));
            Field<TreeView>(main, "RepositoryNavigation").Focus(); Press(main, PhysicalKey.W, RawInputModifiers.Control | RawInputModifiers.Alt);
            manage = await OwnedAsync<WorktreeDialog>(main);
            Assert.That(Field<ListBox>(manage, "WorktreeList").Items.OfType<WorktreeSnapshot>().Select(item => item.Path), Is.EquivalentTo(new[] { f.Repository.Path, linked }));
            manage.Close(); await IdleAsync(main); Assert.That(PathField(main).Text, Is.EqualTo(f.Repository.Path)); Assert.That(main.LastException, Is.Null);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.ShortName == "missing"), Is.True, "Prune removes stale registration, never its branch.");
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task ToolbarButtonAndSplitOverflowUseTheSharedManagerAndSwitchDirectlyWithCurrentAndMissingStates()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = await AddLinkedAsync(f, "linked", "toolbar-linked"), missing = await AddLinkedAsync(f, "missing", "toolbar-missing"); Directory.Delete(missing, true);
        MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            Grid split = Field<Grid>(main, "BrowseWorktreesControl"); Assert.That(split.Bounds.Height, Is.EqualTo(24));
            Click(Field<Button>(main, "BrowseWorktrees")); WorktreeDialog manager = await OwnedAsync<WorktreeDialog>(main);
            manager.Close(); await IdleAsync(main);
            Button dropdown = Field<Button>(main, "BrowseWorktreesDropdown"); Click(dropdown); MenuFlyout flyout = (MenuFlyout)dropdown.Flyout!;
            await Until(() => flyout.IsOpen && flyout.Items.OfType<MenuItem>().Count(item => item.Tag is string) == 3);
            MenuItem current = PathItem(flyout.Items, f.Repository.Path), target = PathItem(flyout.Items, linked), deleted = PathItem(flyout.Items, missing);
            Assert.That(current.IsChecked, Is.True); Assert.That(current.IsEnabled, Is.False); Assert.That(target.IsEnabled, Is.True); Assert.That(deleted.IsEnabled, Is.False);
            Assert.That(flyout.Items.OfType<MenuItem>().Where(item => item.Tag is not string).Select(item => item.Name),
                Is.EqualTo(new[] { "WorktreeMenuCreate", "WorktreeMenuPrune", "WorktreeMenuManage" }));
            Click(target); await Until(() => !main.IsBusy && PathField(main).Text == linked);
            Assert.That(PushProcessTestDriver.Windows(main).Any(window => window.Name == "SwitchWorktreeConfirmation"), Is.False);
            Assert.That(flyout.IsOpen, Is.False);
            Field<CompactBrowserToolbar>(main, "RepositoryToolbar").Width = 320; Pump(main);
            Button overflowButton = Field<Button>(main, "RepositoryToolbarOverflow"); Click(overflowButton); MenuFlyout overflow = (MenuFlyout)overflowButton.Flyout!;
            MenuItem worktrees = overflow.Items.OfType<MenuItem>().Single(item => item.Name == "WorktreeOverflow"); worktrees.IsSubMenuOpen = true; Pump(main);
            await Until(() => worktrees.IsSubMenuOpen && worktrees.Items.OfType<MenuItem>().Count(item => item.Tag is string) == 3);
            Assert.That(PathItem(worktrees.Items, linked).IsChecked, Is.True); Assert.That(PathItem(worktrees.Items, linked).IsEnabled, Is.False);
            Assert.That(PathItem(worktrees.Items, missing).IsEnabled, Is.False); Assert.That(PathItem(worktrees.Items, f.Repository.Path).IsEnabled, Is.True);
            Assert.That(worktrees.Items.OfType<MenuItem>().Where(item => item.Tag is not string).Select(item => item.Name),
                Is.EqualTo(new[] { "WorktreeOverflowCreate", "WorktreeOverflowPrune", "WorktreeOverflowManage" }));
            Click(PathItem(worktrees.Items, f.Repository.Path)); await Until(() => !main.IsBusy && PathField(main).Text == f.Repository.Path);
            Assert.That(overflow.IsOpen, Is.False); Assert.That(main.OwnedWindows, Is.Empty); Assert.That(main.LastException, Is.Null);
            Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That(await GitTextAsync(linked, "symbolic-ref", "--short", "HEAD"), Is.EqualTo("linked"), "Switching windows must not check out or reset either worktree's branch.");
        }
        finally { await CloseAsync(main); }
    }

    private static IEnumerable<TreeViewItem> Nodes(MainWindow main) => BrowseTestHelpers.NavigationNodes(Field<TreeView>(main, "RepositoryNavigation"));
    private static TreeViewItem Node(MainWindow main, string path) => Nodes(main).Single(node => node.Tag is GitWorktree worktree && worktree.Path == path);
    private static TreeViewItem WorktreeRoot(MainWindow main) => Field<TreeView>(main, "RepositoryNavigation").Items.OfType<TreeViewItem>().Single(node => node.Items.OfType<TreeViewItem>().Any(child => child.Tag is GitWorktree));
    private static TextBox PathField(MainWindow main) => Field<TextBox>(main, "RepositoryPath");
    private static MenuItem Item(IEnumerable<object?> items, string name) => items.OfType<MenuItem>().Single(item => item.Name == name);
    private static MenuItem PathItem(IEnumerable<object?> items, string path) => items.OfType<MenuItem>().Single(item => item.Tag as string == path);
    private static ContextMenu OpenContext(TreeViewItem node, MainWindow main)
    { ContextMenu menu = node.ContextMenu!; menu.Open(node); Pump(main); return menu; }
    private static void AssertNodeActions(TreeViewItem node, bool open, bool delete)
    {
        MainWindow main = (MainWindow)TopLevel.GetTopLevel(node)!; ContextMenu menu = OpenContext(node, main);
        try { Assert.That(Item(menu.Items, "mnubtnOpenWorktree").IsEnabled, Is.EqualTo(open)); Assert.That(Item(menu.Items, "mnubtnDeleteWorktree").IsEnabled, Is.EqualTo(delete)); }
        finally { menu.Close(); Pump(main); }
    }
    private static async Task<string> AddLinkedAsync(Fixture f, string branch, string directory, string revision = "HEAD")
    { string path = f.Output(directory); await f.GitAsync("branch", branch, revision); await f.GitAsync("worktree", "add", "--", path, branch); return path; }
    private static async Task<MainWindow> MainAsync(string path)
    { MainWindow main = new() { Width = 1440, Height = 900 }; main.Show(); await main.OpenRepositoryAsync(path); await IdleAsync(main); Pump(main); return main; }
    private static async Task<T> OwnedAsync<T>(Window owner) where T : RepositoryDialog
    {
        await Until(() => PushProcessTestDriver.Windows(owner).OfType<T>().Any(window => window.IsVisible && !window.IsBusy));
        T dialog = PushProcessTestDriver.Windows(owner).OfType<T>().Single(window => window.IsVisible); Pump(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static async Task<Window> QuestionAsync(Window owner, string name)
    { await Until(() => PushProcessTestDriver.Windows(owner).Any(window => window.IsVisible && window.Name == name)); return PushProcessTestDriver.Windows(owner).Single(window => window.IsVisible && window.Name == name); }
    private static async Task<GitProcessDialog> FinishedProcessAsync(Window owner)
    { await Until(() => PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Any(window => window.IsVisible && !window.IsRunning)); return PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Single(window => window.IsVisible && !window.IsRunning); }
    private static async Task<string> GitTextAsync(string path, params string[] args)
    { GitResult result = await new GitRunner().RunAsync(path, args); Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput.TrimEnd('\r', '\n'); }
    private static Task IdleAsync(MainWindow main) => Until(() => !main.IsBusy);
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The shared worktree entry-point workflow did not settle.");
    }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task CloseAsync(Window window)
    {
        foreach (Window child in window.OwnedWindows.ToArray()) { await CloseAsync(child); }
        if (window.IsVisible) { window.Close(); }
        await Until(() => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy)); Pump(window);
    }
}
