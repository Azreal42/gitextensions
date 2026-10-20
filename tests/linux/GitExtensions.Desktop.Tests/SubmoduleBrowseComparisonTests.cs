using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SubmoduleBrowseComparisonTests
{
    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ActualDiffMenusCarryDivergentChildCommitsInTheSelectedOrder(bool compareDialog, bool reverse)
    {
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        GitCompareRevision first = new(reverse ? f.SuperB : f.SuperA), selected = new(reverse ? f.SuperA : f.SuperB);
        string expectedFirst = reverse ? f.ChildB : f.ChildA, expectedSelected = reverse ? f.ChildA : f.ChildB;
        string expectedOld = reverse ? "child B" : "child A", expectedNew = reverse ? "child A" : "child B";
        Window owner = new() { Width = 1400, Height = 900 }; MainWindow? main = null; CompareRevisionsDialog? compare = null; Task? shown = null;
        ProcessStartInfo? launch = null;
        try
        {
            Window window; FileStatusListView view;
            if (compareDialog)
            {
                owner.Show(); compare = new(f.Git.Repository, first, selected, new FileStatusListPreferencesStore(f.Git.Output("compare-prefs")));
                compare.FileMenuExternalLauncher = Capture; shown = compare.ShowDialog(owner); await compare.InitializeAsync();
                await Until(() => !compare.IsBusy && compare.SelectionCompletion.IsCompleted); Assert.That(compare.LastError, Is.Null);
                window = compare; view = Field<FileStatusListView>(compare, "ComparisonFileStatusList");
            }
            else
            {
                main = new(new BrowseRequest(SelectedRevision: selected, FirstRevision: first)) { Width = 1400, Height = 900 };
                main.ExternalLauncher = Capture; main.Show(); await main.OpenRepositoryAsync(f.Git.Repository.Path); await Idle(main); await OpenDiffTab(main);
                window = main; view = Field<FileStatusListView>(main, "RevisionFileStatusList");
            }
            await Until(() => view.Entries.Count(entry => entry.Path == f.ModulePath) == 1); Pump(window);
            byte[] index = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            await OpenMenuChild(window, view, f.ModulePath);
            await Until(() => launch is not null && main?.IsBusy != true && compare?.IsBusy != true);
            BrowseRequest request = ParseLaunch(launch!, f.ChildRoot);
            Assert.That(request.SelectedRevision, Is.EqualTo(new GitCompareRevision(expectedSelected)));
            Assert.That(request.FirstRevision, Is.EqualTo(new GitCompareRevision(expectedFirst)));
            Assert.That(request.PathFilter, Is.Null, "The superproject path must not become a child path filter.");
            await AssertChildDiff(f, request, expectedOld, expectedNew, expectedSelected);
            Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(main?.LastException, Is.Null); Assert.That(compare?.LastError, Is.Null);
        }
        finally
        {
            compare?.Close(); if (compare is not null) { await compare.SelectionCompletion; } if (shown is not null) { await shown; }
            if (main is not null) { main.Close(); await main.WindowWorkCompletion; } owner.Close();
        }
        Process? Capture(ProcessStartInfo info) { launch = info; return WaitedShortChild(); }
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Commit)]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task ActualFileTreeUsesItsCapturedCommitOrLiveSourceAndTheActualParentGitlink(GitCompareRevisionKind kind)
    {
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        if (kind == GitCompareRevisionKind.Index) { await f.SetParentIndex(f.ChildA); }
        GitCompareRevision source = kind == GitCompareRevisionKind.Commit ? new(f.SuperB) : new("", kind);
        MainWindow main = new(new BrowseRequest(SelectedRevision: source)) { Width = 1400, Height = 900 };
        ProcessStartInfo? launch = null; main.ExternalLauncher = info => { launch = info; return WaitedShortChild(); }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Git.Repository.Path); await Idle(main);
            TabControl tabs = Field<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "FileTreeTab");
            await Idle(main); FileStatusListView view = Field<FileStatusListView>(main, "CommitFileStatusList");
            await Until(() => view.Entries.Any(entry => entry.Path == f.ModulePath)); Pump(main);
            byte[] index = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            await OpenMenuChild(main, view, f.ModulePath); await Until(() => launch is not null && !main.IsBusy);
            BrowseRequest request = ParseLaunch(launch!, f.ChildRoot);
            GitCompareRevision expectedSelected = kind switch
            {
                GitCompareRevisionKind.Index => new(f.ChildA),
                GitCompareRevisionKind.WorkingTree => new("", GitCompareRevisionKind.WorkingTree),
                _ => new(f.ChildB)
            };
            GitCompareRevision expectedFirst = new(kind == GitCompareRevisionKind.Commit ? f.ChildA : f.ChildB);
            Assert.That(request.SelectedRevision, Is.EqualTo(expectedSelected)); Assert.That(request.FirstRevision, Is.EqualTo(expectedFirst));
            Assert.That(request.SelectedRevision!.Kind, Is.Not.EqualTo(GitCompareRevisionKind.Index), "The parent's index is a child commit, not the child's index.");
            await AssertChildDiff(f, request, kind == GitCompareRevisionKind.Commit ? "child A" : "child B",
                kind == GitCompareRevisionKind.WorkingTree ? "live child C" : kind == GitCompareRevisionKind.Index ? "child A" : "child B",
                kind == GitCompareRevisionKind.WorkingTree ? null : expectedSelected.Revision);
            Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(index)); Assert.That(main.LastException, Is.Null);
        }
        finally { main.Close(); await main.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealIndexWorkingTreePairRetainsTheLiveSideWhenReversed(bool reverse)
    {
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        GitCompareRevision index = new("", GitCompareRevisionKind.Index), live = new("", GitCompareRevisionKind.WorkingTree);
        GitComparison pair = await f.Git.Repository.GetComparisonAsync(reverse ? live : index, reverse ? index : live);
        FileMenuSelection selection = await Selection(f.Git.Repository, pair, f.ModulePath);
        Window owner = new(); owner.Show(); ProcessStartInfo? launch = null;
        try
        {
            FileMenuAdditionalOperations service = Service(owner, info => { launch = info; return WaitedShortChild(); });
            byte[] parentIndex = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            await service.ExecuteAsync(FileMenuCommand.OpenSubmodule, selection);
            BrowseRequest request = ParseLaunch(launch!, f.ChildRoot);
            Assert.That(request.SelectedRevision, Is.EqualTo(reverse ? new GitCompareRevision(f.ChildB) : live));
            Assert.That(request.FirstRevision, Is.EqualTo(reverse ? live : new GitCompareRevision(f.ChildB)));
            await AssertChildDiff(f, request, reverse ? "live child C" : "child B", reverse ? "child B" : "live child C", reverse ? f.ChildB : null);
            Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(parentIndex));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task ARealRenamedGitlinkKeepsItsOldPathForTheFirstSideAndCannotOpenTheUnregisteredHistoricalLocation()
    {
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        string renamed = "renamed/module [2] λ;$(still-literal)", other = "other/module";
        await f.Git.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--", f.Source, other);
        await f.Git.GitAsync("commit", "-m", "Other module remains separate"); string before = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        Directory.CreateDirectory(Path.GetDirectoryName(f.Git.PathInRepository(renamed))!);
        await f.Git.GitAsync("mv", "--", f.ModulePath, renamed); await f.Git.GitAsync("commit", "-m", "Rename module");
        string after = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        GitComparison pair = await f.Git.Repository.GetComparisonAsync(new(before), new(after));
        GitRevisionFile leaf = pair.Files.Single(file => file.Path == renamed); Assert.That(leaf.Status, Is.EqualTo('R')); Assert.That(leaf.OriginalPath, Is.EqualTo(f.ModulePath));
        FileMenuSelection selection = await Selection(f.Git.Repository, pair, renamed);
        Window owner = new(); owner.Show(); List<ProcessStartInfo> launches = [];
        try
        {
            FileMenuAdditionalOperations service = Service(owner, info => { launches.Add(info); return WaitedShortChild(); });
            byte[] index = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            await service.ExecuteAsync(FileMenuCommand.OpenSubmodule, selection);
            BrowseRequest request = ParseLaunch(launches.Single(), f.Git.PathInRepository(renamed));
            Assert.That(request.SelectedRevision, Is.EqualTo(new GitCompareRevision(f.ChildB)));
            Assert.That(request.FirstRevision, Is.EqualTo(new GitCompareRevision(f.ChildB)));
            Assert.That(launches.Single().WorkingDirectory, Is.Not.EqualTo(f.Git.PathInRepository(other)));
            GitComparison inverse = await f.Git.Repository.GetComparisonAsync(new(after), new(before));
            FileMenuSelection historical = await Selection(f.Git.Repository, inverse, f.ModulePath);
            Exception? error = await Error(() => service.ExecuteAsync(FileMenuCommand.OpenSubmodule, historical));
            Assert.That(error, Is.TypeOf<InvalidOperationException>()); Assert.That(error!.Message, Does.Contain("registration"));
            Assert.That(launches.Count, Is.EqualTo(1), "No fallback to the current renamed checkout or another module.");
            Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task AChangedIndexOrMissingChildObjectIsReportedBeforeBrowseWithoutChangingGitState()
    {
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        GitComparison pair = await f.Git.Repository.GetComparisonAsync(new("", GitCompareRevisionKind.Index), new("", GitCompareRevisionKind.WorkingTree));
        FileMenuSelection captured = await Selection(f.Git.Repository, pair, f.ModulePath);
        await f.SetParentIndex(f.ChildA);
        Window owner = new(); owner.Show(); int launches = 0;
        try
        {
            FileMenuAdditionalOperations service = Service(owner, _ => { launches++; throw new InvalidOperationException("Unexpected Browse launch."); });
            byte[] index = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            Exception? stale = await Error(() => service.ExecuteAsync(FileMenuCommand.OpenSubmodule, captured));
            Assert.That(stale, Is.TypeOf<InvalidOperationException>()); Assert.That(stale!.Message, Does.Contain("index changed"));
            FileMenuLeaf missing = captured.Leaves.Single() with { SubmoduleSelection = new(new(new string('d', 40)), new(f.ChildB)) };
            Exception? absent = await Error(() => service.ExecuteAsync(FileMenuCommand.OpenSubmodule, captured with { Leaves = [missing] }));
            Assert.That(absent, Is.TypeOf<GitCommandException>()); Assert.That(launches, Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.SuperB));
            Assert.That(await f.Child.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.ChildC));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase("stale")]
    [TestCase("cancel")]
    [TestCase("close")]
    public async Task ARealPendingChildObjectReadCannotLaunchAfterStaleSelectionCancellationOrOwnerClose(string mode)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Uses a private POSIX Git gate."); return; }
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        GitComparison pair = await f.Git.Repository.GetComparisonAsync(new(f.SuperA), new(f.SuperB));
        FileMenuSelection captured = await Selection(f.Git.Repository, pair, f.ModulePath);
        string marker = f.Git.Output("endpoint-started"), release = f.Git.Output("release-endpoint"), wrapper = f.Git.Output("endpoint-git.py");
        await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport os,sys,time\n"
            + "if os.getcwd()==" + JsonSerializer.Serialize(f.ChildRoot) + " and " + JsonSerializer.Serialize(f.ChildB + "^{commit}") + " in sys.argv:\n"
            + " with open(" + JsonSerializer.Serialize(marker) + ",'w') as f:f.write(str(os.getpid()))\n"
            + " while not os.path.exists(" + JsonSerializer.Serialize(release) + "):time.sleep(.01)\n"
            + "os.execv('/usr/bin/git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository repository = new(f.Git.Repository.Path, new GitRunner(wrapper)); captured = captured with { Repository = repository };
        Window owner = new(); owner.Show(); using CancellationTokenSource cancellation = new(); bool current = true; int launches = 0; Task? pending = null; Process? observed = null;
        try
        {
            byte[] index = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            FileMenuAdditionalOperations service = new(owner, (_, action, _) => action(cancellation.Token), _ => current,
                _ => { launches++; throw new InvalidOperationException("Unexpected Browse launch."); }, cancellation.Token);
            pending = service.ExecuteAsync(FileMenuCommand.OpenSubmodule, captured);
            await Until(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
            observed = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture));
            _ = observed.SafeHandle; // Pin the real live process before release/cancellation, avoiding PID reuse and an unbound second handle.
            if (mode == "stale") { current = false; } else if (mode == "cancel") { cancellation.Cancel(); } else { owner.Close(); }
            await File.WriteAllTextAsync(release, "");
            Exception? error = await Error(async () => await pending);
            if (mode == "cancel") { Assert.That(error, Is.InstanceOf<OperationCanceledException>()); }
            else { Assert.That(error, Is.TypeOf<InvalidOperationException>()); }
            await observed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.That(observed.HasExited, Is.True);
            if (mode != "cancel") { Assert.That(observed.ExitCode, Is.Zero); }
            Assert.That(launches, Is.Zero); Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await f.Child.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.ChildC));
        }
        finally
        {
            await File.WriteAllTextAsync(release, ""); cancellation.Cancel();
            if (pending is not null) { await Error(async () => await pending); }
            observed?.Dispose(); owner.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealDeletedAndConflictedSelectedGitlinksDoNotInventAChildEndpoint(bool conflict)
    {
        using ModuleFixture f = await ModuleFixture.CreateAsync();
        GitCompareRevision selected;
        if (conflict)
        {
            await f.Git.GitAsync("update-index", "--force-remove", "--", f.ModulePath);
            string entries = "160000 " + f.ChildA + " 1\t" + f.ModulePath + "\n160000 " + f.ChildB + " 2\t" + f.ModulePath + "\n160000 " + f.ChildC + " 3\t" + f.ModulePath + "\n";
            GitResult staged = await new GitRunner().RunAsync(f.Git.Repository.Path, ["update-index", "--index-info"], standardInput: entries);
            Assert.That(staged.Success, Is.True, staged.StandardError); selected = new("", GitCompareRevisionKind.Index);
        }
        else
        {
            await f.Git.GitAsync("rm", "--cached", "-f", "--", f.ModulePath); await f.Git.GitAsync("commit", "-m", "Delete gitlink only");
            selected = new(await f.Git.Repository.ResolveRevisionAsync("HEAD"));
        }
        GitComparison pair = await f.Git.Repository.GetComparisonAsync(new(f.SuperB), selected);
        GitComparisonFileMetadata facts = (await f.Git.Repository.GetComparisonFileMetadataAsync(pair, null)).Single(item => item.File.Path == f.ModulePath);
        Assert.That(facts.IsSubmodule, Is.True); Assert.That(facts.SubmoduleSelection, Is.Null); Assert.That(facts.SubmoduleSelectionError, Is.Not.Empty);
        FileMenuLeaf leaf = new(facts.File, pair.Base, pair.Compare, pair, IsSubmodule: true) { SubmoduleSelectionError = facts.SubmoduleSelectionError };
        Window owner = new(); owner.Show(); int launches = 0;
        try
        {
            byte[] index = await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index"));
            Exception? error = await Error(() => Service(owner, _ => { launches++; throw new InvalidOperationException("Unexpected Browse launch."); })
                .ExecuteAsync(FileMenuCommand.OpenSubmodule, new(f.Git.Repository, [leaf], [f.ModulePath])));
            Assert.That(error, Is.TypeOf<InvalidOperationException>()); Assert.That(launches, Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await f.Child.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.ChildC));
        }
        finally { owner.Close(); }
    }

    private static async Task<FileMenuSelection> Selection(GitRepository repository, GitComparison pair, string path)
    {
        GitComparisonFileMetadata facts = (await repository.GetComparisonFileMetadataAsync(pair, null)).Single(item => item.File.Path == path);
        Assert.That(facts.IsSubmodule, Is.True); Assert.That(facts.SubmoduleSelectionError, Is.Null);
        FileMenuLeaf leaf = new(facts.File, pair.Base, pair.Compare, pair, IsSubmodule: true)
            { SubmoduleSelection = facts.SubmoduleSelection, SubmoduleSelectionError = facts.SubmoduleSelectionError };
        return new(repository, [leaf], [path]);
    }
    private static FileMenuAdditionalOperations Service(Window owner, Func<ProcessStartInfo, Process?> launch)
        => new(owner, (_, action, _) => action(default), _ => true, launch, default);
    private static Process WaitedShortChild()
    {
        // Intercept only the external Browse boundary. Execute and wait for a harmless real process;
        // the parsed Browse request is separately exercised in a real MainWindow below.
        Process child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
        if (!child.WaitForExit(5000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); child.Dispose(); throw new TimeoutException("Short launch fixture did not exit."); }
        Assert.That(child.ExitCode, Is.Zero); return child; // LoggedExternalProcess owns/disposes this handle.
    }
    private static BrowseRequest ParseLaunch(ProcessStartInfo info, string root)
    {
        Assert.That(info.UseShellExecute, Is.False); Assert.That(info.Arguments, Is.Empty); Assert.That(info.WorkingDirectory, Is.EqualTo(root));
        string[] argv = info.ArgumentList.ToArray(); int browse = Array.IndexOf(argv, "browse"); Assert.That(browse, Is.GreaterThanOrEqualTo(0));
        BrowseRequest request = DesktopStartupArguments.Parse(argv[browse..]).BrowseRequest!;
        Assert.That(request.RepositoryPath, Is.EqualTo(root)); return request;
    }
    private static async Task OpenDiffTab(MainWindow window)
    {
        // Browse defaults to CommitInfoTab. Open the visible Diff header before querying its visual subtree.
        TabControl tabs = Field<TabControl>(window, "DetailsTabs");
        TabItem diff = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
        PushProcessTestDriver.Pointer(diff); await Idle(window);
        Assert.That(tabs.SelectedItem, Is.SameAs(diff));
        Assert.That(tabs.SelectedContent, Is.SameAs(diff.Content));
    }
    private static async Task AssertChildDiff(ModuleFixture f, BrowseRequest request, string oldText, string newText, string? primary)
    {
        byte[] index = await File.ReadAllBytesAsync(Path.Combine((await f.Child.GetInfoAsync()).GitDirectory, "index"));
        MainWindow child = new(request) { Width = 1400, Height = 900 }; child.Show();
        try
        {
            await child.OpenRepositoryAsync(request.RepositoryPath!); await Idle(child); await OpenDiffTab(child);
            FileStatusListView view = Field<FileStatusListView>(child, "RevisionFileStatusList");
            await Until(() => view.Entries.Any(entry => entry.Path == "file.txt"));
            object tag = PrimaryTag(view, "file.txt"); view.SelectTags([tag]); await Idle(child);
            PushProcessTestDriver.Pointer(Header(view.FindItemByTag(tag)!)); await Idle(child);
            string diff = Field<DiffView>(child, "CommitDiff").Text;
            Assert.That(diff, Does.Contain("-" + oldText).And.Contain("+" + newText));
            if (primary is not null) { Assert.That(((GitCommit)Field<ListBox>(child, "RevisionHistory").SelectedItem!).Hash, Is.EqualTo(primary)); }
            Assert.That(await f.Child.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.ChildC), "Opening Browse cannot check out the selected commit.");
            Assert.That(await File.ReadAllTextAsync(Path.Combine(f.ChildRoot, "file.txt")), Is.EqualTo("live child C\n"));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine((await f.Child.GetInfoAsync()).GitDirectory, "index")), Is.EqualTo(index));
        }
        finally { child.Close(); await child.WindowWorkCompletion; }
    }
    private static async Task OpenMenuChild(Window window, FileStatusListView view, string path)
    {
        object tag = view.Entries.Single(entry => entry.Path == path).Tag; view.SelectTags([tag]);
        await Until(() => window is MainWindow main ? !main.IsBusy && main.WindowWorkCompletion.IsCompleted
            : window is CompareRevisionsDialog compare ? !compare.IsBusy && compare.SelectionCompletion.IsCompleted : true); Pump(window);
        Control row = Header(view.FindItemByTag(tag)!); Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Pump(window);
        ContextMenu menu = view.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
        MenuItem open = menu.Items.OfType<MenuItem>().Single(item => item.Name == "OpenSubmodule"); Assert.That(open.IsEnabled, Is.True);
        PushProcessTestDriver.Pointer(open); await Task.Yield();
    }
    private static object PrimaryTag(FileStatusListView view, string path)
    {
        object[] tags = view.Entries.Where(entry => entry.Path == path).Select(entry => entry.Tag).ToArray();
        return tags.FirstOrDefault(tag => tag is RevisionDiffFileSelection { Group.Kind: GitRevisionDiffGroupKind.Comparison }) ?? tags.First();
    }
    private static Control Header(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "PART_Header" && item.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow window) { await Until(() => !window.IsBusy && window.WindowWorkCompletion.IsCompleted); Pump(window); Assert.That(window.LastException, Is.Null, window.LastException?.ToString()); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(30)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Submodule Browse fixture did not settle."); Dispatcher.UIThread.RunJobs();
    }
    private static async Task<Exception?> Error(Func<Task> action) { try { await action(); return null; } catch (Exception error) { return error; } }
    private sealed class ModuleFixture : IDisposable
    {
        internal Fixture Git { get; }
        internal string Source { get; }
        internal string ModulePath => "nested/module [1] λ;$(literal)";
        internal string ChildRoot => Git.PathInRepository(ModulePath);
        internal GitRepository Child { get; private set; } = null!;
        internal string ChildA { get; private set; } = "";
        internal string ChildB { get; private set; } = "";
        internal string ChildC { get; private set; } = "";
        internal string SuperA { get; private set; } = "";
        internal string SuperB { get; private set; } = "";
        private ModuleFixture(Fixture git) { Git = git; Source = git.Output("source module λ"); }
        internal static async Task<ModuleFixture> CreateAsync()
        {
            ModuleFixture f = new(await Fixture.CreateAsync());
            try
            {
                await f.Git.GitAsync("clone", "--", f.Git.Repository.Path, f.Source);
                await f.Git.GitAsync("-C", f.Source, "config", "user.name", "Submodule Tester"); await f.Git.GitAsync("-C", f.Source, "config", "user.email", "submodule@example.com");
                await f.Git.GitAsync("-C", f.Source, "config", "commit.gpgsign", "false"); await f.Git.GitAsync("-C", f.Source, "config", "core.hooksPath", f.Git.Output("no-hooks"));
                string basis = (await f.Git.GitAsync("-C", f.Source, "rev-parse", "HEAD")).StandardOutput.Trim();
                f.ChildA = await Commit("side-a", "child A\n"); f.ChildB = await Commit("side-b", "child B\n"); f.ChildC = await Commit("live-c", "child C\n");
                await f.Git.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--", f.Source, f.ModulePath);
                f.Child = await GitRepository.OpenAsync(f.ChildRoot);
                await f.Git.GitAsync("-C", f.ChildRoot, "checkout", "--detach", f.ChildA); await f.Git.Repository.StageAllAsync(); await f.Git.GitAsync("commit", "-m", "Module A"); f.SuperA = await f.Git.Repository.ResolveRevisionAsync("HEAD");
                await f.Git.GitAsync("-C", f.ChildRoot, "checkout", "--detach", f.ChildB); await f.Git.Repository.StageAllAsync(); await f.Git.GitAsync("commit", "-m", "Module B"); f.SuperB = await f.Git.Repository.ResolveRevisionAsync("HEAD");
                await f.Git.GitAsync("-C", f.ChildRoot, "checkout", "--detach", f.ChildC); await File.WriteAllTextAsync(Path.Combine(f.ChildRoot, "file.txt"), "live child C\n");
                return f;
                async Task<string> Commit(string branch, string text)
                {
                    await f.Git.GitAsync("-C", f.Source, "checkout", "-b", branch, basis); await File.WriteAllTextAsync(Path.Combine(f.Source, "file.txt"), text);
                    await f.Git.GitAsync("-C", f.Source, "add", "--", "file.txt"); await f.Git.GitAsync("-C", f.Source, "commit", "-m", branch);
                    return (await f.Git.GitAsync("-C", f.Source, "rev-parse", "HEAD")).StandardOutput.Trim();
                }
            }
            catch { f.Dispose(); throw; }
        }
        internal Task<GitResult> SetParentIndex(string oid) => Git.GitAsync("update-index", "--cacheinfo", "160000", oid, ModulePath);
        public void Dispose() => Git.Dispose();
    }
}
