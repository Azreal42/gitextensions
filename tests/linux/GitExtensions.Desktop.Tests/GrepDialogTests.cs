using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class GrepDialogTests
{
    [AvaloniaTest]
    public async Task OriginalCompactFindFiltersFilesAndReadsTheImmutableCp1252BlobAtItsByteColumn()
    {
        await using Fixture f = await Fixture.CreateAsync(); const string path = ":file[1]* λ\n雪:42.txt";
        Encoding cp = GitFileEncodingCodec.GetEncoding("windows-1252"); string source = "first\r\npré: café €\r\nlast\r\n";
        await f.WriteAsync(path, cp.GetBytes(source)); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Legacy blob");
        await f.Repository.SetConfigAsync("i18n.filesEncoding", "windows-1252"); await f.WriteAsync(path, cp.GetBytes("different worktree\n"));
        MainWindow main = await f.ShowAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD"), before = await f.SnapshotAsync();
        FindInCommitFilesDialog dialog = await Open(main); Enter(dialog, "café"); Click(Find<Button>(dialog, "GrepFind"));
        await Until(() => !dialog.IsVisible && !main.IsFileGrepBusy && main.FileGrepResult is not null);
        TreeView files = Find<TreeView>(main, "CommitFileTree"); Assert.That(FileStatusTestHelpers.Rows(files).Select(item => item.Tag).OfType<string>(), Is.EqualTo(new[] { path }));
        FileContentView content = Find<FileContentView>(main, "CommitFileContent");
        Assert.That(content.TextView.Text, Is.EqualTo(source)); Assert.That(content.TextView.SyntaxPath, Is.EqualTo(path));
        Assert.That(content.TextView.DocumentIdentity, Is.EqualTo(string.Join('\0', f.Root, path, hash, "blob")));
        Assert.That(content.TextView.CurrentFileLine, Is.EqualTo(2)); Assert.That(content.TextView.Editor.TextArea.Caret.Column, Is.EqualTo(6));
        Assert.That(main.FileGrepResult!.Matches.Single().ByteColumn, Is.EqualTo(6)); Assert.That(content.TextView.Editor.TextArea.IsFocused, Is.True);
        content.TextView.Select(0, 5);
        dialog = await Open(main); Click(Find<Button>(dialog, "GrepFind")); await Until(() => !dialog.IsVisible && !main.IsFileGrepBusy); Pump(main);
        Assert.That(content.TextView.IsDocumentReady, Is.True); Assert.That(content.TextView.CurrentFileLine, Is.EqualTo(2));
        Assert.That(content.TextView.Editor.TextArea.Caret.Column, Is.EqualTo(6)); Assert.That(content.TextView.SelectionLength, Is.Zero, "A deferred old reading selection must not replace the requested hit after rendering the same source.");
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(await File.ReadAllBytesAsync(f.File(path)), Is.EqualTo(cp.GetBytes("different worktree\n")));
    }

    [AvaloniaTest]
    public async Task CloseRetainsActiveFilterAndSessionMruAndDisablingOrClearingRestoresTheFileList()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("matching.txt", "target\n"); await f.CommitAsync(); MainWindow main = await f.ShowAsync();
        FindInCommitFilesDialog dialog = await Open(main); Enter(dialog, "target"); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main);
        await Until(() => !dialog.IsVisible); main.RequestedThemeVariant = ThemeVariant.Dark; dialog = await Open(main);
        Assert.That(Find<AutoCompleteBox>(dialog, "GrepExpression").ItemsSource!.Cast<string>(), Does.Contain("target"));
        Assert.That(Find<AutoCompleteBox>(dialog, "GrepExpression").Text, Is.EqualTo("target"));
        Assert.That(Find<AutoCompleteBox>(dialog, "GrepExpression").GetVisualDescendants().OfType<TextBox>().Single().Text, Is.EqualTo("target"));
        Assert.That(dialog.ActualThemeVariant, Is.EqualTo(main.ActualThemeVariant));
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !dialog.IsVisible); Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EqualTo(new[] { "matching.txt" }));
        dialog = await Open(main); Click(Find<CheckBox>(dialog, "GrepShowSearchBox")); dialog.Close(); await Until(() => !dialog.IsVisible);
        Assert.That(main.FileGrepResult, Is.Null); Assert.That(Find<Control>(main, "FileGrepBar").IsVisible, Is.False);
        Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EquivalentTo(new[] { "seed.txt", "matching.txt" }));
        dialog = await Open(main); Enter(dialog, "target"); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible);
        TextBox input = Find<TextBox>(main, "FileGrepExpression"); input.Focus(); input.SelectAll(); main.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None); Pump(main);
        Assert.That(main.FileGrepResult, Is.Null); Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Count(item => item.Tag is string), Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task NativeCaseWholeWordAndOptionsDriveTheFilterAndInvalidRegexRestoresTheSourceList()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("exact.txt", "CAT\n"); await f.WriteAsync("suffix.txt", "catfish\n"); await f.CommitAsync(); MainWindow main = await f.ShowAsync();
        FindInCommitFilesDialog dialog = await Open(main); Enter(dialog, "cat"); Click(Find<CheckBox>(dialog, "GrepMatchCase")); Click(Find<CheckBox>(dialog, "GrepMatchWholeWord"));
        TextBox options = Find<TextBox>(dialog, "GrepOptions"); options.Focus(); dialog.KeyTextInput("--fixed-strings"); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible);
        Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EqualTo(new[] { "exact.txt" }));
        dialog = await Open(main); Enter(dialog, "["); options = Find<TextBox>(dialog, "GrepOptions"); options.Focus(); options.SelectAll(); dialog.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible);
        Assert.That(main.LastException, Is.TypeOf<GitCommandException>()); Assert.That(main.FileGrepResult, Is.Null);
        Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EquivalentTo(new[] { "seed.txt", "exact.txt", "suffix.txt" }));
    }

    [AvaloniaTest]
    public async Task CommitAndRepositoryChangesRecalculateActiveSearchAndCannotDisplayEarlierResults()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.WriteAsync("old.txt", "target old\n"); await f.CommitAsync(); string oldHash = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.WriteAsync("old.txt", "gone\n"); await f.WriteAsync("new.txt", "target new\n"); await f.CommitAsync(); MainWindow main = await f.ShowAsync();
        FindInCommitFilesDialog dialog = await Open(main); Enter(dialog, "target"); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible);
        Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EqualTo(new[] { "new.txt" }));
        ListBox commits = Find<ListBox>(main, "RevisionHistory"); commits.SelectedItem = commits.Items.OfType<GitCommit>().Single(commit => commit.Hash == oldHash);
        await Until(() => !main.IsBusy && !main.IsFileGrepBusy && main.FileGrepResult?.Revision == oldHash);
        Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EqualTo(new[] { "old.txt" }));
        Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.Text, Is.EqualTo("target old\n"));
        GitRepository other = await GitRepository.InitAsync(Path.Combine(f.Root, "other-repository")); await Fixture.Configure(other);
        await File.WriteAllTextAsync(Path.Combine(other.Path, "other.txt"), "no match\n"); await other.StageAllAsync(); await other.CommitAsync("Other source");
        await main.OpenRepositoryAsync(other.Path); await Until(() => !main.IsBusy && !main.IsFileGrepBusy && main.FileGrepResult is not null);
        Assert.That(main.FileGrepResult!.Revision, Is.EqualTo(await other.ResolveRevisionAsync("HEAD"))); Assert.That(main.FileGrepResult.HasMatches, Is.False);
        Assert.That(Find<TreeView>(main, "CommitFileTree").Items, Is.Empty); Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.Text, Is.Empty.Or.Null);
        Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.IsDocumentReady, Is.False); Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.DocumentIdentity, Is.Null);
    }

    [AvaloniaTest]
    public async Task FrenchOriginalCatalogueAndBothThemesKeepFindAndEverySettingVisible()
    {
        await using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR"); MainWindow main = await f.ShowAsync();
        FindInCommitFilesDialog dialog = await Open(main);
        Assert.That(dialog.Title, Is.Not.EqualTo("Find in commit files using git-grep"));
        Assert.That(((Avalonia.Controls.Primitives.AccessText)Find<CheckBox>(dialog, "GrepMatchCase").Content!).Text, Is.Not.EqualTo("Match _case"));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; Pump(dialog);
            var expression = Find<AutoCompleteBox>(dialog, "GrepExpression");
            var findLabel = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.AccessText>().Single(label => label.Text?.Replace("_", "").StartsWith("Texte à rechercher", StringComparison.Ordinal) == true);
            Point labelPoint = findLabel.TranslatePoint(default, dialog)!.Value, inputPoint = expression.TranslatePoint(default, dialog)!.Value;
            Assert.That(labelPoint.X + findLabel.Bounds.Width, Is.LessThanOrEqualTo(inputPoint.X), "The translated Find what label must not overlap the input.");
            foreach (string name in new[] { "GrepExpression", "GrepOptions", "GrepMatchCase", "GrepMatchWholeWord", "GrepShowSearchBox", "GrepFind" })
            {
                Control item = Find<Control>(dialog, name); Assert.That(item.IsEffectivelyVisible, Is.True, name);
                Point point = item.TranslatePoint(default, dialog)!.Value; Assert.That(point.Y + item.Bounds.Height, Is.LessThanOrEqualTo(dialog.Bounds.Height), name);
            }
            Capture(dialog, "grep-french-" + theme + ".png");
        }
        dialog.Close();
    }

    [AvaloniaTest]
    public async Task NativeBinaryMatchKeepsTheLiteralFileAndReadsItsImmutableBlobWithoutInventingCoordinates()
    {
        await using Fixture f = await Fixture.CreateAsync(); const string path = ":binary[1]* λ\nmatch.bin";
        byte[] source = Encoding.UTF8.GetBytes("\0target in binary\n"); await f.WriteAsync(path, source); await f.CommitAsync();
        string hash = await f.Repository.ResolveRevisionAsync("HEAD"); await f.WriteAsync(path, Encoding.UTF8.GetBytes("different worktree\n"));
        MainWindow main = await f.ShowAsync(); string before = await f.SnapshotAsync();
        FindInCommitFilesDialog dialog = await Open(main); Enter(dialog, "target"); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible);
        Assert.That(main.FileGrepResult!.HasMatches, Is.True); Assert.That(main.FileGrepResult.Matches, Is.Empty);
        GitGrepBinaryMatch binary = main.FileGrepResult.BinaryMatches.Single(); Assert.That(binary.Path, Is.EqualTo(path)); Assert.That(binary.PathBytes.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes(path)));
        Assert.That(FileStatusTestHelpers.Rows(Find<TreeView>(main, "CommitFileTree")).Select(item => item.Tag).OfType<string>(), Is.EqualTo(new[] { path }));
        FileContentView content = Find<FileContentView>(main, "CommitFileContent"); Assert.That(content.TextView.Text, Is.EqualTo("Binary file\n" + Convert.ToHexString(source)));
        Assert.That(content.TextView.SyntaxPath, Is.Null); Assert.That(content.TextView.DocumentIdentity, Is.EqualTo(string.Join('\0', f.Root, path, hash, "blob")));
        Assert.That(main.LastException, Is.Null);
        content.TextView.Select(12, 4); dialog = await Open(main); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible); Pump(main);
        Assert.That(content.TextView.SelectionLength, Is.EqualTo(4), "A binary filename match has no source coordinates that could override the reader's position.");
        dialog = await Open(main); TextBox options = Find<TextBox>(dialog, "GrepOptions"); options.Focus(); dialog.KeyTextInput("-I"); Click(Find<Button>(dialog, "GrepFind")); await GrepIdle(main); await Until(() => !dialog.IsVisible);
        Assert.That(main.FileGrepResult!.HasMatches, Is.False); Assert.That(Find<TreeView>(main, "CommitFileTree").Items, Is.Empty);
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(await File.ReadAllBytesAsync(f.File(path)), Is.EqualTo(Encoding.UTF8.GetBytes("different worktree\n")));
    }

    [AvaloniaTest]
    public async Task ClosingTheMainWindowCancelsItsOwnedGrepProcessAndPreservesGit()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux process cancellation fixture."); return; }
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = await f.ShowAsync(); string before = await f.SnapshotAsync();
        string executable = f.Control("slow-git"), marker = f.Control("grep.pid");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nfor task_arg do\n case \"$task_arg\" in grep) printf '%s\\n' \"$$\" > '" + marker + "'; sleep 60 & wait ;; esac\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, new GitRepository(f.Root, new GitRunner(executable)));
        FindInCommitFilesDialog dialog = await Open(main); Enter(dialog, "seed"); Click(Find<Button>(dialog, "GrepFind")); await Until(() => File.Exists(marker));
        main.Close(); await Until(() => !main.IsFileGrepBusy); int pid = int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture);
        await Until(() => !Directory.Exists("/proc/" + pid)); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
    }

    private static T Find<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void Click(Control item)
    {
        TopLevel root = TopLevel.GetTopLevel(item)!; Pump(root); Assert.That(item.IsEffectivelyVisible && item.IsEffectivelyEnabled, Is.True, item.Name);
        Point p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
        root.MouseMove(p); root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump(root);
    }
    private static void Enter(FindInCommitFilesDialog dialog, string text)
    {
        AutoCompleteBox expression = Find<AutoCompleteBox>(dialog, "GrepExpression");
        TextBox input = expression.GetVisualDescendants().OfType<TextBox>().Single(); input.Focus(); input.SelectAll(); dialog.KeyTextInput(text); Pump(dialog);
    }
    private static async Task Until(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.That(condition(), Is.True, "Grep UI did not reach the expected state.");
    }
    private static async Task GrepIdle(MainWindow main) { await Task.Delay(30); await Until(() => !main.IsBusy && !main.IsFileGrepBusy); Pump(main); }
    private static async Task<FindInCommitFilesDialog> Open(MainWindow main)
    {
        await Until(() => !main.IsBusy);
        TreeView tree = Find<TreeView>(main, "CommitFileTree"); tree.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Avalonia.Controls.Control.ContextRequestedEvent, Source = tree }); Pump(main);
        await Until(() => tree.ContextMenu!.Items.Count > 0);
        tree.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiOpenFindInCommitFilesGitGrepDialog").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        tree.ContextMenu!.Close(); await Until(() => main.OwnedWindows.OfType<FindInCommitFilesDialog>().Any());
        FindInCommitFilesDialog dialog = main.OwnedWindows.OfType<FindInCommitFilesDialog>().Single(); Pump(dialog);
        Assert.That(Find<AutoCompleteBox>(dialog, "GrepExpression").GetVisualDescendants().OfType<TextBox>().Single().IsFocused, Is.True); return dialog;
    }
    private static void Capture(TopLevel root, string filename)
    {
        Pump(root); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var image = root.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-grep-proof"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, filename); image!.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-grep-ui-" + Guid.NewGuid().ToString("N"));
        internal GitRepository Repository => new(Root);
        internal string File(string path) => Path.Combine(Root, path);
        internal string Control(string path) { Directory.CreateDirectory(Root + ".control"); return Path.Combine(Root + ".control", path); }
        private MainWindow? _main;
        private readonly Dictionary<string, string?> _environment = new();
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private Fixture()
        {
            foreach ((string key, string value) in new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = Root + ".config", ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" })
            { _environment[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(); await GitRepository.InitAsync(f.Root); await Configure(f.Repository);
            await f.WriteAsync("seed.txt", "seed\n"); await f.CommitAsync(); return f;
        }
        internal static async Task Configure(GitRepository r)
        { await r.SetConfigAsync("user.name", "Grep Tester"); await r.SetConfigAsync("user.email", "grep@example.invalid"); await r.SetConfigAsync("commit.gpgsign", "false"); await r.SetConfigAsync("core.hooksPath", "/dev/null"); }
        internal Task WriteAsync(string path, string text) => WriteAsync(path, Encoding.UTF8.GetBytes(text));
        internal Task WriteAsync(string path, byte[] bytes) => System.IO.File.WriteAllBytesAsync(File(path), bytes);
        internal async Task CommitAsync() { await Repository.StageAllAsync(); await Repository.CommitAsync("Grep source"); }
        internal async Task<MainWindow> ShowAsync()
        {
            _main = new MainWindow { Width = 1280, Height = 752 }; _main.Show(); await _main.OpenRepositoryAsync(Root); await GrepIdle(_main);
            TabControl tabs = Find<TabControl>(_main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "FileTreeTab"); Pump(_main); return _main;
        }
        internal async Task<string> SnapshotAsync() => (await Repository.RunAsync(["status", "--porcelain=v2", "-z"])).StandardOutput
            + (await Repository.RunAsync(["ls-files", "--stage", "-z"])).StandardOutput + (await Repository.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"])).StandardOutput;
        public async ValueTask DisposeAsync()
        {
            if (_main is not null) { foreach (Window child in _main.OwnedWindows.ToArray()) { child.Close(); } _main.Close(); await GrepIdle(_main); }
            foreach ((string key, string? value) in _environment) { Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = _culture; Directory.Delete(Root, true); if (Directory.Exists(Root + ".config")) { Directory.Delete(Root + ".config", true); }
            if (Directory.Exists(Root + ".control")) { Directory.Delete(Root + ".control", true); }
        }
    }
}
