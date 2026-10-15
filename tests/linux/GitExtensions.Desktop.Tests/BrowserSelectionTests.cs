using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BrowserSelectionTests
{
    [AvaloniaTest]
    public async Task ClickingACommitKeepsKeyboardNavigationAndOnlyTheLatestSelectionPublishesWhileGitIsSlow()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        TreeView files = Find<TreeView>(main, "RevisionFiles");
        FileStatusListView hiddenTree = Find<FileStatusListView>(main, "CommitFileStatusList");
        DiffView viewer = Find<DiffView>(main, "CommitDiff");
        object? historySource = history.ItemsSource;
        object[] hiddenRoots = hiddenTree.Tree.Items.Cast<object>().ToArray();
        string oldText = viewer.Text;
        List<Control> disabled = [];
        foreach (Control control in new Control[] { history, files, hiddenTree, Find<TabControl>(main, "DetailsTabs") })
        { control.PropertyChanged += (_, args) => { if (args.Property == InputElement.IsEnabledProperty && !control.IsEnabled) { disabled.Add(control); } }; }
        using SlowGit gate = new(f.Repository, "diff", "diff-tree");
        GitCommandLog.Clear();
        ClickRow(main, history.ContainerFromIndex(1)!);
        await Until(() => gate.Started);
        Assert.That(history.IsKeyboardFocusWithin, Is.True, "A commit read must not release the grid's keyboard focus.");
        Assert.That(viewer.Text, Is.EqualTo(oldText), "Keep the previous document until the selected replacement is ready.");
        main.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.That(history.SelectedIndex, Is.EqualTo(2), "Down must work while the preceding read is still pending.");
        main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.That(history.SelectedIndex, Is.EqualTo(1));
        string selected = ((GitCommit)history.SelectedItem!).Hash;
        gate.Release(); await Idle(main);
        Assert.Multiple(() =>
        {
            Assert.That(viewer.DocumentIdentity, Does.Contain(selected));
            Assert.That(viewer.Text, Does.Contain("+second file").Or.Contain("+second other"));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(history.ItemsSource, Is.SameAs(historySource));
            Assert.That(hiddenTree.Tree.Items.Cast<object>(), Is.EqualTo(hiddenRoots));
            Assert.That(disabled, Is.Empty);
            Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Cancelled), Is.True);
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git ls-tree").And.Not.Contain("git status").And.Not.Contain("git log"));
        });
    }

    [AvaloniaTest]
    public async Task ChangingDiffFilesLeavesTheRestOfTheBrowserAndOldPreviewIntactAndTheLastFileWins()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        TreeView files = Find<TreeView>(main, "RevisionFiles");
        DiffView viewer = Find<DiffView>(main, "CommitDiff");
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        TreeViewItem[] rows = FileStatusTestHelpers.Rows(files).Where(item => item.Tag is RevisionDiffFileSelection).ToArray();
        object[] roots = files.Items.Cast<object>().ToArray();
        object? source = history.ItemsSource;
        string oldText = viewer.Text;
        string? oldIdentity = viewer.DocumentIdentity;
        viewer.PropertyChanged += (_, args) => { if (args.Property == Control.IsEnabledProperty) { Assert.That(viewer.IsEnabled, Is.True); } };
        using SlowGit gate = new(f.Repository, "diff", "diff-tree");
        ClickRow(main, rows[1]); await Until(() => gate.Started);
        Assert.That(files.IsKeyboardFocusWithin, Is.True);
        Assert.That(viewer.Text, Is.EqualTo(oldText));
        Assert.That(viewer.DocumentIdentity, Is.EqualTo(oldIdentity));
        ClickRow(main, rows[0]);
        gate.Release(); await Idle(main);
        RevisionDiffFileSelection selected = (RevisionDiffFileSelection)rows[0].Tag!;
        Assert.Multiple(() =>
        {
            Assert.That(viewer.DocumentIdentity, Does.Contain(selected.File.Path));
            Assert.That(files.IsKeyboardFocusWithin, Is.True);
            Assert.That(files.Items.Cast<object>(), Is.EqualTo(roots));
            Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(main.LastException, Is.Null);
        });
    }

    [AvaloniaTest]
    public async Task HiddenPanelsLoadOnActivationAndFileTreeReadsPreserveTheGridFocus()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        FileStatusListView tree = Find<FileStatusListView>(main, "CommitFileStatusList");
        tree.SelectTags(["file.txt"]); await Idle(main);
        history.SelectedIndex = 1; await Idle(main);
        object[] diffRoots = Find<TreeView>(main, "RevisionFiles").Items.Cast<object>().ToArray();
        SelectTab(main, "FileTreeTab"); await Idle(main);
        FileContentView content = Find<FileContentView>(main, "CommitFileContent");
        Assert.That(content.TextView.Text, Is.EqualTo("second file\n"));
        using SlowGit gate = new(f.Repository, "ls-tree");
        ClickRow(main, history.ContainerFromIndex(0)!); await Until(() => gate.Started);
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        gate.Release(); await Idle(main);
        Assert.That(content.TextView.Text, Is.EqualTo("third file\n"));
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Assert.That(Find<TreeView>(main, "RevisionFiles").Items.Cast<object>(), Is.EqualTo(diffRoots), "The hidden Diff panel is not rebuilt by a tree read.");
        SelectTab(main, "DiffTab"); await Idle(main);
        Assert.That(Find<DiffView>(main, "CommitDiff").DocumentIdentity, Does.Contain(((GitCommit)history.SelectedItem!).Hash));
    }

    [AvaloniaTest]
    public async Task ClosingARepositoryCancelsPendingSelectionWithoutPublishingOldResultsIntoTheDashboard()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        using SlowGit gate = new(f.Repository, "diff", "diff-tree");
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        ClickRow(main, history.ContainerFromIndex(1)!); await Until(() => gate.Started);
        main.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.W, RawInputModifiers.Control);
        gate.Release(); await Idle(main);
        Assert.That(history.Items, Is.Empty);
        Assert.That(Find<TreeView>(main, "RevisionFiles").Items, Is.Empty);
        Assert.That(main.LastException, Is.Null);
        Assert.That(main.Title, Is.EqualTo("Git Extensions"));
    }

    [AvaloniaTest]
    public async Task FilteringAwayAPendingDiffSelectionCannotBringItsOldPreviewBack()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        FileStatusListView files = Find<FileStatusListView>(main, "RevisionFileStatusList");
        TreeViewItem other = FileStatusTestHelpers.Rows(files.Tree).Single(item => item.Tag is RevisionDiffFileSelection { File.Path: "other.txt" });
        using SlowGit gate = new(f.Repository, "diff", "diff-tree");
        ClickRow(main, other); await Until(() => gate.Started);
        files.FilterText = "no_matching_file";
        await Until(() => files.FilteredEntries.Count == 0);
        gate.Release(); await Idle(main);
        Assert.That(Find<DiffView>(main, "CommitDiff").Text, Is.Empty);
        Assert.That(Find<DiffView>(main, "CommitDiff").DocumentIdentity, Is.Null);
    }

    [AvaloniaTest]
    public async Task WorkingFileReadsKeepFocusAndCannotStageThePreviousFilesPatchWhileLoading()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        await File.WriteAllTextAsync(Path.Combine(f.Repository, "file.txt"), "dirty file\n");
        await File.WriteAllTextAsync(Path.Combine(f.Repository, "other.txt"), "dirty other\n");
        await main.OpenRepositoryAsync(f.Repository); await Idle(main);
        SelectTab(main, "ChangesTab");
        FileStatusListView files = Find<FileStatusListView>(main, "WorkingFileStatusList");
        files.SelectTags([files.Entries.Select(entry => entry.Tag).OfType<GitChange>().Single(change => change.Path == "file.txt")]);
        await Idle(main);
        DiffView viewer = main.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible);
        int offset = viewer.Text.IndexOf("+dirty file", StringComparison.Ordinal); Assert.That(offset, Is.GreaterThan(0));
        viewer.Select(offset, "+dirty file".Length);
        GitRunner direct = new("/usr/bin/git");
        string before = (await direct.RunAsync(f.Repository, ["ls-files", "--stage"])).StandardOutput;
        using SlowGit gate = new(f.Repository, "diff", "diff-tree");
        TreeViewItem other = FileStatusTestHelpers.Rows(files.Tree).Single(item => item.Tag is GitChange { Path: "other.txt" });
        ClickRow(main, other); await Until(() => gate.Started);
        Assert.That(files.Tree.IsKeyboardFocusWithin, Is.True);
        Button stage = main.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Stage selected lines");
        Assert.That(stage.IsEnabled, Is.False);
        stage.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.That((await direct.RunAsync(f.Repository, ["ls-files", "--stage"])).StandardOutput, Is.EqualTo(before));
        gate.Release(); await Idle(main);
        Assert.That(viewer.Text, Does.Contain("+dirty other").And.Not.Contain("+dirty file"));
        Assert.That(files.Tree.IsKeyboardFocusWithin, Is.True);
    }

    private static async Task<Fixture> CreateAsync()
    {
        Fixture f = await Fixture.CreateAsync("first file\n", "first file\n");
        try
        {
            foreach (string version in new[] { "second", "third" })
            {
                await File.WriteAllTextAsync(Path.Combine(f.Repository, "file.txt"), version + " file\n");
                await File.WriteAllTextAsync(Path.Combine(f.Repository, "other.txt"), version + " other\n");
                foreach (string[] arguments in new[] { new[] { "add", "--", "file.txt", "other.txt" }, new[] { "commit", "-m", version } })
                { GitResult result = await f.Git.RunAsync(f.Repository, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
            }
            await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
            Find<ListBox>(f.Window, "RevisionHistory").SelectedIndex = 0; await Idle(f.Window);
            SelectTab(f.Window, "DiffTab"); await Idle(f.Window); return f;
        }
        catch { f.Dispose(); throw; }
    }

    private sealed class SlowGit : IDisposable
    {
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string _directory;
        private string Gate => Path.Combine(_directory, "hold");
        internal bool Started => File.Exists(Path.Combine(_directory, "started"));
        internal SlowGit(string repository, params string[] commands)
        {
            _directory = Path.Combine(Path.GetDirectoryName(repository)!, "slow-git-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory);
            File.WriteAllText(Gate, "");
            string script = "#!/bin/sh\nfor argument do\n case \"$argument\" in " + string.Join('|', commands) + ")\n"
                + " touch '" + _directory + "/started'\n while [ -f '" + Gate + "' ]; do sleep 0.02; done\n break;;\n esac\ndone\nexec /usr/bin/git \"$@\"\n";
            string executable = Path.Combine(_directory, "git"); File.WriteAllText(executable, script);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _oldPath);
        }
        internal void Release() { File.Delete(Gate); }
        public void Dispose() { Release(); Environment.SetEnvironmentVariable("PATH", _oldPath); }
    }

    private static void ClickRow(MainWindow main, Control row)
    {
        main.UpdateLayout(); Point point = row.TranslatePoint(new Point(80, row.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
    }
    private static void SelectTab(MainWindow main, string name)
    { Find<TabControl>(main, "DetailsTabs").SelectedItem = main.GetLogicalDescendants().OfType<TabItem>().Single(tab => tab.Name == name); main.UpdateLayout(); }
    private static T Find<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().Concat(root.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);
    private static async Task Idle(MainWindow main)
    { await Until(() => !main.IsBusy); Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) { Assert.Fail("Browser selection did not settle."); } Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }
}
