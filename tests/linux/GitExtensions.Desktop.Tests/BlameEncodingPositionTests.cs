using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
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
public sealed class BlameEncodingPositionTests
{
    private const string LiteralPath = "été\nquote'\"[*]? λ.cs";
    private const string OtherPath = "other[*]? λ.cs";

    [AvaloniaTest]
    public Task ConfiguredWindows1252BlameDisplaysRealHighLinesAndUtf8AttributionInBothThemes()
        => ConfiguredEncodingAsync("windows-1252", "Café — prix €");

    [AvaloniaTest]
    public Task ConfiguredLatin1BlameDisplaysRealHighLinesAndUtf8AttributionInBothThemes()
        => ConfiguredEncodingAsync("iso-8859-1", "Café été à bientôt");

    private static async Task ConfiguredEncodingAsync(string encodingName, string description)
    {
        using Fixture fixture = await Fixture.CreateAsync("seed\n", "seed\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("i18n.filesencoding", encodingName);
        string source = Source(description);
        byte[] bytes = await CommitFileAsync(fixture, LiteralPath, source, encodingName, "Encoded source", "Auteure 🧪 <author@example.invalid>");
        string head = await repository.ResolveRevisionAsync("HEAD");
        BlameDialog dialog = new(repository, LiteralPath, initialLine: 180); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.Multiple(() =>
            {
                Assert.That(view.TextView.Text, Is.EqualTo(source));
                Assert.That(view.CurrentFileEncodingName, Is.EqualTo(encodingName));
                Assert.That(view.TextView.DefaultFileEncodingName, Is.EqualTo(encodingName));
                Assert.That(dialog.LastBlame!.FileEncodingName, Is.EqualTo(encodingName));
                Assert.That(dialog.LastBlame.Bytes, Is.EqualTo(bytes));
                Assert.That(view.SelectedLine!.Line.Text, Is.EqualTo(SourceLine(description, 180)));
                Assert.That(view.SelectedLine.Line.AuthorName, Is.EqualTo("Auteure 🧪"));
                Assert.That(view.CurrentFileLine, Is.EqualTo(180));
                Assert.That(view.TextView.Editor.TextArea.Caret.Line, Is.EqualTo(180));
                Assert.That(view.TextView.IsDocumentReady, Is.True);
                Assert.That(view.TextView.CanStagePatch, Is.False);
            });
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme; dialog.UpdateLayout();
                Assert.That(((ISolidColorBrush)view.TextView.Editor.Foreground!).Color,
                    Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#f0f0f0" : "#000000")));
                using WriteableBitmap frame = Helpers.Capture(dialog, "blame-encoding-" + encodingName + "-180-" + theme + ".png");
                Assert.That(view.TextView.Editor.TextArea.TextView.VisualLines.Any(line => line.FirstDocumentLine.LineNumber <= 180 && line.LastDocumentLine.LineNumber >= 180), Is.True);
                Assert.That(HighLineInk(frame, dialog, view), Is.GreaterThan(30), "The actual source row at line 180 must contain rendered glyphs beside its attribution.");
                Assert.That(view.CurrentFileLine, Is.EqualTo(180), "Changing the palette must preserve the source position.");
            }
            Assert.That(await repository.GetFileBytesAsync(LiteralPath), Is.EqualTo(bytes));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, LiteralPath)), Is.EqualTo(bytes));
            Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SameFileOptionsAndManualRevisionKeepSelectionWhileOtherFilesResetAndFailedLoadsStayUnavailable()
    {
        using Fixture fixture = await Fixture.CreateAsync("seed\n", "seed\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        string source = Source("Café €");
        await CommitFileAsync(fixture, LiteralPath, source, "windows-1252", "First source");
        string first = await repository.ResolveRevisionAsync("HEAD");
        string changed = source.Replace(SourceLine("Café €", 185), SourceLine("Café € edited", 185), StringComparison.Ordinal);
        await CommitFileAsync(fixture, LiteralPath, changed, "windows-1252", "Source edit");
        string second = await repository.ResolveRevisionAsync("HEAD");
        await CommitFileAsync(fixture, OtherPath, changed, "windows-1252", "Same bytes another file");
        BlameDialog dialog = new(repository, LiteralPath, second, 180); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            int offset = view.TextView.Editor.Document.GetLineByNumber(180).Offset + 3;
            view.TextView.Select(offset, 8);
            view.TextView.Editor.ScrollToLine(180);
            dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            double scroll = view.TextView.Editor.TextArea.TextView.VerticalOffset;
            view.SetPreferences(view.Preferences with { DetectMoves = true });
            await Helpers.Idle(dialog); Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(view.CurrentFileLine, Is.EqualTo(180));
                Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(180));
                Assert.That(view.TextView.SelectionStart, Is.EqualTo(offset));
                Assert.That(view.TextView.SelectionLength, Is.EqualTo(8));
                Assert.That(view.TextView.Editor.TextArea.TextView.VerticalOffset, Is.EqualTo(scroll).Within(1));
                Assert.That(view.TextView.IsDocumentReady, Is.True, "An unchanged source must finish its load even without TextBox.PropertyChanged.");
                Assert.That(view.TextView.Search.IsAvailable, Is.True);
            });
            view.TextView.Editor.TextArea.Focus();
            Press(dialog, PhysicalKey.G, RawInputModifiers.Control);
            await Helpers.Until(() => dialog.OwnedWindows.Any(window => window.Name == "DiffViewerGoToLineDialog"));
            Window goTo = dialog.OwnedWindows.Single(window => window.Name == "DiffViewerGoToLineDialog");
            Assert.That(Helpers.Field<NumericUpDown>(goTo, "DiffViewerLineNumber").Maximum, Is.GreaterThan(180));
            Helpers.Click(goTo, "DiffViewerGoToCancel");
            await view.TextView.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(10));
            Press(dialog, PhysicalKey.F, RawInputModifiers.Control);
            await Helpers.Until(() => view.TextView.Search.Dialog is { IsVisible: true });
            view.TextView.Search.Dialog!.Close();
            Helpers.Field<TextBox>(dialog, "BlameRevision").Text = first;
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            Assert.That(view.CurrentFileLine, Is.EqualTo(180), "The original blame keeps the line when a revision changes but the repository and filename do not.");
            Assert.That(view.TextView.Text, Is.EqualTo(source));
            Assert.That(view.SelectedLine!.Line.Text, Is.EqualTo(SourceLine("Café €", 180)));
            view.TextView.Select(offset, 8);
            Helpers.Field<TextBox>(dialog, "BlameRevision").Text = "missing-blame-revision";
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(view.TextView.IsDocumentReady, Is.False, "An old source must not become an active reader after the new Git load failed.");
            Assert.That(view.TextView.Search.IsAvailable, Is.False);
            Assert.That(view.TextView.CanStagePatch, Is.False);
            Assert.That(view.TextView.Text, Is.EqualTo(source));
            Helpers.Field<TextBox>(dialog, "BlameRevision").Text = first;
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            Assert.That(view.TextView.IsDocumentReady, Is.True);
            Assert.That(view.CurrentFileLine, Is.EqualTo(180));
            Assert.That(view.TextView.SelectionStart, Is.EqualTo(offset));
            Assert.That(view.TextView.SelectionLength, Is.EqualTo(8));
            Helpers.Field<TextBox>(dialog, "BlameFilePath").Text = OtherPath;
            Helpers.Field<TextBox>(dialog, "BlameRevision").Text = "HEAD";
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            Assert.That(view.CurrentFileLine, Is.EqualTo(1));
            Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(1));
            Assert.That(view.TextView.SelectionLength, Is.Zero);
            Assert.That(view.TextView.Text, Is.EqualTo(changed));
            view.SelectLine(180); await Helpers.Idle(dialog);
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            Assert.That(view.CurrentFileLine, Is.EqualTo(180), "A subsequent reload of the newly selected file must use its own position.");
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ANewRepositoryWithTheSameLiteralPathAndBytesResetsPositionAndSessionEncoding()
    {
        using Fixture first = await Fixture.CreateAsync("seed\n", "seed\n");
        GitRepository firstRepository = await GitRepository.OpenAsync(first.Repository);
        await firstRepository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        string source = Source("ASCII source");
        byte[] bytes = await CommitFileAsync(first, LiteralPath, source, "windows-1252", "First repository source");
        BlameView view = new(); Window owner = new() { Width = 1000, Height = 650, Content = view }; owner.Show();
        try
        {
            await view.ConfigureRepositoryAsync(firstRepository);
            view.SetBlame(await firstRepository.GetFileBlameAsync(LiteralPath));
            view.TextView.FileEncodingName = "windows-1252";
            view.SelectLine(180);
            view.TextView.Select(view.TextView.Editor.Document.GetLineByNumber(180).Offset, 10);
            using Fixture second = await Fixture.CreateAsync("seed\n", "seed\n");
            GitRepository secondRepository = await GitRepository.OpenAsync(second.Repository);
            await secondRepository.SetConfigAsync("i18n.filesencoding", "iso-8859-1");
            await CommitFileAsync(second, LiteralPath, source, "iso-8859-1", "Second repository source");
            await view.ConfigureRepositoryAsync(secondRepository);
            view.SetBlame(await secondRepository.GetFileBlameAsync(LiteralPath));
            Assert.Multiple(() =>
            {
                Assert.That(view.TextView.Text, Is.EqualTo(source));
                Assert.That(view.CurrentFileLine, Is.EqualTo(1));
                Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(1));
                Assert.That(view.TextView.SelectionLength, Is.Zero);
                Assert.That(view.TextView.FileEncodingName, Is.Null);
                Assert.That(view.CurrentFileEncodingName, Is.EqualTo("iso-8859-1"));
                Assert.That(view.TextView.DefaultFileEncodingName, Is.EqualTo("iso-8859-1"));
                Assert.That(view.TextView.IsDocumentReady, Is.True, "Equal bytes in a different repository still complete a new document load.");
            });
            view.SelectLine(180);
            await view.ConfigureRepositoryAsync(secondRepository);
            view.SetBlame(await secondRepository.GetFileBlameAsync(LiteralPath));
            Assert.That(view.CurrentFileLine, Is.EqualTo(180));
            Assert.That(await firstRepository.GetFileBytesAsync(LiteralPath), Is.EqualTo(bytes));
            Assert.That(await secondRepository.GetFileBytesAsync(LiteralPath), Is.EqualTo(bytes));
            Assert.That((await firstRepository.GetStatusAsync()).Changes, Is.Empty);
            Assert.That((await secondRepository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { owner.Close(); view.Dispose(); }
    }

    [AvaloniaTest]
    public async Task FileHistoryPassesSourceLineAndEncodingToBlameAndRetainsTheLineOnOptionsAndRevisionReload()
    {
        using Fixture fixture = await Fixture.CreateAsync("seed\n", "seed\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        string source = Source("Café €");
        await CommitFileAsync(fixture, LiteralPath, source, "windows-1252", "First encoded source");
        string changed = source.Replace(SourceLine("Café €", 185), SourceLine("Café € changed", 185), StringComparison.Ordinal);
        await CommitFileAsync(fixture, LiteralPath, changed, "windows-1252", "Latest encoded source");
        await CommitFileAsync(fixture, OtherPath, changed, "windows-1252", "Another encoded file");
        FileHistoryDialog dialog = new(repository, LiteralPath); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            TabControl tabs = Helpers.Field<TabControl>(dialog, "FileHistoryTabs");
            tabs.SelectedIndex = 2; await Helpers.Idle(dialog);
            FileHistoryContentView file = Helpers.Field<FileHistoryContentView>(dialog, "FileHistoryView");
            Assert.That(file.CurrentFileEncodingName, Is.EqualTo("windows-1252"));
            file.SetFileEncoding("windows-1252");
            Assert.That(file.TextView.GoToFileLine(180), Is.True);
            tabs.SelectedIndex = 3; await Helpers.Idle(dialog);
            BlameView blame = Helpers.Field<BlameView>(dialog, "FileHistoryBlame");
            Assert.That(blame.TextView.Text, Is.EqualTo(changed));
            Assert.That(blame.CurrentFileLine, Is.EqualTo(180));
            Assert.That(blame.TextView.FileEncodingName, Is.EqualTo("windows-1252"));
            Assert.That(blame.CurrentFileEncodingName, Is.EqualTo("windows-1252"));
            int offset = blame.TextView.Editor.Document.GetLineByNumber(180).Offset + 3;
            blame.TextView.Select(offset, 8);
            blame.SetPreferences(blame.Preferences with { DetectCopies = true });
            await Helpers.Idle(dialog);
            Assert.That(blame.CurrentFileLine, Is.EqualTo(180));
            Assert.That(blame.TextView.SelectionStart, Is.EqualTo(offset));
            Assert.That(blame.TextView.SelectionLength, Is.EqualTo(8));
            Assert.That(blame.TextView.IsDocumentReady, Is.True);
            ListBox revisions = Helpers.Field<ListBox>(dialog, "FileHistoryRevisions");
            revisions.SelectedItem = revisions.Items.OfType<GitCommit>().Single(commit => commit.Subject == "First encoded source");
            await Helpers.Idle(dialog);
            Assert.That(blame.TextView.Text, Is.EqualTo(source));
            Assert.That(blame.CurrentFileLine, Is.EqualTo(180));
            Helpers.Field<TextBox>(dialog, "FileHistoryPath").Text = OtherPath;
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            Assert.That(blame.TextView.Text, Is.EqualTo(changed));
            Assert.That(blame.CurrentFileLine, Is.EqualTo(1));
            Assert.That(blame.SelectedLine!.Line.LineNumber, Is.EqualTo(1));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BrowseBlameOpensAtTheDisplayedFileLineAndKeepsItsSessionCodecAndOwnerSelection()
    {
        using Fixture fixture = await Fixture.CreateAsync("seed\n", "seed\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        string source = Source("Café €");
        byte[] bytes = await CommitFileAsync(fixture, LiteralPath, source, "windows-1252", "Browse encoded source");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Helpers.Field<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Browse encoded source");
        await Helpers.Until(() => !main.IsBusy);
        TabControl tabs = Helpers.Field<TabControl>(main, "DetailsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "FileTreeTab"); main.UpdateLayout();
        await Helpers.Until(() => !main.IsBusy);
        FileStatusListView fileList = Helpers.Field<FileStatusListView>(main, "CommitFileStatusList");
        TreeView tree = fileList.Tree; fileList.SelectTags([LiteralPath]);
        await Helpers.Until(() => !main.IsBusy);
        FileContentView content = main.GetVisualDescendants().OfType<FileContentView>().Single(view => view.IsEffectivelyVisible);
        Assert.That(content.TextView.Text, Is.EqualTo(source));
        content.TextView.FileEncodingName = "windows-1252";
        Assert.That(content.TextView.GoToFileLine(180), Is.True);
        int offset = content.TextView.Editor.Document.GetLineByNumber(180).Offset + 3;
        content.TextView.Select(offset, 8);
        tree.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = tree });
        Dispatcher.UIThread.RunJobs();
        MenuItem action = tree.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiBlame");
        action.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); tree.ContextMenu.Close();
        await Helpers.Until(() => main.OwnedWindows.OfType<BlameDialog>().Any());
        BlameDialog dialog = main.OwnedWindows.OfType<BlameDialog>().Single();
        try
        {
            await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.CurrentFileLine, Is.EqualTo(180));
            Assert.That(view.SelectedLine!.Line.Text, Is.EqualTo(SourceLine("Café €", 180)));
            Assert.That(view.TextView.Text, Is.EqualTo(source));
            Assert.That(view.TextView.FileEncodingName, Is.EqualTo("windows-1252"));
            Assert.That(view.CurrentFileEncodingName, Is.EqualTo("windows-1252"));
            Assert.That(dialog.Owner, Is.SameAs(main));
        }
        finally { dialog.Close(); }
        await Helpers.Until(() => !main.IsBusy);
        Assert.That(content.TextView.CurrentFileLine, Is.EqualTo(180));
        Assert.That(content.TextView.SelectionStart, Is.EqualTo(offset));
        Assert.That(content.TextView.SelectionLength, Is.EqualTo(8));
        Assert.That(await repository.GetFileBytesAsync(LiteralPath), Is.EqualTo(bytes));
        Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task PreviousRevisionMapsEncodedLine183AcrossRealHunksAndRenameToOriginalLine180()
    {
        using Fixture fixture = await Fixture.CreateAsync("seed\n", "seed\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        string source = Source("Café €");
        byte[] bytes = await CommitFileAsync(fixture, LiteralPath, source, "windows-1252", "Original encoded source");
        string parent = await repository.ResolveRevisionAsync("HEAD");
        List<string> changed = source.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        changed.InsertRange(0, Enumerable.Range(1, 5).Select(index => "// inserted Café € " + index));
        changed.RemoveRange(45, 2);
        changed[182] = SourceLine("Café € modified", 180);
        await Helpers.Git(fixture, "mv", "--", LiteralPath, OtherPath);
        byte[] current = await CommitFileAsync(fixture, OtherPath, string.Join('\n', changed) + "\n", "windows-1252", "Rename with high hunk edit");
        BlameDialog dialog = new(repository, OtherPath, initialLine: 183); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.SelectedLine!.Line.Text, Is.EqualTo(SourceLine("Café € modified", 180)));
            Assert.That(view.SelectedLine.PreviousPath, Is.EqualTo(LiteralPath));
            Helpers.Click(dialog, "BlamePreviousRevision"); await Helpers.Idle(dialog);
            Assert.That(dialog.LastBlame!.Revision, Is.EqualTo(parent));
            Assert.That(dialog.LastBlame.Path, Is.EqualTo(LiteralPath));
            Assert.That(dialog.LastBlame.Bytes, Is.EqualTo(bytes));
            Assert.That(view.TextView.Text, Is.EqualTo(source));
            Assert.That(view.CurrentFileLine, Is.EqualTo(180));
            Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(180));
            Assert.That(view.SelectedLine.Line.Text, Is.EqualTo(SourceLine("Café €", 180)));
            Assert.That(view.CurrentFileEncodingName, Is.EqualTo("windows-1252"));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, OtherPath)), Is.EqualTo(current));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ReloadingBlameDisablesTheExistingReaderAndCloseCancelsItsActualGitProcess()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync("seed\n", "seed\n");
        string source = Source("Café €");
        byte[] bytes = await CommitFileAsync(fixture, LiteralPath, source, "windows-1252", "Encoded cancellation source");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        string directory = Path.GetDirectoryName(fixture.Repository)!;
        string script = Path.Combine(directory, "armed-blame.py");
        string armed = Path.ChangeExtension(script, ".armed");
        string started = Path.ChangeExtension(script, ".started");
        await File.WriteAllTextAsync(script, "#!/usr/bin/python3\nimport os, pathlib, sys, time\n" +
            "marker=pathlib.Path(__file__)\nif 'blame' in sys.argv[1:] and marker.with_suffix('.armed').exists():\n" +
            "    marker.with_suffix('.started').write_text(str(os.getpid()))\n    time.sleep(30)\nos.execvp('git', ['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        BlameDialog dialog = new(new GitRepository(fixture.Repository, new GitRunner(script)), LiteralPath, initialLine: 180); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.CurrentFileLine, Is.EqualTo(180));
            await File.WriteAllTextAsync(armed, "armed");
            Task load = dialog.InitializeAsync();
            await Helpers.Until(() => File.Exists(started));
            Assert.Multiple(() =>
            {
                Assert.That(dialog.IsBusy, Is.True);
                Assert.That(view.IsEffectivelyEnabled, Is.False);
                Assert.That(view.TextView.IsDocumentReady, Is.False);
                Assert.That(view.TextView.Search.IsAvailable, Is.False);
                Assert.That(view.TextView.CanStagePatch, Is.False);
                Assert.That(Helpers.Field<TextBox>(dialog, "BlameFilePath").IsEnabled, Is.False);
                Assert.That(view.TextView.Text, Is.EqualTo(source));
            });
            Helpers.Click(dialog, "CancelBlame");
            await load.WaitAsync(TimeSpan.FromSeconds(10));
            await Helpers.Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, LiteralPath)), Is.EqualTo(bytes));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    private static string Source(string description) => string.Join('\n', Enumerable.Range(1, 210).Select(index => SourceLine(description, index))) + "\n";
    private static string SourceLine(string description, int index) => "// " + description + " source line " + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
    private static async Task<byte[]> CommitFileAsync(Fixture fixture, string path, string text, string encodingName, string message, string? author = null)
    {
        byte[] bytes = GitFileEncodingCodec.GetEncoding(encodingName).GetBytes(text);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, path), bytes);
        await Helpers.Git(fixture, "add", "--", path);
        await Helpers.Git(fixture, author is null ? ["commit", "-m", message] : ["commit", "-m", message, "--author=" + author]);
        return bytes;
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers)
    { owner.KeyPressQwerty(key, modifiers); owner.KeyReleaseQwerty(key, modifiers); }
    private static void PointerClick(Window owner, Control control)
    {
        owner.UpdateLayout();
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner)!.Value;
        owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left);
    }
    private static int HighLineInk(WriteableBitmap frame, Window owner, BlameView blame)
    {
        AvaloniaEdit.Rendering.TextView view = blame.TextView.Editor.TextArea.TextView;
        Point origin = view.TranslatePoint(default, owner)!.Value;
        double top = view.GetVisualTopByDocumentLine(180) - view.VerticalOffset;
        using ILockedFramebuffer buffer = frame.Lock();
        byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        int count = 0;
        for (int y = Math.Max(0, (int)(origin.Y + top)); y < Math.Min(buffer.Size.Height, (int)Math.Ceiling(origin.Y + top + view.DefaultLineHeight)); y++)
        for (int x = Math.Max(0, (int)origin.X + 8); x < Math.Min(buffer.Size.Width, (int)(origin.X + Math.Min(500, view.Bounds.Width - 20))); x++)
        {
            int offset = y * buffer.RowBytes + x * 4;
            byte r = pixels[offset + (buffer.Format == PixelFormat.Rgba8888 ? 0 : 2)], g = pixels[offset + 1], b = pixels[offset + (buffer.Format == PixelFormat.Rgba8888 ? 2 : 0)];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 20) { count++; }
        }
        return count;
    }
}
