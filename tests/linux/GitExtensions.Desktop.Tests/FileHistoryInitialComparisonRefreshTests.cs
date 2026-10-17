using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryInitialComparisonRefreshTests
{
    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task RefreshAndReopenReloadTheRealSelectedCommitAndKeepTheActualArtificialFirstSide(GitCompareRevisionKind first)
    {
        await using Scenario scenario = await Scenario.OpenAsync(first);
        MainWindow main = scenario.Main;
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");
        Assert.That(information.Text, Does.Contain(scenario.Selected).And.Contain("Selected snapshot"));
        AssertPair(scenario, first == GitCompareRevisionKind.Index ? "INDEX FILE" : "WORKTREE FILE");

        await scenario.UpdateFirstAsync("UPDATED FIRST");
        await scenario.Git.GitAsync("notes", "add", "-m", "Fresh note after opening", scenario.Selected);
        Material before = await scenario.MaterialAsync();
        PushProcessTestDriver.Pointer(information.MessageControl);
        Assert.That(information.MessageControl.IsKeyboardFocusWithin, Is.True);

        Press(main, PhysicalKey.F5);
        await Idle(main);

        Assert.That(information.Text, Does.Contain(scenario.Selected).And.Contain("Selected snapshot").And.Contain("Fresh note after opening"),
            "F5 must rebind and load CommitInformationView for the selected real commit, even after its startup token is consumed.");
        Assert.That(information.Text, Does.Not.Contain("Descendant snapshot"));
        Assert.That(information.MessageControl.IsKeyboardFocusWithin, Is.True, "Supplementary metadata must not move the operation's input focus.");
        AssertPair(scenario, "UPDATED FIRST");
        await AssertFirstMenuAsync(scenario);
        Assert.That(await scenario.MaterialAsync(), Is.EqualTo(before));

        await scenario.Git.GitAsync("notes", "append", "-m", "Fresh note before same-repository reopen", scenario.Selected);
        before = await scenario.MaterialAsync();
        await main.OpenRepositoryAsync(scenario.Git.Repository.Path);
        await Idle(main);

        Assert.That(information.Text, Does.Contain(scenario.Selected).And.Contain("Fresh note before same-repository reopen"));
        AssertPair(scenario, "UPDATED FIRST");
        Assert.That(await scenario.MaterialAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task SelectingAnotherCommitCancelsTheRefreshedMetadataAndDoesNotRestoreTheStartupPair(GitCompareRevisionKind first)
    {
        await using Scenario scenario = await Scenario.OpenAsync(first);
        using MetadataGate gate = new(scenario.Git, scenario.Selected);
        gate.Arm(block: true);
        MainWindow main = scenario.Main;
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");

        Press(main, PhysicalKey.F5);
        await Until(() => gate.Started && Field<ListBox>(main, "RevisionHistory").IsEffectivelyEnabled);
        int pid = gate.Pid;
        Task oldWork = main.WindowWorkCompletion;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit next = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == scenario.Head);
        PushProcessTestDriver.Pointer((Control)history.ContainerFromIndex(history.Items.IndexOf(next))!);
        await Idle(main);

        Assert.That(oldWork.IsCompleted, Is.True);
        Assert.That(Alive(pid), Is.False, "The obsolete real metadata reader must drain before returning idle.");
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(scenario.Head));
        Assert.That(information.Text, Does.Contain(scenario.Head).And.Contain("Descendant snapshot").And.Not.Contain("Selected snapshot"));
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<DiffView>(main, "CommitDiff").Text, Does.Contain("-SELECTED FILE").And.Contain("+DESCENDANT FILE"));
        Assert.That(main.LastException, Is.Null);

        gate.Disarm();
        Press(main, PhysicalKey.F5);
        await Idle(main);
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(scenario.Head));
        Assert.That(information.Text, Does.Contain(scenario.Head).And.Not.Contain("Selected snapshot"));
        Assert.That(Field<DiffView>(main, "CommitDiff").DocumentIdentity, Does.Not.Contain(first.ToString()));
        Assert.That(Field<DiffView>(main, "CommitDiff").Text, Does.Contain("-SELECTED FILE").And.Contain("+DESCENDANT FILE"));
    }

    [AvaloniaTest]
    public async Task AFailedInitialPairMetadataRefreshRecoversAndClosingTheNextReadDrainsItsGitProcess()
    {
        await using Scenario scenario = await Scenario.OpenAsync(GitCompareRevisionKind.Index);
        using MetadataGate gate = new(scenario.Git, scenario.Selected);
        MainWindow main = scenario.Main;
        CommitInformationView information = Field<CommitInformationView>(main, "CommitInformation");
        gate.Arm(block: false);

        Press(main, PhysicalKey.F5);
        await Idle(main, allowError: true);
        Assert.That(gate.Started, Is.True, "The error must come from an actual supplementary read of the selected commit.");
        Assert.That(main.LastException, Is.TypeOf<GitCommandException>());
        GitCommandException error = (GitCommandException)main.LastException!;
        Assert.That(error.Result.ExitCode, Is.EqualTo(73));
        Assert.That(error.Arguments, Does.Contain(scenario.Selected).And.Contain("--no-notes"));
        AssertPair(scenario, "INDEX FILE");

        gate.Disarm();
        await scenario.Git.GitAsync("notes", "add", "-m", "Recovered real metadata", scenario.Selected);
        Material before = await scenario.MaterialAsync();
        Press(main, PhysicalKey.F5);
        await Idle(main);
        Assert.That(information.Text, Does.Contain(scenario.Selected).And.Contain("Recovered real metadata"));
        Assert.That(main.LastException, Is.Null);
        AssertPair(scenario, "INDEX FILE");

        gate.Arm(block: true);
        Press(main, PhysicalKey.F5);
        await Until(() => gate.Started && information.IsLoading);
        int pid = gate.Pid;
        Task pending = main.WindowWorkCompletion;
        main.Close();
        await Until(() => !main.IsVisible && pending.IsCompleted && main.WindowWorkCompletion.IsCompleted);
        try { await pending; }
        catch (OperationCanceledException) { /* The captured metadata read is cancelled by owner close. */ }
        Assert.That(Alive(pid), Is.False, "Owner close must cancel and drain the real Git reader, not release it after closing.");
        Assert.That(information.Completion.IsCompleted, Is.True);
        Assert.That(main.LastException, Is.Null, "Closing cancellation must not be published as a new metadata failure.");
        Assert.That(await scenario.MaterialAsync(), Is.EqualTo(before));
    }

    private static void AssertPair(Scenario scenario, string firstContent)
    {
        MainWindow main = scenario.Main;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(scenario.Selected));
        Assert.That(history.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { scenario.Selected }));
        Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Contain(scenario.Head),
            "The selected older commit and its First side must never become a graph traversal bound.");
        Assert.That(history.Items.Cast<object>(), Is.All.TypeOf<GitCommit>(), "Artificial endpoints must not be fabricated GitCommit rows.");
        DiffView diff = Field<DiffView>(main, "CommitDiff");
        Assert.That(diff.Text, Does.Contain("-" + firstContent).And.Contain("+SELECTED FILE").And.Not.Contain("+DESCENDANT FILE"));
        Assert.That(diff.DocumentIdentity, Does.Contain(scenario.First.ToString()).And.Contain(scenario.Selected));
        Assert.That(FileStatusTestHelpers.Rows(Field<TreeView>(main, "RevisionFiles")).Select(row => row.Tag)
            .OfType<RevisionDiffFileSelection>().Select(row => row.File.Path), Is.EquivalentTo(new[] { "file.txt", "other.txt" }));
    }

    private static async Task AssertFirstMenuAsync(Scenario scenario)
    {
        static string Caption(MenuItem item) => item.Header is AccessText access ? access.Text ?? "" : item.Header?.ToString() ?? "";
        MainWindow main = scenario.Main;
        TreeView files = Field<TreeView>(main, "RevisionFiles");
        TreeViewItem row = FileStatusTestHelpers.Rows(files).Single(item => item.Tag is RevisionDiffFileSelection { File.Path: "file.txt" });
        Control header = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
            && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), row));
        PushProcessTestDriver.Pointer(header);
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), main)!.Value;
        Assert.That((main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(header), Is.True);
        main.MouseMove(point); main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
        await Until(() => files.ContextMenu!.IsOpen);
        Pump(main);
        MenuItem reset = files.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileTo");
        Assert.That(reset.Items.OfType<MenuItem>().Select(item => item.Name), Does.Not.Contain("tsmiResetFileToParent"),
            "An artificial First cannot be a reset destination for a committed Selected side.");
        MenuItem second = reset.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileToSelected");
        Assert.That(Caption(second), Does.Contain(scenario.Selected[..8]));
        MenuItem difftool = files.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiOpenWithDifftool");
        MenuItem first = difftool.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiFirstDiffCaption");
        Assert.That(Caption(first), Does.Contain(scenario.First == GitCompareRevisionKind.Index ? "Index" : "Working directory"));
        Assert.That(Caption(difftool.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiSecondDiffCaption")),
            Does.Contain(scenario.Selected[..8]));
        Assert.That(files.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Name), Does.Not.Contain("tsmiStageFile").And.Not.Contain("tsmiUnstageFile"),
            "An artificial First does not turn the real committed Selected side into a staging view.");
        Press(main, PhysicalKey.Escape);
        await Until(() => !files.ContextMenu.IsOpen);
    }

    private sealed record Material(string Head, string Index, string Entries, string Refs, string File, string Other);

    private sealed class Scenario(ReferenceFixture git, MainWindow main, GitCompareRevisionKind first, string selected, string head) : IAsyncDisposable
    {
        internal ReferenceFixture Git => git;
        internal MainWindow Main => main;
        internal GitCompareRevisionKind First => first;
        internal string Selected => selected;
        internal string Head => head;

        internal static async Task<Scenario> OpenAsync(GitCompareRevisionKind first)
        {
            ReferenceFixture git = await ReferenceFixture.CreateAsync();
            MainWindow? main = null;
            try
            {
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "SELECTED OTHER\n");
                await git.ChangeAsync("SELECTED FILE\n", "Selected snapshot");
                string selected = await git.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "DESCENDANT OTHER\n");
                await git.ChangeAsync("DESCENDANT FILE\n", "Descendant snapshot");
                string head = await git.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(git.File, "INDEX FILE\n");
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "INDEX OTHER\n");
                await git.Repository.StageAllAsync();
                await File.WriteAllTextAsync(git.File, "WORKTREE FILE\n");
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "WORKTREE OTHER\n");
                new PreferencesStore().Save(new DesktopPreferences { HistoryLimit = 1 });
                new BrowserLayoutPreferencesStore().Save(new(CommitInfoPosition.RightwardFromList));
                main = new(new BrowseRequest(PathFilter: new("file.txt", BrowsePathKind.File),
                    SelectedRevision: new(selected), FirstRevision: new("", first))) { Width = 1440, Height = 900 };
                main.Show();
                await main.OpenRepositoryAsync(git.Repository.Path);
                await Idle(main);
                return new(git, main, first, selected, head);
            }
            catch
            {
                if (main is not null) { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); }
                git.Dispose(); throw;
            }
        }

        internal async Task UpdateFirstAsync(string value)
        {
            await File.WriteAllTextAsync(git.File, value + "\n");
            if (first == GitCompareRevisionKind.Index)
            { await git.GitAsync("add", "--", "file.txt"); await File.WriteAllTextAsync(git.File, "WORKTREE FILE\n"); }
        }

        internal async Task<Material> MaterialAsync() => new(await git.Repository.ResolveRevisionAsync("HEAD"),
            Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(git.Repository.Path, ".git", "index"))),
            (await git.GitAsync("ls-files", "--stage", "-z")).StandardOutput, (await git.GitAsync("show-ref")).StandardOutput,
            await File.ReadAllTextAsync(git.File), await File.ReadAllTextAsync(git.PathInRepository("other.txt")));

        public async ValueTask DisposeAsync()
        {
            main.Close();
            await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
            await main.WindowWorkCompletion;
            git.Dispose();
        }
    }

    private sealed class MetadataGate : IDisposable
    {
        private readonly ReferenceFixture _git;
        private readonly string? _oldPath;
        private readonly string _mode, _marker, _release;
        internal bool Started => File.Exists(_marker) && new FileInfo(_marker).Length > 0;
        internal int Pid => int.Parse(File.ReadAllText(_marker), System.Globalization.CultureInfo.InvariantCulture);

        internal MetadataGate(ReferenceFixture git, string hash)
        {
            _git = git; _oldPath = Environment.GetEnvironmentVariable("PATH");
            string root = git.Output("initial-pair-metadata-gate"); Directory.CreateDirectory(root);
            _mode = Path.Combine(root, "mode"); _marker = Path.Combine(root, "started"); _release = Path.Combine(root, "release");
            string executable = Path.Combine(root, "git");
            File.WriteAllText(executable, $$"""
                #!/usr/bin/python3
                import os,sys,time
                from pathlib import Path
                mode=Path({{JsonSerializer.Serialize(_mode)}})
                if mode.exists() and {{JsonSerializer.Serialize(hash)}} in sys.argv[1:] and 'show' in sys.argv[1:] and '--no-notes' in sys.argv[1:]:
                    Path({{JsonSerializer.Serialize(_marker)}}).write_text(str(os.getpid()))
                    if mode.read_text()=='fail':
                        print('Initial-pair metadata failure',file=sys.stderr)
                        sys.exit(73)
                    while not Path({{JsonSerializer.Serialize(_release)}}).exists():time.sleep(.01)
                os.execv('/usr/bin/git',['git',*sys.argv[1:]])
                """);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            git.SetEnvironment("PATH", root + Path.PathSeparator + _oldPath);
        }

        internal void Arm(bool block)
        { File.Delete(_marker); File.Delete(_release); File.WriteAllText(_mode, block ? "block" : "fail"); }
        internal void Disarm() { File.Delete(_mode); File.WriteAllText(_release, "released"); }
        public void Dispose() { Disarm(); _git.SetEnvironment("PATH", _oldPath); }
    }

    private static bool Alive(int pid)
    {
        try { string raw = File.ReadAllText($"/proc/{pid}/stat"); return raw[(raw.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); }
        catch (IOException) { return false; }
    }
    private static T Field<T>(TopLevel main, string name) where T : Control => PushProcessTestDriver.Field<T>(main, name);
    private static void Press(MainWindow main, PhysicalKey key)
    { main.KeyPressQwerty(key, RawInputModifiers.None); main.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(main); }
    private static void Pump(TopLevel main)
    { Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task Idle(MainWindow main, bool allowError = false)
    {
        await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
        Pump(main);
        if (!allowError) { Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(condition(), Is.True, "The initial comparison's actual Git/window work did not settle.");
    }
}
