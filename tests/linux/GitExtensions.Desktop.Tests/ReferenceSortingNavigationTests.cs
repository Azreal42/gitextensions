using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ReferenceSortingNavigationTests
{
    [AvaloniaTest]
    public async Task NativeVersionOrderingSurvivesPriorityBucketsFoldersRemoteNamesWithSlashesAndTags()
    {
        using Fixture f = await CreateReferencesAsync();
        new PreferencesStore().Save(new() { ReferenceSorting = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Descending), PrioritizedBranchNames = "release/.*;main", PrioritizedRemoteNames = "upstream;Team/Upstream;origin" });
        string refs = (await f.GitAsync("show-ref")).StandardOutput; byte[] index = File.ReadAllBytes(f.PathInRepository(".git/index"));
        MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            Assert.That(References(Group(main, "Branches")).Select(reference => reference.ShortName), Is.EqualTo(new[] { "release/v10", "release/v2", "main", "zeta", "alpha" }));
            TreeViewItem remotes = Group(main, "Remotes");
            Assert.That(remotes.Items.OfType<TreeViewItem>().Select(Caption), Is.EqualTo(new[] { "upstream", "Team/Upstream", "origin" }));
            foreach (TreeViewItem remote in remotes.Items.OfType<TreeViewItem>())
            { Assert.That(References(remote).Select(reference => reference.ShortName), Is.EqualTo(new[] { Caption(remote) + "/release/v10", Caption(remote) + "/release/v2", Caption(remote) + "/main", Caption(remote) + "/zeta" })); }
            Assert.That(References(Group(main, "Tags")).Select(reference => reference.ShortName), Is.EqualTo(new[] { "v10", "v2" }), "Tags keep native ordering and never receive branch priority buckets.");
            Assert.That((await f.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs)); Assert.That(File.ReadAllBytes(f.PathInRepository(".git/index")), Is.EqualTo(index));
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task RealTreeSortMenuPreservesSelectionExpansionFilterAndFirstArrowWithoutGlobalRefresh()
    {
        using Fixture f = await CreateReferencesAsync(); MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            TreeView tree = Tree(main); TreeViewItem folder = Group(main, "Branches").Items.OfType<TreeViewItem>().Single(item => Caption(item) == "release");
            Control chevron = folder.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_ExpandCollapseChevron" && control.TemplatedParent == folder);
            PushProcessTestDriver.Pointer(chevron); Pump(main); Assert.That(folder.IsExpanded, Is.False);
            TextBox filter = Field<TextBox>(main, "RepositoryNavigationSearch"); Edit(main, filter, "a");
            TreeViewItem leaf = Leaf(main, "refs/heads/main"); RightClick(main, leaf); await Until(() => tree.ContextMenu!.IsOpen); ContextMenu menu = tree.ContextMenu!;
            MenuItem by = Item(menu, "GitRefsSortByContextMenuItem"), order = Item(menu, "GitRefsSortOrderContextMenuItem");
            Assert.That(by.IsEnabled, Is.True); Assert.That(order.IsEnabled, Is.False);
            GitCommandLog.Clear(); PushProcessTestDriver.Pointer(by); Pump(main);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                main.RequestedThemeVariant = theme; Pump(main);
                Assert.That(by.Items.OfType<MenuItem>().Select(item => item.ToggleType), Is.All.EqualTo(MenuItemToggleType.CheckBox));
                Assert.That(by.Items.OfType<MenuItem>().Single(item => item.IsChecked).Tag, Is.EqualTo(GitRefsSortBy.GitDefault));
                TopLevel popup = TopLevel.GetTopLevel(by.Items.OfType<MenuItem>().First())!; Pump(popup);
                using var frame = popup.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
                string capture = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"reference-sorting-context-{theme}.png"); frame!.Save(capture); TestContext.AddTestAttachment(capture);
            }
            main.RequestedThemeVariant = ThemeVariant.Light; Pump(main);
            PushProcessTestDriver.Pointer(by.Items.OfType<MenuItem>().Single(item => item.Tag is GitRefsSortBy.VersionRefName));
            await Until(() => !main.IsBusy && new PreferencesStore().Load().ReferenceSorting.By == GitRefsSortBy.VersionRefName); Pump(main);
            Assert.That(filter.Text, Is.EqualTo("a")); Assert.That((tree.SelectedItem as TreeViewItem)?.Tag, Is.TypeOf<GitReference>());
            Assert.That(((GitReference)((TreeViewItem)tree.SelectedItem!).Tag!).Name, Is.EqualTo("refs/heads/main"));
            Assert.That(tree.IsKeyboardFocusWithin, Is.True); Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command is "git status" or "git log" or "git ls-tree"), Is.False, "A reference resort is a scoped ref read, not a reload of the repository/history.");
            Press(main, PhysicalKey.ArrowDown); await Until(() => !main.IsBusy); Pump(main);
            Assert.That(((tree.SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo("refs/heads/zeta"), "The first arrow must navigate from the preserved row without a focus repair.");
            Edit(main, filter, ""); Assert.That(filter.Text ?? "", Is.Empty);
            folder = Group(main, "Branches").Items.OfType<TreeViewItem>().Single(item => Caption(item) == "release"); Assert.That(folder.IsExpanded, Is.False);
            RightClick(main, folder); await Until(() => tree.ContextMenu!.IsOpen); Assert.That(Item(menu, "GitRefsSortByContextMenuItem").IsEnabled, Is.False); Assert.That(Item(menu, "GitRefsSortOrderContextMenuItem").IsEnabled, Is.False); menu.Close(); Pump(main);
            RightClick(main, Leaf(main, "refs/heads/main")); await Until(() => menu.IsOpen); Assert.That(Item(menu, "GitRefsSortOrderContextMenuItem").IsEnabled, Is.True); menu.Close();
            Assert.That(main.LastException, Is.Null);
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task SavedNativeOrderingIsAppliedBeforeToolbarHundredAndGoToThousandBounds()
    {
        using Fixture f = await Fixture.CreateAsync(); string head = (await f.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        string updates = string.Join("", Enumerable.Range(0, 1010).Select(index => $"update refs/heads/b{index:0000} {head}\nupdate refs/tags/v{index:0000} {head}\n"));
        GitResult created = await new GitRunner().RunAsync(f.Repository.Path, ["update-ref", "--stdin"], standardInput: updates); Assert.That(created.Success, Is.True, created.StandardError);
        new PreferencesStore().Save(new() { ReferenceSorting = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Descending), PrioritizedBranchNames = "", PrioritizedRemoteNames = "" });
        MainWindow main = await MainAsync(f.Repository.Path);
        try
        {
            Button branch = Field<Button>(main, "BrowseCurrentBranch"); PushProcessTestDriver.Pointer(branch);
            var flyout = (MenuFlyout)branch.Flyout!;
            await Until(() => flyout.Items.OfType<MenuItem>().Count(item => item.Name == "BranchMenuLocal") == 100);
            string[] actual = flyout.Items.OfType<MenuItem>().Where(item => item.Name == "BranchMenuLocal").Select(item => ((GitReference)item.Tag!).ShortName).ToArray();
            Assert.That(actual, Is.EqualTo(new[] { "main" }.Concat(Enumerable.Range(911, 99).Reverse().Select(index => $"b{index:0000}"))));
            Press(main, PhysicalKey.Escape); Pump(main);
            MenuItem navigate = Menu(main, "NavigateMenu"); PushProcessTestDriver.Pointer(navigate); Pump(main);
            PushProcessTestDriver.Pointer(navigate.Items.OfType<MenuItem>().Single(item => item.Name == "NavigateGotoCommit"));
            await Until(() => main.OwnedWindows.OfType<GoToCommitDialog>().Any(dialog => dialog.IsVisible && !dialog.IsLoadingTags && !dialog.IsLoadingBranches));
            GoToCommitDialog go = main.OwnedWindows.OfType<GoToCommitDialog>().Single(); await go.Completion; Pump(go);
            string[] branches = Field<ComboBox>(go, "comboBoxBranches").Items.Cast<string>().ToArray(), tags = Field<ComboBox>(go, "comboBoxTags").Items.Cast<string>().ToArray();
            Assert.That(branches.Length, Is.EqualTo(1000)); Assert.That(branches[0], Is.EqualTo("main")); Assert.That(branches[1], Is.EqualTo("b1009")); Assert.That(branches[^1], Is.EqualTo("b0011")); Assert.That(branches, Does.Not.Contain("b0010"));
            Assert.That(tags.Length, Is.EqualTo(1000)); Assert.That(tags[0], Is.EqualTo("v1009")); Assert.That(tags[^1], Is.EqualTo("v0010"));
            Press(go, PhysicalKey.Escape); await Until(() => !go.IsVisible && !main.IsBusy); Assert.That(go.WasAccepted, Is.False);
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task SettingsSaveSortingWithoutSavingUnrelatedSessionOnlyViewChoices()
    {
        using Fixture f = await Fixture.CreateAsync(); HistoryViewPreferencesStore historyStore = new(); HistoryViewPreferences saved = historyStore.Load(); Assert.That(saved.ShowTags, Is.True);
        MainWindow main = await MainAsync(f.Repository.Path); MainWindow? restored = null;
        try
        {
            PushProcessTestDriver.Pointer(Menu(main, "ViewMenu")); Pump(main);
            PushProcessTestDriver.Pointer(ViewItem(main, "ViewshowTagsToolStripMenuItem")); await Until(() => !main.IsBusy); Pump(main);
            Assert.That(ViewItem(main, "ViewshowTagsToolStripMenuItem").IsChecked, Is.False); Assert.That(historyStore.Load().ShowTags, Is.True);
            Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
            await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any()); DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(); Pump(settings);
            PushProcessTestDriver.Pointer(Field<TabItem>(settings, "SettingsSortingTab")); Pump(settings);
            Field<ComboBox>(settings, "_NO_TRANSLATE_cmbRevisionsSortBy").SelectedItem = GitLogSort.AuthorDate;
            Field<ComboBox>(settings, "_NO_TRANSLATE_cmbBranchesSortBy").SelectedItem = GitRefsSortBy.VersionRefName;
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings")); await Until(() => !settings.IsVisible && !main.IsBusy); Pump(main);
            Assert.That(historyStore.Load().Sort, Is.EqualTo(GitLogSort.AuthorDate)); Assert.That(historyStore.Load().ShowTags, Is.EqualTo(saved.ShowTags));
            Assert.That(ViewItem(main, "ViewshowTagsToolStripMenuItem").IsChecked, Is.False); Assert.That(ViewItem(main, "ViewAuthorDateSort").IsChecked, Is.True);
            Assert.That(new PreferencesStore().Load().ReferenceSorting.By, Is.EqualTo(GitRefsSortBy.VersionRefName));
            restored = await MainAsync(f.Repository.Path);
            Assert.That(ViewItem(restored, "ViewshowTagsToolStripMenuItem").IsChecked, Is.True); Assert.That(ViewItem(restored, "ViewAuthorDateSort").IsChecked, Is.True);
        }
        finally { if (restored is not null) { await CloseAsync(restored); } await CloseAsync(main); }
    }

    private static async Task<Fixture> CreateReferencesAsync()
    {
        Fixture f = await Fixture.CreateAsync();
        try
        {
            foreach (string name in new[] { "alpha", "zeta", "release/v2", "release/v10" }) { await f.GitAsync("branch", name); }
            foreach (string name in new[] { "v2", "v10" }) { await f.GitAsync("tag", name); }
            foreach (string remote in new[] { "origin", "upstream", "Team/Upstream" })
            {
                await f.GitAsync("config", "remote." + remote + ".url", f.Repository.Path);
                foreach (string name in new[] { "main", "zeta", "release/v2", "release/v10" }) { await f.GitAsync("update-ref", "refs/remotes/" + remote + "/" + name, "HEAD"); }
            }
            return f;
        }
        catch { f.Dispose(); throw; }
    }
    private static async Task<MainWindow> MainAsync(string repository) { MainWindow main = new() { Width = 1440, Height = 900 }; main.Show(); await main.OpenRepositoryAsync(repository); await Until(() => !main.IsBusy); Pump(main); return main; }
    private static TreeView Tree(MainWindow main) => Field<TreeView>(main, "RepositoryNavigation");
    private static IEnumerable<TreeViewItem> Nodes(MainWindow main) => BrowseTestHelpers.NavigationNodes(Tree(main));
    private static TreeViewItem Group(MainWindow main, string name) => Tree(main).Items.OfType<TreeViewItem>().Single(item => Caption(item) == name);
    private static TreeViewItem Leaf(MainWindow main, string name) => Nodes(main).Single(item => item.Tag is GitReference reference && reference.Name == name);
    private static IEnumerable<GitReference> References(TreeViewItem root) { foreach (TreeViewItem node in root.Items.OfType<TreeViewItem>()) { if (node.Tag is GitReference reference) { yield return reference; } foreach (GitReference child in References(node)) { yield return child; } } }
    private static string Caption(TreeViewItem item) => ((StackPanel)item.Header!).Children.OfType<TextBlock>().Single().Text!;
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static MenuItem Menu(MainWindow main, string name) => main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static MenuItem ViewItem(MainWindow main, string name) => Menu(main, "ViewMenu").Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static void RightClick(MainWindow main, TreeViewItem row)
    {
        Pump(main); Control label = (Control)row.Header!; Point point = label.TranslatePoint(new Point(Math.Min(35, label.Bounds.Width / 2), 8), main)!.Value;
        Visual? hit = main.InputHitTest(point) as Visual; Assert.That(hit?.GetSelfAndVisualAncestors().Contains(row), Is.True, "The native right-click must hit the intended tree row.");
        main.MouseMove(point); main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right); Pump(main);
    }
    private static void Edit(Window window, TextBox input, string text) { PushProcessTestDriver.Pointer(input); Press(window, PhysicalKey.A, RawInputModifiers.Control); if (text.Length == 0) { Press(window, PhysicalKey.Backspace); } else { window.KeyTextInput(text); } Pump(window); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition) { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "The reference-sorting workflow did not settle."); }
    private static async Task CloseAsync(MainWindow main) { foreach (Window child in main.OwnedWindows.ToArray()) { if (child is GoToCommitDialog go) { child.Close(); await go.Completion; } else { child.Close(); } } main.Close(); await Until(() => !main.IsVisible && !main.IsBusy); }
}
