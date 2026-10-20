using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitCommandLogWindowTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-10-20T12:34:56Z", CultureInfo.InvariantCulture);

    [TestCase("en-US", ",")]
    [TestCase("fr-FR", ";")]
    public void CsvExportPreservesUnicodeQuotesCarriageReturnsAndTheCultureSeparator(string culture, string separator)
    {
        GitCommandLogEntry entry = new("git status", "répertoire,;\"quoted\"\r\nnext\tpart", Started, TimeSpan.FromMilliseconds(12.5), 7, true) { Id = 1, ProcessId = 42 };
        string expected = "2026-10-20T12:34:56.0000000+00:00" + separator + "12.5" + separator + "42" + separator + "7" + separator + "true" + separator + "false" + separator + "git status" + separator + "\"répertoire,;\"\"quoted\"\"\r\nnext\tpart\"" + separator + separator + "\n";
        Assert.That(GitCommandLogPresentation.Export([entry], true, CultureInfo.GetCultureInfo(culture)), Is.EqualTo(expected));
        Assert.That(entry.Directory, Is.EqualTo("répertoire,;\"quoted\"\r\nnext\tpart"));
    }

    [Test]
    public void TextExportEscapesControlCharactersAndZeroOrFiveHundredRowsDoNotInventContent()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        Assert.That(GitCommandLogPresentation.Export([], false, culture), Is.Empty); Assert.That(GitCommandLogPresentation.Export([], true, culture), Is.Empty);
        GitCommandLogEntry running = new("git log", "λ\\folder\tpart\r\nnext", Started, TimeSpan.Zero, null, false) { Id = 1, IsRunning = true };
        Assert.That(GitCommandLogPresentation.Export([running], false, culture), Is.EqualTo("2026-10-20T12:34:56.0000000+00:00\t\t\t\tfalse\ttrue\tgit log\tλ\\\\folder\\tpart\\r\\nnext\t\t\n"));
        GitCommandLogEntry[] rows = Enumerable.Range(1, 500).Select(index => running with { Id = index }).ToArray();
        string txt = GitCommandLogPresentation.Export(rows, false, culture), csv = GitCommandLogPresentation.Export(rows, true, culture);
        Assert.That(txt.Count(character => character == '\n'), Is.EqualTo(500));
        Assert.That(csv.Count(character => character == '\n'), Is.EqualTo(1000), "The actual embedded CRLF is retained in every quoted CSV field.");
        Assert.That(rows.All(entry => entry.IsRunning && entry.ExitCode is null && entry.Duration == TimeSpan.Zero), Is.True);
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalProportionsMenuOrderIconsAndLocalOptionsUseFixedMonospaceWithoutProfileWrites(string language, bool dark)
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open(language, dark); f.Add("status"); await Ready(log, 1);
        Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(log.Width, Is.EqualTo(659)); Assert.That(log.Height, Is.EqualTo(470)); Assert.That(log.MinWidth, Is.EqualTo(400)); Assert.That(log.MinHeight, Is.EqualTo(240));
        Assert.That(log.ShowInTaskbar, Is.True); Assert.That(Field<TabControl>(log, "CommandLogTabs").Items.Count, Is.EqualTo(1));
        Assert.That(Field<TabItem>(log, "tabPageCommandLog").Header, Is.EqualTo(language == "fr" ? "Journal des commandes" : "Command log"));
        Assert.That(Field<GridSplitter>(log, "CommandLogSplitter").ResizeDirection, Is.EqualTo(GridResizeDirection.Rows));
        ListBox list = List(log); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        MenuItem[] menu = list.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Assert.That(menu.Select(item => item.Name), Is.EqualTo(new[] { "mnuSaveToFile", "mnuCopyCommandLine", "mnuClear" }));
        Assert.That(menu.Select(item => ((Image)item.Icon!).Tag), Is.EqualTo(new[] { "SaveAs.png", "CopyToClipboard.png", "ClearLog.png" }));
        Assert.That(menu.Select(item => item.InputGesture!.Key), Is.EqualTo(new[] { Key.S, Key.C, Key.L })); Assert.That(menu.All(item => item.InputGesture!.KeyModifiers == KeyModifiers.Control), Is.True);
        string copy = ((AccessText)menu[1].Header!).Text!; Assert.That(copy, Is.EqualTo(language == "fr" ? "Copier" : "Copy"));
        Assert.That(copy, Does.Not.Contain("complète").And.Not.Contain("complete"));
        TextBox detail = Detail(log); Assert.That(detail.FontFamily, Is.EqualTo(new FontFamily("DejaVu Sans Mono"))); Assert.That(detail.FontSize, Is.EqualTo(12));
        Assert.That(detail.FontWeight, Is.EqualTo(FontWeight.Normal)); Assert.That(detail.FontStyle, Is.EqualTo(FontStyle.Normal));
        CheckBox top = Field<CheckBox>(log, "chkAlwaysOnTop"), wrap = Field<CheckBox>(log, "chkWordWrap");
        Assert.That(top.IsChecked, Is.False); Assert.That(wrap.IsChecked, Is.True); Assert.That(detail.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
        PushProcessTestDriver.Pointer(top); PushProcessTestDriver.Pointer(wrap);
        Assert.That(log.Topmost, Is.True); Assert.That(detail.TextWrapping, Is.EqualTo(TextWrapping.NoWrap));
        Assert.That(log.GetVisualDescendants().OfType<CheckBox>().Count(), Is.EqualTo(3)); f.Profile.AssertMaterial(before);
        using var frame = log.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("en", true)] [TestCase("fr", false)] [TestCase("fr", true)]
    public async Task WordWrapReflowsTheActualDetailViewportAndReturnsToHorizontalScrollingWhenUnchecked(string language, bool dark)
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open(language, dark);
        f.Add("status", "/literal-working-directory/" + string.Join('/', Enumerable.Repeat("long-directory-for-wrapping", 24)));
        await Ready(log, 1); TextBox detail = Detail(log); CheckBox wrap = Field<CheckBox>(log, "chkWordWrap");
        ListBox list = List(log); object? source = list.ItemsSource, selected = list.SelectedItem;
        string text = detail.Text!; int logicalLines = text.Split('\n').Length;
        Dictionary<string, byte[]> profile = f.Profile.Material(); string repository = f.Material();
        ScrollViewer scroll = detail.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "PART_ScrollViewer");
        TextPresenter presenter = detail.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
        int wrappedLines = AssertWrapped();
        PushProcessTestDriver.Pointer(wrap); Pump(log);
        Assert.That(wrap.IsChecked, Is.False); Assert.That(presenter.TextWrapping, Is.EqualTo(TextWrapping.NoWrap));
        Assert.That(scroll.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Auto));
        Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width + 10), "The unchecked detail must really scroll horizontally.");
        Assert.That(detail.GetLineCount(), Is.EqualTo(logicalLines)); Assert.That(presenter.TextLayout.TextLines.Count, Is.EqualTo(logicalLines));
        PushProcessTestDriver.Pointer(wrap); Pump(log); Assert.That(AssertWrapped(), Is.EqualTo(wrappedLines));
        log.Width = 440; Pump(log); Assert.That(AssertWrapped(), Is.GreaterThan(wrappedLines), "A narrower real viewport must compose more visual lines.");
        log.Width = 659; Pump(log); Assert.That(AssertWrapped(), Is.EqualTo(wrappedLines));
        Assert.That(Detail(log), Is.SameAs(detail)); Assert.That(detail.Text, Is.EqualTo(text));
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(selected));
        f.Profile.AssertMaterial(profile); Assert.That(f.Material(), Is.EqualTo(repository));
        using var frame = log.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);

        int AssertWrapped()
        {
            Assert.That(wrap.IsChecked, Is.True); Assert.That(presenter.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(scroll.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Disabled));
            Assert.That(scroll.Viewport.Width, Is.GreaterThan(0)); Assert.That(double.IsFinite(presenter.TextLayout.MaxWidth), Is.True);
            Assert.That(presenter.TextLayout.MaxWidth, Is.LessThanOrEqualTo(scroll.Viewport.Width + 1));
            Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(scroll.Viewport.Width + 1), "Wrapping constrains the rendered content to the viewport.");
            int actual = detail.GetLineCount(); Assert.That(actual, Is.GreaterThan(logicalLines), "Check composed lines, not only TextBox.TextWrapping.");
            Assert.That(presenter.TextLayout.TextLines.Count, Is.EqualTo(actual)); return actual;
        }
    }

    [AvaloniaTest]
    public async Task WorkerNotificationsPreserveOldRowsSelectionFocusAndTheFirstArrowsThenOnlyFollowTheLastRow()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open(); f.Add("status"); f.Add("log"); f.Add("diff"); await Ready(log, 3);
        ListBox list = List(log); object? source = list.ItemsSource; object[] rows = list.Items.Cast<object>().ToArray();
        Assert.That(list.SelectedItem, Is.SameAs(rows[2])); PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(0)!);
        Control firstContainer = (Control)list.ContainerFromIndex(0)!; string detail = Detail(log).Text!;
        await Task.Run(() => { for (int index = 0; index < 40; index++) { f.Add("show"); } }); await Ready(log, 43);
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(rows[0])); Assert.That(list.Items[1], Is.SameAs(rows[1]));
        Assert.That(list.ContainerFromIndex(0), Is.SameAs(firstContainer)); Assert.That(Detail(log).Text, Is.EqualTo(detail)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowDown); Assert.That(list.SelectedItem, Is.SameAs(rows[1])); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(rows[0])); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.End); Assert.That(list.SelectedIndex, Is.EqualTo(42));
        long newest = f.Add("rev-parse"); await Ready(log, 44); Assert.That(Entry(list.SelectedItem!).Id, Is.EqualTo(newest));
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        string beforeClear = f.Material(); Press(log, PhysicalKey.L, RawInputModifiers.Control);
        Assert.That(list.Items, Is.Empty); Assert.That(Detail(log).Text, Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty); Assert.That(f.Material(), Is.EqualTo(beforeClear));
    }

    [AvaloniaTest]
    public async Task CapacityEvictsTheNeighborThenTheSelectedIdWithoutGhostRowsOrBrokenFirstArrows()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open();
        for (int index = 0; index < GitCommandLog.Capacity; index++) { f.Add("status"); }
        await Ready(log, 500); ListBox list = List(log); object? source = list.ItemsSource;
        object selected = list.Items[1]!, next = list.Items[2]!, afterNext = list.Items[3]!;
        long evictedNeighbor = Entry(list.Items[0]!).Id, selectedId = Entry(selected).Id;
        list.ScrollIntoView(selected); Pump(log); PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(1)!);
        string material = f.Material(); await Task.Run(() => f.Add("log"));
        await Until(() => list.Items.Count == 500 && Entry(list.Items[0]!).Id == selectedId); Pump(log);
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(selected));
        Assert.That(list.Items.Cast<object>().Select(Entry).Any(entry => entry.Id == evictedNeighbor), Is.False);
        Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowDown); Assert.That(list.SelectedItem, Is.SameAs(next)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(selected)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        await Task.Run(() => f.Add("diff")); await Until(() => Entry(list.Items[0]!).Id == Entry(next).Id); Pump(log);
        Assert.That(list.SelectedItem, Is.SameAs(next)); Assert.That(list.Items.Count, Is.EqualTo(500)); Assert.That(list.ItemsSource, Is.SameAs(source));
        Assert.That(list.Items.Cast<object>().Select(Entry).Any(entry => entry.Id == selectedId), Is.False, "An evicted selection must not remain as a ghost row.");
        Assert.That(list.Items.Cast<object>().Select(Entry).Select(entry => entry.Id), Is.EqualTo(GitCommandLog.Snapshot().Select(entry => entry.Id)));
        Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowDown); Assert.That(list.SelectedItem, Is.SameAs(afterNext)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(log, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(next)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Material(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task RealRunningGitPublishesPidAndCompletionOnTheSameRowWithoutRetainingAnyPayload()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open();
        (string release, Task<GitResult> run) = f.StartGated(); await Until(() => GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId is > 0)); await Ready(log, 1);
        ListBox list = List(log); object row = list.Items[0]!; object? source = list.ItemsSource; long id = Entry(row).Id;
        Assert.That(Detail(log).Text, Does.Contain("still running")); Assert.That(Entry(row).IsRunning, Is.True); Assert.That(Entry(row).ProcessId, Is.GreaterThan(0));
        string before = Detail(log).Text!; Assert.That(before, Does.Not.Contain("journal-secret").And.Not.Contain("Authorization").And.Not.Contain("extraHeader"));
        list.Focus(); File.WriteAllText(release, "release"); GitResult result = await run.WaitAsync(TimeSpan.FromSeconds(15));
        await Until(() => !Entry(list.Items[0]!).IsRunning); Pump(log);
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.Items[0], Is.SameAs(row)); Assert.That(list.SelectedItem, Is.SameAs(row));
        Assert.That(Entry(row).Id, Is.EqualTo(id)); Assert.That(Entry(row).ExitCode, Is.EqualTo(result.ExitCode)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Assert.That(result.StandardOutput, Does.Contain("stdout-journal-secret")); Assert.That(result.StandardError, Does.Contain("stderr-journal-secret"));
        Assert.That(Detail(log).Text, Does.Not.Contain("journal-secret").And.Not.Contain("Authorization").And.Not.Contain("extraHeader"));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(GitCommandLog.Snapshot()), Does.Not.Contain("journal-secret"));
        Assert.That(GitCommandLogPresentation.Export(GitCommandLog.Snapshot(), true, CultureInfo.InvariantCulture), Does.Not.Contain("journal-secret"));
    }

    [AvaloniaTest]
    public async Task ClearWhileActualGitRunsDoesNotResurrectItsRowAndCloseDiscardsQueuedCallbacks()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open();
        (string release, Task<GitResult> run) = f.StartGated(); await Until(() => GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId is > 0)); await Ready(log, 1);
        List(log).Focus(); Press(log, PhysicalKey.L, RawInputModifiers.Control); Assert.That(List(log).Items, Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        File.WriteAllText(release, "release"); await run.WaitAsync(TimeSpan.FromSeconds(15)); Pump(log);
        Assert.That(List(log).Items, Is.Empty); Assert.That(Detail(log).Text, Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        f.Add("log"); log.Close(); int count = List(log).Items.Count; string? text = Detail(log).Text;
        await Task.Run(() => f.Add("diff")); Dispatcher.UIThread.RunJobs();
        Assert.That(List(log).Items.Count, Is.EqualTo(count)); Assert.That(Detail(log).Text, Is.EqualTo(text)); Assert.That(log.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task CopyUsesTheSafeCommandButARealDetailSelectionHasNativeCtrlCPriorityAndSurvivesOtherRows()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open(); f.Add("status"); f.Add("log"); await Ready(log, 2);
        ListBox list = List(log); PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(0)!);
        var clipboard = log.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            list.Focus(); Press(log, PhysicalKey.C, RawInputModifiers.Control); await log.Work;
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("git status"));
            TextBox detail = Detail(log); detail.Focus(); int start = detail.Text!.IndexOf("git status", StringComparison.Ordinal);
            detail.SelectionStart = start; detail.SelectionEnd = start + 3; string selectedText = detail.SelectedText!;
            await Task.Run(() => f.Add("diff")); await Ready(log, 3);
            Assert.That(detail.IsKeyboardFocusWithin, Is.True); Assert.That(detail.SelectedText, Is.EqualTo(selectedText));
            Press(log, PhysicalKey.C, RawInputModifiers.Control); await Until(async () => await ClipboardExtensions.TryGetTextAsync(clipboard) == "git");
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("git"));
            detail.SelectionStart = detail.SelectionEnd = 0; Press(log, PhysicalKey.C, RawInputModifiers.Control); await log.Work;
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("git status")); Assert.That(detail.IsKeyboardFocusWithin, Is.True);
        }
        finally { if (previous is not null) { await clipboard.SetTextAsync(previous); } }
    }

    [AvaloniaTest]
    public async Task SaveUsesARealStorageFileAndTheSnapshotBeforeThePickerWithOverwriteAndNoProfileOrGitMutation()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open(); f.Add("status", "λ,;\"quoted\"\r\npart"); await Ready(log, 1);
        string destination = f.Profile.Git.Output("journal-é.CSV"); File.WriteAllText(destination, new string('z', 4000)); IStorageFile chosen = await StorageFile(log, destination);
        TaskCompletionSource<IStorageFile?> picker = new(TaskCreationOptions.RunContinuationsAsynchronously); FilePickerSaveOptions? options = null;
        log.SaveFilePicker = value => { options = value; return picker.Task; };
        GitCommandLogEntry[] snapshot = GitCommandLog.Snapshot().ToArray(); string expected = GitCommandLogPresentation.Export(snapshot, true, CultureInfo.CurrentCulture);
        string before = f.Material(); Dictionary<string, byte[]> profiles = f.Profile.Material(); List(log).Focus(); Press(log, PhysicalKey.S, RawInputModifiers.Control);
        Assert.That(log.IsBusy, Is.True); FilePickerSaveOptions observedOptions = options ?? throw new InvalidOperationException("The real save gesture did not open the picker.");
        Assert.That(observedOptions.ShowOverwritePrompt, Is.True); Assert.That(observedOptions.DefaultExtension, Is.EqualTo("txt"));
        Assert.That(observedOptions.FileTypeChoices!.SelectMany(type => type.Patterns ?? []), Does.Contain("*.csv").And.Contain("*.txt"));
        f.Add("rev-parse"); picker.SetResult(chosen); await log.Work.WaitAsync(TimeSpan.FromSeconds(15)); Pump(log);
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new UTF8Encoding(false).GetBytes(expected)));
        Assert.That(File.ReadAllText(destination), Does.Not.Contain("git rev-parse")); Assert.That(log.IsBusy, Is.False);
        Assert.That(f.Material(), Is.EqualTo(before)); f.Profile.AssertMaterial(profiles);
        string txt = f.Profile.Git.Output("journal.txt"); File.WriteAllText(txt, "old");
        log.SaveFilePicker = async _ => await StorageFile(log, txt);
        snapshot = GitCommandLog.Snapshot().ToArray(); Press(log, PhysicalKey.S, RawInputModifiers.Control); await log.Work;
        Assert.That(File.ReadAllText(txt), Is.EqualTo(GitCommandLogPresentation.Export(snapshot, false, CultureInfo.CurrentCulture)));
    }

    [AvaloniaTest]
    public async Task PickerCancelAndCloseWithLateResultDoNotWriteAndActualWriteErrorsUseTheOwnedSharedDialog()
    {
        await using Fixture f = await Fixture.Create(); GitCommandLogWindow log = f.Open(); f.Add("status"); await Ready(log, 1); string before = f.Material();
        log.SaveFilePicker = _ => Task.FromResult<IStorageFile?>(null); Press(log, PhysicalKey.S, RawInputModifiers.Control); await log.Work; Assert.That(f.Material(), Is.EqualTo(before));
        string blocked = f.Profile.Git.Output("write-error.txt"); File.WriteAllText(blocked, "old"); IStorageFile storage = await StorageFile(log, blocked);
        File.Delete(blocked); Directory.CreateDirectory(blocked); log.SaveFilePicker = _ => Task.FromResult<IStorageFile?>(storage);
        Press(log, PhysicalKey.S, RawInputModifiers.Control); await Until(() => log.OwnedWindows.Any(child => child.Name == "MessageDialog"));
        Window error = log.OwnedWindows.Single(child => child.Name == "MessageDialog"); Assert.That(error.Owner, Is.SameAs(log));
        Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Is.Not.Empty); PushProcessTestDriver.Pointer(Field<Button>(error, "MessageDialogAccept")); await log.Work;
        Assert.That(log.IsVisible, Is.True); Assert.That(Directory.Exists(blocked), Is.True);
        string late = f.Profile.Git.Output("late.txt"); File.WriteAllText(late, "unchanged"); IStorageFile lateStorage = await StorageFile(log, late);
        TaskCompletionSource<IStorageFile?> picker = new(TaskCreationOptions.RunContinuationsAsynchronously); log.SaveFilePicker = _ => picker.Task;
        Press(log, PhysicalKey.S, RawInputModifiers.Control); Assert.That(log.IsBusy, Is.True); log.Close(); picker.SetResult(lateStorage); await log.Work.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(File.ReadAllText(late), Is.EqualTo("unchanged")); Assert.That(log.OwnedWindows, Is.Empty); Assert.That(f.Material(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task F12IsGlobalBetweenActualBrowsersRestoresMinimizedAndOwnerCloseAllowsANewHost()
    {
        await using Fixture f = await Fixture.Create(); MainWindow first = await f.Browser(), second = await f.Browser();
        int secondF12 = 0;
        second.AddHandler(InputElement.KeyDownEvent, (_, args) => { if (args.Key == Key.F12) { secondF12++; } }, RoutingStrategies.Tunnel, handledEventsToo: true);
        first.Activate(); Press(first, PhysicalKey.F12); GitCommandLogWindow log = first.OwnedWindows.OfType<GitCommandLogWindow>().Single(); f.Windows.Add(log);
        Assert.That(List(log).IsKeyboardFocusWithin, Is.True); log.WindowState = WindowState.Minimized; second.Activate();
        PointAtSecondHistory(); Press(second, PhysicalKey.F12); Assert.That(secondF12, Is.EqualTo(1), "The actual F12 must route through the second Main.");
        Assert.That(first.OwnedWindows.OfType<GitCommandLogWindow>().Single(), Is.SameAs(log)); Assert.That(second.OwnedWindows.OfType<GitCommandLogWindow>(), Is.Empty);
        Assert.That(log.WindowState, Is.EqualTo(WindowState.Normal),
            $"Second Main F12 routed={secondF12}; active={second.IsActive}; second focus={FocusDescription(second)}; log focus={FocusDescription(log)}"); first.Close(); await Until(() => !first.IsVisible && !log.IsVisible);
        second.Activate(); PointAtSecondHistory(); Press(second, PhysicalKey.F12);
        Assert.That(secondF12, Is.EqualTo(2), "The next actual F12 must also route through the second Main.");
        GitCommandLogWindow replacement = second.OwnedWindows.OfType<GitCommandLogWindow>().Single(); f.Windows.Add(replacement);
        Assert.That(replacement, Is.Not.SameAs(log)); Assert.That(replacement.Owner, Is.SameAs(second)); Assert.That(List(replacement).IsKeyboardFocusWithin, Is.True);

        void PointAtSecondHistory()
        {
            // Headless Activate does not move keyboard focus out of a minimized
            // owned window. Use the same real pointer gesture as choosing Main.
            ListBox history = Field<ListBox>(second, "RevisionHistory");
            object selected = history.SelectedItem ?? history.Items[0]!;
            history.ScrollIntoView(selected); Pump(second);
            PushProcessTestDriver.Pointer((ListBoxItem)history.ContainerFromIndex(history.Items.IndexOf(selected))!);
            Assert.That(history.IsKeyboardFocusWithin, Is.True, "The browser must really own focus before its F12 gesture.");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal DetailedSettingsTestScope Profile { get; }
        internal List<Window> Windows { get; } = [];
        private readonly List<(string Release, Task<GitResult> Run)> _runs = [];
        private Fixture(DetailedSettingsTestScope profile) { Profile = profile; }
        internal static async Task<Fixture> Create()
        {
            DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
            profile.Desktop.Save(profile.Desktop.Load() with { Language = "English", StartWithRecentWorkingDir = false, CheckForUpdates = false });
            GitCommandLog.Clear(); return new(profile);
        }
        internal GitCommandLogWindow Open(string language = "en", bool dark = false)
        {
            GitCommandLogWindow log = new(TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language)))
                { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Windows.Add(log); log.Show(); Pump(log); return log;
        }
        internal async Task<MainWindow> Browser()
        {
            MainWindow window = new() { Width = 1000, Height = 700 }; Windows.Add(window); window.Show(); await window.OpenRepositoryAsync(Profile.Repository!.Path); await Until(() => !window.IsBusy); return window;
        }
        internal long Add(string verb, string? directory = null)
        {
            long id = (long)typeof(GitCommandLog).GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { new[] { "-c", "http.extraHeader=Authorization: omitted-journal-secret", verb, "omitted-argument-journal-secret" }, directory ?? Profile.Repository!.Path, Started })!;
            typeof(GitCommandLog).GetMethod("SetProcessId", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { id, 42 });
            typeof(GitCommandLog).GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { id, TimeSpan.FromMilliseconds(12.5), 0, false }); return id;
        }
        internal (string Release, Task<GitResult> Run) StartGated()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The native gated Git fixture requires Linux."); }
            string wrapper = Profile.Git.Output("journal-gate-" + _runs.Count), release = wrapper + ".release";
            File.WriteAllText(wrapper, "#!/bin/sh\nprintf 'stdout-journal-secret\\n'\nprintf 'stderr-journal-secret\\n' >&2\nwhile [ ! -e " + Shell(release) + " ]; do /bin/sleep 0.02; done\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Task<GitResult> run = new GitRunner(wrapper).RunAsync(Profile.Repository!.Path, ["-c", "http.extraHeader=Authorization: argument-journal-secret", "status", "--porcelain"],
                standardInput: "stdin-journal-secret", environment: new Dictionary<string, string?> { ["JOURNAL_PRIVATE"] = "env-journal-secret" });
            _runs.Add((release, run)); return (release, run);
        }
        internal string Material()
        {
            string root = Profile.Repository!.Path;
            return string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var run in _runs) { File.WriteAllText(run.Release, "cleanup"); }
            foreach (var run in _runs) { await run.Run.WaitAsync(TimeSpan.FromSeconds(15)); }
            foreach (Window window in Windows.AsEnumerable().Reverse())
            {
                foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
                if (window.IsVisible) { window.Close(); }
                if (window is GitCommandLogWindow log) { await log.Work.WaitAsync(TimeSpan.FromSeconds(15)); }
            }
            await Until(() => Windows.All(window => !window.IsVisible)); GitCommandLog.Clear(); Profile.Dispose();
        }
        private static string Shell(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
    private static string FocusDescription(TopLevel window) => window.FocusManager?.GetFocusedElement() is Control control
        ? control.GetType().Name + "/" + control.Name + "; visible=" + control.IsEffectivelyVisible + "; root=" + TopLevel.GetTopLevel(control)?.GetType().Name : "none";
    private static GitCommandLogEntry Entry(object row) => (GitCommandLogEntry)row.GetType().GetProperty("Entry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
    private static ListBox List(Control root) => Field<ListBox>(root, "LogItems");
    private static TextBox Detail(Control root) => Field<TextBox>(root, "GitCommandLogText");
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Menu(GitCommandLogWindow log, string name) => List(log).ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static async Task<IStorageFile> StorageFile(Window owner, string path)
    { IStorageFile? file = await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(path)); Assert.That(file, Is.Not.Null, "The picker must supply a real IStorageFile."); return file!; }
    private static async Task Ready(GitCommandLogWindow log, int count) { await Until(() => List(log).Items.Count == count && !log.IsBusy); Pump(log); }
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Pump(window); }
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static Task Until(Func<bool> predicate) => DetailedSettingsUiHost.Until(predicate);
    private static async Task Until(Func<Task<bool>> predicate)
    { for (int count = 0; count < 100; count++) { Dispatcher.UIThread.RunJobs(); if (await predicate()) { return; } await Task.Delay(10); } Assert.That(await predicate(), Is.True); }
}
