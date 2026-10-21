using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionLogCloseRefreshTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingAnActuallyReadOnlyLogKeepsParentRowsSelectionAndFirstArrowWithoutGlobalHistoryOrStatusReads(bool refreshLog)
    {
        using Fixture git = await History(); await using Host f = await Host.Open(git);
        object source = f.History.ItemsSource!; GitCommit selected = f.History.Items.OfType<GitCommit>().Skip(1).First();
        PushProcessTestDriver.Pointer((Control)f.History.ContainerFromItem(selected)!); await Idle(f.Owner);
        Control row = (Control)f.History.ContainerFromItem(selected)!;
        MainWindow log = await f.OpenLog();
        if (refreshLog) { Press(log, PhysicalKey.F5); await Idle(log); }
        Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.False, "F5 is a read, not a repository mutation attempt.");
        GitCommandLog.Clear(); await f.CloseLog(log);
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(f.History.SelectedItem, Is.SameAs(selected));
        Assert.That(f.History.ContainerFromItem(selected), Is.SameAs(row)); Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        AssertNoGlobalReads();
        int index = f.History.Items.IndexOf(selected); object next = f.History.Items[index + 1]!;
        Press(f.Owner, PhysicalKey.ArrowDown); await Idle(f.Owner);
        Assert.That(f.History.SelectedItem, Is.SameAs(next)); Assert.That(f.History.IsKeyboardFocusWithin, Is.True); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    public async Task ARealFileMenuResetMarksTheLogAndRefreshesTheParentAfterItCloses()
    {
        using Fixture git = await History(); File.WriteAllText(git.File, "dirty worktree before Log\n"); await using Host f = await Host.Open(git);
        object source = f.History.ItemsSource!; MainWindow log = await f.OpenLog();
        FileStatusListView files = Field<FileStatusListView>(log, "RevisionFileStatusList");
        object tag = files.Entries.Single(entry => entry.Path == "file.txt").Tag;
        TreeViewItem item = files.FindItemByTag(tag)!; Control header = Header(item); header.BringIntoView(); Pump(log);
        PushProcessTestDriver.Pointer(header); await Idle(log);
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), log)!.Value;
        log.MouseMove(point); log.MouseDown(point, MouseButton.Right); log.MouseUp(point, MouseButton.Right); Pump(log);
        ContextMenu menu = files.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
        MenuItem reset = menu.Items.OfType<MenuItem>().Single(menuItem => menuItem.Name == "tsmiResetFileTo");
        TopLevel popup = TopLevel.GetTopLevel(reset)!;
        popup.MouseMove(reset.TranslatePoint(new Point(reset.Bounds.Width * .7, reset.Bounds.Height / 2), popup)!.Value);
        await PushProcessTestDriver.UntilAsync(log, () => reset.IsSubMenuOpen && reset.Items.OfType<MenuItem>().First().Bounds.Height > 0);
        PushProcessTestDriver.Pointer(reset.Items.OfType<MenuItem>().Single(menuItem => menuItem.Name == "tsmiResetFileToSelected"));
        await PushProcessTestDriver.UntilAsync(log, () => log.OwnedWindows.OfType<ResetChangesDialog>().Any(window => window.IsVisible));
        ResetChangesDialog question = log.OwnedWindows.OfType<ResetChangesDialog>().Single(window => window.IsVisible);
        PushProcessTestDriver.Pointer(Field<Button>(question, "ConfirmResetChanges")); await Idle(log);
        Assert.That(File.ReadAllText(git.File), Is.EqualTo("third\n")); Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.True);
        GitCommandLog.Clear(); await f.CloseLog(log);
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Contain("git log"));
        Assert.That((await git.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(PrivateField<FileStatusListView>(f.Owner, "_changesFilesView").Entries, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ARealRepositoryDialogFailureOrCancellationAfterStagingKeepsItsPossibleEffectsThroughCancelAndLogClose(bool cancel)
    {
        using Fixture git = await History(); await using Host f = await Host.Open(git); object source = f.History.ItemsSource!;
        MainWindow log = await f.OpenLog();
        TaskCompletionSource? release = cancel ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        EffectDialog dialog = new(Repository(log), cancel, release?.Task); Task modal = ShowRepositoryDialog(log, dialog);
        try
        {
            await PushProcessTestDriver.UntilAsync(log, () => dialog.IsVisible);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "WriteThenFail"));
            await PushProcessTestDriver.UntilAsync(dialog, () => dialog.EffectWritten);
            Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.True, "The owned mutation signal is immediate, before the dialog closes.");
            if (cancel)
            {
                Press(dialog, PhysicalKey.Escape); // Real Closing requests cancellation of the actual operation token.
                release!.TrySetResult(); await dialog.AttemptCompletion; await modal;
                Assert.That(dialog.IsVisible, Is.False);
            }
            else
            {
                await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && dialog.LastError is not null);
                await dialog.AttemptCompletion;
                PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseProbe")); await modal;
            }
            Assert.That(dialog.HasRepositoryChanges, Is.False, "The historical flag still records successful completions, not attempts.");
            Assert.That(dialog.MayHaveRepositoryChanges, Is.True); Assert.That(File.ReadAllText(git.File), Is.EqualTo("partial effect before failure\n"));
        }
        finally { release?.TrySetResult(); }
        Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.True);
        GitCommandLog.Clear(); await f.CloseLog(log);
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Contain("git log"));
        Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("partial effect before failure\n"));
        Assert.That((await git.Repository.GetStatusAsync()).Changes.Any(change => change.Path == "file.txt"), Is.True);
    }

    [AvaloniaTest]
    public async Task CancellingAnUntouchedRepositoryDialogInsideLogDoesNotTurnReadingIntoAMutation()
    {
        using Fixture git = await History(); await using Host f = await Host.Open(git); object source = f.History.ItemsSource!;
        MainWindow log = await f.OpenLog(); EffectDialog dialog = new(Repository(log), false); Task modal = ShowRepositoryDialog(log, dialog);
        await PushProcessTestDriver.UntilAsync(log, () => dialog.IsVisible);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseProbe")); await modal;
        Assert.That(dialog.MayHaveRepositoryChanges, Is.False); Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.False);
        GitCommandLog.Clear(); await f.CloseLog(log); Assert.That(f.History.ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
        Assert.That((await git.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task OwnerClosingDrainsAPartialEffectAndRetainsItsSignalWithoutPublishingAParentRefresh()
    {
        using Fixture git = await History(); await using Host f = await Host.Open(git); object source = f.History.ItemsSource!;
        MainWindow log = await f.OpenLog(); TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectDialog dialog = new(Repository(log), true, release.Task);
        List<string> closingTrace = [];
        TraceClose("Browse", f.Owner, closingTrace); TraceClose("Log", log, closingTrace); TraceClose("Effect", dialog, closingTrace);
        Task modal = ShowRepositoryDialog(log, dialog);
        try
        {
            await PushProcessTestDriver.UntilAsync(log, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "WriteThenFail"));
            await PushProcessTestDriver.UntilAsync(dialog, () => dialog.EffectWritten);
            GitCommandLog.Clear(); f.Owner.Close();
            Assert.That(f.Owner.WindowWorkCompletion.IsCompleted, Is.False, "The genuine modal mutation must be drained, including an uncooperative tail.");
            Assert.That(dialog.AttemptCancellationRequested, Is.True, "The UI owner-close must synchronously seal the pending operation before its tail is released.");
            release.TrySetResult(); await dialog.AttemptCompletion; await modal;
            Assert.That(dialog.CancellationWasRequested, Is.True, "The first real owner-close request must reach the operation token.");
            await PushProcessTestDriver.UntilAsync(f.Owner, () => !f.Owner.IsVisible && f.Owner.WindowWorkCompletion.IsCompleted,
                "The original owner-close request must resume after the real descendant operation drains.",
                () => Task.FromResult(string.Join("\n", closingTrace.Append("Final Browse " + CloseState(f.Owner)).Append("Final Log " + CloseState(log)).Append("Final Effect " + CloseState(dialog)))));
            Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.True); Assert.That(f.History.ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
            Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("partial effect before failure\n"));
        }
        finally
        {
            release.TrySetResult();
            TestContext.Progress.WriteLine(string.Join("\n", closingTrace));
        }
    }

    [AvaloniaTest]
    public async Task ClosingOnlyTheLogDuringAPartialEffectDrainsAndRefreshesWithoutClosingBrowse()
    {
        using Fixture git = await History(); await using Host f = await Host.Open(git);
        object source = f.History.ItemsSource!; WindowClosingBehavior originalPolicy = f.Owner.ClosingBehavior;
        MainWindow log = await f.OpenLog(); TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectDialog dialog = new(Repository(log), true, release.Task); Task modal = ShowRepositoryDialog(log, dialog);
        try
        {
            await PushProcessTestDriver.UntilAsync(log, () => dialog.IsVisible);
            Assert.That(f.Owner.ClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerWindowOnly));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "WriteThenFail"));
            await PushProcessTestDriver.UntilAsync(dialog, () => dialog.EffectWritten);
            log.Close(); // The user closes Log only, never requests Browse to close.
            Assert.That(f.Owner.IsVisible, Is.True); Assert.That(log.WindowWorkCompletion.IsCompleted, Is.False);
            Assert.That(dialog.AttemptCancellationRequested, Is.True, "Closing only Log also seals the actual pending child token without closing Browse.");
            release.TrySetResult(); await dialog.AttemptCompletion; await modal;
            Assert.That(dialog.CancellationWasRequested, Is.True);
            await f.WaitForLogClose(log); // Observe the first close; do not send another one.
            Assert.That(f.Owner.IsVisible, Is.True); Assert.That(f.Owner.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
            Assert.That(f.Owner.ClosingBehavior, Is.EqualTo(originalPolicy));
            Assert.That(log.StandaloneMayHaveRepositoryChanges, Is.True);
            Assert.That(f.History.ItemsSource, Is.SameAs(source), "A live owner publishes the refreshed snapshot in the same history source after the Log's acquired partial effect.");
            Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("partial effect before failure\n"));
        }
        finally { release.TrySetResult(); }
    }

    private sealed class EffectDialog : RepositoryDialog
    {
        private readonly bool _cancel; private readonly Task? _tail;
        private CancellationToken _attemptCancellationToken;
        internal bool AttemptCancellationRequested => _attemptCancellationToken.IsCancellationRequested;
        internal bool EffectWritten { get; private set; }
        internal bool CancellationWasRequested { get; private set; }
        internal Task AttemptCompletion { get; private set; } = Task.CompletedTask;
        internal EffectDialog(GitRepository repository, bool cancel, Task? tail = null) : base(repository, "Private partial-effect fixture")
        {
            _cancel = cancel; _tail = tail; Width = 460; Height = 220;
            Button mutate = Action("WriteThenFail", "Write then fail", () => AttemptCompletion = Execute());
            Button close = Action("CloseProbe", "Cancel", () => { Close(); return Task.CompletedTask; });
            Root.Children.Add(new StackPanel { Children = { mutate, close } });
        }
        public override Task InitializeAsync() => Task.CompletedTask;
        private Task Execute() => RunAsync("Private staged effect", async token =>
        {
            _attemptCancellationToken = token; // Observe the real token; never alter its source.
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), "partial effect before failure\n", token);
            await Repository.StageAllAsync(token); EffectWritten = true;
            if (_tail is not null) { await _tail.WaitAsync(TimeSpan.FromSeconds(20)); }
            CancellationWasRequested = token.IsCancellationRequested;
            if (_cancel)
            { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("The fixture expected actual owned cancellation after its effect."); }
            throw new IOException("Private failure after a real effect.");
        }, changes: true);
    }

    private sealed class Host(MainWindow owner) : IAsyncDisposable
    {
        internal MainWindow Owner => owner;
        internal ListBox History => Field<ListBox>(owner, "RevisionHistory");
        private Task _pending = Task.CompletedTask;
        internal static async Task<Host> Open(Fixture git)
        {
            MainWindow owner = new() { Width = 1440, Height = 960 }; owner.Show(); await owner.OpenRepositoryAsync(git.Repository.Path); await Idle(owner);
            return new(owner);
        }
        internal async Task<MainWindow> OpenLog()
        {
            _pending = owner.ShowRevisionLogAsync();
            await PushProcessTestDriver.UntilAsync(owner, () => owner.OwnedWindows.OfType<MainWindow>().Any(window => window.IsVisible && window.WindowKind == MainWindowKind.RevisionLog));
            MainWindow log = owner.OwnedWindows.OfType<MainWindow>().Single(window => window.IsVisible && window.WindowKind == MainWindowKind.RevisionLog);
            await Idle(log); return log;
        }
        internal async Task CloseLog(MainWindow log)
        {
            Press(log, PhysicalKey.Escape); await WaitForLogClose(log);
        }
        internal async Task WaitForLogClose(MainWindow log)
        {
            await PushProcessTestDriver.UntilAsync(owner, () => !log.IsVisible && _pending.IsCompleted);
            await _pending; await log.WindowWorkCompletion; await Idle(owner);
        }
        public async ValueTask DisposeAsync()
        {
            if (owner.IsVisible) { owner.Close(); }
            await PushProcessTestDriver.UntilAsync(owner, () => !owner.IsVisible && owner.WindowWorkCompletion.IsCompleted);
            await owner.WindowWorkCompletion; await _pending;
        }
    }
    private static async Task<Fixture> History()
    {
        Fixture git = await Fixture.CreateAsync();
        try { await git.ChangeAsync("second\n", "Second"); await git.ChangeAsync("third\n", "Third"); return git; }
        catch { git.Dispose(); throw; }
    }
    // OpenRepositoryAsync constructs its own instance. Reflection observes the
    // actual host repository; no state, selection or publication guard is injected.
    private static GitRepository Repository(MainWindow log) => PrivateField<GitRepository>(log, "_repository");
    private static T PrivateField<T>(MainWindow owner, string name) where T : class
        => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void TraceClose(string label, Window window, List<string> trace)
    {
        window.Closing += (_, args) => trace.Add($"{Environment.TickCount64} {label} Closing reason={args.CloseReason} programmatic={args.IsProgrammatic} cancelled={args.Cancel} {CloseState(window)}");
        window.Closed += (_, _) => trace.Add($"{Environment.TickCount64} {label} Closed {CloseState(window)}");
    }
    private static string CloseState(Window window)
    {
        string state = $"visible={window.IsVisible} enabled={window.IsEffectivelyEnabled} owner={window.Owner?.Name}";
        if (window is MainWindow main)
        {
            object? Read(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
            state += $" busy={main.IsBusy} workDone={main.WindowWorkCompletion.IsCompleted} closing={Read("_commitInfoClosing")} ready={Read("_commitInfoCloseReady")} lifetimeCancelled={((CancellationTokenSource)Read("_lifetime")!).IsCancellationRequested} may={main.StandaloneMayHaveRepositoryChanges}";
        }
        else if (window is RepositoryDialog dialog)
        { state += $" busy={dialog.IsBusy} may={dialog.MayHaveRepositoryChanges} error={dialog.LastError}"; }
        return state;
    }
    private static Task ShowRepositoryDialog(MainWindow log, RepositoryDialog dialog)
        => (Task)typeof(MainWindow).GetMethod("ShowRepositoryDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(log, [dialog, true])!;
    private static Task Idle(MainWindow main) => PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void AssertNoGlobalReads() => Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
    private static Control Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
    private static void Pump(TopLevel root) => PortableHelperSettingsFixture.Pump(root);
    private static void Press(Window root, PhysicalKey key) => PortableHelperSettingsFixture.Press(root, key);
}
