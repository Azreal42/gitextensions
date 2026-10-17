using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RepositoryFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ArtificialHistoryRevisionTests
{
    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task StartupArtificialRevisionShowsItsCompleteTypedDiffAndItsRealFileMenuState(GitCompareRevisionKind kind)
    {
        await using Fixture fixture = await Fixture.CreateAsync(kind);
        MainWindow main = fixture.Window;
        TreeView files = Field<TreeView>(main, "RevisionFiles");
        DiffView viewer = Field<DiffView>(main, "CommitDiff");
        RevisionDiffFileSelection[] entries = FileStatusTestHelpers.Rows(files).Select(row => row.Tag).OfType<RevisionDiffFileSelection>().ToArray();
        Assert.That(entries.Select(entry => entry.File.Path), Is.EquivalentTo(kind == GitCompareRevisionKind.Index
            ? new[] { "file.txt", "other.txt" } : new[] { "file.txt", "other.txt", "untracked.txt" }), "The path history filter must not prune the artificial revision's complete file list.");
        Assert.That(((RevisionDiffFileSelection)((TreeViewItem)files.SelectedItem!).Tag!).File.Path, Is.EqualTo("file.txt"));
        Assert.That(viewer.DocumentIdentity, Does.Contain(kind.ToString()));
        Assert.That(viewer.Text, Does.Contain(kind == GitCompareRevisionKind.Index ? "+INDEX FILE" : "+WORKTREE FILE"));
        Assert.That(viewer.Text, Does.Contain(kind == GitCompareRevisionKind.Index ? "-base" : "-INDEX FILE"));
        Assert.That(viewer.Text, Does.Not.Contain(kind == GitCompareRevisionKind.Index ? "WORKTREE FILE" : "+INDEX FILE"));
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.Cast<object>().All(item => item is GitCommit), Is.True);
        Assert.That(Field<ListBox>(main, "RevisionHistory").SelectedItem, Is.Null);
        Assert.That(((TabItem)Field<TabControl>(main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        OpenMenu(main, files, "file.txt");
        ContextMenu menu = files.ContextMenu!;
        string stage = kind == GitCompareRevisionKind.Index ? "tsmiUnstageFile" : "tsmiStageFile";
        Assert.That(menu.Items.OfType<MenuItem>().Single(item => item.Name == stage).IsEnabled, Is.True);
        Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == (kind == GitCompareRevisionKind.Index ? "tsmiStageFile" : "tsmiUnstageFile")), Is.False);
        MenuItem reset = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileTo");
        Assert.That(reset.Items.OfType<MenuItem>().Any(item => item.Name == "tsmiResetFileToSelected"), Is.False,
            "Restoring the artificial second side to itself is not an available reset operation.");
        object? header = reset.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileToParent").Header;
        string first = header is Avalonia.Controls.Primitives.AccessText access ? access.Text ?? "" : header?.ToString() ?? "";
        Assert.That(first, Does.Contain(kind == GitCompareRevisionKind.Index ? fixture.Head[..8] : "Index"));
        menu.Close();
        Assert.That(await fixture.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(fixture.Head));
        Assert.That(System.Text.Encoding.UTF8.GetString(await fixture.Git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("INDEX FILE\n"));
    }

    [AvaloniaTest]
    public async Task StagingTheArtificialWorkingTreeFileUsesOnlyItsRealSelectionAndKeepsThatRevisionOnRefresh()
    {
        await using Fixture fixture = await Fixture.CreateAsync(GitCompareRevisionKind.WorkingTree);
        MainWindow main = fixture.Window;
        TreeView files = Field<TreeView>(main, "RevisionFiles");
        OpenMenu(main, files, "file.txt");
        PushProcessTestDriver.Pointer(files.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiStageFile"));
        await Until(() => !main.IsBusy);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        Assert.That(System.Text.Encoding.UTF8.GetString(await fixture.Git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("WORKTREE FILE\n"));
        Assert.That(System.Text.Encoding.UTF8.GetString(await fixture.Git.Repository.GetIndexFileBytesAsync("other.txt")), Is.EqualTo("INDEX OTHER\n"));
        Assert.That((await fixture.Git.GitAsync("ls-files", "--", "untracked.txt")).StandardOutput, Is.Empty);
        Assert.That(Field<ListBox>(main, "RevisionHistory").SelectedItem, Is.Null);
        Assert.That(FileStatusTestHelpers.Rows(files).Select(row => row.Tag).OfType<RevisionDiffFileSelection>().Select(entry => entry.File.Path),
            Is.EquivalentTo(new[] { "other.txt", "untracked.txt" }));
        Assert.That(await fixture.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(fixture.Head));
    }

    [AvaloniaTest]
    public async Task FirstDownFromWorkingThenIndexReachesTheRealGridWithoutARepairClick()
    {
        await using Fixture fixture = await Fixture.CreateAsync(GitCompareRevisionKind.WorkingTree);
        MainWindow main = fixture.Window;
        Button working = Field<Button>(main, "WorkingDirectoryRevision"), index = Field<Button>(main, "IndexRevision");
        PushProcessTestDriver.Pointer(working); await Idle(main);
        Assert.That(working.IsKeyboardFocusWithin, Is.True);
        Press(main, PhysicalKey.ArrowDown); await Idle(main);
        Assert.That(index.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<DiffView>(main, "CommitDiff").Text, Does.Contain("+INDEX FILE"));
        Press(main, PhysicalKey.ArrowDown); await Idle(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        Assert.That(history.SelectedItem, Is.TypeOf<GitCommit>());
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<DiffView>(main, "CommitDiff").DocumentIdentity, Does.Not.Contain(GitCompareRevisionKind.Index.ToString()));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index, 3, 4)]
    [TestCase(GitCompareRevisionKind.WorkingTree, 5, 6)]
    public async Task ArtificialImageReadsTheIndexOrWorkingTreeBytesInsteadOfHead(GitCompareRevisionKind kind, int width, int height)
    {
        await using Fixture fixture = await Fixture.CreateAsync(kind, image: true);
        MainWindow main = fixture.Window;
        TreeView files = Field<TreeView>(main, "RevisionFiles");
        TreeViewItem row = FileStatusTestHelpers.Rows(files).Single(item => item.Tag is RevisionDiffFileSelection { File.Path: "image.png" });
        PushProcessTestDriver.Pointer(Header(row)); await Idle(main);
        FileContentView content = main.GetVisualDescendants().OfType<FileContentView>().Single(view => view.TextView.Name == "CommitDiff");
        Assert.That(content.IsImage, Is.True);
        Assert.That(content.Bitmap!.PixelSize, Is.EqualTo(new PixelSize(width, height)));
    }

    [AvaloniaTest]
    public async Task AnObsoleteIndexDiffIsCancelledWhenTheUserSelectsTheWorkingTree()
    {
        await using Fixture fixture = await Fixture.CreateAsync(GitCompareRevisionKind.WorkingTree);
        using IndexDiffGate gate = new(fixture.Git);
        MainWindow main = fixture.Window;
        GitCommandLog.Clear();
        PushProcessTestDriver.Pointer(Field<Button>(main, "IndexRevision"));
        await Until(() => gate.Started);
        int pid = gate.Pid;
        PushProcessTestDriver.Pointer(Field<Button>(main, "WorkingDirectoryRevision"));
        await Until(() => !main.IsBusy);
        Assert.That(Field<DiffView>(main, "CommitDiff").Text, Does.Contain("+WORKTREE FILE").And.Not.Contain("+INDEX FILE"));
        Assert.That(Field<DiffView>(main, "CommitDiff").DocumentIdentity, Does.Contain(GitCompareRevisionKind.WorkingTree.ToString()));
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Cancelled), Is.True);
        Assert.That(ProcessAlive(pid), Is.False, "The actual obsolete Git read must have stopped before completion.");
        Assert.That(await fixture.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(fixture.Head));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal RepositoryFixture Git { get; }
        internal MainWindow Window { get; }
        internal string Head { get; }
        private Fixture(RepositoryFixture git, MainWindow window, string head) { Git = git; Window = window; Head = head; }

        internal static async Task<Fixture> CreateAsync(GitCompareRevisionKind kind, bool image = false)
        {
            RepositoryFixture git = await RepositoryFixture.CreateAsync();
            MainWindow? main = null;
            try
            {
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "BASE OTHER\n");
                if (image) { WriteImage(git.PathInRepository("image.png"), 2, 2); }
                await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Other base file");
                string head = await git.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(git.File, "INDEX FILE\n");
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "INDEX OTHER\n");
                if (image) { WriteImage(git.PathInRepository("image.png"), 3, 4); }
                await git.Repository.StageAllAsync();
                await File.WriteAllTextAsync(git.File, "WORKTREE FILE\n");
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "WORKTREE OTHER\n");
                await File.WriteAllTextAsync(git.PathInRepository("untracked.txt"), "UNTRACKED\n");
                if (image) { WriteImage(git.PathInRepository("image.png"), 5, 6); }
                main = new MainWindow(new BrowseRequest(git.Repository.Path, new("file.txt", BrowsePathKind.File), new("", kind))) { Width = 1440, Height = 900 };
                main.Show(); await Idle(main);
                Assert.That(FileStatusTestHelpers.Rows(Field<TreeView>(main, "RevisionFiles")).Any(row => row.Tag is RevisionDiffFileSelection), Is.True);
                return new(git, main, head);
            }
            catch
            {
                if (main is not null) { main.Close(); await Until(() => !main.IsVisible && !main.IsBusy && main.CommitInformationCompletion.IsCompleted); }
                git.Dispose(); throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Until(() => !Window.IsVisible && !Window.IsBusy && Window.CommitInformationCompletion.IsCompleted);
            Git.Dispose();
        }
    }

    private sealed class IndexDiffGate : IDisposable
    {
        private readonly RepositoryFixture _fixture;
        private readonly string? _path;
        private readonly string _release, _marker;
        internal bool Started => File.Exists(_marker);
        internal int Pid => int.Parse(File.ReadAllText(_marker), System.Globalization.CultureInfo.InvariantCulture);
        internal IndexDiffGate(RepositoryFixture fixture)
        {
            _fixture = fixture; _path = Environment.GetEnvironmentVariable("PATH");
            string root = fixture.Output("index-read-gate"); Directory.CreateDirectory(root);
            _release = Path.Combine(root, "release"); _marker = Path.Combine(root, "started");
            string executable = Path.Combine(root, "git");
            string script = "#!/usr/bin/python3\nimport os,sys,time\na=sys.argv[1:]\nif 'diff' in a and '--cached' in a and '--name-status' not in a and '--raw' not in a and 'file.txt' in a:\n with open(" + System.Text.Json.JsonSerializer.Serialize(_marker) + ",'w') as f: f.write(str(os.getpid()))\n while not os.path.exists(" + System.Text.Json.JsonSerializer.Serialize(_release) + "): time.sleep(.01)\nos.execv('/usr/bin/git',['git']+a)\n";
            File.WriteAllText(executable, script);
            if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            fixture.SetEnvironment("PATH", root + Path.PathSeparator + _path);
        }
        public void Dispose() { File.WriteAllText(_release, "release"); _fixture.SetEnvironment("PATH", _path); }
    }

    private static bool ProcessAlive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static void WriteImage(string path, int width, int height)
    {
        using WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (ILockedFramebuffer buffer = bitmap.Lock()) { Marshal.Copy(new byte[buffer.RowBytes * height], 0, buffer.Address, buffer.RowBytes * height); }
        bitmap.Save(path);
    }
    private static Control Header(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
        && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), row));
    private static void OpenMenu(MainWindow main, TreeView files, string path)
    {
        TreeViewItem row = FileStatusTestHelpers.Rows(files).Single(item => item.Tag is RevisionDiffFileSelection selection && selection.File.Path == path);
        PushProcessTestDriver.Pointer(Header(row));
        Control header = Header(row); Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), main)!.Value;
        Assert.That((main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(header), Is.True);
        main.MouseMove(point); main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right); Dispatcher.UIThread.RunJobs();
        Assert.That(files.ContextMenu!.IsOpen, Is.True);
    }
    private static void Press(MainWindow main, PhysicalKey key)
    { main.KeyPressQwerty(key, RawInputModifiers.None); main.KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static T Field<T>(MainWindow main, string name) where T : Control => PushProcessTestDriver.Field<T>(main, name);
    private static async Task Idle(MainWindow main)
    { await Until(() => !main.IsBusy); Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(condition(), Is.True, "The real artificial revision read did not settle.");
    }
}
