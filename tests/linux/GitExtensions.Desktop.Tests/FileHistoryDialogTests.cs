using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class FileHistoryDialogTests
{
    internal const string LiteralPath = "été\nquote'\"[*]?\\literal.cs";
    internal const string Initial = "class Alpha {\n    public int Value = 1;\n    // source line\n}\n";

    [AvaloniaTest]
    public async Task FileTreeActionsOpenOwnedHistoryAndBlameWithTheSelectedLiteralFileAndRevision()
    {
        using Fixture fixture = await Fixture.CreateAsync(Initial, Initial);
        PreferencesStore preferences = new(); preferences.Save(preferences.Load() with { UseBrowseForFileHistory = false });
        await MakeHistoryAsync(fixture);
        MainWindow window = fixture.Window;
        await window.OpenRepositoryAsync(fixture.Repository);
        GitCommit selected = Field<ListBox>(window, "RevisionHistory").Items.OfType<GitCommit>().Single(commit => commit.Subject == "Change literal file");
        Field<ListBox>(window, "RevisionHistory").SelectedItem = selected;
        await Until(() => !window.IsBusy);
        TabControl tabs = Field<TabControl>(window, "DetailsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "FileTreeTab");
        window.UpdateLayout();
        await Until(() => !window.IsBusy);
        FileStatusListView fileList = Field<FileStatusListView>(window, "CommitFileStatusList");
        TreeView tree = fileList.Tree; fileList.SelectTags([LiteralPath]);
        await Until(() => !window.IsBusy);
        string originalPreview = window.GetVisualDescendants().OfType<FileContentView>().Single(view => view.IsEffectivelyVisible).TextView.Text;
        MenuItem Action(string name)
        {
            tree.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = tree });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            return tree.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
        }
        Action("tsmiFileHistory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); tree.ContextMenu!.Close();
        await Until(() => window.OwnedWindows.OfType<FileHistoryDialog>().Any());
        FileHistoryDialog history = window.OwnedWindows.OfType<FileHistoryDialog>().Single();
        try
        {
            await Idle(history);
            Assert.That(history.History.Select(entry => entry.Commit.Subject), Is.EqualTo(new[] { "Change literal file", "Rename literal file", "Initial commit" }));
            Assert.That(Field<TextBox>(history, "FileHistoryPath").Text, Is.EqualTo(LiteralPath));
            Assert.That(Field<TextBox>(history, "FileHistoryRevision").Text, Is.EqualTo(selected.Hash));
            Assert.That(Field<FileHistoryContentView>(history, "FileHistoryDiff").TextView.Text, Does.Contain("+    public int Value = 2;"));
            Assert.That(history.Owner, Is.SameAs(window));
            Assert.That(window.IsBusy, Is.True, "The owner must block overlapping repository operations while its modal viewer is open.");
        }
        finally { history.Close(); }
        await Until(() => !window.IsBusy);
        MenuItem blameAction = Action("tsmiBlame"); Assert.That(blameAction.IsEnabled, Is.True);
        blameAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); tree.ContextMenu!.Close();
        await Until(() => window.OwnedWindows.OfType<BlameDialog>().Any());
        BlameDialog blame = window.OwnedWindows.OfType<BlameDialog>().Single();
        try
        {
            await Idle(blame);
            Assert.That(blame.Owner, Is.SameAs(window));
            Assert.That(blame.LastBlame!.Path, Is.EqualTo(LiteralPath));
            Assert.That(blame.LastBlame.Revision, Is.EqualTo(selected.Hash));
            Assert.That(blame.LastBlame.Lines.Select(line => line.Line.AuthorName), Does.Contain("Béatrice"));
            Assert.That(Field<BlameView>(blame, "BlameView").TextView.Text, Does.Contain("Value = 2"));
        }
        finally { blame.Close(); }
        await Until(() => !window.IsBusy);
        Assert.That(window.LastException, Is.Null, window.LastException?.ToString());
        Assert.That(window.GetVisualDescendants().OfType<FileContentView>().Single(view => view.IsEffectivelyVisible).TextView.Text,
            Is.EqualTo(originalPreview), "Closing either native viewer preserves the selected file preview in its owner.");
    }

    [AvaloniaTest]
    public async Task HistoryFollowsLiteralRenamesAndKeepsClassicGraphTableAndTabsWithActualRevisionBlobs()
    {
        using Fixture fixture = await Fixture.CreateAsync(Initial, Initial);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await MakeHistoryAsync(fixture);
        string refs = (await repository.RunAsync(["show-ref"])).StandardOutput;
        FileHistoryDialog dialog = new(repository, LiteralPath);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.History.Select(entry => entry.Commit.Subject), Is.EqualTo(new[] { "Change literal file", "Rename literal file", "Initial commit" }));
            Assert.That(dialog.History.Select(entry => entry.Path), Is.EqualTo(new[] { LiteralPath, LiteralPath, "file.txt" }));
            Assert.That(dialog.History[1].PreviousPath, Is.EqualTo("file.txt"));
            ListBox list = Field<ListBox>(dialog, "FileHistoryRevisions");
            CommitGraph graph = Field<CommitGraph>(dialog, "FileHistoryGraph");
            TabControl tabs = Field<TabControl>(dialog, "FileHistoryTabs");
            dialog.UpdateLayout();
            Assert.Multiple(() =>
            {
                Assert.That(list.GetVisualDescendants().OfType<ListBoxItem>().Select(row => row.Bounds.Height), Is.All.EqualTo(24));
                Assert.That(graph.Commits.Count, Is.EqualTo(3));
                Assert.That(graph.TranslatePoint(default, dialog)!.Value.X + graph.Bounds.Width, Is.LessThanOrEqualTo(list.TranslatePoint(default, dialog)!.Value.X));
                Assert.That(list.TranslatePoint(default, dialog)!.Value.Y + list.Bounds.Height, Is.LessThanOrEqualTo(tabs.TranslatePoint(default, dialog)!.Value.Y));
                Assert.That(tabs.Items.OfType<TabItem>().Select(tab => ((StackPanel)tab.Header!).Children.OfType<TextBlock>().Single().Text), Is.EqualTo(new[] { "Commit", "Diff", "View", "Blame" }));
                Assert.That(tabs.Items.OfType<TabItem>().Select(tab => ((StackPanel)tab.Header!).Children.OfType<Image>().Single().Source), Is.All.Not.Null);
                Assert.That(Field<FileHistoryContentView>(dialog, "FileHistoryDiff").TextView.Text, Does.Contain("+    public int Value = 2;"));
            });

            list.SelectedItem = graph.OrderedCommits.Single(commit => commit.Subject == "Initial commit");
            await Idle(dialog);
            tabs.SelectedIndex = 2;
            await Idle(dialog);
            Assert.That(dialog.SelectedEntry!.Path, Is.EqualTo("file.txt"));
            Assert.That(Field<FileHistoryContentView>(dialog, "FileHistoryView").TextView.Text, Is.EqualTo(Initial), "The historical viewer must read the pre-rename blob.");
            list.SelectedItem = graph.OrderedCommits.Single(commit => commit.Subject == "Change literal file");
            await Idle(dialog);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                dialog.UpdateLayout();
                FileHistoryContentView content = Field<FileHistoryContentView>(dialog, "FileHistoryView");
                Assert.That(content.TextView.Editor.SyntaxHighlighting?.Name, Is.EqualTo("C#"), "The existing native grammar must be used for the selected filename.");
                Assert.That(((ISolidColorBrush)content.TextView.Editor.Background!).Color, Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#323232" : "#ffffff")));
                using WriteableBitmap frame = Capture(dialog, "classic-file-history-" + theme + ".png");
                Assert.That(BlueGlyphPixels(frame, dialog, content.TextView.Editor.TextArea.TextView), Is.GreaterThan(4), "The file syntax must be colored in the rendered editor.");
            }
            Option(dialog, "followFileHistoryToolStripMenuItem").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await Idle(dialog);
            Assert.That(dialog.History.Count, Is.EqualTo(2), "Disabling --follow must stop before the old filename.");
            tabs.SelectedIndex = 3; await Idle(dialog);
            BlameView blame = Field<BlameView>(dialog, "FileHistoryBlame");
            Assert.That(blame.Lines, Is.Not.Empty);
            Field<TextBox>(dialog, "FileHistoryPath").Text = "no-history-for-this-file.txt";
            Click(dialog, "LoadFileHistory"); await Idle(dialog);
            Assert.That(dialog.History, Is.Empty);
            Assert.That(blame.Lines, Is.Empty, "An empty reload must not retain attribution from the previous file.");
            Assert.That(tabs.Items.OfType<TabItem>().Skip(2).Select(tab => tab.IsEnabled), Is.All.False);
            Assert.That(Field<FileHistoryContentView>(dialog, "FileHistoryDiff").TextView.Text, Is.EqualTo("No commits found."));
            Assert.That((await repository.RunAsync(["show-ref"])).StandardOutput, Is.EqualTo(refs));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty, "History viewing must not alter the index or working tree.");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FullFileHistoryPreservesRealMergeParentsAndConnectsAcrossOmittedCommitsWithoutFalseHeadMarkers()
    {
        using Fixture fixture = await Fixture.CreateAsync("top\ncommon\nbottom\n", "top\ncommon\nbottom\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await Git(fixture, "checkout", "-b", "side");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "side top\ncommon\nbottom\n");
        await Git(fixture, "add", "--", "file.txt"); await Git(fixture, "commit", "-m", "Side changes file");
        await Git(fixture, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "top\ncommon\nmain bottom\n");
        await Git(fixture, "add", "--", "file.txt"); await Git(fixture, "commit", "-m", "Main changes file");
        GitResult merge = await fixture.Git.RunAsync(fixture.Repository, ["merge", "--no-ff", "side", "-m", "Merge file branches"]);
        if (!merge.Success)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "side top\ncommon\nmain bottom\n");
            await Git(fixture, "add", "--", "file.txt"); await Git(fixture, "commit", "-m", "Merge file branches");
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "unrelated.txt"), "unrelated\n");
        await Git(fixture, "add", "--", "unrelated.txt"); await Git(fixture, "commit", "-m", "Actual HEAD does not touch file");
        new FileHistoryPreferencesStore().Save(new() { FollowRenames = false, FullHistory = true });
        FileHistoryDialog dialog = new(repository, "file.txt"); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            GitCommit merged = dialog.History.Single(entry => entry.Commit.Subject == "Merge file branches").Commit;
            Assert.That(merged.Parents.Count, Is.EqualTo(2));
            Assert.That(merged.Parents, Is.EquivalentTo(dialog.History.Where(entry => entry.Commit.Subject is "Main changes file" or "Side changes file").Select(entry => entry.Commit.Hash)));
            CommitGraph graph = Field<CommitGraph>(dialog, "FileHistoryGraph");
            Assert.That(graph.CurrentHead, Is.EqualTo(await repository.ResolveRevisionAsync("HEAD")));
            Assert.That(graph.Commits.Any(commit => commit.Hash == graph.CurrentHead), Is.False, "A file-history row must not be falsely marked HEAD when HEAD was filtered out.");
            using var frame = Capture(dialog, "classic-file-history-merge.png");
            Assert.That(graph.Bounds.Width, Is.EqualTo(180));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ExactRenameFilteringAndDeletionUseRealGitHistoryWhileImagesAndSavePreserveBlobBytes()
    {
        using Fixture fixture = await Fixture.CreateAsync(Initial, Initial);
        await MakeHistoryAsync(fixture, modifyOnRename: true);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        Assert.That((await repository.GetFileHistoryAsync(LiteralPath)).Count, Is.EqualTo(3));
        Assert.That((await repository.GetFileHistoryAsync(LiteralPath, options: new(ExactRenames: true))).Count, Is.EqualTo(2),
            "Exact follow must reject a rename that also changed contents.");
        const string imagePath = "image λ[*].png";
        byte[] original = ImageBytes(2, 3, 0x19);
        byte[] changed = ImageBytes(3, 2, 0xa1);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, imagePath), original);
        await repository.StageAsync([imagePath]); await repository.CommitAsync("Image first");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, imagePath), changed);
        await repository.StageAsync([imagePath]); await repository.CommitAsync("Image changed");
        await repository.RunAsync(["rm", "--", imagePath]); await repository.CommitAsync("Image deleted");
        FileHistoryDialog dialog = new(repository, imagePath);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.SelectedEntry!.IsDeleted, Is.True);
            TabControl tabs = Field<TabControl>(dialog, "FileHistoryTabs");
            Assert.That(tabs.Items.OfType<TabItem>().Skip(2).Select(tab => tab.IsEnabled), Is.All.False);
            Assert.That(Field<FileHistoryContentView>(dialog, "FileHistoryDiff").TextView.Text, Does.Contain("deleted file mode"));
            Field<ListBox>(dialog, "FileHistoryRevisions").SelectedItem = dialog.History.Single(entry => entry.Commit.Subject == "Image first").Commit;
            await Idle(dialog); tabs.SelectedIndex = 2; await Idle(dialog);
            FileHistoryContentView view = Field<FileHistoryContentView>(dialog, "FileHistoryView");
            Assert.That(view.IsImage, Is.True, "Image preview must be decoded from the selected Git blob despite its deletion from disk.");
            Image image = view.GetVisualDescendants().OfType<Image>().Single();
            Assert.That(((Bitmap)image.Source!).PixelSize, Is.EqualTo(new PixelSize(2, 3)));
            string saved = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "saved literal image.png");
            await dialog.SaveSelectedFileAsync(saved);
            Assert.That(await File.ReadAllBytesAsync(saved), Is.EqualTo(original), "Save as must preserve the original blob, not the current or decoded image.");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingHistoryDuringGitLoadCancelsItsProcessAndRejectsInvalidLiteralPaths()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync(Initial, Initial);
        (GitRepository repository, string started) = SlowRepository(fixture, "log");
        FileHistoryDialog dialog = new(repository, "file.txt");
        dialog.Show();
        Task load = dialog.InitializeAsync();
        await Until(() => File.Exists(started));
        Assert.That(Field<TextBox>(dialog, "FileHistoryPath").IsEnabled, Is.False);
        Click(dialog, "CancelFileHistory");
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => !dialog.IsVisible);
        Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["status", "--porcelain"])).StandardOutput, Is.Empty);
        GitRepository real = await GitRepository.OpenAsync(fixture.Repository);
        await ExpectAsync<ArgumentException>(() => real.GetFileHistoryAsync("../outside"));
        await ExpectAsync<GitCommandException>(() => real.GetFileHistoryAsync("file.txt", "missing-ref"));
    }

    internal static async Task MakeHistoryAsync(Fixture fixture, bool modifyOnRename = false)
    {
        await Git(fixture, "mv", "--", "file.txt", LiteralPath);
        if (modifyOnRename) { await File.AppendAllTextAsync(Path.Combine(fixture.Repository, LiteralPath), "// changed during rename\n"); }
        await Git(fixture, "add", "--", LiteralPath);
        await Git(fixture, "commit", "-m", "Rename literal file");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "unrelated.txt"), "Unrelated history must be omitted.\n");
        await Git(fixture, "add", "--", "unrelated.txt"); await Git(fixture, "commit", "-m", "Unrelated commit");
        string text = await File.ReadAllTextAsync(Path.Combine(fixture.Repository, LiteralPath));
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, LiteralPath), text.Replace("Value = 1", "Value = 2", StringComparison.Ordinal));
        await Git(fixture, "add", "--", LiteralPath);
        await Git(fixture, "commit", "--author=Béatrice <beatrice@example.invalid>", "-m", "Change literal file\n\nActual author commit body.");
    }
    internal static async Task Git(Fixture fixture, params string[] arguments)
    { GitResult result = await fixture.Git.RunAsync(fixture.Repository, ["--literal-pathspecs", .. arguments]); Assert.That(result.Success, Is.True, result.StandardError); }
    internal static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal static MenuItem Option(FileHistoryDialog window, string name)
        => new[] { Field<ListBox>(window, "FileHistoryRevisions").ContextMenu!, Field<Button>(window, "ShowFullHistory").ContextMenu! }
            .SelectMany(menu => menu.Items.OfType<MenuItem>()).Single(item => item.Name == name);
    internal static void Click(Window window, string name)
    { Button button = Field<Button>(window, name); Assert.That(button.IsEnabled, Is.True, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    internal static async Task Idle(RepositoryDialog dialog)
    { await Until(() => !dialog.IsBusy && dialog.GetVisualDescendants().OfType<CommitInformationView>().All(view => !view.IsLoading)); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString() ?? dialog.LastError); }
    internal static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } Assert.Fail("Expected " + typeof(T).Name); }
    internal static WriteableBitmap Capture(Window window, string name)
    {
        window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No native frame.");
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(path); TestContext.AddTestAttachment(path); return frame;
    }
    internal static byte[] ImageBytes(int width, int height, byte value)
    {
        using WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            byte[] pixels = Enumerable.Range(0, buffer.RowBytes * height).Select(index => index % 4 == 3 ? (byte)255 : value).ToArray();
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }
        using MemoryStream stream = new(); bitmap.Save(stream); return stream.ToArray();
    }
    internal static (GitRepository Repository, string Started) SlowRepository(Fixture fixture, string command, string? startWhenFileExists = null)
    {
        if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        string directory = Path.GetDirectoryName(fixture.Repository)!;
        string started = Path.Combine(directory, "slow-" + command + ".started");
        string script = Path.Combine(directory, "slow-" + command + ".py");
        File.WriteAllText(script, "#!/usr/bin/python3\nimport os, pathlib, sys, time\n" +
            "if " + System.Text.Json.JsonSerializer.Serialize(command) + " in sys.argv[1:]" + (startWhenFileExists is null ? "" : " and pathlib.Path(" + System.Text.Json.JsonSerializer.Serialize(startWhenFileExists) + ").exists()") + ":\n" +
            "    pathlib.Path(__file__).with_suffix('.started').write_text(str(os.getpid()))\n    time.sleep(30)\n" +
            "os.execvp('git', ['git'] + sys.argv[1:])\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (new(fixture.Repository, new GitRunner(script)), started);
    }
    private static int BlueGlyphPixels(WriteableBitmap frame, Window owner, Control editor)
    {
        Point top = editor.TranslatePoint(default, owner)!.Value;
        using ILockedFramebuffer buffer = frame.Lock();
        byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        int count = 0;
        for (int y = Math.Max(0, (int)top.Y); y < Math.Min(buffer.Size.Height, (int)(top.Y + editor.Bounds.Height)); y++)
        for (int x = Math.Max(0, (int)top.X); x < Math.Min(buffer.Size.Width, (int)(top.X + editor.Bounds.Width)); x++)
        {
            int offset = y * buffer.RowBytes + x * 4;
            byte r = bytes[offset + (buffer.Format == PixelFormat.Rgba8888 ? 0 : 2)], g = bytes[offset + 1], b = bytes[offset + (buffer.Format == PixelFormat.Rgba8888 ? 2 : 0)];
            if (b > r + 25 && b > g + 25) { count++; }
        }
        return count;
    }
}
