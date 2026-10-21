using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GitExtensions.Linux;

internal static class ArtificialResetScenarios
{
    private const string Literal = ":[literal]* λ\t\n.txt";
    private static readonly GitCompareRevisionKind[] Kinds = [GitCompareRevisionKind.WorkingTree, GitCompareRevisionKind.Index];
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("artificial Working tree reset matches native checkout while preserving staged contents and optional nonignored cleanup", WorkingTreeAsync),
        ("artificial Index reset matches native implicit HEAD hard reset and optional nonignored cleanup", IndexAsync),
        ("artificial reset leaves gitlink child contents to native nonrecursive Git semantics", GitlinkAsync),
        ("artificial reset preserves native unborn empty staged and intent-to-add outcomes without resolving HEAD", UnbornAsync),
        ("artificial reset stops before cleanup on a real reset failure and exposes a failed cleanup after actual reset effects", FailuresAsync),
        ("artificial reset holds one mutation lease across both commands and drains cancellation at each native phase", LeaseAndCancellationAsync),
        ("artificial reset rejects other endpoint kinds before IO and preserves precancellation and bare errors", ValidationAsync)
    ];

    private static Task WorkingTreeAsync() => NativeParityAsync(GitCompareRevisionKind.WorkingTree);
    private static Task IndexAsync() => NativeParityAsync(GitCompareRevisionKind.Index);
    private static async Task NativeParityAsync(GitCompareRevisionKind kind)
    {
        foreach (bool deleteNew in new[] { false, true })
        {
            using RepositoryFixture f = await DirtyFixture();
            GitRepository oracle = Twin(f, "native");
            string index = (await Native(f.Repository, ["ls-files", "--stage", "-z"])).StandardOutput;
            string refs = (await Native(f.Repository, ["show-ref", "--head"])).StandardOutput;
            using Harness h = Harness.Create(f, "parity", []);
            Outcome expected = await NativeReset(oracle, kind, deleteNew);
            GitResult actual = await h.Repository.ResetArtificialRevisionAsync(kind, deleteNew);
            Matches(actual, expected.Result, h.Repository, oracle);
            Check(expected.Result.Success && await State(h.Repository) == await State(oracle), "The exact native reset/cleanup twin has different worktree, index, ignored, reference, config or object material.");
            Check(Commands(h).Select(a => a[0]).SequenceEqual(deleteNew ? [First(kind)[0], "clean"] : [First(kind)[0]]), "The artificial action introduced extra mutation commands or omitted its native cleanup.");
            Check(Commands(h)[0].SequenceEqual(First(kind)) && (!deleteNew || Commands(h)[1].SequenceEqual(["clean", "-d", "-f", "--"])), "The native arguments lost their whole-repository scope or gained HEAD resolution, ignored cleanup or recursion.");
            Check((await Native(f.Repository, ["show-ref", "--head"])).StandardOutput == refs, "An artificial reset must not move a branch to another commit.");
            if (kind == GitCompareRevisionKind.WorkingTree)
            {
                Check((await Native(f.Repository, ["ls-files", "--stage", "-z"])).StandardOutput == index, "Working tree reset changed staged object IDs, modes, stages or paths.");
                Check(File.ReadAllText(f.File(Literal)) == "staged λ\n" && File.ReadAllText(f.File("staged-new.txt")) == "new index\n", "Working tree reset did not use the actual index contents for both tracked and newly staged files.");
            }
            else
            {
                Check(File.ReadAllText(f.File(Literal)) == "committed λ\n" && !File.Exists(f.File("staged-new.txt")), "Index reset did not apply the native implicit-HEAD hard reset to tracked and newly staged material.");
            }
            Check(File.Exists(f.File("ordinary.txt")) == !deleteNew && Directory.Exists(f.File("ordinary-dir")) == !deleteNew,
                "The explicit delete choice did not control nonignored files and directories.");
            Check(File.ReadAllText(f.File("keep.ignored")) == "ignored\n" && File.Exists(f.File("ignored-dir/keep.txt")), "Artificial cleanup accidentally used -x/-X or removed ignored data.");
        }
    }

    private static async Task GitlinkAsync()
    {
        foreach (GitCompareRevisionKind kind in Kinds)
        {
            using RepositoryFixture f = await DirtyFixture();
            GitRepository source = await GitRepository.InitAsync(Path.Combine(f.Root, "module-source"));
            await RepositoryFixture.ConfigureAsync(source);
            await File.WriteAllTextAsync(Path.Combine(source.Path, "child.txt"), "committed child\n");
            await Native(source, ["add", "--", "child.txt"]); await Native(source, ["commit", "-m", "Child"]);
            await Native(f.Repository, ["-c", "protocol.file.allow=always", "submodule", "add", "--", source.Path, "module"]);
            await Native(f.Repository, ["commit", "-m", "Parent gitlink"]);
            await Native(f.Repository, ["config", "--local", "submodule.recurse", "false"]);
            f.Write("module/child.txt", "dirty child\n"); f.Write("module/child-untracked.txt", "new child\n");
            f.Write("after-link.txt", "delete parent\n");
            GitRepository oracle = Twin(f, "native-module");
            string child = Worktree(f.File("module"));
            using Harness h = Harness.Create(f, "gitlink", []);
            Outcome expected = await NativeReset(oracle, kind, true);
            Matches(await h.Repository.ResetArtificialRevisionAsync(kind, true), expected.Result, h.Repository, oracle);
            Check(await State(h.Repository) == await State(oracle) && Worktree(f.File("module")) == child,
                "The parent artificial reset introduced an explicit child reset/cleanup beyond native Git semantics.");
            Check(!File.Exists(f.File("after-link.txt")) && File.Exists(f.File("module/child-untracked.txt")), "Parent cleanup failed or unexpectedly recursed into a tracked gitlink.");
            Check(Commands(h).All(args => args[0] != "submodule") && Commands(h).Count == 2, "The artificial workflow invoked a submodule command or duplicated cleanup.");
        }
    }

    private static async Task UnbornAsync()
    {
        foreach (GitCompareRevisionKind kind in Kinds)
        foreach (string shape in new[] { "empty", "staged", "intent" })
        foreach (bool deleteNew in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
            f.Write("loose.txt", "loose\n");
            if (shape != "empty")
            {
                f.Write("initial.txt", "initial\n");
                await Native(f.Repository, shape == "intent" ? ["add", "-N", "--", "initial.txt"] : ["add", "--", "initial.txt"]);
                f.Write("initial.txt", "working initial\n");
            }
            GitRepository oracle = Twin(f, "native-unborn");
            using Harness h = Harness.Create(f, "unborn", []);
            Outcome expected = await NativeReset(oracle, kind, deleteNew);
            Outcome actual = await Capture(() => h.Repository.ResetArtificialRevisionAsync(kind, deleteNew), deleteNew ? ["clean", "-d", "-f", "--"] : First(kind));
            Matches(actual.Result, expected.Result, h.Repository, oracle);
            Check(actual.Arguments.SequenceEqual(expected.Arguments) && await State(h.Repository) == await State(oracle),
                "An unborn artificial reset differs from native command order, failure diagnostics or actual resulting material.");
            Check(Commands(h)[0].SequenceEqual(First(kind)) && Commands(h).Count == (expected.Arguments[0] == "clean" ? 2 : 1),
                "Unborn handling skipped the actual reset, resolved HEAD beforehand, or continued clean after failure.");
            Check(!(await Native(f.Repository, ["rev-parse", "--verify", "HEAD"], false)).Success, "An unborn artificial action manufactured a commit.");
        }
    }

    private static async Task FailuresAsync()
    {
        using (RepositoryFixture f = await DirtyFixture())
        {
            await File.WriteAllTextAsync(f.File(".git/index.lock"), "external lock\n");
            GitRepository oracle = Twin(f, "native-locked");
            string before = await State(f.Repository);
            using Harness h = Harness.Create(f, "locked", []);
            Outcome expected = await NativeReset(oracle, GitCompareRevisionKind.Index, true);
            GitCommandException error = await Expect<GitCommandException>(() => h.Repository.ResetArtificialRevisionAsync(GitCompareRevisionKind.Index, true));
            Matches(error.Result, expected.Result, h.Repository, oracle);
            Check(!error.Result.Success && error.Arguments.SequenceEqual(First(GitCompareRevisionKind.Index)) && Commands(h).Count == 1,
                "A failed hard reset was swallowed or followed by cleanup.");
            Check(File.Exists(f.File("ordinary.txt")) && await State(f.Repository) == before && await State(oracle) == before,
                "The real index-lock failure deleted untracked material or otherwise changed the preimage.");
        }
        using (RepositoryFixture f = await DirtyFixture())
        {
            GitRepository oracle = Twin(f, "native-clean-error");
            using Harness h = Harness.Create(f, "clean-error", [], failClean: true);
            await Native(oracle, First(GitCompareRevisionKind.Index));
            GitResult nativeFailure = await Native(oracle, ["clean", "--invalid-option-for-artificial-reset-fixture"], false);
            GitCommandException error = await Expect<GitCommandException>(() => h.Repository.ResetArtificialRevisionAsync(GitCompareRevisionKind.Index, true));
            Matches(error.Result, nativeFailure, h.Repository, oracle);
            Check(error.Arguments.SequenceEqual(["clean", "-d", "-f", "--"]) && Commands(h).Count == 2 && await State(f.Repository) == await State(oracle),
                "A nonzero native cleanup was converted into success, lost its requested arguments, or masked the preceding reset effects.");
            Check(File.ReadAllText(f.File(Literal)) == "committed λ\n" && File.Exists(f.File("ordinary.txt")),
                "The partial-failure fixture must retain a real successful reset followed by failed cleanup, without invented rollback.");
        }
    }

    private static async Task LeaseAndCancellationAsync()
    {
        using (RepositoryFixture f = await DirtyFixture())
        using (Harness h = Harness.Create(f, "lease", ["checkout", "clean"]))
        {
            Task<GitResult> reset = h.Repository.ResetArtificialRevisionAsync(GitCompareRevisionKind.WorkingTree, true);
            Task<GitResult>? queued = null;
            try
            {
                await h.Wait("checkout");
                // SetConfigAsync waits directly on the mutation semaphore before native execution.
                queued = h.Repository.SetConfigAsync("fixture.interleaved", "yes");
                Check(!queued.IsCompleted, "The queued real mutation escaped the pending reset lease.");
                await h.Release("checkout"); await h.Wait("clean");
                Check(!queued.IsCompleted && Commands(h).Select(a => a[0]).SequenceEqual(["checkout", "clean"]),
                    "A queued mutation interleaved between reset and cleanup; they must share one lease.");
                await h.Release("clean");
                Check((await reset.WaitAsync(TimeSpan.FromSeconds(15))).Success && (await queued.WaitAsync(TimeSpan.FromSeconds(15))).Success,
                    "The completed native workflow did not release its mutation lease.");
                Check(Commands(h).Select(a => a[0]).SequenceEqual(["checkout", "clean", "config"]), "The final command order lost the queued mutation or duplicated a step.");
            }
            finally
            {
                await h.Release("checkout"); await h.Release("clean");
                await reset.WaitAsync(TimeSpan.FromSeconds(15));
                if (queued is not null) { await queued.WaitAsync(TimeSpan.FromSeconds(15)); }
            }
        }
        foreach (GitCompareRevisionKind kind in Kinds)
        foreach (bool duringClean in new[] { false, true })
        {
            using RepositoryFixture f = await DirtyFixture();
            GitRepository oracle = Twin(f, "native-before-cancel");
            string phase = duringClean ? "clean" : First(kind)[0];
            using Harness h = Harness.Create(f, "cancel", [phase]);
            using CancellationTokenSource token = new();
            Task<GitResult> work = h.Repository.ResetArtificialRevisionAsync(kind, true, token.Token);
            (int Pid, string Start)? identity = null;
            try
            {
                identity = await h.Wait(phase);
                Check(!work.IsCompleted && Alive(identity.Value), "The actual cancellation phase was not retained by a live native actor.");
                if (duringClean) { await Native(oracle, First(kind)); }
                token.Cancel(); OperationCanceledException error = await Expect<OperationCanceledException>(() => work);
                Check(error.CancellationToken == token.Token && work.IsCanceled && !Alive(identity.Value), "Cancellation lost its caller token or returned before exact PID/start retirement, including zombies.");
                Check(Commands(h).Count == (duringClean ? 2 : 1) && await State(f.Repository) == await State(oracle),
                    "Cancelled reset ran cleanup, or cancellation after reset lost the exact native partial effects.");
                Check((await h.Repository.SetConfigAsync("fixture.aftercancel", "yes").WaitAsync(TimeSpan.FromSeconds(10))).Success,
                    "Cancellation retained the mutation lease after the actual process drained.");
            }
            finally
            {
                token.Cancel(); await h.Release(phase);
                try { await work.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { }
                if (identity is { } observed) { await Until(() => !Alive(observed)); }
            }
        }
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await DirtyFixture();
        using Harness h = Harness.Create(f, "invalid", []);
        string before = await State(f.Repository);
        foreach (GitCompareRevisionKind kind in new[] { GitCompareRevisionKind.Commit, GitCompareRevisionKind.EmptyTree, (GitCompareRevisionKind)999 })
        { await Expect<ArgumentOutOfRangeException>(() => h.Repository.ResetArtificialRevisionAsync(kind, true)); }
        using CancellationTokenSource token = new(); token.Cancel();
        OperationCanceledException error = await Expect<OperationCanceledException>(() => h.Repository.ResetArtificialRevisionAsync(GitCompareRevisionKind.Index, true, token.Token));
        Check(error.CancellationToken == token.Token && !File.Exists(Path.Combine(h.Root, "invoked")) && Commands(h).Count == 0 && await State(f.Repository) == before,
            "Invalid kinds or precancellation started a native mutation or changed repository material.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare"), bare: true);
        string bareBefore = Material(bare.Path);
        foreach (GitCompareRevisionKind kind in Kinds)
        { await Expect<InvalidOperationException>(() => bare.ResetArtificialRevisionAsync(kind, true)); }
        Check(Material(bare.Path) == bareBefore, "Bare rejection modified its repository metadata.");
    }

    private static async Task<RepositoryFixture> DirtyFixture()
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync();
        try
        {
            f.Write(".gitignore", "*.ignored\nignored-dir/\n"); f.Write(Literal, "committed λ\n"); f.Write("deleted.txt", "restore me\n");
            await Native(f.Repository, ["add", "--", "."]); await Native(f.Repository, ["commit", "-m", "Reset fixture"]);
            f.Write(Literal, "staged λ\n"); f.Write("staged-new.txt", "new index\n");
            await Native(f.Repository, ["add", "--", Literal, "staged-new.txt"]);
            f.Write(Literal, "working λ\n"); f.Write("staged-new.txt", "new working\n"); File.Delete(f.File("deleted.txt"));
            f.Write("ordinary.txt", "untracked\n"); f.Write("ordinary-dir/file.txt", "untracked directory\n");
            f.Write("keep.ignored", "ignored\n"); f.Write("ignored-dir/keep.txt", "ignored directory\n");
            return f;
        }
        catch { f.Dispose(); throw; }
    }

    private static string[] First(GitCompareRevisionKind kind) => kind == GitCompareRevisionKind.WorkingTree
        ? ["checkout", "--", "."] : ["reset", "--hard", "--quiet", "--"];
    private sealed record Outcome(GitResult Result, IReadOnlyList<string> Arguments);
    private static async Task<Outcome> NativeReset(GitRepository repository, GitCompareRevisionKind kind, bool deleteNew)
    {
        string[] args = First(kind); GitResult result = await Native(repository, args, false);
        if (result.Success && deleteNew) { args = ["clean", "-d", "-f", "--"]; result = await Native(repository, args, false); }
        return new(result, args);
    }
    private static async Task<Outcome> Capture(Func<Task<GitResult>> action, IReadOnlyList<string> successArguments)
    {
        try { return new(await action(), successArguments); }
        catch (GitCommandException error) { return new(error.Result, error.Arguments); }
    }
    private static void Matches(GitResult actual, GitResult native, GitRepository actualRepository, GitRepository oracle)
    {
        string Normalize(string text, string path) => text.Replace(path, "<repository>", StringComparison.Ordinal);
        Check(actual.ExitCode == native.ExitCode && Normalize(actual.StandardOutput, actualRepository.Path) == Normalize(native.StandardOutput, oracle.Path)
            && Normalize(actual.StandardError, actualRepository.Path) == Normalize(native.StandardError, oracle.Path), "The native exit/stdout/stderr outcome differs from the independently executed command.");
    }
    private static async Task<GitResult> Native(GitRepository repository, IReadOnlyList<string> args, bool requireSuccess = true)
    {
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(repository.Path,
            ["--no-optional-locks", "--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "--literal-pathspecs", .. args]);
        if (requireSuccess && !result.Success) { throw new GitCommandException(args, result); } return result;
    }
    private static async Task<string> State(GitRepository repository)
    {
        string[] reads =
        [
            (await Native(repository, ["ls-files", "--stage", "-z"])).StandardOutput,
            (await Native(repository, ["ls-files", "-v", "-z"])).StandardOutput,
            (await Native(repository, ["status", "--porcelain=2", "-z", "--untracked-files=all", "--ignored"])).StandardOutput,
            (await Native(repository, ["show-ref", "--head"], false)).StandardOutput,
            (await Native(repository, ["symbolic-ref", "--quiet", "HEAD"], false)).StandardOutput
        ];
        return JsonSerializer.Serialize(new { Worktree = Worktree(repository.Path), Reads = reads,
            Config = File.ReadAllText(Path.Combine(repository.Path, ".git", "config")), Objects = Material(Path.Combine(repository.Path, ".git", "objects")) });
    }
    private static GitRepository Twin(RepositoryFixture fixture, string name)
    { string path = Path.Combine(fixture.Root, name); Copy(fixture.Repository.Path, path); return new(path); }
    private static string Worktree(string root) => Material(root, skipGitDirectories: true);
    private static string Material(string root, bool skipGitDirectories = false)
    {
        List<string> entries = []; Walk(root); return JsonSerializer.Serialize(entries);
        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileInfo file = new(path); string relative = Path.GetRelativePath(root, path);
                if (file.LinkTarget is string target) { entries.Add(relative + ":link:" + target); continue; }
                if (Directory.Exists(path) && skipGitDirectories && Path.GetFileName(path) == ".git") { continue; }
                string mode = OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture);
                if (Directory.Exists(path)) { entries.Add(relative + ":dir:" + mode); Walk(path); }
                else { entries.Add(relative + ":" + mode + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))); }
            }
        }
    }
    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            string copied = Path.Combine(destination, Path.GetFileName(path)); FileInfo info = new(path);
            if (info.LinkTarget is string target)
            { if (Directory.Exists(path)) { Directory.CreateSymbolicLink(copied, target); } else { File.CreateSymbolicLink(copied, target); } }
            else if (Directory.Exists(path)) { Copy(path, copied); }
            else { File.Copy(path, copied); if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(copied, File.GetUnixFileMode(path)); } }
        }
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(destination, File.GetUnixFileMode(source)); }
    }
    private static IReadOnlyList<string[]> Commands(Harness harness) => File.Exists(harness.Log)
        ? File.ReadAllLines(harness.Log).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray() : [];
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(TimeSpan.FromSeconds(15)); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); } Check(condition(), "The exact native reset gate did not reach its bounded barrier."); }
    private static string? Start(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => Start(identity.Pid) == identity.Start;

    private sealed record Harness(GitRepository Repository, string Root, string Log) : IDisposable
    {
        internal static Harness Create(RepositoryFixture owner, string name, string[] gated, bool failClean = false)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private native gates use Linux procfs and executable modes."); }
            string root = Path.Combine(owner.Root, name + "-gate"); Directory.CreateDirectory(root);
            string executable = Path.Combine(root, "git"), log = Path.Combine(root, "commands.jsonl");
            string script = "#!/usr/bin/python3\nimport json,os,pathlib,sys,time\n"
                + "root=pathlib.Path(" + JsonSerializer.Serialize(root) + ")\n(root/'invoked').touch()\na=sys.argv[1:]\n"
                + "i=next((i for i,x in enumerate(a) if x in ('checkout','reset','clean','config')),None)\n"
                + "if i is not None:\n    command=a[i]\n    with open(root/'commands.jsonl','a') as log: log.write(json.dumps(a[i:])+'\\n')\n"
                + "    if command in " + JsonSerializer.Serialize(gated) + ":\n"
                + "        ready=root/(command+'.pid'); temporary=root/(command+'.tmp'); temporary.write_text(str(os.getpid())); os.replace(temporary,ready)\n"
                + "        while not (root/(command+'.release')).exists(): time.sleep(.01)\n"
                + (failClean ? "    if command=='clean': os.execv('/usr/bin/git',['/usr/bin/git','clean','--invalid-option-for-artificial-reset-fixture'])\n" : "")
                + "os.execv('/usr/bin/git',['/usr/bin/git',*a])\n";
            File.WriteAllText(executable, script); File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(new(owner.Repository.Path, new GitRunner(executable)), root, log);
        }
        internal Task Release(string command) => File.WriteAllTextAsync(Path.Combine(Root, command + ".release"), "release\n");
        internal async Task<(int Pid, string Start)> Wait(string command)
        {
            (int Pid, string Start)? identity = null; string path = Path.Combine(Root, command + ".pid");
            await Until(() => { if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out int pid) && ArtificialResetScenarios.Start(pid) is string start) { identity = (pid, start); return true; } return false; });
            return identity ?? throw new InvalidOperationException("No exact native PID/start identity was captured.");
        }
        public void Dispose() { }
    }
}
