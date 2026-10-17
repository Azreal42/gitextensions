using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using HistoryHelpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitInformationIntegrationTests
{
    [AvaloniaTest]
    public async Task HiddenBrowseInformationLoadsOnlyOnActivationWithoutReplacingTheHistory()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await MainIdle(main);
        await PlaceBelow(main);
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");
        PushProcessTestDriver.Pointer(Field<TabItem>(main, "DiffTab")); await MainIdle(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory"); object source = history.ItemsSource!;
        GitCommit selected = (GitCommit)history.Items[1]!;
        using MetadataGate gate = new(f); gate.Arm(selected.Hash); SetRepository(main, gate.Repository);
        try
        {
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!);
            await MainIdle(main);
            Assert.That(gate.Started, Is.False, "A hidden lower CommitInfo tab must not start its supplementary metadata read.");
            Assert.That(information.MessageControl.Text, Does.Contain(selected.Subject), "Cached metadata is immediate even while the panel is hidden.");
            Assert.That(history.ItemsSource, Is.SameAs(source));
            PushProcessTestDriver.Pointer(Field<TabItem>(main, "CommitInfoTab"));
            await Until(() => gate.Started);
            Assert.That(history.IsEffectivelyEnabled, Is.True);
            gate.Release(); await MainIdle(main);
            Assert.That(information.Text, Does.Contain(selected.Hash));
            Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(main.LastException, Is.Null);
        }
        finally { gate.Release(); main.Close(); await Drain(information.Completion); }
    }

    [AvaloniaTest]
    public async Task TheFirstArrowDuringBrowseMetadataReadKeepsFocusAndOnlyTheLatestCommitPublishes()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await MainIdle(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");
        GitCommit first = (GitCommit)history.Items[1]!, last = (GitCommit)history.Items[2]!;
        object source = history.ItemsSource!;
        using MetadataGate gate = new(f); gate.Arm(first.Hash); SetRepository(main, gate.Repository);
        GitCommandLog.Clear();
        try
        {
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!); await Until(() => gate.Started);
            int pid = gate.Pid;
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(history.IsEffectivelyEnabled, Is.True);
            Press(main, PhysicalKey.ArrowDown);
            Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(last.Hash));
            await MainIdle(main); await Until(() => !Alive(pid));
            Assert.That(information.MessageControl.Text, Does.Contain(last.Subject).And.Not.Contain(first.Subject));
            Assert.That(information.Text, Does.Contain(last.Hash));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(main.LastException, Is.Null, "Cancellation of the prior generation must not become a current metadata error.");
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status").And.Not.Contain("git ls-tree"));
        }
        finally { gate.Release(); main.Close(); await Drain(information.Completion); }
    }

    [AvaloniaTest]
    public async Task FileHistoryCommitMetadataLeavesItsRevisionListActiveForTheNextSelection()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await File.WriteAllTextAsync(Path.Combine(f.Repository, "file.txt"), "base\nfirst change\n");
        await HistoryHelpers.Git(f, "add", "--", "file.txt");
        await HistoryHelpers.Git(f, "commit", "-m", "First file change");
        await File.WriteAllTextAsync(Path.Combine(f.Repository, "file.txt"), "base\nsecond change\n");
        await HistoryHelpers.Git(f, "add", "--", "file.txt");
        await HistoryHelpers.Git(f, "commit", "-m", "Second file change");
        using MetadataGate gate = new(f);
        FileHistoryDialog dialog = new(gate.Repository, "file.txt"); dialog.Show();
        CommitInformationView? information = null;
        try
        {
            await dialog.InitializeAsync(); await HistoryHelpers.Idle(dialog);
            ListBox history = Field<ListBox>(dialog, "FileHistoryRevisions"); object source = history.ItemsSource!;
            GitCommit first = (GitCommit)history.Items[0]!, last = (GitCommit)history.Items[1]!;
            gate.Arm(first.Hash);
            PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "FileHistoryCommitTab")); await Until(() => gate.Started);
            information = Field<CommitInformationView>(dialog, "FileHistoryCommitInfo"); int pid = gate.Pid;
            Assert.That(dialog.IsBusy, Is.False, "CommitInfo alone must not acquire RepositoryDialog's global busy state.");
            Assert.That(history.IsEffectivelyEnabled, Is.True);
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(0)!);
            Press(dialog, PhysicalKey.ArrowDown);
            await HistoryHelpers.Idle(dialog); await Until(() => !Alive(pid));
            Assert.That(dialog.SelectedEntry?.Commit.Hash, Is.EqualTo(last.Hash));
            Assert.That(information.MessageControl.Text, Does.Contain(last.Subject).And.Not.Contain(first.Subject));
            Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(dialog.LastError, Is.Null);
        }
        finally { gate.Release(); dialog.Close(); await Drain(dialog.InformationCompletion); }
    }

    [AvaloniaTest]
    public async Task AFailedBrowseMetadataReadRecoversWhenTheNextCommitLoadsSuccessfully()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await MainIdle(main); await PlaceBelow(main);
        PushProcessTestDriver.Pointer(Field<TabItem>(main, "CommitInfoTab")); await MainIdle(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");
        GitCommit first = (GitCommit)history.Items[1]!, last = (GitCommit)history.Items[2]!;
        object source = history.ItemsSource!;
        using MetadataGate gate = new(f, failMetadata: true); gate.Arm(first.Hash); SetRepository(main, gate.Repository);
        try
        {
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!); await MainIdle(main);
            Assert.That(gate.Started, Is.True, "The failure must come from the targeted metadata command.");
            Assert.That(main.LastException, Is.TypeOf<GitCommandException>());
            GitCommandException error = (GitCommandException)main.LastException!;
            Assert.That(error.Result.ExitCode, Is.EqualTo(73));
            Assert.That(error.Arguments, Does.Contain("show").And.Contain("--no-notes").And.Contain(first.Hash));
            Assert.That(information.MessageControl.Text, Does.Contain(first.Subject));

            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(2)!); await MainIdle(main);

            Assert.That(information.MessageControl.Text, Does.Contain(last.Subject).And.Not.Contain(first.Subject));
            Assert.That(information.Text, Does.Contain(last.Hash));
            Assert.That(main.LastException, Is.Null, "A succeeding metadata request clears its own preceding failure.");
            Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
        }
        finally { gate.Release(); main.Close(); await Drain(information.Completion); }
    }

    [AvaloniaTest]
    public async Task BlameMetadataFollowsTheNewlyClickedAttributionWithoutDisablingTheSource()
    {
        using Fixture f = await Fixture.CreateAsync(HistoryHelpers.Initial, HistoryHelpers.Initial);
        await HistoryHelpers.MakeHistoryAsync(f);
        using MetadataGate gate = new(f);
        BlameDialog dialog = new(gate.Repository, HistoryHelpers.LiteralPath); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await HistoryHelpers.Idle(dialog);
            BlameView blame = Field<BlameView>(dialog, "BlameView");
            CommitInformationView information = Field<CommitInformationView>(dialog, "BlameCommitInfo");
            string first = blame.Lines[0].Line.Hash, last = blame.Lines[1].Line.Hash;
            Assert.That(first, Is.Not.EqualTo(last));
            ClickBlameLine(dialog, 1); await HistoryHelpers.Idle(dialog);
            gate.Arm(first);
            ClickBlameLine(dialog, 0); await Until(() => gate.Started); int pid = gate.Pid;
            Assert.That(dialog.IsBusy, Is.False);
            Assert.That(blame.IsEffectivelyEnabled, Is.True);
            ClickBlameLine(dialog, 1);
            await HistoryHelpers.Idle(dialog); await Until(() => !Alive(pid));
            Assert.That(blame.SelectedLine?.Line.Hash, Is.EqualTo(last));
            Assert.That(information.Text, Does.Contain(last).And.Contain("Actual author commit body."));
            Assert.That(dialog.LastError, Is.Null);
        }
        finally { gate.Release(); dialog.Close(); await Drain(dialog.InformationCompletion); }
    }

    [AvaloniaTest]
    [TestCase("Browse")]
    [TestCase("FileHistory")]
    [TestCase("Blame")]
    public async Task ClosingTheInformationHostCancelsAndDrainsTheRealMetadataProcess(string host)
    {
        using Fixture f = await Fixture.CreateAsync(HistoryHelpers.Initial, HistoryHelpers.Initial, createMerge: true);
        using MetadataGate gate = new(f);
        Window window; CommitInformationView? information = null;
        Task? shown = null;
        if (host == "Browse")
        {
            MainWindow main = f.Window; await MainIdle(main);
            ListBox history = Field<ListBox>(main, "RevisionHistory");
            gate.Arm(((GitCommit)history.Items[1]!).Hash); SetRepository(main, gate.Repository);
            window = main; information = Field<CommitInformationView>(main, "CommitInformation");
            PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!);
        }
        else if (host == "FileHistory")
        {
            FileHistoryDialog history = new(gate.Repository, "file.txt"); window = history;
            shown = history.ShowDialog(f.Window); await history.InitializeAsync();
            gate.Arm(history.SelectedEntry!.Commit.Hash);
            PushProcessTestDriver.Pointer(Field<TabItem>(history, "FileHistoryCommitTab"));
            information = Field<CommitInformationView>(history, "FileHistoryCommitInfo");
        }
        else
        {
            await HistoryHelpers.MakeHistoryAsync(f);
            BlameDialog blame = new(gate.Repository, HistoryHelpers.LiteralPath); window = blame;
            shown = blame.ShowDialog(f.Window); await blame.InitializeAsync();
            BlameView view = Field<BlameView>(blame, "BlameView");
            gate.Arm(view.Lines[1].Line.Hash); ClickBlameLine(blame, 1);
            information = Field<CommitInformationView>(blame, "BlameCommitInfo");
        }
        try
        {
            await Until(() => gate.Started); int pid = gate.Pid;
            window.Close();
            await Until(() => !window.IsVisible);
            await Drain(information!.Completion);
            if (shown is not null) { await shown; }
            await Until(() => !Alive(pid));
            Assert.That(information.Completion.IsCompleted, Is.True);
            Assert.That(Alive(pid), Is.False);
        }
        finally
        {
            gate.Release(); window.Close();
            if (information is not null) { await Drain(information.Completion); }
            if (shown is not null) { await shown; }
        }
    }

    [AvaloniaTest]
    public async Task RepositoryFixtureDisposalDrainsPendingMetadataBeforeRestoringItsProfile()
    {
        string? previousProfile = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; await MainIdle(main);
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        using MetadataGate gate = new(f); gate.Arm(((GitCommit)history.Items[1]!).Hash); SetRepository(main, gate.Repository);
        PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(1)!);
        await Until(() => gate.Started); int pid = gate.Pid;
        Assert.That(information.IsLoading, Is.True);

        f.Dispose();

        Assert.That(main.IsVisible, Is.False);
        Assert.That(information.Completion.IsCompleted, Is.True);
        Assert.That(Alive(pid), Is.False, "The fixture must cancel and drain Git without releasing the blocked metadata command.");
        Assert.That(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Is.EqualTo(previousProfile));
        Assert.That(Directory.Exists(f.Repository), Is.False);
    }

    private static async Task PlaceBelow(MainWindow main)
    {
        Button button = Field<Button>(main, "CommitInfoPositionMenu");
        MenuFlyout menu = (MenuFlyout)button.Flyout!;
        MenuItem below = menu.ItemsSource!.Cast<MenuItem>().Single(item => Equals(item.Tag, CommitInfoPosition.BelowList));
        PushProcessTestDriver.Pointer(button); await Until(() => below.IsEffectivelyVisible);
        PushProcessTestDriver.Pointer(below); await MainIdle(main);
    }
    private static void ClickBlameLine(Window window, int row)
    {
        Pump(window);
        BlameView view = Field<BlameView>(window, "BlameView"); Control margin = Field<Control>(window, "BlameAuthorMargin");
        Point point = margin.TranslatePoint(new Point(100, view.TextView.Editor.TextArea.TextView.DefaultLineHeight * (row + .5)), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(margin), Is.True);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
    }
    private static void SetRepository(MainWindow main, GitRepository repository)
        => typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, repository);
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Press(TopLevel window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task MainIdle(MainWindow window)
    { await Until(() => !window.IsBusy); Pump(window); }
    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }
    private static async Task Drain(Task pending)
    { try { await pending; } catch (OperationCanceledException) { } }
    private static bool Alive(int pid)
    {
        try { string data = File.ReadAllText($"/proc/{pid}/stat"); return data[(data.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); }
        catch (IOException) { return false; }
    }

    private sealed class MetadataGate : IDisposable
    {
        private readonly string _target, _started, _release;
        internal GitRepository Repository { get; }
        internal bool Started => File.Exists(_started) && new FileInfo(_started).Length > 0;
        internal int Pid => int.Parse(File.ReadAllText(_started), System.Globalization.CultureInfo.InvariantCulture);
        internal MetadataGate(Fixture fixture, bool failMetadata = false)
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("Actual Git cancellation gates require Linux procfs."); }
            string root = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "metadata-gate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            _target = Path.Combine(root, "target"); _started = Path.Combine(root, "started"); _release = Path.Combine(root, "release");
            string executable = Path.Combine(root, "git");
            File.WriteAllText(executable, $$"""
                #!/usr/bin/python3
                import os,sys,time
                from pathlib import Path
                target=Path({{JsonSerializer.Serialize(_target)}})
                if target.exists() and target.read_text() in sys.argv[1:] and 'show' in sys.argv[1:] and '--no-notes' in sys.argv[1:]:
                    Path({{JsonSerializer.Serialize(_started)}}).write_text(str(os.getpid()))
                    if {{(failMetadata ? "True" : "False")}}:
                        print('Commit information fixture failure', file=sys.stderr)
                        sys.exit(73)
                    for _ in range(2000):
                        if Path({{JsonSerializer.Serialize(_release)}}).exists(): break
                        time.sleep(.01)
                    else: sys.exit(74)
                os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[1:]])
                """);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Repository = new(fixture.Repository, new GitRunner(executable));
        }
        internal void Arm(string hash) => File.WriteAllText(_target, hash);
        internal void Release() => File.WriteAllText(_release, "release");
        public void Dispose()
        { if (Directory.Exists(Path.GetDirectoryName(_release))) { Release(); } }
    }
}
