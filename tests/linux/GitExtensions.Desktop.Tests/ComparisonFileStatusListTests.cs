using GitExtensions.Linux.Desktop.Controls;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ComparisonFileStatusListTests
{
    private const string One = "src/one.cs", Two = "src/two.txt", Renamed = "src/renamed\nλ.cs", Old = "old/previous [λ].cs", Added = "docs/new.md";

    [AvaloniaTest]
    [TestCase(FileStatusListSortMode.FilePath)]
    [TestCase(FileStatusListSortMode.FilePathFlat)]
    [TestCase(FileStatusListSortMode.FileExtension)]
    [TestCase(FileStatusListSortMode.FileExtensionFlat)]
    [TestCase(FileStatusListSortMode.FileStatus)]
    [TestCase(FileStatusListSortMode.FileStatusFlat)]
    public async Task OriginalSixModeMenuUsesTheSharedTreeAndPreservesTheActualSelectedFile(FileStatusListSortMode mode)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Select(f, One); await Idle(f.Dialog);
        SplitButton split = Field<SplitButton>(f.Dialog, "ComparisonFilesViewMode");
        Button arrow = split.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_SecondaryButton");
        PushProcessTestDriver.Pointer(arrow);
        MenuFlyout menu = (MenuFlyout)split.Flyout!;
        MenuItem item = menu.ItemsSource!.OfType<MenuItem>().Single(candidate => candidate.Name == "ComparisonFilesSort_" + mode);
        await Until(() => item.IsEffectivelyVisible && item.Bounds.Height > 0);
        PushProcessTestDriver.Pointer(item); await Idle(f.Dialog);
        Assert.That(f.View.SortMode, Is.EqualTo(mode));
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(One));
        Assert.That(f.Dialog.SelectedFiles.Select(file => file.Path), Is.EqualTo(new[] { One }));
        Assert.That(f.View.Entries.Select(entry => entry.Path), Is.EquivalentTo(f.Dialog.Comparison!.Files.Select(file => file.Path)));
        Assert.That(f.View.Entries.Single(entry => entry.Path == Renamed).OriginalPath, Is.EqualTo(Old));
        Assert.That(f.View.Entries.Single(entry => entry.Path == Renamed).SimilarityPercent, Is.EqualTo(100));
        Assert.That(f.View.Entries.All(entry => entry.Tag is GitRevisionFile), Is.True);
        Assert.That(Diff(f.Dialog), Does.Contain("new one"));
        Assert.That(f.View.Toolbar.Bounds.Height, Is.EqualTo(24));
        Assert.That(Rows(f.View.Tree).Where(row => row.IsEffectivelyVisible).Select(row => ((Control)row.Header!).Bounds.Height), Is.All.EqualTo(ClassicTreeViewAppearance.RowHeight));
        TreeViewItem beforeArrow = f.Item(One);
        Press(f.Dialog, PhysicalKey.ArrowDown); await Idle(f.Dialog);
        Assert.That(f.View.Tree.SelectedItem, Is.Not.SameAs(beforeArrow), "The first Down after the mode popup must navigate the file tree without a repair click.");
        Assert.That(f.View.Tree.IsKeyboardFocusWithin, Is.True);
        Press(f.Dialog, PhysicalKey.ArrowUp); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(One));
        Assert.That(f.Dialog.LastError, Is.Null);
        Capture(f.Dialog, "comparison-shared-mode-" + mode + ".png");
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealComboEditorUsesRegexOldNameMruAndLastGoodSelectionWithoutTakingItsFocus(bool dark)
    {
        await using Fixture f = await Fixture.CreateAsync(dark);
        Capture(f.Dialog, "comparison-shared-tree-" + (dark ? "dark" : "light") + ".png");
        await TypeFilter(f, "renamed"); await Until(() => f.View.FilteredEntries.Count == 1);
        Select(f, Renamed); await Idle(f.Dialog);
        TextBox editor = await TypeFilter(f, "^old/");
        await Until(() => f.View.FilterHistory.Contains("^old/")); await Idle(f.Dialog);
        Assert.That(f.View.FilteredEntries.Single().Path, Is.EqualTo(Renamed));
        Assert.That(f.Dialog.SelectedFile!.OriginalPath, Is.EqualTo(Old));
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor));
        Assert.That(editor.CaretIndex, Is.EqualTo("^old/".Length));
        TreeViewItem oldItem = f.View.FindItemByTag(f.Dialog.SelectedFile)!;
        await TypeFilter(f, "["); await Until(() => f.View.FilterError is not null); await Idle(f.Dialog);
        Assert.That(f.View.FindItemByTag(f.Dialog.SelectedFile!), Is.SameAs(oldItem), "Invalid regex retains the last good displayed tree, including selection.");
        Assert.That(f.View.FilteredEntries.Single().Path, Is.EqualTo(Renamed));
        Assert.That(f.View.FilterHistory, Does.Not.Contain("["));
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(editor));
        Capture(f.Dialog, "comparison-shared-regex-invalid-" + (dark ? "dark" : "light") + ".png");
        await TypeFilter(f, "^never-matches$"); await Until(() => f.View.FilteredEntries.Count == 0 && f.View.FilterError is null); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(f.Dialog.SelectedFiles, Is.Empty); Assert.That(Diff(f.Dialog), Is.Empty);
        Assert.That(f.View.FilterHistory, Does.Not.Contain("^never-matches$"));
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(editor));
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "ComparisonFilesClearFilter"));
        await Until(() => f.View.FilteredEntries.Count == 5); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null, "Clearing a filter does not invent a file selection.");
        await TypeFilter(f, @"\.cs$"); await Until(() => f.View.FilteredEntries.Count == 2); await Idle(f.Dialog);
        Assert.That(f.View.FilterHistory.First(), Is.EqualTo(@"\.cs$"));
        Assert.That(Field<ComboBox>(f.Dialog, "ComparisonFilesFilter").ItemsSource!.OfType<string>(), Is.EqualTo(f.View.FilterHistory));
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(editor));
        Capture(f.Dialog, "comparison-shared-regex-valid-" + (dark ? "dark" : "light") + ".png");
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task NativeFolderGroupMultiAndKeyboardSelectionKeepSingleFileSeparateFromSelectedFiles()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TreeViewItem folder = Rows(f.View.Tree).Single(row => row.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder, Key: "src" });
        PushProcessTestDriver.Pointer(RowTarget(folder)); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(f.Dialog.SelectedFiles.Select(file => file.Path), Is.EquivalentTo(new[] { One, Two, Renamed }));
        Assert.That(Diff(f.Dialog), Is.Empty);
        foreach (string name in new[] { "ComparisonFileTool", "ComparisonSaveBase", "ComparisonSaveCompare" }) { Assert.That(Field<Button>(f.Dialog, name).IsEnabled, Is.False); }
        ClickWithModifier(f.Item(Added), RawInputModifiers.Control); await Idle(f.Dialog);
        Assert.That(f.View.Tree.SelectedItems!.Count, Is.EqualTo(2));
        Assert.That(f.Dialog.SelectedFiles.Select(file => file.Path), Is.EqualTo(new[] { Added }), "Multiple nodes use only explicitly selected leaves, unlike the descendants of one selected parent.");
        ClickWithModifier(f.Item(Two), RawInputModifiers.Control); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(Diff(f.Dialog), Is.Empty);
        Assert.That(f.Dialog.SelectedFiles.Select(file => file.Path), Is.EquivalentTo(new[] { Added, Two }));
        Select(f, One); await Idle(f.Dialog);
        Press(f.Dialog, PhysicalKey.ArrowDown); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Not.Null); Assert.That(f.Dialog.SelectedFile!.Path, Is.Not.EqualTo(One));
        Assert.That(f.View.Tree.IsKeyboardFocusWithin, Is.True, "The first arrow after a real leaf click navigates this file list.");
        f.View.SortMode = FileStatusListSortMode.FileExtension; Pump(f.Dialog);
        TreeViewItem group = Rows(f.View.Tree).Single(row => row.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Extension, Key: ".cs" });
        PushProcessTestDriver.Pointer(RowTarget(group)); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(f.Dialog.SelectedFiles.Select(file => file.Path), Is.EquivalentTo(new[] { One, Renamed }));
        Assert.That(Diff(f.Dialog), Is.Empty);
    }

    [AvaloniaTest]
    public async Task SharedPreferencesChangeExistingCompareAndPeerButKeepTheirFiltersLocal()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using FileStatusListView peer = new(new TreeView { Name = "Peer" }, preferencesStore: f.Store);
        peer.SetGroups([new(new object(), "peer", new TextBlock(), [new(new object(), "peer/one.cs"), new(new object(), "peer/two.txt")])]);
        Select(f, One); await Idle(f.Dialog);
        TextBox revisionInput = Field<TextBox>(f.Dialog, "ComparisonBaseRevision"); PushProcessTestDriver.Pointer(revisionInput);
        peer.DenseTree = true; peer.ShowGroupNodesInFlatList = true;
        peer.SortMode = FileStatusListSortMode.FileExtensionFlat; peer.FilterFileNameOnly = true;
        await Idle(f.Dialog);
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(revisionInput), "A peer preference refresh must not restore focus to the tree.");
        Assert.That(f.View.SortMode, Is.EqualTo(FileStatusListSortMode.FileExtensionFlat));
        Assert.That(f.View.DenseTree && f.View.ShowGroupNodesInFlatList && f.View.FilterFileNameOnly, Is.True);
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(One));
        await TypeFilter(f, @"^one\.cs$"); await Until(() => f.View.FilteredEntries.Count == 1); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(One)); Assert.That(peer.FilteredEntries.Count, Is.EqualTo(2)); Assert.That(peer.FilterText, Is.Empty);
        PushProcessTestDriver.Pointer(Field<ToggleButton>(f.Dialog, "ComparisonFilesByPath")); await Idle(f.Dialog);
        Assert.That(peer.SortMode, Is.EqualTo(FileStatusListSortMode.FilePathFlat));
        Assert.That(File.ReadAllText(f.Store.FilePath), Does.Not.Contain("^one"));
        using FileStatusListView reopened = new(new TreeView(), preferencesStore: f.Store);
        Assert.That(reopened.SortMode, Is.EqualTo(FileStatusListSortMode.FilePathFlat)); Assert.That(reopened.FilterText, Is.Empty);
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task TheOriginalFlatGroupToggleAndComparisonHeaderRemainReadableWhenGroupsAreShown()
    {
        await using Fixture f = await Fixture.CreateAsync();
        f.View.SortMode = FileStatusListSortMode.FileExtensionFlat; Select(f, One); await Idle(f.Dialog);
        SplitButton split = Field<SplitButton>(f.Dialog, "ComparisonFilesViewMode");
        PushProcessTestDriver.Pointer(split.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_SecondaryButton"));
        MenuFlyout menu = (MenuFlyout)split.Flyout!;
        MenuItem toggle = menu.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "ComparisonFilesShowGroupNodesInFlatList");
        await Until(() => toggle.IsEffectivelyVisible && toggle.Bounds.Height > 0); PushProcessTestDriver.Pointer(toggle); await Idle(f.Dialog);
        Assert.That(f.View.ShowGroupNodesInFlatList, Is.True);
        TreeViewItem group = Rows(f.View.Tree).Single(row => row.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Extension, Key: ".cs" });
        Assert.That(((Control)group.Header!).GetVisualDescendants().OfType<TextBlock>().Single().Text, Is.EqualTo(".cs"));
        PushProcessTestDriver.Pointer(RowTarget(group)); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(Diff(f.Dialog), Is.Empty); Assert.That(f.Dialog.SelectedFiles.Count, Is.EqualTo(2));
        // FormDiff leaves its sole revision group hidden by default. Exercise the real
        // header factory as an explicit presentation capability, without changing that default.
        f.View.GroupByRevision = true; PushProcessTestDriver.Pointer(Field<ToggleButton>(f.Dialog, "ComparisonFilesByPath"));
        await TypeFilter(f, @"\.cs$"); await Until(() => f.View.FilteredEntries.Count == 2); await Idle(f.Dialog);
        TreeViewItem revision = Field<TreeViewItem>(f.Dialog, "ComparisonFileGroup");
        TextBlock label = ((Control)revision.Header!).GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.That(label.Text, Is.EqualTo("(2/5) Diff with A " + f.Dialog.Comparison!.Base.Revision[..9]));
        Assert.That(((Control)revision.Header!).GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("Diff.png"));
        PushProcessTestDriver.Pointer(RowTarget(revision)); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(Diff(f.Dialog), Is.Empty); Assert.That(f.Dialog.SelectedFiles.Count, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task AltChangeNavigationIncludesFilesInsideCollapsedFoldersAsWindowsItemTraversalDoes()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TreeViewItem docs = Rows(f.View.Tree).Single(row => row.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder, Key: "docs" });
        PushProcessTestDriver.Pointer(RowTarget(docs)); await Idle(f.Dialog);
        Press(f.Dialog, PhysicalKey.ArrowLeft); await Idle(f.Dialog); Assert.That(docs.IsExpanded, Is.False);
        Select(f, One); await Idle(f.Dialog);
        var view = Field<FileHistoryContentView>(f.Dialog, "ComparisonDiff").TextView;
        PushProcessTestDriver.Pointer(view.Editor.TextArea); view.Select(0, 0);
        Press(f.Dialog, PhysicalKey.ArrowUp, RawInputModifiers.Alt); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(Added)); Assert.That(docs.IsExpanded, Is.True);
        Assert.That(Diff(f.Dialog), Does.Contain("+added"));
        Press(f.Dialog, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(One)); Assert.That(Diff(f.Dialog), Does.Contain("new one"));
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task FilteringToOneRootLeafSelectsItButASoleParentDoesNotSelectItsDescendant(bool flat, bool dense)
    {
        await using Fixture f = await Fixture.CreateAsync();
        f.View.SortMode = flat ? FileStatusListSortMode.FilePathFlat : FileStatusListSortMode.FilePath; f.View.DenseTree = dense;
        Select(f, Added); await Idle(f.Dialog);
        await TypeFilter(f, @"^src/one\.cs$"); await Until(() => f.View.FilteredEntries.Count == 1); await Idle(f.Dialog);
        TreeViewItem root = f.View.Tree.Items.OfType<TreeViewItem>().Single();
        if (flat || dense)
        {
            Assert.That(root.Items.Count, Is.Zero);
            Assert.That(f.View.Tree.SelectedItem, Is.SameAs(root), "Windows1192–1197 selects exactly one root with no child, even after filtering.");
            Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo(One)); Assert.That(Diff(f.Dialog), Does.Contain("new one"));
        }
        else
        {
            Assert.That(root.Items.Count, Is.EqualTo(1));
            Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(f.View.Tree.SelectedItems, Is.Empty); Assert.That(Diff(f.Dialog), Is.Empty);
        }
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(Field<ComboBox>(f.Dialog, "ComparisonFilesFilter").GetVisualDescendants().OfType<TextBox>().Single()));
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task TheFirstFolderKeysAfterARealClickNavigateTheRowAndFilteringKeepsItsOwnEditor()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TreeViewItem docs = Rows(f.View.Tree).Single(row => row.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder, Key: "docs" });
        PushProcessTestDriver.Pointer(RowTarget(docs)); await Idle(f.Dialog);
        Assert.That(docs.GetVisualDescendants().OfType<Border>().Single(part => part.Name == "PART_LayoutRoot"
            && ReferenceEquals(part.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), docs)).Focusable, Is.False);
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(docs), "A pointer click focuses the actual row, not its inner template Border.");
        Capture(f.Dialog, "comparison-folder-before-first-left.png");
        Press(f.Dialog, PhysicalKey.ArrowLeft); await Idle(f.Dialog);
        Assert.That(docs.IsExpanded, Is.False); Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(docs));
        Capture(f.Dialog, "comparison-folder-after-first-left.png");
        Press(f.Dialog, PhysicalKey.ArrowRight); await Idle(f.Dialog);
        Assert.That(docs.IsExpanded, Is.True); Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(docs));
        Press(f.Dialog, PhysicalKey.ArrowDown); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile!.Path, Is.EqualTo("docs/deleted.md"));
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(f.Item("docs/deleted.md")));
        Capture(f.Dialog, "comparison-folder-after-first-down.png");
        TextBox editor = await TypeFilter(f, "^src/"); await Until(() => f.View.FilteredEntries.Count == 3); await Idle(f.Dialog);
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(editor));
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(Diff(f.Dialog), Is.Empty); Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public async Task ModePopupKeepsTheCapturedEditorCaretOrTreeOriginOnChoiceAndCancellation(bool editorOrigin, bool cancel)
    {
        await using Fixture f = await Fixture.CreateAsync();
        TextBox? editor = null; int caret = 0, start = 0, end = 0;
        if (editorOrigin)
        {
            editor = await TypeFilter(f, "src"); await Until(() => f.View.FilteredEntries.Count == 3); await Idle(f.Dialog);
            Press(f.Dialog, PhysicalKey.Home); Press(f.Dialog, PhysicalKey.ArrowRight); Press(f.Dialog, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            caret = editor.CaretIndex; start = editor.SelectionStart; end = editor.SelectionEnd;
            Assert.That(start, Is.Not.EqualTo(end), "The real editor starts with an actual keyboard selection.");
        }
        else { Select(f, One); await Idle(f.Dialog); }
        MenuFlyout menu = await OpenMode(f);
        if (cancel) { Press(TopLevel.GetTopLevel(menu.ItemsSource!.OfType<MenuItem>().First())!, PhysicalKey.Escape); }
        else { PushProcessTestDriver.Pointer(menu.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "ComparisonFilesSort_FileExtensionFlat")); }
        await Until(() => !menu.IsOpen); await Idle(f.Dialog);
        if (editorOrigin)
        {
            Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor)); Assert.That(editor!.Text, Is.EqualTo("src"));
            Assert.That((editor.CaretIndex, editor.SelectionStart, editor.SelectionEnd), Is.EqualTo((caret, start, end)));
        }
        else { Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(f.Item(One))); }
        Assert.That(f.View.SortMode, Is.EqualTo(cancel ? FileStatusListSortMode.FilePath : FileStatusListSortMode.FileExtensionFlat));
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ModeFocusCallbackDoesNotRunAfterHostCloseOrOwnedChildOpening(bool closeHost)
    {
        await using Fixture f = await Fixture.CreateAsync(); Select(f, One); await Idle(f.Dialog);
        MenuFlyout menu = await OpenMode(f); Window replacement = new() { Width = 320, Height = 160, Content = new TextBox { Text = "Other active input" } };
        Task? childShown = null;
        menu.Closed += (_, _) => { if (closeHost) { f.Dialog.Close(); } else { childShown = replacement.ShowDialog(f.Dialog); } };
        try
        {
            PushProcessTestDriver.Pointer(menu.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "ComparisonFilesSort_FileExtensionFlat"));
            if (closeHost) { await Until(() => !f.Dialog.IsVisible); }
            else
            {
                await Until(() => replacement.IsVisible && childShown is not null); Pump(replacement);
                Assert.That(replacement.Owner, Is.SameAs(f.Dialog)); Assert.That(f.Dialog.OwnedWindows, Does.Contain(replacement));
                Assert.That(childShown!.IsCompleted, Is.False); Assert.That(f.View.Tree.IsKeyboardFocusWithin, Is.False,
                    "A queued mode callback must not focus the owner while its real modal child is open.");
            }
        }
        finally { replacement.Close(); if (childShown is not null) { await childShown; } }
    }

    [AvaloniaTest]
    public async Task AbandonedPointerOriginCannotBeReusedByALaterKeyboardModeAction()
    {
        await using Fixture f = await Fixture.CreateAsync(); Select(f, One); await Idle(f.Dialog);
        ToggleButton button = Field<ToggleButton>(f.Dialog, "ComparisonFilesByExtension");
        TextBox other = Field<TextBox>(f.Dialog, "ComparisonBaseRevision"); Pump(f.Dialog);
        Point start = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), f.Dialog)!.Value;
        Point outside = other.TranslatePoint(new Point(other.Bounds.Width / 2, other.Bounds.Height / 2), f.Dialog)!.Value;
        Assert.That((f.Dialog.InputHitTest(start) as Visual)?.GetSelfAndVisualAncestors().Contains(button), Is.True);
        f.Dialog.MouseMove(start); f.Dialog.MouseDown(start, MouseButton.Left); f.Dialog.MouseMove(outside); f.Dialog.MouseUp(outside, MouseButton.Left); Pump(f.Dialog);
        Assert.That(f.View.SortMode, Is.EqualTo(FileStatusListSortMode.FilePath), "Release outside abandons the pointer action; no mode Click occurred.");
        PushProcessTestDriver.Pointer(other); Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(other));
        for (int index = 0; index < 60 && !ReferenceEquals(f.Dialog.FocusManager.GetFocusedElement(), button); index++) { Press(f.Dialog, PhysicalKey.Tab); }
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(button), "The next action reaches the toolbar using actual Tab navigation.");
        Capture(f.Dialog, "comparison-abandoned-before-space.png");
        Press(f.Dialog, PhysicalKey.Space); await Idle(f.Dialog); Capture(f.Dialog, "comparison-abandoned-after-space.png");
        Assert.That(f.View.SortMode, Is.EqualTo(FileStatusListSortMode.FileExtension));
        Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(button), "Keyboard invocation must not reuse a cancelled pointer origin from the earlier tree click.");
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    private static async Task<MenuFlyout> OpenMode(Fixture f)
    {
        SplitButton split = Field<SplitButton>(f.Dialog, "ComparisonFilesViewMode");
        PushProcessTestDriver.Pointer(split.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_SecondaryButton"));
        MenuFlyout menu = (MenuFlyout)split.Flyout!;
        await Until(() => menu.IsOpen && menu.ItemsSource!.OfType<MenuItem>().First().Bounds.Height > 0); return menu;
    }

    private static void Select(Fixture f, string path) { TreeViewItem item = f.Item(path); item.BringIntoView(); Pump(f.Dialog); PushProcessTestDriver.Pointer(RowTarget(item)); }
    private static async Task<TextBox> TypeFilter(Fixture f, string text)
    {
        TextBox editor = Field<ComboBox>(f.Dialog, "ComparisonFilesFilter").GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); Press(f.Dialog, PhysicalKey.A, RawInputModifiers.Control); Press(f.Dialog, PhysicalKey.Backspace);
        foreach (char c in text) { f.Dialog.KeyTextInput(c.ToString()); }
        Pump(f.Dialog); Assert.That(editor.Text, Is.EqualTo(text)); Assert.That(editor.IsEffectivelyEnabled, Is.True);
        await Task.CompletedTask; return editor;
    }
    private static void ClickWithModifier(TreeViewItem item, RawInputModifiers modifiers)
    {
        Control header = RowTarget(item); TopLevel root = TopLevel.GetTopLevel(header)!; Pump(root);
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), root)!.Value;
        Assert.That((root.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(header), Is.True, "Real modified pointer target must hit the intended row header.");
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left, modifiers); root.MouseUp(point, MouseButton.Left, modifiers); Pump(root);
    }
    private static Control RowTarget(TreeViewItem item)
        => item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
            && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
    private static IEnumerable<TreeViewItem> Rows(TreeView tree) => Rows(tree.Items);
    private static IEnumerable<TreeViewItem> Rows(System.Collections.IEnumerable items)
    { foreach (TreeViewItem item in items.OfType<TreeViewItem>()) { yield return item; foreach (TreeViewItem child in Rows(item.Items)) { yield return child; } } }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static string Diff(CompareRevisionsDialog dialog) => Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text;
    private static async Task Idle(CompareRevisionsDialog dialog) { await Until(() => !dialog.IsBusy && dialog.SelectionCompletion.IsCompleted); Pump(dialog); }
    private static async Task Until(Func<bool> ready) { Stopwatch timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(ready(), Is.True); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Capture(TopLevel root, string name) { Pump(root); using var image = root.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ReferenceFixture _git;
        internal CompareRevisionsDialog Dialog { get; }
        internal FileStatusListView View => Field<FileStatusListView>(Dialog, "ComparisonFileStatusList");
        internal FileStatusListPreferencesStore Store { get; }
        private Fixture(ReferenceFixture git, string before, bool dark)
        {
            _git = git; Store = new(git.Output("preferences"));
            Dialog = new(git.Repository, before, "HEAD", Store) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Dialog.Show();
        }
        internal TreeViewItem Item(string path) => View.FindItemByTag(Dialog.Comparison!.Files.Single(file => file.Path == path))!;
        internal static async Task<Fixture> CreateAsync(bool dark = false)
        {
            ReferenceFixture git = await ReferenceFixture.CreateAsync(); Fixture? f = null;
            try
            {
                foreach (string path in new[] { One, Two, Old, "docs/deleted.md" }) { Directory.CreateDirectory(Path.GetDirectoryName(git.PathInRepository(path))!); await File.WriteAllTextAsync(git.PathInRepository(path), "old " + (path == One ? "one" : path) + "\n"); }
                await git.GitAsync("add", "."); await git.GitAsync("commit", "-m", "Before shared files"); string before = await git.Repository.ResolveRevisionAsync("HEAD");
                await git.GitAsync("mv", "--", Old, Renamed);
                await File.WriteAllTextAsync(git.PathInRepository(One), "new one\n"); await File.WriteAllTextAsync(git.PathInRepository(Two), "new two\n");
                await git.GitAsync("rm", "--", "docs/deleted.md"); Directory.CreateDirectory(Path.GetDirectoryName(git.PathInRepository(Added))!); await File.WriteAllTextAsync(git.PathInRepository(Added), "added\n");
                await git.GitAsync("add", "."); await git.GitAsync("commit", "-m", "After shared files");
                f = new(git, before, dark); await f.Dialog.InitializeAsync(); await Idle(f.Dialog); Assert.That(f.Dialog.Comparison!.Files.Count, Is.EqualTo(5)); Assert.That(f.Dialog.LastError, Is.Null); return f;
            }
            catch { if (f is not null) { await f.DisposeAsync(); } else { git.Dispose(); } throw; }
        }
        public async ValueTask DisposeAsync() { Dialog.Close(); await Dialog.SelectionCompletion; await Until(() => !Dialog.IsVisible); _git.Dispose(); }
    }
}
