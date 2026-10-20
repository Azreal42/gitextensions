using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class LeftPanelSelectedRefsTests
{
    private const string Local = "refs/heads/topic/shared", Tag = "refs/tags/topic/shared";
    private const string Remote = "refs/remotes/Team/Upstream/topic/shared";
    private const string Literal = "refs/heads/topic/λ;$()'quoted";

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task UnderlinedHomonymsNavigateWithoutReloadAndOnlyTheExplicitMenuFiltersFullReferences(string language, bool dark)
    {
        await using Scope f = await Scope.CreateAsync(language, dark); MainWindow main = f.Main;
        await f.OpenTagsAsync(); object? source = f.History.ItemsSource; object[] rows = f.History.Items.Cast<object>().ToArray();
        string? expression = Field<ComboBox>(main, "BranchHistoryFilter").Text;
        GitCommandLog.Clear(); f.Click(Leaf(main, Local)); await f.IdleAsync();
        Assert.That(((GitCommit)f.History.SelectedItem!).Hash, Is.EqualTo(f.First));
        f.Click(Leaf(main, Remote), RawInputModifiers.Control); await f.IdleAsync();
        f.Click(Leaf(main, Tag), RawInputModifiers.Control); await f.IdleAsync();
        AssertMarks(main, Local, Remote, Tag);
        Assert.That(((GitCommit)f.History.SelectedItem!).Hash, Is.EqualTo(f.Middle));
        Assert.That(f.Tree.SelectedItem, Is.SameAs(Leaf(main, Tag)));
        Assert.That(Leaf(main, Tag).IsKeyboardFocusWithin, Is.True);
        AssertUnchangedHistory(f, source, rows, expression);
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command is "git log" or "git status"), Is.False);

        ContextMenu menu = await f.ContextAsync(Leaf(main, Local));
        AssertMarks(main, Local, Remote, Tag);
        MenuItem action = Item(menu, "filterForSelectedRefsMenuItem");
        Assert.That(action.Header, Is.EqualTo(language == "fr" ? "_Filtrer sur la sélection" : "_Filter for selected"));
        Assert.That(action.Icon, Is.TypeOf<Image>()); Assert.That(((Image)action.Icon!).Source, Is.Not.Null);
        Assert.That(ToolTip.GetTip(action)?.ToString(), Does.Contain(language == "fr" ? "soulignées" : "underlined"));
        Assert.That(action.IsEnabled, Is.True);
        Assert.That(Item(menu, "GitRefsSortByContextMenuItem").IsEnabled, Is.False, "Windows single-node actions stay disabled for an underlined multiset.");
        AssertUnchangedHistory(f, source, rows, expression);
        PushProcessTestDriver.Pointer(action); await f.IdleAsync();
        Assert.That((Field<ComboBox>(main, "BranchHistoryFilter").Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { Local, Remote, Tag }));
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { f.First, f.Middle }));
        Assert.That(f.History.ItemsSource, Is.Not.SameAs(source)); AssertMarks(main, Local, Remote, Tag);
        PushProcessTestDriver.Pointer(Menu(main, "ViewMenu")); Pump(main);
        PushProcessTestDriver.Pointer(Menu(main, "ViewMenu").Items.OfType<MenuItem>().Single(item => item.Name == "ViewShowAllBranches"));
        await f.IdleAsync();
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { f.First, f.Middle, f.Last }));
        AssertMarks(main, Local, Remote, Tag);
        await f.AssertGitIntactAsync();
    }

    [AvaloniaTest]
    public async Task ExplicitReferenceFilteringComposesWithTheExistingMessageFilter()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main; await f.OpenTagsAsync();
        PushProcessTestDriver.Pointer(Field<Button>(main, "BrowseAdvancedFilter"));
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Field<CheckBox>(dialog, "MessageCheck").IsChecked = true;
        Edit(dialog, Field<TextBox>(dialog, "Message"), "Middle selected-reference commit");
        PushProcessTestDriver.Pointer(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK"));
        await PushProcessTestDriver.UntilAsync(main, () => !dialog.IsVisible); await f.IdleAsync();
        object? source = f.History.ItemsSource;
        f.Click(Leaf(main, Local)); f.Click(Leaf(main, Tag), RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.History.ItemsSource, Is.SameAs(source));
        ContextMenu menu = await f.ContextAsync(Leaf(main, Local));
        PushProcessTestDriver.Pointer(Item(menu, "filterForSelectedRefsMenuItem")); await f.IdleAsync();
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { f.Middle }));
        Assert.That((Field<ComboBox>(main, "BranchHistoryFilter").Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { Local, Tag }));
        PushProcessTestDriver.Pointer(Field<Button>(main, "BrowseAdvancedFilter"));
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(dialog.ReadQuery().Message, Is.EqualTo("Middle selected-reference commit"));
        PushProcessTestDriver.Pointer(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Cancel"));
        await f.IdleAsync(); await f.AssertGitIntactAsync();
    }

    [AvaloniaTest]
    public async Task CtrlShiftIncludesLogicalDescendantsHiddenBySearchWithoutClearingOtherMarks()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        f.Click(Leaf(main, "refs/heads/outside")); await f.IdleAsync();
        Edit(main, Field<TextBox>(main, "RepositoryNavigationSearch"), "shared");
        Assert.That(Nodes(main).OfType<TreeViewItem>().Any(node => node.Tag is GitReference reference && reference.Name == Literal), Is.False);
        TreeViewItem folder = TopicFolder(main); f.Click(folder, RawInputModifiers.Control | RawInputModifiers.Shift); await f.IdleAsync();
        Edit(main, Field<TextBox>(main, "RepositoryNavigationSearch"), "");
        AssertMarks(main, "refs/heads/outside", Local, "refs/heads/topic/other", Literal);
        Assert.That(Label(TopicFolder(main)).TextDecorations, Is.EqualTo(TextDecorations.Underline));
        ContextMenu menu = await f.ContextAsync(TopicFolder(main));
        PushProcessTestDriver.Pointer(Item(menu, "filterForSelectedRefsMenuItem")); await f.IdleAsync();
        Assert.That((Field<ComboBox>(main, "BranchHistoryFilter").Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Is.EquivalentTo(new[] { "refs/heads/outside", Local, "refs/heads/topic/other", Literal }));
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { f.First }));
        await f.AssertGitIntactAsync();
    }

    [AvaloniaTest]
    public async Task CtrlSpaceAndCtrlShiftSpaceToggleMarksWhileArrowsOnlyMoveTheNativeCursor()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        TreeViewItem folder = TopicFolder(main); f.Click(folder); await f.IdleAsync();
        object? source = f.History.ItemsSource; object[] rows = f.History.Items.Cast<object>().ToArray();
        string? expression = Field<ComboBox>(main, "BranchHistoryFilter").Text;
        AssertMarks(main); Assert.That(Label(folder).TextDecorations, Is.EqualTo(TextDecorations.Underline));
        Press(main, PhysicalKey.Space, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(Label(folder).TextDecorations, Is.Null.Or.Empty); Assert.That(f.Tree.SelectedItem, Is.SameAs(folder));
        Assert.That(folder.IsKeyboardFocusWithin, Is.True);
        Press(main, PhysicalKey.Space, RawInputModifiers.Control | RawInputModifiers.Shift); await f.IdleAsync();
        AssertMarks(main, Local, "refs/heads/topic/other", Literal);
        Press(main, PhysicalKey.ArrowDown); await f.IdleAsync();
        Assert.That(f.Tree.SelectedItem, Is.Not.SameAs(folder)); Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True);
        AssertMarks(main, Local, "refs/heads/topic/other", Literal);
        AssertUnchangedHistory(f, source, rows, expression);
        f.Click(TopicFolder(main), RawInputModifiers.Control | RawInputModifiers.Shift); await f.IdleAsync();
        AssertMarks(main); Assert.That(Label(TopicFolder(main)).TextDecorations, Is.Null.Or.Empty);
        f.Click(TopicFolder(main), RawInputModifiers.Shift); await f.IdleAsync();
        Assert.That(Label(TopicFolder(main)).TextDecorations, Is.EqualTo(TextDecorations.Underline)); AssertMarks(main);
        ContextMenu menu = await f.ContextAsync(TopicFolder(main));
        Assert.That(Item(menu, "filterForSelectedRefsMenuItem").IsEnabled, Is.False);
        menu.Close(); await f.AssertGitIntactAsync();
    }

    [AvaloniaTest]
    public async Task CtrlReclickKeepsThePrimaryRowAndRightClickOnAnUnmarkedRowStartsOneSelection()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        TreeViewItem local = Leaf(main, Local);
        int doubleTaps = 0;
        local.AddHandler(InputElement.DoubleTappedEvent, (_, _) => doubleTaps++, RoutingStrategies.Bubble, handledEventsToo: true);
        f.Click(local); await f.IdleAsync();
        await f.WaitForIndependentMouseClickAsync();
        f.Click(local, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(doubleTaps, Is.Zero, "These independent single clicks must not invoke the separate Windows double-click checkout gesture.");
        AssertMarks(main); Assert.That(f.Tree.SelectedItem, Is.SameAs(local)); Assert.That(local.IsKeyboardFocusWithin, Is.True);
        await f.WaitForIndependentMouseClickAsync();
        f.Click(local, RawInputModifiers.Control); f.Click(Leaf(main, Literal), RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(doubleTaps, Is.Zero);
        AssertMarks(main, Local, Literal);
        object selected = f.History.SelectedItem!; object? source = f.History.ItemsSource;
        ContextMenu menu = await f.ContextAsync(Leaf(main, "refs/heads/main"));
        AssertMarks(main, "refs/heads/main"); Assert.That(f.History.SelectedItem, Is.SameAs(selected));
        Assert.That(f.History.ItemsSource, Is.SameAs(source));
        Assert.That(Item(menu, "filterForSelectedRefsMenuItem").IsEnabled, Is.True);
        Assert.That(Item(menu, "GitRefsSortByContextMenuItem").IsEnabled, Is.True);
        Assert.That(Label(Leaf(main, "refs/heads/main")).FontWeight, Is.EqualTo(FontWeight.Bold), "Underline must preserve the current-branch weight.");
        menu.Close(); await f.AssertGitIntactAsync();
    }

    [AvaloniaTest]
    public async Task SettingsSortSearchAndActualRefreshPreserveMarksByCompleteRefIdentity()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        f.Click(Leaf(main, Local)); f.Click(Leaf(main, Literal), RawInputModifiers.Control); await f.IdleAsync();
        Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        SettingsNavigationTestDriver.Select(settings, "Sorting");
        Field<ComboBox>(settings, "_NO_TRANSLATE_cmbBranchesSortBy").SelectedItem = GitRefsSortBy.VersionRefName;
        Field<ComboBox>(settings, "_NO_TRANSLATE_cmbBranchesOrder").SelectedItem = GitRefsSortOrder.Descending;
        PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(main, () => !settings.IsVisible); await f.IdleAsync();
        Assert.That(new PreferencesStore().Load().ReferenceSorting, Is.EqualTo(new GitReferenceSorting(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Descending)));
        AssertMarks(main, Local, Literal);
        Edit(main, Field<TextBox>(main, "RepositoryNavigationSearch"), "λ;$()'quoted");
        Assert.That(Nodes(main).Where(node => node.Tag is GitReference).Select(node => ((GitReference)node.Tag!).Name), Is.EqualTo(new[] { Literal }));
        ContextMenu menu = await f.ContextAsync(Leaf(main, Literal));
        Assert.That(Item(menu, "GitRefsSortByContextMenuItem").IsEnabled, Is.False, "A marked ref hidden by search still belongs to the set.");
        menu.Close(); Edit(main, Field<TextBox>(main, "RepositoryNavigationSearch"), ""); AssertMarks(main, Local, Literal);
        Press(main, PhysicalKey.F5); await f.IdleAsync(); AssertMarks(main, Local, Literal);
        await f.AssertGitIntactAsync();
    }

    [AvaloniaTest]
    [TestCase("remove")]
    [TestCase("repository")]
    public async Task RemovedRefsAndRepositoryChangesInvalidateTheCapturedMenuWithoutFallback(string change)
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        string[] originalRefs = (await f.Git.GitAsync("show-ref")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        byte[] originalIndex = File.ReadAllBytes(f.Git.PathInRepository(".git/index"));
        f.Click(Leaf(main, Local)); f.Click(Leaf(main, Literal), RawInputModifiers.Control); await f.IdleAsync();
        ContextMenu menu = await f.ContextAsync(Leaf(main, Local)); MenuItem stale = Item(menu, "filterForSelectedRefsMenuItem");
        menu.Close(); GitFixture? other = null;
        try
        {
            if (change == "remove")
            {
                await f.Git.GitAsync("branch", "-D", Literal[11..]); Press(main, PhysicalKey.F5); await f.IdleAsync();
                AssertMarks(main, Local);
                Assert.That(Nodes(main).Any(node => node.Tag is GitReference reference && reference.Name == Literal), Is.False);
            }
            else
            {
                other = await GitFixture.CreateAsync(); await other.ChangeAsync("replacement\n", "Replacement selection repository");
                await other.GitAsync("branch", Local[11..]);
                await main.OpenRepositoryAsync(other.Repository.Path); await f.IdleAsync();
                AssertMarks(main); Assert.That(f.Tree.SelectedItem, Is.Null);
            }
            object? source = f.History.ItemsSource; object[] rows = f.History.Items.Cast<object>().ToArray();
            string? expression = Field<ComboBox>(main, "BranchHistoryFilter").Text;
            // Deliver an obsolete captured command after the real native change.
            // Only this stale-event probe is injected; all normal commands above
            // use actual pointer/keyboard input with strict hit testing.
            stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await f.IdleAsync();
            AssertUnchangedHistory(f, source, rows, expression);
            Assert.That(Field<ComboBox>(main, "BranchHistoryFilter").Text ?? "", Is.Empty);
            Assert.That((await f.Git.GitAsync("show-ref")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                Is.EquivalentTo(change == "remove" ? originalRefs.Where(line => !line.EndsWith(" " + Literal, StringComparison.Ordinal)) : originalRefs));
            Assert.That(File.ReadAllBytes(f.Git.PathInRepository(".git/index")), Is.EqualTo(originalIndex));
            Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Last));
            menu = await f.ContextAsync(Leaf(main, Local));
            Assert.That(Item(menu, "filterForSelectedRefsMenuItem").IsEnabled, Is.True);
            menu.Close();
        }
        finally { await f.CloseAsync(); other?.Dispose(); }
    }

    [AvaloniaTest]
    public async Task GridSelectInLeftPanelReplacesPreviousMarksWithItsSingleRefBeforeExplicitFiltering()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        f.Click(Leaf(main, Local)); f.Click(Leaf(main, Literal), RawInputModifiers.Control); await f.IdleAsync();
        AssertMarks(main, Local, Literal);
        object? source = f.History.ItemsSource; object[] rows = f.History.Items.Cast<object>().ToArray();
        string? expression = Field<ComboBox>(main, "BranchHistoryFilter").Text;
        GitCommit targetCommit = f.History.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Last);
        ClickHistorySubject(f, targetCommit, MouseButton.Left); await f.IdleAsync();
        Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.Last));
        AssertMarks(main, Local, Literal);
        ClickHistorySubject(f, targetCommit, MouseButton.Right);
        ContextMenu gridMenu = f.History.ContextMenu!;
        await PushProcessTestDriver.UntilAsync(main, () => gridMenu.IsOpen);
        MenuItem reveal = Item(gridMenu, "RevisionSelectInLeftPanel");
        Assert.That(reveal.IsEnabled, Is.True); Assert.That(reveal.Items, Is.Empty, "The final commit has only the real main ref.");
        Assert.That((reveal.Tag as GitReference)?.Name, Is.EqualTo("refs/heads/main"));
        PushProcessTestDriver.Pointer(reveal);
        await PushProcessTestDriver.UntilAsync(main, () => !gridMenu.IsOpen); await f.IdleAsync();
        AssertMarks(main, "refs/heads/main");
        TreeViewItem treeTarget = Leaf(main, "refs/heads/main");
        Assert.That(f.Tree.SelectedItem, Is.SameAs(treeTarget)); Assert.That(treeTarget.IsKeyboardFocusWithin, Is.True);
        Assert.That(Label(treeTarget).FontWeight, Is.EqualTo(FontWeight.Bold), "The current-ref weight remains cumulative with underline.");
        AssertUnchangedHistory(f, source, rows, expression); Assert.That(f.History.SelectedItem, Is.SameAs(targetCommit));
        Press(main, PhysicalKey.ArrowLeft); await f.IdleAsync();
        Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True); Assert.That(f.Tree.SelectedItem, Is.Not.SameAs(treeTarget));
        AssertMarks(main, "refs/heads/main"); AssertUnchangedHistory(f, source, rows, expression);
        ContextMenu treeMenu = await f.ContextAsync(Leaf(main, "refs/heads/main"));
        Assert.That(Item(treeMenu, "GitRefsSortByContextMenuItem").IsEnabled, Is.True, "The source single-selection actions must recover after explicit reveal.");
        PushProcessTestDriver.Pointer(Item(treeMenu, "filterForSelectedRefsMenuItem")); await f.IdleAsync();
        Assert.That(Field<ComboBox>(main, "BranchHistoryFilter").Text, Is.EqualTo("refs/heads/main"));
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { f.First, f.Middle, f.Last }));
        AssertMarks(main, "refs/heads/main"); await f.AssertGitIntactAsync();
    }

    private static void ClickHistorySubject(Scope f, GitCommit commit, MouseButton button)
    {
        f.History.ScrollIntoView(commit);
        for (int frame = 0; frame < 10; frame++) { Pump(f.Main); }
        ListBoxItem row = f.History.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(item => item.DataContext is GitCommit candidate && candidate.Hash == commit.Hash);
        Control subject = row.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "RevisionSubject");
        Point point = subject.TranslatePoint(new Point(subject.Bounds.Width / 2, subject.Bounds.Height / 2), f.Main)!.Value;
        Assert.That(subject.IsEffectivelyVisible && subject.IsEffectivelyEnabled && subject.Bounds.Width > 0 && subject.Bounds.Height > 0
            && new Rect(default, f.Main.ClientSize).Contains(point)
            && (f.Main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(subject) == true,
            Is.True, "Real commit-subject target: " + commit.Hash);
        f.Main.MouseMove(point); f.Main.MouseDown(point, button); f.Main.MouseUp(point, button); Pump(f.Main);
    }

    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static IEnumerable<TreeViewItem> Nodes(MainWindow main) => BrowseTestHelpers.NavigationNodes(Field<TreeView>(main, "RepositoryNavigation"));
    private static TreeViewItem Leaf(MainWindow main, string reference) => Nodes(main).Single(node => node.Tag is GitReference value && value.Name == reference);
    private static TextBlock Label(TreeViewItem node) => ((StackPanel)node.Header!).Children.OfType<TextBlock>().Single();
    private static TreeViewItem TopicFolder(MainWindow main)
        => Field<TreeView>(main, "RepositoryNavigation").Items.OfType<TreeViewItem>()
            .Single(root => Subtree(root).Any(node => node.Tag is GitReference { IsBranch: true }))
            .Items.OfType<TreeViewItem>().Single(node => node.Tag is not GitReference && Label(node).Text == "topic");
    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static MenuItem Menu(MainWindow main, string name) => main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static IEnumerable<TreeViewItem> Subtree(TreeViewItem root)
    {
        yield return root;
        foreach (TreeViewItem child in root.Items.OfType<TreeViewItem>())
        { foreach (TreeViewItem node in Subtree(child)) { yield return node; } }
    }
    private static void AssertMarks(MainWindow main, params string[] expected)
        => Assert.That(Nodes(main).Where(node => node.Tag is GitReference && Label(node).TextDecorations?.Count > 0)
            .Select(node => ((GitReference)node.Tag!).Name), Is.EquivalentTo(expected));
    private static void AssertUnchangedHistory(Scope f, object? source, object[] rows, string? expression)
    {
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(f.History.Items.Count, Is.EqualTo(rows.Length));
        Assert.That(f.History.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        Assert.That(Field<ComboBox>(f.Main, "BranchHistoryFilter").Text, Is.EqualTo(expression));
    }
    private static void Edit(Window main, TextBox input, string text)
    { PushProcessTestDriver.Pointer(input); Press(main, PhysicalKey.A, RawInputModifiers.Control); if (text.Length == 0) { Press(main, PhysicalKey.Backspace); } else { main.KeyTextInput(text); } Pump(main); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

    private sealed class Scope(GitFixture git, string first, string middle, string last) : IAsyncDisposable
    {
        internal GitFixture Git { get; } = git;
        internal MainWindow Main { get; private set; } = null!;
        internal string First { get; } = first;
        internal string Middle { get; } = middle;
        internal string Last { get; } = last;
        internal ListBox History => Field<ListBox>(Main, "RevisionHistory");
        internal TreeView Tree => Field<TreeView>(Main, "RepositoryNavigation");
        private string _refs = "";
        private byte[] _index = [];
        internal static async Task<Scope> CreateAsync(string language = "en", bool dark = false)
        {
            GitFixture git = await GitFixture.CreateAsync(); Scope? f = null;
            try
            {
                git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
                string first = await git.Repository.ResolveRevisionAsync("HEAD");
                foreach (string name in new[] { Local[11..], "topic/other", Literal[11..], "outside" }) { await git.GitAsync("branch", name, first); }
                await git.ChangeAsync("middle\n", "Middle selected-reference commit");
                string middle = await git.Repository.ResolveRevisionAsync("HEAD");
                await git.GitAsync("tag", Tag[10..], middle);
                await git.GitAsync("config", "remote.Team/Upstream.url", git.Repository.Path);
                await git.GitAsync("update-ref", Remote, middle);
                await git.ChangeAsync("latest\n", "Latest selected-reference commit");
                new PreferencesStore().Save(new() { ShowRepoCurrentBranch = false, CheckForUpdates = false });
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                f = new(git, first, middle, await git.Repository.ResolveRevisionAsync("HEAD"));
                f.Main = new() { Width = 1440, Height = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
                f.Main.Show(); await f.Main.OpenRepositoryAsync(git.Repository.Path); await f.IdleAsync();
                f._refs = (await git.GitAsync("show-ref")).StandardOutput;
                f._index = File.ReadAllBytes(git.PathInRepository(".git/index"));
                return f;
            }
            catch { if (f?.Main is not null) { await f.CloseAsync(); } git.Dispose(); throw; }
        }
        internal async Task IdleAsync()
        {
            Pump(Main); await PushProcessTestDriver.UntilAsync(Main, () => !Main.IsBusy && Main.WindowWorkCompletion.IsCompleted);
            await Main.WindowWorkCompletion; Pump(Main); Assert.That(Main.LastException, Is.Null, Main.LastException?.ToString());
        }
        internal async Task WaitForIndependentMouseClickAsync()
        {
            TimeSpan doubleTap = Main.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse)
                ?? throw new InvalidOperationException("The actual platform double-tap interval is unavailable.");
            if (doubleTap < TimeSpan.Zero) { throw new InvalidOperationException("The platform double-tap interval is invalid."); }
            // Keep distinct single-click gestures beyond the actual platform interval,
            // including its fractional millisecond; do not assume a fixed 500ms threshold.
            long milliseconds = checked((long)Math.Ceiling(doubleTap.TotalMilliseconds) + 1);
            Stopwatch elapsed = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds));
            while (elapsed.Elapsed <= doubleTap) { await Task.Yield(); }
            Pump(Main);
        }
        internal void Click(TreeViewItem node, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
        {
            TextBlock label = Label(node); node.BringIntoView();
            for (int frame = 0; frame < 10; frame++) { Pump(Main); }
            Point point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), Main)!.Value;
            Assert.That(label.IsEffectivelyVisible && node.IsEffectivelyEnabled && new Rect(default, Main.ClientSize).Contains(point)
                && (Main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(label) == true,
                Is.True, "Real navigation header target: " + label.Text);
            Main.MouseMove(point); Main.MouseDown(point, button, modifiers); Main.MouseUp(point, button, modifiers); Pump(Main);
        }
        internal async Task<ContextMenu> ContextAsync(TreeViewItem node)
        { Click(node, button: MouseButton.Right); await PushProcessTestDriver.UntilAsync(Main, () => Tree.ContextMenu!.IsOpen); return Tree.ContextMenu!; }
        internal async Task OpenTagsAsync()
        {
            TreeViewItem tags = Tree.Items.OfType<TreeViewItem>().Single(root => Subtree(root).Any(node => node.Tag is GitReference { IsTag: true }));
            Click(tags); Press(Main, PhysicalKey.ArrowRight); await IdleAsync(); Assert.That(tags.IsExpanded, Is.True);
        }
        internal async Task AssertGitIntactAsync()
        {
            Assert.That((await Git.GitAsync("show-ref")).StandardOutput, Is.EqualTo(_refs));
            Assert.That(File.ReadAllBytes(Git.PathInRepository(".git/index")), Is.EqualTo(_index));
            Assert.That(await Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(Last));
        }
        internal async Task CloseAsync()
        {
            Tree.ContextMenu?.Close(); foreach (Window child in Main.OwnedWindows.ToArray()) { child.Close(); }
            Main.Close(); await PushProcessTestDriver.UntilAsync(Main, () => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted);
            await Main.WindowWorkCompletion;
        }
        public async ValueTask DisposeAsync()
        { try { await CloseAsync(); } finally { Git.Dispose(); } }
    }
}
