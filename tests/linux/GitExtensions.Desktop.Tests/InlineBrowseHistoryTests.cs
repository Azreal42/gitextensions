using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
[SupportedOSPlatform("linux")]
public sealed class InlineBrowseHistoryTests
{
    private const string Working = ArtificialHistoryRevision.WorkingTreeKey, Index = ArtificialHistoryRevision.IndexKey;

    // B01/B02. HEAD is deliberately not the first native row.
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task EndpointsBelongImmediatelyBeforeActualHeadAfterANewerIndependentBranch(bool detached)
    {
        await using Scope f = await Scope.OpenAsync(detached: detached);
        Assert.That(f.Native[0], Is.Not.EqualTo(f.Head)); AssertOrder(f, f.Native, f.Head);
        Assert.That(Key(f.Rows.SelectedItem!), Is.EqualTo(f.Head)); object source = f.Rows.ItemsSource!;
        await f.SelectAsync(f.Head); Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync();
        AssertSelected(f, Index); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
        Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync(); AssertSelected(f, Working);
        Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync(); AssertSelected(f, f.Native[0]);
        f.NoReload(source); await f.UnchangedAsync();
    }

    // B03/B04. Browse currently publishes a bounded snapshot after EOF, not streaming batches.
    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task FilteredHeadWaitsForRealLogEofAndUsesLoadedAncestorOrTopWithoutChangingIndexParent(bool ancestor)
    {
        await using Scope f = await Scope.OpenAsync(); object source = f.Rows.ItemsSource!;
        object[] original = f.Rows.Items.Cast<object>().ToArray();
        TextBox filter = Field<TextBox>(f.Main, "RevisionQuickFilter"); Edit(f.Main, filter, ancestor ? "Visible ancestor" : "Visible unrelated");
        f.Gate.Arm("log");
        try
        {
            Press(f.Main, PhysicalKey.Enter); await Until(f.Main, () => f.Gate.Paused);
            Assert.That(f.Gate.Identities, Has.Length.EqualTo(2)); Assert.That(Alive(f.Gate.Identities[0]), Is.True);
            Assert.That(f.Main.WindowWorkCompletion.IsCompleted, Is.False);
            Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.Items.Cast<object>(), Is.EqualTo(original));
            await f.Gate.ReleaseAsync(); await f.IdleAsync();
            string[] expected = ancestor ? [f.Ancestor] : [f.Native[0]];
            AssertOrder(f, expected, expected[0]);
            Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>().Single(row => row.Kind == GitCompareRevisionKind.Index).ParentIds, Is.EqualTo(new[] { f.Head }));
            Assert.That(f.Rows.Items.OfType<GitCommit>().Any(commit => commit.Hash == f.Head), Is.False);
            f.Gate.AssertRetired(); await f.UnchangedAsync();
        }
        finally { await f.Gate.ReleaseAsync(); }
    }

    // B05/B06/B07.
    [AvaloniaTest]
    [TestCase("unborn")] [TestCase("bare")] [TestCase("preference-off")]
    public async Task UnbornBareAndSavedPreferenceHaveTheirOwnEndpointVisibilityAndTypedParents(string mode)
    {
        await using Scope f = await Scope.OpenAsync(mode: mode);
        int count = mode == "unborn" ? 2 : 0;
        Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>().Count(), Is.EqualTo(count)); AssertActualTypes(f.Rows);
        if (mode == "unborn")
        {
            Assert.That(f.Rows.ItemCount, Is.EqualTo(2));
            await f.SelectAsync(Working); AssertSelected(f, Working);
            Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>().Single(row => row.Kind == GitCompareRevisionKind.WorkingTree).ParentIds, Is.EqualTo(new[] { Index }));
            Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync(); AssertSelected(f, Index);
            Assert.That(((ArtificialHistoryRevision)f.Rows.SelectedItem!).ParentIds, Is.Empty);
            Assert.That(((ArtificialHistoryRevision)f.Rows.SelectedItem!).Revision, Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.Index)));
        }
        else { Assert.That(f.Rows.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(f.Native)); }
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty); await f.UnchangedAsync();
    }

    // B08/B09: first mouse click/first arrow; labels are not an independent tab stop.
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task NativeMouseHomeEndPagesAndOneTabStopKeepTheFirstArrowAndRenderInlineLabels(string language, bool dark)
    {
        await using Scope f = await Scope.OpenAsync(language: language, dark: dark); object source = f.Rows.ItemsSource!;
        await f.SelectAsync(dark ? Index : f.Head);
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
        Press(f.Main, dark ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp); await f.IdleAsync(); AssertSelected(f, dark ? f.Head : Index);
        foreach (Button label in f.Rows.GetVisualDescendants().OfType<Button>().Where(button => button.Name is "WorkingDirectoryRevision" or "IndexRevision"))
        { Assert.That(label.Focusable, Is.False); Assert.That(label.IsHitTestVisible, Is.False); Assert.That(label.FindAncestorOfType<ListBoxItem>(), Is.Not.Null); }
        Assert.That(f.Rows.GetVisualDescendants().OfType<Button>().Count(button => button.Name is "WorkingDirectoryRevision" or "IndexRevision"), Is.EqualTo(2));
        object selected = f.Rows.SelectedItem!;
        Press(f.Main, PhysicalKey.Tab); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.False);
        Press(f.Main, PhysicalKey.Tab, RawInputModifiers.Shift); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
        Press(f.Main, PhysicalKey.Home); await f.IdleAsync(); AssertSelected(f, f.Native[0]);
        Press(f.Main, PhysicalKey.End); await f.IdleAsync(); AssertSelected(f, f.Native[^1]);
        int last = f.Rows.SelectedIndex; Press(f.Main, PhysicalKey.PageUp); await f.IdleAsync(); Assert.That(f.Rows.SelectedIndex, Is.LessThan(last - 1));
        int page = f.Rows.SelectedIndex; Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync(); Assert.That(f.Rows.SelectedIndex, Is.EqualTo(page + 1));
        Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync(); Assert.That(f.Rows.SelectedIndex, Is.EqualTo(page));
        Press(f.Main, PhysicalKey.PageDown); await f.IdleAsync(); Assert.That(f.Rows.SelectedIndex, Is.GreaterThan(page));
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); await f.SelectAsync(Index);
        f.Main.Width = f.Main.MinWidth; f.Main.Height = f.Main.MinHeight; Pump(f.Main);
        Capture(f.Main, "browse-inline-" + language + (dark ? "-dark" : "-light"));
        f.NoReload(source); await f.UnchangedAsync();
    }

    // B10/B11. The most recently added selected row is B, regardless of its visual index.
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ControlMixedSelectionUsesNativeAdditionOrderForPatchDirectionAndCanDeselectItsCaret(bool reverse)
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab");
        foreach (string endpoint in new[] { Working, Index })
        {
            string first = reverse ? endpoint : f.Head, last = reverse ? f.Head : endpoint;
            await f.SelectAsync(first); object source = f.Rows.ItemsSource!; GitCommandLog.Clear();
            await f.SelectAsync(last, RawInputModifiers.Control);
            Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(new[] { first, last }));
            Assert.That(FocusedRowKey(f), Is.EqualTo(last));
            AssertDirection(f, first, last);
            string text = endpoint == Working ? "WORKTREE FILE" : "INDEX FILE";
            Assert.That(f.Diff.CanonicalText, Does.Contain((reverse ? "-" : "+") + text));
            Assert.That(f.Diff.CanonicalText, Does.Contain((reverse ? "+" : "-") + "MAIN 59"));
            await f.SelectAsync(last, RawInputModifiers.Control);
            AssertSelected(f, first); Assert.That(FocusedRowKey(f), Is.EqualTo(last), "A Ctrl-deselected caret remains distinct from the surviving selected row.");
            Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync(); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
            f.NoReload(source);
        }
        await f.UnchangedAsync();
    }

    // B12/B13. Shift is a native range, including the actual artificial objects.
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ShiftRangeIncludesBothEndpointsAndRealHeadWithoutInventedCommitRows(bool reverse)
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab");
        string start = reverse ? f.Head : Working, finish = reverse ? Working : f.Head;
        await f.SelectAsync(start); await f.SelectAsync(finish, RawInputModifiers.Shift);
        Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(new[] { Working, Index, f.Head }));
        Assert.That(f.Rows.Selection.AnchorIndex, Is.EqualTo(Array.IndexOf(Keys(f.Rows), start)));
        Assert.That(FocusedRowKey(f), Is.EqualTo(finish)); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
        AssertActualTypes(f.Rows); AssertDirection(f, Working, f.Head);
        Assert.That(f.Diff.CanonicalText, Does.Contain("-WORKTREE FILE").And.Contain("+MAIN 59"));
        Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync(); Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(1));
        await f.UnchangedAsync();
    }

    // B14. Same-payload identity preservation is an explicit Linux refinement; new HEAD follows Windows selection.
    [AvaloniaTest]
    public async Task RefreshRetainsSameSnapshotObjectsOffscreenSelectionCaretAndViewportThenSelectsANewHeadWithFreshIndexParent()
    {
        await using Scope f = await Scope.OpenAsync();
        await f.SelectAsync(Index); await f.SelectAsync(f.Native[42], RawInputModifiers.Control);
        object source = f.Rows.ItemsSource!; object[] objects = f.Rows.Items.Cast<object>().ToArray(); string[] selected = f.Rows.SelectedItems!.Cast<object>().Select(Key).ToArray();
        ScrollViewer scroll = f.Rows.GetVisualDescendants().OfType<ScrollViewer>().First(); Vector offset = scroll.Offset;
        Assert.That(offset.Y, Is.GreaterThan(0)); string? caret = FocusedRowKey(f); int anchor = f.Rows.Selection.AnchorIndex;
        Press(f.Main, PhysicalKey.F5); await f.IdleAsync();
        Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
        for (int i = 0; i < objects.Length; i++) { Assert.That(f.Rows.Items[i], Is.SameAs(objects[i]), "Same payload at " + i); }
        Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(selected));
        Assert.That(FocusedRowKey(f), Is.EqualTo(caret)); Assert.That(f.Rows.Selection.AnchorIndex, Is.EqualTo(anchor)); Assert.That(scroll.Offset, Is.EqualTo(offset));
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); await f.UnchangedAsync();
        await f.Git.GitAsync("add", "--all"); await f.Git.GitAsync("commit", "-m", "New real HEAD after retained refresh");
        string newHead = (await f.Git.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim(); Assert.That(newHead, Is.Not.EqualTo(f.Head));
        Press(f.Main, PhysicalKey.F5); await f.IdleAsync(); AssertSelected(f, newHead);
        ArtificialHistoryRevision index = f.Rows.Items.OfType<ArtificialHistoryRevision>().Single(row => row.Kind == GitCompareRevisionKind.Index);
        Assert.That(index.ParentIds, Is.EqualTo(new[] { newHead })); Assert.That(index, Is.Not.SameAs(objects.OfType<ArtificialHistoryRevision>().Single(row => row.Kind == GitCompareRevisionKind.Index)));
        Assert.That(f.Rows.Items[Array.IndexOf(Keys(f.Rows), newHead) - 1], Is.SameAs(index)); Assert.That(f.Main.LastException, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Commit)] [TestCase(GitCompareRevisionKind.Index)] [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task F5AfterAnExternalHeadChangePublishesTheEffectiveSelectedCommitToTheActiveTreeDiffAndInformation(GitCompareRevisionKind previousKind)
    {
        await using Scope f = await Scope.OpenAsync();
        string previous = previousKind == GitCompareRevisionKind.Commit ? f.Head : previousKind == GitCompareRevisionKind.Index ? Index : Working;
        await f.SelectAsync(previous); await f.TabAsync("FileTreeTab");
        TreeView tree = Field<TreeView>(f.Main, "CommitFileTree");
        TreeViewItem file = FileStatusTestHelpers.Rows(tree).Single(item => item.Tag is string path && path == "file.txt");
        Control header = file.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "PART_Header"
            && ReferenceEquals(item.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), file));
        PushProcessTestDriver.Pointer(header); await f.IdleAsync();
        FileContentView content = Field<FileContentView>(f.Main, "CommitFileContent");
        Assert.That(content.TextView.Text, Is.EqualTo(previousKind == GitCompareRevisionKind.Commit ? "MAIN 59\n"
            : previousKind == GitCompareRevisionKind.Index ? "INDEX FILE\n" : "WORKTREE FILE\n"));
        await f.SelectAsync(previous); // Real row click establishes the F5 input origin before the external mutation.
        object source = f.Rows.ItemsSource!; GitCommit oldHead = f.Rows.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Head);
        const string subject = "External HEAD with a genuinely new tree";
        await File.WriteAllTextAsync(f.Git.File, "NEW HEAD FILE\n");
        await File.WriteAllTextAsync(f.Git.PathInRepository("new-head-only.txt"), "ONLY NEW HEAD\n");
        await f.Git.GitAsync("add", "--all"); await f.Git.GitAsync("commit", "-m", subject);
        string head = await f.Git.Repository.ResolveRevisionAsync("HEAD"); Assert.That(head, Is.Not.EqualTo(f.Head));
        Press(f.Main, PhysicalKey.F5); await f.IdleAsync();
        AssertSelected(f, head); Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
        Assert.That(f.Rows.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Head), Is.SameAs(oldHead));
        Assert.That(((TabItem)f.Tabs.SelectedItem!).Name, Is.EqualTo("FileTreeTab"));
        Assert.That(FileStatusTestHelpers.Rows(tree).Where(item => item.Tag is string).Select(item => (string)item.Tag!), Does.Contain("new-head-only.txt"));
        Assert.That(((TreeViewItem)tree.SelectedItem!).Tag, Is.EqualTo("file.txt"));
        Assert.That(content.TextView.Text, Is.EqualTo("NEW HEAD FILE\n"));
        GitCompareRevision? treeSource = (GitCompareRevision?)typeof(MainWindow).GetField("_fileTreeSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(f.Main);
        Assert.That(treeSource, Is.EqualTo(new GitCompareRevision(head)), "The active tree belongs to the selected real HEAD, never a stale Index/WorkingTree source.");
        Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>().Single(item => item.Kind == GitCompareRevisionKind.Index).ParentIds, Is.EqualTo(new[] { head }));
        CommitInformationView information = Field<CommitInformationView>(f.Main, "CommitInformation");
        Assert.That(information.MessageControl.Text, Does.Contain(subject)); Assert.That(information.Text, Does.Contain(head));
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
        if (previousKind == GitCompareRevisionKind.Commit)
        { Assert.That(FocusedRowKey(f), Is.EqualTo(f.Head), "F5 preserves an unselected surviving caret while following the new HEAD selection."); }
        // The Diff page mounts its real file-list control only when shown.
        // Verify the F5 input origin first, then exercise the actual tab switch.
        await f.TabAsync("DiffTab");
        GitRevisionDiffGroup diff = Groups(f).First(group => group.Kind == GitRevisionDiffGroupKind.Parent);
        Assert.That(diff.Revision, Is.EqualTo(head)); Assert.That(diff.BaseRevision, Is.EqualTo(f.Head));
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await f.Git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.Empty);
    }

    // B15/B16. A genuine native patch read is retained and actually drained, including owner-close.
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task RetainedNativeDiffDoesNotRebuildHistoryAndLatestSelectionOrOwnerCloseDrainsItsExactActors(bool closeOwner)
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab"); await f.SelectAsync(f.Head);
        object source = f.Rows.ItemsSource!; object[] objects = f.Rows.Items.Cast<object>().ToArray(); string oldPatch = f.Diff.CanonicalText;
        f.Gate.Arm("patch"); GitCommandLog.Clear();
        try
        {
            await f.ClickAsync(Index); await Until(f.Main, () => f.Gate.Paused);
            Assert.That(Alive(f.Gate.Identities[0]), Is.True); Assert.That(f.Main.WindowWorkCompletion.IsCompleted, Is.False);
            Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.Items.Cast<object>(), Is.EqualTo(objects));
            Assert.That(f.Diff.CanonicalText, Is.EqualTo(oldPatch), "Keep the previous completed preview until the new patch can publish.");
            if (closeOwner)
            {
                Task admitted = f.Main.WindowWorkCompletion; f.Main.Close(); await f.Gate.ReleaseAsync();
                await Until(f.Main, () => !f.Main.IsVisible && admitted.IsCompleted && f.Main.WindowWorkCompletion.IsCompleted); await admitted; await f.Main.WindowWorkCompletion;
                Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty); Assert.That(f.Main.LastException, Is.Null);
            }
            else
            {
                Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); Press(f.Main, PhysicalKey.ArrowUp);
                await f.Gate.ReleaseAsync(); await f.IdleAsync(); AssertSelected(f, Working);
                Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); Assert.That(f.Diff.CanonicalText, Does.Contain("+WORKTREE FILE"));
                AssertDirection(f, Index, Working);
            }
            f.Gate.AssertRetired(); f.NoReload(source); await f.UnchangedAsync();
        }
        finally { await f.Gate.ReleaseAsync(); }
    }

    // B17. A file-only read keeps the graph, file nodes and old preview until its own EOF.
    [AvaloniaTest]
    public async Task PickingAnotherDiffFileOnlyReplacesItsPreviewAndKeepsTheHistorySelectionAndFileNodes()
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab"); await f.SelectAsync(Index);
        await f.FileAsync("file.txt"); string before = f.Diff.CanonicalText; object source = f.Rows.ItemsSource!;
        TreeView files = f.Files; TreeViewItem[] nodes = FileStatusTestHelpers.Rows(files).ToArray(); object selected = f.Rows.SelectedItem!;
        f.Gate.Arm("patch"); GitCommandLog.Clear();
        try
        {
            f.ClickFile("other.txt"); await Until(f.Main, () => f.Gate.Paused);
            Assert.That(f.Diff.CanonicalText, Is.EqualTo(before)); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
            Assert.That(FileStatusTestHelpers.Rows(files), Is.EqualTo(nodes));
            await f.Gate.ReleaseAsync(); await f.IdleAsync();
            Assert.That(f.Diff.CanonicalText, Does.Contain("+INDEX OTHER").And.Not.Contain("+INDEX FILE"));
            Assert.That(f.Rows.SelectedItem, Is.SameAs(selected)); Assert.That(FileStatusTestHelpers.Rows(files), Is.EqualTo(nodes));
            f.Gate.AssertRetired(); f.NoReload(source); await f.UnchangedAsync();
        }
        finally { await f.Gate.ReleaseAsync(); }
    }

    // B18. Ordinary selection and double-click must not invoke the explicit Toggle command.
    [AvaloniaTest]
    public async Task ClickingOrDoubleClickingAnEndpointKeepsTheActivePanelWhileToggleAloneForcesDiffAndFileFocus()
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("ConsoleTab"); object source = f.Rows.ItemsSource!; GitCommandLog.Clear();
        await f.SelectAsync(Index); Assert.That(f.Tabs.SelectedItem, Is.SameAs(Field<TabItem>(f.Main, "ConsoleTab")));
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); int doubleTaps = 0; f.Rows.DoubleTapped += (_, _) => doubleTaps++;
        await f.ClickAsync(Index); await f.ClickAsync(Index, independent: false); await f.IdleAsync();
        Assert.That(doubleTaps, Is.EqualTo(1), "Two real, adjacent native clicks must reach the actual DoubleTapped route.");
        Assert.That(f.Tabs.SelectedItem, Is.SameAs(Field<TabItem>(f.Main, "ConsoleTab"))); Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git diff"));
        Press(f.Main, PhysicalKey.IntlBackslash, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(((TabItem)f.Tabs.SelectedItem!).Name, Is.EqualTo("DiffTab"));
        Assert.That(f.Files.IsKeyboardFocusWithin, Is.True); Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        Assert.That(f.Diff.CanonicalText, Is.Not.Empty); f.NoReload(source); await f.UnchangedAsync();
    }

    // B19. Native additions yield B,BASE,A; the middle BASE must not become the ordinary A side.
    [AvaloniaTest]
    public async Task ThreeMixedRowsPreserveNativeRecentFirstOrderInTheVisibleComparisonGroups()
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab");
        string a = f.Older, explicitBase = f.Ancestor;
        await f.SelectAsync(a); await f.SelectAsync(explicitBase, RawInputModifiers.Control); await f.SelectAsync(Working, RawInputModifiers.Control);
        Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(new[] { a, explicitBase, Working }));
        AssertDirection(f, a, Working); Assert.That(f.Diff.CanonicalText, Does.Contain("-MAIN 58").And.Contain("+WORKTREE FILE"));
        GitRangeDiffComparison range = Groups(f).Single(group => group.Kind == GitRevisionDiffGroupKind.RangeDiff).RangeDiff!;
        Assert.That(range, Is.Not.Null); Assert.That(range.SelectionCount, Is.EqualTo(3));
        Assert.That(range.First.BaseRevision, Is.EqualTo(explicitBase)); Assert.That(range.Second.BaseRevision, Is.EqualTo(explicitBase));
        Assert.That(range.First.TipRevision, Is.EqualTo(a)); Assert.That(range.Second.TipRevision, Is.EqualTo(f.Head));
        Assert.That(FocusedRowKey(f), Is.EqualTo(Working)); AssertActualTypes(f.Rows); await f.UnchangedAsync();
    }

    // B20. Native additions yield B,baseB,A,baseA, retaining the exact explicit range bounds.
    [AvaloniaTest]
    public async Task FourMixedRowsUseTheThirdRecentRowAsOrdinaryBaseAndKeepFourDistinctSelectedObjects()
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab");
        string a = f.Older, baseA = f.Ancestor, baseB = f.Native[32];
        await f.SelectAsync(baseA); await f.SelectAsync(a, RawInputModifiers.Control); await f.SelectAsync(baseB, RawInputModifiers.Control); await f.SelectAsync(Index, RawInputModifiers.Control);
        Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(new[] { Index, baseB, a, baseA }));
        AssertDirection(f, a, Index); Assert.That(f.Diff.CanonicalText, Does.Contain("-MAIN 58").And.Contain("+INDEX FILE"));
        GitRangeDiffComparison range = Groups(f).Single(group => group.Kind == GitRevisionDiffGroupKind.RangeDiff).RangeDiff!;
        Assert.That(range, Is.Not.Null); Assert.That(range.SelectionCount, Is.EqualTo(4));
        Assert.That(range.First.BaseRevision, Is.EqualTo(baseA)); Assert.That(range.First.TipRevision, Is.EqualTo(a));
        Assert.That(range.Second.BaseRevision, Is.EqualTo(baseB)); Assert.That(range.Second.TipRevision, Is.EqualTo(f.Head));
        Assert.That(FocusedRowKey(f), Is.EqualTo(Index)); AssertActualTypes(f.Rows); await f.UnchangedAsync();
    }

    // B21–B24. Reload controls must consume the entire mixed native selection.
    [AvaloniaTest]
    [TestCase(Working, false)] [TestCase(Working, true)]
    [TestCase(Index, false)] [TestCase(Index, true)]
    public async Task RealDiffRefreshAndAllParentsMenuRetainMixedTwoThreeFourRowEndpointsAndExplicitRanges(string endpoint, bool realPrimary)
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab");
        string baseA = f.Ancestor, baseB = f.Native[32];
        foreach (int count in new[] { 2, 3, 4 })
        {
            string[] recentFirst = count switch
            {
                2 => realPrimary ? [f.Head, endpoint] : [endpoint, f.Head],
                3 => realPrimary ? [f.Older, baseA, endpoint] : [endpoint, baseA, f.Older],
                _ => realPrimary ? [f.Older, baseB, endpoint, baseA] : [endpoint, baseB, f.Older, baseA]
            };
            foreach ((string key, int index) in recentFirst.Reverse().Select((key, index) => (key, index)))
            { await f.SelectAsync(key, index == 0 ? RawInputModifiers.None : RawInputModifiers.Control); }
            Assert.That(FocusedRowKey(f), Is.EqualTo(recentFirst[0]));
            Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(recentFirst));
            object source = f.Rows.ItemsSource!; object[] rowObjects = f.Rows.Items.Cast<object>().ToArray();
            object[] selectedObjects = f.Rows.SelectedItems!.Cast<object>().ToArray(); object firstNativeSelected = f.Rows.SelectedItem!;
            int anchor = f.Rows.Selection.AnchorIndex; GitCommandLog.Clear();
            AssertMixedReload(f, recentFirst, allParents: true);
            string originalGroups = GroupSignature(f), originalPatch = f.Diff.CanonicalText;

            await RefreshFromRealButton(); AssertMixedReload(f, recentFirst, allParents: true);
            Assert.That(GroupSignature(f), Is.EqualTo(originalGroups)); Assert.That(f.Diff.CanonicalText, Is.EqualTo(originalPatch));
            await ToggleAllParentsFromRealMenu(expected: false); AssertMixedReload(f, recentFirst, allParents: false);
            string reducedGroups = GroupSignature(f), reducedPatch = f.Diff.CanonicalText;
            await RefreshFromRealButton(); AssertMixedReload(f, recentFirst, allParents: false);
            Assert.That(GroupSignature(f), Is.EqualTo(reducedGroups)); Assert.That(f.Diff.CanonicalText, Is.EqualTo(reducedPatch));
            await ToggleAllParentsFromRealMenu(expected: true); AssertMixedReload(f, recentFirst, allParents: true);
            Assert.That(GroupSignature(f), Is.EqualTo(originalGroups)); Assert.That(f.Diff.CanonicalText, Is.EqualTo(originalPatch));
            f.NoReload(source); await f.UnchangedAsync();

            void AssertHistoryUnchanged()
            {
                Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
                Assert.That(f.Rows.ItemCount, Is.EqualTo(rowObjects.Length));
                for (int i = 0; i < rowObjects.Length; i++) { Assert.That(f.Rows.Items[i], Is.SameAs(rowObjects[i])); }
                Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(selectedObjects.Length));
                foreach (object selected in selectedObjects)
                { Assert.That(f.Rows.SelectedItems!.Cast<object>().Any(current => ReferenceEquals(current, selected)), Is.True); }
                Assert.That(f.Rows.SelectedItem, Is.SameAs(firstNativeSelected)); Assert.That(f.Rows.Selection.AnchorIndex, Is.EqualTo(anchor));
                Assert.That(((TabItem)f.Tabs.SelectedItem!).Name, Is.EqualTo("DiffTab")); Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
            }
            async Task RefreshFromRealButton()
            {
                Button refresh = Field<Button>(f.Main, "RevisionDiffRefresh"); PushProcessTestDriver.Pointer(refresh);
                IInputElement? gestureFocus = f.Main.FocusManager?.GetFocusedElement();
                Assert.That(gestureFocus, Is.Not.Null); await f.IdleAsync();
                Assert.That(f.Main.FocusManager?.GetFocusedElement(), Is.SameAs(gestureFocus), "Completing a targeted refresh must not move the focus established by its actual gesture.");
                AssertHistoryUnchanged();
            }
            async Task ToggleAllParentsFromRealMenu(bool expected)
            {
                FileStatusListView view = Field<FileStatusListView>(f.Main, "RevisionFileStatusList");
                Button settings = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Flyout, view.SettingsMenu));
                PushProcessTestDriver.Pointer(settings); await Until(f.Main, () => view.SettingsMenu.IsOpen);
                MenuItem all = view.SettingsMenu.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionDiffAllParents");
                Assert.That(all.IsChecked, Is.EqualTo(!expected));
                await Until(f.Main, () => TopLevel.GetTopLevel(all) is not null && all.Bounds.Height > 0);
                PushProcessTestDriver.Pointer(all); Pump(f.Main);
                IInputElement? gestureFocus = f.Main.FocusManager?.GetFocusedElement();
                Assert.That(gestureFocus, Is.Not.Null); await f.IdleAsync();
                Assert.That(view.SettingsMenu.IsOpen, Is.False); Assert.That(all.IsChecked, Is.EqualTo(expected));
                Assert.That(new RevisionDiffPreferencesStore().Load().ShowDiffForAllParents, Is.EqualTo(expected));
                Assert.That(f.Main.FocusManager?.GetFocusedElement(), Is.SameAs(gestureFocus), "A completed grouped read must retain the native menu-dismissal focus.");
                AssertHistoryUnchanged();
            }
        }
    }

    private static void AssertMixedReload(Scope f, string[] recentFirst, bool allParents)
    {
        string from = recentFirst[allParents && recentFirst.Length == 4 ? 2 : recentFirst.Length - 1], to = recentFirst[0];
        AssertDirection(f, from, to); GitRevisionDiffGroup[] groups = Groups(f);
        string Text(string key) => key switch
        {
            Working => "WORKTREE FILE", Index => "INDEX FILE",
            _ when key == f.Head => "MAIN 59", _ when key == f.Older => "MAIN 58", _ when key == f.Ancestor => "MAIN 40",
            _ => throw new AssertionException("Unspecified real fixture endpoint: " + key)
        };
        Assert.That(f.Diff.CanonicalText, Does.Contain("-" + Text(from)).And.Contain("+" + Text(to)));
        if (!allParents || recentFirst.Length == 2)
        { Assert.That(groups.Select(group => group.Kind), Is.EqualTo(new[] { GitRevisionDiffGroupKind.Comparison })); return; }
        Assert.That(groups.Select(group => group.Kind), Is.EqualTo(new[] { GitRevisionDiffGroupKind.Comparison,
            GitRevisionDiffGroupKind.RangeDiff, GitRevisionDiffGroupKind.BaseToSecond, GitRevisionDiffGroupKind.BaseToFirst }));
        GitRangeDiffComparison range = groups[1].RangeDiff!; Assert.That(range, Is.Not.Null);
        Assert.That(range.SelectionCount, Is.EqualTo(recentFirst.Length));
        Assert.That(range.First.BaseRevision, Is.EqualTo(recentFirst[recentFirst.Length == 4 ? 3 : 1])); Assert.That(range.Second.BaseRevision, Is.EqualTo(recentFirst[1]));
        Assert.That(range.First.TipRevision, Is.EqualTo(from is Working or Index ? f.Head : from));
        Assert.That(range.Second.TipRevision, Is.EqualTo(to is Working or Index ? f.Head : to));
    }
    private static string GroupSignature(Scope f) => JsonSerializer.Serialize(Groups(f).Select(group => new
    {
        group.Kind, group.Revision, group.BaseRevision, group.RangeDiff,
        Files = group.Files.Select(file => new { file.Path, file.OriginalPath, file.Status, file.DiffStatus })
    }));

    // B25–B28. Artificial kind alone never means that the displayed A side is its parent.
    [AvaloniaTest]
    [TestCase(Working, true)] [TestCase(Working, false)]
    [TestCase(Index, true)] [TestCase(Index, false)]
    public async Task RealFileMenuOffersStageOrUnstageOnlyForTheActualArtificialParentAndRetainsHistoricalComparisonMetadata(string endpoint, bool actualParent)
    {
        await using Scope f = await Scope.OpenAsync(); await f.TabAsync("DiffTab");
        string from = actualParent ? endpoint == Working ? Index : f.Head : f.Older;
        await f.SelectAsync(from); await f.SelectAsync(endpoint, RawInputModifiers.Control);
        Assert.That(f.Rows.SelectedItems!.Cast<object>().Select(Key), Is.EquivalentTo(new[] { from, endpoint })); AssertDirection(f, from, endpoint);
        await f.FileAsync("file.txt"); object source = f.Rows.ItemsSource!; object[] selected = f.Rows.SelectedItems!.Cast<object>().ToArray();
        string patch = f.Diff.CanonicalText; Assert.That(patch, Does.Contain("+" + (endpoint == Working ? "WORKTREE FILE" : "INDEX FILE")));
        Assert.That(patch, Does.Contain("-" + (actualParent ? endpoint == Working ? "INDEX FILE" : "MAIN 59" : "MAIN 58")));
        GitCommandLog.Clear();
        TreeViewItem row = FileStatusTestHelpers.Rows(f.Files).Single(item => item.Tag is RevisionDiffFileSelection leaf
            && leaf.Group.Kind == GitRevisionDiffGroupKind.Comparison && leaf.File.Path == "file.txt");
        Control header = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
            && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), row));
        Pump(f.Main); Point point = header.TranslatePoint(new Point(Math.Min(120, header.Bounds.Width / 2), header.Bounds.Height / 2), f.Main)!.Value;
        Assert.That(header.IsEffectivelyVisible && header.IsEffectivelyEnabled && new Rect(default, f.Main.ClientSize).Contains(point), Is.True);
        Assert.That((f.Main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(header), Is.True);
        f.Main.MouseMove(point); f.Main.MouseDown(point, MouseButton.Right); f.Main.MouseUp(point, MouseButton.Right); Pump(f.Main);
        ContextMenu menu = f.Files.ContextMenu!; await Until(f.Main, () => menu.IsOpen && TopLevel.GetTopLevel(menu) is not null && menu.Bounds.Height > 0); await f.IdleAsync();
        MenuItem? stage = menu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "tsmiStageFile");
        MenuItem? unstage = menu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "tsmiUnstageFile");
        if (actualParent)
        {
            MenuItem permitted = (endpoint == Working ? stage : unstage)!; Assert.That(permitted, Is.Not.Null);
            Assert.That(permitted.IsEnabled && permitted.IsEffectivelyVisible, Is.True);
            Assert.That(endpoint == Working ? unstage : stage, Is.Null);
        }
        else { Assert.That(stage, Is.Null); Assert.That(unstage, Is.Null); }

        // Modified files expose no public control property for FirstIsParent.
        // This read-only snapshot neither selects/focuses controls nor starts Git work.
        var snapshotMethod = typeof(MainWindow).GetMethod("GetFileMenuSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(snapshotMethod, Is.Not.Null);
        FileMenuSelection snapshot = (FileMenuSelection)snapshotMethod!.Invoke(f.Main, new object[] { false })!;
        Assert.That(snapshot.Leaves, Has.Count.EqualTo(1)); FileMenuLeaf observed = snapshot.Leaves.Single();
        Assert.That(observed.File.Path, Is.EqualTo("file.txt")); Assert.That(observed.FirstIsParent, Is.EqualTo(actualParent));
        Assert.That(observed.Stage, Is.EqualTo(actualParent ? endpoint == Working ? FileMenuStage.WorkingTree : FileMenuStage.Index : FileMenuStage.Unknown));
        Assert.That(observed.First, Is.EqualTo(from == Index ? new GitCompareRevision("", GitCompareRevisionKind.Index) : new GitCompareRevision(from)));
        Assert.That(observed.Second, Is.EqualTo(new GitCompareRevision("", endpoint == Working ? GitCompareRevisionKind.WorkingTree : GitCompareRevisionKind.Index)));
        AssertDirection(f, from, endpoint); Assert.That(f.Diff.CanonicalText, Is.EqualTo(patch));
        foreach (object member in selected) { Assert.That(f.Rows.SelectedItems!.Cast<object>().Any(current => ReferenceEquals(current, member)), Is.True); }
        Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(2));
        TopLevel popup = TopLevel.GetTopLevel(menu)!;
        popup.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        if (popup.IsVisible && popup.PlatformImpl is not null) { popup.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
        Pump(f.Main); await Until(f.Main, () => !menu.IsOpen); await f.IdleAsync();
        Assert.That(f.Files.IsKeyboardFocusWithin, Is.True, "Menu dismissal retains the real file-list input origin.");
        AssertDirection(f, from, endpoint); f.NoReload(source); await f.UnchangedAsync();
    }

    // View topology changes reuse the completed native snapshot, including cached
    // placement ancestors when HEAD was excluded and no artificial row was shown.
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ViewArtificialVisibilityKeepsFilteredHeadPlacementAndCommitIdentityWithoutAnyHistoryRead(bool initiallyHidden)
    {
        await using Scope f = await Scope.OpenAsync(mode: initiallyHidden ? "preference-off" : "ordinary");
        TextBox filter = Field<TextBox>(f.Main, "RevisionQuickFilter"); Edit(f.Main, filter, "Visible");
        Press(f.Main, PhysicalKey.Enter); await f.IdleAsync();
        string[] native = [f.Native[0], f.Ancestor];
        Assert.That(f.Rows.Items.OfType<GitCommit>().Select(item => item.Hash), Is.EqualTo(native));
        Assert.That(f.Rows.Items.OfType<GitCommit>().Any(item => item.Hash == f.Head), Is.False);
        await f.SelectAsync(f.Ancestor);
        object source = f.Rows.ItemsSource!; object selected = f.Rows.SelectedItem!;
        GitCommit[] commits = f.Rows.Items.OfType<GitCommit>().ToArray();
        byte[] profile = await File.ReadAllBytesAsync(new HistoryViewPreferencesStore().FilePath);
        List<System.Collections.Specialized.NotifyCollectionChangedAction> changes = [];
        ((System.Collections.Specialized.INotifyCollectionChanged)source).CollectionChanged += (_, args) => changes.Add(args.Action);
        if (!initiallyHidden)
        {
            await ToggleArtificialView(f);
            Assert.That(Keys(f.Rows), Is.EqualTo(native));
        }
        for (int cycle = 0; cycle < 2; cycle++)
        {
            GitCommandLog.Clear(); await ToggleArtificialView(f);
            AssertOrder(f, native, f.Ancestor);
            Assert.That(((ArtificialHistoryRevision)f.Rows.Items.Cast<object>().Single(item => Key(item) == Index)).ParentIds,
                Is.EqualTo(new[] { f.Head }), "A visible placement ancestor never replaces the actual Index parent.");
            Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
            foreach (GitCommit commit in commits) { Assert.That(f.Rows.Items.Cast<object>().Single(item => Key(item) == commit.Hash), Is.SameAs(commit)); }
            Assert.That(Key(f.Rows.Items[f.Rows.Selection.AnchorIndex]!), Is.EqualTo(f.Ancestor));
            Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); AssertNoHistoryRead();
            GitCommandLog.Clear(); await ToggleArtificialView(f);
            Assert.That(Keys(f.Rows), Is.EqualTo(native)); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
            Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); AssertNoHistoryRead();
        }
        Assert.That(changes, Does.Not.Contain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        Assert.That(await File.ReadAllBytesAsync(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(profile));
        await f.UnchangedAsync();
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)] [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task HidingTheSelectedArtificialRowReloadsOnlyTheActiveTreeForTheRealFallbackAndDoesNotRestoreItOnShow(GitCompareRevisionKind kind)
    {
        await using Scope f = await Scope.OpenAsync();
        await f.SelectAsync(kind == GitCompareRevisionKind.Index ? Index : Working); await f.TabAsync("FileTreeTab");
        FileContentView content = Field<FileContentView>(f.Main, "CommitFileContent");
        Assert.That(content.TextView.Text, Is.EqualTo(kind == GitCompareRevisionKind.Index ? "INDEX FILE\n" : "WORKTREE FILE\n"));
        object source = f.Rows.ItemsSource!; GitCommit head = f.Rows.Items.OfType<GitCommit>().Single(item => item.Hash == f.Head);
        byte[] profile = await File.ReadAllBytesAsync(new HistoryViewPreferencesStore().FilePath);
        object? State(string name) => typeof(MainWindow).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Main);
        var repositoryLists = new[] { "_references", "_remotes", "_stashes", "_worktrees", "_submodules" }
            .Select(name => (Name: name, List: (ListBox)State(name)!))
            .Select(entry => (entry.Name, entry.List, Source: entry.List.ItemsSource, Items: entry.List.Items.Cast<object>().ToArray())).ToArray();
        object? status = State("_historyStatus");
        object[] commits = f.Rows.Items.OfType<GitCommit>().Cast<object>().ToArray();
        string[]? ancestors = (State("_historySnapshotAncestors") as IReadOnlyList<string>)?.ToArray();
        CommitGraph graph = (CommitGraph)State("_historyGraph")!;
        string? graphHead = graph.CurrentHead;
        CommitInformationView information = Field<CommitInformationView>(f.Main, "CommitInformation");
        Assert.That(information.IsEffectivelyVisible, Is.True, "The rightward CommitInfo panel remains visible while File tree is active.");
        GitCommandLog.Clear(); await ToggleArtificialView(f);
        Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>(), Is.Empty); Assert.That(f.Rows.SelectedItem, Is.SameAs(head));
        Assert.That(Key(f.Rows.Items[f.Rows.Selection.AnchorIndex]!), Is.EqualTo(f.Head));
        Assert.That(((TabItem)f.Tabs.SelectedItem!).Name, Is.EqualTo("FileTreeTab"));
        Assert.That(content.TextView.Text, Is.EqualTo("MAIN 59\n"));
        Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
        Assert.That(State("_historyStatus"), Is.SameAs(status), "Toggling visibility must reuse the captured repository status.");
        Assert.That(graph.CurrentHead, Is.EqualTo(graphHead));
        Assert.That(State("_historySnapshotAncestors") as IReadOnlyList<string>, Is.EqualTo(ancestors), "Placement reuses the captured ancestry cache.");
        object[] remaining = f.Rows.Items.Cast<object>().ToArray();
        Assert.That(remaining, Has.Length.EqualTo(commits.Length));
        for (int index = 0; index < commits.Length; index++) { Assert.That(remaining[index], Is.SameAs(commits[index])); }
        Assert.That(graph.OrderedItems.Select(Key), Is.EqualTo(remaining.Select(Key)));
        for (int index = 0; index < remaining.Length; index++) { Assert.That(graph.OrderedItems[index], Is.SameAs(remaining[index])); }
        foreach (var repositoryList in repositoryLists)
        {
            Assert.That(repositoryList.List.ItemsSource, Is.SameAs(repositoryList.Source), repositoryList.Name);
            Assert.That(repositoryList.List.ItemCount, Is.EqualTo(repositoryList.Items.Length), repositoryList.Name);
            for (int index = 0; index < repositoryList.Items.Length; index++)
            { Assert.That(repositoryList.List.Items[index], Is.SameAs(repositoryList.Items[index]), repositoryList.Name); }
        }
        GitCommitContainment? containment = (GitCommitContainment?)typeof(CommitInformationView).GetField("_containment",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(information);
        Assert.That(information.IsLoading, Is.False); Assert.That(information.Completion.IsCompletedSuccessfully, Is.True);
        Assert.That(information.Text, Does.Contain(f.Head)); Assert.That(containment, Is.Not.Null);
        GitCommitContainment loaded = containment!;
        Assert.That(loaded.CommitHash, Is.EqualTo(f.Head));
        Assert.That(loaded.CurrentBranchFullName, Is.EqualTo("refs/heads/main"));
        Assert.That(loaded.References.Single(reference => reference.FullName == "refs/heads/main").IsCurrent, Is.True);
        IReadOnlyList<GitCommandLogEntry> reads = GitCommandLog.Snapshot();
        string commands = string.Join("\n", reads.Select(entry => entry.Id + " " + entry.Command));
        Assert.That(reads, Has.Count.LessThan(GitCommandLog.Capacity), "The complete command boundary must fit in the bounded journal.\n" + commands);
        Assert.That(reads.All(entry => !entry.IsRunning), Is.True, commands);
        Assert.That(reads.Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status")
            .And.Not.Contain("git rev-list").And.Not.Contain("git fetch"), commands);
        Assert.That(reads.Count(entry => entry.Command == "git for-each-ref"), Is.EqualTo(1),
            "Only the visible CommitInfo containment read is expected; no ref-panel reload.\n" + commands);
        Assert.That(reads.Select(entry => entry.Command), Does.Not.Contain("git diff"), "Changing a tree source must not reload the hidden diff pane.\n" + commands);
        object[] current = f.Rows.Items.Cast<object>().ToArray(); string document = content.TextView.Text;
        GitCommandLog.Clear(); await ToggleArtificialView(f);
        AssertOrder(f, f.Native, f.Head); Assert.That(f.Rows.SelectedItem, Is.SameAs(head));
        foreach (object item in current) { Assert.That(f.Rows.Items.Cast<object>().Single(member => Key(member) == Key(item)), Is.SameAs(item)); }
        Assert.That(content.TextView.Text, Is.EqualTo(document)); Assert.That(((TabItem)f.Tabs.SelectedItem!).Name, Is.EqualTo("FileTreeTab"));
        Assert.That(GitCommandLog.Snapshot(), Is.Empty, "Showing rows with the same selection must not reread any pane.");
        Assert.That(await File.ReadAllBytesAsync(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(profile));
        await f.UnchangedAsync();
    }

    [AvaloniaTest]
    public async Task HidingAndShowingArtificialRowsKeepsADeepRealCaretAnchorAndTopViewportWithoutAReset()
    {
        await using Scope f = await Scope.OpenAsync(); await f.SelectAsync(f.Head);
        Press(f.Main, PhysicalKey.PageDown); await f.IdleAsync();
        ScrollViewer scroll = f.Rows.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.That(scroll.Offset.Y, Is.GreaterThan(0));
        double height = ((Control)f.Rows.ContainerFromItem(f.Rows.SelectedItem!)!).Bounds.Height;
        int topIndex = (int)(scroll.Offset.Y / height); string top = Key(f.Rows.Items[topIndex]!);
        double fraction = scroll.Offset.Y - topIndex * height;
        Assert.That(f.Rows.Items[topIndex], Is.TypeOf<GitCommit>());
        object source = f.Rows.ItemsSource!, selected = f.Rows.SelectedItem!;
        string anchor = Key(f.Rows.Items[f.Rows.Selection.AnchorIndex]!);
        GitCommit[] commits = f.Rows.Items.OfType<GitCommit>().ToArray();
        List<System.Collections.Specialized.NotifyCollectionChangedAction> changes = [];
        ((System.Collections.Specialized.INotifyCollectionChanged)source).CollectionChanged += (_, args) => changes.Add(args.Action);
        for (int pass = 0; pass < 2; pass++)
        {
            GitCommandLog.Clear(); await ToggleArtificialView(f);
            Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
            Assert.That(Key(f.Rows.Items[f.Rows.Selection.AnchorIndex]!), Is.EqualTo(anchor));
            Assert.That(FocusedRowKey(f), Is.EqualTo(Key(selected))); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
            int now = Array.IndexOf(Keys(f.Rows), top);
            Assert.That(scroll.Offset.Y, Is.EqualTo(now * height + fraction).Within(.1));
            foreach (GitCommit commit in commits) { Assert.That(f.Rows.Items.Cast<object>().Single(item => Key(item) == commit.Hash), Is.SameAs(commit)); }
            AssertNoHistoryRead();
        }
        Assert.That(changes, Does.Not.Contain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        int next = f.Rows.SelectedIndex + 1; Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync();
        Assert.That(f.Rows.SelectedIndex, Is.EqualTo(next)); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
        await f.UnchangedAsync();
    }

    [AvaloniaTest]
    public async Task UnbornVisibilityCanRemoveAllRowsWithoutLeavingAnArtificialSelectionOrTreeSourceAndShowThemAgain()
    {
        await using Scope f = await Scope.OpenAsync(mode: "unborn"); await f.TabAsync("FileTreeTab"); await f.SelectAsync(Index);
        object source = f.Rows.ItemsSource!; byte[] profile = await File.ReadAllBytesAsync(new HistoryViewPreferencesStore().FilePath);
        List<System.Collections.Specialized.NotifyCollectionChangedAction> changes = [];
        ((System.Collections.Specialized.INotifyCollectionChanged)source).CollectionChanged += (_, args) => changes.Add(args.Action);
        GitCommandLog.Clear(); await ToggleWithFocusTrace("hide");
        Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.Items, Is.Empty);
        Assert.That(f.Rows.SelectedItems, Is.Empty); Assert.That(f.Rows.SelectedItem, Is.Null); Assert.That(f.Rows.Selection.AnchorIndex, Is.EqualTo(-1));
        Assert.That(Field<TreeView>(f.Main, "CommitFileTree").Items, Is.Empty); Assert.That(f.Main.FileGrepResult, Is.Null);
        Assert.That(Field<FileContentView>(f.Main, "CommitFileContent").TextView.DocumentIdentity, Is.Null);
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); AssertNoHistoryRead();
        GitCommandLog.Clear(); await ToggleWithFocusTrace("show");
        Assert.That(Keys(f.Rows), Is.EqualTo(new[] { Working, Index })); Assert.That(f.Rows.Items.OfType<GitCommit>(), Is.Empty);
        Assert.That(((ArtificialHistoryRevision)f.Rows.Items[1]!).ParentIds, Is.Empty);
        Assert.That(f.Rows.SelectedItem, Is.SameAs(f.Rows.Items[0])); Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(1));
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True); AssertNoHistoryRead();
        Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync(); AssertSelected(f, Index);
        Assert.That(changes, Does.Not.Contain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        Assert.That(await File.ReadAllBytesAsync(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(profile));
        await f.UnchangedAsync();

        async Task ToggleWithFocusTrace(string phase)
        {
            MenuItem view = Field<MenuItem>(f.Main, "ViewMenu");
            Trace(phase + ":before-open", view);
            PushProcessTestDriver.Pointer(view); Pump(f.Main);
            Trace(phase + ":menu-open", view);
            Assert.That(view.IsSubMenuOpen, Is.True);
            PushProcessTestDriver.Pointer(view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewShowArtificialCommits"));
            Trace(phase + ":after-gesture", view);
            await f.IdleAsync();
            Trace(phase + ":after-idle", view);
            Assert.That(view.IsSubMenuOpen, Is.False);
        }

        void Trace(string phase, MenuItem view)
        {
            IInputElement? focused = f.Main.FocusManager?.GetFocusedElement();
            Control? input = focused as Control; TopLevel? top = input is null ? null : TopLevel.GetTopLevel(input);
            TestContext.Progress.WriteLine("EMPTY-HISTORY-FOCUS " + JsonSerializer.Serialize(new
            {
                Phase = phase, FocusType = focused?.GetType().FullName, FocusName = input?.Name,
                FocusReference = input is null ? (int?)null : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(input),
                FocusFocusable = input?.Focusable, FocusEnabled = input?.IsEffectivelyEnabled, FocusVisible = input?.IsEffectivelyVisible,
                FocusAncestors = input?.GetSelfAndVisualAncestors().Select(visual => visual.GetType().Name + ":" + (visual as Control)?.Name).ToArray() ?? [],
                FocusTopLevel = top?.GetType().FullName, FocusTopLevelVisible = top?.IsVisible, FocusTopLevelIsMain = ReferenceEquals(top, f.Main),
                CurrentWouldBlockRestore = input is not null && input is not Window && input.Focusable && input.IsEffectivelyEnabled
                    && input.IsEffectivelyVisible && top is { IsVisible: true },
                MainVisible = f.Main.IsVisible, MainEnabled = f.Main.IsEffectivelyEnabled, MainBusy = f.Main.IsBusy,
                WorkCompleted = f.Main.WindowWorkCompletion.IsCompleted, VisibleOwnedWindows = f.Main.OwnedWindows.Count(window => window.IsVisible),
                GridFocusable = f.Rows.Focusable, GridEnabled = f.Rows.IsEffectivelyEnabled, GridVisible = f.Rows.IsEffectivelyVisible,
                GridFocusWithin = f.Rows.IsKeyboardFocusWithin, GridRows = f.Rows.ItemCount, SelectedCount = f.Rows.SelectedItems?.Count,
                GridAnchor = f.Rows.Selection.AnchorIndex, FocusedRow = FocusedRowKey(f),
                MenuOpen = view.IsSubMenuOpen, MenuFocusWithin = view.IsKeyboardFocusWithin, MenuFocusable = view.Focusable,
                MenuVisible = view.IsEffectivelyVisible, MenuEnabled = view.IsEffectivelyEnabled
            }));
        }
    }

    private static void AssertNoHistoryRead() => Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command),
        Does.Not.Contain("git log").And.Not.Contain("git status").And.Not.Contain("git rev-list").And.Not.Contain("git for-each-ref"));

    private static async Task ToggleArtificialView(Scope f)
    {
        MenuItem view = Field<MenuItem>(f.Main, "ViewMenu");
        PushProcessTestDriver.Pointer(view); Pump(f.Main);
        Assert.That(view.IsSubMenuOpen, Is.True);
        PushProcessTestDriver.Pointer(view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewShowArtificialCommits"));
        await f.IdleAsync();
        Assert.That(view.IsSubMenuOpen, Is.False);
    }

    private static string Key(object item) => HistoryRevisionItems.Key(item);
    private static string[] Keys(ListBox rows) => rows.Items.Cast<object>().Select(Key).ToArray();
    private static void AssertSelected(Scope f, string key)
    { Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(1)); Assert.That(Key(f.Rows.SelectedItem!), Is.EqualTo(key)); }
    private static string? FocusedRowKey(Scope f)
    {
        Visual? focus = f.Main.FocusManager?.GetFocusedElement() as Visual;
        ListBoxItem? row = focus?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.FindAncestorOfType<ListBox>(), f.Rows));
        return row?.Content is object item ? Key(item) : null;
    }
    private static void AssertActualTypes(ListBox rows)
    {
        foreach (object? item in rows.Items)
        {
            Assert.That(item, Is.TypeOf<GitCommit>().Or.TypeOf<ArtificialHistoryRevision>());
            if (item is GitCommit commit) { Assert.That(commit.Hash, Does.Match("^[0-9a-f]{40}$")); }
            else { Assert.That(((ArtificialHistoryRevision)item!).Revision.Revision, Is.Empty); }
        }
    }
    private static void AssertOrder(Scope f, string[] native, string before)
    {
        List<string> expected = [.. native]; expected.InsertRange(Array.IndexOf(native, before), [Working, Index]);
        Assert.That(Keys(f.Rows), Is.EqualTo(expected)); AssertActualTypes(f.Rows);
    }
    private static GitRevisionDiffGroup[] Groups(Scope f) => f.Files.Items.OfType<TreeViewItem>().Select(item => item.Tag).OfType<GitRevisionDiffGroup>().ToArray();
    private static string Endpoint(string key) => key == Working ? "WorkingTree" : key == Index ? "Index" : key;
    private static void AssertDirection(Scope f, string from, string to)
    {
        GitRevisionDiffGroup group = Groups(f).First(); Assert.That(group.BaseRevision, Is.EqualTo(Endpoint(from))); Assert.That(group.Revision, Is.EqualTo(Endpoint(to)));
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Edit(Window window, TextBox box, string value)
    { PushProcessTestDriver.Pointer(box); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace); window.KeyTextInput(value); Pump(window); Assert.That(box.Text, Is.EqualTo(value)); }
    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window window, Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) { Pump(window); await Task.Delay(10); }
        Pump(window); if (!condition() && window.IsVisible) { Capture(window, "browse-inline-timeout"); }
        Assert.That(condition(), Is.True, "Browse work did not settle: " + (window as MainWindow)?.LastException);
    }
    private static void Capture(Window window, string name)
    {
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        using var frame = window.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".png"); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static bool Alive((int Pid, string Start) identity)
    { try { string stat = File.ReadAllText("/proc/" + identity.Pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] == identity.Start; } catch (IOException) { return false; } }

    private sealed class Scope : IAsyncDisposable
    {
        internal GitFixture Git { get; } internal MainWindow Main { get; } internal Gate Gate { get; }
        internal string[] Native { get; } internal string Head { get; } internal string Older { get; } internal string Ancestor { get; }
        private readonly GitEnvironmentOptions _oldOptions; private readonly string _repository; private readonly string _material;
        internal ListBox Rows => Field<ListBox>(Main, "RevisionHistory"); internal TabControl Tabs => Field<TabControl>(Main, "DetailsTabs");
        internal TreeView Files => Field<TreeView>(Main, "RevisionFiles"); internal DiffView Diff => Field<DiffView>(Main, "CommitDiff");
        private Scope(GitFixture git, MainWindow main, Gate gate, GitEnvironmentOptions oldOptions, string repository, string[] native, string head, string older, string ancestor, string material)
        { Git = git; Main = main; Gate = gate; _oldOptions = oldOptions; _repository = repository; Native = native; Head = head; Older = older; Ancestor = ancestor; _material = material; }
        internal static async Task<Scope> OpenAsync(bool detached = false, string mode = "ordinary", string language = "en", bool dark = false)
        {
            GitEnvironmentOptions oldOptions = GitEnvironment.Options; GitFixture git = await GitFixture.CreateAsync(unborn: true); MainWindow? main = null;
            try
            {
                git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                string head = "", older = "", ancestor = "";
                if (mode != "unborn")
                {
                    StringBuilder import = new();
                    void Branch(string name, int count, int firstMark, long date)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            string message = name == "newer" ? "Visible unrelated\n" : i == 40 ? "Visible ancestor\n" : "Main revision " + i + "\n";
                            import.Append("commit refs/heads/").Append(name).Append("\nmark :").Append(firstMark + i).Append("\ncommitter Inline Browse <inline@example.invalid> ").Append(date + i).Append(" +0000\ndata ").Append(Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message);
                            if (i > 0) { import.Append("from :").Append(firstMark + i - 1).Append('\n'); }
                            foreach (string file in new[] { "file.txt", "other.txt" })
                            {
                                string text = (name == "main" ? "MAIN " : "OTHER ") + i + (file == "file.txt" ? "\n" : " OTHER\n");
                                import.Append("M 100644 inline ").Append(file).Append("\ndata ").Append(Encoding.UTF8.GetByteCount(text)).Append('\n').Append(text);
                            }
                            import.Append('\n');
                        }
                    }
                    Branch("main", 60, 1, 1700000000); Branch("newer", 1, 1000, 1800000000); import.Append("done\n");
                    GitResult result = await new GitRunner("/usr/bin/git").RunAsync(git.Repository.Path, ["fast-import", "--quiet"], standardInput: import.ToString()); Assert.That(result.Success, Is.True, result.StandardError);
                    await git.GitAsync("reset", "--hard", "HEAD"); if (detached) { await git.GitAsync("checkout", "--detach", "HEAD"); }
                    head = await git.Repository.ResolveRevisionAsync("HEAD"); older = await git.Repository.ResolveRevisionAsync("HEAD~1"); ancestor = await git.Repository.ResolveRevisionAsync("HEAD~19");
                    await File.WriteAllTextAsync(git.File, "INDEX FILE\n"); await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "INDEX OTHER\n"); await git.GitAsync("add", "--all");
                    await File.WriteAllTextAsync(git.File, "WORKTREE FILE\n"); await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "WORKTREE OTHER\n");
                }
                string[] native = mode == "unborn" ? [] : (await git.GitAsync("log", "--format=%H", "--all", "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string repository = git.Repository.Path;
                if (mode == "bare") { repository = git.Output("inline-bare.git"); await git.GitAsync("clone", "--bare", git.Repository.Path, repository); }
                new PreferencesStore().Save(new DesktopPreferences { MaxRevisionGraphCommits = 0, HistoryLimit = 128, Theme = dark ? "Dark" : "Light" });
                new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = mode != "preference-off", ShowAvatar = false, ShowStashes = false, ShowSessionCheckpoints = false, Sort = GitLogSort.GitDefault });
                new BrowserLayoutPreferencesStore().Save(new(CommitInfoPosition.RightwardFromList)); new FileHistoryPreferencesStore().Save(new());
                Gate gate = await Gate.CreateAsync(git); string material = await MaterialAsync(git, repository);
                GitEnvironment.Configure(oldOptions with { Executable = gate.Executable });
                main = new() { Width = 1440, Height = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; main.Show(); await main.OpenRepositoryAsync(repository);
                Scope scope = new(git, main, gate, oldOptions, repository, native, head, older, ancestor, material); await scope.IdleAsync(); GitCommandLog.Clear(); return scope;
            }
            catch
            {
                if (main is not null) { main.Close(); await Until(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await main.WindowWorkCompletion; }
                GitEnvironment.Configure(oldOptions); git.Dispose(); throw;
            }
        }
        internal async Task IdleAsync()
        { await Until(Main, () => !Main.IsBusy && Main.WindowWorkCompletion.IsCompleted); await Main.WindowWorkCompletion; Pump(Main); Assert.That(Main.LastException, Is.Null, Main.LastException?.ToString()); }
        internal async Task ClickAsync(string key, RawInputModifiers modifiers = RawInputModifiers.None, bool independent = true)
        {
            if (independent)
            {
                TimeSpan interval = Main.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? throw new InvalidOperationException("No platform double-tap interval.");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(interval.TotalMilliseconds) + 1));
            }
            object item = Rows.Items.Cast<object>().Single(item => Key(item) == key); Rows.ScrollIntoView(item); Pump(Main);
            Control row = (Control)Rows.ContainerFromItem(item)!; Assert.That(row, Is.Not.Null);
            Point point = row.TranslatePoint(new Point(Math.Min(180, row.Bounds.Width / 2), row.Bounds.Height / 2), Main)!.Value;
            Assert.That(row.IsEffectivelyVisible && row.IsEffectivelyEnabled && new Rect(default, Main.ClientSize).Contains(point), Is.True);
            Assert.That((Main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True, "Click the actual ListBoxItem, not its non-hit-testable label.");
            Main.MouseMove(point); Main.MouseDown(point, MouseButton.Left, modifiers); Main.MouseUp(point, MouseButton.Left, modifiers); Pump(Main);
        }
        internal async Task SelectAsync(string key, RawInputModifiers modifiers = RawInputModifiers.None)
        { await ClickAsync(key, modifiers); Assert.That(Rows.IsKeyboardFocusWithin, Is.True, "The first click owns native row focus before the read completes."); await IdleAsync(); }
        internal async Task TabAsync(string name)
        { PushProcessTestDriver.Pointer(Field<TabItem>(Main, name)); await IdleAsync(); Assert.That(((TabItem)Tabs.SelectedItem!).Name, Is.EqualTo(name)); }
        internal void ClickFile(string path)
        {
            TreeViewItem item = FileStatusTestHelpers.Rows(Files).First(item => item.Tag is RevisionDiffFileSelection file && file.File.Path == path);
            foreach (TreeViewItem parent in FileStatusTestHelpers.Rows(Files).Where(parent => parent.Items.Contains(item))) { parent.IsExpanded = true; }
            Pump(Main);
            Control header = item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
            PushProcessTestDriver.Pointer(header);
        }
        internal async Task FileAsync(string path) { ClickFile(path); await IdleAsync(); }
        internal void NoReload(object source)
        { Assert.That(Rows.ItemsSource, Is.SameAs(source)); Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status").And.Not.Contain("git ls-tree")); }
        internal async Task UnchangedAsync() => Assert.That(await MaterialAsync(Git, _repository), Is.EqualTo(_material));
        public async ValueTask DisposeAsync()
        {
            await Gate.ReleaseAsync(); Main.Close(); await Until(Main, () => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted); await Main.WindowWorkCompletion;
            Gate.AssertRetired(); GitEnvironment.Configure(_oldOptions); Git.Dispose();
        }
        private static async Task<string> MaterialAsync(GitFixture git, string repository)
        {
            GitResult refs = await new GitRunner("/usr/bin/git").RunAsync(repository, ["for-each-ref", "--format=%(refname) %(objectname)"]); Assert.That(refs.Success, Is.True, refs.StandardError);
            return refs.StandardOutput + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt", "other.txt" }
                .Select(path => git.PathInRepository(path)).Where(File.Exists).Select(path => Path.GetRelativePath(git.Repository.Path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }
    }

    // A one-shot wrapper around real /usr/bin/git. It withholds actual stdout EOF;
    // it never substitutes a fake repository result or abandons an awaited sink.
    private sealed class Gate(string directory, string executable)
    {
        internal string Executable => executable; internal bool Paused => File.Exists(Path.Combine(directory, "paused"));
        internal void Arm(string kind)
        { foreach (string name in new[] { "release", "paused", "identities.json" }) { File.Delete(Path.Combine(directory, name)); } File.WriteAllText(Path.Combine(directory, "arm"), kind); }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal (int Pid, string Start)[] Identities
        { get { string path = Path.Combine(directory, "identities.json"); if (!File.Exists(path)) { return []; } using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.EnumerateArray().Select(item => (item.GetProperty("pid").GetInt32(), item.GetProperty("start").GetString()!)).ToArray(); } }
        internal void AssertRetired() { foreach (var identity in Identities) { Assert.That(Alive(identity), Is.False, "Exact PID/start (including zombies) still exists: " + identity); } }
        internal static async Task<Gate> CreateAsync(GitFixture fixture)
        {
            string directory = fixture.Output("browse-native-gate"); Directory.CreateDirectory(directory); string executable = Path.Combine(directory, "git");
            await File.WriteAllTextAsync(executable, $$"""
                #!/usr/bin/python3
                import os,sys,json,time,subprocess,threading
                from pathlib import Path
                directory=Path({{JsonSerializer.Serialize(directory)}})
                args=sys.argv[1:]
                try: kind=(directory/'arm').read_text().strip()
                except FileNotFoundError: kind=''
                patch='diff' in args and '--name-status' not in args and '--raw' not in args and '--numstat' not in args and '--name-only' not in args and '--stat' not in args
                selected=(kind=='log' and 'log' in args) or (kind=='patch' and patch)
                if not selected: os.execv('/usr/bin/git',['/usr/bin/git',*args])
                try: os.rename(directory/'arm',directory/'claimed')
                except FileNotFoundError: os.execv('/usr/bin/git',['/usr/bin/git',*args])
                deadline=threading.Timer(40,lambda:os._exit(124)); deadline.daemon=True; deadline.start()
                read_fd,write_fd=os.pipe()
                trampoline="import os,sys; fd=int(sys.argv[1]); token=os.read(fd,1); os.close(fd); token==b'G' or sys.exit(125); os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[2:]])"
                try:
                    child=subprocess.Popen(['/usr/bin/python3','-c',trampoline,str(read_fd),*args],stdout=subprocess.PIPE,bufsize=0,pass_fds=(read_fd,))
                except BaseException:
                    os.close(read_fd); os.close(write_fd); raise
                os.close(read_fd)
                def identity(pid):
                    fields=Path('/proc/'+str(pid)+'/stat').read_text().rsplit(')',1)[1].strip().split()
                    return dict(pid=pid,start=fields[19])
                try:
                    # The child cannot exec Git until its exact PID/start has
                    # been published. Both values remain unchanged by execv.
                    (directory/'identities.pending').write_text(json.dumps([identity(os.getpid()),identity(child.pid)]))
                    os.replace(directory/'identities.pending',directory/'identities.json')
                    os.write(write_fd,b'G')
                except BaseException:
                    # EOF rejects the trampoline admission and communicate
                    # reaps it before any identity-publication failure escapes.
                    os.close(write_fd); child.communicate(); raise
                else:
                    os.close(write_fd)
                # Drain and reap the real child before retaining the wrapper's
                # actual output pipe. Cancelling the wrapper cannot orphan a
                # zombie native Git child. Fixture output is deliberately small.
                payload=child.communicate()[0]
                code=child.returncode
                (directory/'paused').write_text(json.dumps(args))
                while not (directory/'release').exists(): time.sleep(.01)
                sys.stdout.buffer.write(payload); sys.stdout.buffer.flush(); sys.exit(code)
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return new(directory, executable);
        }
    }
}
