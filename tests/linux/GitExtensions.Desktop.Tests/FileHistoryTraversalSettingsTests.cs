using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryTraversalSettingsTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AdvancedCancelOkAndResetKeepSharedSettingsSeparateFromTheAcceptedFilters(bool dark)
    {
        await using Scope scope = await Scope.OpenAsync(path: "file.txt", dark: dark, secondCommit: true);
        MainWindow main = scope.Window; ListBox history = Field<ListBox>(main, "RevisionHistory");
        string selected = ((GitCommit)history.SelectedItem!).Hash, gitBefore = Snapshot(scope.Fixture.Repository.Path);
        byte[] preferences = File.ReadAllBytes(scope.Store.FilePath);
        HistoryFilterDialog cancel = await OpenFilterAsync(main, shortcut: true);
        Toggle(cancel, "FullHistory", true); Toggle(cancel, "SimplifyMerges", true);
        Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(preferences), "A filter draft cannot persist before acceptance.");
        Press(cancel, PhysicalKey.Escape, release: main); await Idle(main);
        Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(preferences));
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected));
        Assert.That(history.IsKeyboardFocusWithin, Is.True, "Ctrl+I cancellation must preserve the actual grid input origin.");

        HistoryFilterDialog accept = await OpenFilterAsync(main, shortcut: false);
        Toggle(accept, "FullHistory", true); Toggle(accept, "SimplifyMerges", true); scope.ClearTrace();
        ClickButton(accept, "OK"); await Idle(main);
        Assert.That(scope.Store.Load().FullHistory && scope.Store.Load().SimplifyMerges, Is.True);
        Assert.That(scope.Store.Load().AdditionalSettings!["UnrelatedFutureValue"].GetString(), Is.EqualTo("preserve me"));
        AssertGraphFlags(scope.GraphCommands(), full: true, simplify: true);
        AssertDiscoveryIsSeparate(scope);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected), "Traversal changes must not turn the selection into a bound or choose another commit.");

        HistoryFilterDialog nested = await OpenFilterAsync(main, shortcut: true);
        Assert.That(Field<CheckBox>(nested, "FullHistory").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(nested, "SimplifyMerges").IsChecked, Is.True);
        Toggle(nested, "FullHistory", false);
        Assert.That(Field<CheckBox>(nested, "SimplifyMerges").IsEnabled, Is.False);
        Assert.That(Field<CheckBox>(nested, "SimplifyMerges").IsChecked, Is.True, "The disabled preference stays stored rather than being reset.");
        scope.ClearTrace(); ClickButton(nested, "OK"); await Idle(main);
        Assert.That(scope.Store.Load().FullHistory, Is.False); Assert.That(scope.Store.Load().SimplifyMerges, Is.True);
        AssertGraphFlags(scope.GraphCommands(), full: false, simplify: false);

        HistoryFilterDialog reset = await OpenFilterAsync(main, shortcut: false);
        Assert.That(Field<CheckBox>(reset, "SimplifyMerges").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(reset, "SimplifyMerges").IsEnabled, Is.False);
        scope.ClearTrace(); ClickButton(reset, "Reset"); await Idle(main);
        Assert.That(scope.Store.Load().FullHistory, Is.False); Assert.That(scope.Store.Load().SimplifyMerges, Is.True);
        Assert.That(scope.DiscoveryCommands(), Is.Empty, "Reset removed the path, not its global history preferences.");
        AssertGraphFlags(scope.GraphCommands(), full: false, simplify: false, rewriteParents: false);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected));
        Assert.That(Snapshot(scope.Fixture.Repository.Path), Is.EqualTo(gitBefore));
    }

    [AvaloniaTest]
    [TestCase("none")]
    [TestCase("branch")]
    [TestCase("dates")]
    [TestCase("reflog")]
    [TestCase("first-parent")]
    public async Task BranchDateReflogAndFirstParentAloneDoNotActivateTheStoredTraversalFlags(string kind)
    {
        await using Scope scope = await Scope.OpenAsync(full: true, simplify: true);
        string before = Snapshot(scope.Fixture.Repository.Path);
        HistoryFilterDialog dialog = await OpenFilterAsync(scope.Window, shortcut: true);
        switch (kind)
        {
            case "branch": SetTextFilter(dialog, "BranchFilter", "main"); break;
            case "dates":
                DatePicker since = Field<DatePicker>(dialog, "Since");
                Grid fields = (Grid)since.Parent!;
                CheckBox enabled = fields.Children.OfType<CheckBox>().Single(control => Grid.GetRow(control) == Grid.GetRow(since) && Grid.GetColumn(control) == 1);
                PushProcessTestDriver.Pointer(enabled); since.SelectedDate = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero); break;
            case "reflog": Toggle(dialog, "Reflog", true); break;
            case "first-parent": Toggle(dialog, "OnlyFirstParent", true); break;
        }
        scope.ClearTrace(); ClickButton(dialog, "OK"); await Idle(scope.Window);
        AssertGraphFlags(scope.GraphCommands(), full: false, simplify: false, rewriteParents: false);
        Assert.That(scope.DiscoveryCommands(), Is.Empty);
        string[] graph = scope.GraphCommands().Single();
        if (kind == "branch") { Assert.That(graph, Does.Contain("main")); }
        if (kind == "dates") { Assert.That(graph.Any(value => value.StartsWith("--since=", StringComparison.Ordinal)), Is.True); }
        if (kind == "reflog") { Assert.That(graph, Does.Contain("--reflog")); }
        if (kind == "first-parent") { Assert.That(graph, Does.Contain("--first-parent")); }
        Assert.That(scope.Store.Load().FullHistory && scope.Store.Load().SimplifyMerges, Is.True, "Inactive settings stay global and survive a query which cannot use them.");
        Assert.That(Reader(scope.Window).Any(), Is.True); Assert.That(Snapshot(scope.Fixture.Repository.Path), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("path")]
    [TestCase("author")]
    [TestCase("committer")]
    [TestCase("message")]
    [TestCase("diff")]
    [TestCase("hide-merges")]
    [TestCase("decorated")]
    public async Task EveryActualWindowsRevisionFilterEnablesTheStoredFullAndSimplifyFlags(string kind)
    {
        await using Scope scope = await Scope.OpenAsync(full: true, simplify: true);
        string before = Snapshot(scope.Fixture.Repository.Path);
        HistoryFilterDialog dialog = await OpenFilterAsync(scope.Window, shortcut: false);
        switch (kind)
        {
            case "path": SetTextFilter(dialog, "PathFilter", "file.txt"); break;
            case "author": SetTextFilter(dialog, "Author", "Reference Tester"); break;
            case "committer": SetTextFilter(dialog, "Committer", "Reference Tester"); break;
            case "message": SetTextFilter(dialog, "Message", "Initial commit"); break;
            case "diff": SetTextFilter(dialog, "DiffContent", "base"); break;
            case "hide-merges": Toggle(dialog, "HideMergeCommits", true); break;
            case "decorated": Toggle(dialog, "SimplifyByDecoration", true); break;
        }
        scope.ClearTrace(); ClickButton(dialog, "OK"); await Idle(scope.Window);
        AssertGraphFlags(scope.GraphCommands(), full: true, simplify: true);
        Assert.That(Reader(scope.Window).Any(), Is.True, "The real Git graph must still publish valid commits for this accepted filter.");
        if (kind == "path") { AssertDiscoveryIsSeparate(scope); }
        else { Assert.That(scope.DiscoveryCommands(), Is.Empty); }
        Assert.That(Snapshot(scope.Fixture.Repository.Path), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheThreeTraversalStatesMatchNativeTreesameHistoryWhileDiscoveryKeepsItsOwnCommand()
    {
        await using Scope scope = await Scope.OpenAsync(path: "file.txt", buildDag: true);
        string before = Snapshot(scope.Fixture.Repository.Path);
        string merge = (await scope.NativeAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        Assert.That((await scope.NativeAsync("rev-parse", merge + ":file.txt")).StandardOutput,
            Is.EqualTo((await scope.NativeAsync("rev-parse", merge + "^1:file.txt")).StandardOutput), "The merge fixture must actually be TREESAME to its first parent.");
        List<string> signatures = [];
        foreach ((bool full, bool simplify) in new[] { (false, false), (true, false), (true, true) })
        {
            HistoryFilterDialog dialog = await OpenFilterAsync(scope.Window, shortcut: true);
            // Full=false legitimately retains a previously true Simplify value, but must never emit it.
            Toggle(dialog, "FullHistory", true); Toggle(dialog, "SimplifyMerges", simplify); Toggle(dialog, "FullHistory", full);
            scope.ClearTrace(); ClickButton(dialog, "OK"); await Idle(scope.Window);
            AssertGraphFlags(scope.GraphCommands(), full, full && simplify); AssertDiscoveryIsSeparate(scope);
            string[] traversal = full ? simplify ? ["--full-history", "--simplify-merges"] : ["--full-history"] : [];
            string native = (await scope.NativeAsync(["log", "--parents", "--format=%H%x00%P", .. traversal, "--all", "--", "file.txt"])).StandardOutput;
            string expected = string.Join('\n', native.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            string actual = string.Join('\n', Reader(scope.Window).Select(commit => commit.Hash + '\0' + string.Join(' ', commit.Parents)));
            Assert.That(actual, Is.EqualTo(expected), "The raw reader commits and rewritten parents must match native Git; graph screen ordering has its own Windows algorithm.");
            signatures.Add(expected);
        }
        Assert.That(signatures.Distinct().Count(), Is.GreaterThan(1), "The fixture must distinguish full traversal rather than merely exercising equivalent flags.");
        Assert.That(Snapshot(scope.Fixture.Repository.Path), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ClosingAfterAdvancedOkDrainsTheActuallyBlockedTraversalAndItsDescendant()
    {
        await using Scope scope = await Scope.OpenAsync(path: "file.txt");
        string before = Snapshot(scope.Fixture.Repository.Path);
        HistoryFilterDialog dialog = await OpenFilterAsync(scope.Window, shortcut: false);
        Toggle(dialog, "FullHistory", true); Toggle(dialog, "SimplifyMerges", true);
        File.WriteAllText(scope.Gate, "hold the real traversal"); ClickButton(dialog, "OK");
        int[]? observed = null;
        await Until(() =>
        {
            try { observed = JsonSerializer.Deserialize<int[]>(File.ReadAllText(scope.Marker)); return observed is [> 0, > 0]; }
            catch (Exception error) when (error is IOException or JsonException) { return false; }
        });
        int[] pids = observed!;
        using Process parent = Process.GetProcessById(pids[0]), child = Process.GetProcessById(pids[1]);
        Task readers = scope.Window.WindowWorkCompletion; scope.Window.Close();
        await Until(() => !scope.Window.IsVisible && readers.IsCompleted && parent.HasExited && child.HasExited);
        await readers;
        Assert.That(scope.Store.Load().FullHistory && scope.Store.Load().SimplifyMerges, Is.True, "A completed settings OK is not rolled back by cancelling its subsequent read.");
        Assert.That(scope.Window.LastException, Is.Null); Assert.That(Snapshot(scope.Fixture.Repository.Path), Is.EqualTo(before));
    }

    private static void AssertGraphFlags(IReadOnlyList<string[]> commands, bool full, bool simplify, bool rewriteParents = true)
    {
        Assert.That(commands, Is.Not.Empty, "No actual graph log command was captured.");
        foreach (string[] command in commands)
        {
            Assert.That(command.Contains("--full-history", StringComparer.Ordinal), Is.EqualTo(full));
            Assert.That(command.Contains("--simplify-merges", StringComparer.Ordinal), Is.EqualTo(simplify));
            Assert.That(command.Contains("--parents", StringComparer.Ordinal), Is.EqualTo(rewriteParents), "Only actual revision filters rewrite graph parents.");
            Assert.That(command.Contains("--follow", StringComparer.Ordinal), Is.False, "Browse's graph is never the direct --follow reader.");
        }
    }
    private static void AssertDiscoveryIsSeparate(Scope scope)
    {
        IReadOnlyList<string[]> discovery = scope.DiscoveryCommands(); Assert.That(discovery, Has.Count.EqualTo(1));
        string[] command = discovery.Single();
        Assert.That(command, Does.Contain("--follow").And.Contain("--find-renames").And.Contain("--find-copies").And.Contain("--literal-pathspecs"));
        Assert.That(command.Any(value => value is "--full-history" or "--simplify-merges" or "--find-copies-harder" or "--all" || value.StartsWith("--max-count=", StringComparison.Ordinal)), Is.False);
    }
    private static IReadOnlyList<GitCommit> Reader(MainWindow main) => main.GetVisualDescendants().OfType<CommitGraph>().Single().Commits;
    private static T Field<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>()
        .Concat(root.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);
    private static void Toggle(HistoryFilterDialog dialog, string name, bool value)
    {
        CheckBox box = Field<CheckBox>(dialog, name);
        if (box.IsChecked != value) { PushProcessTestDriver.Pointer(box); }
        Assert.That(box.IsChecked, Is.EqualTo(value), name);
    }
    private static void SetTextFilter(HistoryFilterDialog dialog, string name, string value)
    {
        CheckBox check = Field<CheckBox>(dialog, name + "Check");
        if (check.IsChecked != true) { PushProcessTestDriver.Pointer(check); }
        Field<TextBox>(dialog, name).Text = value;
    }
    private static void ClickButton(HistoryFilterDialog dialog, string label)
        => PushProcessTestDriver.Pointer(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == label));
    private static async Task<HistoryFilterDialog> OpenFilterAsync(MainWindow main, bool shortcut)
    {
        await Idle(main);
        if (shortcut)
        {
            ListBox history = Field<ListBox>(main, "RevisionHistory");
            object selected = history.SelectedItem ?? throw new InvalidOperationException("The loaded history must have a selected revision.");
            history.ScrollIntoView(selected); main.UpdateLayout();
            PushProcessTestDriver.Pointer(history.ContainerFromItem(selected)!);
            Press(main, PhysicalKey.I, RawInputModifiers.Control);
        }
        else { PushProcessTestDriver.Pointer(Field<Button>(main, "BrowseAdvancedFilter")); }
        await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single(); Pump(dialog); return dialog;
    }
    private static void Press(Window target, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, Window? release = null)
    {
        target.KeyPressQwerty(key, modifiers); Window recipient = release ?? target;
        if (recipient.IsVisible) { recipient.KeyReleaseQwerty(key, modifiers); }
        Dispatcher.UIThread.RunJobs();
    }
    private static void Pump(TopLevel top) { Dispatcher.UIThread.RunJobs(); top.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow main)
    {
        await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
        await main.WindowWorkCompletion; Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) { Assert.Fail("The traversal settings/native reader did not settle."); } Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }
    private static string Snapshot(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));

    private sealed class Scope(Fixture fixture, MainWindow window, FileHistoryPreferencesStore store, string trace, string gate, string marker) : IAsyncDisposable
    {
        internal Fixture Fixture => fixture;
        internal MainWindow Window => window;
        internal FileHistoryPreferencesStore Store => store;
        internal string Gate => gate;
        internal string Marker => marker;
        internal void ClearTrace() => File.WriteAllText(trace, "");
        internal string[][] Commands() => File.Exists(trace) ? File.ReadAllLines(trace).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray() : [];
        internal IReadOnlyList<string[]> GraphCommands() => Commands().Where(command => command.Contains("log", StringComparer.Ordinal)
            && command.Any(value => value.StartsWith("--format=%H%x00%P%x00%aN", StringComparison.Ordinal))).ToArray();
        internal IReadOnlyList<string[]> DiscoveryCommands() => Commands().Where(command => command.Contains("log", StringComparer.Ordinal) && command.Contains("--name-only", StringComparer.Ordinal)).ToArray();
        internal async Task<GitResult> NativeAsync(params string[] arguments)
        {
            GitResult result = await new GitRunner("/usr/bin/git").RunAsync(fixture.Repository.Path, ["--no-optional-locks", "--literal-pathspecs", .. arguments]);
            Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        internal static async Task<Scope> OpenAsync(string? path = null, bool full = false, bool simplify = false, bool dark = false, bool secondCommit = false, bool buildDag = false)
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("The real Git argv/gated process fixtures run on Linux."); throw new PlatformNotSupportedException(); }
            Fixture fixture = await ReferenceDialogTests.ReferenceFixture.CreateAsync(); MainWindow? main = null;
            try
            {
                if (buildDag)
                {
                    await fixture.GitAsync("checkout", "-b", "treesame-side");
                    await File.WriteAllTextAsync(fixture.PathInRepository("side-only.txt"), "not the followed file\n");
                    await fixture.Repository.StageAllAsync(); await fixture.GitAsync("commit", "-m", "Unrelated side");
                    await fixture.GitAsync("checkout", "main"); await fixture.ChangeAsync("changed on main\n", "Followed main file");
                    await fixture.GitAsync("merge", "--no-ff", "treesame-side", "-m", "TREESAME merge");
                }
                else if (secondCommit) { await fixture.ChangeAsync("second followed file\n", "Second followed file"); }
                FileHistoryPreferencesStore store = new();
                using JsonDocument future = JsonDocument.Parse("\"preserve me\"");
                store.Save(new() { FullHistory = full, SimplifyMerges = simplify,
                    AdditionalSettings = new() { ["UnrelatedFutureValue"] = future.RootElement.Clone() } });
                string bin = fixture.Output("trace-bin"), trace = fixture.Output("git-argv.jsonl"), gate = fixture.Output("hold-traversal"), marker = fixture.Output("native-pids.json");
                Directory.CreateDirectory(bin); string wrapper = Path.Combine(bin, "git");
                await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport json,os,subprocess,sys\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(trace) + ", 'a', encoding='utf-8') as f: f.write(json.dumps(a)+'\\n')\nif 'log' in a and '--full-history' in a and os.path.exists(" + JsonSerializer.Serialize(gate) + "):\n child=subprocess.Popen(['/bin/sleep','60'])\n with open(" + JsonSerializer.Serialize(marker) + ", 'w') as f: json.dump([os.getpid(),child.pid],f)\n child.wait()\nos.execv('/usr/bin/git',['git',*a])\n");
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                fixture.SetEnvironment("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
                main = new(new BrowseRequest(PathFilter: path is null ? null : new(path, BrowsePathKind.File)))
                { Width = 1440, Height = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
                main.Show(); await main.OpenRepositoryAsync(fixture.Repository.Path); await Idle(main);
                return new(fixture, main, store, trace, gate, marker);
            }
            catch
            {
                if (main is not null) { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await main.WindowWorkCompletion; }
                fixture.Dispose(); throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
            window.Close(); await Until(() => !window.IsVisible && window.WindowWorkCompletion.IsCompleted);
            await window.WindowWorkCompletion; fixture.Dispose();
        }
    }
}
