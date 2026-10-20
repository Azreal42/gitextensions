using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
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

[NonParallelizable]
public sealed class ParentChildNavigationTests
{
    [AvaloniaTest]
    [TestCase("shortcut")]
    [TestCase("main-menu")]
    [TestCase("context-menu")]
    public async Task ParentToItsMergeChildAndBackReturnsTheExactSecondParentWithoutAPicker(string route)
    {
        await using Scope f = await Scope.OpenAsync();
        await f.SelectAsync(f.P); GitCommandLog.Clear();
        await f.NavigateAsync("GotoChildCommit", route);
        Assert.That(f.Selected, Is.EqualTo(f.C));
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty, "Navigate never opens a picker when the merge has several parents.");
        await f.NavigateAsync("GotoParentCommit", route);
        Assert.That(f.Selected, Is.EqualTo(f.P), "The reverse operation must pop P, rather than choose the merge's first parent Q.");
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task FirstAndLastParentUseNativeOrderAndBothCanReturnToTheirExactMergeChild()
    {
        await using Scope f = await Scope.OpenAsync();
        GitCommit merge = f.Reader.Single(commit => commit.Hash == f.C);
        Assert.That(merge.Parents, Is.EqualTo(new[] { f.Q, f.P }));
        await f.SelectAsync(f.C);
        Press(f.Main, PhysicalKey.ArrowRight, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(f.P));
        Press(f.Main, PhysicalKey.N, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(f.C));
        Press(f.Main, PhysicalKey.ArrowLeft, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(f.Q));
        Press(f.Main, PhysicalKey.N, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(f.C));
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True); f.AssertNoReload();
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task SeveralStepsInvertAndAManualSelectionClearsTheirIndependentParentChildStacks()
    {
        await using Scope f = await Scope.OpenAsync();
        await f.SelectAsync(f.P);
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Selected, Is.EqualTo(f.C));
        string child = f.Reader.First(commit => commit.Parents.Contains(f.C)).Hash;
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Selected, Is.EqualTo(child));
        await f.NavigateAsync("GotoParentCommit"); Assert.That(f.Selected, Is.EqualTo(f.C));
        await f.NavigateAsync("GotoParentCommit"); Assert.That(f.Selected, Is.EqualTo(f.P));
        await f.SelectAsync(f.C);
        await f.NavigateAsync("GotoParentCommit");
        Assert.That(f.Selected, Is.EqualTo(f.Q), "An external selection must clear the old return-to-P stack.");
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Selected, Is.EqualTo(f.C));
        await f.SelectAsync(f.R);
        await f.NavigateAsync("GotoParentCommit");
        Assert.That(f.Selected, Is.EqualTo(f.R), "A parentless root cannot reuse a return from another selection.");
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task TheLatestMouseRowAnchorsNavigationEvenWhenItsControlClickDeselectsThatRow()
    {
        await using Scope f = await Scope.OpenAsync();
        await f.SelectAsync(f.D);
        string? added = null;
        f.History.SelectionChanged += (_, args) => added = args.AddedItems.OfType<GitCommit>().LastOrDefault()?.Hash ?? added;
        f.Click(f.C, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { f.D, f.C }));
        Assert.That(added, Is.EqualTo(f.C));
        Press(f.Main, PhysicalKey.ArrowRight, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(f.P), "LastParent must use the most recently selected merge, not the older D row.");
        Assert.That(f.History.SelectedItems!.Count, Is.EqualTo(1));

        await f.SelectAsync(f.D); f.Click(f.C, RawInputModifiers.Control); await f.IdleAsync();
        f.Click(f.C, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { f.D }));
        await f.NavigateAsync("GotoParentCommit");
        // Windows OnGridViewMouseClick assigns the hit row after SelectionChanged,
        // even when Ctrl+click just deselected it (RevisionGridControl.cs:1889-1892).
        Assert.That(f.Selected, Is.EqualTo(f.Q), "The last clicked merge remains LatestSelectedRevision; its first parent is Q, not D's parent C.");
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True); f.AssertNoReload();
    }

    [AvaloniaTest]
    public async Task ADetachedOlderHeadKeepsItsRealReaderChildrenAheadOfTheArtificialIndex()
    {
        await using Scope f = await Scope.OpenAsync(showArtificial: true, headAtMerge: true);
        string[] realChildren = f.Reader.Where(commit => commit.Parents.Contains(f.C)).Select(commit => commit.Hash).ToArray();
        Assert.That(realChildren, Is.EquivalentTo(new[] { f.D, f.E }));
        await f.SelectAsync(f.C);
        await f.NavigateAsync("GotoChildCommit");
        Assert.That(f.Selected, Is.EqualTo(realChildren[0]), "Windows inserts Index when HEAD is read, after the real children which the reader already saw.");
        Assert.That(f.Artificial, Is.Null);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task QuickSearchUsesTheMostRecentSurvivingSelectionAfterProgrammaticRemovalAndPreservesItAcrossRefresh()
    {
        await using Scope f = await Scope.OpenAsync();
        Assert.That(f.Reader.All(commit => commit.AuthorName.Contains("Reference", StringComparison.Ordinal)), Is.True,
            "The real fixture authors must all match the shared non-whitespace search criterion.");
        foreach (bool refresh in new[] { false, true })
        {
            await f.SelectAsync(f.D);
            f.Click(f.E, RawInputModifiers.Control); await f.IdleAsync();
            f.Click(f.C, RawInputModifiers.Control); await f.IdleAsync();
            Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { f.D, f.E, f.C }));
            GitCommit removed = f.History.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.C);
            f.History.SelectedItems!.Remove(removed); await f.IdleAsync();
            Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { f.D, f.E }));
            if (refresh) { Press(f.Main, PhysicalKey.F5); await f.IdleAsync(); }
            Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
            object? source = f.History.ItemsSource; GitCommandLog.Clear();

            f.Main.KeyTextInput("reference"); await f.IdleAsync();

            Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { f.E }),
                "Every fixture author contains Reference; QuickSearch must start with E, the latest surviving selection, rather than D or the removed C.");
            Assert.That(f.History.ItemsSource, Is.SameAs(source));
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
            Press(f.Main, PhysicalKey.Escape);
        }
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task ChildFallbackUsesReaderInsertionOrderAndFirstParentModeDoesNotInventAHiddenSecondParentEdge()
    {
        await using Scope f = await Scope.OpenAsync();
        string[] children = f.Reader.Where(commit => commit.Parents.Contains(f.C)).Select(commit => commit.Hash).ToArray();
        Assert.That(children, Is.EquivalentTo(new[] { f.D, f.E }));
        await f.SelectAsync(f.C); await f.NavigateAsync("GotoChildCommit");
        Assert.That(f.Selected, Is.EqualTo(children[0]), "Use graph-reader insertion, never sort the visible neighbor list by hash or label.");
        await f.SelectAsync(f.P);
        Press(f.Main, PhysicalKey.I, RawInputModifiers.Control);
        await Until(() => f.Main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog filter = f.Main.OwnedWindows.OfType<HistoryFilterDialog>().Single(); Pump(filter);
        PushProcessTestDriver.Pointer(Field<CheckBox>(filter, "OnlyFirstParent"));
        PushProcessTestDriver.Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK"));
        await f.IdleAsync();
        Assert.That(f.Reader.Select(commit => commit.Hash), Does.Contain(f.C).And.Contain(f.P));
        Assert.That(f.Reader.Single(commit => commit.Hash == f.C).Parents, Does.Contain(f.P), "The raw commit still has P; OnlyFirstParent suppresses the graph edge.");
        await f.SelectAsync(f.P); object? source = f.History.ItemsSource; GitCommandLog.Clear();
        await f.NavigateAsync("GotoChildCommit");
        Assert.That(f.Selected, Is.EqualTo(f.P)); Assert.That(f.History.ItemsSource, Is.SameAs(source));
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr-FR", true)]
    public async Task AnOutOfPageParentShowsTheTranslatedModalWithoutReloadingOrChangingSelection(string culture, bool dark)
    {
        await using Scope f = await Scope.OpenAsync(culture: culture, dark: dark, limit: 1);
        GitCommit visible = f.History.Items.OfType<GitCommit>().Single();
        string parent = visible.Parents.First();
        Assert.That(f.History.Items.OfType<GitCommit>().Any(commit => commit.Hash == parent), Is.False);
        await f.SelectAsync(visible.Hash); object? source = f.History.ItemsSource; GitCommandLog.Clear();
        Press(f.Main, PhysicalKey.P, RawInputModifiers.Control);
        await Until(() => f.Main.OwnedWindows.Any(window => window.Name == "GoToCommitError"));
        Window error = f.Main.OwnedWindows.Single(window => window.Name == "GoToCommitError"); Pump(error);
        Assert.That(error.Title, Is.EqualTo(culture == "en" ? "Cannot find revision" : "Impossible de trouver la révision"));
        string message = Field<TextBlock>(error, "GoToCommitErrorMessage").Text!;
        Assert.That(message, Does.Contain(parent[..7]).And.Contain(culture == "en" ? "Remove the revision filter" : "Supprimez le filtre de révision"));
        Capture(error, $"parent-child-invisible-{culture}-{dark}.png");
        FocusCheckpoint("modal-before-first-Escape", f.Main, modal: error);
        error.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        if (f.Main.IsVisible) { f.Main.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
        FocusCheckpoint("modal-sync-after-first-Escape", f.Main, modal: error);
        await Until(() => !error.IsVisible); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(visible.Hash)); Assert.That(f.History.ItemsSource, Is.SameAs(source));
        Assert.That(f.History.Items.Count, Is.EqualTo(1));
        Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task ParentAndChildFromTheRealFileListKeepItsFocusAndItsFirstArrowAfterEachRead()
    {
        await using Scope f = await Scope.OpenAsync(); await f.SelectAsync(f.C);
        TreeView files = Field<TreeView>(f.Main, "RevisionFiles");
        TreeViewItem first = FileStatusTestHelpers.Rows(files).First(item => item.Tag is RevisionDiffFileSelection);
        PushProcessTestDriver.Pointer(Header(first)); await f.IdleAsync();
        bool focusedBeforeParent = files.IsKeyboardFocusWithin;
        FocusCheckpoint("file-before-first-CtrlP", f.Main, files);
        f.Main.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control); f.Main.KeyReleaseQwerty(PhysicalKey.P, RawInputModifiers.Control);
        FocusCheckpoint("file-sync-after-first-CtrlP", f.Main, files);
        await f.IdleAsync(); FocusCheckpoint("file-after-CtrlP-read", f.Main, files);
        Assert.That(focusedBeforeParent, Is.True, "The real file-header click must focus the file list before the shortcut.");
        Assert.That(f.Selected, Is.EqualTo(f.Q)); Assert.That(files.IsKeyboardFocusWithin, Is.True);
        object? before = files.SelectedItem;
        Press(f.Main, PhysicalKey.ArrowDown); await f.IdleAsync();
        Assert.That(files.SelectedItem, Is.Not.SameAs(before)); Assert.That(files.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Selected, Is.EqualTo(f.Q));
        FocusCheckpoint("file-before-first-CtrlN", f.Main, files);
        f.Main.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control); f.Main.KeyReleaseQwerty(PhysicalKey.N, RawInputModifiers.Control);
        FocusCheckpoint("file-sync-after-first-CtrlN", f.Main, files);
        await f.IdleAsync(); FocusCheckpoint("file-after-CtrlN-read", f.Main, files);
        Assert.That(f.Selected, Is.EqualTo(f.C)); Assert.That(files.IsKeyboardFocusWithin, Is.True);
        before = files.SelectedItem;
        Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync();
        Assert.That(files.SelectedItem, Is.Not.SameAs(before)); Assert.That(files.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<DiffView>(f.Main, "CommitDiff").DocumentIdentity, Does.Contain(f.C));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task WorkingTreeIndexAndHeadAreTypedReversibleDestinationsInTheirActualHost(bool fileHistory)
    {
        await using Scope f = await Scope.OpenAsync(showArtificial: true, fileHistory: fileHistory);
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, "WorkingDirectoryRevision")); await f.IdleAsync();
        Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        await f.NavigateAsync("GotoParentCommit");
        Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Assert.That(Field<Button>(f.Main, "IndexRevision").IsKeyboardFocusWithin, Is.True);
        Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo(fileHistory ? "DiffTab" : "ChangesTab"));
        await f.NavigateAsync("GotoParentCommit");
        Assert.That(f.Artificial, Is.Null); Assert.That(f.Selected, Is.EqualTo(f.D));
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(f.History.Items.Cast<object>(), Is.All.TypeOf<GitCommit>());
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OrdinaryArtificialSelectionSurvivesRefreshFromTheWorkingDiffAndKeepsItsTypedParentAndChild(bool staged)
    {
        await using Scope f = await Scope.OpenAsync(showArtificial: true, dirtyWorkingTree: true);
        await f.SelectAsync(f.C);
        Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        GitCompareRevisionKind kind = staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree;
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision"));
        await f.IdleAsync();
        Assert.That(f.Artificial?.Kind, Is.EqualTo(kind));
        Assert.That(f.Selected, Is.EqualTo(f.C), "Ordinary Browse retains its physical real selection behind the typed artificial destination.");
        Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"));
        DiffView working = Field<DiffView>(f.Main, "WorktreeDiff");
        Assert.That(working.Text, Does.Contain(staged ? "-D FILE" : "-INDEX FILE").And.Contain(staged ? "+INDEX FILE" : "+WORKTREE FILE"));
        Assert.That(working.DocumentIdentity, Does.Contain(staged ? "index" : "worktree"));

        // The ordinary typed selection must survive when its button is no longer
        // focused. Ctrl+1 would deliberately select the real-grid surface instead.
        PushProcessTestDriver.Pointer(working.Editor.TextArea); await f.IdleAsync();
        Assert.That(working.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.History.IsKeyboardFocusWithin, Is.False);
        Assert.That(Field<Button>(f.Main, "WorkingDirectoryRevision").IsKeyboardFocusWithin, Is.False);
        Assert.That(Field<Button>(f.Main, "IndexRevision").IsKeyboardFocusWithin, Is.False);
        Assert.That(f.Artificial?.Kind, Is.EqualTo(kind));
        FocusCheckpoint("ordinary-artificial-before-F5-" + kind, f.Main);
        Press(f.Main, PhysicalKey.F5); await f.IdleAsync();
        FocusCheckpoint("ordinary-artificial-after-F5-" + kind, f.Main);
        Assert.That(f.Artificial?.Kind, Is.EqualTo(kind), "F5 must preserve the ordinary typed destination rather than let the real-row rebind replace it.");
        Assert.That(working.Editor.TextArea.IsKeyboardFocusWithin, Is.True, "The same working-diff editor must remain the real input origin after F5.");
        Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"));
        Assert.That(working.Text, Does.Contain(staged ? "-D FILE" : "-INDEX FILE").And.Contain(staged ? "+INDEX FILE" : "+WORKTREE FILE"));
        Assert.That(working.DocumentIdentity, Does.Contain(staged ? "index" : "worktree"));
        Assert.That(f.Selected, Is.EqualTo(f.C));
        object? refreshedSource = f.History.ItemsSource; GitCommandLog.Clear();

        // First command after refresh, without a focus repair or another click.
        Press(f.Main, PhysicalKey.P, RawInputModifiers.Control); await f.IdleAsync();
        if (staged)
        {
            Assert.That(f.Artificial, Is.Null);
            Assert.That(f.Selected, Is.EqualTo(f.D), "Index's parent is real HEAD D, never the parent of the retained physical C row.");
            Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
            Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"),
                "F5 must also preserve the prior revision panel for the automatic Changes-to-real transition.");
        }
        else
        {
            Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
            Assert.That(Field<Button>(f.Main, "IndexRevision").IsKeyboardFocusWithin, Is.True);
            Assert.That(working.Text, Does.Contain("-D FILE").And.Contain("+INDEX FILE"));
            Assert.That(working.DocumentIdentity, Does.Contain("index"));
        }
        Press(f.Main, PhysicalKey.N, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Artificial?.Kind, Is.EqualTo(kind), "Child must reverse the exact typed parent step after F5.");
        Assert.That(Field<Button>(f.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision").IsKeyboardFocusWithin, Is.True);
        Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"));
        Assert.That(working.Text, Does.Contain(staged ? "-D FILE" : "-INDEX FILE").And.Contain(staged ? "+INDEX FILE" : "+WORKTREE FILE"));
        Assert.That(working.DocumentIdentity, Does.Contain(staged ? "index" : "worktree"));
        Assert.That(f.History.ItemsSource, Is.SameAs(refreshedSource));
        Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"),
            "After the explicit F5, Parent/Child uses the loaded graph and only the normal targeted detail readers.");
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task TheUnbornIndexHasNoFakeHeadParentButCanReturnToItsWorkingTreeChild()
    {
        await using Scope f = await Scope.OpenAsync(showArtificial: true, fileHistory: true, unborn: true);
        Assert.That(f.History.Items, Is.Empty); Assert.That(f.Artificial, Is.Null);
        Press(f.Main, PhysicalKey.P, RawInputModifiers.Control); Press(f.Main, PhysicalKey.N, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.Artificial, Is.Null, "No initial selection in the unborn grid must not invent a current revision.");
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, "WorkingDirectoryRevision")); await f.IdleAsync();
        await f.NavigateAsync("GotoParentCommit"); Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        await f.NavigateAsync("GotoParentCommit"); Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Assert.That(f.History.Items, Is.Empty); Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Artificial?.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(f.Main.LastException, Is.Null); f.AssertNoReload();
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task HiddenOrBareArtificialRowsCannotBecomeAHeadChild(bool bare)
    {
        await using Scope f = await Scope.OpenAsync(showArtificial: bare, bare: bare);
        await f.SelectAsync(f.D); Assert.That(Field<Control>(f.Main, "WorkingRevisions").IsEffectivelyVisible, Is.False);
        await f.NavigateAsync("GotoChildCommit");
        Assert.That(f.Selected, Is.EqualTo(f.D)); Assert.That(f.Artificial, Is.Null);
        await f.WaitForIndependentMouseClickAsync();
        f.Click(f.D, RawInputModifiers.Control); await f.IdleAsync();
        Assert.That(f.History.SelectedItems, Is.Empty);
        await f.NavigateAsync("GotoParentCommit");
        Assert.That(f.Selected, Is.EqualTo(f.C), "Windows retains the final mouse-hit row D even after Ctrl+click deselects it.");
        await f.NavigateAsync("GotoChildCommit"); Assert.That(f.Selected, Is.EqualTo(f.D));
        Assert.That(f.Artificial, Is.Null);
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RapidControlSecondClickThatClearsTheSelectedHeadOpensTheActualOwnedLog(bool bare)
    {
        await using Scope f = await Scope.OpenAsync(showArtificial: bare, bare: bare);
        List<(int Count, KeyModifiers Modifiers)> pressed = [];
        f.Main.AddHandler(InputElement.PointerPressedEvent, (_, args) => pressed.Add((args.ClickCount, args.KeyModifiers)),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        f.Click(f.D); Assert.That(f.Selected, Is.EqualTo(f.D));
        Assert.That(pressed, Is.EqualTo(new[] { (1, KeyModifiers.None) }));
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        // Deliberately no delay/await between the actual two clicks.
        f.Click(f.D, RawInputModifiers.Control);
        Assert.That(pressed, Is.EqualTo(new[] { (1, KeyModifiers.None), (2, KeyModifiers.Control) }));
        Assert.That(f.History.SelectedItems, Is.Empty);
        await Until(() => f.Main.OwnedWindows.OfType<MainWindow>().Any(window => window.IsVisible && window.WindowKind == MainWindowKind.RevisionLog));
        MainWindow log = f.Main.OwnedWindows.OfType<MainWindow>().Single(window => window.IsVisible && window.WindowKind == MainWindowKind.RevisionLog);
        Assert.That(log.Owner, Is.SameAs(f.Main));
        await Until(() => !log.IsBusy && log.WindowWorkCompletion.IsCompleted); await Drain(log.WindowWorkCompletion);
        Press(log, PhysicalKey.Escape);
        await Until(() => !log.IsVisible && log.WindowWorkCompletion.IsCompleted); await Drain(log.WindowWorkCompletion);
        await f.IdleAsync();
        Assert.That(f.Main.IsVisible, Is.True); Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerWhileItsInvisibleParentMessageIsOpenDrainsTheActionWithoutPublishingOrRestoringFocus()
    {
        await using Scope f = await Scope.OpenAsync(limit: 1);
        string selected = f.Selected;
        await f.SelectAsync(selected); Press(f.Main, PhysicalKey.P, RawInputModifiers.Control);
        await Until(() => f.Main.OwnedWindows.Any(window => window.Name == "GoToCommitError"));
        Window error = f.Main.OwnedWindows.Single(window => window.Name == "GoToCommitError");
        f.Main.Close();
        await Until(() => !f.Main.IsVisible && !error.IsVisible && f.Main.WindowWorkCompletion.IsCompleted);
        await Drain(f.Main.WindowWorkCompletion);
        Assert.That(f.Main.LastException, Is.Null); Assert.That(f.History.SelectedItem is GitCommit commit ? commit.Hash : null, Is.EqualTo(selected));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    private sealed record Material(string Head, string Index, string Refs, string Files);

    private sealed class Scope(GitFixture git, MainWindow main, string repository, string r, string q, string p, string c, string d, string e) : IAsyncDisposable
    {
        internal MainWindow Main => main;
        internal ListBox History => Field<ListBox>(main, "RevisionHistory");
        internal IReadOnlyList<GitCommit> Reader => main.GetVisualDescendants().OfType<CommitGraph>().Single().Commits;
        internal string Selected => ((GitCommit)History.SelectedItem!).Hash;
        internal string R => r;
        internal string Q => q;
        internal string P => p;
        internal string C => c;
        internal string D => d;
        internal string E => e;
        internal object? Source { get; private set; }
        internal Material Ready { get; private set; } = null!;
        internal GitCompareRevision? Artificial => (GitCompareRevision?)typeof(MainWindow)
            .GetField("_quickSearchArtificialSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);

        internal static async Task<Scope> OpenAsync(string culture = "en", bool dark = false, int limit = 300, bool showArtificial = false,
            bool fileHistory = false, bool unborn = false, bool bare = false, bool headAtMerge = false, bool dirtyWorkingTree = false)
        {
            GitFixture git = await GitFixture.CreateAsync(unborn: true); MainWindow? main = null;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
                string r = "", q = "", p = "", c = "", d = "", e = "";
                async Task<string> Commit(string message)
                { await git.GitAsync("add", "--all"); await git.GitAsync("commit", "-m", message); return await git.Repository.ResolveRevisionAsync("HEAD"); }
                if (!unborn)
                {
                    await File.WriteAllTextAsync(git.File, "ROOT FILE\n"); await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "ROOT OTHER\n"); r = await Commit("Root R");
                    await git.GitAsync("branch", "p", r);
                    await File.WriteAllTextAsync(git.File, "Q FILE\n"); await File.WriteAllTextAsync(git.PathInRepository("q.txt"), "Q TWO\n"); q = await Commit("First parent Q");
                    await git.GitAsync("checkout", "p");
                    await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "P OTHER\n"); await File.WriteAllTextAsync(git.PathInRepository("p.txt"), "P TWO\n"); p = await Commit("Second parent P");
                    await git.GitAsync("checkout", "main"); await git.GitAsync("merge", "--no-ff", "p", "-m", "Merge C"); c = await git.Repository.ResolveRevisionAsync("HEAD");
                    Assert.That((await git.GitAsync("rev-list", "--parents", "-n", "1", c)).StandardOutput.Trim().Split(' ').Skip(1), Is.EqualTo(new[] { q, p }));
                    await File.WriteAllTextAsync(git.File, "D FILE\n"); d = await Commit("Child D");
                    await git.GitAsync("checkout", "-b", "alternate", c);
                    await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "E OTHER\n"); e = await Commit("Child E"); await git.GitAsync("checkout", "main");
                    if (headAtMerge) { await git.GitAsync("checkout", "--detach", c); }
                }
                if (dirtyWorkingTree)
                {
                    Assert.That(unborn || bare || headAtMerge, Is.False, "The dirty ordinary fixture requires HEAD D in a non-bare repository.");
                    await File.WriteAllTextAsync(git.File, "INDEX FILE\n");
                    await git.GitAsync("add", "--", "file.txt");
                    await File.WriteAllTextAsync(git.File, "WORKTREE FILE\n");
                }
                string repository = git.Repository.Path;
                if (bare) { repository = git.Output("bare.git"); await git.GitAsync("clone", "--bare", git.Repository.Path, repository); }
                new PreferencesStore().Save(new DesktopPreferences { HistoryLimit = limit });
                new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = showArtificial });
                new BrowserLayoutPreferencesStore().Save(new(CommitInfoPosition.RightwardFromList));
                main = new(new BrowseRequest(PathFilter: fileHistory ? new("file.txt", BrowsePathKind.File) : null))
                    { Width = 1440, Height = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
                main.Show(); await main.OpenRepositoryAsync(repository);
                Scope result = new(git, main, repository, r, q, p, c, d, e); await result.IdleAsync();
                result.Source = result.History.ItemsSource; result.Ready = await result.MaterialAsync(); GitCommandLog.Clear(); return result;
            }
            catch
            {
                if (main is not null) { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await Drain(main.WindowWorkCompletion); }
                git.Dispose(); throw;
            }
        }

        internal void Click(string hash, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
        {
            GitCommit commit = History.Items.OfType<GitCommit>().Single(row => row.Hash == hash); History.ScrollIntoView(commit); Pump(main);
            Control row = (Control)History.ContainerFromItem(commit)!;
            Point point = row.TranslatePoint(new Point(Math.Min(180, row.Bounds.Width / 2), row.Bounds.Height / 2), main)!.Value;
            Assert.That((main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
            main.MouseMove(point); main.MouseDown(point, button, modifiers); main.MouseUp(point, button, modifiers); Pump(main);
        }
        internal async Task WaitForIndependentMouseClickAsync()
        {
            TimeSpan doubleTap = main.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse)
                ?? throw new InvalidOperationException("The actual platform double-tap interval is unavailable.");
            if (doubleTap < TimeSpan.Zero) { throw new InvalidOperationException("The platform double-tap interval is invalid."); }
            // Round the requested delay strictly above the actual platform maximum;
            // no fixed 500ms assumption. The elapsed-time check also requires a strict gap.
            long milliseconds = checked((long)Math.Ceiling(doubleTap.TotalMilliseconds) + 1);
            Stopwatch elapsed = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds));
            while (elapsed.Elapsed <= doubleTap) { await Task.Yield(); }
            Pump(main);
        }
        internal async Task SelectAsync(string hash) { Click(hash); await IdleAsync(); Assert.That(History.IsKeyboardFocusWithin, Is.True); }
        internal async Task NavigateAsync(string command, string route = "shortcut")
        {
            if (route == "shortcut")
            { Press(main, command == "GotoChildCommit" ? PhysicalKey.N : PhysicalKey.P, RawInputModifiers.Control); }
            else
            {
                MenuItem menu;
                if (route == "main-menu") { menu = Field<MenuItem>(main, "NavigateMenu"); PushProcessTestDriver.Pointer(menu); }
                else
                {
                    Click(Selected, button: MouseButton.Right); await Until(() => History.ContextMenu!.IsOpen);
                    menu = History.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionNavigate");
                    TopLevel root = TopLevel.GetTopLevel(menu)!; Pump(root);
                    Point center = menu.TranslatePoint(new Point(menu.Bounds.Width / 2, menu.Bounds.Height / 2), root)!.Value;
                    Assert.That((root.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(menu), Is.True);
                    // Context submenus open on hover; only the final command
                    // needs a click. Keep this a real pointer path throughout.
                    root.MouseMove(center); Pump(root);
                }
                await Until(() => menu.IsSubMenuOpen);
                MenuItem target = menu.Items.OfType<MenuItem>().Single(item => item.Name == (route == "main-menu" ? "Navigate" : "RevisionNavigate") + command);
                // IsSubMenuOpen is a logical state. Wait for this actual child
                // to join its popup's rendered/hit-testable visual tree.
                await Until(() =>
                {
                    Pump(main);
                    if (target.GetVisualRoot() is not TopLevel root) { return false; }
                    Pump(root);
                    Point? center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root);
                    return ReferenceEquals(TopLevel.GetTopLevel(target), root) && target.IsEffectivelyVisible && target.IsEffectivelyEnabled
                        && target.Bounds.Width > 0 && target.Bounds.Height > 0 && center is Point point
                        && new Rect(default, root.ClientSize).Contains(point)
                        && (root.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(target) == true;
                });
                PushProcessTestDriver.Pointer(target);
            }
            await IdleAsync();
        }
        internal async Task IdleAsync()
        { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted); await Drain(main.WindowWorkCompletion); Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
        internal void AssertNoReload()
        {
            Assert.That(History.ItemsSource, Is.SameAs(Source));
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"), "Navigation uses loaded graph neighbors and ordinary targeted detail readers.");
        }
        internal async Task<Material> MaterialAsync()
        {
            GitRunner reader = new("/usr/bin/git");
            GitResult head = await reader.RunAsync(repository, ["--no-optional-locks", "rev-parse", "--verify", "HEAD"]); Assert.That(head.ExitCode, Is.AnyOf(0, 128));
            GitResult refs = await reader.RunAsync(repository, ["--no-optional-locks", "show-ref"]); Assert.That(refs.ExitCode, Is.AnyOf(0, 1));
            bool isBare = File.Exists(Path.Combine(repository, "HEAD")); string index = Path.Combine(repository, isBare ? "index" : ".git/index");
            string files = isBare ? "<bare>" : string.Join('\n', Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(repository, path).StartsWith(".git/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(repository, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
            return new(head.Success ? head.StandardOutput : "<unborn>", File.Exists(index) ? Convert.ToHexString(await File.ReadAllBytesAsync(index)) : "<no index>", refs.StandardOutput, files);
        }
        public async ValueTask DisposeAsync()
        { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await Drain(main.WindowWorkCompletion); git.Dispose(); }
    }

    private static Control Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
        && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
    private static void FocusCheckpoint(string label, MainWindow main, TreeView? files = null, Window? modal = null)
    {
        IInputElement? focused = main.FocusManager?.GetFocusedElement();
        TopLevel? focusTopLevel = focused is Visual visual ? TopLevel.GetTopLevel(visual) : null;
        RevisionDiffFileSelection? file = (files?.SelectedItem as TreeViewItem)?.Tag as RevisionDiffFileSelection;
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            Label = label, FocusedType = focused?.GetType().Name, FocusedName = (focused as Control)?.Name,
            FocusTopLevel = (focusTopLevel as Window)?.Name, FocusTopLevelType = focusTopLevel?.GetType().Name,
            HistoryFocused = Field<ListBox>(main, "RevisionHistory").IsKeyboardFocusWithin,
            SelectedCommit = (Field<ListBox>(main, "RevisionHistory").SelectedItem as GitCommit)?.Hash,
            SelectedCommits = Field<ListBox>(main, "RevisionHistory").SelectedItems?.OfType<GitCommit>().Select(commit => commit.Hash).ToArray(),
            TreeFocusable = files?.Focusable, TreeEnabled = files?.IsEffectivelyEnabled, TreeVisible = files?.IsEffectivelyVisible,
            TreeFocused = files?.IsKeyboardFocusWithin, SelectedFile = file?.File.Path,
            ModalVisible = modal?.IsVisible, ModalEnabled = modal?.IsEffectivelyEnabled,
            FocusInModal = modal is not null && focused is Visual input && ReferenceEquals(TopLevel.GetTopLevel(input), modal),
            ModalOkFocused = modal?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "CloseGoToCommitError")?.IsKeyboardFocusWithin
        }));
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, Window? release = null)
    {
        window.KeyPressQwerty(key, modifiers); Window target = release ?? window;
        if (target.IsVisible) { target.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs();
    }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Drain(Task work) { try { await work; } catch (OperationCanceledException) { } }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(condition(), Is.True, "The actual parent/child selection, reader or window did not settle.");
    }
    private static void Capture(Window window, string name)
    { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
}
