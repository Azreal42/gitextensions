using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
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
public sealed class CommitDiffWindowTests
{
    private const string Literal = "literal [*]\n\t雪;$(path).txt";

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task ResolvedSnapshotUsesRealParentsAndCompactSharedControlsWithoutAnyHistoryRows(string culture, bool dark)
    {
        using Fixture f = await History(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        string oid = await Text(f, "rev-parse", "HEAD"), parent = await Text(f, "rev-parse", "HEAD~");
        MainWindow window = await CommitDiffWindow.CreateAsync(f.Repository, "HEAD");
        await File.WriteAllTextAsync(f.File, "new HEAD after factory\n"); await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "New HEAD after snapshot");
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; window.Show();
        try
        {
            await Idle(window); Grid layout = Field<Grid>(window, "CommitDiffLayout"); Grid split = Field<Grid>(window, "RevisionDiffSplit");
            CommitInformationView information = Field<CommitInformationView>(window, "CommitInformation");
            Assert.Multiple(() =>
            {
                Assert.That(window.WindowKind, Is.EqualTo(MainWindowKind.CommitDiff)); Assert.That(window.CommitDiffRevision, Is.EqualTo(oid));
                Assert.That(window.ClientSize, Is.EqualTo(new Size(717, 529)));
                Assert.That(layout.RowDefinitions[0].ActualHeight, Is.EqualTo(200).Within(1)); Assert.That(split.ColumnDefinitions[0].ActualWidth, Is.EqualTo(260).Within(1));
                Assert.That(information.Text, Does.Contain("Fourth").And.Not.Contain("New HEAD after snapshot"));
                Assert.That(information.ShowRevisionLinks || information.ShowBranchesAsLinks, Is.False);
                Assert.That(information.GetVisualDescendants().OfType<Button>().Any(button => button.Tag is ValueTuple<CommitInformationCommand, string> link
                    && (link.Item1 is CommitInformationCommand.NavigateRevision or CommitInformationCommand.NavigateReference) && !link.Item2.StartsWith("showall/", StringComparison.Ordinal)), Is.False);
                Assert.That(window.GetVisualDescendants().OfType<ListBox>().Any(list => list.Name == "RevisionHistory"), Is.False);
                Assert.That(Private<ListBox>(window, "_commits").Items, Is.Empty);
                Assert.That(Private<IReadOnlyList<GitCommit>>(window, "_history"), Is.Empty);
                Assert.That(Groups(window).Single().BaseRevision, Is.EqualTo(parent));
            });
            Capture(window, "commit-diff-" + culture + "-" + (dark ? "dark" : "light") + ".png");
            FileStatusListView files = Files(window); object first = files.Entries.Single(entry => entry.Path == "file.txt").Tag;
            PushProcessTestDriver.Pointer(Header(files.FindItemByTag(first)!)); await Idle(window);
            Assert.That(Diff(window).Text, Does.Contain("+third")); Press(window, PhysicalKey.ArrowDown); await Idle(window);
            Assert.That(files.SelectedEntries.Select(entry => entry.Path), Is.EqualTo(new[] { Literal })); Assert.That(Diff(window).Text, Does.Contain("+literal new"));
            Press(window, PhysicalKey.F5); await Idle(window); Assert.That(window.CommitDiffRevision, Is.EqualTo(oid)); Assert.That(Groups(window).Single().BaseRevision, Is.EqualTo(parent));
            Assert.That(Diff(window).Text, Does.Contain("+literal new"));
            List<string> links = []; information.RevisionLinkLauncher = uri => { links.Add(uri.AbsoluteUri); return Task.CompletedTask; };
            await information.ActivateRevisionLinkAsync("gitext://gotocommit/" + parent);
            Assert.That(window.CommitDiffRevision, Is.EqualTo(oid), "Standalone Windows CommitInfo has no revision navigation consumer.");
            await information.ActivateRevisionLinkAsync("https://example.invalid/private-commit-link"); Assert.That(links, Has.Count.EqualTo(1));
            Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { window.Close(); await window.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task RootMergeCombinedAndBareReadTheActualCommitRatherThanAnArbitraryParentPair()
    {
        using Fixture f = await Fixture.CreateAsync(); string root = await Text(f, "rev-parse", "HEAD");
        MainWindow first = await CommitDiffWindow.CreateAsync(f.Repository, root); first.Show();
        try { await Idle(first); Assert.That(Groups(first).Single().BaseRevision, Is.Null); Assert.That(Diff(first).Text, Does.Contain("+base")); }
        finally { first.Close(); await first.WindowWorkCompletion; }
        await f.GitAsync("checkout", "-b", "side"); await f.ChangeAsync("side\n", "Side");
        await f.GitAsync("checkout", "main"); await f.ChangeAsync("main\n", "Main");
        GitResult merge = await new GitRunner().RunAsync(f.Repository.Path, ["merge", "side"]); Assert.That(merge.Success, Is.False);
        await File.WriteAllTextAsync(f.File, "resolved from both parents\n"); await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Resolved merge");
        string oid = await Text(f, "rev-parse", "HEAD"), barePath = f.Output("bare repository λ.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, barePath);
        foreach (GitRepository repository in new[] { f.Repository, await GitRepository.OpenAsync(barePath) })
        {
            MainWindow window = await CommitDiffWindow.CreateAsync(repository, oid); window.Show();
            try
            {
                await Idle(window); Assert.That(Groups(window).Count(group => group.Kind == GitRevisionDiffGroupKind.Parent), Is.EqualTo(2));
                GitRevisionDiffGroup combined = Groups(window).Single(group => group.Kind == GitRevisionDiffGroupKind.Combined);
                FileStatusListView files = Files(window); object tag = files.Entries.Single(entry => entry.Tag is RevisionDiffFileSelection selection && ReferenceEquals(selection.Group, combined)).Tag;
                TreeViewItem combinedRow = files.Tree.Items.OfType<TreeViewItem>().Single(item => item.Name == "RevisionDiffGroup_Combined");
                combinedRow.BringIntoView(); Pump(window);
                if (!combinedRow.IsExpanded)
                {
                    PushProcessTestDriver.Pointer(Header(combinedRow)); await Idle(window);
                    Assert.That(combinedRow.IsKeyboardFocusWithin, Is.True, "Clicking the group header must focus its native tree row.");
                    Press(window, PhysicalKey.ArrowRight); Pump(window);
                    Assert.That(combinedRow.IsExpanded, Is.True, "Right must reveal Combined through native keyboard input.");
                }
                PushProcessTestDriver.Pointer(Header(files.FindItemByTag(tag)!)); await Idle(window);
                Assert.That(Diff(window).EffectiveMode, Is.EqualTo(DiffViewMode.Combined)); Assert.That(Diff(window).Text, Does.Contain("resolved from both parents"));
                Assert.That(window.CommitDiffRevision, Is.EqualTo(oid)); Assert.That(Private<ListBox>(window, "_commits").Items, Is.Empty);
            }
            finally { window.Close(); await window.WindowWorkCompletion; }
        }
    }

    [AvaloniaTest]
    public async Task RealDoubleClickOpensUnownedModelessWhileBrowseKeepsItsFirstArrowAndQuickSearch()
    {
        using Fixture f = await History(); using ClassicDesktopStyleApplicationLifetime lifetime = new();
        MainWindow root = new(new BrowseRequest(f.Repository.Path, new("file.txt"))) { ModelessApplicationLifetime = lifetime }; lifetime.MainWindow = root; root.Show();
        try
        {
            await Idle(root); ListBox history = Field<ListBox>(root, "RevisionHistory");
            GitCommit fourth = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Fourth");
            Assert.That(history.Items.OfType<GitCommit>().Any(commit => commit.Subject == "Hidden parent"), Is.False);
            Control row = (Control)history.ContainerFromIndex(history.Items.IndexOf(fourth))!; Point point = PointInRow(root, row);
            List<int> counts = []; root.AddHandler(InputElement.PointerPressedEvent, (_, args) => counts.Add(args.ClickCount), RoutingStrategies.Tunnel, handledEventsToo: true);
            root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
            Assert.That(root.ModelessCommitDiffWindows, Is.Empty);
            root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
            await Until(() => root.ModelessCommitDiffWindows.Count == 1); MainWindow child = root.ModelessCommitDiffWindows.Single(); await Idle(child); await InputIdle(root);
            Assert.That(counts.Take(2), Is.EqualTo(new[] { 1, 2 })); Assert.That(child.Owner, Is.Null); Assert.That(root.IsEffectivelyEnabled, Is.True);
            Assert.That(root.OwnedWindows, Is.Empty); Assert.That(root.IsBusy, Is.False); Assert.That(child.CommitDiffRevision, Is.EqualTo(fourth.Hash));
            Assert.That(Groups(child).Single().BaseRevision, Is.EqualTo(await Text(f, "rev-parse", fourth.Hash + "~")), "Filtered-row parents cannot replace the real commit parent.");
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(history.Items.IndexOf(fourth))!); Press(root, PhysicalKey.ArrowDown); await InputIdle(root);
            Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Second")); Assert.That(child.CommitDiffRevision, Is.EqualTo(fourth.Hash));
            root.KeyTextInput("Fourth"); await InputIdle(root); Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Fourth"));
            Assert.That(child.IsVisible, Is.True); child.Close(); await Until(() => root.ModelessCommitDiffWindows.Count == 0); await root.WindowWorkCompletion;
            // Labels retain priority over opening CommitDiff. Do not turn their current action into a commit popup.
            Control reference = root.GetVisualDescendants().OfType<Control>().First(control => control.Name == "RevisionReference"
                && control.Tag is GitReference && control.IsEffectivelyVisible);
            DoubleClick(root, reference); await InputIdle(root); Assert.That(root.ModelessCommitDiffWindows, Is.Empty);
            PushProcessTestDriver.Pointer(Field<Button>(root, "IndexRevision").FindAncestorOfType<ListBoxItem>()!); await InputIdle(root);
            DoubleClick(root, Field<Button>(root, "IndexRevision").FindAncestorOfType<ListBoxItem>()!); await InputIdle(root); Assert.That(root.ModelessCommitDiffWindows, Is.Empty);
        }
        finally { root.Close(); await root.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task ClosingASecondaryLauncherLeavesTheSnapshotAliveUntilTheActualRootCloses()
    {
        using Fixture f = await History(); using ClassicDesktopStyleApplicationLifetime lifetime = new();
        MainWindow root = new(new BrowseRequest(f.Repository.Path)) { ModelessApplicationLifetime = lifetime }; lifetime.MainWindow = root; root.Show();
        MainWindow launcher = new(new BrowseRequest(f.Repository.Path)) { ModelessApplicationLifetime = lifetime }; launcher.Show();
        try
        {
            await Idle(root); await Idle(launcher); GitCommit commit = Field<ListBox>(launcher, "RevisionHistory").Items.OfType<GitCommit>().First();
            await launcher.ShowCommitDiffAsync(commit); MainWindow child = root.ModelessCommitDiffWindows.Single(); await Idle(child);
            Assert.That(launcher.ModelessCommitDiffWindows, Is.Empty); launcher.Close(); await launcher.WindowWorkCompletion;
            Assert.That(child.IsVisible, Is.True); Assert.That(child.CommitDiffRevision, Is.EqualTo(commit.Hash));
            root.Close(); await Until(() => !root.IsVisible && !child.IsVisible && root.WindowWorkCompletion.IsCompleted); await root.WindowWorkCompletion;
            Assert.That(root.ModelessCommitDiffWindows, Is.Empty); Assert.That(child.WindowWorkCompletion.IsCompleted, Is.True);
        }
        finally { launcher.Close(); root.Close(); await launcher.WindowWorkCompletion; await root.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARealSlowDiffProcessIsCancelledAndDrainedOnChildOrRootClose(bool rootClose)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private Linux Git gate."); return; }
        using Fixture f = await History(); using ClassicDesktopStyleApplicationLifetime lifetime = new();
        MainWindow root = new(new BrowseRequest(f.Repository.Path)) { ModelessApplicationLifetime = lifetime }; lifetime.MainWindow = root; root.Show();
        GitEnvironmentOptions original = GitEnvironment.Options; Process? process = null; string release = f.Output("release-diff"), marker = f.Output("diff-started"), wrapper = f.Output("git-diff-gate.py");
        try
        {
            await Idle(root); GitCommit commit = Field<ListBox>(root, "RevisionHistory").Items.OfType<GitCommit>().First();
            string before = await File.ReadAllTextAsync(f.File);
            await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport os,sys,time\nif os.getcwd()==" + JsonSerializer.Serialize(f.Repository.Path)
                + " and any(x in ('diff','diff-tree') for x in sys.argv):\n with open(" + JsonSerializer.Serialize(marker) + ",'w') as f:f.write(str(os.getpid()))\n while not os.path.exists(" + JsonSerializer.Serialize(release) + "):time.sleep(.01)\nos.execv('/usr/bin/git',['git']+sys.argv[1:])\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); GitEnvironment.Configure(original with { Executable = wrapper });
            await root.ShowCommitDiffAsync(commit); MainWindow child = root.ModelessCommitDiffWindows.Single();
            await Until(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
            process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture)); _ = process.SafeHandle;
            Assert.That(root.IsBusy, Is.False); Assert.That(root.IsEffectivelyEnabled, Is.True); Assert.That(child.IsVisible, Is.True);
            if (rootClose) { root.Close(); } else { Press(child, PhysicalKey.Escape); }
            await Until(() => !child.IsVisible && child.WindowWorkCompletion.IsCompleted && root.ModelessCommitDiffWindows.Count == 0);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.That(process.HasExited, Is.True);
            Assert.That(root.IsVisible, Is.EqualTo(!rootClose)); Assert.That(Files(child).Entries, Is.Empty, "No cancelled file list may be published late.");
            Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo(before));
        }
        finally
        {
            await File.WriteAllTextAsync(release, ""); GitEnvironment.Configure(original); root.Close(); await root.WindowWorkCompletion; process?.Dispose();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OverscrollUsesTheSharedRealFileReadAndAppliesTheLandingAfterPublication(bool logHost)
    {
        using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.File, string.Concat(Enumerable.Range(1, 300).Select(index => "first " + index + "\n")));
        await File.WriteAllTextAsync(f.PathInRepository(Literal), string.Concat(Enumerable.Range(1, 300).Select(index => "second " + index + "\n")));
        await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Long files");
        DiffViewerPreferencesStore store = new(); Assert.That(store.SaveSettings(store.Load() with { AutomaticContinuousScroll = false, AutomaticContinuousScrollDelay = 0 }), Is.True);
        MainWindow window = logHost ? RevisionLogWindow.Create(new(f.Repository.Path)) : await CommitDiffWindow.CreateAsync(f.Repository, "HEAD"); window.Show();
        try
        {
            await Idle(window); await SelectFile(window, "file.txt"); DiffView diff = Diff(window);
            int documentChanges = 0, documentLoads = 0;
            diff.Editor.Document.Changed += (_, _) => documentChanges++;
            diff.DocumentLoading += (_, _) => documentLoads++;
            diff.Editor.ScrollToHome(); Pump(window); await AssertNoBoundaryRead(forward: false, "file.txt");
            diff.Editor.ScrollToEnd(); Pump(window);
            Wheel(window, -1, RawInputModifiers.None); await Idle(window); Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo("file.txt"));
            Wheel(window, -1, RawInputModifiers.Alt); await Idle(window);
            Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo(Literal)); Assert.That(diff.Text, Does.Contain("+second 300")); Assert.That(diff.Editor.VerticalOffset, Is.EqualTo(0).Within(1));
            Assert.That(documentChanges, Is.GreaterThan(0), "The counter observes a real neighboring document publication.");
            diff.Editor.ScrollToEnd(); Pump(window); await AssertNoBoundaryRead(forward: true, Literal);
            diff.Editor.ScrollToHome(); Pump(window);
            Wheel(window, 1, RawInputModifiers.Alt); await Idle(window);
            Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo("file.txt")); Assert.That(diff.Text, Does.Contain("+first 300"));
            Assert.That(diff.Editor.VerticalOffset + diff.Editor.ViewportHeight, Is.GreaterThanOrEqualTo(diff.Editor.ExtentHeight - 1));
            Wheel(window, -1, RawInputModifiers.Shift | RawInputModifiers.Alt); await Idle(window); Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo("file.txt"));
            double font = diff.Editor.FontSize; Wheel(window, -1, RawInputModifiers.Control); await Idle(window);
            Assert.That(diff.Editor.FontSize, Is.LessThan(font)); Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo("file.txt"));
            Assert.That(store.SaveSettings(store.Load() with { AutomaticContinuousScroll = true }), Is.True);
            diff.Editor.ScrollToEnd(); Pump(window); Wheel(window, -1, RawInputModifiers.None); await Idle(window);
            Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo(Literal)); Assert.That(diff.Editor.VerticalOffset, Is.EqualTo(0).Within(1));

            async Task AssertNoBoundaryRead(bool forward, string path)
            {
                string? identity = diff.DocumentIdentity; double offset = diff.Editor.VerticalOffset;
                int changes = documentChanges, loads = documentLoads;
                long[] commands = GitCommandLog.Snapshot().Where(entry => entry.Directory == f.Repository.Path).Select(entry => entry.Id).ToArray();
                Assert.That(identity, Is.Not.Null.And.Not.Empty);
                Wheel(window, forward ? -1 : 1, RawInputModifiers.Alt); await Idle(window);
                Assert.Multiple(() =>
                {
                    Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo(path), "The first/last file cannot wrap to another file.");
                    Assert.That(diff.DocumentIdentity, Is.EqualTo(identity));
                    Assert.That(documentChanges, Is.EqualTo(changes), "Overscroll without a neighbor cannot replace the editor document.");
                    Assert.That(documentLoads, Is.EqualTo(loads), "Even an identical document cannot be republished at the boundary.");
                    Assert.That(diff.Editor.VerticalOffset, Is.EqualTo(offset).Within(1), "No same-file reload may flip the viewport to the other boundary.");
                    Assert.That(GitCommandLog.Snapshot().Where(entry => entry.Directory == f.Repository.Path).Select(entry => entry.Id), Is.EqualTo(commands), "No Git read may start when no neighboring file exists.");
                });
            }
        }
        finally { window.Close(); await window.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task ContinuousScrollDelayRejectsASecondWheelBurstWithoutBlockingInput()
    {
        using Fixture f = await History(); DiffViewerPreferencesStore store = new();
        Assert.That(store.SaveSettings(store.Load() with { AutomaticContinuousScroll = true, AutomaticContinuousScrollDelay = 60000 }), Is.True);
        MainWindow window = await CommitDiffWindow.CreateAsync(f.Repository, "HEAD"); window.Show();
        try
        {
            await Idle(window); await SelectFile(window, "file.txt"); Diff(window).Editor.ScrollToEnd(); Pump(window); Wheel(window, -1, RawInputModifiers.None); await Idle(window);
            Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo(Literal));
            Wheel(window, 1, RawInputModifiers.None); await Idle(window); Assert.That(Files(window).SelectedEntries.Single().Path, Is.EqualTo(Literal));
            Assert.That(window.IsBusy, Is.False); Assert.That(Diff(window).IsEffectivelyEnabled, Is.True);
        }
        finally { window.Close(); await window.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task AnInvalidOrCancelledCommitSourceNeverConstructsASuccessfulWindowOrChangesGit()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Text(f, "rev-parse", "HEAD"); bool failed = false;
        try { await CommitDiffWindow.CreateAsync(f.Repository, "missing-reference-for-commit-diff"); }
        catch (GitCommandException) { failed = true; }
        Assert.That(failed, Is.True); using CancellationTokenSource cancel = new(); cancel.Cancel(); bool cancelled = false;
        try { await CommitDiffWindow.CreateAsync(f.Repository, "HEAD", cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert.That(cancelled, Is.True); Assert.That(await Text(f, "rev-parse", "HEAD"), Is.EqualTo(before)); Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    private static async Task<Fixture> History()
    {
        Fixture f = await Fixture.CreateAsync();
        try
        {
            await File.WriteAllTextAsync(f.File, "second\n"); await File.WriteAllTextAsync(f.PathInRepository(Literal), "literal old\n"); await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Second");
            await File.WriteAllTextAsync(f.PathInRepository("other.txt"), "hidden parent\n"); await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Hidden parent");
            await File.WriteAllTextAsync(f.File, "third\n"); await File.WriteAllTextAsync(f.PathInRepository(Literal), "literal new\n"); await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Fourth"); return f;
        }
        catch { f.Dispose(); throw; }
    }
    private static async Task<string> Text(Fixture f, params string[] args) => (await f.GitAsync(args)).StandardOutput.TrimEnd('\r', '\n');
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static T Private<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static FileStatusListView Files(MainWindow window) => Field<FileStatusListView>(window, "RevisionFileStatusList");
    private static DiffView Diff(MainWindow window) => Field<DiffView>(window, "CommitDiff");
    private static IReadOnlyList<GitRevisionDiffGroup> Groups(MainWindow window) => Files(window).Entries.Select(entry => ((RevisionDiffFileSelection)entry.Tag).Group).Distinct().ToArray();
    private static Control Header(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "PART_Header" && item.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static async Task SelectFile(MainWindow window, string path)
    { FileStatusListView files = Files(window); object tag = files.Entries.First(entry => entry.Path == path).Tag; PushProcessTestDriver.Pointer(Header(files.FindItemByTag(tag)!)); await Idle(window); }
    private static Point PointInRow(TopLevel top, Control row)
    { Point point = row.TranslatePoint(new Point(Math.Max(1, row.Bounds.Width - 20), row.Bounds.Height / 2), top)!.Value; Assert.That((top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True); return point; }
    private static void DoubleClick(TopLevel top, Control control)
    { Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value; Assert.That((top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True); top.MouseMove(point); for (int index = 0; index < 2; index++) { top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); } Pump(top); }
    private static void Wheel(MainWindow window, double delta, RawInputModifiers modifiers)
    { DiffView diff = Diff(window); Control editor = diff.Editor; Point point = editor.TranslatePoint(new Point(Math.Min(editor.Bounds.Width - 2, 150), editor.Bounds.Height / 2), window)!.Value; Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(editor), Is.True); window.MouseMove(point); window.MouseWheel(point, new Vector(0, delta), modifiers); Pump(window); }
    private static void Press(TopLevel window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); } Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow window)
    { await Until(() => !window.IsBusy && window.WindowWorkCompletion.IsCompleted); Pump(window); Assert.That(window.LastException, Is.Null, window.LastException?.ToString()); }
    private static async Task InputIdle(MainWindow window)
    { await Until(() => !window.IsBusy); Pump(window); Assert.That(window.LastException, Is.Null, window.LastException?.ToString()); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(30)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "CommitDiff did not settle."); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(MainWindow window, string name)
    { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
}
