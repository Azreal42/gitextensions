using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Git;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class GeneralBrowseStatusTests
{
    [Test]
    public async Task PushDestinationTakesPrecedenceOverADifferentUpstreamAndItsBehindCountUsesTheWindowsDisplay()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.ConfigureTrackingAsync(differentPush: true);
        string before = await f.MaterialAsync(); f.ClearLog();
        GitBranchTrackingStatus? tracking = await f.Git.Repository.GetBranchTrackingStatusAsync("main");
        Assert.That(tracking, Is.Not.Null);
        Assert.That(tracking!.RemoteRef, Is.EqualTo("refs/remotes/publish/main"));
        Assert.That(tracking.AheadCount, Is.Empty); Assert.That(tracking.BehindCount, Is.EqualTo("1"));
        Assert.That(tracking.Display, Is.EqualTo("1↓"));
        Assert.That(tracking.Display, Is.EqualTo(new AheadBehindData("main", tracking.RemoteRef, "", "1").ToDisplay()));
        Assert.That(f.Commands().Count(IsTrackingRead), Is.EqualTo(1));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(before));
    }

    [Test]
    public async Task MissingPushTrackingFallsBackToUpstreamRatherThanTreatingAnUncreatedPushRefAsTheRemote()
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureTrackingAsync(differentPush: true);
        await f.NativeAsync(f.Git.Repository, "update-ref", "-d", "refs/remotes/publish/main");
        GitBranchTrackingStatus? tracking = await f.Git.Repository.GetBranchTrackingStatusAsync("main");
        Assert.That(tracking, Is.Not.Null); Assert.That(tracking!.RemoteRef, Is.EqualTo("refs/remotes/origin/main"));
        Assert.That(tracking.AheadCount, Is.EqualTo("1")); Assert.That(tracking.BehindCount, Is.Empty);
        Assert.That(tracking.Display, Is.EqualTo("1↑"));
    }

    [Test]
    [TestCase("same", "0", "", "0↑↓")]
    [TestCase("ahead", "1", "", "1↑")]
    [TestCase("behind", "", "1", "1↓")]
    [TestCase("diverged", "1", "1", "1↑ 1↓")]
    [TestCase("gone", "gone", "", "✗")]
    public async Task RealTrackingRefsDistinguishSynchronizedAheadBehindDivergedAndGone(string mode, string ahead, string behind, string display)
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureTrackingAsync(differentPush: false);
        if (mode == "same") { await f.NativeAsync(f.Git.Repository, "update-ref", "refs/remotes/origin/main", f.Local); }
        else if (mode == "behind")
        {
            await f.NativeAsync(f.Git.Repository, "update-ref", "refs/remotes/origin/main", f.Future);
        }
        else if (mode == "diverged")
        {
            await f.NativeAsync(f.Git.Repository, "checkout", "--detach", f.Initial);
            await File.WriteAllTextAsync(f.Git.File, "upstream sibling\n");
            await f.NativeAsync(f.Git.Repository, "add", "--", "file.txt"); await f.NativeAsync(f.Git.Repository, "commit", "-m", "Upstream sibling");
            string sibling = await f.Git.Repository.ResolveRevisionAsync("HEAD");
            await f.NativeAsync(f.Git.Repository, "update-ref", "refs/remotes/origin/main", sibling);
            await f.NativeAsync(f.Git.Repository, "checkout", "main");
        }
        else if (mode == "gone") { await f.NativeAsync(f.Git.Repository, "update-ref", "-d", "refs/remotes/origin/main"); }
        GitBranchTrackingStatus? tracking = await f.Git.Repository.GetBranchTrackingStatusAsync("main");
        Assert.That(tracking, Is.Not.Null); Assert.That(tracking!.RemoteRef, Is.EqualTo("refs/remotes/origin/main"));
        Assert.That(tracking.AheadCount, Is.EqualTo(ahead)); Assert.That(tracking.BehindCount, Is.EqualTo(behind));
        Assert.That(tracking.Display, Is.EqualTo(display)); Assert.That(tracking.IsGone, Is.EqualTo(mode == "gone"));
    }

    [Test]
    public async Task UntrackedOrMissingBranchesHaveNoInventedZeroCountsAndDetachedDoesNotReadTrackingRefs()
    {
        await using Scope f = await Scope.CreateAsync();
        Assert.That(await f.Git.Repository.GetBranchTrackingStatusAsync("main"), Is.Null);
        Assert.That(await f.Git.Repository.GetBranchTrackingStatusAsync("missing"), Is.Null);
        await f.NativeAsync(f.Git.Repository, "checkout", "--detach", "HEAD"); f.ClearLog();
        Assert.That(await f.Git.Repository.GetBranchTrackingStatusAsync("(detached)"), Is.Null);
        Assert.That(await f.Git.Repository.GetBranchTrackingStatusAsync(""), Is.Null);
        Assert.That(f.Commands(), Is.Empty);
    }

    [Test]
    [TestCase("same", GitSubmoduleBrowseState.SameCommit, false, 0, 0)]
    [TestCase("dirty", GitSubmoduleBrowseState.SameCommit, true, 0, 0)]
    [TestCase("forward", GitSubmoduleBrowseState.FastForward, false, 1, 0)]
    [TestCase("forward-dirty", GitSubmoduleBrowseState.FastForward, true, 1, 0)]
    [TestCase("rewind", GitSubmoduleBrowseState.Rewind, false, 0, 1)]
    [TestCase("newer", GitSubmoduleBrowseState.NewerTime, false, 1, 1)]
    [TestCase("older", GitSubmoduleBrowseState.OlderTime, false, 1, 1)]
    public async Task SubmoduleStatusUsesTheIndexedGitlinkAndRealDagBeforeFallingBackToCommitDates(string mode,
        GitSubmoduleBrowseState expected, bool dirty, int added, int removed)
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureModuleAsync(mode);
        string before = await f.MaterialAsync(includeModule: true); f.ClearLog();
        GitSubmoduleBrowseStatus status = (await f.Git.Repository.GetSubmoduleBrowseStatusesAsync()).Single();
        Assert.That(status.Path, Is.EqualTo(Scope.ModulePath)); Assert.That(status.State, Is.EqualTo(expected), status.Error);
        Assert.That(status.IsDirty, Is.EqualTo(dirty)); Assert.That(status.AddedCommits, Is.EqualTo(added)); Assert.That(status.RemovedCommits, Is.EqualTo(removed));
        Assert.That(status.CountSuffix, Is.EqualTo(added == 0 && removed == 0 ? "" : $" (+{added}-{removed})"));
        Assert.That(f.Commands().Any(command => command.Cwd == f.Module!.Path && command.Arguments.Contains("status")), Is.True);
        Assert.That(f.Commands().Any(command => command.Arguments.Contains("rev-list")), Is.EqualTo(mode is not ("same" or "dirty")));
        Assert.That(await f.MaterialAsync(includeModule: true), Is.EqualTo(before), "A Browse status read must leave both indexes, HEADs and worktrees intact.");
    }

    [Test]
    public async Task DeinitializedSubmoduleHasAnExplicitStatusAndIsNeverOpenedAsItsParentRepository()
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureModuleAsync("same");
        await f.NativeAsync(f.Git.Repository, "--literal-pathspecs", "submodule", "deinit", "--force", "--", Scope.ModulePath); f.ClearLog();
        GitSubmoduleBrowseStatus status = (await f.Git.Repository.GetSubmoduleBrowseStatusesAsync()).Single();
        Assert.That(status.State, Is.EqualTo(GitSubmoduleBrowseState.NotInitialized)); Assert.That(status.IsDirty, Is.False);
        Assert.That(status.AddedCommits, Is.Null); Assert.That(status.RemovedCommits, Is.Null);
        Assert.That(f.Commands().Any(command => command.Cwd == f.Module!.Path && command.Arguments.Contains("status")), Is.False);
    }

    [AvaloniaTest]
    public async Task BrowsePushUsesItsActualPushTargetCountTooltipAndIconThenSettingsTurnsOffTheOptionalRead()
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureTrackingAsync(differentPush: true);
        MainWindow main = await f.ShowAsync(new() { ShowAheadBehindData = true });
        Button push = Field<Button>(main, "BrowsePush");
        Assert.That(Text(push.Content), Is.EqualTo("1↓")); Assert.That(Icon(push.Content), Is.EqualTo("Unstage.png"));
        Assert.That(ToolTip.GetTip(push)?.ToString(), Does.Contain("refs/remotes/publish/main").And.Contain("1 commit(s) should be integrated"));
        string before = await f.MaterialAsync(); f.ClearLog();
        await f.ChangeSettingAsync("chkShowAheadBehindDataInBrowseWindow");
        Assert.That(Text(push.Content), Is.Empty); Assert.That(Icon(push.Content), Is.EqualTo("Push.png"));
        Assert.That(push.Width, Is.EqualTo(24)); Assert.That(f.Commands().Any(IsTrackingRead), Is.False);
        Assert.That(new PreferencesStore().Load().ShowAheadBehindData, Is.False);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(true, false, true, false)]
    [TestCase(true, true, true, false)]
    [TestCase(true, true, false, true)]
    public async Task OptionalReadsRespectTheFlagsWhileIndependentCountsDoNotHideArtificialRevisions(bool ahead, bool submodules, bool toolbarCount, bool artificialCount)
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureTrackingAsync(differentPush: false); await f.ConfigureModuleAsync("forward-dirty");
        await File.WriteAllTextAsync(f.Git.File, "staged\n"); await f.NativeAsync(f.Git.Repository, "add", "--", "file.txt"); await File.WriteAllTextAsync(f.Git.File, "staged and working\n");
        int changed = (await f.Git.Repository.GetStatusAsync()).Changes.Count;
        string before = await f.MaterialAsync(includeModule: true); f.ClearLog();
        MainWindow main = await f.ShowAsync(new() { ShowAheadBehindData = ahead, ShowSubmoduleStatus = submodules,
            ShowGitStatusInBrowseToolbar = toolbarCount, ShowGitStatusForArtificialCommits = artificialCount });
        Assert.That(f.Commands().Any(IsTrackingRead), Is.EqualTo(ahead));
        bool detailReads = submodules && (toolbarCount || artificialCount);
        Assert.That(f.Commands().Any(command => command.Cwd == f.Module!.Path && command.Arguments.Contains("status")), Is.EqualTo(detailReads));
        Assert.That(f.Commands().Any(command => command.Arguments.Contains("rev-list") && command.Arguments.Contains("--left-right")), Is.EqualTo(detailReads));
        Button working = Field<Button>(main, "WorkingDirectoryRevision"), index = Field<Button>(main, "IndexRevision");
        Assert.That(working.IsEffectivelyVisible && index.IsEffectivelyVisible, Is.True);
        foreach (Button label in new[] { working, index })
        {
            ListBoxItem row = label.FindAncestorOfType<ListBoxItem>()!;
            Assert.That(row.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "ArtificialRevisionStatus" && control.IsEffectivelyVisible), Is.EqualTo(artificialCount));
        }
        string commit = Text(Field<Button>(main, "BrowseCommit").Content);
        Assert.That(commit, Is.EqualTo(toolbarCount ? "Commit (" + changed + ")" : "Commit"));
        TreeViewItem module = ModuleNode(main);
        Assert.That(Icon(module.Header), Is.EqualTo(detailReads ? "SubmoduleRevisionUpDirty.png" : "FolderSubmodule.png"));
        Assert.That(Text(module.Header), Is.EqualTo(Scope.ModulePath + (detailReads ? " (+1-0)" : "")));
        Assert.That(await f.MaterialAsync(includeModule: true), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("forward", "SubmoduleRevisionUp.png", " (+1-0)")]
    [TestCase("rewind", "SubmoduleRevisionDown.png", " (+0-1)")]
    [TestCase("dirty", "SubmoduleDirty.png", "")]
    [TestCase("newer", "SubmoduleRevisionSemiUp.png", " (+1-1)")]
    [TestCase("older", "SubmoduleRevisionSemiDown.png", " (+1-1)")]
    public async Task BrowseSubmoduleHeadersUseTheOriginalIconsAndCommitCounts(string mode, string icon, string suffix)
    {
        await using Scope f = await Scope.CreateAsync(); await f.ConfigureModuleAsync(mode);
        MainWindow main = await f.ShowAsync(new() { ShowAheadBehindData = false, ShowSubmoduleStatus = true });
        TreeViewItem node = ModuleNode(main);
        Assert.That(Icon(node.Header), Is.EqualTo(icon)); Assert.That(Text(node.Header), Is.EqualTo(Scope.ModulePath + suffix));
        if (mode == "dirty") { Assert.That(ToolTip.GetTip(node)?.ToString(), Does.Contain("Local changes")); }
        else { Assert.That(ToolTip.GetTip(node)?.ToString(), Is.Not.Empty); }
    }

    [AvaloniaTest]
    public async Task StashCountExpandsTheRealSplitControlAndItsOverflowThenDisablingRestoresCompactGeometry()
    {
        await using Scope f = await Scope.CreateAsync();
        for (int i = 0; i < 12; i++)
        {
            await File.WriteAllTextAsync(f.Git.File, "stash payload " + i + "\n");
            await f.NativeAsync(f.Git.Repository, "stash", "push", "-m", "count " + i);
        }
        MainWindow main = await f.ShowAsync(new() { ShowStashCount = true, ShowAheadBehindData = false });
        Button stash = Field<Button>(main, "BrowseStash"), arrow = Field<Button>(main, "BrowseStashDropdown"); Grid split = Field<Grid>(main, "BrowseStashControl");
        Assert.That(Text(stash.Content), Is.EqualTo("(12)")); Assert.That(stash.Bounds.Width, Is.GreaterThan(24));
        Assert.That(split.Bounds.Width, Is.EqualTo(stash.Bounds.Width + 10).Within(1));
        Assert.That(arrow.TranslatePoint(default, split)!.Value.X, Is.EqualTo(stash.Bounds.Width).Within(1));
        string before = await f.MaterialAsync();
        CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
        toolbar.Width = 520; Pump(main); toolbar.Place(); Pump(main);
        Pointer(toolbar.Overflow); Pump(main);
        MenuItem overflow = toolbar.OverflowFlyout.Items.OfType<MenuItem>().Single(item => item.Name == "StashOverflow");
        Assert.That(overflow.IsEffectivelyVisible, Is.True); Assert.That(overflow.Header, Is.EqualTo("Stash (12)"));
        Assert.That(overflow.Items.OfType<MenuItem>().Any(item => item.Name == "StashOverflowManage"), Is.True);
        toolbar.OverflowFlyout.Hide(); toolbar.Width = double.NaN; Pump(main);
        await f.ChangeSettingAsync("chkShowStashCountInBrowseWindow");
        Assert.That(Text(stash.Content), Is.Empty); Assert.That(stash.Bounds.Width, Is.EqualTo(24)); Assert.That(split.Bounds.Width, Is.EqualTo(34));
        Assert.That(Private<MenuItem>(main, "_stashOverflow").Header, Is.EqualTo("Stash"));
        Assert.That((await f.Git.Repository.GetStashesAsync()).Count, Is.EqualTo(12)); Assert.That(await f.MaterialAsync(), Is.EqualTo(before));
    }

    private static bool IsTrackingRead(Command command) => command.Arguments.Any(argument => argument.Contains("%(push:track,nobracket)", StringComparison.Ordinal));
    private static TreeViewItem ModuleNode(MainWindow main)
        => Field<TreeView>(main, "RepositoryNavigation").GetVisualDescendants().OfType<TreeViewItem>()
            .Single(item => Text(item.Header).StartsWith(Scope.ModulePath, StringComparison.Ordinal));
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Private<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static string Text(object? content) => content switch
    {
        string text => text, TextBlock block => block.Text ?? "",
        Control control => string.Concat(control.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)), _ => ""
    };
    private static string? Icon(object? content) => content is Image image ? image.Tag as string
        : (content as Control)?.GetVisualDescendants().OfType<Image>().Select(image => image.Tag as string).FirstOrDefault();
    private static void Pointer(Control control)
    {
        TopLevel window = TopLevel.GetTopLevel(control)!; control.BringIntoView(); Pump(window);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && new Rect(default, window.ClientSize).Contains(point), Is.True,
            $"{control.Name}: visible={control.IsEffectivelyVisible}, enabled={control.IsEffectivelyEnabled}, point={point}, client={window.ClientSize}");
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, control.Name);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
    }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> done)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(30)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(done(), Is.True, "The real repository/UI operation did not finish.");
    }
    private sealed record Command(string Cwd, string[] Arguments);

    private sealed class Scope : IAsyncDisposable
    {
        internal const string ModulePath = "modules/status [x]* λ";
        private readonly GitEnvironmentOptions _previous;
        internal RootFixture Git { get; }
        internal GitRepository? Module { get; private set; }
        internal MainWindow? Main { get; private set; }
        internal string Initial { get; private set; } = "";
        internal string Local { get; private set; } = "";
        internal string Future { get; private set; } = "";
        private string Log => Git.Output("status-argv.jsonl");
        private Scope(RootFixture git, GitEnvironmentOptions previous) { Git = git; _previous = previous; }
        internal static async Task<Scope> CreateAsync()
        {
            GitEnvironmentOptions previous = GitEnvironment.Options; GitEnvironment.Configure(new());
            RootFixture? root = null;
            try
            {
                root = await RootFixture.CreateAsync(); Scope scope = new(root, previous);
                string global = root.Output("private.gitconfig"); await File.WriteAllTextAsync(global, "[protocol \"file\"]\n\tallow = always\n");
                root.SetEnvironment("GIT_CONFIG_GLOBAL", global); root.SetEnvironment("GIT_ALLOW_PROTOCOL", "file"); root.SetEnvironment("GIT_TERMINAL_PROMPT", "0");
                string wrapper = root.Output("record-native-git");
                await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport os,sys,json\nwith open(" + JsonSerializer.Serialize(scope.Log)
                    + ", 'a', encoding='utf-8') as f: f.write(json.dumps({'cwd':os.getcwd(),'argv':sys.argv[1:]},ensure_ascii=False)+'\\n')\nos.execv('/usr/bin/git',['git']+sys.argv[1:])\n");
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                GitEnvironment.Configure(new() { Executable = wrapper }); scope.Initial = await root.Repository.ResolveRevisionAsync("HEAD");
                return scope;
            }
            catch { root?.Dispose(); GitEnvironment.Configure(previous); throw; }
        }
        internal async Task<GitResult> NativeAsync(GitRepository repository, params string[] arguments)
        {
            GitResult result = await new GitRunner("/usr/bin/git").RunAsync(repository.Path, arguments);
            Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        internal async Task ConfigureTrackingAsync(bool differentPush)
        {
            await Git.ChangeAsync("local\n", "Local change"); Local = await Git.Repository.ResolveRevisionAsync("HEAD");
            await Git.ChangeAsync("future\n", "Future remote change"); Future = await Git.Repository.ResolveRevisionAsync("HEAD");
            await NativeAsync(Git.Repository, "reset", "--hard", Local);
            await NativeAsync(Git.Repository, "remote", "add", "origin", Git.Output("origin.git"));
            await NativeAsync(Git.Repository, "update-ref", "refs/remotes/origin/main", Initial);
            await NativeAsync(Git.Repository, "config", "branch.main.remote", "origin"); await NativeAsync(Git.Repository, "config", "branch.main.merge", "refs/heads/main");
            if (differentPush)
            {
                await NativeAsync(Git.Repository, "remote", "add", "publish", Git.Output("publish.git"));
                await NativeAsync(Git.Repository, "config", "branch.main.pushRemote", "publish"); await NativeAsync(Git.Repository, "config", "push.default", "current");
                await NativeAsync(Git.Repository, "update-ref", "refs/remotes/publish/main", Future);
            }
        }
        internal async Task ConfigureModuleAsync(string mode)
        {
            GitRepository source = await GitRepository.InitAsync(Git.Output("module-source"), new GitInitOptions("main"));
            await NativeAsync(source, "config", "user.name", "Submodule status tester"); await NativeAsync(source, "config", "user.email", "module-status@example.invalid");
            await NativeAsync(source, "config", "commit.gpgsign", "false"); await NativeAsync(source, "config", "core.hooksPath", Git.Output("no-hooks"));
            await CommitModuleAsync(source, "base module\n", "2020-01-01T00:00:00+00:00");
            await NativeAsync(Git.Repository, "submodule", "add", "--name", "status", "--", source.Path, ModulePath);
            await NativeAsync(Git.Repository, "--literal-pathspecs", "add", "--", ".gitmodules", ModulePath); await NativeAsync(Git.Repository, "commit", "-m", "Record module");
            Module = new GitRepository(Git.PathInRepository(ModulePath));
            await NativeAsync(Module, "config", "user.name", "Submodule status tester"); await NativeAsync(Module, "config", "user.email", "module-status@example.invalid");
            await NativeAsync(Module, "config", "commit.gpgsign", "false"); await NativeAsync(Module, "config", "core.hooksPath", Git.Output("no-hooks"));
            string initial = await Module.ResolveRevisionAsync("HEAD");
            if (mode is not ("same" or "dirty"))
            {
                await CommitModuleAsync(Module, "forward module\n", "2025-01-01T00:00:00+00:00");
                if (mode is "rewind" or "newer" or "older")
                {
                    await NativeAsync(Git.Repository, "--literal-pathspecs", "add", "--", ModulePath); await NativeAsync(Git.Repository, "commit", "-m", "Advance indexed module");
                    await NativeAsync(Module, "checkout", "--detach", initial);
                    if (mode is "newer" or "older")
                    { await CommitModuleAsync(Module, "sibling module\n", mode == "newer" ? "2026-01-01T00:00:00+00:00" : "2024-01-01T00:00:00+00:00"); }
                }
            }
            if (mode is "dirty" or "forward-dirty") { await File.WriteAllTextAsync(Path.Combine(Module.Path, "module.txt"), "dirty module\n"); }
        }
        private async Task CommitModuleAsync(GitRepository module, string text, string date)
        {
            await File.WriteAllTextAsync(Path.Combine(module.Path, "module.txt"), text); await NativeAsync(module, "add", "--", "module.txt");
            Git.SetEnvironment("GIT_AUTHOR_DATE", date); Git.SetEnvironment("GIT_COMMITTER_DATE", date);
            try { await NativeAsync(module, "commit", "-m", text.Trim()); }
            finally { Git.SetEnvironment("GIT_AUTHOR_DATE", null); Git.SetEnvironment("GIT_COMMITTER_DATE", null); }
        }
        internal void ClearLog() => File.WriteAllText(Log, "");
        internal IReadOnlyList<Command> Commands() => !File.Exists(Log) ? [] : File.ReadAllLines(Log).Where(line => line.Length > 0).Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line); JsonElement value = document.RootElement;
            return new Command(value.GetProperty("cwd").GetString()!, value.GetProperty("argv").EnumerateArray().Select(argument => argument.GetString()!).ToArray());
        }).ToArray();
        internal async Task<string> MaterialAsync(bool includeModule = false)
        {
            List<string> material = [];
            foreach (GitRepository repository in includeModule && Module is not null ? new[] { Git.Repository, Module! } : new[] { Git.Repository })
            {
                foreach (string[] args in new[] { new[] { "rev-parse", "HEAD" }, ["status", "--porcelain=v2", "-z", "--untracked-files=all"], ["ls-files", "--stage", "-z"], ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(upstream)"] })
                { material.Add((await NativeAsync(repository, args)).StandardOutput); }
            }
            return string.Join('\0', material);
        }
        internal async Task<MainWindow> ShowAsync(DesktopPreferences preferences)
        {
            new PreferencesStore().Save(preferences with { Language = "English", HistoryLimit = 30 });
            Main = new MainWindow(new BrowseRequest(Git.Repository.Path)) { Width = 1800, Height = 1000 };
            Main.Show(); await Until(() => !Main.IsBusy && Private<GitRepository?>(Main, "_repository") is not null
                && Main.HistoryLoadingCompletion.IsCompleted);
            await Main.HistoryLoadingCompletion; Pump(Main);
            Assert.That(Main.LastException, Is.Null, Main.LastException?.ToString()); return Main;
        }
        internal async Task ChangeSettingAsync(string name)
        {
            MainWindow main = Main!;
            main.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
            await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
            DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
            await Until(() => Field<Button>(settings, "ApplyDesktopSettings").IsEnabled);
            SettingsNavigationTestDriver.Select(settings, "General"); Pointer(Field<CheckBox>(settings, name));
            await Until(() => Field<Button>(settings, "ApplyDesktopSettings").IsEffectivelyEnabled);
            Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
            await Until(() => Field<Button>(settings, "ApplyDesktopSettings").IsEnabled && !main.IsBusy);
            Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
            Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Until(() => !settings.IsVisible); Pump(main);
        }
        public async ValueTask DisposeAsync()
        {
            if (Main is not null)
            {
                foreach (Window child in Main.OwnedWindows.ToArray()) { child.Close(); } Main.Close();
                await Until(() => !Main.IsVisible && !Main.IsBusy);
            }
            GitEnvironment.Configure(_previous); Git.Dispose();
        }
    }
}
