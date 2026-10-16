using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class MainWindowBusyFocusTests
{
    [AvaloniaTest]
    [TestCase(0, false)]
    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public async Task CreateFromRevisionContextReturnsToTheOriginRowAfterTheInitializedModalCloses(int closeMode, bool multiple)
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; ListBox history = Field<ListBox>(main, "RevisionHistory"); await Ready(main);
        GitCommit[] commits = history.Items.OfType<GitCommit>().ToArray(); int originIndex = 2;
        ListBoxItem origin = (ListBoxItem)history.ContainerFromIndex(originIndex)!;
        if (multiple)
        {
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!);
            Pointer(main, origin, MouseButton.Left, RawInputModifiers.Control);
        }
        Pointer(main, origin, MouseButton.Right); await Until(() => history.ContextMenu!.IsOpen);
        string[] selected = history.SelectedItems!.OfType<GitCommit>().Select(c => c.Hash).ToArray();
        if (closeMode == 3) { Press(TopLevel.GetTopLevel(history.ContextMenu!)!, PhysicalKey.B, RawInputModifiers.Control); }
        else { PushProcessTestDriver.Pointer(history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCreateBranch")); }
        await Until(() => main.OwnedWindows.OfType<CreateBranchDialog>().Any());
        CreateBranchDialog child = main.OwnedWindows.OfType<CreateBranchDialog>().Single();
        TextBox name = Field<TextBox>(child, "CreateBranchName"); TextBlock summary = Field<TextBlock>(child, "CreateBranchSummary");
        await Until(() => !child.IsBusy && name.IsEnabled && !string.IsNullOrEmpty(summary.Text)); Pump(child);
        Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(name));
        child.KeyTextInput("m"); Pump(child); Assert.That(name.Text, Is.EqualTo("m"));
        child.KeyTextInput("arker"); Pump(child); Assert.That(name.Text, Is.EqualTo("marker"));
        if (closeMode is 0 or 3) { Press(child, PhysicalKey.Escape); }
        else if (closeMode == 1) { PushProcessTestDriver.Pointer(Field<Button>(child, "CancelCreateBranch")); }
        else { child.Close(); }
        await Ready(main); Log(main, "after Create cancellation");
        Assert.That(history.SelectedItems!.OfType<GitCommit>().Select(c => c.Hash), Is.EquivalentTo(selected));
        Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(origin), "The completed busy workflow must return to its revision row, without a driver Focus().");
        Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedItem, Is.SameAs(commits[originIndex - 1]));
        Press(main, PhysicalKey.ArrowDown); Assert.That(history.SelectedItem, Is.SameAs(commits[originIndex]));
        Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedItem, Is.SameAs(commits[originIndex - 1]));
        Assert.That(main.LastException, Is.Null);
        GitResult branches = await f.Git.RunAsync(f.Repository, ["branch", "--list", "marker"]); Assert.That(branches.StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task AReferenceSubmenuReturnsToItsRevisionAfterRenameIsCancelled()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; ListBox history = Field<ListBox>(main, "RevisionHistory"); await Ready(main);
        GitCommit[] commits = history.Items.OfType<GitCommit>().ToArray();
        int index = Array.FindIndex(commits, commit => commit.Subject == "Feature branch change");
        Assert.That(index, Is.GreaterThan(0)); ListBoxItem origin = (ListBoxItem)history.ContainerFromIndex(index)!;
        GitResult before = await f.Git.RunAsync(f.Repository, ["show-ref"]);
        Pointer(main, origin, MouseButton.Right); await Until(() => history.ContextMenu!.IsOpen);
        MenuItem rename = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionRenameBranch");
        PushProcessTestDriver.Pointer(rename); await Until(() => rename.IsSubMenuOpen);
        MenuItem entry = rename.Items.OfType<MenuItem>().Single(item => (item.Tag as GitReference)?.ShortName == "feature");
        PushProcessTestDriver.Pointer(entry); await Until(() => main.OwnedWindows.OfType<RenameBranchDialog>().Any());
        RenameBranchDialog child = main.OwnedWindows.OfType<RenameBranchDialog>().Single();
        TextBox name = Field<TextBox>(child, "RenameBranchName");
        await Until(() => !child.IsBusy && name.IsEnabled); Pump(child);
        Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(name));
        Press(child, PhysicalKey.A, RawInputModifiers.Control); child.KeyTextInput("discarded-rename"); Pump(child); Assert.That(name.Text, Is.EqualTo("discarded-rename"));
        Press(child, PhysicalKey.Escape); await Ready(main);
        Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(origin));
        Assert.That(history.SelectedItem, Is.SameAs(commits[index]));
        Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedItem, Is.SameAs(commits[index - 1]));
        Press(main, PhysicalKey.ArrowDown); Assert.That(history.SelectedItem, Is.SameAs(commits[index]));
        Assert.That((await f.Git.RunAsync(f.Repository, ["show-ref"])).StandardOutput, Is.EqualTo(before.StandardOutput));
    }

    [AvaloniaTest]
    public async Task AContextOriginIsConsumedOnceAndOverlappingScopesCannotReinstallAnOldOrigin()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(2)!);
        TaskCompletionSource firstRelease = new(), secondRelease = new();
        Task first = Scope(main, async () => { Capture(main); await firstRelease.Task; });
        Task? work = null, second = null;
        try
        {
            TextBox search = Field<TextBox>(main, "RepositoryNavigationSearch"); PushProcessTestDriver.Pointer(search);
            work = Run(main, _ => gate.Repository.GetStatusAsync(Token(main)));
            await Until(() => gate.HasStarted); gate.Release(); await work; await Ready(main);
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(search), "A consumed context bookmark must not leak into the next operation while its original callback is still pending.");
            main.KeyTextInput("m"); Pump(main); Assert.That(search.Text, Is.EqualTo("m"));
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!);
            string hash = ((GitCommit)history.SelectedItem!).Hash;
            second = Scope(main, () => secondRelease.Task);
            firstRelease.SetResult(); await first;
            object origin = Capture(main)!;
            Assert.That(origin.GetType().GetProperty("CommitHash")!.GetValue(origin), Is.EqualTo(hash));
            Assert.That(origin.GetType().GetProperty("Input")!.GetValue(origin), Is.Null, "Completing the previous scope must leave the newer context origin intact.");
            secondRelease.SetResult(); await second; PushProcessTestDriver.Pointer(search);
            object input = Capture(main)!;
            Assert.That(input.GetType().GetProperty("Input")!.GetValue(input), Is.SameAs(search), "Completing the newer scope must not reinstall an older context origin.");
        }
        finally
        {
            gate.Release(); firstRelease.TrySetResult(); secondRelease.TrySetResult();
            if (work is not null) { await work; } await first; if (second is not null) { await second; }
        }
    }

    [AvaloniaTest]
    public async Task ASharedComparisonMenuActionReturnsToItsRevisionAfterTheModalCloses()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem origin = (ListBoxItem)history.ContainerFromIndex(2)!; GitCommit selected = (GitCommit)origin.DataContext!;
        GitResult before = await f.Git.RunAsync(f.Repository, ["show-ref"]);
        Pointer(main, origin, MouseButton.Right); await Until(() => history.ContextMenu!.IsOpen);
        MenuItem compare = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCompare");
        PushProcessTestDriver.Pointer(compare); await Until(() => compare.IsSubMenuOpen);
        PushProcessTestDriver.Pointer(compare.Items.OfType<MenuItem>().Single(item => item.Header as string == "Compare with current branch"));
        await Until(() => main.OwnedWindows.OfType<CompareRevisionsDialog>().Any());
        CompareRevisionsDialog child = main.OwnedWindows.OfType<CompareRevisionsDialog>().Single();
        await Until(() => !child.IsBusy && child.Comparison is not null); Pump(child);
        PushProcessTestDriver.Pointer(Field<Button>(child, "CloseComparison")); await Ready(main);
        Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(origin));
        Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedIndex, Is.EqualTo(1));
        Press(main, PhysicalKey.ArrowDown); Assert.That(history.SelectedItem, Is.SameAs(selected));
        Assert.That((await f.Git.RunAsync(f.Repository, ["show-ref"])).StandardOutput, Is.EqualTo(before.StandardOutput));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AShortcutDispatchedFromTheRevisionPopupPreservesItsOriginForNavigation(bool compare)
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem origin = (ListBoxItem)history.ContainerFromIndex(2)!; string hash = ((GitCommit)origin.DataContext!).Hash;
        object source = history.ItemsSource!; Pointer(main, origin, MouseButton.Right); await Until(() => history.ContextMenu!.IsOpen);
        Press(TopLevel.GetTopLevel(history.ContextMenu!)!, compare ? PhysicalKey.R : PhysicalKey.F5,
            compare ? RawInputModifiers.Control : RawInputModifiers.None);
        await Until(() => !history.ContextMenu!.IsOpen);
        if (compare)
        {
            await Until(() => main.OwnedWindows.OfType<CompareRevisionsDialog>().Any());
            CompareRevisionsDialog child = main.OwnedWindows.OfType<CompareRevisionsDialog>().Single();
            await Until(() => !child.IsBusy && child.Comparison is not null); Press(child, PhysicalKey.Escape);
        }
        else { await Until(() => !ReferenceEquals(history.ItemsSource, source)); }
        await Ready(main); Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(hash));
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(history.SelectedIndex)!;
        Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(row));
        Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedIndex, Is.EqualTo(1));
        Press(main, PhysicalKey.ArrowDown); Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(hash));
    }

    [AvaloniaTest]
    public async Task AHistorySubmenuShortcutReturnsToTheRevisionAfterTheFilterIsCancelled()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem origin = (ListBoxItem)history.ContainerFromIndex(2)!; GitCommit selected = (GitCommit)origin.DataContext!;
        object source = history.ItemsSource!;
        object? query = typeof(MainWindow).GetField("_historyQuery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        Pointer(main, origin, MouseButton.Right); await Until(() => history.ContextMenu!.IsOpen);
        MenuItem view = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionView");
        PushProcessTestDriver.Pointer(view); await Until(() => view.IsSubMenuOpen);
        Press(TopLevel.GetTopLevel(view)!, PhysicalKey.I, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog child = main.OwnedWindows.OfType<HistoryFilterDialog>().Single(); Pump(child);
        Control initial = (Control)typeof(HistoryFilterDialog).GetField("_sinceEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child)!;
        Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(initial), "The dialog must own its initial input so a real Escape reaches its Cancel button.");
        Press(child, PhysicalKey.Escape);
        await Ready(main); await Until(() => !history.ContextMenu!.IsOpen);
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
        Assert.That(typeof(MainWindow).GetField("_historyQuery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main), Is.SameAs(query));
        Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedIndex, Is.EqualTo(1), "The first Up after the context View/Ctrl+I modal must navigate commits.");
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Press(main, PhysicalKey.ArrowDown); Assert.That(history.SelectedItem, Is.SameAs(selected));
    }

    [AvaloniaTest]
    public async Task RefreshResolvesTheFocusedRowByHashAfterReplacingTheHistoryObjects()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); MainWindow main = f.Window;
        ListBox history = Field<ListBox>(main, "RevisionHistory"); await Ready(main);
        GitCommit selected = history.Items.OfType<GitCommit>().ElementAt(2); object source = history.ItemsSource!;
        PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(2)!); Press(main, PhysicalKey.F5);
        await Until(() => !ReferenceEquals(history.ItemsSource, source)); await Ready(main); Log(main, "after real F5");
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(selected.Hash));
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(history.SelectedIndex)!;
        Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(row));
        Press(main, PhysicalKey.ArrowUp); Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(history.Items.OfType<GitCommit>().ElementAt(1).Hash));
    }

    [AvaloniaTest]
    public async Task TheOriginalMainInputReturnsAfterItsBusyDisabledInterval()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); TextBox search = Field<TextBox>(main, "RepositoryNavigationSearch");
        PushProcessTestDriver.Pointer(search);
        Task work = Run(main, _ => gate.Repository.GetStatusAsync(Token(main)));
        try
        {
            await Until(() => gate.HasStarted); Assert.That(search.IsEffectivelyEnabled, Is.False);
            gate.Release(); await work; await Ready(main);
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(search));
            main.KeyTextInput("m"); Pump(main); Assert.That(search.Text, Is.EqualTo("m"));
        }
        finally { gate.Release(); await work; }
    }

    [AvaloniaTest]
    public async Task AValidInformationFieldChosenDuringBusyIsNeverReplacedByTheOldGridFocus()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(2)!);
        Task work = Run(main, _ => gate.Repository.GetStatusAsync(Token(main)));
        try
        {
            await Until(() => gate.HasStarted); TextBox info = Field<TextBox>(main, "CommitInformation");
            Assert.That(info.IsEffectivelyEnabled, Is.True); PushProcessTestDriver.Pointer(info);
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(info));
            gate.Release(); await work; await Ready(main);
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(info));
        }
        finally { gate.Release(); await work; }
    }

    [AvaloniaTest]
    public async Task AnOwnedModalKeepsItsEditingFocusWhenTheBusyReadCompletes()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(2)!);
        Task work = Run(main, _ => gate.Repository.GetStatusAsync(Token(main))); GoToCommitDialog? child = null; Task? shown = null;
        try
        {
            await Until(() => gate.HasStarted); child = new(new(f.Repository), readClipboard: _ => Task.FromResult<string?>(null));
            shown = child.ShowDialog(main); Pump(child); TextBox expression = Field<TextBox>(child, "textboxCommitExpression");
            Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(expression));
            gate.Release(); await work; Pump(child);
            Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(expression));
            child.KeyTextInput("marker"); Pump(child); Assert.That(expression.Text, Is.EqualTo("marker"));
        }
        finally { gate.Release(); await work; child?.Close(); if (shown is not null) { await shown; } }
    }

    [AvaloniaTest]
    public async Task ReplacingTheRepositoryInstanceAtTheSamePathInvalidatesTheCapturedRow()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(2)!; PushProcessTestDriver.Pointer(row);
        Task work = Run(main, async _ =>
        {
            await gate.Repository.GetStatusAsync(Token(main));
            typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, new GitRepository(f.Repository));
        });
        try
        {
            await Until(() => gate.HasStarted); gate.Release(); await work; await Ready(main);
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.Not.SameAs(row));
        }
        finally { gate.Release(); await work; }
    }

    [AvaloniaTest]
    public async Task CancelToolbarDrainsTheRealGitProcessThenRestoresTheRevisionOrigin()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(2)!; PushProcessTestDriver.Pointer(row);
        Task work = Run(main, _ => gate.Repository.GetStatusAsync(Token(main)));
        try
        {
            await Until(() => gate.HasStarted); int pid = gate.Pid;
            PushProcessTestDriver.Pointer(Field<Button>(main, "BrowseCancelOperation")); await work; await Ready(main);
            Assert.That(Alive(pid), Is.False); Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(row));
            Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedIndex, Is.EqualTo(1));
        }
        finally { gate.Release(); await work; }
    }

    [AvaloniaTest]
    public async Task AClosedMainWindowNeverRestoresItsFormerRevisionInput()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(2)!; PushProcessTestDriver.Pointer(row);
        Task work = Run(main, _ => gate.Repository.GetStatusAsync(Token(main)));
        try
        {
            await Until(() => gate.HasStarted); int pid = gate.Pid; main.Close(); await work;
            Assert.That(Alive(pid), Is.False); Assert.That(main.IsVisible, Is.False); Assert.That(main.IsBusy, Is.False);
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.Not.SameAs(row));
        }
        finally { gate.Release(); await work; }
    }

    [AvaloniaTest]
    public async Task AFailedBusyReadReturnsToTheRevisionForImmediateNavigation()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true); using Gate gate = new(f);
        MainWindow main = f.Window; await Ready(main); ListBox history = Field<ListBox>(main, "RevisionHistory");
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(2)!; PushProcessTestDriver.Pointer(row);
        Task work = Run(main, async _ => { await gate.Repository.GetStatusAsync(Token(main)); throw new InvalidOperationException("Fixture failed operation"); });
        try
        {
            await Until(() => gate.HasStarted); gate.Release(); await work; await Ready(main);
            Assert.That(((TextBox)typeof(MainWindow).GetField("_output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Text,
                Does.Contain("Fixture failed operation"), "Panel-read resumption may clear LastException, but the actual caught failure remains in command output.");
            Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(row)); Press(main, PhysicalKey.ArrowUp); Assert.That(history.SelectedIndex, Is.EqualTo(1));
        }
        finally { gate.Release(); await work; }
    }

    private static Task Scope(MainWindow main, Func<Task> action) => (Task)typeof(MainWindow)
        .GetMethod("WithRevisionActionFocusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [action])!;
    private static object? Capture(MainWindow main) => typeof(MainWindow)
        .GetMethod("CaptureOperationFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
    private static Task Run(MainWindow main, Func<GitRepository, Task> action) => (Task)typeof(MainWindow)
        .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, ["Fixture native read", action, false, true])!;
    private static CancellationToken Token(MainWindow main) => (CancellationToken)typeof(MainWindow).GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static bool Alive(int pid)
    { try { string stat = System.IO.File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); } catch (IOException) { return false; } }

    private sealed class Gate : IDisposable
    {
        private readonly string _started, _release;
        public GitRepository Repository { get; }
        public bool HasStarted => System.IO.File.Exists(_started) && new FileInfo(_started).Length > 0;
        public int Pid => int.Parse(System.IO.File.ReadAllText(_started));
        public Gate(Fixture fixture)
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private real Git gates use Linux procfs."); }
            string root = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "gate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            _started = Path.Combine(root, "started"); _release = Path.Combine(root, "release"); string executable = Path.Combine(root, "git");
            System.IO.File.WriteAllText(executable, $$"""
                #!/usr/bin/python3
                import os,sys,time
                from pathlib import Path
                if 'status' in sys.argv[1:]:
                    Path({{JsonSerializer.Serialize(_started)}}).write_text(str(os.getpid()))
                    for _ in range(2000):
                        if Path({{JsonSerializer.Serialize(_release)}}).exists(): break
                        time.sleep(.01)
                    else: sys.exit(74)
                os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[1:]])
                """);
            if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Repository = new(fixture.Repository, new GitRunner(executable));
        }
        public void Release() => System.IO.File.WriteAllText(_release, "release");
        public void Dispose() => Release();
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Pointer(Window window, Control control, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Pump(window); Point point = control.TranslatePoint(new Point(Math.Min(100, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True);
        window.MouseMove(point); window.MouseDown(point, button, modifiers); window.MouseUp(point, button, modifiers); Pump(window);
    }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Ready(MainWindow main)
    {
        await Until(() => !main.IsBusy && main.OwnedWindows.Count == 0 && ((Array)typeof(MainWindow).GetProperty("BrowserReads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!)
            .Cast<object>().All(read => (int)read.GetType().GetField("Pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(read)! == 0)); Pump(main);
    }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static void Log(MainWindow main, string phase)
    { ListBox history = Field<ListBox>(main, "RevisionHistory"); Control? focus = main.FocusManager?.GetFocusedElement() as Control; TestContext.WriteLine($"{phase}: focus={focus?.GetType().Name}/{focus?.Name}, selected={history.SelectedIndex}, historyFocus={history.IsKeyboardFocusWithin}"); }
}
