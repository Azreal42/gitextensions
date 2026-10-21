using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class LeftPanelReferenceNavigationTests
{
    [AvaloniaTest]
    [TestCase("refs/heads/topic")]
    [TestCase("refs/remotes/origin/topic")]
    [TestCase("refs/tags/annotated-topic")]
    public async Task ARealSingleClickSelectsTheReferencedCommitWithoutReplacingHistoryFilteringOrTreeFocus(string reference)
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory"); TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
        object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray();
        string? filter = Field<ComboBox>(main, "BranchHistoryFilter").Text;
        string refs = (await f.Git.GitAsync("show-ref")).StandardOutput;
        byte[] index = File.ReadAllBytes(f.Git.PathInRepository(".git/index"));
        if (reference.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            TreeViewItem tags = tree.Items.OfType<TreeViewItem>().Single(item => Caption(item) == "Tags");
            PushProcessTestDriver.Pointer(Label(tags));
            PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowRight); await Idle(main);
            Assert.That(tags.IsExpanded, Is.True, "The tag leaf must be opened through the native tree gesture before clicking it.");
        }
        TreeViewItem node = Reference(main, reference); GitCommandLog.Clear();
        PushProcessTestDriver.Pointer(Label(node));
        Assert.That(tree.IsEffectivelyEnabled && node.IsEffectivelyEnabled && history.IsEffectivelyEnabled, Is.True,
            "The first click must leave the same row ready for a native second click.");
        await Idle(main);
        Assert.That(tree.SelectedItem, Is.SameAs(node)); Assert.That(node.IsKeyboardFocusWithin, Is.True);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.First));
        Assert.That(history.ItemsSource, Is.SameAs(source));
        Assert.That(history.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        Assert.That(history.Items.Count, Is.EqualTo(rows.Length));
        Assert.That(Field<ComboBox>(main, "BranchHistoryFilter").Text, Is.EqualTo(filter));
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command is "git log" or "git status"), Is.False,
            "Tree navigation is a cancellable ref read, not a global repository refresh.");
        Assert.That((await f.Git.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
        Assert.That(File.ReadAllBytes(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Last));
    }

    [AvaloniaTest]
    public Task AFilteredOutReferenceReportsItsAbsenceWithoutChangingRowsFilterSelectionOrTreeFocus()
        => FilteredOutReferenceAsync(RawInputModifiers.None);

    [AvaloniaTest]
    public Task ACtrlFilteredOutReferencePreservesTheWholeSelectionAndDoesNotOpenAModalOrChangeFilters()
        => FilteredOutReferenceAsync(RawInputModifiers.Control);

    private static async Task FilteredOutReferenceAsync(RawInputModifiers modifiers)
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        PushProcessTestDriver.Pointer(Field<Button>(main, "BrowseAdvancedFilter"));
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog filter = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Field<CheckBox>(filter, "MessageCheck").IsChecked = true;
        Field<TextBox>(filter, "Message").Text = "Latest navigation commit";
        PushProcessTestDriver.Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK"));
        await PushProcessTestDriver.UntilAsync(main, () => !filter.IsVisible && !main.IsBusy);
        ListBox history = Field<ListBox>(main, "RevisionHistory"); object? source = history.ItemsSource;
        object selected = history.SelectedItem!; string? expression = Field<ComboBox>(main, "BranchHistoryFilter").Text;
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { f.Last }));
        TreeViewItem node = Reference(main, "refs/heads/topic"); ReferencePointer(main, node, modifiers); await Idle(main);
        Assert.That(main.OwnedWindows.Any(window => window.IsVisible), Is.False);
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { f.Last }));
        Assert.That(Field<ComboBox>(main, "BranchHistoryFilter").Text, Is.EqualTo(expression));
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.Contain("not visible").And.Contain(f.First));
        Assert.That(node.IsKeyboardFocusWithin && node.IsEffectivelyEnabled, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(main, "BrowseAdvancedFilter"));
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        filter = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(filter.ReadQuery().Message, Is.EqualTo("Latest navigation commit"));
        PushProcessTestDriver.Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Cancel"));
        await Idle(main);
    }

    [AvaloniaTest]
    [TestCase("selection")]
    [TestCase("repository")]
    [TestCase("grid")]
    [TestCase("close")]
    public Task ABlockedRealRefReadRemainsInteractiveAndIsCancelledAndDrainedBeforeAnyStalePublication(string supersedingAction)
        => BlockedRealRefReadAsync(supersedingAction, RawInputModifiers.None);

    [AvaloniaTest]
    [TestCase("selection")]
    [TestCase("repository")]
    [TestCase("grid")]
    [TestCase("close")]
    public Task ABlockedCtrlRefReadCapturesItsModifierAndCannotPublishAfterASupersedingRealGesture(string supersedingAction)
        => BlockedRealRefReadAsync(supersedingAction, RawInputModifiers.Control);

    [AvaloniaTest]
    public Task ACtrlModifierCapturedBeforeTheAwaitStillAddsAfterThePointerHasBeenReleased()
        => BlockedRealRefReadAsync("release", RawInputModifiers.Control);

    private static async Task BlockedRealRefReadAsync(string supersedingAction, RawInputModifiers modifiers)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private native process gate requires Linux procfs."); return; }
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory"); object? source = history.ItemsSource; object selected = history.SelectedItem!;
        TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
        using ResolutionGate gate = new(f.Git); GitFixture? other = null;
        try
        {
            ReferencePointer(main, Reference(main, "refs/heads/topic"), modifiers);
            await PushProcessTestDriver.UntilAsync(main, () => gate.HasStarted);
            Assert.That(tree.IsEffectivelyEnabled && history.IsEffectivelyEnabled, Is.True);
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            if (supersedingAction == "selection")
            {
                TreeViewItem branches = tree.Items.OfType<TreeViewItem>().Single(node => Caption(node) == "Branches");
                PushProcessTestDriver.Pointer(Label(branches));
                await PushProcessTestDriver.UntilAsync(main, () => gate.Retired && !main.IsBusy);
                Assert.That(tree.SelectedItem, Is.SameAs(branches)); Assert.That(branches.IsKeyboardFocusWithin, Is.True);
                Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            }
            else if (supersedingAction == "grid")
            {
                GitCommit newer = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Middle navigation commit");
                PushProcessTestDriver.Pointer((Control)history.ContainerFromItem(newer)!);
                Assert.That(history.SelectedItem, Is.SameAs(newer));
                gate.Release(); await Idle(main);
                Assert.That(history.SelectedItem, Is.SameAs(newer), "A newer real grid click wins over the older in-flight ref resolution.");
                Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(history.ItemsSource, Is.SameAs(source));
            }
            else if (supersedingAction == "release")
            {
                Assert.That(Private(main, "_leftPanelReferenceModifiers"), Is.EqualTo(KeyModifiers.None),
                    "The real PointerReleased already cleared live modifier state while rev-parse is blocked.");
                gate.Release(); await Idle(main);
                GitCommit target = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.First);
                AssertGridSelection(main, [target, (GitCommit)selected], (GitCommit)selected);
                Assert.That(Reference(main, "refs/heads/topic").IsKeyboardFocusWithin, Is.True);
            }
            else if (supersedingAction == "repository")
            {
                other = await GitFixture.CreateAsync(); await other.ChangeAsync("replacement\n", "Replacement repository commit");
                string replacement = await other.Repository.ResolveRevisionAsync("HEAD");
                await main.OpenRepositoryAsync(other.Repository.Path); await Idle(main);
                await PushProcessTestDriver.UntilAsync(main, () => gate.Retired);
                Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(replacement));
                Assert.That(tree.SelectedItem, Is.Null); Assert.That(history.ItemsSource, Is.Not.SameAs(source));
            }
            else
            {
                main.Close();
                await PushProcessTestDriver.UntilAsync(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted && gate.Retired);
                await main.WindowWorkCompletion;
                Assert.That(history.SelectedItem, Is.SameAs(selected));
            }
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            Assert.That(gate.Retired, Is.True, "A matching PID/start-time still present in procfs, including Z, has not drained.");
        }
        finally
        {
            gate.Release(); await f.CloseAsync(); other?.Dispose();
        }
    }


    [AvaloniaTest]
    [TestCase("refs/heads/topic-alias")]
    [TestCase("refs/remotes/origin/topic")]
    [TestCase("refs/tags/annotated-topic")]
    public async Task CtrlReferencesToggleRecentFirstSelectionWithoutMovingTheMultiSelectionCaretOrFiltering(string sameFirstReference)
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory"); TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
        if (sameFirstReference.StartsWith("refs/tags/", StringComparison.Ordinal)) { await OpenTagsAsync(main); }
        GitCommit first = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.First);
        GitCommit middle = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Middle navigation commit");
        GitCommit last = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Last);
        object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray();
        string? filter = Field<ComboBox>(main, "BranchHistoryFilter").Text;
        string refs = (await f.Git.GitAsync("show-ref")).StandardOutput;
        byte[] index = File.ReadAllBytes(f.Git.PathInRepository(".git/index"));
        ReferencePointer(main, Reference(main, "refs/heads/topic")); await Idle(main);
        AssertGridSelection(main, [first], first);
        GitCommandLog.Clear();
        ReferencePointer(main, Reference(main, "refs/heads/middle"), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [middle, first], first);
        // A distinct node with an already selected SHA removes that SHA. The
        // modifier must still apply after the real async rev-parse has finished.
        ReferencePointer(main, Reference(main, sameFirstReference), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [middle], middle);
        ReferencePointer(main, Reference(main, "refs/heads/main"), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [last, middle], middle);
        ReferencePointer(main, Reference(main, "refs/heads/topic"), RawInputModifiers.Control | RawInputModifiers.Shift); await Idle(main);
        AssertGridSelection(main, [first, last, middle], middle);
        // Removing the row carrying the caret must retain that unselected caret
        // while two survivors remain (Windows CurrentCell is not SelectedRows[0]).
        ReferencePointer(main, Reference(main, "refs/heads/middle"), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [first, last], middle);
        ReferencePointer(main, Reference(main, "refs/heads/main"), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [first], first);
        ReferencePointer(main, Reference(main, sameFirstReference), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [first], first); // The last selected revision cannot be toggled off.
        Assert.That(tree.SelectedItem, Is.SameAs(Reference(main, sameFirstReference)));
        Assert.That(((TreeViewItem)tree.SelectedItem!).IsKeyboardFocusWithin, Is.True);
        Assert.That(history.ItemsSource, Is.SameAs(source));
        Assert.That(history.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        Assert.That(Field<ComboBox>(main, "BranchHistoryFilter").Text, Is.EqualTo(filter));
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command is "git log" or "git status"), Is.False);
        // SetSelectedRevision pushes the target even when Ctrl removed it.
        PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowLeft, RawInputModifiers.Alt); await Idle(main);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.Last));
        PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowRight, RawInputModifiers.Alt); await Idle(main);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.First));
        Assert.That((await f.Git.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
        Assert.That(File.ReadAllBytes(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Last));
    }

    [AvaloniaTest]
    public async Task ARepeatedCtrlClickOfTheSameNativeNodeOnlyTogglesItsMarkAndDoesNotRetoggleTheGrid()
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        TreeViewItem topic = Reference(main, "refs/heads/topic"); ReferencePointer(main, topic); await Idle(main);
        ReferencePointer(main, Reference(main, "refs/heads/middle"), RawInputModifiers.Control); await Idle(main);
        object[] selection = history.SelectedItems!.Cast<object>().ToArray(); int anchor = history.Selection.AnchorIndex;
        TreeViewItem middle = Reference(main, "refs/heads/middle");
        Assert.That(Label(middle).TextDecorations, Is.EqualTo(Avalonia.Media.TextDecorations.Underline));
        await IndependentClickAsync(main); ReferencePointer(main, middle, RawInputModifiers.Control); await Idle(main);
        Assert.That(history.SelectedItems!.Cast<object>(), Is.EqualTo(selection)); Assert.That(history.Selection.AnchorIndex, Is.EqualTo(anchor));
        Assert.That(Label(middle).TextDecorations, Is.Null.Or.Empty);
        Assert.That(middle.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task AWorktreeHeadUsesTheSameCtrlToggleAndLeavesItsTreeCursorFocused()
    {
        await using Fixture f = await Fixture.CreateAsync(worktree: true); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit first = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.First);
        GitCommit middle = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Middle navigation commit");
        ReferencePointer(main, Reference(main, "refs/heads/topic")); await Idle(main);
        TreeViewItem worktree = BrowseTestHelpers.NavigationNodes(Field<TreeView>(main, "RepositoryNavigation"))
            .Single(node => node.Tag is GitWorktree candidate && candidate.Head == middle.Hash);
        ReferencePointer(main, worktree, RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [middle, first], first);
        Assert.That(worktree.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task TheFirstArrowAfterCtrlReferenceUsesTheTreeAndReturningToTheGridUsesItsRetainedCaret()
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit first = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.First);
        GitCommit middle = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Middle navigation commit");
        ReferencePointer(main, Reference(main, "refs/heads/topic")); await Idle(main);
        ReferencePointer(main, Reference(main, "refs/heads/middle"), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [middle, first], first);
        Assert.That(Reference(main, "refs/heads/middle").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowDown); await Idle(main);
        TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
        Assert.That(tree.SelectedItem, Is.SameAs(Reference(main, "refs/heads/topic")));
        Assert.That(tree.IsKeyboardFocusWithin, Is.True); AssertGridSelection(main, [first], first);
        // Return with a real pointer, then exercise the actual ListBox keyboard path.
        PushProcessTestDriver.Pointer((Control)history.ContainerFromItem(middle)!); await Idle(main);
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowDown); await Idle(main);
        Assert.That(history.SelectedItem, Is.SameAs(first));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CtrlReferencePreservesAnOrdinaryArtificialEndpointUntilItsTypedExtensionIsSupported(bool staged)
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = f.Main;
        PushProcessTestDriver.Pointer(Field<Button>(main, staged ? "IndexRevision" : "WorkingDirectoryRevision")); await Idle(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory"); object? source = history.ItemsSource;
        object[] selected = history.SelectedItems!.Cast<object>().ToArray();
        object? endpoint = Private(main, "_selectedArtificialHistoryRevision");
        object? search = Private(main, "_quickSearchArtificialSelection");
        object? tab = Field<TabControl>(main, "DetailsTabs").SelectedItem;
        ReferencePointer(main, Reference(main, "refs/heads/topic"), RawInputModifiers.Control); await Idle(main);
        Assert.That(search, Is.Not.Null); Assert.That(Private(main, "_selectedArtificialHistoryRevision"), Is.EqualTo(endpoint));
        Assert.That(Private(main, "_quickSearchArtificialSelection"), Is.EqualTo(search));
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItems!.Cast<object>(), Is.EqualTo(selected));
        Assert.That(Field<TabControl>(main, "DetailsTabs").SelectedItem, Is.SameAs(tab));
        Assert.That(Reference(main, "refs/heads/topic").IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task CtrlReferencePreservesAnInitialTypedComparisonWithoutDiscardingEitherEndpoint()
    {
        await using Fixture f = await Fixture.CreateAsync(initialComparison: true); MainWindow main = f.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory"); object? pair = Private(main, "_browseInitialComparison");
        object? artificial = Private(main, "_selectedArtificialHistoryRevision"); object[] selected = history.SelectedItems!.Cast<object>().ToArray();
        object? source = history.ItemsSource;
        ReferencePointer(main, Reference(main, "refs/heads/middle"), RawInputModifiers.Control); await Idle(main);
        Assert.That(pair, Is.Not.Null); Assert.That(Private(main, "_browseInitialComparison"), Is.EqualTo(pair));
        Assert.That(Private(main, "_selectedArtificialHistoryRevision"), Is.EqualTo(artificial));
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItems!.Cast<object>(), Is.EqualTo(selected));
    }

    [AvaloniaTest]
    public async Task CtrlReferenceExtendsARealInitialPairAndPublishesTheNewPrimaryWithoutDiscardingItsRealEndpoints()
    {
        await using Fixture f = await Fixture.CreateAsync(initialComparison: true, artificialComparison: false);
        MainWindow main = f.Main; ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit first = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.First);
        GitCommit last = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Last);
        GitCommit middle = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Middle navigation commit");
        Assert.That(Private(main, "_browseInitialComparison"), Is.Not.Null);
        Assert.That(history.SelectedItems!.OfType<GitCommit>(), Is.EquivalentTo(new[] { first, last }));
        object? source = history.ItemsSource;
        // Windows startup selects [FirstId, SelectedId], hence recent order
        // [SelectedId, FirstId] and CurrentCell remains on FirstId (= last).
        Assert.That(history.Selection.AnchorIndex, Is.EqualTo(history.Items.IndexOf(last)));
        PushProcessTestDriver.Pointer(Field<TabControl>(main, "DetailsTabs").Items.OfType<TabItem>().Single(item => item.Name == "DiffTab"));
        await Idle(main);
        ReferencePointer(main, Reference(main, "refs/heads/middle"), RawInputModifiers.Control); await Idle(main);
        AssertGridSelection(main, [middle, first, last], last);
        IReadOnlyList<GitRevisionDiffGroup> groups = (IReadOnlyList<GitRevisionDiffGroup>)Private(main, "_revisionDiffGroups")!;
        GitRevisionDiffGroup comparison = groups.First(group => group.Kind == GitRevisionDiffGroupKind.Comparison);
        Assert.That(comparison.BaseRevision, Is.EqualTo(last.Hash)); Assert.That(comparison.Revision, Is.EqualTo(middle.Hash));
        Assert.That(Private(main, "_loadedDiffSelection"), Is.EqualTo(string.Join('\0', middle.Hash, first.Hash, last.Hash)));
        Assert.That(Private(main, "_browseInitialComparison"), Is.Null);
        Assert.That(history.ItemsSource, Is.SameAs(source));
        Assert.That(Reference(main, "refs/heads/middle").IsKeyboardFocusWithin, Is.True);
    }

    private static object? Private(MainWindow main, string name) => typeof(MainWindow)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main);
    private static void AssertGridSelection(MainWindow main, GitCommit[] expected, GitCommit caret)
    {
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        Assert.That(history.SelectedItems!.OfType<GitCommit>(), Is.EqualTo(expected));
        Assert.That(history.SelectedItem, Is.SameAs(expected[0]));
        Assert.That((IEnumerable<string>)Private(main, "_historySelectionOrder")!, Is.EqualTo(expected.Reverse().Select(commit => commit.Hash)));
        Assert.That(Private(main, "_quickSearchLastCommit"), Is.EqualTo(expected[0].Hash));
        Assert.That(Private(main, "_parentChildLatestCommit"), Is.EqualTo(expected[0].Hash));
        Assert.That(history.Selection.AnchorIndex, Is.EqualTo(history.Items.IndexOf(caret)));
    }
    private static void ReferencePointer(MainWindow main, TreeViewItem node, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        node.BringIntoView(); TextBlock label = Label(node);
        for (int frame = 0; frame < 10; frame++) { Pump(main); }
        Point point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), main)!.Value;
        Assert.That(label.IsEffectivelyVisible && label.IsEffectivelyEnabled && new Rect(default, main.ClientSize).Contains(point)
            && (main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(label) == true,
            Is.True, "Real reference label: " + label.Text);
        main.MouseMove(point); main.MouseDown(point, MouseButton.Left, modifiers); main.MouseUp(point, MouseButton.Left, modifiers); Pump(main);
    }
    private static void Pump(MainWindow main) { Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task IndependentClickAsync(MainWindow main)
    {
        TimeSpan span = main.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? throw new AssertionException("No platform DoubleTapTime");
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed <= span) { Pump(main); await Task.Delay(10); }
    }
    private static async Task OpenTagsAsync(MainWindow main)
    {
        TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
        TreeViewItem tags = tree.Items.OfType<TreeViewItem>().Single(item => Caption(item) == "Tags");
        ReferencePointer(main, tags); PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowRight); await Idle(main);
        Assert.That(tags.IsExpanded, Is.True);
    }

    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static TreeViewItem Reference(MainWindow main, string name) => BrowseTestHelpers.NavigationNodes(Field<TreeView>(main, "RepositoryNavigation"))
        .Single(node => node.Tag is GitReference reference && reference.Name == name);
    private static TextBlock Label(TreeViewItem item) => ((Control)item.Header!).GetVisualDescendants().OfType<TextBlock>().Single();
    private static string? Caption(TreeViewItem item) => Label(item).Text;
    private static async Task Idle(MainWindow main)
    {
        await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
        await main.WindowWorkCompletion; Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    private sealed class Fixture(GitFixture git, string first, string last) : IAsyncDisposable
    {
        internal GitFixture Git { get; } = git;
        internal string First { get; } = first;
        internal string Last { get; } = last;
        internal MainWindow Main { get; private set; } = null!;
        internal static async Task<Fixture> CreateAsync(bool worktree = false, bool initialComparison = false, bool artificialComparison = true)
        {
            GitFixture git = await GitFixture.CreateAsync(); Fixture? fixture = null;
            try
            {
                string first = await git.Repository.ResolveRevisionAsync("HEAD");
                await git.GitAsync("branch", "topic", first);
                await git.GitAsync("branch", "topic-alias", first);
                await git.GitAsync("update-ref", "refs/remotes/origin/topic", first);
                await git.GitAsync("-c", "tag.gpgsign=false", "tag", "-a", "annotated-topic", "-m", "Navigation target", first);
                await git.ChangeAsync("middle navigation\n", "Middle navigation commit");
                string middle = await git.Repository.ResolveRevisionAsync("HEAD");
                await git.GitAsync("branch", "middle", middle);
                if (worktree) { await git.GitAsync("worktree", "add", "--detach", git.Output("linked-worktree"), middle); }
                await git.ChangeAsync("latest navigation\n", "Latest navigation commit");
                new PreferencesStore().Save(new() { ShowRepoCurrentBranch = false });
                string last = await git.Repository.ResolveRevisionAsync("HEAD");
                fixture = new(git, first, last);
                fixture.Main = new(initialComparison ? new BrowseRequest(SelectedRevision: new(first),
                    FirstRevision: artificialComparison ? new("", GitCompareRevisionKind.WorkingTree) : new(last)) : new BrowseRequest())
                    { Width = 1440, Height = 900 }; fixture.Main.Show();
                await fixture.Main.OpenRepositoryAsync(git.Repository.Path); await Idle(fixture.Main); return fixture;
            }
            catch { if (fixture?.Main is not null) { await fixture.CloseAsync(); } git.Dispose(); throw; }
        }
        internal async Task CloseAsync()
        {
            Main.Close();
            await PushProcessTestDriver.UntilAsync(Main, () => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted);
            await Main.WindowWorkCompletion;
        }
        public async ValueTask DisposeAsync()
        {
            try { await CloseAsync(); }
            finally { Git.Dispose(); }
        }
    }

    private sealed class ResolutionGate : IDisposable
    {
        private readonly GitEnvironmentOptions _previous = GitEnvironment.Options;
        private readonly string _started, _release;
        private (int Pid, string StartTicks)? _identity;
        internal ResolutionGate(GitFixture git)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private process gate requires Linux procfs."); }
            string executable = git.Output("navigation-gated-git"); _started = git.Output("navigation-read-started.json");
            _release = git.Output("navigation-read-release");
            File.WriteAllText(executable, $$"""
                #!/usr/bin/python3
                import json,os,sys,time
                from pathlib import Path
                if 'rev-parse' in sys.argv[1:] and 'refs/heads/topic^{commit}' in sys.argv[1:]:
                    marker=Path({{JsonSerializer.Serialize(_started)}})
                    stat=Path('/proc/self/stat').read_text().rsplit(') ',1)[1].split()
                    pending=marker.with_suffix('.pending')
                    pending.write_text(json.dumps({'pid':os.getpid(),'start_ticks':stat[19]}))
                    os.replace(pending,marker)
                    for _ in range(6000):
                        if Path({{JsonSerializer.Serialize(_release)}}).exists(): break
                        time.sleep(.01)
                    else: sys.exit(74)
                os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[1:]])
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitEnvironment.Configure(_previous with { Executable = executable });
        }
        internal bool HasStarted => File.Exists(_started);
        internal bool Retired
        {
            get
            {
                if (_identity is null)
                {
                    if (!HasStarted) { return false; }
                    using JsonDocument marker = JsonDocument.Parse(File.ReadAllBytes(_started));
                    _identity = (marker.RootElement.GetProperty("pid").GetInt32(), marker.RootElement.GetProperty("start_ticks").GetString()!);
                }
                (int pid, string startTicks) = _identity.Value;
                try
                {
                    string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
                    return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] != startTicks;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
            }
        }
        internal void Release() => File.WriteAllText(_release, "release");
        public void Dispose() { Release(); GitEnvironment.Configure(_previous); }
    }
}
