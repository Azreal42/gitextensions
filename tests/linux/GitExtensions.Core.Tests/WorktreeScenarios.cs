using System.Diagnostics;
using System.Text;
using GitExtensions.Linux;

internal static class WorktreeScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("worktree snapshots identify main current linked detached missing and empty-reason locks", SnapshotsAsync),
        ("worktree creation preserves literal newline paths branches and native streamed output", CreationAsync),
        ("worktree creation defaults to native relative paths and records its repository extension", RelativeAsync),
        ("worktree creation honors explicitly disabled relative paths", AbsoluteAsync),
        ("worktree creation refuses existing branches occupied paths and Git metadata destinations without resetting refs", ValidationAsync),
        ("worktree native safe removal retains dirty tracked untracked and source files until discard is explicit", DirtyAsync),
        ("worktree deletion never removes main current or their aliases", MainCurrentAliasesAsync),
        ("worktree deletion requires explicit lock override and preserves dirty locked worktrees", LockedAsync),
        ("worktree snapshots refuse replaced directories and Git registrations before open or removal", ReplacedAsync),
        ("worktree prune streams native output and preserves locked missing registrations", PruneAsync),
        ("worktree removal retains native submodule safety unless discard is explicit", SubmodulesAsync),
        ("worktree creation operates from bare and linked repositories without changing their source checkout", BareLinkedAsync),
        ("worktree post-checkout hook failure retains actual native partial state and output", HookFailureAsync),
        ("worktree cancellation joins native hook processes without inventing rollback", CancellationAsync),
        ("worktree pre-cancellation prevents all native worktree mutations", PreCancellationAsync),
    ];

    private static async Task SnapshotsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await NativeAdd(f, "linked", "topic"); await f.Repository.RunAsync(["worktree", "add", "--detach", "--", f.Target("detached"), "HEAD"]);
        await f.Repository.RunAsync(["worktree", "lock", "--", f.Target("linked")]);
        IReadOnlyList<WorktreeSnapshot> all = await f.Repository.GetWorktreeSnapshotsAsync();
        WorktreeSnapshot main = all.Single(s => s.IsMain), linked = all.Single(s => s.Path == f.Target("linked"));
        Check(main.IsCurrent && !main.CanRemove && !main.CanOpen && main.Exists, "The main/current source is recognized by identity.");
        Check(linked.IsLocked && linked.LockedReason == "" && linked.Branch == "refs/heads/topic" && linked.CanOpen && linked.CanRemove,
            "An empty lock reason still represents a locked, valid linked worktree.");
        Check(all.Single(s => s.Path == f.Target("detached")).IsDetached, "Detached state is not confused with a branch.");
        Directory.Delete(f.Target("detached"), recursive: true);
        WorktreeSnapshot missing = (await f.Repository.GetWorktreeSnapshotsAsync()).Single(s => s.Path == f.Target("detached"));
        Check(!missing.Exists && !missing.CanOpen && !missing.CanRemove, "Missing registrations remain visible but cannot be opened or removed as live directories.");
    }

    private static async Task CreationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string target = f.Target("literal space\nété '$();"), branch = "feature/été-$(marker);'";
        byte[] index = await File.ReadAllBytesAsync(f.Root.File(".git/index"));
        Pipes pipes = new(); WorktreeOperationResult result = await f.Repository.CreateWorktreeAsync(new(target, "HEAD", branch), outputReceived: pipes.Receive);
        Check(result.Result.Success && result.Worktree?.Branch == "refs/heads/" + branch && result.Worktree.Exists, "The exact path and branch reach native Git as literal arguments.");
        Check(pipes.Output.SequenceEqual(result.Result.StandardOutputBytes) && pipes.Error.SequenceEqual(result.Result.StandardErrorBytes)
            && pipes.Output.Length + pipes.Error.Length > 0, "The process dialog receives exactly the retained native stdout/stderr bytes.");
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == f.Initial && Bytes(index, await File.ReadAllBytesAsync(f.Root.File(".git/index")))
            && !File.Exists(f.Root.File("marker")), "Creation keeps source HEAD/index and does not execute metacharacters.");
        Check(await f.Repository.ValidateWorktreeOpenAsync(result.Worktree!) == target, "Opening uses the revalidated registered worktree path.");
    }

    private static async Task RelativeAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Repository.UnsetConfigAsync("worktree.useRelativePaths");
        WorktreeOperationResult result = await f.Repository.CreateWorktreeAsync(new(f.Target("relative"), "HEAD", "relative"));
        Check(result.Result.Success && result.UsesRelativePaths && result.EnabledRelativeWorktreeExtension,
            "The installed native Git writes relative metadata and enables its required extension.");
        Check((await f.Repository.GetConfigAsync("^worktree\\.userelativepaths$")).Count == 0
            && (await f.Repository.GetConfigAsync("^extensions\\.relativeworktrees$")).Last().Value == "true", "The command default is transient while Git's compatibility extension is persisted explicitly.");
        string text = await File.ReadAllTextAsync(Path.Combine(f.Target("relative"), ".git"));
        Check(!Path.IsPathRooted(text[8..].TrimEnd('\n')), "The resulting .git pointer is actually relative.");
    }

    private static async Task AbsoluteAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        WorktreeOperationResult result = await f.Repository.CreateWorktreeAsync(new(f.Target("absolute"), "HEAD", "absolute"));
        Check(result.Result.Success && !result.UsesRelativePaths && !result.EnabledRelativeWorktreeExtension, "An explicit false remains effective.");
        Check(Path.IsPathRooted((await File.ReadAllTextAsync(Path.Combine(f.Target("absolute"), ".git")))[8..].TrimEnd('\n')), "Native metadata remains absolute.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string target = f.Target("occupied"); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "sentinel"), "keep");
        await Expect<WorktreeOperationException>(() => f.Repository.CreateWorktreeAsync(new(target, "HEAD", "new")));
        await Expect<WorktreeOperationException>(() => f.Repository.CreateWorktreeAsync(new(f.Target("existing"), "HEAD", "main")));
        await Expect<WorktreeOperationException>(() => f.Repository.CreateWorktreeAsync(new(f.Target("busy"), "main")));
        await Expect<WorktreeOperationException>(() => f.Repository.CreateWorktreeAsync(new(f.Root.File(".git/unsafe"), "HEAD", "unsafe")));
        await Expect<ArgumentException>(() => f.Repository.CreateWorktreeAsync(new(f.Target("bad"), "HEAD", "bad..name")));
        Check(await f.Repository.ResolveRevisionAsync("main") == f.Initial && await File.ReadAllTextAsync(Path.Combine(target, "sentinel")) == "keep"
            && (await f.Repository.GetWorktreesAsync()).Count == 1, "Validation never uses -B or alters occupied files/registrations.");
    }

    private static async Task DirtyAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await NativeAdd(f, "dirty", "dirty");
        string target = f.Target("dirty"); await File.AppendAllTextAsync(Path.Combine(target, "seed.txt"), "dirty\n"); await File.WriteAllTextAsync(Path.Combine(target, "untracked"), "keep");
        WorktreeSnapshot selected = await Selected(f, "dirty"); Pipes pipes = new();
        WorktreeOperationResult refused = await f.Repository.DeleteWorktreeAsync(selected, outputReceived: pipes.Receive);
        Check(!refused.Result.Success && File.Exists(Path.Combine(target, "untracked")) && pipes.Error.Length > 0, "Native refusal retains tracked and untracked changes and displays its diagnostics.");
        WorktreeOperationResult deleted = await f.Repository.DeleteWorktreeAsync(selected, discardChanges: true);
        Check(deleted.Result.Success && !Directory.Exists(target) && await f.Repository.ResolveRevisionAsync("dirty") == f.Initial
            && await f.Repository.ResolveRevisionAsync("HEAD") == f.Initial && await File.ReadAllTextAsync(f.Root.File("seed.txt")) == "seed\n",
            "Explicit discard removes only the linked directory and retains local branch commits and the source checkout.");
    }

    private static async Task MainCurrentAliasesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await NativeAdd(f, "linked", "linked");
        WorktreeSnapshot main = (await f.Repository.GetWorktreeSnapshotsAsync()).Single(s => s.IsMain);
        await Expect<WorktreeOperationException>(() => f.Repository.DeleteWorktreeAsync(main, true, true));
        GitRepository linked = await GitRepository.OpenAsync(f.Target("linked"));
        WorktreeSnapshot current = (await linked.GetWorktreeSnapshotsAsync()).Single(s => s.IsCurrent);
        await Expect<WorktreeOperationException>(() => linked.DeleteWorktreeAsync(current, true, true));
        WorktreeSnapshot fromLinkedMain = (await linked.GetWorktreeSnapshotsAsync()).Single(s => s.IsMain);
        Check(fromLinkedMain.CanOpen && !fromLinkedMain.CanRemove, "The main checkout may be opened from a linked checkout but can never be removed.");
        if (!OperatingSystem.IsWindows())
        {
            string alias = f.Target("alias"); Directory.CreateSymbolicLink(alias, f.Target("linked"));
            GitRepository aliased = new(alias);
            Check((await aliased.GetWorktreeSnapshotsAsync()).Single(s => s.IsCurrent).Path == current.Path, "Symlink aliases retain current-worktree identity.");
            await Expect<WorktreeOperationException>(() => aliased.RemoveWorktreeAsync(alias, force: true));
        }
        Check(Directory.Exists(f.Target("linked")) && Directory.Exists(f.Repository.Path), "No protected directory is removed.");
    }

    private static async Task LockedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await NativeAdd(f, "locked", "locked");
        await f.Repository.RunAsync(["worktree", "lock", "--reason", "keep mounted", "--", f.Target("locked")]);
        WorktreeSnapshot locked = await Selected(f, "locked");
        await Expect<WorktreeOperationException>(() => f.Repository.DeleteWorktreeAsync(locked, discardChanges: true));
        await File.WriteAllTextAsync(Path.Combine(f.Target("locked"), "untracked"), "keep");
        await Expect<WorktreeOperationException>(() => f.Repository.DeleteWorktreeAsync(locked, overrideLock: true));
        Check(File.Exists(Path.Combine(f.Target("locked"), "untracked")), "Lock override never silently consents to discarding untracked files.");
        Check((await f.Repository.DeleteWorktreeAsync(locked, true, true)).Result.Success, "Explicit lock override plus discard sends native double force.");
        await NativeAdd(f, "clean-locked", "clean-locked"); await f.Repository.RunAsync(["worktree", "lock", "--", f.Target("clean-locked")]);
        WorktreeSnapshot clean = await Selected(f, "clean-locked");
        WorktreeOperationException cleanRefusal = await Expect<WorktreeOperationException>(() => f.Repository.DeleteWorktreeAsync(clean, overrideLock: true));
        Check(!cleanRefusal.HasRepositoryChanges && Directory.Exists(clean.Path) && cleanRefusal.Message.Contains("double force", StringComparison.Ordinal),
            "Even a currently clean locked worktree requires discard consent because Git cannot guarantee cleanliness atomically with double force.");
        if (!OperatingSystem.IsWindows())
        {
            string marker = f.Target("unexpected-native-remove"), wrapper = f.Target("observe-native-git");
            string quoted = "'" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            await File.WriteAllTextAsync(wrapper, "#!/bin/sh\ncase \"$*\" in\n *'worktree remove'*) echo entered > " + quoted + "; exit 91 ;;\nesac\nexec git \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper));
            WorktreeOperationException refused = await Expect<WorktreeOperationException>(() => observed.DeleteWorktreeAsync(clean, overrideLock: true));
            Check(!refused.HasRepositoryChanges && !File.Exists(marker) && Directory.Exists(clean.Path),
                "Override alone is refused before any mutation-capable native remove, rather than relying on a racy status preflight.");
        }
        Check((await f.Repository.DeleteWorktreeAsync(clean, discardChanges: true, overrideLock: true)).Result.Success,
            "A clean locked worktree remains removable after both explicit confirmations.");
    }

    private static async Task ReplacedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await NativeAdd(f, "replace", "replace");
        WorktreeSnapshot selected = await Selected(f, "replace"); string target = f.Target("replace"), old = f.Target("old-directory");
        Directory.Move(target, old); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "sentinel"), "replacement");
        await Expect<WorktreeOperationException>(() => f.Repository.DeleteWorktreeAsync(selected, true, true));
        await Expect<WorktreeOperationException>(() => f.Repository.ValidateWorktreeOpenAsync(selected));
        Check(Directory.Exists(old) && await File.ReadAllTextAsync(Path.Combine(target, "sentinel")) == "replacement", "Directory replacement is rejected before deleting either directory.");
        Directory.Delete(target, recursive: true); Directory.Move(old, target);
        selected = await Selected(f, "replace"); string marker = Path.Combine(target, ".git"), saved = Path.Combine(target, "saved-marker");
        File.Move(marker, saved); await File.WriteAllTextAsync(marker, "gitdir: " + f.Root.File(".git") + "\n");
        await Expect<WorktreeOperationException>(() => f.Repository.DeleteWorktreeAsync(selected, true, true));
        await Expect<WorktreeOperationException>(() => f.Repository.ValidateWorktreeOpenAsync(selected));
        Check(File.Exists(saved) && Directory.Exists(target), "Replacing a .git pointer cannot authorize deleting an unrelated repository.");
    }

    private static async Task PruneAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await NativeAdd(f, "gone", "gone"); await NativeAdd(f, "locked-gone", "locked-gone");
        await f.Repository.RunAsync(["worktree", "lock", "--", f.Target("locked-gone")]);
        foreach (string name in new[] { "gone", "locked-gone" })
        {
            GitRepository tree = await GitRepository.OpenAsync(f.Target(name)); string admin = tree.Info!.GitDirectory;
            foreach (string path in Directory.EnumerateFiles(admin)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddYears(-1)); }
            Directory.Delete(f.Target(name), recursive: true);
        }
        Pipes pipes = new(); GitResult result = await f.Repository.PruneWorktreesAsync(default, pipes.Receive);
        IReadOnlyList<WorktreeSnapshot> all = await f.Repository.GetWorktreeSnapshotsAsync();
        Check(result.Success && all.All(s => s.Path != f.Target("gone")) && all.Single(s => s.Path == f.Target("locked-gone")).IsLocked,
            "Native prune removes stale unlocked administration and retains locked missing registrations.");
        Check(pipes.Output.SequenceEqual(result.StandardOutputBytes) && pipes.Error.SequenceEqual(result.StandardErrorBytes) && pipes.Output.Length + pipes.Error.Length > 0,
            "Prune streams the actual verbose native result.");
    }

    private static async Task SubmodulesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); using RepositoryFixture sub = await RepositoryFixture.CreateAsync();
        await f.Repository.RunAsync(["submodule", "add", "--", sub.Repository.Path, "module"]); await f.Repository.CommitAsync("Add submodule");
        await NativeAdd(f, "subtree", "subtree"); GitRepository target = await GitRepository.OpenAsync(f.Target("subtree"));
        await target.RunAsync(["submodule", "update", "--init"]);
        WorktreeSnapshot selected = await Selected(f, "subtree");
        Check(!(await f.Repository.DeleteWorktreeAsync(selected)).Result.Success && Directory.Exists(Path.Combine(f.Target("subtree"), "module")), "Native removal retains an initialized submodule without force consent.");
        Check((await f.Repository.DeleteWorktreeAsync(selected, discardChanges: true)).Result.Success && Directory.Exists(f.Root.File("module")),
            "Explicit native forced removal keeps the source submodule/repository intact.");
    }

    private static async Task BareLinkedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository bare = await GitRepository.InitAsync(f.Target("bare.git"), bare: true);
        await f.Repository.RunAsync(["push", "--", bare.Path, "HEAD:refs/heads/main"]); await bare.SetConfigAsync("worktree.useRelativePaths", "false");
        WorktreeOperationResult created = await bare.CreateWorktreeAsync(new(f.Target("from-bare"), "main"));
        Check(created.Result.Success && created.Worktree?.CanOpen == true && (await bare.GetWorktreeSnapshotsAsync()).Single(s => s.IsMain).IsBare, "Bare repositories manage linked worktrees safely.");
        GitRepository linked = await GitRepository.OpenAsync(f.Target("from-bare"));
        WorktreeOperationResult another = await linked.CreateWorktreeAsync(new(f.Target("from-linked"), "HEAD", "another"));
        Check(another.Result.Success && (await linked.GetStatusAsync()).Branch == "main" && await f.Repository.ResolveRevisionAsync("HEAD") == f.Initial,
            "Creating from a linked repository preserves both source checkouts.");
    }

    private static async Task HookFailureAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Hook("echo native-post-checkout >&2\nexit 1\n"); Pipes pipes = new();
        WorktreeOperationResult result = await f.Repository.CreateWorktreeAsync(new(f.Target("hook-failure"), "HEAD", "hook-failure"), outputReceived: pipes.Receive);
        Check(!result.Result.Success && result.Worktree?.Exists == true && await f.Repository.ResolveRevisionAsync("hook-failure") == f.Initial
            && Encoding.UTF8.GetString(pipes.Error).Contains("native-post-checkout", StringComparison.Ordinal), "A hook failure keeps Git's actual created branch and worktree and exposes its diagnostics.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string marker = f.Target("hook-pid");
        await f.Hook("echo $$ > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\necho live-worktree-hook >&2\nsleep 60\n");
        Pipes pipes = new(); using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
        Task<WorktreeOperationResult> pending = f.Repository.CreateWorktreeAsync(new(f.Target("cancelled"), "HEAD", "cancelled"), cancel.Token, pipes.Receive);
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("The hook did not stay live."); }
                if (clock.Elapsed > TimeSpan.FromSeconds(8)) { throw new TimeoutException("No native hook witness."); }
                await Task.Delay(20);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(marker)); cancel.Cancel();
            WorktreeOperationException error = await Expect<WorktreeOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(error.HasRepositoryChanges && error.InnerException is OperationCanceledException && !LinuxProcessState.IsRunning(pid)
                && (await f.Repository.GetWorktreeSnapshotsAsync()).Any(s => s.Path == f.Target("cancelled"))
                && await f.Repository.ResolveRevisionAsync("HEAD") == f.Initial, "Cancellation joins native processes and reports retained partial state instead of rollback.");
        }
        finally { cancel.Cancel(); try { await pending; } catch (WorktreeOperationException) { } }
    }

    private static async Task PreCancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await NativeAdd(f, "pre-cancel", "pre-cancel"); WorktreeSnapshot selected = await Selected(f, "pre-cancel");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.CreateWorktreeAsync(new(f.Target("never"), "HEAD", "never"), cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.DeleteWorktreeAsync(selected, true, true, cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.PruneWorktreesAsync(cancelled.Token, null));
        Check(!Directory.Exists(f.Target("never")) && Directory.Exists(selected.Path), "Pre-cancellation cannot create, remove or prune worktrees.");
    }

    private static Task<GitResult> NativeAdd(Fixture f, string path, string branch) => f.Repository.RunAsync(["worktree", "add", "-b", branch, "--", f.Target(path), "HEAD"]);
    private static async Task<WorktreeSnapshot> Selected(Fixture f, string name) => (await f.Repository.GetWorktreeSnapshotsAsync()).Single(s => s.Path == f.Target(name));
    private static bool Bytes(byte[] first, byte[] second) => first.SequenceEqual(second);
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Pipes
    {
        private readonly MemoryStream _output = new(), _error = new(); private readonly object _gate = new();
        public byte[] Output { get { lock (_gate) { return _output.ToArray(); } } }
        public byte[] Error { get { lock (_gate) { return _error.ToArray(); } } }
        public void Receive(GitProcessOutput output) { lock (_gate) { (output.Stream == GitProcessOutputStream.StandardOutput ? _output : _error).Write(output.Bytes.Span); } }
    }
    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture root, string initial) { Root = root; Initial = initial; }
        public RepositoryFixture Root { get; }
        public GitRepository Repository => Root.Repository;
        public string Initial { get; }
        public string Target(string name) => Path.Combine(Root.Root, name);
        public static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture root = await RepositoryFixture.CreateAsync(); await root.Repository.SetConfigAsync("worktree.useRelativePaths", "false");
            return new(root, await root.Repository.ResolveRevisionAsync("HEAD"));
        }
        public async Task Hook(string body)
        {
            string directory = Target("hooks"); Directory.CreateDirectory(directory); string script = Path.Combine(directory, "post-checkout");
            await File.WriteAllTextAsync(script, "#!/bin/sh\n" + body);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            await Repository.SetConfigAsync("core.hooksPath", directory);
        }
        public void Dispose() => Root.Dispose();
    }
}
