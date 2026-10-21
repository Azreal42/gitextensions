using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ViewPatchDialogTests
{
    [AvaloniaTest, TestCase("en", false), TestCase("fr", true)]
    public async Task RealStorageFilePublishesTheOriginalColumnsAndTheFirstArrowsChangeTheDiffWithoutFocusRepair(string language, bool dark)
    {
        await using Host h = new(language, dark); ViewPatchDialog d = h.Open();
        string path = h.Write("two files.patch", TextPatch("z-last.txt", "last") + TextPatch("a-first.txt", "first"));
        byte[] before = File.ReadAllBytes(path); await h.Load(d, path);
        Assert.That(d.ClientSize.Width, Is.EqualTo(689)); Assert.That(d.ClientSize.Height, Is.EqualTo(501));
        Assert.That(d.Owner, Is.SameAs(h.Owner)); Assert.That(d.ActualThemeVariant, Is.EqualTo(h.Owner.ActualThemeVariant));
        Assert.That(Field<Button>(d, "FileNameA").Content, Is.EqualTo(language == "fr" ? "Nom du fichier" : "Filename"));
        Assert.That(Field<Button>(d, "typeDataGridViewTextBoxColumn").Content, Is.Not.Null);
        Assert.That(Field<Button>(d, "File").Content, Is.Not.Null);
        ListBox list = Field<ListBox>(d, "GridChangedFiles"); Assert.That(list.SelectionMode, Is.EqualTo(SelectionMode.Single));
        Assert.That(d.Patches.Select(p => p.FileNameA), Is.EqualTo(new[] { "z-last.txt", "a-first.txt" }));
        Assert.That(d.Viewer.Text, Does.Contain("+last"));
        PushProcessTestDriver.Pointer(list.ContainerFromIndex(0)!);
        Press(d, PhysicalKey.ArrowDown);
        Assert.That((list.SelectedItem as Patch)?.FileNameA, Is.EqualTo("a-first.txt"));
        Assert.That(d.Viewer.Text, Does.Contain("+first").And.Not.Contain("+last"));
        Press(d, PhysicalKey.ArrowUp);
        Assert.That((list.SelectedItem as Patch)?.FileNameA, Is.EqualTo("z-last.txt"));
        Assert.That(d.Viewer.Text, Does.Contain("+last")); Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        Assert.That(d.Viewer.IsReadOnly, Is.True); Assert.That(d.Viewer.IsFixedPatch, Is.True);
        Assert.That(d.Viewer.ComparisonMenu.IncreaseContext(), Is.False);
        Assert.That(new Control[] { d.Viewer.Toolbar.IncreaseContext, d.Viewer.Toolbar.DecreaseContext, d.Viewer.Toolbar.EntireFile,
            d.Viewer.Toolbar.WhitespaceEol, d.Viewer.Toolbar.WhitespaceChanges, d.Viewer.Toolbar.AllWhitespace, d.Viewer.Toolbar.EncodingSelector }
            .All(control => !control.IsVisible), Is.True, "A fixed patch must not offer reader options with no Git reader behind them.");
        Assert.That(d.Viewer.Toolbar.NextChange.IsVisible && d.Viewer.Toolbar.PreviousChange.IsVisible, Is.True,
            "Refresh(partial: true) retains fixed-patch navigation while reader options stay hidden.");
    }

    [AvaloniaTest]
    public async Task AllThreeSortableColumnsUseTheWindowsOrdinalKeysAndRetainTheSelectedPatch()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open();
        await h.Load(d, h.Write("sort.patch", TextPatch("z.txt", "z") + "diff --git a/B.txt b/B.txt\nold mode 100644\nnew mode 100755\n"));
        ListBox list = Field<ListBox>(d, "GridChangedFiles"); object selected = list.SelectedItem!; string text = d.Viewer.Text;
        Click(d, "FileNameA"); Assert.That(list.ItemsSource!.OfType<Patch>().Select(p => p.FileNameA), Is.EqualTo(new[] { "B.txt", "z.txt" }));
        Assert.That(list.SelectedItem, Is.SameAs(selected)); Assert.That(d.Viewer.Text, Is.EqualTo(text));
        Click(d, "FileNameA"); Assert.That(list.ItemsSource!.OfType<Patch>().First().FileNameA, Is.EqualTo("z.txt"));
        Click(d, "typeDataGridViewTextBoxColumn"); Assert.That(list.ItemsSource!.OfType<Patch>().Select(p => p.ChangeType.ToString()), Is.Ordered.Using<string>(StringComparer.Ordinal));
        Click(d, "File"); Assert.That(list.SelectedItem, Is.SameAs(selected));
    }

    [AvaloniaTest]
    public async Task HeaderClickSortNeverPublishesAnIntermediateSelectionOrDiffAndTheFirstUpUsesTheReorderedGrid()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open();
        await h.Load(d, h.Write("silent-sort.patch", TextPatch("z.txt", "last") + TextPatch("a.txt", "first") + TextPatch("m.txt", "middle")));
        ListBox list = Field<ListBox>(d, "GridChangedFiles"); Patch selected = (Patch)list.SelectedItem!;
        object rows = list.ItemsSource!, document = d.Viewer.Editor.Document;
        PushProcessTestDriver.Pointer(d.Viewer.Editor.TextArea);
        Press(d, PhysicalKey.Home, RawInputModifiers.Control); Press(d, PhysicalKey.ArrowRight); Press(d, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        int caret = d.Viewer.CaretOffset, selectionStart = d.Viewer.SelectionStart, selectionLength = d.Viewer.SelectionLength;
        Assert.That(caret, Is.GreaterThan(0)); Assert.That(selectionLength, Is.GreaterThan(0));
        string identity = d.Viewer.DocumentIdentity!, text = d.Viewer.Text;
        int documentChanges = 0, caretChanges = 0, textSelectionChanges = 0, sourceChanges = 0;
        List<SelectionChangedEventArgs> selections = [];
        List<object?> selectedDuringMoves = [];
        d.Viewer.Editor.Document.Changed += (_, _) => ++documentChanges;
        d.Viewer.Editor.TextArea.Caret.PositionChanged += (_, _) => ++caretChanges;
        d.Viewer.Editor.TextArea.SelectionChanged += (_, _) => ++textSelectionChanges;
        list.SelectionChanged += (_, args) => selections.Add(args);
        list.PropertyChanged += (_, args) => { if (args.Property == ItemsControl.ItemsSourceProperty) { ++sourceChanges; } };
        ((INotifyCollectionChanged)rows).CollectionChanged += (_, _) => selectedDuringMoves.Add(list.SelectedItem);

        Click(d, "FileNameA");
        Assert.That(list.ItemsSource, Is.SameAs(rows)); Assert.That(sourceChanges, Is.Zero);
        Assert.That(list.ItemsSource!.OfType<Patch>().Select(p => p.FileNameA), Is.EqualTo(new[] { "a.txt", "m.txt", "z.txt" }));
        Assert.That(selections, Is.Empty, "The real grid must never publish a temporary deselection during sorting.");
        Assert.That(selectedDuringMoves, Is.Not.Empty); Assert.That(selectedDuringMoves.All(item => ReferenceEquals(item, selected)), Is.True);
        Assert.That(documentChanges, Is.Zero, "No clear/reload of the actual editor document may occur, even transiently.");
        Assert.That(caretChanges, Is.Zero); Assert.That(textSelectionChanges, Is.Zero);
        Assert.That(d.Viewer.Editor.Document, Is.SameAs(document)); Assert.That(d.Viewer.Text, Is.EqualTo(text));
        Assert.That(d.Viewer.DocumentIdentity, Is.EqualTo(identity)); Assert.That(d.Viewer.CaretOffset, Is.EqualTo(caret));
        Assert.That(d.Viewer.SelectionStart, Is.EqualTo(selectionStart)); Assert.That(d.Viewer.SelectionLength, Is.EqualTo(selectionLength));
        Assert.That(d.FocusManager!.GetFocusedElement(), Is.SameAs(list.ContainerFromIndex(2)));

        // No click, Focus(), delayed selection assignment or second arrow repairs this first key.
        Press(d, PhysicalKey.ArrowUp);
        Assert.That((list.SelectedItem as Patch)?.FileNameA, Is.EqualTo("m.txt")); Assert.That(d.Viewer.Text, Does.Contain("+middle"));
        Assert.That(selections.Count, Is.EqualTo(1)); Assert.That(selections[0].RemovedItems.Cast<object>(), Does.Contain(selected));
        Assert.That(selections[0].AddedItems.Cast<Patch>().Single().FileNameA, Is.EqualTo("m.txt"));
        Assert.That(documentChanges, Is.GreaterThan(0), "The actual arrow, rather than the header sort, publishes the next preview.");
    }

    [AvaloniaTest]
    public async Task CombinedModeOnlyBinaryAndMultiFileMetadataComeFromTheOriginalParser()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open();
        string patch = "diff --cc merged.txt\nindex aaa,bbb..ccc\n--- a/merged.txt\n+++ b/merged.txt\n@@@ -1,1 -1,1 +1,1 @@@\n++merged\n"
            + "diff --git a/executable b/executable\nold mode 100644\nnew mode 100755\n"
            + "diff --git a/image.bin b/image.bin\nnew file mode 100644\nindex 0000000..1111111\nGIT binary patch\nliteral 1\nLc$@<O00001\n";
        await h.Load(d, h.Write("variants.patch", patch));
        Assert.That(d.Patches.Count, Is.EqualTo(3)); Assert.That(d.Patches[1].ChangeType, Is.EqualTo(PatchChangeType.ChangeFileMode));
        Assert.That(d.Patches[2].FileType, Is.EqualTo(PatchFileType.Binary)); Assert.That(d.Patches[2].ChangeType, Is.EqualTo(PatchChangeType.NewFile));
        Assert.That(d.Viewer.Text, Does.Contain("@@@")); Assert.That(d.Viewer.EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
        ListBox list = Field<ListBox>(d, "GridChangedFiles"); PushProcessTestDriver.Pointer(list.ContainerFromIndex(2)!);
        Assert.That(d.Viewer.Text, Does.Contain("GIT binary patch")); Assert.That(d.Viewer.Text, Does.Not.Contain("++merged"));
    }

    [AvaloniaTest, TestCase("none"), TestCase("utf8"), TestCase("utf16")]
    public async Task BomDetectionIsTheSameAsReadAllTextLosslessAndDoesNotIntroduceABomIntoTheDiff(string kind)
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); string text = TextPatch("plain.txt", "ASCII body");
        Encoding encoding = kind == "utf16" ? Encoding.Unicode : Encoding.UTF8;
        byte[] preamble = kind == "none" ? [] : encoding.GetPreamble();
        string path = h.WriteBytes("bom.patch", [.. preamble, .. encoding.GetBytes(text)]);
        byte[] before = File.ReadAllBytes(path); await h.Load(d, path);
        Assert.That(d.Patches.Single().FileNameA, Is.EqualTo("plain.txt")); Assert.That(d.Viewer.Text, Does.StartWith("diff --git").And.Contain("+ASCII body"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task FilesEncodingDecodesNonUtf8BodyWhileGitQuotedOctetsUseUtf8Independently()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(codec: "windows-1252");
        Encoding codec = GitFileEncodingCodec.GetEncoding("windows-1252");
        string quoted = "diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n--- \"a/caf\\303\\251.txt\"\n+++ \"b/caf\\303\\251.txt\"\n@@ -1 +1 @@\n-old\n+café\n";
        byte[] bytes = codec.GetBytes(quoted); string path = h.WriteBytes("quoted.patch", bytes); await h.Load(d, path);
        Assert.That(d.Viewer.Text, Does.Contain("+café").And.Contain("café.txt"));
        Assert.That(d.Viewer.Text, Does.Not.Contain("caf\\303\\251"));
        // Do not rewrite the linked parser's filename fields or strip their quotes in the UI adapter.
        Assert.That(d.Patches.Single().Header, Does.Contain("café.txt")); Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
    }

    [AvaloniaTest]
    public async Task TheRealBrowseButtonPassesThePatchFilterAndPickerCancelKeepsSelectionIdentityAndPreview()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); await h.Load(d, h.Write("first.patch", TextPatch("first.txt", "first")));
        object selected = Field<ListBox>(d, "GridChangedFiles").SelectedItem!; string text = d.Viewer.Text, identity = d.Viewer.DocumentIdentity!;
        bool called = false;
        d.OpenFilePicker = options =>
        {
            called = true; Assert.That(options.AllowMultiple, Is.False);
            Assert.That(options.FileTypeFilter!.First().Patterns, Does.Contain("*.patch"));
            return Task.FromResult<IReadOnlyList<IStorageFile>>([]);
        };
        Click(d, "BrowsePatch"); await Until(() => !d.IsBusy);
        Assert.That(called, Is.True); Assert.That(d.LastException, Is.Null); Assert.That(Field<ListBox>(d, "GridChangedFiles").SelectedItem, Is.SameAs(selected));
        Assert.That(d.Viewer.Text, Is.EqualTo(text)); Assert.That(d.Viewer.DocumentIdentity, Is.EqualTo(identity));
        string chosen = h.Write("chosen.patch", TextPatch("chosen.txt", "chosen"));
        IStorageFile realFile = await Storage(h.Owner, chosen);
        d.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([realFile]);
        Click(d, "BrowsePatch"); await Until(() => !d.IsBusy && d.LoadedPath == chosen);
        Assert.That(d.Viewer.Text, Does.Contain("+chosen"));
    }

    [AvaloniaTest, TestCase("syntax"), TestCase("io")]
    public async Task ErrorsRemainVisibleAndNeverRelabelAnOldPreviewAsTheFailedFile(string failure)
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); string original = h.Write("good.patch", TextPatch("good.txt", "good"));
        await h.Load(d, original); object selected = Field<ListBox>(d, "GridChangedFiles").SelectedItem!; string identity = d.Viewer.DocumentIdentity!;
        string bad = h.Write("bad.patch", "not a Git patch");
        if (failure == "io")
        {
            IStorageFile selectedFile = await Storage(h.Owner, bad); File.Delete(bad);
            await d.LoadAsync(selectedFile); Pump(d);
        }
        else { await h.Load(d, bad, success: false); }
        if (failure == "io") { Assert.That(d.LastException, Is.InstanceOf<IOException>()); }
        else { Assert.That(d.LastException, Is.TypeOf<FormatException>()); }
        Assert.That(Field<TextBlock>(d, "ViewPatchMessage").Text, Is.Not.Null.And.Not.Empty);
        Assert.That(d.LoadedPath, Is.EqualTo(original)); Assert.That(d.Viewer.DocumentIdentity, Is.EqualTo(identity));
        Assert.That(Field<ListBox>(d, "GridChangedFiles").SelectedItem, Is.SameAs(selected)); Assert.That(d.Viewer.Text, Does.Contain("+good"));
    }

    [AvaloniaTest]
    public async Task AnActuallyEmptyStorageFileReplacesThePreviousDocumentWithoutInventingAPatch()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); await h.Load(d, h.Write("old.patch", TextPatch("old.txt", "old")));
        string empty = h.Write("empty.patch", ""); await h.Load(d, empty);
        Assert.That(d.Patches, Is.Empty); Assert.That(Field<ListBox>(d, "GridChangedFiles").SelectedIndex, Is.EqualTo(-1));
        Assert.That(d.LoadedPath, Is.EqualTo(empty)); Assert.That(d.Viewer.Text, Is.Empty); Assert.That(d.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task CancelARealPendingStreamKeepsTheOldPreviewAndDrainsTheOwnedRead()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); await h.Load(d, h.Write("old.patch", TextPatch("old.txt", "old")));
        string next = h.Write("next.patch", TextPatch("next.txt", "next")); TaskCompletionSource release = new();
        TrackingStream? stream = null;
        d.OpenRead = async (file, _) => { stream = new(await file.OpenReadAsync()); await release.Task; return stream; };
        Task read = d.LoadAsync(await Storage(h.Owner, next)); await Until(() => stream is not null);
        try
        {
            Assert.That(d.Viewer.Text, Does.Contain("+old")); Click(d, "ViewPatchCancelRead"); Assert.That(read.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); }
        await Until(() => read.IsCompleted && !d.IsBusy);
        Assert.That(stream!.Disposed, Is.True); Assert.That(d.Viewer.Text, Does.Contain("+old").And.Not.Contain("+next"));
        Assert.That(d.LastException, Is.Null); Assert.That(d.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task ANewReadWinsEvenWhenAnOlderRealStorageResponseIgnoresCancellationUntilLater()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); TaskCompletionSource release = new(); bool waiting = false;
        string slow = h.Write("slow.patch", TextPatch("slow.txt", "slow")), fast = h.Write("fast.patch", TextPatch("fast.txt", "fast"));
        d.OpenRead = async (file, _) => { Stream stream = await file.OpenReadAsync(); if (file.Name == "slow.patch") { waiting = true; await release.Task; } return stream; };
        Task old = d.LoadAsync(await Storage(h.Owner, slow)); await Until(() => waiting);
        try { await h.Load(d, fast); Assert.That(d.Viewer.Text, Does.Contain("+fast")); }
        finally { release.TrySetResult(); }
        await Until(() => old.IsCompleted && !d.IsBusy);
        Assert.That(d.LoadedPath, Is.EqualTo(fast)); Assert.That(d.Viewer.Text, Does.Contain("+fast").And.Not.Contain("+slow"));
    }

    [AvaloniaTest]
    public async Task ClosingWaitsForTheOwnedRealStreamBeforeDisposalAndLeavesItsOwnerUsable()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); TaskCompletionSource release = new(); TrackingStream? stream = null;
        d.OpenRead = async (file, _) => { stream = new(await file.OpenReadAsync()); await release.Task; return stream; };
        Task read = d.LoadAsync(await Storage(h.Owner, h.Write("slow.patch", TextPatch("slow.txt", "slow"))));
        await Until(() => stream is not null);
        try { d.Close(); Pump(d); Assert.That(d.IsVisible, Is.True); Assert.That(d.CloseCompletion.IsCompleted, Is.False); }
        finally { release.TrySetResult(); }
        await Until(() => read.IsCompleted && d.CloseCompletion.IsCompleted && !d.IsVisible);
        Assert.That(stream!.Disposed, Is.True); Assert.That(h.Owner.IsVisible, Is.True);
        TextBox original = Field<TextBox>(h.Owner, "OriginalOwnerText"); PushProcessTestDriver.Pointer(original); h.Owner.KeyTextInput("still usable");
        Assert.That(original.Text, Is.EqualTo("still usable"));
    }

    [AvaloniaTest]
    public async Task ClosingAlsoDrainsALateNativePickerReplyBeforeAnyStreamCanBeOpened()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); TaskCompletionSource<IReadOnlyList<IStorageFile>> picker = new(); bool opened = false, pickerStarted = false;
        d.OpenFilePicker = _ => { pickerStarted = true; return picker.Task; }; d.OpenRead = (file, _) => { opened = true; return file.OpenReadAsync(); };
        IStorageFile real = await Storage(h.Owner, h.Write("late.patch", TextPatch("late.txt", "late")));
        Click(d, "BrowsePatch"); await Until(() => pickerStarted); d.Close(); Pump(d);
        try { Assert.That(d.CloseCompletion.IsCompleted, Is.False); }
        finally { picker.TrySetResult([real]); }
        await Until(() => d.CloseCompletion.IsCompleted && !d.IsVisible); Assert.That(opened, Is.False); Assert.That(h.Owner.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task ReadOnlyViewerDoesNotWritePatchOrWorkingFilesForTypingSaveOrSelection()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open(); string target = h.Write("working.txt", "untouched worktree bytes\n");
        string patch = h.Write("view.patch", TextPatch("working.txt", "would change only if applied"));
        byte[] beforePatch = File.ReadAllBytes(patch), beforeTarget = File.ReadAllBytes(target); await h.Load(d, patch);
        // A real pointer into the viewer followed by actual text/key input; no write command exists.
        PushProcessTestDriver.Pointer(d.Viewer.Editor); d.KeyTextInput("must not edit"); Press(d, PhysicalKey.S, RawInputModifiers.Control);
        Assert.That(d.Viewer.IsReadOnly, Is.True); Assert.That(d.Viewer.IsModified, Is.False);
        Assert.That(File.ReadAllBytes(target), Is.EqualTo(beforeTarget)); Assert.That(File.ReadAllBytes(patch), Is.EqualTo(beforePatch));
        Assert.That(d.GetVisualDescendants().OfType<Button>().Any(button => button.Name is "Apply" or "Save"), Is.False);
    }

    [AvaloniaTest]
    public async Task RealCommandsMenuUsesTheWindowsRouteAndIconWithoutAnInventedShortcutThenRestoresBrowseFirstArrow()
    {
        using var f = await MainWindowClassicTests.RepositoryFixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await Until(() => !main.IsBusy);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit[] commits = history.ItemsSource!.OfType<GitCommit>().ToArray(); Assert.That(commits.Length, Is.GreaterThan(1));
        Pump(main); PushProcessTestDriver.Pointer(history.ContainerFromItem(commits[0])!);
        MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Commands");
        PushProcessTestDriver.Pointer(commands); MenuItem view = commands.Items.OfType<MenuItem>().Single(item => item.Name == "patchToolStripMenuItem");
        Assert.That(((Image)view.Icon!).Tag, Is.EqualTo("PatchView.png")); Assert.That(view.InputGesture, Is.Null);
        Assert.That(((AccessText)view.Header!).Text, Is.EqualTo("View patc_h file…"));
        MenuItem[] tail = commands.Items.OfType<MenuItem>().TakeLast(3).ToArray(); Assert.That(tail[^1], Is.SameAs(view));
        PushProcessTestDriver.Pointer(view); await Until(() => main.OwnedWindows.OfType<ViewPatchDialog>().Any(window => window.IsVisible));
        ViewPatchDialog dialog = main.OwnedWindows.OfType<ViewPatchDialog>().Single(window => window.IsVisible);
        dialog.Close(); await Until(() => dialog.CloseCompletion.IsCompleted && main.WindowWorkCompletion.IsCompleted && !main.IsBusy);
        Assert.That(main.IsVisible, Is.True); Assert.That(main.LastException, Is.Null);
        Press(main, PhysicalKey.ArrowDown); Assert.That(history.SelectedItem, Is.SameAs(commits[1]), "First Down after the modal closes must reach Browse without a corrective click or Focus().");
    }

    [AvaloniaTest]
    public async Task EnterOnTheOriginalPathFieldLoadsALiteralNameAndMissingPathKeepsThePreviousPreview()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open();
        string path = h.Write("literal [1] λ.patch", TextPatch("literal.txt", "literal"));
        TextBox input = Field<TextBox>(d, "PatchFileNameEdit");
        PushProcessTestDriver.Pointer(input); d.KeyTextInput(path); Press(d, PhysicalKey.Enter);
        await Until(() => !d.IsBusy && d.LoadedPath == path);
        Assert.That(d.Viewer.Text, Does.Contain("+literal"));
        PushProcessTestDriver.Pointer(input); Press(d, PhysicalKey.A, RawInputModifiers.Control);
        d.KeyTextInput(Path.Combine(h.Root, "absent.patch")); Press(d, PhysicalKey.Enter);
        await Until(() => !d.IsBusy);
        Assert.That(d.LastException, Is.InstanceOf<IOException>());
        Assert.That(d.LoadedPath, Is.EqualTo(path)); Assert.That(d.Viewer.Text, Does.Contain("+literal"));
    }

    [AvaloniaTest]
    public async Task EmailPreambleAndNewDeletedFilesKeepTheirWindowsMetadataAndCompletePatchText()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open();
        string content = "From abc Mon Sep 17 00:00:00 2001\nSubject: [PATCH] example\n\n"
            + "diff --git a/new.txt b/new.txt\nnew file mode 100644\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+added\n"
            + "diff --git a/old.txt b/old.txt\ndeleted file mode 100644\n--- a/old.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-removed\n";
        await h.Load(d, h.Write("mail.patch", content));
        Assert.That(d.Patches.Select(p => p.ChangeType), Is.EqualTo(new[] { PatchChangeType.NewFile, PatchChangeType.DeleteFile }));
        Assert.That(d.Viewer.Text, Does.StartWith("diff --git").And.Contain("+added").And.Not.Contain("Subject:"));
        ListBox list = Field<ListBox>(d, "GridChangedFiles"); PushProcessTestDriver.Pointer(list.ContainerFromIndex(1)!);
        Assert.That(d.Viewer.Text, Does.Contain("--- a/old.txt").And.Contain("+++ /dev/null").And.Contain("-removed"));
    }

    [Test]
    public void AlreadyCancelledParsingDoesNotStartTheLinkedIteratorOrValidateAnUnneededCodec()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GitPatchPreview.Parse(TextPatch("file.txt", "body"), "not-an-encoding", cancellation.Token));
    }

    [AvaloniaTest]
    public async Task InvalidUtf8PreviewBytesKeepTheWindowsReplacementFallbackAndNeverRewriteTheirSource()
    {
        await using Host h = new(); ViewPatchDialog d = h.Open();
        byte[] bytes = [.. Encoding.ASCII.GetBytes(TextPatch("bytes.txt", "caf").TrimEnd('\n')), 0xe9, (byte)'\n'];
        string path = h.WriteBytes("invalid-utf8.patch", bytes); await h.Load(d, path);
        Assert.That(d.LastException, Is.Null); Assert.That(d.Viewer.Text, Does.Contain("\ufffd"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
    }

    private static string TextPatch(string name, string body) => $"diff --git a/{name} b/{name}\nindex 1111111..2222222 100644\n--- a/{name}\n+++ b/{name}\n@@ -1 +1 @@\n-old\n+{body}\n";
    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static void Click(TopLevel owner, string name) { Pump(owner); PushProcessTestDriver.Pointer(Field<Button>(owner, name)); Pump(owner); }
    private static void Press(TopLevel owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } Pump(owner); }
    private static void Pump(TopLevel owner)
    { Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> ready)
    {
        Stopwatch clock = Stopwatch.StartNew(); while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(10)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(ready(), Is.True, "The owned patch preview operation did not settle.");
    }
    private static async Task<IStorageFile> Storage(Window owner, string path)
    { IStorageFile? file = await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(path)); Assert.That(file, Is.Not.Null); return file!; }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly List<ViewPatchDialog> _dialogs = [];
        private readonly TranslationCatalog _translations;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ge-view-patch-" + Guid.NewGuid().ToString("N"));
        internal Window Owner { get; } = new() { Width = 1000, Height = 750, Content = new TextBox { Name = "OriginalOwnerText" } };
        internal Host(string language = "en", bool dark = false)
        {
            Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Root, "profile"));
            _translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Owner.Show(); Pump(Owner);
        }
        internal string Write(string name, string text) => WriteBytes(name, Encoding.UTF8.GetBytes(text));
        internal string WriteBytes(string name, byte[] bytes) { string path = Path.Combine(Root, name); File.WriteAllBytes(path, bytes); return path; }
        internal ViewPatchDialog Open(string codec = "utf-8")
        { ViewPatchDialog dialog = new(codec, _translations); _dialogs.Add(dialog); _ = dialog.ShowDialog(Owner); Pump(dialog); return dialog; }
        internal async Task Load(ViewPatchDialog dialog, string path, bool success = true)
        {
            await dialog.LoadAsync(await Storage(Owner, path)); Pump(dialog);
            if (success) { Assert.That(dialog.LastException, Is.Null, dialog.LastException?.ToString()); }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (ViewPatchDialog dialog in _dialogs)
                { dialog.Close(); await Until(() => dialog.CloseCompletion.IsCompleted && !dialog.IsVisible); dialog.Dispose(); }
                Owner.Close(); Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                if (_dialogs.All(dialog => dialog.CloseCompletion.IsCompleted && !dialog.IsVisible && !dialog.IsBusy))
                { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config); Directory.Delete(Root, true); }
                else { TestContext.Progress.WriteLine("Preserved undrained ViewPatch fixture and its private XDG profile: " + Root); }
            }
        }
    }

    private sealed class TrackingStream(Stream inner) : Stream
    {
        internal bool Disposed { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; inner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { Disposed = true; await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
