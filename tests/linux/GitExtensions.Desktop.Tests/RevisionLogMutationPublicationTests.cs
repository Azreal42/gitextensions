using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionLogMutationPublicationTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ReadOnlyIndependentDiffAndF5DoNotReloadLogOrBrowseAndTheFirstLogArrowStillWorks(bool refreshChild)
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        object? browseSource = History(host.Launcher).ItemsSource;
        await host.OpenLog(); ListBox history = History(host.Log);
        object? source = history.ItemsSource; object[] rows = history.Items.Cast<object>().ToArray();
        MainWindow child = await host.OpenDiff(); object? selected = history.SelectedItem;
        if (refreshChild) { Press(child, PhysicalKey.F5); await SourceIdle(child); }
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.False);
        GitCommandLog.Clear(); await host.CloseDiff(child);
        AssertUnchangedRows(history, source, rows); Assert.That(history.SelectedItem, Is.SameAs(selected));
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(browseSource)); AssertNoGlobalReads();
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        object next = history.Items[history.Items.IndexOf(selected!) + 1]!;
        Press(host.Log, PhysicalKey.ArrowDown); await SourceIdle(host.Log);
        Assert.That(history.SelectedItem, Is.SameAs(next)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        AssertUnchangedRows(history, source, rows); AssertNoGlobalReads();
        await host.CloseLog(); await host.Root.WindowWorkCompletion;
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(browseSource)); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ARealChildResetAndFurtherDrainedEffectsLeaveLogRowsIntactAndCoalesceOnceAtTheModalReturn(bool secondEffect)
    {
        using Fixture git = await History(); await File.WriteAllTextAsync(git.File, "dirty before Log child reset\n");
        await using Host host = await Host.Open(git); object? browseSource = History(host.Launcher).ItemsSource;
        await host.OpenLog(); object? logSource = History(host.Log).ItemsSource; object[] rows = History(host.Log).Items.Cast<object>().ToArray();
        MainWindow child = await host.OpenDiff(); object? selected = History(host.Log).SelectedItem;
        ResetChangesDialog prompt = await OpenResetPrompt(child); GitCommandLog.Clear();
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "ConfirmResetChanges")); await SourceIdle(child);
        Assert.That(File.ReadAllText(git.File), Is.EqualTo("third\n"));
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
        if (secondEffect) { await StageAndFail(child); }
        Assert.That(child.IsVisible, Is.True); Assert.That(host.Log.IsVisible, Is.True);
        AssertUnchangedRows(History(host.Log), logSource, rows); Assert.That(History(host.Log).SelectedItem, Is.SameAs(selected));
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(browseSource)); AssertNoHistoryReads();
        GitCommandLog.Clear(); await host.CloseLog(); await ReceiverRefresh(host.Launcher, browseSource);
        AssertIndependentSessionIsStillAdmitted(host, child);
        Assert.That(child.IsVisible, Is.True, "The notification is released by the modal Log return, without closing the independent child.");
        AssertUnchangedRows(History(host.Log), logSource, rows); AssertOneHistoryRead();
        Assert.That(Private<FileStatusListView>(host.Launcher, "_changesFilesView").Entries.Any(entry => entry.Path == "file.txt"), Is.EqualTo(secondEffect));
        Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(secondEffect ? EffectDialog.FinalText : "third\n"));
        await CloseWithoutAnotherRefresh(host, child);
    }

    [AvaloniaTest]
    public async Task AnEffectAdmittedAfterLogClosedStillNotifiesItsExactLivingBrowseWhileTheChildStaysOpen()
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        object? browseSource = History(host.Launcher).ItemsSource;
        await host.OpenLog(); MainWindow child = await host.OpenDiff(); MainWindow oldLog = host.Log;
        await host.CloseLog(); Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(browseSource));
        Assert.That(child.IsVisible, Is.True); Assert.That(oldLog.IsVisible, Is.False);
        GitCommandLog.Clear(); await StageAndFail(child); await ReceiverRefresh(host.Launcher, browseSource);
        AssertIndependentSessionIsStillAdmitted(host, child);
        Assert.That(child.IsVisible, Is.True); Assert.That(child.Owner, Is.Null);
        Assert.That(oldLog.IsVisible, Is.False); Assert.That(host.Launcher.OwnedWindows, Is.Empty, "No replacement Log is invented to deliver a late notification.");
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True); AssertOneHistoryRead();
        Assert.That(Private<FileStatusListView>(host.Launcher, "_changesFilesView").Entries.Any(entry => entry.Path == "file.txt"), Is.True);
        Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(EffectDialog.FinalText));
        await CloseWithoutAnotherRefresh(host, child);
    }

    [AvaloniaTest]
    public async Task ReturningFromLogDuringAPartialEffectWaitsForTheActualSourceDrainBeforePublishingTheFinalIndex()
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        object? browseSource = History(host.Launcher).ItemsSource;
        await host.OpenLog(); MainWindow child = await host.OpenDiff();
        TaskCompletionSource tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectDialog dialog = new(Repository(child), tail.Task); Task modal = ShowDialog(child, dialog);
        try
        {
            await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "WriteLogChildEffect"));
            Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True, "May is synchronous at admission, before the native work yields.");
            await Until(child, () => dialog.PartialEffectWritten);
            Assert.That(dialog.AttemptCompletion.IsCompleted, Is.False); Assert.That(dialog.HasRepositoryChanges, Is.False);
            GitCommandLog.Clear(); await host.CloseLog();
            Assert.That(child.IsVisible && dialog.IsVisible && dialog.IsBusy, Is.True);
            Assert.That(child.WindowWorkCompletion.IsCompleted, Is.False);
            Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(browseSource)); AssertNoGlobalReads();
            tail.TrySetResult(); await dialog.AttemptCompletion;
            await Until(child, () => !dialog.IsBusy && dialog.LastError is not null);
            Assert.That(dialog.FinalEffectWritten, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges, Is.True);
            Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(browseSource), "The owned dialog/session still belongs to the source's real work frontier.");
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseLogChildEffect")); await modal; await SourceIdle(child);
            await ReceiverRefresh(host.Launcher, browseSource);
            AssertIndependentSessionIsStillAdmitted(host, child);
            Assert.That(child.IsVisible, Is.True); AssertOneHistoryRead();
            Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(EffectDialog.FinalText));
            Assert.That(Private<FileStatusListView>(host.Launcher, "_changesFilesView").Entries.Any(entry => entry.Path == "file.txt"), Is.True);
            await CloseWithoutAnotherRefresh(host, child);
        }
        finally { tail.TrySetResult(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ASharedContextNeverRebindsToAReplacementBrowseRepositoryEvenAtTheSamePath(bool samePath)
    {
        using Fixture git = await History(); using Fixture other = await History(); await using Host host = await Host.Open(git);
        await host.OpenLog(); MainWindow child = await host.OpenDiff(); GitRepository original = Repository(host.Launcher);
        await host.CloseLog(); await host.Launcher.OpenRepositoryAsync(samePath ? git.Repository.Path : other.Repository.Path);
        await ReceiverOperationIdle(host.Launcher); Assert.That(Repository(host.Launcher), Is.Not.SameAs(original));
        object? source = History(host.Launcher).ItemsSource, selected = History(host.Launcher).SelectedItem;
        GitCommandLog.Clear(); await StageAndFail(child); await host.CloseDiff(child); await host.Root.WindowWorkCompletion;
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); Assert.That(History(host.Launcher).SelectedItem, Is.SameAs(selected)); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    public async Task ClosingTheActualSecondaryReceiverDoesNotRedirectItsLateChildEffectToTheLifetimeRoot()
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git, secondary: true);
        object? rootSource = History(host.Root).ItemsSource;
        await host.OpenLog(); MainWindow child = await host.OpenDiff(); await host.CloseLog();
        Assert.That(ReferenceEquals(Repository(host.Root), Repository(host.Launcher)), Is.False);
        host.Launcher.Close(); await Until(host.Root, () => !host.Launcher.IsVisible && host.Launcher.WindowWorkCompletion.IsCompleted);
        await host.Launcher.WindowWorkCompletion; Assert.That(child.IsVisible, Is.True);
        GitCommandLog.Clear(); await StageAndFail(child); await host.CloseDiff(child); await host.Root.WindowWorkCompletion;
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True); Assert.That(History(host.Root).ItemsSource, Is.SameAs(rootSource)); AssertNoGlobalReads();
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingTheLifetimeRootDrainsThePartialIndependentEffectWithoutAParentLogWatcherCycle(bool closeLogFirst)
    {
        using Fixture git = await History(); await using Host host = await Host.Open(git);
        object? browseSource = History(host.Root).ItemsSource;
        await host.OpenLog(); MainWindow child = await host.OpenDiff();
        TaskCompletionSource tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectDialog dialog = new(Repository(child), tail.Task); Task modal = ShowDialog(child, dialog);
        try
        {
            await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "WriteLogChildEffect"));
            await Until(child, () => dialog.PartialEffectWritten); Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
            if (closeLogFirst) { await host.CloseLog(); }
            GitCommandLog.Clear(); host.Root.Close(); await Until(host.Root, () => dialog.AttemptCancellationRequested);
            Assert.That(dialog.AttemptCompletion.IsCompleted, Is.False); Assert.That(host.Root.WindowWorkCompletion.IsCompleted, Is.False);
            tail.TrySetResult(); await dialog.AttemptCompletion; await modal;
            await Until(host.Root, () => !host.Root.IsVisible && !host.Log.IsVisible && !child.IsVisible && host.Root.WindowWorkCompletion.IsCompleted);
            await host.Root.WindowWorkCompletion; await host.LogSession; await child.WindowWorkCompletion;
            Assert.That(host.Root.ModelessCommitDiffWindows, Is.Empty); Assert.That(dialog.CancellationWasRequested, Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges && child.StandaloneMayHaveRepositoryChanges, Is.True);
            Assert.That(dialog.FinalEffectWritten, Is.False); Assert.That(History(host.Root).ItemsSource, Is.SameAs(browseSource)); AssertNoGlobalReads();
            Assert.That((await git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(EffectDialog.PartialText));
        }
        finally { tail.TrySetResult(); }
    }

    private static async Task StageAndFail(MainWindow child)
    {
        EffectDialog dialog = new(Repository(child)); Task modal = ShowDialog(child, dialog);
        await Until(child, () => dialog.IsVisible); PushProcessTestDriver.Pointer(Field<Button>(dialog, "WriteLogChildEffect"));
        Assert.That(child.StandaloneMayHaveRepositoryChanges, Is.True);
        await Until(child, () => dialog.FinalEffectWritten && !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CloseLogChildEffect")); await modal; await SourceIdle(child);
    }

    private sealed class EffectDialog : RepositoryDialog
    {
        internal const string PartialText = "partial effect before Log returns\n";
        internal const string FinalText = "final effect after Log returns\n";
        private readonly Task? _tail; private CancellationToken _attemptToken;
        internal bool PartialEffectWritten { get; private set; }
        internal bool FinalEffectWritten { get; private set; }
        internal bool AttemptCancellationRequested => _attemptToken.IsCancellationRequested;
        internal bool CancellationWasRequested { get; private set; }
        internal Task AttemptCompletion { get; private set; } = Task.CompletedTask;
        internal EffectDialog(GitRepository repository, Task? tail = null) : base(repository, "Private Log child effect")
        {
            _tail = tail; Width = 460; Height = 220;
            Button mutate = Action("WriteLogChildEffect", "Write staged effect then fail", () => AttemptCompletion = Execute());
            Button close = Action("CloseLogChildEffect", "Close", () => { Close(); return Task.CompletedTask; });
            Root.Children.Add(new StackPanel { Children = { mutate, close } });
        }
        public override Task InitializeAsync() => Task.CompletedTask;
        private Task Execute() => RunAsync("Private partial staged effect", async token =>
        {
            _attemptToken = token;
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), PartialText, token);
            await Repository.StageAllAsync(token); PartialEffectWritten = true;
            if (_tail is not null) { await _tail.WaitAsync(TimeSpan.FromSeconds(20)); }
            CancellationWasRequested = token.IsCancellationRequested; token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), FinalText, token);
            await Repository.StageAllAsync(token); FinalEffectWritten = true;
            throw new IOException("Private failure after real staged effects.");
        }, changes: true);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ClassicDesktopStyleApplicationLifetime _lifetime = new();
        internal MainWindow Root { get; private set; } = null!;
        internal MainWindow Launcher { get; private set; } = null!;
        internal MainWindow Log { get; private set; } = null!;
        internal Task LogSession { get; private set; } = Task.CompletedTask;
        internal static async Task<Host> Open(Fixture git, bool secondary = false)
        {
            Host host = new();
            try
            {
                host.Root = new(new BrowseRequest(git.Repository.Path)) { ModelessApplicationLifetime = host._lifetime, Width = 1440, Height = 960 };
                host._lifetime.MainWindow = host.Root; host.Root.Show(); await SourceIdle(host.Root);
                if (secondary)
                {
                    host.Launcher = new(new BrowseRequest(git.Repository.Path)) { ModelessApplicationLifetime = host._lifetime, Width = 1440, Height = 960 };
                    host.Launcher.Show(); await SourceIdle(host.Launcher);
                }
                else { host.Launcher = host.Root; }
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        internal async Task OpenLog()
        {
            LogSession = Launcher.ShowRevisionLogAsync();
            await Until(Launcher, () => Launcher.OwnedWindows.OfType<MainWindow>().Any(window => window.WindowKind == MainWindowKind.RevisionLog));
            Log = Launcher.OwnedWindows.OfType<MainWindow>().Single(window => window.WindowKind == MainWindowKind.RevisionLog);
            await SourceIdle(Log); Assert.That(Log.Owner, Is.SameAs(Launcher));
            Assert.That(Repository(Log), Is.Not.SameAs(Repository(Launcher)), "The modal return bridges the actual independently opened repository instances.");
        }
        internal async Task<MainWindow> OpenDiff()
        {
            ListBox history = History(Log); GitCommit commit = history.Items.OfType<GitCommit>().First();
            Control row = (Control)history.ContainerFromItem(commit)!;
            Point point = row.TranslatePoint(new Point(Math.Max(1, row.Bounds.Width - 20), row.Bounds.Height / 2), Log)!.Value;
            Assert.That((Log.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
            Log.MouseMove(point);
            for (int index = 0; index < 2; index++) { Log.MouseDown(point, MouseButton.Left); Log.MouseUp(point, MouseButton.Left); }
            Pump(Log); await Until(Root, () => Root.ModelessCommitDiffWindows.Count == 1);
            MainWindow child = Root.ModelessCommitDiffWindows.Single(); await SourceIdle(child);
            Assert.That(child.Owner, Is.Null); Assert.That(Log.ModelessCommitDiffWindows, Is.Empty);
            Assert.That(Repository(child), Is.SameAs(Repository(Log))); Assert.That(child.CommitDiffRevision, Is.EqualTo(commit.Hash));
            return child;
        }
        internal async Task CloseDiff(MainWindow child)
        {
            Press(child, PhysicalKey.Escape);
            await Until(Root, () => !child.IsVisible && child.WindowWorkCompletion.IsCompleted && Root.ModelessCommitDiffWindows.Count == 0);
            await child.WindowWorkCompletion;
        }
        internal async Task CloseLog()
        {
            Log.Close(); await Until(Launcher, () => !Log.IsVisible && LogSession.IsCompleted && !Launcher.IsBusy);
            await Log.WindowWorkCompletion; await LogSession; await ReceiverOperationIdle(Launcher);
        }
        public async ValueTask DisposeAsync()
        {
            if (Log is not null && Log.IsVisible) { Log.Close(); }
            if (Launcher is not null && !ReferenceEquals(Launcher, Root) && Launcher.IsVisible) { Launcher.Close(); }
            if (Root is not null)
            {
                Root.Close(); await Until(Root, () => !Root.IsVisible && Root.WindowWorkCompletion.IsCompleted); await Root.WindowWorkCompletion;
            }
            await LogSession;
            if (Launcher is not null) { await Launcher.WindowWorkCompletion; }
            _lifetime.Dispose();
        }
    }

    private static async Task<ResetChangesDialog> OpenResetPrompt(MainWindow child)
    {
        FileStatusListView files = Field<FileStatusListView>(child, "RevisionFileStatusList");
        object tag = files.Entries.Single(entry => entry.Path == "file.txt").Tag; TreeViewItem item = files.FindItemByTag(tag)!;
        Control header = item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == item);
        header.BringIntoView(); Pump(child); PushProcessTestDriver.Pointer(header); await SourceIdle(child);
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), child)!.Value;
        child.MouseMove(point); child.MouseDown(point, MouseButton.Right); child.MouseUp(point, MouseButton.Right); Pump(child);
        ContextMenu menu = files.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
        MenuItem reset = menu.Items.OfType<MenuItem>().Single(control => control.Name == "tsmiResetFileTo");
        TopLevel popup = TopLevel.GetTopLevel(reset)!;
        popup.MouseMove(reset.TranslatePoint(new Point(reset.Bounds.Width * .7, reset.Bounds.Height / 2), popup)!.Value);
        await Until(child, () => reset.IsSubMenuOpen && reset.Items.OfType<MenuItem>().First().Bounds.Height > 0);
        PushProcessTestDriver.Pointer(reset.Items.OfType<MenuItem>().Single(control => control.Name == "tsmiResetFileToSelected"));
        await Until(child, () => child.OwnedWindows.OfType<ResetChangesDialog>().Any(dialog => dialog.IsVisible));
        return child.OwnedWindows.OfType<ResetChangesDialog>().Single(dialog => dialog.IsVisible);
    }
    private static async Task CloseWithoutAnotherRefresh(Host host, MainWindow child)
    {
        object? source = History(host.Launcher).ItemsSource; GitCommandLog.Clear();
        await host.CloseDiff(child); await host.Root.WindowWorkCompletion;
        Assert.That(History(host.Launcher).ItemsSource, Is.SameAs(source)); AssertNoGlobalReads();
    }
    private static void AssertIndependentSessionIsStillAdmitted(Host host, MainWindow child)
    {
        Assert.That(host.Root.ModelessCommitDiffWindows.Single(), Is.SameAs(child));
        Assert.That(host.Root.WindowWorkCompletion.IsCompleted, Is.False,
            "The living independent child still owns its root session lease after the receiver refresh drains.");
    }
    private static async Task ReceiverRefresh(MainWindow receiver, object? previousSource)
    {
        // Root also owns the still-open independent session. Its global work
        // completion belongs after CloseDiff; this is the refresh's frontier.
        await Until(receiver, () => GitCommandLog.Snapshot().Any(entry => entry.Command == "git log")
            && !receiver.IsBusy && Private<Task>(receiver, "_repositoryOperationCompletion").IsCompleted);
        await ReceiverOperationIdle(receiver);
        Assert.That(History(receiver).ItemsSource, Is.SameAs(previousSource));
    }
    private static async Task ReceiverOperationIdle(MainWindow receiver)
    { await Until(receiver, () => !receiver.IsBusy); await Private<Task>(receiver, "_repositoryOperationCompletion"); Pump(receiver); }
    private static void AssertUnchangedRows(ListBox history, object? source, object[] rows)
    {
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.Items.Count, Is.EqualTo(rows.Length));
        Assert.That(history.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
    }
    private static async Task<Fixture> History()
    { Fixture git = await Fixture.CreateAsync(); try { await git.ChangeAsync("second\n", "Second"); await git.ChangeAsync("third\n", "Third"); return git; } catch { git.Dispose(); throw; } }
    private static ListBox History(MainWindow main) => Field<ListBox>(main, "RevisionHistory");
    private static GitRepository Repository(MainWindow main) => Private<GitRepository>(main, "_repository");
    private static T Private<T>(MainWindow main, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static Task ShowDialog(MainWindow child, RepositoryDialog dialog) => (Task)typeof(MainWindow)
        .GetMethod("ShowRepositoryDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(child, [dialog, true])!;
    private static Task Until(MainWindow main, Func<bool> condition) => PushProcessTestDriver.UntilAsync(main, condition);
    private static async Task SourceIdle(MainWindow child)
    { await Until(child, () => !child.IsBusy && child.WindowWorkCompletion.IsCompleted); await child.WindowWorkCompletion; Assert.That(child.LastException, Is.Null, child.LastException?.ToString()); }
    private static void Press(Window window, PhysicalKey key) => PortableHelperSettingsFixture.Press(window, key);
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static void AssertNoGlobalReads() => Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"));
    private static void AssertNoHistoryReads() => Assert.That(GitCommandLog.Snapshot().Count(entry => entry.Command == "git log"), Is.Zero);
    private static void AssertOneHistoryRead() => Assert.That(GitCommandLog.Snapshot().Count(entry => entry.Command == "git log"), Is.EqualTo(1));
}
