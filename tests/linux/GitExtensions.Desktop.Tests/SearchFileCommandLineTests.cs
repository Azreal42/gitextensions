using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using DesktopProgram = GitExtensions.Linux.Desktop.Program;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class SearchFileCommandLineTests
{
    [Test]
    public void SearchfileKeepsOnlyTheLiteralSecondArgumentAndItsOriginalCaseSensitiveVerb()
    {
        foreach (string? path in new string?[] { null, "", " \t ", "--help", "--version", "--smoke-test", "-", "literal [*] λ;$()\"" })
        {
            DesktopStartupArguments request = DesktopStartupArguments.Parse(path is null ? ["searchfile"] : ["searchfile", path, "ignored", "--help"]);
            Assert.That(request.Mode, Is.EqualTo(DesktopStartupMode.SearchFile)); Assert.That(request.RepositoryArgument, Is.EqualTo(path));
            Assert.That(request.SmokeTest, Is.False); Assert.That(request.BrowseRequest, Is.Null);
        }
        Assert.That(DesktopStartupArguments.IsSearchFileInvocation(["SearchFile"]), Is.False);
        Assert.That(DesktopStartupArguments.Parse(["./searchfile"]).Mode, Is.EqualTo(DesktopStartupMode.Browse));
        Assert.Throws<InvalidOperationException>(() => DesktopStartupDispatcher.CreateMainWindow(new(new(DesktopStartupMode.SearchFile))));
    }

    [Test]
    public async Task DiscoveryUsesTheExplicitFileOrNestedRepositoryBeforeTheInvokingCwdAndNeverMru()
    {
        await using Scope f = await Scope.CreateAsync();
        string target = f.Git.Output("literal repository λ [*];$()\"");
        GitRepository other = await GitRepository.InitAsync(target); Directory.CreateDirectory(Path.Combine(target, "nested"));
        string literalFile = Path.Combine(target, "nested", "file\" λ.txt"); await File.WriteAllTextAsync(literalFile, "target\n");
        string before = f.Materials();
        foreach (string path in new[] { target, literalFile, Path.Combine(target, "missing/directory/file.txt"), Path.GetRelativePath(f.Repository.Path, literalFile) })
        {
            DesktopStartupRequest request = await Prepare(path, f.Repository.Path);
            Assert.That(request.Repository!.Path, Is.EqualTo(other.Path)); Assert.That(request.BrowseRequest, Is.Null);
        }
        foreach (string? path in new string?[] { null, "", " \t", "--help", f.Git.Output("not-a-repository") })
        { Assert.That((await Prepare(path, f.Repository.Path)).Repository!.Path, Is.EqualTo(f.Repository.Path)); }
        Assert.ThrowsAsync<ArgumentException>(() => Prepare("bad\0path", f.Repository.Path));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["searchfile", f.Repository.Path]), f.Repository.Path, canceled.Token));
        Assert.That(f.Materials(), Is.EqualTo(before), "Discovery must not change index/refs/config/worktree/profile bytes or modes.");
    }

    [Test]
    public async Task TheRealCliRoutePrecedesNormalProfileAndJournalStartupAndPreservesLiteralFlagPaths()
    {
        await using Scope f = await Scope.CreateAsync(); f.MalformedProfiles(); string before = f.Materials();
        GitEnvironmentOptions options = GitEnvironment.Options; DesktopStartupRequest oldRequest = DesktopProgram.StartupRequest;
        string? oldRepository = DesktopProgram.RepositoryPath; bool smoke = DesktopProgram.SmokeTest;
        TextWriter oldOut = Console.Out, oldError = Console.Error; using StringWriter output = new(), error = new(); int admitted = 0;
        try
        {
            Console.SetOut(output); Console.SetError(error);
            foreach (string path in new[] { "--version", "--help", f.Repository.Path })
            {
                Assert.That(DesktopProgram.Run(["searchfile", path, "ignored"], () => throw new AssertionException("Unexpected help"), args =>
                {
                    admitted++; Assert.That(args.Mode, Is.EqualTo(DesktopStartupMode.SearchFile)); Assert.That(args.RepositoryArgument, Is.EqualTo(path));
                    Assert.That(f.Materials(), Is.EqualTo(before)); return -1;
                }), Is.EqualTo(-1));
            }
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.That(admitted, Is.EqualTo(3)); Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.Empty);
        Assert.That(DesktopProgram.StartupRequest, Is.SameAs(oldRequest)); Assert.That(DesktopProgram.RepositoryPath, Is.EqualTo(oldRepository));
        Assert.That(DesktopProgram.SmokeTest, Is.EqualTo(smoke)); Assert.That(GitEnvironment.Options, Is.SameAs(options)); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [Test]
    public void CliLaunchErrorsNeverWriteExceptionDetailsOrASelectedPath()
    {
        TextWriter oldOut = Console.Out, oldError = Console.Error; using StringWriter output = new(), error = new();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            Assert.That(DesktopProgram.Run(["searchfile"], () => throw new AssertionException("Unexpected help"), _ => throw new IOException("private launch detail")), Is.EqualTo(-1));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.EqualTo("Unable to search repository files." + Environment.NewLine));
    }

    [Test]
    public async Task ActualMainRejectsAnUnreadableAdmittedEnvironmentWithoutStartingAWindowOrContaminatingStdout()
    {
        await using Scope f = await Scope.CreateAsync(); f.MalformedProfiles(); string before = f.Materials();
        TextWriter oldOut = Console.Out, oldError = Console.Error; using StringWriter output = new(), error = new();
        try
        {
            Console.SetOut(output); Console.SetError(error); TextWriter protocolWriter = Console.Out;
            Assert.That(DesktopProgram.Main(["searchfile", f.Repository.Path]), Is.EqualTo(-1));
            Assert.That(Console.Out, Is.SameAs(protocolWriter), "The protocol writer must be restored even when preparation fails before the UI.");
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.EqualTo("Unable to search repository files." + Environment.NewLine));
        Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheSharedSelectorReadsHeadAndWritesOnlyTheAcceptedAbsoluteLiteralPath()
    {
        await using Scope f = await Scope.CreateAsync(); const string literal = "folder/literal [*];$()' λ\tline\nfile.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(f.Git.PathInRepository(literal))!); await File.WriteAllTextAsync(f.Git.PathInRepository(literal), "committed\n");
        await f.Git.GitAsync("add", "--", literal); await f.Git.GitAsync("commit", "-m", "Literal committed path");
        File.Delete(f.Git.PathInRepository(literal));
        await File.WriteAllTextAsync(f.Git.PathInRepository("staged-only.txt"), "staged\n"); await f.Git.GitAsync("add", "staged-only.txt");
        await File.WriteAllTextAsync(f.Git.PathInRepository("untracked-only.txt"), "untracked\n"); string before = f.Materials();
        SearchFileSession session = f.Show(SearchFileApplication.CreateSession(await Prepare(f.Repository.Path, f.Repository.Path)));
        await Ready(session); FileListFindDialog dialog = session.Dialog; TextBox search = Field<TextBox>(dialog, "txtSearchBox"); ListBox matches = Field<ListBox>(dialog, "listBoxSearchResult");
        Assert.That(dialog.Name, Is.EqualTo("SearchWindow")); Assert.That(search.IsKeyboardFocusWithin, Is.True);
        search.Text = "only"; Pump(dialog); Assert.That(matches.ItemCount, Is.Zero, "Index-only and live files are absent from HEAD.");
        search.Text = "literal [*];$()' λ"; Pump(dialog); Assert.That(matches.ItemCount, Is.EqualTo(1));
        Assert.That(((FileStatusListEntry)matches.Items[0]!).Path, Is.EqualTo(literal)); Press(dialog, PhysicalKey.Enter); await Until(() => !dialog.IsVisible);
        using StringWriter output = new(), error = new();
        Assert.That(SearchFileApplication.Complete(session, output, error), Is.Zero);
        Assert.That(output.ToString(), Is.EqualTo(Path.Combine(f.Repository.Path, literal) + Environment.NewLine)); Assert.That(error.ToString(), Is.Empty);
        Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task RootedCaseInsensitiveSearchTwentyResultsAndArrowWrapRemainTheSharedSearchWindowBehavior()
    {
        await using Scope f = await Scope.CreateAsync();
        for (int i = 0; i < 23; i++) { await File.WriteAllTextAsync(f.Git.PathInRepository($"match-{i:00}.txt"), "candidate\n"); }
        await f.Git.Repository.StageAllAsync(); await f.Git.GitAsync("commit", "-m", "Many candidates"); string before = f.Materials();
        SearchFileSession session = f.Show(new(f.Repository)); await Ready(session);
        TextBox search = Field<TextBox>(session.Dialog, "txtSearchBox"); ListBox matches = Field<ListBox>(session.Dialog, "listBoxSearchResult");
        search.Text = Path.Combine(f.Repository.Path, "MATCH-"); Pump(session.Dialog);
        Assert.That(matches.ItemCount, Is.EqualTo(20)); Assert.That(matches.SelectedIndex, Is.Zero);
        Press(session.Dialog, PhysicalKey.ArrowUp); Assert.That(matches.SelectedIndex, Is.EqualTo(19));
        Press(session.Dialog, PhysicalKey.ArrowDown); Assert.That(matches.SelectedIndex, Is.Zero);
        Assert.That(((FileStatusListEntry)matches.SelectedItem!).Path, Is.EqualTo("match-00.txt"));
        Press(session.Dialog, PhysicalKey.Enter); await Until(() => !session.Dialog.IsVisible);
        using StringWriter output = new(), error = new(); Assert.That(SearchFileApplication.Complete(session, output, error), Is.Zero);
        Assert.That(output.ToString(), Is.EqualTo(f.Git.PathInRepository("match-00.txt") + Environment.NewLine)); Assert.That(error.ToString(), Is.Empty);
        Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task EscapeAndWindowCloseDoNotAcceptTheHighlightedMatchOrEmitAPath(bool escape)
    {
        await using Scope f = await Scope.CreateAsync(); string before = f.Materials(); SearchFileSession session = f.Show(new(f.Repository)); await Ready(session);
        Field<TextBox>(session.Dialog, "txtSearchBox").Text = "file"; Pump(session.Dialog);
        Assert.That(Field<ListBox>(session.Dialog, "listBoxSearchResult").SelectedItem, Is.Not.Null);
        if (escape) { Press(session.Dialog, PhysicalKey.Escape); } else { session.Dialog.Close(); }
        await Until(() => !session.Dialog.IsVisible); using StringWriter output = new(), error = new();
        Assert.That(SearchFileApplication.Complete(session, output, error), Is.EqualTo(-1)); Assert.That(session.Dialog.SelectedEntry, Is.Null);
        Assert.That(session.LastException, Is.Null); Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.Empty); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ARealHeadFailureCannotBecomeALocalFileListOrSuccessfulSelection()
    {
        await using Scope f = await Scope.CreateAsync(unborn: true); string before = f.Materials(); SearchFileSession session = f.Show(new(f.Repository));
        await Until(() => session.LoadTask.IsCompleted && !session.Dialog.IsVisible); await session.LoadTask;
        Assert.That(session.LastException, Is.TypeOf<GitCommandException>()); Assert.That(session.Dialog.SelectedEntry, Is.Null);
        using StringWriter output = new(), error = new(); Assert.That(SearchFileApplication.Complete(session, output, error), Is.EqualTo(-1));
        Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.EqualTo("Unable to search repository files." + Environment.NewLine)); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ClosingDuringTheRealHeadReadCancelsAndDrainsItsOwnedDescendantBeforeCompleting()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The owned transport fixture requires Linux procfs."); }
        await using Scope f = await Scope.CreateAsync(); string executable = f.Git.Output("blocked-git"), marker = f.Git.Output("reader-pids");
        string realGit = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(path => Path.Combine(path, "git")).First(File.Exists);
        static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
        await File.WriteAllTextAsync(executable, "#!/bin/sh\ncase \"$*\" in\n*ls-tree*) sleep 60 & echo \"$$ $!\" > " + Quote(marker)
            + "; wait;;\n*) exec " + Quote(realGit) + " \"$@\";;\nesac\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string before = f.Materials(marker); SearchFileSession session = f.Show(new(new GitRepository(f.Repository.Path, new GitRunner(executable))));
        await Until(() => File.Exists(marker) && File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2 && !session.LoadTask.IsCompleted);
        int[] pids = (await File.ReadAllTextAsync(marker)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Assert.That(pids.All(Running), Is.True); using StringWriter premature = new(), prematureError = new();
        Assert.Throws<InvalidOperationException>(() => SearchFileApplication.Complete(session, premature, prematureError));
        session.Dialog.Close(); await Until(() => session.LoadTask.IsCompleted && !session.Dialog.IsVisible && pids.All(pid => !Running(pid))); await session.LoadTask;
        using StringWriter output = new(), error = new(); Assert.That(SearchFileApplication.Complete(session, output, error), Is.EqualTo(-1));
        Assert.That(session.LastException, Is.Null); Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.Empty);
        Assert.That(f.Materials(marker), Is.EqualTo(before), "Only the fixture's PID witness is new; no repository/profile effect is authorized.");
    }

    private static Task<DesktopStartupRequest> Prepare(string? path, string cwd)
        => DesktopStartupDispatcher.PrepareAsync(DesktopStartupArguments.Parse(path is null ? ["searchfile"] : ["searchfile", path]), cwd);
    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window window, PhysicalKey key) { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); } Pump(window); }
    private static async Task Ready(SearchFileSession session) { await Until(() => session.LoadTask.IsCompleted); await session.LoadTask; Assert.That(session.LastException, Is.Null, session.LastException?.ToString()); Pump(session.Dialog); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch time = Stopwatch.StartNew(); while (!condition() && time.Elapsed < TimeSpan.FromSeconds(12)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Searchfile did not reach its bounded state.");
    }
    private static bool Running(int pid)
    {
        try { string text = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat"); int end = text.LastIndexOf(')'); return end < 0 || text[end + 2] is not ('Z' or 'X' or 'x'); }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly GitEnvironmentOptions _oldOptions;
        internal Fixture Git { get; }
        internal GitRepository Repository => Git.Repository;
        private readonly List<SearchFileSession> _sessions = [];
        private Scope(Fixture git, GitEnvironmentOptions oldOptions) { Git = git; _oldOptions = oldOptions; Git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); }
        internal static async Task<Scope> CreateAsync(bool unborn = false)
        {
            GitEnvironmentOptions old = GitEnvironment.Options; GitEnvironment.Configure(new());
            try { return new(await Fixture.CreateAsync(unborn), old); } catch { GitEnvironment.Configure(old); throw; }
        }
        internal SearchFileSession Show(SearchFileSession session) { _sessions.Add(session); session.Dialog.Show(); Pump(session.Dialog); return session; }
        internal void MalformedProfiles()
        {
            string file = new PreferencesStore().FilePath; Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            foreach (string name in new[] { Path.GetFileName(file), "git-environment.json", "GitExtensions.settings", "window-placements.json" })
            { File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, name), "not JSON"); }
        }
        internal string Materials(string? omit = null)
        {
            string root = Git.Output(""); SortedDictionary<string, string> entries = new(StringComparer.Ordinal);
            void Walk(string directory)
            {
                foreach (string file in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (file == omit) { continue; }
                    FileSystemInfo info = Directory.Exists(file) ? new DirectoryInfo(file) : new FileInfo(file);
                    string name = Path.GetRelativePath(root, file);
                    if (info.LinkTarget is string target) { entries[name] = "link:" + target; continue; }
                    string mode = OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(file)).ToString(CultureInfo.InvariantCulture);
                    if (info is DirectoryInfo) { entries[name] = "directory:" + mode; Walk(file); }
                    else { entries[name] = mode + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))); }
                }
            }
            Walk(root); return JsonSerializer.Serialize(entries);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (SearchFileSession session in _sessions.AsEnumerable().Reverse()) { if (session.Dialog.IsVisible) { session.Dialog.Close(); } }
                if (_sessions.Count != 0) { await Until(() => _sessions.All(session => session.LoadTask.IsCompleted && !session.Dialog.IsVisible)); }
                foreach (SearchFileSession session in _sessions) { await session.LoadTask; }
            }
            finally { Git.Dispose(); GitEnvironment.Configure(_oldOptions); }
        }
    }
}
