using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ModelessCommitDiffMutationTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ReadOnlyCloseAndF5KeepBothBrowsesRowsSelectionAndTheLaunchersFirstArrow(bool refreshChild)
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git, secondary: true);
        ListBox history = History(host.Launcher); GitCommit selected = history.Items.OfType<GitCommit>().First();
        PushProcessTestDriver.Pointer((Control)history.ContainerFromItem(selected)!);
        object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray();
        object? foreignSource = History(host.Root).ItemsSource;
        MainWindow child = await host.OpenDiff(selected);
        if (refreshChild) { Press(child, PhysicalKey.F5); await ChildIdle(child); }
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.False);
        GitCommandLog.Clear(); await host.CloseDiff(child);
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
        Assert.That(history.Items.Count, Is.EqualTo(rows.Length));
        Assert.That(history.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        Assert.That(History(host.Root).ItemsSource, Is.SameAs(foreignSource)); AssertNoGlobalReads();
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        GitCommit next = (GitCommit)history.Items[history.Items.IndexOf(selected) + 1]!;
        Press(host.Launcher, PhysicalKey.ArrowDown); await InputIdle(host.Launcher);
        Assert.That(history.SelectedItem, Is.SameAs(next)); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    public async Task ARealFileMenuResetRefreshesOnlyTheSecondaryBrowseThatOpenedTheUnownedDiffOnce()
    {
        using Fixture git = await History(); File.WriteAllText(git.File, "dirty before independent diff\n");
        await using Host host = await Host.Open(git, secondary: true);
        object? source = History(host.Launcher).ItemsSource, otherSource = History(host.Root).ItemsSource;
        MainWindow child = await host.OpenDiff(); Assert.That(child.Owner, Is.Null);
        ResetChangesDialog prompt = await OpenResetPrompt(child); GitCommandLog.Clear();
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "ConfirmResetChanges")); await ChildIdle(child);
        Assert.That(File.ReadAllText(git.File), Is.EqualTo("third\n")); Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
        await ReceiverRefresh(host.Launcher, source);
        Assert.That(child.IsVisible, Is.True, "Publication follows the real mutating operation's drainage; the independent reader need not close.");
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source));
        Assert.That(History(host.Root).ItemsSource, Is.SameAs(otherSource), "The lifetime root is not a second mutation recipient.");
        AssertOneHistoryRefresh(); Assert.That(Private<FileStatusListView>(host.Launcher, "_changesFilesView").Entries, Is.Empty);
        await CloseWithoutAnotherRefresh(host, child);
        Assert.That((await git.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ARealOwnedDialogRetainsMayAfterItsStagedEffectFailsOrIsCancelled(bool cancel)
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        object? source = History(host.Launcher).ItemsSource; MainWindow child = await host.OpenDiff();
        TaskCompletionSource? tail = cancel ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        EffectDialog dialog = new(Repository(child), cancel, tail?.Task); Task modal = ShowDialog(child, dialog);
        try
        {
            await Until(child, () => dialog.IsVisible); GitCommandLog.Clear();
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageThenFail"));
            await Until(child, () => dialog.EffectWritten);
            Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
            if (cancel)
            {
                Press(dialog, PhysicalKey.Escape); await Until(child, () => dialog.AttemptCancellationRequested);
                tail!.TrySetResult(); await dialog.AttemptCompletion; await modal;
                Assert.That(dialog.CancellationWasRequested, Is.True);
            }
            else
            {
                await Until(child, () => !dialog.IsBusy && dialog.LastError is not null);
                PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseEffect")); await modal;
            }
            Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges, Is.True);
            await ReceiverRefresh(host.Launcher, source);
            Assert.That(host.Root.ModelessCommitDiffWindows, Has.Count.EqualTo(1));
            Assert.That(host.Root.WindowWorkCompletion.IsCompleted, Is.False,
                "The live independent child retains the root session lease; this does not mean the receiver's refresh is still running.");
            Assert.That(child.IsVisible, Is.True, "Failure/cancellation still publishes the admitted effect after the owned operation has drained.");
            Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
            Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); AssertOneHistoryRefresh();
            await CloseWithoutAnotherRefresh(host, child);
            Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(EffectDialog.EffectText));
        }
        finally { tail?.TrySetResult(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AReceiptWaitsForTheOwnersActualPriorGitOperationOrDrainsOnOwnerCloseWithoutAGlobalWaitCycle(bool closeOwner)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private owned Git gate needs Linux procfs."); return; }
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        MainWindow child = await host.OpenDiff(); object? source = History(host.Launcher).ItemsSource;
        using GitGate gate = new(git);
        TaskCompletionSource tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectDialog dialog = new(Repository(child), false, tail.Task); Task modal = ShowDialog(child, dialog);
        try
        {
            await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageThenFail"));
            await Until(child, () => dialog.EffectWritten);
            Assert.That(dialog.IsBusy, Is.True); Assert.That(dialog.AttemptCompletion.IsCompleted, Is.False);
            Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
            // Both windows use this same GitRepository. Acquire the real staged
            // effect before the receiver's generic RunAsync command holds its
            // mutation lock; the test tail still keeps the source work admitted.
            Task busy = RunOwner(host.Launcher, async repository =>
            {
                CancellationToken token = Private<CancellationTokenSource>(host.Launcher, "_operation").Token;
                GitResult result = await repository.RunAsync(["-c", "gitextensions.modelessGate=true", "status", "--porcelain"], token);
                Assert.That(result.Success, Is.True, result.StandardError);
            });
            await Until(host.Launcher, () => gate.Started && host.Launcher.IsBusy);
            Task operation = Private<Task>(host.Launcher, "_repositoryOperationCompletion");
            Assert.That(operation.IsCompleted, Is.False);
            GitCommandLog.Clear(); tail.TrySetResult(); await dialog.AttemptCompletion;
            await Until(child, () => !dialog.IsBusy && dialog.LastError is not null);
            Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges, Is.True);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseEffect")); await modal;
            child.Close(); await Until(host.Root, () => !child.IsVisible && child.WindowWorkCompletion.IsCompleted);
            Assert.That(host.Root.ModelessCommitDiffWindows, Has.Count.EqualTo(1), "The Browse focus return remains pending while the shared notification waits its prior operation; Closed did not originate this notification.");
            Assert.That(operation.IsCompleted, Is.False);
            Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
            Assert.That(busy.IsCompleted, Is.False);
            if (closeOwner)
            {
                host.Launcher.Close();
                await Until(host.Root, () => !host.Launcher.IsVisible && host.Launcher.WindowWorkCompletion.IsCompleted);
                await busy; await host.Launcher.WindowWorkCompletion;
                Assert.That(host.Root.ModelessCommitDiffWindows, Is.Empty); Assert.That(gate.Retired, Is.True);
                Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
            }
            else
            {
                gate.Release(); await busy; await host.Drained(child);
                Assert.That(operation.IsCompleted, Is.True); Assert.That(gate.Retired, Is.True);
                Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); AssertOneHistoryRefresh();
            }
            Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True, "Cancellation of UI publication never erases the child's admitted receipt.");
        }
        finally { tail.TrySetResult(); gate.Release(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingTheDiffOrActualRootDrainsItsBusyDescendantAndOnlyALivingReceiverRefreshes(bool closeRoot)
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        MainWindow child = await host.OpenDiff(); object? source = History(host.Launcher).ItemsSource;
        TaskCompletionSource tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectDialog dialog = new(Repository(child), true, tail.Task); Task modal = ShowDialog(child, dialog);
        try
        {
            await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageThenFail"));
            await Until(child, () => dialog.EffectWritten); Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
            GitCommandLog.Clear();
            if (closeRoot) { host.Root.Close(); } else { child.Close(); }
            // This is the real cancellation barrier, not a pump/delay chosen to
            // let a posted Close win a race against the uncooperative tail.
            await Until(host.Root, () => dialog.AttemptCancellationRequested);
            Assert.That(dialog.AttemptCompletion.IsCompleted, Is.False);
            Assert.That(host.Root.WindowWorkCompletion.IsCompleted, Is.False);
            tail.TrySetResult(); await dialog.AttemptCompletion; await modal; await host.Drained(child);
            Assert.That(dialog.CancellationWasRequested, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(dialog.MayHaveRepositoryChanges && child.StandaloneMayHaveRepositoryChanges, Is.True);
            if (closeRoot)
            {
                Assert.That(host.Root.IsVisible, Is.False); Assert.That(History(host.Root).ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
            }
            else { Assert.That(host.Root.IsVisible, Is.True); Assert.That(History(host.Root).ItemsSource, Is.SameAs(source)); AssertOneHistoryRefresh(); }
            Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(EffectDialog.EffectText));
        }
        finally { tail.TrySetResult(); }
    }

    [AvaloniaTest]
    public async Task AClosedSecondaryLauncherDoesNotTransferItsReceiptToTheLifetimeRootWithTheSamePath()
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git, secondary: true);
        MainWindow child = await host.OpenDiff(); object? source = History(host.Root).ItemsSource;
        Assert.That(ReferenceEquals(Repository(host.Root), Repository(host.Launcher)), Is.False);
        host.Launcher.Close(); await Until(host.Root, () => !host.Launcher.IsVisible && host.Launcher.WindowWorkCompletion.IsCompleted);
        Assert.That(child.IsVisible, Is.True); GitCommandLog.Clear(); await StageAndFail(child);
        await host.CloseDiff(child);
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True); Assert.That(History(host.Root).ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ReplacingTheLaunchersRepositoryRejectsTheOldReceiptEvenWhenThePathIsTheSame(bool samePath)
    {
        using Fixture git = await History(); using Fixture other = await Fixture.CreateAsync(); await using Host host = await Host.Open(git);
        MainWindow child = await host.OpenDiff(); GitRepository original = Repository(host.Launcher);
        await host.Launcher.OpenRepositoryAsync(samePath ? git.Repository.Path : other.Repository.Path); await InputIdle(host.Launcher);
        Assert.That(Repository(host.Launcher), Is.Not.SameAs(original));
        object? source = History(host.Launcher).ItemsSource; object? selected = History(host.Launcher).SelectedItem;
        GitCommandLog.Clear(); await StageAndFail(child); await host.CloseDiff(child);
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); Assert.That(History(host.Launcher).SelectedItem, Is.SameAs(selected)); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    public async Task CancellingAnUntouchedOwnedDialogDoesNotTurnReadOnlyCloseIntoARefresh()
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git); MainWindow child = await host.OpenDiff();
        object? source = History(host.Root).ItemsSource;
        EffectDialog dialog = new(Repository(child), false); Task modal = ShowDialog(child, dialog);
        await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseEffect")); await modal;
        Assert.That(dialog.MayHaveRepositoryChanges, Is.False); Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.False);
        GitCommandLog.Clear(); await host.CloseDiff(child); Assert.That(History(host.Root).ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
    }

    private static async Task ReceiverRefresh(MainWindow receiver, object? previousSource)
    {
        // A root Browse also owns every independent CommitDiff session lease.
        // Wait for this actual repository operation, then drain the full root
        // lifetime only after the child closes in Host.Drained.
        await Until(receiver, () => GitCommandLog.Snapshot().Any(entry => entry.Command == "git log")
            && !receiver.IsBusy && Private<Task>(receiver, "_repositoryOperationCompletion").IsCompleted);
        await Private<Task>(receiver, "_repositoryOperationCompletion");
        Assert.That(History(receiver).ItemsSource, Is.SameAs(previousSource));
    }
    private static async Task CloseWithoutAnotherRefresh(Host host, MainWindow child)
    {
        object? source = History(host.Launcher).ItemsSource; GitCommandLog.Clear();
        await host.CloseDiff(child);
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source), "A close with no new mutation cannot replay the already-published receipt.");
        AssertNoGlobalReads();
    }

    private static async Task StageAndFail(MainWindow child)
    {
        EffectDialog dialog = new(Repository(child), false); Task modal = ShowDialog(child, dialog);
        await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageThenFail"));
        await Until(child, () => dialog.EffectWritten && !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseEffect")); await modal;
    }

    private sealed class EffectDialog : RepositoryDialog
    {
        internal const string EffectText = "partial independent diff effect\n";
        private readonly bool _cancel; private readonly Task? _tail; private CancellationToken _attemptToken;
        internal bool EffectWritten { get; private set; }
        internal bool AttemptCancellationRequested => _attemptToken.IsCancellationRequested;
        internal bool CancellationWasRequested { get; private set; }
        internal Task AttemptCompletion { get; private set; } = Task.CompletedTask;
        internal EffectDialog(GitRepository repository, bool cancel, Task? tail = null) : base(repository, "Private modeless mutation fixture")
        {
            _cancel = cancel; _tail = tail; Width = 460; Height = 220;
            Button mutate = Action("StageThenFail", "Stage then fail", () => AttemptCompletion = Execute());
            Button close = Action("CloseEffect", "Cancel", () => { Close(); return Task.CompletedTask; });
            Root.Children.Add(new StackPanel { Children = { mutate, close } });
        }
        public override Task InitializeAsync() => Task.CompletedTask;
        private Task Execute() => RunAsync("Private staged effect", async token =>
        {
            _attemptToken = token;
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), EffectText, token);
            await Repository.StageAllAsync(token); EffectWritten = true;
            if (_tail is not null) { await _tail.WaitAsync(TimeSpan.FromSeconds(20)); }
            CancellationWasRequested = token.IsCancellationRequested;
            if (_cancel) { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("Expected the actual owned cancellation after the effect."); }
            throw new IOException("Private failure after an admitted effect.");
        }, changes: true);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ClassicDesktopStyleApplicationLifetime _lifetime = new();
        internal MainWindow Root { get; private set; } = null!;
        internal MainWindow Launcher { get; private set; } = null!;
        internal static async Task<Host> Open(Fixture git, bool secondary = false)
        {
            Host host = new();
            try
            {
                host.Root = new(new BrowseRequest(git.Repository.Path)) { ModelessApplicationLifetime = host._lifetime, Width = 1440, Height = 960 };
                host._lifetime.MainWindow = host.Root; host.Root.Show(); await ChildIdle(host.Root);
                if (secondary)
                {
                    host.Launcher = new(new BrowseRequest(git.Repository.Path)) { ModelessApplicationLifetime = host._lifetime, Width = 1440, Height = 960 };
                    host.Launcher.Show(); await ChildIdle(host.Launcher);
                }
                else { host.Launcher = host.Root; }
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        internal async Task<MainWindow> OpenDiff(GitCommit? commit = null)
        {
            commit ??= History(Launcher).Items.OfType<GitCommit>().First();
            await Launcher.ShowCommitDiffAsync(commit); await Until(Root, () => Root.ModelessCommitDiffWindows.Count == 1);
            MainWindow child = Root.ModelessCommitDiffWindows.Single(); await ChildIdle(child); await InputIdle(Launcher);
            return child;
        }
        internal async Task CloseDiff(MainWindow child) { Press(child, PhysicalKey.Escape); await Drained(child); }
        internal async Task Drained(MainWindow child)
        {
            await Until(Root, () => !child.IsVisible && child.WindowWorkCompletion.IsCompleted && Root.ModelessCommitDiffWindows.Count == 0);
            await child.WindowWorkCompletion; await Root.WindowWorkCompletion;
            if (Launcher.IsVisible) { await Launcher.WindowWorkCompletion; await InputIdle(Launcher); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Launcher is not null && !ReferenceEquals(Launcher, Root) && Launcher.IsVisible) { Launcher.Close(); }
            if (Root is not null) { Root.Close(); await Until(Root, () => !Root.IsVisible && Root.WindowWorkCompletion.IsCompleted); await Root.WindowWorkCompletion; }
            if (Launcher is not null) { await Launcher.WindowWorkCompletion; }
            _lifetime.Dispose();
        }
    }

    private sealed class GitGate : IDisposable
    {
        private readonly GitEnvironmentOptions _previous = GitEnvironment.Options;
        private readonly string _marker, _release; private (int Pid, string Start)? _identity;
        internal GitGate(Fixture git)
        {
            _marker = git.Output("modeless-owner-git-started.json"); _release = git.Output("modeless-owner-git-release");
            string script = git.Output("modeless-owner-git-gate.py");
            File.WriteAllText(script, $$"""
                #!/usr/bin/python3
                import json,os,sys,time
                from pathlib import Path
                if 'gitextensions.modelessGate=true' in sys.argv[1:]:
                    marker=Path({{JsonSerializer.Serialize(_marker)}})
                    stat=Path('/proc/self/stat').read_text().rsplit(') ',1)[1].split()
                    pending=marker.with_suffix('.pending')
                    pending.write_text(json.dumps({'pid':os.getpid(),'start_ticks':stat[19]}))
                    os.replace(pending,marker)
                    for _ in range(6000):
                        if Path({{JsonSerializer.Serialize(_release)}}).exists(): break
                        time.sleep(.01)
                    else: sys.exit(74)
                os.execv('/usr/bin/git',['git',*sys.argv[1:]])
                """);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            GitEnvironment.Configure(_previous with { Executable = script });
        }
        internal bool Started => File.Exists(_marker);
        internal bool Retired
        {
            get
            {
                if (_identity is null)
                {
                    if (!Started) { return false; }
                    using JsonDocument marker = JsonDocument.Parse(File.ReadAllBytes(_marker));
                    _identity = (marker.RootElement.GetProperty("pid").GetInt32(), marker.RootElement.GetProperty("start_ticks").GetString()!);
                }
                try
                {
                    string stat = File.ReadAllText("/proc/" + _identity.Value.Pid.ToString(CultureInfo.InvariantCulture) + "/stat");
                    return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] != _identity.Value.Start;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
                // A procfs task may vanish after open; .NET Unix exposes ESRCH as raw errno 3.
                catch (IOException error) when (OperatingSystem.IsLinux() && error.HResult == 3) { return true; }
            }
        }
        internal void Release() => File.WriteAllText(_release, "release");
        public void Dispose() { Release(); GitEnvironment.Configure(_previous); }
    }

    private static async Task<ResetChangesDialog> OpenResetPrompt(MainWindow child)
    {
        FileStatusListView files = Field<FileStatusListView>(child, "RevisionFileStatusList");
        object tag = files.Entries.Single(entry => entry.Path == "file.txt").Tag; TreeViewItem item = files.FindItemByTag(tag)!;
        Control header = item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == item);
        header.BringIntoView(); PortableHelperSettingsFixture.Pump(child); PushProcessTestDriver.Pointer(header); await ChildIdle(child);
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), child)!.Value;
        child.MouseMove(point); child.MouseDown(point, MouseButton.Right); child.MouseUp(point, MouseButton.Right); PortableHelperSettingsFixture.Pump(child);
        ContextMenu menu = files.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
        MenuItem reset = menu.Items.OfType<MenuItem>().Single(control => control.Name == "tsmiResetFileTo");
        TopLevel popup = TopLevel.GetTopLevel(reset)!;
        popup.MouseMove(reset.TranslatePoint(new Point(reset.Bounds.Width * .7, reset.Bounds.Height / 2), popup)!.Value);
        await Until(child, () => reset.IsSubMenuOpen && reset.Items.OfType<MenuItem>().First().Bounds.Height > 0);
        PushProcessTestDriver.Pointer(reset.Items.OfType<MenuItem>().Single(control => control.Name == "tsmiResetFileToSelected"));
        await Until(child, () => child.OwnedWindows.OfType<ResetChangesDialog>().Any(dialog => dialog.IsVisible));
        return child.OwnedWindows.OfType<ResetChangesDialog>().Single(dialog => dialog.IsVisible);
    }
    private static async Task<Fixture> History()
    { Fixture git = await Fixture.CreateAsync(); try { await git.ChangeAsync("second\n", "Second"); await git.ChangeAsync("third\n", "Third"); return git; } catch { git.Dispose(); throw; } }
    private static ListBox History(MainWindow main) => Field<ListBox>(main, "RevisionHistory");
    private static GitRepository Repository(MainWindow main) => Private<GitRepository>(main, "_repository");
    private static T Private<T>(MainWindow main, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static Task ShowDialog(MainWindow child, RepositoryDialog dialog) => (Task)typeof(MainWindow)
        .GetMethod("ShowRepositoryDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(child, [dialog, true])!;
    private static Task RunOwner(MainWindow owner, Func<GitRepository, Task> action) => (Task)typeof(MainWindow)
        .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, ["Private independent owner Git gate", action, false, true])!;
    private static Task Until(MainWindow main, Func<bool> condition) => PushProcessTestDriver.UntilAsync(main, condition);
    private static async Task ChildIdle(MainWindow child) { await Until(child, () => !child.IsBusy && child.WindowWorkCompletion.IsCompleted); await child.WindowWorkCompletion; Assert.That(child.LastException, Is.Null, child.LastException?.ToString()); }
    private static Task InputIdle(MainWindow owner) => Until(owner, () => !owner.IsBusy);
    private static void Press(Window window, PhysicalKey key) => PortableHelperSettingsFixture.Press(window, key);
    private static void AssertNoGlobalReads() => Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
    private static void AssertOneHistoryRefresh() => Assert.That(GitCommandLog.Snapshot().Count(entry => entry.Command == "git log"), Is.EqualTo(1));
}
