using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
public class BlameOptionsTests
{
    [AvaloniaTest]
    public async Task OriginalDisplayDefaultsAndMenuGesturesPersistAndChangeTheRenderedAuthorGutter()
    {
        using Fixture fixture = await Fixture.CreateAsync("one\ntwo\nthree\n", "one\ntwo\nthree\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlamePreferencesStore store = new();
        Assert.That(store.FilePath, Does.StartWith(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!));
        Assert.That(store.Load(), Is.EqualTo(new BlamePreferences()));
        BlameDialog dialog = new(repository, "file.txt"); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(Helpers.Field<CheckBox>(dialog, "BlameIgnoreWhitespace").IsChecked, Is.True);
            Assert.That(view.ShowTime, Is.True); Assert.That(view.ShowLineNumbers, Is.False);
            using WriteableBitmap noNumbers = Helpers.Capture(dialog, "classic-blame-default-options.png");
            int blank = GutterDarkPixels(noNumbers, dialog, 1);
            await Option(dialog, "showLineNumbersToolStripMenuItem");
            using WriteableBitmap numbers = Helpers.Capture(dialog, "classic-blame-author-numbers.png");
            Assert.That(GutterDarkPixels(numbers, dialog, 1), Is.GreaterThan(blank + 3), "The option must draw numbers on otherwise blank author rows, rather than hiding the code's independent numbers.");
            await Option(dialog, "displayAuthorFirstToolStripMenuItem");
            await Option(dialog, "showAuthorAvatarToolStripMenuItem");
            await Option(dialog, "showOriginalFilePathToolStripMenuItem");
            await Option(dialog, "showAuthorDateToolStripMenuItem");
            Assert.That(view.ShowAuthor, Is.True); Assert.That(view.ShowDate, Is.False);
            ContextMenu menu = OpenOptions(dialog);
            Assert.That(Item(menu, "BlameOption_showAuthorTimeToolStripMenuItem").IsEnabled, Is.False);
            menu.Close();
            await Option(dialog, "showAuthorToolStripMenuItem");
            Assert.That(view.ShowAuthor, Is.False); Assert.That(view.ShowDate, Is.True, "Disabling the last author/date column must restore the date, as in the original.");
            await Option(dialog, "UseHistogramDiffAlgorithm");
            BlamePreferences saved = store.Load();
            Assert.Multiple(() =>
            {
                Assert.That(saved.ShowLineNumbers, Is.True); Assert.That(saved.DisplayAuthorFirst, Is.True);
                Assert.That(saved.ShowAuthorAvatar, Is.False); Assert.That(saved.ShowOriginalFilePath, Is.False);
                Assert.That(saved.ShowAuthor, Is.False); Assert.That(saved.ShowAuthorDate, Is.True);
                Assert.That(saved.UseHistogramDiffAlgorithm, Is.True);
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
            });
            dialog.Close();
            BlameDialog reopened = new(repository, "file.txt"); reopened.Show();
            try
            {
                await reopened.InitializeAsync();
                BlameView restored = Helpers.Field<BlameView>(reopened, "BlameView");
                Assert.That(restored.Preferences, Is.EqualTo(saved));
                Assert.That(restored.GetAuthorLine(restored.Lines[0]), Does.Not.Contain("Desktop Tester"));
                foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    reopened.RequestedThemeVariant = theme;
                    using var frame = Helpers.Capture(reopened, "classic-blame-options-" + theme + ".png");
                    Assert.That(frame.PixelSize.Width, Is.EqualTo(1100));
                }
            }
            finally { reopened.Close(); }
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task QueryMenuUpdatesActualWhitespaceAttributionAndIsSharedWithFileHistory()
    {
        const string original = "one\nhello world\nlast\n";
        using Fixture fixture = await Fixture.CreateAsync(original, original);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), original.Replace("hello world", "hello    world", StringComparison.Ordinal));
        await Helpers.Git(fixture, "commit", "-am", "Whitespace changed", "--author=Whitespace Author <white@example.invalid>");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlameDialog dialog = new(repository, "file.txt", initialLine: 2); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastBlame!.Lines[1].Line.AuthorName, Is.EqualTo("Desktop Tester"));
            await Option(dialog, "ignoreWhitespaceToolStripMenuItem");
            Assert.That(dialog.LastBlame!.Lines[1].Line.AuthorName, Is.EqualTo("Whitespace Author"), "Changing the original menu must reload real Git attribution.");
            dialog.Close();
            FileHistoryDialog history = new(repository, "file.txt"); history.Show();
            try
            {
                await history.InitializeAsync(); Helpers.Field<TabControl>(history, "FileHistoryTabs").SelectedIndex = 3; await Helpers.Idle(history);
                BlameView embedded = Helpers.Field<BlameView>(history, "FileHistoryBlame");
                Assert.That(embedded.Lines[1].Line.AuthorName, Is.EqualTo("Whitespace Author"));
                await Option(history, "ignoreWhitespaceToolStripMenuItem");
                Assert.That(embedded.Lines[1].Line.AuthorName, Is.EqualTo("Desktop Tester"));
                Assert.That(new BlamePreferencesStore().Load().IgnoreWhitespace, Is.True);
            }
            finally { history.Close(); }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task HoverShowsRealAuthorCommitterAndFilenameInformationInBothThemesAndClearsOnExit()
    {
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial); await Helpers.MakeHistoryAsync(fixture);
        BlameDialog dialog = new(await GitRepository.OpenAsync(fixture.Repository), Helpers.LiteralPath); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView"); Control margin = Helpers.Field<Control>(dialog, "BlameAuthorMargin");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme; dialog.UpdateLayout();
                Point point = AuthorPoint(dialog, 1); dialog.MouseMove(point);
                await view.TooltipLoading.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(ToolTip.GetIsOpen(margin), Is.True);
                Border tooltip = (Border)ToolTip.GetTip(margin)!; TextBlock text = (TextBlock)tooltip.Child!;
                Assert.That(text.Text, Does.Contain("Béatrice").And.Contain("Committer: Desktop Tester").And.Contain("Change literal file").And.Contain("\\n"));
                Assert.That(text.Text, Does.Contain(view.Lines[1].Line.Hash[..9]));
                Color background = theme == ThemeVariant.Dark ? Color.Parse("#50503c") : Color.Parse("#ffffe1");
                Color foreground = theme == ThemeVariant.Dark ? Color.Parse("#bebebe") : Colors.Black;
                Assert.That(((ISolidColorBrush)tooltip.Background!).Color, Is.EqualTo(background));
                Assert.That(((ISolidColorBrush)text.Foreground!).Color, Is.EqualTo(foreground));
                Assert.That(FileHistorySyntax.Ratio(foreground, background), Is.GreaterThan(4.4), "The original Info/InfoText colors must remain readable in the actual popup.");
                TopLevel popup = (TopLevel)tooltip.GetVisualRoot()!;
                await Helpers.Until(() =>
                {
                    popup.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using WriteableBitmap? rendered = popup.CaptureRenderedFrame();
                    return rendered is not null && ColorPixels(rendered, background) > 100;
                });
                using WriteableBitmap frame = popup.CaptureRenderedFrame()!;
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "classic-blame-tooltip-" + theme + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
                Assert.That(tooltip.Bounds.Width, Is.GreaterThan(0));
                Assert.That(ColorPixels(frame, background), Is.GreaterThan(100));
                dialog.MouseMove(new Point(500, 10));
                Assert.That(ToolTip.GetIsOpen(margin), Is.False);
                Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(1), "Hover must not change the selected attribution line.");
            }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ContextActionsCopyAndShowTheRightClickedCommitRatherThanTheCaretCommit()
    {
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial); await Helpers.MakeHistoryAsync(fixture);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlameDialog dialog = new(repository, Helpers.LiteralPath); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(1));
            GitFileBlameLine target = view.Lines[1];
            foreach ((string item, string? expected) in new[]
            {
                ("commitHashToolStripMenuItem", (string?)target.Line.Hash),
                ("commitMessageToolStripMenuItem", (string?)"Change literal file"),
                ("allCommitInfoToolStripMenuItem", (string?)null)
            })
            {
                ContextMenu context = await RightClick(dialog, 1);
                MenuItem copy = Item(context, "copyToClipboardToolStripMenuItem");
                MenuItem command = copy.Items.OfType<MenuItem>().Single(menu => menu.Name == item);
                await ClipboardExtensions.SetTextAsync(dialog.Clipboard!, "before-copy");
                command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                string? actual = null;
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                do
                {
                    actual = await ClipboardExtensions.TryGetTextAsync(dialog.Clipboard!).WaitAsync(timeout.Token);
                    if (actual == "before-copy") { await Task.Delay(10, timeout.Token); }
                }
                while (actual == "before-copy");
                if (expected is not null) { Assert.That(actual, Is.EqualTo(expected)); }
                else { Assert.That(actual, Does.Contain("Béatrice").And.Contain("Committer: Desktop Tester").And.Contain("Change literal file")); }
                context.Close();
                Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(1));
            }
            ContextMenu changes = await RightClick(dialog, 1);
            Item(changes, "showChangesToolStripMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); changes.Close(); await Helpers.Idle(dialog);
            Assert.That(Helpers.Field<FileHistoryContentView>(dialog, "BlameChanges").TextView.Text, Does.Contain("+    public int Value = 2;"));
            Helpers.Field<TabControl>(dialog, "BlameTabs").SelectedIndex = 0; dialog.UpdateLayout();
            ContextMenu previous = await RightClick(dialog, 1);
            Assert.That(Item(previous, "blamePreviousRevisionToolStripMenuItem").IsEnabled, Is.True);
            Item(previous, "blamePreviousRevisionToolStripMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); previous.Close(); await Helpers.Idle(dialog);
            Assert.That(view.TextView.Text, Is.EqualTo(Helpers.Initial));
            Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(2));
            ContextMenu root = await RightClick(dialog, 0);
            Assert.That(Item(root, "blamePreviousRevisionToolStripMenuItem").IsEnabled, Is.False);
            root.Close();
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheBlameTooltipCancelsItsActualMetadataGitProcess()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial);
        string enable = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "slow-tooltip.enable");
        (GitRepository repository, string started) = Helpers.SlowRepository(fixture, "show", enable);
        BlameDialog dialog = new(repository, "file.txt"); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); dialog.UpdateLayout();
            await File.WriteAllTextAsync(enable, "enabled after the initial CommitInfo read");
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            dialog.MouseMove(AuthorPoint(dialog, 0));
            await Helpers.Until(() => File.Exists(started) && new FileInfo(started).Length > 0);
            int pid = int.Parse(await File.ReadAllTextAsync(started), CultureInfo.InvariantCulture);
            Assert.That(view.TooltipLoading.IsCompleted, Is.False);
            dialog.Close();
            await view.TooltipLoading.WaitAsync(TimeSpan.FromSeconds(5));
            await Helpers.Until(() => !Directory.Exists("/proc/" + pid));
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["status", "--porcelain"])).StandardOutput, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FileHistoryContextUsesRealOriginalRevisionPathAndAdjustedPreviousLine()
    {
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial); await Helpers.MakeHistoryAsync(fixture);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        FileHistoryDialog history = new(repository, Helpers.LiteralPath); history.Show();
        try
        {
            await history.InitializeAsync(); Helpers.Field<TabControl>(history, "FileHistoryTabs").SelectedIndex = 3; await Helpers.Idle(history);
            BlameView embedded = Helpers.Field<BlameView>(history, "FileHistoryBlame");
            GitFileBlameLine target = embedded.Lines[1]; string selectedRevision = history.SelectedEntry!.Commit.Hash;
            foreach (string action in new[] { "blameRevisionToolStripMenuItem", "showChangesToolStripMenuItem", "blamePreviousRevisionToolStripMenuItem" })
            {
                ContextMenu context = await RightClick(history, 1);
                Item(context, action).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); context.Close();
                await Helpers.Until(() => history.OwnedWindows.OfType<BlameDialog>().Any());
                BlameDialog child = history.OwnedWindows.OfType<BlameDialog>().Single();
                try
                {
                    await Helpers.Idle(child);
                    if (action == "blamePreviousRevisionToolStripMenuItem")
                    {
                        GitBlamePreviousRevision expected = (await repository.GetBlamePreviousRevisionAsync(target, embedded.Preferences.GitOptions))!;
                        Assert.That(child.LastBlame!.Revision, Is.EqualTo(expected.Revision));
                        Assert.That(child.LastBlame.Path, Is.EqualTo(expected.Path));
                        Assert.That(Helpers.Field<BlameView>(child, "BlameView").SelectedLine!.Line.LineNumber, Is.EqualTo(expected.Line));
                    }
                    else
                    {
                        Assert.That(child.LastBlame!.Revision, Is.EqualTo(target.Line.Hash));
                        Assert.That(child.LastBlame.Path, Is.EqualTo(target.OriginalPath));
                        if (action == "showChangesToolStripMenuItem")
                        { Assert.That(Helpers.Field<FileHistoryContentView>(child, "BlameChanges").TextView.Text, Does.Contain("+    public int Value = 2;")); }
                        else { Assert.That(Helpers.Field<BlameView>(child, "BlameView").SelectedLine!.Line.LineNumber, Is.EqualTo(target.Line.OriginalLine)); }
                    }
                }
                finally { child.Close(); }
                await Helpers.Until(() => !history.OwnedWindows.Contains(child));
                Assert.That(history.SelectedEntry!.Commit.Hash, Is.EqualTo(selectedRevision));
            }
            // The original author viewer also opens the blamed revision on double-click.
            GitFileBlameLine first = embedded.Lines[0]; Point point = AuthorPoint(history, 0);
            history.MouseMove(point);
            for (int click = 0; click < 2; click++) { history.MouseDown(point, MouseButton.Left); history.MouseUp(point, MouseButton.Left); }
            await Helpers.Until(() => history.OwnedWindows.OfType<BlameDialog>().Any());
            BlameDialog doubleClick = history.OwnedWindows.OfType<BlameDialog>().Single();
            try
            {
                await Helpers.Idle(doubleClick);
                Assert.That(doubleClick.LastBlame!.Revision, Is.EqualTo(first.Line.Hash));
                Assert.That(doubleClick.LastBlame.Path, Is.EqualTo(first.OriginalPath));
            }
            finally { doubleClick.Close(); }
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { foreach (Window child in history.OwnedWindows.ToArray()) { child.Close(); } history.Close(); }
    }

    [AvaloniaTest]
    public async Task MenusReuseOriginalFrenchTranslationsAndMalformedSettingsRestoreOriginalDefaults()
    {
        using Fixture fixture = await Fixture.CreateAsync("one\ntwo\n", "one\ntwo\n");
        BlamePreferencesStore store = new(); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, "{ malformed");
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = new("fr-FR");
        BlameDialog dialog = new(await GitRepository.OpenAsync(fixture.Repository), "file.txt"); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Assert.That(store.Load(), Is.EqualTo(new BlamePreferences()));
            ContextMenu options = OpenOptions(dialog);
            Assert.That(Item(options, "BlameOption_showAuthorToolStripMenuItem").Header, Is.EqualTo("Afficher l'auteur"));
            Assert.That(Item(options, "BlameOption_ignoreWhitespaceToolStripMenuItem").Header, Is.EqualTo("Ignorer les espaces"));
            options.Close();
            ContextMenu context = await RightClick(dialog, 0);
            Assert.That(Item(context, "blameRevisionToolStripMenuItem").Header, Is.EqualTo("Blâmer cette révision"));
            Assert.That(Item(context, "blamePreviousRevisionToolStripMenuItem").Header, Is.EqualTo("Blâmer la révision précédente"));
            Assert.That(Item(context, "copyToClipboardToolStripMenuItem").Header, Is.EqualTo("Copier dans le presse-papiers"));
            context.Close();
            store.Save(new() { ShowAuthor = false, ShowAuthorDate = false });
            Assert.That(store.Load().ShowAuthorDate, Is.True);
        }
        finally { dialog.Close(); CultureInfo.CurrentUICulture = previous; }
    }

    private static ContextMenu OpenOptions(Window window)
    { Button button = Helpers.Field<Button>(window, "BlameOptions"); Helpers.Click(window, "BlameOptions"); return button.ContextMenu!; }
    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static async Task Option(RepositoryDialog dialog, string name)
    {
        ContextMenu menu = OpenOptions(dialog); MenuItem item = Item(menu, "BlameOption_" + name);
        Assert.That(item.IsEnabled, Is.True); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Close(); await Helpers.Idle(dialog);
    }
    private static Point AuthorPoint(Window window, int index)
    {
        BlameView view = window is BlameDialog ? Helpers.Field<BlameView>(window, "BlameView") : Helpers.Field<BlameView>(window, "FileHistoryBlame");
        return Helpers.Field<Control>(window, "BlameAuthorMargin").TranslatePoint(new Point(100, view.TextView.Editor.TextArea.TextView.DefaultLineHeight * (index + .5)), window)!.Value;
    }
    private static async Task<ContextMenu> RightClick(Window dialog, int index)
    {
        dialog.UpdateLayout(); Point point = AuthorPoint(dialog, index);
        dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Right); dialog.MouseUp(point, MouseButton.Right);
        ContextMenu menu = Helpers.Field<Control>(dialog, "BlameAuthorMargin").ContextMenu!;
        await Helpers.Until(() => menu.IsOpen); return menu;
    }
    private static int GutterDarkPixels(WriteableBitmap frame, Window owner, int row)
    {
        BlameView view = Helpers.Field<BlameView>(owner, "BlameView");
        double height = view.TextView.Editor.TextArea.TextView.DefaultLineHeight;
        Point top = Helpers.Field<Control>(owner, "BlameAuthorMargin").TranslatePoint(new Point(8 + Math.Min(20, height), row * height), owner)!.Value;
        using ILockedFramebuffer buffer = frame.Lock(); byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        int result = 0;
        for (int y = (int)top.Y; y < top.Y + height; y++)
        for (int x = (int)top.X; x < top.X + 20; x++)
        { int i = y * buffer.RowBytes + x * 4; if (bytes[i] < 80 && bytes[i + 1] < 80 && bytes[i + 2] < 80) { result++; } }
        return result;
    }
    private static int ColorPixels(WriteableBitmap frame, Color color)
    {
        using ILockedFramebuffer buffer = frame.Lock(); byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        int result = 0;
        for (int y = 0; y < buffer.Size.Height; y++)
        for (int x = 0; x < buffer.Size.Width; x++)
        {
            int i = y * buffer.RowBytes + x * 4;
            byte red = bytes[i + (buffer.Format == PixelFormat.Rgba8888 ? 0 : 2)], green = bytes[i + 1], blue = bytes[i + (buffer.Format == PixelFormat.Rgba8888 ? 2 : 0)];
            if (red == color.R && green == color.G && blue == color.B) { result++; }
        }
        return result;
    }
}
