using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommandCacheWindowTests
{
    private const string Separator = "\n-------------------------------------\n\n";

    [Test]
    public void PrintableCharactersPreserveTheWindowsOrderAndDoNotApplyTheJournalTxtEscaper()
    {
        Assert.That(GitCommandCachePresentation.PrintableChars(null), Is.Null);
        Assert.That(GitCommandCachePresentation.PrintableChars(""), Is.Empty);
        const string input = "\0\r\n\t \u001b[31m literal\\n Ω\u00a0";
        const string printable = "\\0\\r\\n\n»·\\x1b[31m·literal\\n·Ω\u00a0";
        Assert.That(GitCommandCachePresentation.PrintableChars(input), Is.EqualTo(printable));
        GitCommandCacheEntry entry = new(1, "cat-file blob literal key", "display", input, "stderr\t\r\n");
        Assert.That(GitCommandCachePresentation.Detail(entry), Is.EqualTo("cat-file blob literal key" + Separator + printable + Separator + "stderr»\\r\\n\n"));
        Assert.That(entry.StandardOutput, Is.EqualTo(input)); Assert.That(entry.StandardError, Is.EqualTo("stderr\t\r\n"));
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task RealBlobResultsAndHitsReachTheSecondTabWithTheWindowsMenuAndExactDetail(string language, bool dark)
    {
        await using Fixture f = await Fixture.Create();
        byte[] first = await f.Blob(0); GitCommandCacheEntry cached = f.CachedBlob(0);
        Assert.That(first, Is.EqualTo(Encoding.UTF8.GetBytes(Payload(0))));
        int nativeCatFiles = NativeCount("git cat-file"); Assert.That(nativeCatFiles, Is.GreaterThan(0));
        byte[] hit = await f.Blob(0);
        Assert.That(hit, Is.EqualTo(first)); Assert.That(f.CachedBlob(0).Id, Is.EqualTo(cached.Id));
        Assert.That(NativeCount("git cat-file"), Is.EqualTo(nativeCatFiles), "The target cat-file must hit; revision/context reads can still run independently.");
        GitCommandLogWindow log = await f.Open(language, dark); ListBox journal = Field<ListBox>(log, "LogItems"); object? journalSource = journal.ItemsSource;
        TabItem[] pages = Tabs(log).Items.OfType<TabItem>().ToArray();
        Assert.That(pages.Select(page => page.Name), Is.EqualTo(new[] { "tabPageCommandLog", "tabPageCommandCache" }));
        Assert.That(pages[1].Header, Is.EqualTo(language == "fr" ? "Cache de commandes" : "Command cache"));
        Switch(log, 1); ListBox list = CacheList(log); Select(log, cached.Id);
        Assert.That(CacheText(log).Text, Is.EqualTo(cached.Key + Separator + PrintablePayload(0) + Separator));
        Assert.That(Entry(list.SelectedItem!).StandardOutput, Is.EqualTo(Payload(0)));
        Assert.That(Field<GridSplitter>(log, "CommandCacheSplitter").ResizeDirection, Is.EqualTo(GridResizeDirection.Rows));
        Grid split = Field<Grid>(log, "CommandCacheSplit");
        Assert.That(split.RowDefinitions[0].Height.Value, Is.EqualTo(183)); Assert.That(split.RowDefinitions[2].Height.Value, Is.EqualTo(231));
        MenuItem clear = list.ContextMenu!.Items.OfType<MenuItem>().Single();
        Assert.That(clear.Name, Is.EqualTo("tsmiClearCache")); Assert.That(((Image)clear.Icon!).Tag, Is.EqualTo("ClearLog.png"));
        Assert.That(clear.InputGesture!.Key, Is.EqualTo(Key.L)); Assert.That(clear.InputGesture.KeyModifiers, Is.EqualTo(KeyModifiers.Control));
        Assert.That(((AccessText)clear.Header!).Text, Is.EqualTo(language == "fr" ? "_Effacer" : "C_lear"));
        Assert.That(pages[1].ContextMenu, Is.Not.SameAs(list.ContextMenu)); Assert.That(pages[1].ContextMenu!.Items.Count, Is.EqualTo(1));
        string material = f.Material(); Dictionary<string, byte[]> profile = f.Profile.Material();
        RightClick(list); Assert.That(list.ContextMenu.IsOpen, Is.True); Press(log, PhysicalKey.Escape);
        Assert.That(log.IsVisible, Is.True); Assert.That(list.ContextMenu.IsOpen, Is.False);
        Switch(log, 0); Assert.That(Field<ListBox>(log, "LogItems"), Is.SameAs(journal)); Assert.That(journal.ItemsSource, Is.SameAs(journalSource));
        f.Profile.AssertMaterial(profile); Assert.That(f.Material(), Is.EqualTo(material));
        using var frame = log.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
    }

    [AvaloniaTest]
    public async Task TreeAndCommitDetailConsumersPublishTheirActualResultsAndRetainTheTargetHits()
    {
        await using Fixture f = await Fixture.Create();
        IReadOnlyList<GitTreeEntry> tree = await f.Repository.GetTreeAsync(f.Hash);
        GitCommandCacheEntry treeEntry = GitCommandCache.Shared.Snapshot().Single(entry => entry.DisplayString.Contains("ls-tree", StringComparison.Ordinal));
        Assert.That(treeEntry.StandardOutput, Does.Contain("\tblob-000.txt\0")); Assert.That(tree.Any(entry => entry.Path == "blob-000.txt"), Is.True);
        int nativeTree = NativeCount("git ls-tree"); await f.Repository.GetTreeAsync(f.Hash);
        Assert.That(NativeCount("git ls-tree"), Is.EqualTo(nativeTree), "Count the target, not uncached revision/context commands.");
        GitCommitDetails details = await f.Repository.GetCommitDetailsAsync(f.Hash, includeNotes: false);
        GitCommandCacheEntry message = GitCommandCache.Shared.Snapshot().Single(entry => entry.DisplayString.Contains("--format=format:%s%x00%b%x00%B", StringComparison.Ordinal));
        Assert.That(message.StandardOutput, Does.Contain(details.Subject)); int nativeShow = NativeCount("git show");
        await f.Repository.GetCommitDetailsAsync(f.Hash, includeNotes: false);
        Assert.That(NativeCount("git show"), Is.EqualTo(nativeShow));
        GitCommandLogWindow log = await f.Open(); Switch(log, 1); Select(log, treeEntry.Id);
        Assert.That(CacheText(log).Text, Does.Contain("»blob-000.txt\\0")); Select(log, message.Id);
        Assert.That(CacheText(log).Text, Does.Contain("Command·cache·actual·reads"));
    }

    [AvaloniaTest]
    public async Task ClearKeyAndMenuAreIndependentForEachRealStoreAndRereadingAfterClearMisses()
    {
        await using Fixture f = await Fixture.Create(); await f.Blob(0); GitCommandLogWindow log = await f.Open();
        long[] journal = GitCommandLog.Snapshot().Select(entry => entry.Id).ToArray(); Assert.That(journal, Is.Not.Empty);
        string material = f.Material(); Dictionary<string, byte[]> profile = f.Profile.Material();
        Switch(log, 1); Select(log, f.CachedBlob(0).Id); Press(log, PhysicalKey.L, RawInputModifiers.Control);
        Assert.That(GitCommandCache.Shared.Snapshot(), Is.Empty); Assert.That(CacheList(log).Items, Is.Empty); Assert.That(CacheText(log).Text, Is.Empty);
        Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Id), Is.EqualTo(journal));
        int before = NativeCount("git cat-file"); await f.Blob(0); await Until(() => CacheList(log).Items.Count == 1);
        Assert.That(NativeCount("git cat-file"), Is.EqualTo(before + 1)); long[] cache = GitCommandCache.Shared.Snapshot().Select(entry => entry.Id).ToArray();
        Switch(log, 0); ListBox logItems = Field<ListBox>(log, "LogItems"); PointAt(logItems, 0); Press(log, PhysicalKey.L, RawInputModifiers.Control);
        Assert.That(GitCommandLog.Snapshot(), Is.Empty); Assert.That(GitCommandCache.Shared.Snapshot().Select(entry => entry.Id), Is.EqualTo(cache));
        Switch(log, 1); ListBox list = CacheList(log); RightClick(list); Assert.That(list.ContextMenu!.IsOpen, Is.True);
        PushProcessTestDriver.Pointer(list.ContextMenu.Items.OfType<MenuItem>().Single()); Pump(log);
        Assert.That(GitCommandCache.Shared.Snapshot(), Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        Assert.That(list.Items, Is.Empty); Assert.That(CacheText(log).Text, Is.Empty); f.Profile.AssertMaterial(profile); Assert.That(f.Material(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task CacheCtrlCUsesOnlyTheRealDetailSelectionAndCtrlSNeverExportsTheOldJournalSelection()
    {
        await using Fixture f = await Fixture.Create(); await f.Blob(0); GitCommandLogWindow log = await f.Open();
        ListBox journal = Field<ListBox>(log, "LogItems"); PointAt(journal, 0); object? oldJournalSelection = journal.SelectedItem;
        var clipboard = log.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            int saves = 0; log.SaveFilePicker = _ => { saves++; return Task.FromResult<Avalonia.Platform.Storage.IStorageFile?>(null); };
            Switch(log, 1); Select(log, f.CachedBlob(0).Id); await clipboard.SetTextAsync("clipboard-before-cache");
            Press(log, PhysicalKey.C, RawInputModifiers.Control); Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("clipboard-before-cache"));
            TextBox detail = CacheText(log); PushProcessTestDriver.Pointer(detail);
            int start = detail.Text!.IndexOf("cache-000", StringComparison.Ordinal); Assert.That(start, Is.GreaterThanOrEqualTo(0));
            detail.SelectionStart = start; detail.SelectionEnd = start + "cache-000".Length; Pump(log);
            Press(log, PhysicalKey.C, RawInputModifiers.Control); await Until(async () => await ClipboardExtensions.TryGetTextAsync(clipboard) == "cache-000");
            Assert.That(detail.IsKeyboardFocusWithin, Is.True); Assert.That(journal.SelectedItem, Is.SameAs(oldJournalSelection));
            detail.SelectionStart = detail.SelectionEnd = 0; Press(log, PhysicalKey.C, RawInputModifiers.Control);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("cache-000"));
            Task work = log.Work; Press(log, PhysicalKey.S, RawInputModifiers.Control);
            Assert.That(saves, Is.Zero); Assert.That(log.Work, Is.SameAs(work)); Assert.That(detail.IsKeyboardFocusWithin, Is.True);
            Switch(log, 0); PointAt(journal, 0); Press(log, PhysicalKey.S, RawInputModifiers.Control); await log.Work;
            Assert.That(saves, Is.EqualTo(1), "The journal Save gesture must remain functional.");
        }
        finally { if (previous is not null) { await clipboard.SetTextAsync(previous); } }
    }

    [AvaloniaTest]
    public async Task WorkerUpdatesAndHiddenTabsPreserveOldRowsCaretScrollAndTheFirstArrows()
    {
        await using Fixture f = await Fixture.Create(); await f.Blob(0); await f.Blob(1);
        GitCommandLogWindow log = await f.Open(); ListBox journal = Field<ListBox>(log, "LogItems"); PointAt(journal, 0);
        object? journalSource = journal.ItemsSource, oldJournal = journal.SelectedItem;
        Switch(log, 1); ListBox list = CacheList(log); Select(log, f.CachedBlob(0).Id);
        object? source = list.ItemsSource, first = list.SelectedItem; object second = list.Items[1]!;
        await Task.Run(() => f.Blob(2)); await Until(() => list.Items.Count == 3); Pump(log);
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(first)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowDown); Assert.That(list.SelectedItem, Is.SameAs(second)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(first)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.End); long last = Entry(list.SelectedItem!).Id; await Task.Run(() => f.Blob(3)); await Until(() => list.Items.Count == 4); Pump(log);
        Assert.That(Entry(list.SelectedItem!).Id, Is.Not.EqualTo(last)); Assert.That(list.SelectedIndex, Is.EqualTo(3)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        await f.LongBlob(); await Until(() => list.Items.Count == 5);
        await f.Blob(4); await Until(() => list.Items.Count == 6); Select(log, f.CachedLong().Id);
        Assert.That(list.SelectedIndex, Is.EqualTo(list.Items.Count - 2), "The inspected detail must be an old row rather than the following-last selection.");
        TextBox detail = CacheText(log); PushProcessTestDriver.Pointer(detail); int caret = detail.Text!.IndexOf("long-result", StringComparison.Ordinal);
        detail.SelectionStart = caret; detail.SelectionEnd = caret + 11; Pump(log);
        ScrollViewer scroll = DetailScroll(detail); scroll.Offset = new Vector(0, 40); Pump(log);
        string? text = detail.Text, selected = detail.SelectedText; Vector offset = scroll.Offset; object? selectedRow = list.SelectedItem;
        await Task.Run(() => f.Profile.Git.GitAsync("status", "--porcelain")); Dispatcher.UIThread.RunJobs(); Pump(log);
        AssertDetailUnchanged(); await Task.Run(() => f.Blob(5)); await Until(() => list.Items.Count == 7); Pump(log); AssertDetailUnchanged();
        Switch(log, 0); Assert.That(Field<ListBox>(log, "LogItems"), Is.SameAs(journal)); Assert.That(journal.ItemsSource, Is.SameAs(journalSource)); Assert.That(journal.SelectedItem, Is.SameAs(oldJournal));
        await Task.Run(() => f.Blob(6)); Dispatcher.UIThread.RunJobs(); Pump(log);
        Switch(log, 1); Assert.That(CacheList(log), Is.SameAs(list)); Assert.That(list.ItemsSource, Is.SameAs(source));
        Assert.That(list.SelectedItem, Is.SameAs(selectedRow)); Assert.That(CacheText(log), Is.SameAs(detail)); Assert.That(detail.Text, Is.EqualTo(text));

        void AssertDetailUnchanged()
        {
            Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(selectedRow));
            Assert.That(detail.Text, Is.EqualTo(text)); Assert.That(detail.SelectedText, Is.EqualTo(selected));
            Assert.That(scroll.Offset, Is.EqualTo(offset)); Assert.That(detail.IsKeyboardFocusWithin, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task CapacityPreservesTheInspectedIdThenSelectsTheClampedNeighborAfterItsActualEviction()
    {
        await using Fixture f = await Fixture.Create();
        for (int index = 0; index < 50; index++) { await f.Blob(index); }
        Assert.That(GitCommandCache.Shared.Snapshot().Count, Is.EqualTo(50));
        GitCommandLogWindow log = await f.Open(); Switch(log, 1); ListBox list = CacheList(log); Select(log, f.CachedBlob(25).Id);
        object? source = list.ItemsSource, selected = list.SelectedItem; object next = list.Items[26]!, afterNext = list.Items[27]!;
        long victim = f.CachedBlob(0).Id, selectedId = Entry(selected!).Id;
        await Task.Run(() => f.Blob(50)); await Until(() => list.Items.Count == 50 && list.Items.OfType<object>().All(row => Entry(row).Id != victim)); Pump(log);
        Assert.That(list.SelectedItem, Is.SameAs(selected)); Assert.That(list.SelectedIndex, Is.EqualTo(24)); Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowDown); Assert.That(list.SelectedItem, Is.SameAs(next)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(selected)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        // Touch every other admitted result via real consumer hits, keeping the
        // inspected middle row the oldest without deleting earlier table rows.
        foreach (GitCommandCacheEntry entry in GitCommandCache.Shared.Snapshot().Where(entry => entry.Id != selectedId))
        { int index = int.Parse(entry.StandardOutput.Substring(6, 3), CultureInfo.InvariantCulture); await f.Blob(index); }
        int oldIndex = list.SelectedIndex; Assert.That(oldIndex, Is.GreaterThan(0));
        await Task.Run(() => f.Blob(51)); await Until(() => list.Items.OfType<object>().All(row => Entry(row).Id != selectedId)); Pump(log);
        Assert.That(GitCommandCache.Shared.Snapshot().All(entry => entry.Id != selectedId), Is.True);
        Assert.That(list.SelectedIndex, Is.EqualTo(oldIndex)); Assert.That(list.SelectedItem, Is.SameAs(next)); Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowDown); Assert.That(list.SelectedItem, Is.SameAs(afterNext)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(next)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task EqualCommandKeysInTwoRealRepositoriesKeepTheirDistinctCacheIdentities()
    {
        await using Fixture f = await Fixture.Create(); await f.Blob(0); GitCommandCacheEntry first = f.CachedBlob(0);
        string clone = f.Profile.Git.Output("other-cache-repository"); await f.Profile.Git.GitAsync("clone", "--no-local", f.Repository.Path, clone);
        GitRepository other = new(clone); await other.GetFileBytesAsync("blob-000.txt", f.Hash);
        GitCommandCacheEntry second = GitCommandCache.Shared.Snapshot().Single(entry => entry.Key == first.Key && entry.Id != first.Id);
        Assert.That(second.StandardOutput, Is.EqualTo(first.StandardOutput)); Assert.That(second.DisplayString, Is.EqualTo(first.DisplayString));
        GitCommandLogWindow log = await f.Open(); Switch(log, 1); Select(log, first.Id); object? selected = CacheList(log).SelectedItem;
        await Task.Run(() => other.GetFileBytesAsync("blob-001.txt", f.Hash)); Pump(log);
        Assert.That(CacheList(log).SelectedItem, Is.SameAs(selected)); Assert.That(Entry(selected!).Id, Is.EqualTo(first.Id));
        Select(log, second.Id); Assert.That(Entry(CacheList(log).SelectedItem!).Id, Is.EqualTo(second.Id));
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("en", true)] [TestCase("fr", false)] [TestCase("fr", true)]
    public async Task TheActualCachePresenterWrapsAndKeepsMono12WithTheSharedOptionsAndCustomApplicationFonts(string language, bool dark)
    {
        await using Fixture f = await Fixture.Create(); await f.LongBlob();
        DesktopFontSession fonts = new(new FontPreferences { Application = new("DejaVu Serif", 12, true, true) }, null);
        GitCommandLogWindow log = await f.Open(language, dark, fonts); TextBox journal = Field<TextBox>(log, "GitCommandLogText");
        Switch(log, 1); Select(log, f.CachedLong().Id); TextBox detail = CacheText(log); ListBox list = CacheList(log);
        object? source = list.ItemsSource, selected = list.SelectedItem; string text = detail.Text!; int logical = text.Split('\n').Length;
        CheckBox wrap = Field<CheckBox>(log, "chkWordWrap"); ScrollViewer scroll = DetailScroll(detail);
        Assert.That(fonts.For(DesktopFontRole.Application).Family, Is.EqualTo(new FontFamily("DejaVu Serif"))); AssertFixed(); int lines = AssertWrapped();
        PushProcessTestDriver.Pointer(wrap); Pump(log); Assert.That(detail.TextWrapping, Is.EqualTo(TextWrapping.NoWrap)); Assert.That(journal.TextWrapping, Is.EqualTo(TextWrapping.NoWrap));
        Assert.That(scroll.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Auto)); Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width + 10));
        Assert.That(detail.GetLineCount(), Is.EqualTo(logical)); AssertFixed();
        PushProcessTestDriver.Pointer(wrap); Pump(log); Assert.That(AssertWrapped(), Is.EqualTo(lines));
        log.Width = 440; Pump(log); Assert.That(AssertWrapped(), Is.GreaterThan(lines)); log.Width = 659; Pump(log); Assert.That(AssertWrapped(), Is.EqualTo(lines));
        PushProcessTestDriver.Pointer(detail); Pump(log); Assert.That(detail.IsKeyboardFocusWithin, Is.True); AssertFixed();
        log.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark; Pump(log); AssertFixed();
        CheckBox top = Field<CheckBox>(log, "chkAlwaysOnTop"); PushProcessTestDriver.Pointer(top); Assert.That(log.Topmost, Is.True);
        Assert.That(CacheText(log), Is.SameAs(detail)); Assert.That(detail.Text, Is.EqualTo(text)); Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(selected));
        using var frame = log.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);

        int AssertWrapped()
        {
            TextPresenter presenter = Presenter(detail); Assert.That(wrap.IsChecked, Is.True); Assert.That(presenter.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(scroll.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Disabled)); Assert.That(scroll.Viewport.Width, Is.GreaterThan(0));
            Assert.That(double.IsFinite(presenter.TextLayout.MaxWidth), Is.True); Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(scroll.Viewport.Width + 1));
            int count = detail.GetLineCount(); Assert.That(count, Is.GreaterThan(logical)); Assert.That(presenter.TextLayout.TextLines.Count, Is.EqualTo(count)); return count;
        }
        void AssertFixed()
        {
            TextPresenter presenter = Presenter(detail); Assert.That(presenter.FontFamily, Is.EqualTo(new FontFamily("DejaVu Sans Mono")));
            Assert.That(presenter.FontSize, Is.EqualTo(12)); Assert.That(presenter.FontWeight, Is.EqualTo(FontWeight.Normal)); Assert.That(presenter.FontStyle, Is.EqualTo(FontStyle.Normal));
            ListBoxItem row = (ListBoxItem)list.ContainerFromIndex(list.SelectedIndex)!;
            TextBlock actual = row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == Entry(list.SelectedItem!).DisplayString);
            Assert.That(actual.FontFamily, Is.EqualTo(new FontFamily("DejaVu Sans Mono"))); Assert.That(actual.FontSize, Is.EqualTo(12));
            Assert.That(actual.FontWeight, Is.EqualTo(FontWeight.Normal)); Assert.That(actual.FontStyle, Is.EqualTo(FontStyle.Normal));
        }
    }

    [AvaloniaTest]
    public async Task ClosingUnsubscribesBothSourcesAndAReopenedRealWindowShowsOnlyTheCurrentSnapshot()
    {
        await using Fixture f = await Fixture.Create(); await f.Blob(0); GitCommandLogWindow log = await f.Open(); Switch(log, 1);
        ListBox oldList = CacheList(log); TextBox oldDetail = CacheText(log); object[] oldRows = oldList.Items.OfType<object>().ToArray(); string? text = oldDetail.Text;
        log.Close(); await log.Work; await Task.Run(() => f.Blob(1)); Dispatcher.UIThread.RunJobs();
        Assert.That(log.IsVisible, Is.False); Assert.That(oldList.Items.OfType<object>(), Is.EqualTo(oldRows)); Assert.That(oldDetail.Text, Is.EqualTo(text));
        GitCommandLogWindow reopened = await f.Open(); Switch(reopened, 1);
        Assert.That(CacheList(reopened).Items.Count, Is.EqualTo(GitCommandCache.Shared.Snapshot().Count));
        Assert.That(CacheList(reopened), Is.Not.SameAs(oldList)); Assert.That(reopened.IsVisible, Is.True);
    }

    private static string Payload(int index) => $"cache-{index:D3} λ \\literal\\n\0\r\n\t \u001b[31m Ω\u00a0\n";
    private static string PrintablePayload(int index) => $"cache-{index:D3}·λ·\\literal\\n\\0\\r\\n\n»·\\x1b[31m·Ω\u00a0\\n\n";
    private static GitCommandCacheEntry Entry(object row) => (GitCommandCacheEntry)row.GetType().GetProperty("Entry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
    private static int NativeCount(string command) => GitCommandLog.Snapshot().Count(entry => entry.Command == command);
    private static TabControl Tabs(GitCommandLogWindow log) => Field<TabControl>(log, "CommandLogTabs");
    private static ListBox CacheList(GitCommandLogWindow log) => Field<ListBox>(log, "CommandCacheItems");
    private static TextBox CacheText(GitCommandLogWindow log) => Field<TextBox>(log, "commandCacheOutput");
    private static TextPresenter Presenter(TextBox detail) => detail.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
    private static ScrollViewer DetailScroll(TextBox detail) => detail.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "PART_ScrollViewer");
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Switch(GitCommandLogWindow log, int index)
    { PushProcessTestDriver.Pointer(Tabs(log).Items.OfType<TabItem>().ElementAt(index)); Pump(log); Assert.That(Tabs(log).SelectedIndex, Is.EqualTo(index)); }
    private static void Select(GitCommandLogWindow log, long id)
    { ListBox list = CacheList(log); int index = list.Items.OfType<object>().Select((row, index) => (row, index)).Single(pair => Entry(pair.row).Id == id).index; PointAt(list, index); Assert.That(Entry(list.SelectedItem!).Id, Is.EqualTo(id)); }
    private static void PointAt(ListBox list, int index)
    { object row = list.Items[index]!; list.ScrollIntoView(row); Pump((TopLevel)TopLevel.GetTopLevel(list)!); PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(index)!); Assert.That(list.IsKeyboardFocusWithin, Is.True); }
    private static void RightClick(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(point); top.MouseDown(point, MouseButton.Right); top.MouseUp(point, MouseButton.Right); Pump(top);
    }
    private static void Press(TopLevel top, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { top.KeyPressQwerty(key, modifiers); top.KeyReleaseQwerty(key, modifiers); Pump(top); }
    private static void Pump(TopLevel top) => DetailedSettingsUiHost.Pump(top);
    private static Task Until(Func<bool> predicate) => DetailedSettingsUiHost.Until(predicate);
    private static async Task Until(Func<Task<bool>> predicate)
    { for (int count = 0; count < 100; count++) { Dispatcher.UIThread.RunJobs(); if (await predicate()) { return; } await Task.Delay(10); } Assert.That(await predicate(), Is.True); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal DetailedSettingsTestScope Profile { get; }
        internal GitRepository Repository => Profile.Repository!;
        internal string Hash { get; }
        private readonly List<GitCommandLogWindow> _windows = [];
        private readonly GitCommandLogCapturePolicy _policy;
        private Fixture(DetailedSettingsTestScope profile, string hash, GitCommandLogCapturePolicy policy) { Profile = profile; Hash = hash; _policy = policy; }
        internal static async Task<Fixture> Create()
        {
            GitCommandLogCapturePolicy policy = GitCommandLog.CapturePolicy; DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
            try
            {
                for (int index = 0; index <= 51; index++) { await File.WriteAllTextAsync(profile.Git.PathInRepository($"blob-{index:D3}.txt"), Payload(index), new UTF8Encoding(false)); }
                await File.WriteAllTextAsync(profile.Git.PathInRepository("long-cache.txt"), string.Join('/', Enumerable.Repeat("long-result for actual wrapping λ", 180)), new UTF8Encoding(false));
                await profile.Repository!.StageAllAsync(); await profile.Git.GitAsync("commit", "-m", "Command cache actual reads");
                string hash = (await profile.Git.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
                GitCommandCache.Shared.Clear(); GitCommandLog.Clear(); return new(profile, hash, policy);
            }
            catch { GitCommandCache.Shared.Clear(); GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(policy); profile.Dispose(); throw; }
        }
        internal Task<byte[]> Blob(int index) => Repository.GetFileBytesAsync($"blob-{index:D3}.txt", Hash + "^0");
        internal Task<byte[]> LongBlob() => Repository.GetFileBytesAsync("long-cache.txt", Hash + "^0");
        internal GitCommandCacheEntry CachedBlob(int index) => GitCommandCache.Shared.Snapshot().Single(entry => entry.StandardOutput == Payload(index));
        internal GitCommandCacheEntry CachedLong() => GitCommandCache.Shared.Snapshot().Single(entry => entry.DisplayString.Contains("long-cache.txt", StringComparison.Ordinal));
        internal async Task<GitCommandLogWindow> Open(string language = "en", bool dark = false, DesktopFontSession? fonts = null)
        {
            GitCommandLogWindow log = new(TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language)))
                { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            _windows.Add(log);
            try
            {
                if (fonts is not null) { log.Styles.Add(ClassicStyles.Create(fonts)); }
                log.Show(); await Until(() => !log.IsBusy); Pump(log); return log;
            }
            catch { if (log.IsVisible) { log.Close(); } await log.Work; throw; }
        }
        internal string Material() => string.Join('\n', Directory.EnumerateFiles(Repository.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Repository.Path, path) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (GitCommandLogWindow log in _windows.AsEnumerable().Reverse())
                { foreach (Window child in log.OwnedWindows.ToArray()) { child.Close(); } if (log.IsVisible) { log.Close(); } await log.Work; }
            }
            finally { GitCommandCache.Shared.Clear(); GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(_policy); Profile.Dispose(); }
        }
    }
}
