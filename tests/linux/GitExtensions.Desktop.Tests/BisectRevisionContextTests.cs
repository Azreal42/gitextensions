using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using BaseFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class BisectRevisionContextTests
{
    private static readonly string[] Names = ["RevisionBisectBad", "RevisionBisectGood", "RevisionBisectSkip", "RevisionBisectStop"];

    [AvaloniaTest]
    public async Task OnlyAnActiveSessionShowsTheFourOriginalTopLevelCommandsAndOpeningTheMenuIsReadOnly()
    {
        await using Fixture f = await Fixture.Create(); GitCommit target = f.Commit(2);
        string before = await f.Witness(); ContextMenu menu = OpenSubject(f, target);
        Assert.That(Names.All(name => !Item(menu, name).IsVisible && !Item(menu, name).IsEnabled), Is.True);
        menu.Close(); await Idle(f.Main); Assert.That(await f.Witness(), Is.EqualTo(before));
        await f.Start(); before = await f.Witness(); menu = OpenSubject(f, target); AssertShape(menu);
        Assert.That(Names.Select(name => ((AccessText)Item(menu, name).Header!).Text),
            Is.EqualTo(new[] { "Mark revision as bad", "Mark revision as good", "Skip revision", "Stop bisect" }));
        Assert.That(Names.Select(name => ((Image)Item(menu, name).Icon!).Tag),
            Is.EqualTo(new[] { "BisectBad.png", "BisectGood.png", "BisectSkip.png", "BisectStop.png" }));
        Assert.That(Names.All(name => Item(menu, name).InputGesture is null), Is.True, "Windows declares no shortcuts on this group.");
        menu.Close(); await Idle(f.Main); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(GitBisectOption.Good, true)]
    [TestCase(GitBisectOption.Good, false)]
    [TestCase(GitBisectOption.Bad, true)]
    [TestCase(GitBisectOption.Bad, false)]
    [TestCase(GitBisectOption.Skip, true)]
    [TestCase(GitBisectOption.Skip, false)]
    public async Task ASubjectVerdictClassifiesTheSelectedShaRatherThanCheckoutAndReturnsTheFirstArrow(GitBisectOption verdict, bool up)
    {
        await using Fixture f = await Fixture.Create(); await f.Start();
        int targetIndex = verdict == GitBisectOption.Good ? 1 : verdict == GitBisectOption.Bad ? 6 : 5;
        string target = f.Hashes[targetIndex], checkout = await f.Head(); Assert.That(target, Is.Not.EqualTo(checkout));
        ContextMenu menu = OpenSubject(f, f.Commit(targetIndex)); object? source = f.History.ItemsSource;
        Pointer(Item(menu, "RevisionBisect" + verdict)); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.UseDialogSettings, Is.False); Assert.That(process.CommandDescription, Is.EqualTo("git bisect " + verdict.ToString().ToLowerInvariant() + " " + target));
        Assert.That(process.Result?.ExitCode, Is.EqualTo(0)); Assert.That(process.Error, Is.Null);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain(process.Result!.StandardOutput));
        await Acknowledge(f, process); GitBisectState state = await f.Repository.GetBisectStateAsync();
        if (verdict == GitBisectOption.Good) { Assert.That(state.GoodRevisions, Does.Contain(target).And.Not.Contain(checkout)); }
        else if (verdict == GitBisectOption.Bad) { Assert.That(state.BadRevision, Is.EqualTo(target)); }
        else { Assert.That(state.SkippedRevisions, Does.Contain(target).And.Not.Contain(checkout)); }
        Assert.That(f.History.ItemsSource, Is.SameAs(source), "An actual mutation refreshes Browse after process acknowledgment.");
        FirstArrow(f, target, up);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task PointerControlSelectionThenSubjectOrShiftF10UsesTheLastRowRatherThanTheMultipleSelectionAnchor(bool keyboard)
    {
        await using Fixture f = await Fixture.Create(); await f.Start();
        GitCommit anchor = f.Commit(6), target = f.Commit(1);
        Pointer(f.Main, Subject(f, anchor), MouseButton.Left);
        Pointer(f.Main, Subject(f, target), MouseButton.Left, RawInputModifiers.Control);
        Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { anchor.Hash, target.Hash }));
        Assert.That((f.Main.FocusManager!.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().First().DataContext,
            Is.SameAs(target), "The actual Ctrl pointer gesture, not a programmed SelectedItem, owns the last row.");
        ContextMenu menu;
        if (keyboard)
        {
            Press(f.Main, PhysicalKey.F10, RawInputModifiers.Shift); menu = f.History.ContextMenu!; await Until(() => menu.IsOpen); AssertShape(menu);
        }
        else { menu = OpenSubject(f, target); }
        Pointer(Item(menu, "RevisionBisectGood")); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.CommandDescription, Is.EqualTo("git bisect good " + target.Hash));
        await Acknowledge(f, process);
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Does.Contain(target.Hash).And.Not.Contain(anchor.Hash));
        FirstArrow(f, target.Hash, up: true);
    }

    [AvaloniaTest]
    [TestCase(RawInputModifiers.None)]
    [TestCase(RawInputModifiers.Shift)]
    [TestCase(RawInputModifiers.Control | RawInputModifiers.Shift)]
    public async Task BadgeMenusKeepBisectAtTheTopLevelAndUseTheSameSelectedCommit(RawInputModifiers modifiers)
    {
        await using Fixture f = await Fixture.Create(); await f.Git("branch", "bisect-target", f.Hashes[5]); await f.Start();
        Control badge = f.History.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionReference"
            && control.Tag is GitReference reference && reference.Name == "refs/heads/bisect-target");
        Pointer(f.Main, badge, MouseButton.Right, modifiers); ContextMenu menu = f.History.ContextMenu!; Assert.That(menu.IsOpen, Is.True); AssertShape(menu);
        MenuItem? other = menu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "RevisionOtherActions");
        if (other is not null) { Assert.That(other.Items.OfType<MenuItem>().Where(item => item.Name is string name && Names.Contains(name)), Is.Empty); }
        Pointer(Item(menu, "RevisionBisectSkip")); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.CommandDescription, Is.EqualTo("git bisect skip " + f.Hashes[5])); await Acknowledge(f, process);
        Assert.That((await f.Repository.GetBisectStateAsync()).SkippedRevisions, Does.Contain(f.Hashes[5]));
    }

    [AvaloniaTest]
    public async Task StopUsesDialogSettingsRestoresTheOriginalHeadAndRemovesTheGroupImmediately()
    {
        await using Fixture f = await Fixture.Create(); string original = await f.Head(); await f.Start();
        ContextMenu menu = OpenSubject(f, f.Commit(2)); Pointer(Item(menu, "RevisionBisectStop")); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.UseDialogSettings, Is.True); Assert.That(process.CommandDescription, Is.EqualTo("git bisect reset"));
        Assert.That(Field<CheckBox>(process, "KeepDialogOpen").IsVisible, Is.True); await Acknowledge(f, process);
        Assert.That(await f.Head(), Is.EqualTo(original)); Assert.That((await f.Repository.GetBisectStateAsync()).IsActive, Is.False);
        menu = OpenSubject(f, f.Commit(2)); Assert.That(Names.All(name => !Item(menu, name).IsVisible && !Item(menu, name).IsEnabled), Is.True);
        menu.Close();
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeTerminalZeroAndInconclusiveTwoStayReadableUntilAcknowledgment(bool inconclusive)
    {
        await using Fixture f = await Fixture.Create(inconclusive ? 3 : 2); await f.Start();
        string target = f.Hashes[1]; ContextMenu menu = OpenSubject(f, f.Commit(1));
        Pointer(Item(menu, inconclusive ? "RevisionBisectSkip" : "RevisionBisectBad")); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.IsVisible, Is.True); Assert.That(process.UseDialogSettings, Is.False);
        Assert.That(process.Result?.ExitCode, Is.EqualTo(inconclusive ? 2 : 0)); Assert.That(process.Error, Is.Null);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text,
            Does.Match(inconclusive ? "first (?:bad|'bad') commit could be any of" : "is the first (?:bad|'bad') commit"));
        Assert.That(process.Outcome?.WasCancelled, Is.False); await Acknowledge(f, process);
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True); FirstArrow(f, target, up: false);
    }

    [AvaloniaTest]
    public async Task ASessionResetAfterOpeningCannotRunALateVerdictAndBareOrMissingRepositoriesNeverOfferTheGroup()
    {
        await using Fixture f = await Fixture.Create(); await f.Start(); ContextMenu menu = OpenSubject(f, f.Commit(1));
        MenuItem late = Item(menu, "RevisionBisectGood"); await f.Repository.ResetBisectAsync(); string before = await f.Witness();
        Pointer(late); await Idle(f.Main); Assert.That(f.Main.OwnedWindows.OfType<GitProcessDialog>(), Is.Empty);
        Assert.That(await f.Witness(), Is.EqualTo(before)); menu.Close();
        string bare = Path.Combine(f.Root, "bare.git"); await f.Git("clone", "--bare", f.Repository.Path, bare);
        await f.Main.OpenRepositoryAsync(bare); await Idle(f.Main);
        File.WriteAllText(Path.Combine(bare, "BISECT_START"), "main\n");
        GitCommit commit = f.History.Items.OfType<GitCommit>().First(); menu = OpenSubject(f, commit);
        Assert.That(Names.All(name => !Item(menu, name).IsVisible && !Item(menu, name).IsEnabled), Is.True); menu.Close();
        MainWindow empty = new(); empty.Show();
        try
        {
            ContextMenu emptyMenu = Field<ListBox>(empty, "RevisionHistory").ContextMenu!; emptyMenu.Open(Field<ListBox>(empty, "RevisionHistory")); Pump(empty);
            Assert.That(Names.All(name => !Item(emptyMenu, name).IsVisible && !Item(emptyMenu, name).IsEnabled), Is.True); emptyMenu.Close();
        }
        finally { empty.Close(); await Until(() => !empty.IsVisible && empty.WindowWorkCompletion.IsCompleted); }
    }

    [AvaloniaTest]
    public async Task AMenuInvocationKeepsItsCapturedShaIfSelectionChangesBeforeTheQueuedClickRuns()
    {
        await using Fixture f = await Fixture.Create(); await f.Start(); GitCommit target = f.Commit(1), later = f.Commit(5);
        ContextMenu menu = OpenSubject(f, target); MenuItem invocation = Item(menu, "RevisionBisectGood"); menu.Close();
        // Model a delayed routed Click after dismissal/binding publication. The normal
        // pointer/Ctrl tests above cover the actual last-row selection path separately.
        Pointer(f.Main, Subject(f, later), MouseButton.Left); Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(later.Hash));
        invocation.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.CommandDescription, Is.EqualTo("git bisect good " + target.Hash)); await Acknowledge(f, process);
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Does.Contain(target.Hash).And.Not.Contain(later.Hash));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AQueuedVerdictKeepsItsCapturedCommandTargetButReturnsToTheLatestSingleOrControlSelectionAfterHeadChanges(bool control)
    {
        await using Fixture f = await Fixture.Create(); await f.Start();
        GitCommit target = f.Commit(1), later = f.Commit(5); string checkout = await f.Head();
        ContextMenu menu = OpenSubject(f, target); MenuItem invocation = Item(menu, "RevisionBisectGood"); menu.Close();
        Pointer(f.Main, Subject(f, later), MouseButton.Left, control ? RawInputModifiers.Control : RawInputModifiers.None);
        string[] selected = f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        Assert.That(selected, Is.EquivalentTo(control ? new[] { target.Hash, later.Hash } : new[] { later.Hash }));
        object source = f.History.ItemsSource!;
        // A delayed menu invocation may retain the original command target;
        // its return-selection intention must never reinstall that older row.
        invocation.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.CommandDescription, Is.EqualTo("git bisect good " + target.Hash)); await Acknowledge(f, process);
        Assert.That(await f.Head(), Is.Not.EqualTo(checkout), "This regression requires a real checkout change, not merely a verdict ref.");
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Does.Contain(target.Hash).And.Not.Contain(later.Hash));
        Assert.That(f.History.ItemsSource, Is.SameAs(source));
        Assert.That(f.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(selected));
        FirstArrow(f, later.Hash, up: true);
    }

    [AvaloniaTest]
    public async Task AnOldMenuCannotMutateTheNextRepository()
    {
        await using Fixture f = await Fixture.Create(); await f.Start(); ContextMenu menu = OpenSubject(f, f.Commit(1));
        MenuItem old = Item(menu, "RevisionBisectStop"); menu.Close();
        string other = Path.Combine(f.Root, "other"); GitRepository next = await GitRepository.InitAsync(other);
        await next.RunAsync(["config", "user.name", "Next repository"]); await next.RunAsync(["config", "user.email", "next@example.invalid"]);
        await next.RunAsync(["-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Next"]);
        await f.Main.OpenRepositoryAsync(other); await Idle(f.Main); string head = await next.ResolveRevisionAsync("HEAD");
        old.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Idle(f.Main);
        Assert.That(f.Main.OwnedWindows.OfType<GitProcessDialog>(), Is.Empty); Assert.That(await next.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await f.Repository.GetBisectStateAsync()).IsActive, Is.True, "The original repository's session must also remain active.");
    }

    [AvaloniaTest]
    public async Task LinkedWorktreesUseTheirOwnDiscoveredGitDirectoryInsteadOfTheMainGitDirectory()
    {
        await using Fixture f = await Fixture.Create(); string linked = Path.Combine(f.Root, "linked");
        await f.Git("worktree", "add", "--detach", linked, f.Hashes[^1]); GitRepository repository = await GitRepository.OpenAsync(linked);
        Assert.That(repository.Info!.GitDirectory, Is.Not.EqualTo(repository.Info.CommonDirectory));
        await repository.StartBisectAsync(f.Hashes[^1], [f.Hashes[0]]); await f.Main.OpenRepositoryAsync(linked); await Idle(f.Main);
        ContextMenu menu = OpenSubject(f, f.History.Items.OfType<GitCommit>().First()); AssertShape(menu); menu.Close();
        await f.Main.OpenRepositoryAsync(f.Repository.Path); await Idle(f.Main); menu = OpenSubject(f, f.Commit(1));
        Assert.That(Names.All(name => !Item(menu, name).IsVisible), Is.True, "A linked worktree's active session must not leak into the main worktree."); menu.Close();
        await repository.ResetBisectAsync();
    }

    [AvaloniaTest]
    public async Task ARealNativeFailureRetainsPartialBisectEffectsAndRefreshesBeforeRestoringFocus()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private Git executable fixture uses POSIX process semantics."); return; }
        await using Fixture f = await Fixture.Create(); await f.Start();
        string wrapper = f.WriteGoodWrapper(cancel: false); await f.UseRunner(wrapper);
        string target = f.Hashes[1]; object? source = f.History.ItemsSource;
        ContextMenu menu = OpenSubject(f, f.Commit(1)); Pointer(Item(menu, "RevisionBisectGood")); GitProcessDialog process = await Process(f.Main);
        Assert.That(process.Result?.ExitCode, Is.EqualTo(23)); Assert.That(process.Error, Is.TypeOf<GitCommandException>());
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("native failure after real good"));
        Exception error = process.Error!; await Acknowledge(f, process);
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Does.Contain(target));
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(f.Main.LastException, Is.SameAs(error));
        FirstArrow(f, target, up: true);
        await f.Main.RepositoryMutationCompletion;
        Assert.That(f.Main.LastException, Is.SameAs(error), "A sibling panel read cannot recover the native failure after its partial effects were refreshed.");
        Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.True);
        await Refresh(f.Main); await Idle(f.Main);
        Assert.That(f.Main.LastException, Is.Null, "A new explicit refresh is a real recovery boundary.");
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Does.Contain(target));
    }

    [AvaloniaTest]
    public async Task CancellingARealProcessAfterItsGoodEffectDrainsChildrenAndRefreshesTheActualResult()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private process fixture exercises Linux cancellation."); return; }
        await using Fixture f = await Fixture.Create(); await f.Start(); string wrapper = f.WriteGoodWrapper(cancel: true); await f.UseRunner(wrapper);
        string target = f.Hashes[1]; object? source = f.History.ItemsSource;
        ContextMenu menu = OpenSubject(f, f.Commit(1)); Pointer(Item(menu, "RevisionBisectGood"));
        await Until(() => File.Exists(f.Marker) && f.Main.OwnedWindows.OfType<GitProcessDialog>().Any(window => window.IsRunning));
        GitProcessDialog process = f.Main.OwnedWindows.OfType<GitProcessDialog>().Single();
        int[] pids = File.ReadAllText(f.Marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Pointer(Field<Button>(process, "Abort")); await Until(() => !process.IsVisible && !f.Main.IsBusy && pids.All(pid => !Running(pid)));
        Assert.That(process.Outcome?.WasCancelled, Is.True); Assert.That(process.Result, Is.Null, "Interrupted process must not invent exit zero.");
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Does.Contain(target));
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); FirstArrow(f, target, up: true);
    }

    private static ContextMenu OpenSubject(Fixture f, GitCommit target)
    {
        Pointer(f.Main, Subject(f, target), MouseButton.Right); ContextMenu menu = f.History.ContextMenu!;
        Assert.That(menu.IsOpen, Is.True, "Only an actual pointer opens the subject menu."); return menu;
    }
    private static Control Subject(Fixture f, GitCommit target)
    {
        f.History.ScrollIntoView(target); Pump(f.Main);
        ListBoxItem row = f.History.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is GitCommit commit && commit.Hash == target.Hash);
        return row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionSubject");
    }
    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static void AssertShape(ContextMenu menu)
    {
        Assert.That(menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).Take(4).Select(item => item.Name), Is.EqualTo(Names));
        Assert.That(Names.All(name => Item(menu, name).IsVisible && Item(menu, name).IsEnabled), Is.True);
    }
    private static async Task<GitProcessDialog> Process(Window owner)
    {
        await Until(() => owner.OwnedWindows.OfType<GitProcessDialog>().Any(window => window.IsVisible && !window.IsRunning));
        GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single(window => window.IsVisible); Pump(process); return process;
    }
    private static async Task Acknowledge(Fixture f, GitProcessDialog process)
    {
        Pointer(Field<Button>(process, "Ok")); await Until(() => !process.IsVisible && !f.Main.IsBusy); await Idle(f.Main);
    }
    private static void FirstArrow(Fixture f, string hash, bool up)
    {
        object[] rows = f.History.Items.Cast<object>().ToArray(); int index = Array.FindIndex(rows, item => item is GitCommit commit && commit.Hash == hash);
        Assert.That(index, Is.GreaterThanOrEqualTo(0)); int next = index + (up ? -1 : 1); Assert.That(next, Is.InRange(0, rows.Length - 1));
        // No Focus(), SelectedItem assignment, or synthetic routed event after acknowledgment.
        Press(f.Main, up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown);
        Assert.That(f.History.SelectedItem, Is.SameAs(rows[next])); Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Pointer(TopLevel top, Control control, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        for (int i = 0; i < 4; i++) { Pump(top); }
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && (top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control) == true, Is.True, control.Name);
        top.MouseMove(point); top.MouseDown(point, button, modifiers); top.MouseUp(point, button, modifiers); Pump(top);
    }
    private static void Press(TopLevel top, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { top.KeyPressQwerty(key, modifiers); if (top is not Window window || window.IsVisible) { top.KeyReleaseQwerty(key, modifiers); } Pump(top); }
    private static void Pump(TopLevel top) { Dispatcher.UIThread.RunJobs(); top.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Bisect context UI/process did not settle.");
    }
    private static async Task Idle(MainWindow main) { await Until(() => !main.IsBusy); Pump(main); await main.CommitInformationCompletion; Pump(main); }
    private static Task Refresh(MainWindow main) => (Task)typeof(MainWindow).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null)!;
    private static bool Running(int pid)
    {
        try { string text = File.ReadAllText("/proc/" + pid + "/stat"); return text[(text.LastIndexOf(')') + 2)] is not ('Z' or 'X'); }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private BaseFixture _base = null!;
        private readonly Dictionary<string, string?> _environment = [];
        public MainWindow Main => _base.Window;
        public ListBox History => Field<ListBox>(Main, "RevisionHistory");
        public GitRepository Repository { get; private set; } = null!;
        public List<string> Hashes { get; } = [];
        public string Root => Path.GetDirectoryName(_base.Repository)!;
        public string Marker => Path.Combine(Root, "good-child-pids");
        public GitCommit Commit(int chronological) => History.Items.OfType<GitCommit>().Single(commit => commit.Hash == Hashes[chronological]);
        public static async Task<Fixture> Create(int count = 8)
        {
            Fixture f = new();
            foreach (var (name, value) in new[] { ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { f._environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            try
            {
                f._base = await BaseFixture.CreateAsync("base\n", "base\n"); f.Hashes.Add(await f.Head());
                for (int i = 1; i < count; i++) { await f.Git("commit", "--allow-empty", "-m", "Bisect commit " + i); f.Hashes.Add(await f.Head()); }
                f.Repository = await GitRepository.OpenAsync(f._base.Repository); new ProcessDialogPreferencesStore().Save(new(CloseProcessDialog: false));
                await f.Main.OpenRepositoryAsync(f._base.Repository); await Idle(f.Main); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<GitResult> Git(params string[] args)
        { GitResult result = await _base.Git.RunAsync(_base.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> Head() => (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        public async Task Start()
        { await Repository.StartBisectAsync(Hashes[^1], [Hashes[0]]); await Refresh(Main); await Idle(Main); }
        public async Task<string> Witness()
        {
            string refs = (await Git("for-each-ref", "--format=%(refname)%00%(objectname)")).StandardOutput;
            string status = (await Git("status", "--porcelain=v2", "-z")).StandardOutput;
            string log = (await Repository.GetBisectStateAsync()).Log; return await Head() + "\0" + refs + "\0" + status + "\0" + log;
        }
        public string WriteGoodWrapper(bool cancel)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            static string Quote(string s) => "'" + s.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
            string path = Path.Combine(Root, "git-good-wrapper");
            string tail = cancel ? "sleep 60 &\n child=$!\n printf '%s %s\\n' \"$$\" \"$child\" > " + Quote(Marker) + "\n wait \"$child\""
                : "printf '%s\\n' 'native failure after real good' >&2\n exit 23";
            File.WriteAllText(path, "#!/bin/sh\ncase \" $* \" in *' bisect good '*)\n /usr/bin/env git \"$@\" || exit $?\n " + tail + ";;\n *) exec /usr/bin/env git \"$@\";;\nesac\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        public async Task UseRunner(string wrapper)
        {
            Repository = await GitRepository.OpenAsync(_base.Repository, runner: new GitRunner(wrapper));
            typeof(MainWindow).GetField("_repository", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Main, Repository);
            await Refresh(Main); await Idle(Main);
        }
        public async ValueTask DisposeAsync()
        {
            if (_base is not null)
            {
                foreach (Window child in Main.OwnedWindows.ToArray()) { child.Close(); }
                Main.Close(); await Until(() => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted); _base.Dispose();
            }
            foreach (var (name, value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
        }
    }
}
