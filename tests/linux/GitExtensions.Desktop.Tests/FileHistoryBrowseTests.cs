using System.Diagnostics;
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
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryBrowseTests
{
    [AvaloniaTest]
    public async Task OldSelectionLoadsMoreFromTheNewestGraphWithoutHidingDescendantsOrOtherChangedFiles()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        string selected = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.ChangeAsync("second\n", "Second");
        await File.WriteAllTextAsync(f.PathInRepository("a-other.txt"), "other\n");
        await f.ChangeAsync("third\n", "Third with two files");
        new PreferencesStore().Save(new DesktopPreferences { HistoryLimit = 1 });
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("file.txt", BrowsePathKind.File), new(selected));
        MainWindow main = scope.Window; ListBox history = Find<ListBox>(main, "RevisionHistory");
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Subject), Is.EqualTo(new[] { "Third with two files", "Second", "Initial commit" }));
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected));
        Assert.That(((TabItem)Find<TabControl>(main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        Assert.That(Find<TreeView>(main, "RepositoryNavigation").IsEffectivelyVisible, Is.False);
        object? source = history.ItemsSource;
        PushProcessTestDriver.Pointer(history.ContainerFromIndex(0)!); await Idle(main);
        RevisionDiffFileSelection[] files = FileStatusTestHelpers.Rows(Find<TreeView>(main, "RevisionFiles"))
            .Select(row => row.Tag).OfType<RevisionDiffFileSelection>().ToArray();
        Assert.That(files.Select(file => file.File.Path), Is.EquivalentTo(new[] { "a-other.txt", "file.txt" }));
        Assert.That(((RevisionDiffFileSelection)((TreeViewItem)Find<TreeView>(main, "RevisionFiles").SelectedItem!).Tag!).File.Path, Is.EqualTo("file.txt"));
        Assert.That(history.ItemsSource, Is.SameAs(source));
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        main.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        await Idle(main); Assert.That(history.SelectedIndex, Is.EqualTo(1));
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task RenamedFileSelectsItsOldNameAndBlobInTheSharedTreeDespiteAnEarlierSortingFile()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(f.PathInRepository("a-other.txt"), "competing old blob\n");
        await f.ChangeAsync("old followed blob\n", "Old path and competing file");
        string selected = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.GitAsync("mv", "file.txt", "z-renamed.txt");
        await f.GitAsync("commit", "-m", "Rename file");
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("z-renamed.txt", BrowsePathKind.File), new(selected));
        MainWindow main = scope.Window;
        SelectTab(main, "FileTreeTab"); await Idle(main);
        Assert.That(((TreeViewItem)Find<TreeView>(main, "CommitFileTree").SelectedItem!).Tag, Is.EqualTo("file.txt"));
        FileHistoryBlameController blame = Find<FileHistoryBlameController>(main, "FileHistoryInlineBlameHost");
        Assert.That(blame.IsBlameActive, Is.True);
        // Disable annotation through the shared control to verify the ordinary blob reader too.
        Find<CheckBox>(main, "FileHistoryInlineBlameToggle").IsChecked = false;
        await Idle(main);
        Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.Text, Is.EqualTo("old followed blob\n"));
        Find<ListBox>(main, "RevisionHistory").SelectedIndex = 0; await Idle(main);
        Assert.That(((TabItem)Find<TabControl>(main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("FileTreeTab"), "Diff is an initial choice only.");
    }

    [AvaloniaTest]
    public async Task MissingSelectedCommitIsReportedWithoutReplacingItWithAnotherCommitOrAnEmptyRepositoryMessage()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        string first = await f.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(f.PathInRepository("other.txt"), "unrelated\n");
        await f.GitAsync("add", "other.txt"); await f.GitAsync("commit", "-m", "Unrelated commit");
        string unrelated = await f.Repository.ResolveRevisionAsync("HEAD");
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("file.txt", BrowsePathKind.File), new(unrelated));
        Assert.That(Find<ListBox>(scope.Window, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Contain(first));
        Assert.That(Find<ListBox>(scope.Window, "RevisionHistory").SelectedItem, Is.Null);
        Assert.That(Find<DiffView>(scope.Window, "CommitDiff").Text, Does.Contain("not visible").And.Contain(unrelated).And.Not.Contain("no commits yet"));
    }

    [AvaloniaTest]
    public async Task FirstAndSelectedRemainComparisonSidesWhileTheSelectedCommitStaysPrimary()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        string first = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.ChangeAsync("selected newer\n", "Selected");
        string selected = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.ChangeAsync("descendant\n", "Visible descendant");
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("file.txt", BrowsePathKind.File), new(selected), new(first));
        ListBox history = Find<ListBox>(scope.Window, "RevisionHistory");
        Assert.That(history.Items.Count, Is.EqualTo(3));
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected));
        Assert.That(history.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { first, selected }));
        Assert.That(Find<DiffView>(scope.Window, "CommitDiff").Text, Does.Contain("+selected newer").And.Contain("-base"));
        history.SelectedIndex = 0; await Idle(scope.Window);
        Assert.That(Find<DiffView>(scope.Window, "CommitDiff").Text, Does.Contain("+descendant"));
    }

    [AvaloniaTest]
    public async Task AnOrphanHeadKeepsTheOtherBranchesAndTheirSelectedTreeVisible()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        string selected = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.GitAsync("switch", "--orphan", "unborn");
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("file.txt", BrowsePathKind.File), new(selected));
        Assert.That(Find<ListBox>(scope.Window, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Contain(selected));
        SelectTab(scope.Window, "FileTreeTab"); await Idle(scope.Window);
        Assert.That(((TreeViewItem)Find<TreeView>(scope.Window, "CommitFileTree").SelectedItem!).Tag, Is.EqualTo("file.txt"));
    }

    [AvaloniaTest]
    public async Task AnExplicitIndexToWorkingDirectoryPairReadsBothActualSources()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(f.File, "actual staged\n"); await f.Repository.StageAllAsync();
        await File.WriteAllTextAsync(f.File, "actual worktree\n");
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("file.txt", BrowsePathKind.File),
            new(Kind: GitCompareRevisionKind.WorkingTree), new(Kind: GitCompareRevisionKind.Index));
        Assert.That(Find<DiffView>(scope.Window, "CommitDiff").Text, Does.Contain("-actual staged").And.Contain("+actual worktree").And.Not.Contain("-base"));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task RefreshKeepsTheActualTreeAndGrepSourceForArtificialSelections(GitCompareRevisionKind kind)
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(f.File, "needle INDEX\n"); await f.Repository.StageAllAsync();
        await File.WriteAllTextAsync(f.File, "needle WORKTREE\n");
        await using BrowseScope scope = await BrowseScope.OpenAsync(f, new("file.txt", BrowsePathKind.File), new(Kind: kind));
        MainWindow main = scope.Window; SelectTab(main, "FileTreeTab"); await Idle(main);
        TreeView tree = Find<TreeView>(main, "CommitFileTree");
        tree.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = tree }); Dispatcher.UIThread.RunJobs();
        tree.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiShowFindInCommitFilesGitGrep")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        tree.ContextMenu.Close();
        Find<TextBox>(main, "FileGrepExpression").Text = "needle";
        await Until(() => !main.IsFileGrepBusy && main.FileGrepResult is not null); await Idle(main);
        Assert.That(main.FileGrepResult!.Cached, Is.EqualTo(kind == GitCompareRevisionKind.Index));
        Assert.That(main.FileGrepResult.Revision, Is.Null);
        Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.Text,
            Is.EqualTo(kind == GitCompareRevisionKind.Index ? "needle INDEX\n" : "needle WORKTREE\n"));
        await File.WriteAllTextAsync(f.File, "needle UPDATED INDEX\n"); await f.Repository.StageAllAsync();
        await File.WriteAllTextAsync(f.File, "needle UPDATED WORKTREE\n");
        main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await Idle(main);
        await Until(() => !main.IsFileGrepBusy && main.FileGrepResult is { Revision: null } result && result.Cached == (kind == GitCompareRevisionKind.Index));
        await Idle(main);
        Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.Text,
            Is.EqualTo(kind == GitCompareRevisionKind.Index ? "needle UPDATED INDEX\n" : "needle UPDATED WORKTREE\n"));
        Assert.That(Find<ListBox>(main, "RevisionHistory").SelectedItem, Is.Null);
    }

    [AvaloniaTest]
    public async Task ClosingDuringPathDiscoveryWaitsForCancelledGitAndNeverPublishesTheLateHistory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux process cancellation contract."); return; }
        using ReferenceFixture f = await ReferenceFixture.CreateAsync();
        string wrapper = f.Output("gate-bin"), marker = f.Output("git-started"), release = f.Output("release");
        Directory.CreateDirectory(wrapper);
        string executable = Path.Combine(wrapper, "git");
        await File.WriteAllTextAsync(executable, "#!/usr/bin/python3\nimport os,sys,time\nif 'log' in sys.argv:\n open(" + System.Text.Json.JsonSerializer.Serialize(marker) + ", 'w').write(str(os.getpid()))\n while not os.path.exists(" + System.Text.Json.JsonSerializer.Serialize(release) + "): time.sleep(.01)\nos.execv('/usr/bin/git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        f.SetEnvironment("PATH", wrapper + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        MainWindow main = new(new BrowseRequest(PathFilter: new("file.txt", BrowsePathKind.File)));
        main.Show(); Task opening = main.OpenRepositoryAsync(f.Repository.Path);
        try
        {
            await Until(() => File.Exists(marker));
            using Process child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            Task pending = main.WindowWorkCompletion;
            main.Close();
            await Until(() => !main.IsVisible && opening.IsCompleted && pending.IsCompleted);
            await opening; await pending;
            Assert.That(child.HasExited, Is.True, "The window cannot release its repository while the actual Git reader is alive.");
            Assert.That(Find<ListBox>(main, "RevisionHistory").Items, Is.Empty);
            Assert.That(main.LastException, Is.Null);
        }
        finally
        {
            await File.WriteAllTextAsync(release, ""); main.Close();
            await Until(() => !main.IsVisible && opening.IsCompleted); await opening;
        }
    }

    private sealed class BrowseScope(MainWindow window) : IAsyncDisposable
    {
        internal MainWindow Window => window;
        internal static async Task<BrowseScope> OpenAsync(ReferenceFixture f, BrowsePathFilter filter, GitCompareRevision? selected = null, GitCompareRevision? first = null)
        {
            MainWindow main = new(new BrowseRequest(PathFilter: filter, SelectedRevision: selected, FirstRevision: first)) { Width = 1440, Height = 900 };
            main.Show();
            BrowseScope scope = new(main);
            try { await main.OpenRepositoryAsync(f.Repository.Path); await Idle(main); return scope; }
            catch { await scope.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        { window.Close(); await Until(() => !window.IsVisible && window.WindowWorkCompletion.IsCompleted); await window.WindowWorkCompletion; }
    }

    private static T Find<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>()
        .Concat(root.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);
    private static void SelectTab(MainWindow main, string name)
    { Find<TabControl>(main, "DetailsTabs").SelectedItem = main.GetLogicalDescendants().OfType<TabItem>().Single(tab => tab.Name == name); main.UpdateLayout(); }
    private static async Task Idle(MainWindow main)
    { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted); main.UpdateLayout(); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Until(Func<bool> predicate)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) { Assert.Fail("File-history Browse did not settle."); } Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }
}
