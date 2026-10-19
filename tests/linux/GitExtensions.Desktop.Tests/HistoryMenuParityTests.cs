using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class HistoryMenuParityTests
{
    private static readonly string[] Navigation = ["ToggleBetweenArtificialAndHeadCommits", "GotoCurrentRevision", "GotoCommit", "|", "GotoChildCommit", "GotoParentCommit", "GotoFirstParentCommit", "GotoLastParentCommit", "GotoMergeBaseCommit", "|", "NavigateBackward", "NavigateForward", "|", "QuickSearch", "PrevQuickSearch", "NextQuickSearch"];

    [AvaloniaTest]
    public async Task OriginalCommandsAreSharedByMainAndContextMenusWithNativeIconsGroupsAndGestures()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        MenuItem navigate = Root(f.Window, "NavigateMenu"), view = Root(f.Window, "ViewMenu");
        ContextMenu context = History(f.Window).ContextMenu!;
        MenuItem contextNavigate = context.Items.OfType<MenuItem>().Single(i => i.Name == "RevisionNavigate");
        MenuItem contextView = context.Items.OfType<MenuItem>().Single(i => i.Name == "RevisionView");
        Assert.That(navigate.Items.Cast<object>().Select(i => i is Separator ? "|" : ((MenuItem)i).Name!["Navigate".Length..]), Is.EqualTo(Navigation));
        Assert.That(contextNavigate.Items.Cast<object>().Select(Caption), Is.EqualTo(navigate.Items.Cast<object>().Select(Caption)));
        Assert.That(contextView.Items.Cast<object>().Select(Caption), Is.EqualTo(view.Items.Cast<object>().SkipLast(2).Select(Caption)));
        Assert.That(view.Items.OfType<MenuItem>().Where(i => i.FontWeight == FontWeight.Bold).Select(Caption), Is.EqualTo(new[] { "Branches", "Commits", "Grid labels", "Grid info", "Columns", "Sorting", "Settings persistence" }));
        Assert.That(view.Items.OfType<MenuItem>().Count(), Is.EqualTo(38));
        Assert.That(Item(navigate, "NavigateGotoFirstParentCommit").InputGesture, Is.EqualTo(new KeyGesture(Key.Left, KeyModifiers.Control)));
        Assert.That(Item(navigate, "NavigateGotoLastParentCommit").InputGesture, Is.EqualTo(new KeyGesture(Key.Right, KeyModifiers.Control)));
        Assert.That(Item(view, "ViewShowReflogReferences").InputGesture, Is.EqualTo(new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift)));
        string[] icons = ["WorkingDirChanges.png", "GotoCurrentRevision.png", "GotoCommit.png", "GoToChildCommit.png", "GoToParentCommit.png", "GoToFirstParentCommit.png", "GoToLastParentCommit.png", "GoToMergeBaseCommit.png", "NavigateBackward.png", "NavigateForward.png"];
        Assert.That(navigate.Items.OfType<MenuItem>().Take(10).Select(i => (i.Icon as Image)?.Tag), Is.EqualTo(icons));
        Assert.That(navigate.Items.OfType<MenuItem>().Take(10).All(i => i.Icon is Image { Source: not null, Width: 16, Height: 16 }), Is.True);
        foreach (string unavailable in new[] { "ShowSuperprojectTags", "ShowSuperprojectRemoteBranches", "ShowSuperprojectBranches" })
        { Assert.That(Item(view, "View" + unavailable).IsEnabled, Is.False); Assert.That(ToolTip.GetTip(Item(view, "View" + unavailable)), Is.Not.Null); }
        foreach (string available in new[] { "showBuildStatusIconToolStripMenuItem", "showBuildStatusTextToolStripMenuItem" })
        { Assert.That(Item(view, "View" + available).IsEnabled, Is.True); }
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            f.Window.RequestedThemeVariant = theme;
            foreach (MenuItem menu in new[] { navigate, view })
            {
                menu.IsSubMenuOpen = true; Pump(f.Window);
                Assert.That(menu.Items.OfType<MenuItem>().Select(i => i.Bounds.Height), Is.All.EqualTo(22));
                Assert.That(menu.Items.OfType<Separator>().Select(i => i.Bounds.Height), Is.All.EqualTo(6));
                using var bitmap = TopLevel.GetTopLevel(menu.Items.OfType<MenuItem>().First())!.CaptureRenderedFrame();
                string file = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"history-{menu.Name}-{theme}.png"); bitmap!.Save(file); TestContext.AddTestAttachment(file);
                menu.IsSubMenuOpen = false; Pump(f.Window);
            }
        }
    }

    [AvaloniaTest]
    public async Task NavigationUsesRealMergeParentsCommonAncestorAndHistoryWithoutLosingGridFocus()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        ListBox list = History(f.Window); GitCommit merge = list.Items.OfType<GitCommit>().Single(c => c.Parents.Count == 2);
        MenuItem menu = Root(f.Window, "NavigateMenu");
        list.SelectedItem = merge; await Idle(f.Window);
        Click(Item(menu, "NavigateGotoLastParentCommit")); await Idle(f.Window);
        Assert.That(((GitCommit)list.SelectedItem!).Hash, Is.EqualTo(merge.Parents[^1]));
        Assert.That(list.IsKeyboardFocusWithin, Is.True);
        f.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt); await Idle(f.Window);
        Assert.That(((GitCommit)list.SelectedItem!).Hash, Is.EqualTo(merge.Hash));
        f.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Control); await Idle(f.Window);
        Assert.That(((GitCommit)list.SelectedItem!).Hash, Is.EqualTo(merge.Parents[0]));
        Click(Item(menu, "NavigateGotoCurrentRevision")); await Idle(f.Window);
        Assert.That(((GitCommit)list.SelectedItem!).Hash, Is.EqualTo(merge.Hash));
        list.SelectedItems!.Clear();
        list.SelectedItems.Add(list.Items.OfType<GitCommit>().Single(c => c.Hash == merge.Parents[0]));
        list.SelectedItems.Add(list.Items.OfType<GitCommit>().Single(c => c.Hash == merge.Parents[1]));
        Click(Item(menu, "NavigateGotoMergeBaseCommit")); await Idle(f.Window);
        Assert.That(((GitCommit)list.SelectedItem!).Subject, Is.EqualTo("Initial commit"));
        Click(Item(menu, "NavigateGotoMergeBaseCommit")); await Idle(f.Window);
        Assert.That(((GitCommit)list.SelectedItem!).Subject, Is.EqualTo("Initial commit"), "A single selection compares with HEAD.");
    }

    [AvaloniaTest]
    public async Task ViewChangesActualRowsAndReferencesInPlaceAndPersistsOnlyWhenSaved()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        ListBox list = History(f.Window); object source = list.ItemsSource!;
        MenuItem view = Root(f.Window, "ViewMenu");
        list.Focus();
        Control Row() => list.GetVisualDescendants().OfType<Control>().First(c => c.Name == "RevisionHistoryRow" && c.Tag is GitCommit g && g.Hash == ((GitCommit)list.SelectedItem!).Hash);
        Control row = Row();
        Click(Item(view, "ViewshowTagsToolStripMenuItem")); Click(Item(view, "ViewShowRemoteBranches")); Pump(f.Window);
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(Row(), Is.SameAs(row));
        Assert.That(list.GetVisualDescendants().OfType<Border>().Where(c => c.Name == "RevisionReference").Select(c => (GitReference)c.Tag!).Any(r => r.IsTag || r.IsRemote), Is.False);
        Click(Item(view, "ViewshowAuthorAvatarColumnToolStripMenuItem")); Click(Item(view, "ViewshowAuthorNameColumnToolStripMenuItem"));
        Click(Item(view, "ViewshowDateColumnToolStripMenuItem")); Click(Item(view, "ViewshowIdColumnToolStripMenuItem"));
        Click(Item(view, "ViewshowRevisionGraphColumnToolStripMenuItem")); Click(Item(view, "ViewShowArtificialCommits")); Pump(f.Window);
        Assert.That(((Grid)row).ColumnDefinitions.Skip(1).Select(c => c.ActualWidth), Is.All.Zero);
        Assert.That(f.Window.GetVisualDescendants().OfType<CommitGraph>().Single().IsVisible, Is.False);
        Assert.That(f.Window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "WorkingRevisions").IsVisible, Is.False);
        Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Assert.That(new HistoryViewPreferencesStore().Load().ShowTags, Is.True, "Toggling a view does not overwrite defaults.");
        ContextMenu context = list.ContextMenu!; MenuItem other = context.Items.OfType<MenuItem>().Single(i => i.Name == "RevisionView");
        Assert.That(Item(other, "RevisionViewshowTagsToolStripMenuItem").IsChecked, Is.False);
        Click(Item(view, "ViewSaveAsDefault"));
        Assert.That(new HistoryViewPreferencesStore().Load().ShowTags, Is.False);
        MainWindow restored = new(); restored.Show();
        try
        {
            await restored.OpenRepositoryAsync(f.Repository); Pump(restored);
            Assert.That(Item(Root(restored, "ViewMenu"), "ViewshowTagsToolStripMenuItem").IsChecked, Is.False);
            Assert.That(restored.GetVisualDescendants().OfType<CommitGraph>().Single().IsVisible, Is.False);
        }
        finally { restored.Close(); }
    }

    [AvaloniaTest]
    public async Task CommitBodyNotesAndCommitterDateComeFromRealGitAndKeepCompactRows()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Helpers.Git(f, "commit", "--allow-empty", "--date=2001-01-01T01:02:03+00:00", "-m", "Subject", "-m", "Body first line\nBody second line");
        await Helpers.Git(f, "notes", "add", "-m", "Real Git note");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window); Pump(f.Window);
        MenuItem view = Root(f.Window, "ViewMenu");
        string Text(string name) => History(f.Window).GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RevisionHistoryRow" && c.Tag is GitCommit g && g.Subject == "Subject").GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == name).Text!;
        Assert.That(Text("RevisionMessageDetails"), Does.Contain("Body first line Body second line"));
        Click(Item(view, "ViewshowCommitMessageBodyToolStripMenuItem")); Assert.That(Text("RevisionMessageDetails"), Is.Empty);
        Click(Item(view, "ViewshowGitNotesToolStripMenuItem")); await Idle(f.Window); Pump(f.Window);
        Assert.That(Text("RevisionMessageDetails"), Does.Contain("Real Git note"));
        Click(Item(view, "ViewshowGitNotesColumnToolStripMenuItem")); await Idle(f.Window); Pump(f.Window);
        Assert.That(Text("RevisionNotes"), Is.EqualTo("Real Git note"));
        Assert.That(Text("RevisionMessageDetails"), Is.Empty);
        string authored = Text("RevisionDate");
        Click(Item(view, "ViewshowAuthorDateToolStripMenuItem")); await Idle(f.Window); Pump(f.Window);
        Assert.That(Text("RevisionDate"), Is.Not.EqualTo(authored));
        Assert.That(History(f.Window).GetVisualDescendants().OfType<ListBoxItem>().Select(c => c.Bounds.Height), Is.All.EqualTo(20));
    }

    [AvaloniaTest]
    public async Task SortAndReferenceInclusionMatchGitAndDoNotChangeRepositoryState()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "dirty\n", true);
        await Helpers.Git(f, "stash", "push", "-m", "Menu stash");
        string tree = (await f.Git.RunAsync(f.Repository, ["rev-parse", "HEAD^{tree}"])).StandardOutput.Trim();
        string checkpoint = (await f.Git.RunAsync(f.Repository, ["commit-tree", tree, "-m", "Isolated checkpoint"])).StandardOutput.Trim();
        await Helpers.Git(f, "update-ref", "refs/sessions/test", checkpoint);
        GitRepository repository = await GitRepository.OpenAsync(f.Repository);
        string stash = await repository.ResolveRevisionAsync("refs/stash");
        IReadOnlyList<GitCommit> hidden = await repository.GetLogAsync(new GitLogQuery { IncludeStashes = false, IncludeSessionRefs = false });
        Assert.That(hidden.Select(c => c.Hash), Does.Not.Contain(stash).And.Not.Contain(checkpoint));
        Assert.That((await repository.GetLogAsync(new GitLogQuery())).Select(c => c.Hash), Does.Contain(stash).And.Contain(checkpoint));
        foreach ((GitLogSort sort, string option) in new[] { (GitLogSort.Topology, "--topo-order"), (GitLogSort.AuthorDate, "--author-date-order"), (GitLogSort.Date, "--date-order") })
        {
            string[] expected = (await f.Git.RunAsync(f.Repository, ["log", option, "--format=%H", "--all", "--"])).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.That((await repository.GetLogAsync(new GitLogQuery { Sort = sort })).Select(c => c.Hash), Is.EqualTo(expected));
        }
        await f.Window.OpenRepositoryAsync(f.Repository); MenuItem view = Root(f.Window, "ViewMenu");
        Assert.That(History(f.Window).Items.OfType<GitCommit>().Select(c => c.Hash), Does.Not.Contain(checkpoint));
        Click(Item(view, "ViewShowSessionCheckpoints")); await Idle(f.Window);
        Assert.That(History(f.Window).Items.OfType<GitCommit>().Select(c => c.Hash), Does.Contain(checkpoint));
        Click(Item(view, "ViewShowStashes")); await Idle(f.Window);
        Assert.That(History(f.Window).Items.OfType<GitCommit>().Select(c => c.Hash), Does.Not.Contain(stash));
        Assert.That(await repository.ResolveRevisionAsync("refs/stash"), Is.EqualTo(stash));
        Assert.That(await repository.ResolveRevisionAsync("refs/sessions/test"), Is.EqualTo(checkpoint));
    }

    [AvaloniaTest]
    public async Task RealAltKeyReleaseAfterNavigationKeepsGridFocusAndPlainAltStillOpensTheMenu()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        ListBox list = History(f.Window); GitCommit head = (GitCommit)list.SelectedItem!;
        GitCommit parent = list.Items.OfType<GitCommit>().Single(c => c.Hash == head.Parents[0]);
        list.SelectedItem = parent; list.Focus(); await Idle(f.Window);
        Menu menu = f.Window.GetVisualDescendants().OfType<Menu>().Single();
        async Task Chord(PhysicalKey key)
        {
            f.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, "Alt");
            f.Window.KeyPressQwerty(key, RawInputModifiers.Alt); f.Window.KeyReleaseQwerty(key, RawInputModifiers.Alt);
            f.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, "Alt"); await Idle(f.Window);
            Assert.That(menu.IsOpen, Is.False); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        }
        await Chord(PhysicalKey.ArrowLeft); Assert.That(list.SelectedItem, Is.EqualTo(head));
        await Chord(PhysicalKey.ArrowRight); Assert.That(list.SelectedItem, Is.EqualTo(parent));
        int before = list.SelectedIndex;
        f.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); f.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); await Idle(f.Window);
        Assert.That(list.SelectedIndex, Is.EqualTo(before - 1));
        f.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, "Alt");
        f.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, "Alt"); Pump(f.Window);
        Assert.That(menu.IsOpen, Is.True, "Plain Alt retains the standard access-key behavior.");
        menu.Close();
    }

    [AvaloniaTest]
    public async Task ToolbarVisibilityDoesNotBringHiddenItemsBackThroughOverflow()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        MenuItem view = Root(f.Window, "ViewMenu"), toolbars = Item(view, "ViewToolbars");
        MenuItem filters = Item(toolbars, "ViewFilterToolbar");
        CompactBrowserToolbar toolbar = f.Window.GetVisualDescendants().OfType<CompactBrowserToolbar>().Single(toolbar => toolbar.Name == "RepositoryToolbar");
        view.IsSubMenuOpen = true; toolbars.IsSubMenuOpen = true; filters.IsSubMenuOpen = true; Pump(f.Window);
        MenuItem quick = Item(filters, "ToolbarHistoryToolbarQuickFilter"); Click(quick);
        toolbars.IsSubMenuOpen = false; view.IsSubMenuOpen = false; Pump(f.Window);
        Assert.That(toolbar.InlineItems.Any(c => c.Name == "HistoryToolbarQuickFilter"), Is.False);
        f.Window.Width = 700; Pump(f.Window);
        Assert.That(toolbar.OverflowFlyout.Items.OfType<MenuItem>().Any(c => c.Name == "OverflowQuickFilter"), Is.False);
        f.Window.Width = 1440; Pump(f.Window);
        Click(filters); Pump(f.Window);
        Assert.That(toolbar.InlineItems.Any(c => c.Name == "HistoryToolbarBranches"), Is.False);
        Click(filters); Click(quick); Pump(f.Window);
        Assert.That(toolbar.InlineItems.Any(c => c.Name == "HistoryToolbarQuickFilter"), Is.True);
    }

    private static ListBox History(MainWindow window) => window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "RevisionHistory");
    private static MenuItem Root(MainWindow window, string name) => window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(c => c.Name == name);
    private static MenuItem Item(MenuItem menu, string name) => menu.Items.OfType<MenuItem>().Single(c => c.Name == name);
    private static string Caption(object item) => item is Separator ? "|" : ((MenuItem)item).Header is AccessText text ? text.Text!.Replace("_", "", StringComparison.Ordinal) : ((MenuItem)item).Header?.ToString() ?? "";
    private static void Click(MenuItem item) { item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
    private static async Task Idle(MainWindow window) { for (int i = 0; i < 500 && window.IsBusy; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); } Assert.That(window.IsBusy, Is.False); Pump(window); }
}
