using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;
using DesktopProgram = GitExtensions.Linux.Desktop.Program;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionLogWindowTests
{
    [Test]
    public async Task ViewdiffReallyDiscoversItsRepositoryAndSelectsADistinctWindowKindWithoutBrowseFilters()
    {
        using Fixture f = await Fixture.CreateAsync(); Directory.CreateDirectory(f.PathInRepository("literal [1] λ;$(path)"));
        DesktopStartupArguments arguments = DesktopStartupArguments.Parse(["viewdiff", "literal [1] λ;$(path)"]);
        Assert.That(arguments.Mode, Is.EqualTo(DesktopStartupMode.ViewDiff));
        DesktopStartupRequest prepared = await DesktopStartupDispatcher.PrepareAsync(arguments, f.Repository.Path);
        Assert.That(prepared.Repository!.Path, Is.EqualTo(f.Repository.Path)); Assert.That(prepared.BrowseRequest, Is.EqualTo(new BrowseRequest(f.Repository.Path)));
        Assert.That(DesktopStartupArguments.Parse(["viewdiff"]).Mode, Is.EqualTo(DesktopStartupMode.ViewDiff));
        Assert.Throws<ArgumentException>(() => DesktopStartupArguments.Parse(["viewdiff", "repo", "-commit=HEAD"]));
        Assert.Throws<ArgumentException>(() => RevisionLogWindow.Create(new(f.Repository.Path, PathFilter: new("file.txt"))));
        Assert.Throws<ArgumentException>(() => RevisionLogWindow.Create(new(f.Repository.Path, SelectedRevision: new("HEAD"))));
        TextWriter old = Console.Out; using StringWriter usage = new();
        try { Console.SetOut(usage); Assert.That(DesktopProgram.Run(["viewdiff", "--help"], () => throw new InvalidOperationException("Unexpected GUI.")), Is.Zero); }
        finally { Console.SetOut(old); }
        Assert.That(usage.ToString(), Is.EqualTo(DesktopProgram.CommandlineUsage + Environment.NewLine)); Assert.That(usage.ToString(), Does.Contain("viewdiff [repository-directory]"));
        Assert.That(DesktopProgram.PublicCommandlineForms, Does.Contain("viewdiff [repository-directory]"));
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task DedicatedLayoutHasTheSharedRealGridFilesAndDiffAndTheFirstClickAndArrowWork(string culture, bool dark)
    {
        using Fixture f = await History(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        BrowserLayoutPreferencesStore layout = new(); layout.Save(new(CommitInfoPosition.LeftwardFromList)); byte[] browseLayout = await File.ReadAllBytesAsync(layout.FilePath);
        DesktopStartupRequest request = await DesktopStartupDispatcher.PrepareAsync(DesktopStartupArguments.Parse(["viewdiff", f.Repository.Path]), f.Repository.Path);
        MainWindow log = (MainWindow)DesktopStartupDispatcher.CreateMainWindow(request); log.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Window owner = new(); owner.Show(); Task shown = log.ShowDialog(owner);
        try
        {
            await Idle(log); Pump(log);
            ListBox history = Field<ListBox>(log, "RevisionHistory");
            Grid body = Field<Grid>(log, "RevisionLogLayout");
            Grid pair = Field<Grid>(log, "RevisionDiffSplit");
            TabControl details = Field<TabControl>(log, "DetailsTabs");
            Assert.Multiple(() =>
            {
                Assert.That(log.WindowKind, Is.EqualTo(MainWindowKind.RevisionLog)); Assert.That(log.Name, Is.EqualTo("RevisionLogWindow"));
                Assert.That(log.ClientSize.Width, Is.EqualTo(750).Within(1)); Assert.That(log.ClientSize.Height, Is.EqualTo(529).Within(1));
                Assert.That(log.Owner, Is.SameAs(owner)); Assert.That(body.RowDefinitions[0].ActualHeight, Is.EqualTo(205).Within(1));
                Assert.That(pair.ColumnDefinitions[0].ActualWidth, Is.EqualTo(188).Within(1));
                Assert.That(log.GetVisualDescendants().OfType<Menu>().Any(), Is.False);
                Assert.That(log.GetVisualDescendants().OfType<Control>().Any(control => control.Name is "RepositoryNavigation" or "RepositoryToolbar" or "CommitInformation"), Is.False);
                Assert.That(details.Items.OfType<TabItem>().Select(tab => tab.Name), Is.EqualTo(new[] { "DiffTab" }));
                Assert.That(details.SelectedContent, Is.SameAs(((TabItem)details.SelectedItem!).Content), "The headerless host must publish the selected tab content through TabControl.");
                Assert.That(Field<DiffView>(log, "CommitDiff").Bounds.Width, Is.GreaterThan(300));
            });
            Capture(log, "revision-log-" + culture + "-" + (dark ? "dark" : "light") + ".png");
            GitCommit third = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Third");
            Control row = (Control)history.ContainerFromIndex(history.Items.IndexOf(third))!;
            PushProcessTestDriver.Pointer(row); await Idle(log);
            Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Third")); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Press(log, PhysicalKey.ArrowDown); await Idle(log);
            Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Second")); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(Field<DiffView>(log, "CommitDiff").Text, Does.Contain("+second"));
            log.KeyTextInput("Fourth"); await Idle(log);
            Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Fourth")); Assert.That(Field<Border>(log, "RevisionQuickSearchNotice").IsVisible, Is.True);
            Press(log, PhysicalKey.Escape); Assert.That(log.IsVisible, Is.True, "Escape first dismisses the active shared quick search.");
            Assert.That(Field<Border>(log, "RevisionQuickSearchNotice").IsVisible, Is.False);
            Assert.That(await File.ReadAllBytesAsync(layout.FilePath), Is.EqualTo(browseLayout), "Log geometry must not publish a Browse CommitInfo layout.");
            Press(log, PhysicalKey.Escape); await Until(() => !log.IsVisible && log.WindowWorkCompletion.IsCompleted); await shown;
            Assert.That(owner.IsVisible, Is.True, "Closing Log must not close its owner.");
        }
        finally { log.Close(); await log.WindowWorkCompletion; await shown; owner.Close(); }
    }

    [AvaloniaTest]
    public async Task ExplicitUpdateAdmissionOnLogNeverArmsTheBrowseSchedulerOrTouchesItsState()
    {
        using Fixture f = await History(); new PreferencesStore().Save(new DesktopPreferences { CheckForUpdates = true });
        MainWindow log = RevisionLogWindow.Create(new(f.Repository.Path)); UpdateCheckStateStore state = new(f.Output("log-update-state"));
        int clockCalls = 0, clientCalls = 0;
        log.UpdatesClientFactory = () => { clientCalls++; throw new InvalidOperationException("Log cannot open the Browse update client."); };
        log.EnableAutomaticUpdates(state, () => { clockCalls++; return DateTimeOffset.UtcNow; }); log.Show();
        try
        {
            await Idle(log); ListBox history = Field<ListBox>(log, "RevisionHistory");
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!); await Idle(log);
            Assert.Multiple(() =>
            {
                Assert.That(log.AutomaticUpdatesCompletion.IsCompletedSuccessfully, Is.True);
                Assert.That(clockCalls, Is.Zero); Assert.That(clientCalls, Is.Zero);
                Assert.That(File.Exists(state.FilePath), Is.False); Assert.That(File.Exists(state.FilePath + ".lock"), Is.False);
                Assert.That(log.OwnedWindows, Is.Empty);
            });
        }
        finally { log.Close(); await log.WindowWorkCompletion; await log.AutomaticUpdatesCompletion; }
    }

    [AvaloniaTest]
    public async Task FileMenuKeepsItsRealLeafAndFirstArrowButDoesNotOfferAbsentBrowsePanes()
    {
        using Fixture f = await History(); MainWindow log = RevisionLogWindow.Create(new(f.Repository.Path)); log.Show();
        try
        {
            await Idle(log); FileStatusListView view = Field<FileStatusListView>(log, "RevisionFileStatusList");
            object tag = view.Entries.Single(entry => entry.Path == "file.txt").Tag; view.SelectTags([tag]); await Idle(log);
            TreeViewItem file = view.FindItemByTag(tag)!; Control row = Header(file); PushProcessTestDriver.Pointer(row); await Idle(log);
            Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), log)!.Value;
            log.MouseMove(point); log.MouseDown(point, MouseButton.Right); log.MouseUp(point, MouseButton.Right); Pump(log);
            ContextMenu menu = view.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name is "tsmiShowInFileTree" or "tsmiFilterFileInGrid" or "tsmiOpenFindInCommitFilesGitGrepDialog" or "tsmiShowFindInCommitFilesGitGrep"), Is.False);
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "tsmiSaveAs"), Is.True);
            Assert.That(log.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "RevisionFilesFindInCommitFiles"), Is.False);
            TopLevel popup = TopLevel.GetTopLevel(menu)!; Press(popup, PhysicalKey.Escape); await Until(() => !menu.IsOpen);
            Assert.That(log.IsVisible, Is.True); Press(log, PhysicalKey.ArrowDown); await Idle(log);
            Assert.That(view.SelectedEntries.Select(entry => entry.Path), Is.EqualTo(new[] { "other.txt" }));
            Assert.That(Field<DiffView>(log, "CommitDiff").Text, Does.Contain("+fourth other"));
        }
        finally { log.Close(); await log.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task IndexAndWorkingTreeAreRealDiffSourcesAndNeverJumpToAnAbsentChangesTab()
    {
        using Fixture f = await History(); await File.WriteAllTextAsync(f.File, "actual index\n"); await f.Repository.StageAllAsync(); await File.WriteAllTextAsync(f.File, "actual working\n");
        MainWindow log = RevisionLogWindow.Create(new(f.Repository.Path)); log.Show();
        try
        {
            await Idle(log); PushProcessTestDriver.Pointer(Field<Button>(log, "IndexRevision").FindAncestorOfType<ListBoxItem>()!); await Idle(log);
            Assert.That(Field<DiffView>(log, "CommitDiff").Text, Does.Contain("+actual index").And.Not.Contain("+actual working"));
            Assert.That(((TabItem)Field<TabControl>(log, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
            Press(log, PhysicalKey.ArrowUp); await Idle(log);
            Assert.That(Field<DiffView>(log, "CommitDiff").Text, Does.Contain("-actual index").And.Contain("+actual working"));
            Press(log, PhysicalKey.ArrowDown); await Idle(log); Press(log, PhysicalKey.ArrowDown); await Idle(log);
            Assert.That(((GitCommit)Field<ListBox>(log, "RevisionHistory").SelectedItem!).Subject, Is.EqualTo("Fourth"));
            Assert.That(log.LastException, Is.Null);
        }
        finally { log.Close(); await log.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task EmptyGridDoubleClickOpensTheOwnedLogAndClosingItReturnsTheFirstArrowToTheOwner()
    {
        using Fixture f = await Fixture.CreateAsync(unborn: true);
        HistoryViewPreferencesStore viewStore = new(); viewStore.Save(viewStore.Load() with { ShowArtificialCommits = false });
        MainWindow owner = new(new BrowseRequest(f.Repository.Path)) { Width = 1200, Height = 800 }; owner.Show();
        try
        {
            await Idle(owner); ListBox history = Field<ListBox>(owner, "RevisionHistory"); Assert.That(history.Items, Is.Empty);
            Point point = history.TranslatePoint(new Point(history.Bounds.Width / 2, history.Bounds.Height / 2), owner)!.Value;
            Visual? hit = owner.InputHitTest(point) as Visual;
            Assert.That(hit?.GetSelfAndVisualAncestors().Contains(history), Is.True, "The raw pointer must hit the real empty history surface.");
            Assert.That(hit?.GetSelfAndVisualAncestors().OfType<ListBoxItem>(), Is.Empty);
            List<int> clickCounts = [];
            owner.AddHandler(InputElement.PointerPressedEvent, (_, args) => clickCounts.Add(args.ClickCount), RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Pump(owner);
            Assert.That(clickCounts, Is.EqualTo(new[] { 1 })); Assert.That(history.SelectedItem, Is.Null);
            Assert.That(owner.OwnedWindows, Is.Empty, "One actual click on an empty grid must not open Log.");
            owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Pump(owner);
            Assert.That(clickCounts, Is.EqualTo(new[] { 1, 2 }), "Headless MouseDevice must recognize the real second press as a double click.");
            Assert.That(history.SelectedItem, Is.Null);
            await Until(() => owner.OwnedWindows.OfType<MainWindow>().Any(window => window.WindowKind == MainWindowKind.RevisionLog));
            MainWindow log = owner.OwnedWindows.OfType<MainWindow>().Single(); await Idle(log);
            Press(log, PhysicalKey.Escape); await Until(() => !log.IsVisible && !owner.IsBusy && log.WindowWorkCompletion.IsCompleted);
            Assert.That(owner.IsVisible, Is.True); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            // This owner is genuinely unborn: the first arrow stays on its empty history rather than reopening a dialog.
            Press(owner, PhysicalKey.ArrowDown); Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(history.SelectedItem, Is.Null);
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); await owner.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task ReturningFromTheOwnedLogKeepsTheOwnerSelectionAndItsFirstArrowWithoutFocusRepair()
    {
        using Fixture f = await History(); MainWindow owner = new(new BrowseRequest(f.Repository.Path)) { Width = 1200, Height = 800 }; owner.Show();
        try
        {
            await Idle(owner); ListBox history = Field<ListBox>(owner, "RevisionHistory"); GitCommit third = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Third");
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(history.Items.IndexOf(third))!); await Idle(owner);
            Task pending = owner.ShowRevisionLogAsync(); await Until(() => owner.OwnedWindows.OfType<MainWindow>().Any());
            MainWindow log = owner.OwnedWindows.OfType<MainWindow>().Single(); await Idle(log); Press(log, PhysicalKey.Escape);
            await Until(() => pending.IsCompleted && !owner.IsBusy); await pending; await Idle(owner);
            Assert.That(owner.IsVisible, Is.True); Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Third")); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Press(owner, PhysicalKey.ArrowDown); await Idle(owner);
            Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Second")); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); await owner.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task BoundedHistoryUsesTheExistingLoadMorePolicyAndTwoSelectedRevisionsUseTheActualPair()
    {
        using Fixture f = await History(); new PreferencesStore().Save(new DesktopPreferences { HistoryLimit = 2 });
        MainWindow log = RevisionLogWindow.Create(new(f.Repository.Path)); log.Show();
        try
        {
            await Idle(log); ListBox history = Field<ListBox>(log, "RevisionHistory"); Assert.That(history.Items.OfType<GitCommit>().Count(), Is.EqualTo(2));
            PushProcessTestDriver.Pointer(Field<Button>(log, "RevisionLogLoadMore")); await Idle(log);
            Assert.That(history.Items.OfType<GitCommit>().Count(), Is.EqualTo(4));
            GitCommit third = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Third"), fourth = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Fourth");
            object source = history.ItemsSource!;
            System.Reflection.FieldInfo queryField = typeof(MainWindow).GetField("_historyQuery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            GitLogQuery filter = (GitLogQuery)queryField.GetValue(log)!; GitLogQuery expectedFilter = filter with { };
            // The last real click is the native current cell B: Third -> Fourth.
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(history.Items.IndexOf(third))!); await Idle(log);
            Control row = (Control)history.ContainerFromIndex(history.Items.IndexOf(fourth))!; Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), log)!.Value;
            Assert.That(row.IsEffectivelyVisible && row.IsEffectivelyEnabled, Is.True);
            Assert.That(new Rect(default, log.ClientSize).Contains(point), Is.True);
            Assert.That((log.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
            log.MouseMove(point); log.MouseDown(point, MouseButton.Left, RawInputModifiers.Control); log.MouseUp(point, MouseButton.Left, RawInputModifiers.Control); await Idle(log);
            Assert.That(history.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Subject), Is.EquivalentTo(new[] { "Third", "Fourth" }));
            ListBoxItem? current = (log.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(item.FindAncestorOfType<ListBox>(), history));
            Assert.That(current?.Content, Is.SameAs(fourth), "The last clicked Fourth cell owns actual focus, independently of Avalonia SelectedItem.");
            GitRevisionDiffGroup comparison = Field<TreeView>(log, "RevisionFiles").Items.OfType<TreeViewItem>().Select(item => item.Tag)
                .OfType<GitRevisionDiffGroup>().Single(group => group.Kind == GitRevisionDiffGroupKind.Comparison);
            Assert.That(comparison.Revision, Is.EqualTo(fourth.Hash)); Assert.That(comparison.BaseRevision, Is.EqualTo(third.Hash));
            Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(queryField.GetValue(log), Is.SameAs(filter)); Assert.That(queryField.GetValue(log), Is.EqualTo(expectedFilter));
            Assert.That(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == third.Hash), Is.SameAs(third));
            Assert.That(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == fourth.Hash), Is.SameAs(fourth));
            string diff = Field<DiffView>(log, "CommitDiff").Text; Assert.That(diff, Does.Contain("-third").And.Contain("+fourth"));
            Assert.That(log.LastException, Is.Null);
        }
        finally { log.Close(); await log.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARealGitReadIsCancelledAndDrainedBeforeLogOrItsOwnerCanClose(bool closeOwner)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private POSIX Git process gate."); return; }
        using Fixture f = await History(); string marker = f.Output("log-started"), release = f.Output("release-log"), wrapper = f.Output("log-git.py");
        await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport os,sys,time\nif 'log' in sys.argv and os.getcwd()==" + JsonSerializer.Serialize(f.Repository.Path) + ":\n"
            + " with open(" + JsonSerializer.Serialize(marker) + ",'w') as f:f.write(str(os.getpid()))\n"
            + " while not os.path.exists(" + JsonSerializer.Serialize(release) + "):time.sleep(.01)\n"
            + "os.execv('/usr/bin/git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitEnvironmentOptions original = GitEnvironment.Options; Window owner = new(); owner.Show(); MainWindow? log = null; Process? child = null; Task? shown = null;
        try
        {
            GitEnvironment.Configure(original with { Executable = wrapper }); log = RevisionLogWindow.Create(new(f.Repository.Path)); shown = log.ShowDialog(owner);
            await Until(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture)); _ = child.SafeHandle;
            Task pending = log.WindowWorkCompletion;
            if (closeOwner) { owner.Close(); } else { Press(log, PhysicalKey.Escape); }
            await Until(() => !log.IsVisible && pending.IsCompleted && log.WindowWorkCompletion.IsCompleted && shown.IsCompleted);
            await pending; await shown; await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(child.HasExited, Is.True); Assert.That(log.LastException, Is.Null);
            Assert.That(Field<ListBox>(log, "RevisionHistory").Items, Is.Empty, "Late cancelled history cannot be published.");
            Assert.That(owner.IsVisible, Is.EqualTo(!closeOwner));
        }
        finally
        {
            await File.WriteAllTextAsync(release, ""); log?.Close(); if (log is not null) { await log.WindowWorkCompletion; }
            if (shown is not null) { await shown; } child?.Dispose(); owner.Close(); GitEnvironment.Configure(original);
        }
    }

    private static async Task<Fixture> History()
    {
        Fixture f = await Fixture.CreateAsync();
        try
        {
            foreach ((string subject, string text) in new[] { ("Second", "second"), ("Third", "third"), ("Fourth", "fourth") })
            {
                await File.WriteAllTextAsync(f.File, text + "\n"); await File.WriteAllTextAsync(f.PathInRepository("other.txt"), text + " other\n");
                await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", subject);
            }
            return f;
        }
        catch { f.Dispose(); throw; }
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static Control Header(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "PART_Header" && item.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow window) { await Until(() => !window.IsBusy && window.WindowWorkCompletion.IsCompleted); Pump(window); Assert.That(window.LastException, Is.Null, window.LastException?.ToString()); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(30)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Revision Log fixture did not settle."); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }
}
