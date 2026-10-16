using System.Diagnostics;
using System.Text;
using GitExtensions.Linux;

internal static class SubmoduleManagementScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("submodule manager snapshots match native commit descriptions tags and dirty-independent status", StatusAsync),
        ("submodule manager inactive and uninitialized entries retain index commit without a fabricated branch", InactiveAsync),
        ("submodule selected update preserves the other checkout parent index and all commit refs", UpdateAsync),
        ("submodule selected synchronization is native and nonrecursive with two modules and a nested child", SyncAsync),
        ("submodule manager preserves native last-value duplicate URL semantics while keeping removal unavailable", DuplicateValuesAsync),
        ("submodule selected operations preserve literal wildcard whitespace Unicode TAB and LF paths", LiteralAsync),
        ("submodule selected operations refuse stale registrations and selected gitlinks before mutation", StaleAsync),
        ("submodule selected update permits unrelated staged changes and preserves their exact bytes", UnrelatedIndexAsync),
        ("submodule manager removal selections reuse the existing safe registration removal workflow", RemovalAsync),
        ("submodule manager opens only exact distinct module roots and preserves the injected Git runner", OpenAsync),
        ("submodule manager refuses replaced directories registrations and parent-repository fallback", ReplacedAsync),
        ("submodule manager retains unmerged gitlinks visibly and returns native update skip without fake hashes", ConflictAsync),
        ("submodule selected update retains native dirty-checkout refusal and user files", DirtyAsync),
        ("submodule selected update preserves native hook failure and its actual partial checkout", HookAsync),
        ("submodule selected update cancellation joins native hook processes without reverting partial checkout", CancellationAsync),
        ("submodule manager supports linked and descendant roots while bare and precancelled operations refuse safely", LinkedBareCancelAsync),
    ];

    private static async Task StatusAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitSubmoduleSnapshot one = await f.Selected("one"); string native = (await f.Native("submodule", "status", "--", "one")).StandardOutput;
        Check(one.Commit == f.NewHead && one.IndexHead == f.NewHead && one.Head == f.NewHead && one.IsInitialized && one.IsUpToDate
            && one.Status == "Up-to-date" && native.EndsWith(" (" + one.Branch + ")\n", StringComparison.Ordinal), "Commit and Branch reproduce native submodule status.");
        await File.AppendAllTextAsync(f.File("one/seed.txt"), "dirty\n");
        Check((await f.Selected("one")).Status == "Up-to-date", "Dirty files alone are not a modified gitlink.");
        await f.Module("one").RunAsync(["reset", "--hard", "HEAD"]); await f.Module("one").CheckoutAsync(f.OldHead, detached: true);
        one = await f.Selected("one");
        Check(one.Status == "Modified" && one.Commit == f.OldHead && one.IndexHead == f.NewHead && !one.IsUpToDate, "A different checkout commit is Modified.");
        await f.Module("one").RunAsync(["tag", "-a", "annotated", "-m", "Annotated", f.OldHead]);
        one = await f.Selected("one"); native = (await f.Native("submodule", "status", "--", "one")).StandardOutput;
        Check(one.Branch == "annotated" && native.EndsWith(" (annotated)\n", StringComparison.Ordinal), "An annotated tag takes native precedence over the fallback branch description.");
    }

    private static async Task InactiveAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Module("one").CheckoutAsync(f.OldHead, detached: true);
        await f.Repository.SetConfigAsync("submodule.one.active", "false"); GitSubmoduleSnapshot inactive = await f.Selected("one");
        Check(!inactive.IsInitialized && inactive.Status == "Not initialized" && inactive.Commit == f.NewHead && inactive.Head == f.OldHead
            && inactive.Branch == "" && inactive.CanOpen && inactive.RemovalSelection?.IsInitialized == true,
            "Native inactive status uses the index commit even when a distinct working repository remains available.");
        await f.Repository.RunAsync(["submodule", "deinit", "-f", "--", "one", "two"]);
        GitSubmoduleSnapshot one = await f.Selected("one"), two = await f.Selected("two");
        Check(one.Commit == f.NewHead && one.Head is null && !one.CanOpen && one.CanUpdate && one.CanSync && one.CanRemove && one.Branch == "", "Uninitialized rows retain usable update/sync/removal actions and their actual indexed commit.");
        Check((await f.Repository.UpdateSubmoduleAsync(one)).Success && File.Exists(f.File("one/seed.txt")) && !File.Exists(f.File("two/.git")), "Initializing the selected module leaves the other module uninitialized.");
        Check(two.IndexHead == f.NewHead, "The second gitlink remains intact.");
    }

    private static async Task UpdateAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.OlderCheckouts(); byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        string parent = await f.Repository.ResolveRevisionAsync("HEAD"); Pipes pipes = new();
        GitResult result = await f.Repository.UpdateSubmoduleAsync(await f.Selected("one"), outputReceived: pipes.Receive);
        Check(result.Success && await f.Module("one").ResolveRevisionAsync("HEAD") == f.NewHead && await f.Module("two").ResolveRevisionAsync("HEAD") == f.OldHead,
            "Only the selected checkout advances to its indexed commit.");
        Check(Bytes(index, await File.ReadAllBytesAsync(f.File(".git/index"))) && await f.Repository.ResolveRevisionAsync("HEAD") == parent
            && await f.Module("one").ResolveRevisionAsync(f.OldHead) == f.OldHead && Bytes(pipes.Output, result.StandardOutputBytes) && Bytes(pipes.Error, result.StandardErrorBytes),
            "Update streams the native output and preserves the superproject index, commits and module objects.");
    }

    private static async Task SyncAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); using RepositoryFixture nested = await RepositoryFixture.CreateAsync();
        await f.Module("one").AddSubmoduleAsync(nested.Repository.Path, "nested"); await RepositoryFixture.ConfigureAsync(f.Module("one"));
        await f.Module("one").CommitAsync("Add nested");
        string first = Path.Combine(f.Root.Root, "first.git"), second = Path.Combine(f.Root.Root, "second.git"), nestedUrl = Path.Combine(f.Root.Root, "nested.git");
        await f.Native("config", "--file", ".gitmodules", "submodule.one.url", first); await f.Native("config", "--file", ".gitmodules", "submodule.two.url", second);
        await Native(f.File("one"), "config", "--file", ".gitmodules", "submodule.nested.url", nestedUrl);
        string oldNested = Line((await Native(f.File("one"), "config", "--get", "submodule.nested.url")).StandardOutput);
        string oldTwo = Line((await f.Native("config", "--get", "submodule.two.url")).StandardOutput); Pipes pipes = new();
        GitResult result = await f.Repository.SyncSubmoduleAsync(await f.Selected("one"), outputReceived: pipes.Receive);
        Check(result.Success && Line((await f.Native("config", "--get", "submodule.one.url")).StandardOutput) == first
            && Line((await Native(f.File("one"), "config", "--get", "remote.origin.url")).StandardOutput) == first, "Native sync updates the selected root registration and its initialized remote.");
        Check(Line((await f.Native("config", "--get", "submodule.two.url")).StandardOutput) == oldTwo
            && Line((await Native(f.File("one"), "config", "--get", "submodule.nested.url")).StandardOutput) == oldNested,
            "Neither the other top-level module nor the selected module's nested registration is synchronized.");
        Check(Bytes(pipes.Output, result.StandardOutputBytes) && Bytes(pipes.Error, result.StandardErrorBytes), "Sync preserves streamed native diagnostics.");
    }

    private static async Task LiteralAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string literal = "mods/été [a]*\tand\nline (suffix)";
        Check((await f.Native("submodule", "add", "--name", "safe-name", "--", f.Source.Repository.Path, literal)).Success, "Git supports path newlines with a distinct safe module name.");
        GitSubmoduleSnapshot selected = (await f.Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Path == literal);
        Check(selected.Name == "safe-name" && selected.Commit == f.NewHead && selected.ValidationError is null && selected.CanOpen,
            "NUL config/index and exact selected status parsing preserve TAB/LF and parenthesized path suffixes.");
        await f.Module(literal).CheckoutAsync(f.OldHead, detached: true); selected = (await f.Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Path == literal);
        Check((await f.Repository.UpdateSubmoduleAsync(selected)).Success && await f.Module(literal).ResolveRevisionAsync("HEAD") == f.NewHead,
            "A literal wildcard pathname reaches only its exact gitlink.");
        selected = (await f.Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Path == literal);
        Check((await f.Repository.SyncSubmoduleAsync(selected)).Success && (await f.Repository.OpenSubmoduleRepositoryAsync((await f.Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Path == literal))).Path == f.File(literal),
            "Sync and opening preserve the exact registered path too.");
        foreach ((string name, string path) in new[] { ("safe-lf", "ending-lf\n"), ("safe-cr", "ending-cr\r") })
        {
            Check((await f.Native("submodule", "add", "--name", name, "--", f.Source.Repository.Path, path)).Success, "Add a path ending in a literal line terminator with a safe section name.");
            Check((await f.Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Name == name).Path == path, "Initialized status preserves the complete pathname before its annotation.");
            await f.Native("submodule", "deinit", "-f", "--", path);
            GitSubmoduleSnapshot uninitialized = (await f.Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Name == name);
            Check(uninitialized.Path == path && uninitialized.Commit == f.NewHead && uninitialized.Branch == "" && uninitialized.ValidationError is null
                && !uninitialized.IsInitialized, "Without an annotation only Git's single output LF is removed; the path's own trailing LF or CR remains intact.");
        }
    }

    private static async Task DuplicateValuesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string alternative = Path.Combine(f.Root.Root, "alternative.git");
        Check((await f.Native("clone", "--bare", "--", f.Source.Repository.Path, alternative)).Success, "Create another local source for the native last-value check.");
        await f.Native("config", "--file", ".gitmodules", "--add", "submodule.one.url", alternative);
        GitSubmoduleSnapshot selected = await f.Selected("one");
        Check(selected.Url == alternative && selected.CanUpdate && selected.CanSync && !selected.CanRemove && selected.RemovalSelection is null,
            "Update and Sync retain native last-value behavior; the stricter existing removal transaction cannot accept an ambiguous URL list.");
        Check((await f.Repository.SyncSubmoduleAsync(selected)).Success
            && Line((await f.Native("config", "--get", "submodule.one.url")).StandardOutput) == alternative,
            "Native synchronization uses the last URL while retaining all .gitmodules values.");
        await f.Module("one").CheckoutAsync(f.OldHead, detached: true);
        Check((await f.Repository.UpdateSubmoduleAsync(await f.Selected("one"))).Success && await f.Module("one").ResolveRevisionAsync("HEAD") == f.NewHead,
            "Selected update stays available after synchronizing the repeated URL registration.");
        string[] values = (await f.Native("config", "--file", ".gitmodules", "--null", "--get-all", "submodule.one.url")).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Check(values.SequenceEqual([f.Source.Repository.Path, alternative]), "Neither operation collapses or rewrites the user's repeated configuration values.");
    }

    private static async Task StaleAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleSnapshot selected = await f.Selected("one");
        await File.AppendAllTextAsync(f.File(".gitmodules"), "# external change\n"); byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        await Refused(f.Repository, selected);
        Check(Bytes(index, await File.ReadAllBytesAsync(f.File(".git/index"))), "A changed registration is refused without an indexed mutation.");
        selected = await f.Selected("one"); await f.Module("one").CheckoutAsync(f.OldHead, detached: true); await f.Repository.StageAsync(["one"]);
        await Refused(f.Repository, selected);
        Check(await f.Module("one").ResolveRevisionAsync("HEAD") == f.OldHead, "A changed selected gitlink is never silently updated from an old confirmation.");
    }

    private static async Task UnrelatedIndexAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Module("one").CheckoutAsync(f.OldHead, detached: true); GitSubmoduleSnapshot selected = await f.Selected("one");
        f.Root.Write("unrelated", "staged\n"); await f.Repository.StageAsync(["unrelated"]); f.Root.Write("unrelated", "unstaged\n");
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        Check((await f.Repository.UpdateSubmoduleAsync(selected)).Success && Bytes(index, await File.ReadAllBytesAsync(f.File(".git/index")))
            && File.ReadAllText(f.File("unrelated")) == "unstaged\n", "Only the selected gitlink's index signature is guarded; unrelated staged/unstaged work remains usable and byte-exact.");
    }

    private static async Task RemovalAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleSnapshot selected = await f.Selected("one");
        GitSubmoduleRemovalSelection existing = (await f.Repository.GetSubmoduleRemovalSelectionsAsync()).Single(item => item.Name == "one");
        Check(selected.RemovalSelection == existing, "Management supplies exactly the existing removal service's confirmation contract.");
        await f.Repository.RemoveSubmoduleAsync(selected.RemovalSelection!);
        Check((await f.Repository.GetSubmoduleSnapshotsAsync()).Select(item => item.Name).SequenceEqual(["two"]) && File.Exists(f.File("one/seed.txt")), "Removing the registration retains all module data and does not resurrect its row.");
        await f.Repository.RemoveSubmoduleAsync((await f.Selected("two")).RemovalSelection!);
        Check((await f.Repository.GetSubmoduleSnapshotsAsync()).Count == 0 && File.Exists(f.File(".gitmodules")), "The final retained worktree .gitmodules does not resurrect removed gitlinks.");
    }

    private static async Task OpenAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleSnapshot selected = await f.Selected("one");
        GitRepository opened = await f.Repository.OpenSubmoduleRepositoryAsync(selected);
        Check(opened.Path == f.File("one") && (await opened.GetInfoAsync()).CommonDirectory != (await f.Repository.GetInfoAsync()).CommonDirectory,
            "Opening selects the actual child repository rather than discovering the superproject upward.");
        if (!OperatingSystem.IsWindows())
        {
            string marker = Path.Combine(f.Root.Root, "runner-used"), wrapper = Path.Combine(f.Root.Root, "observe-git");
            await Executable(wrapper, "#!/bin/sh\ncase \"$*\" in *'status --porcelain=2'*) echo used > " + Quote(marker) + ";; esac\nexec git \"$@\"\n");
            GitRepository injected = new(f.Repository.Path, new GitRunner(wrapper));
            GitSubmoduleSnapshot snapshot = (await injected.GetSubmoduleSnapshotsAsync()).Single(item => item.Name == "one");
            GitRepository retained = await injected.OpenSubmoduleRepositoryAsync(snapshot); await retained.GetStatusAsync();
            Check(File.Exists(marker), "The module repository keeps the injected runner for subsequent Pull/Fetch operations.");
        }
        using RepositoryFixture foreign = await RepositoryFixture.CreateAsync(); await Expect<ArgumentException>(() => foreign.Repository.OpenSubmoduleRepositoryAsync(selected));
    }

    private static async Task ReplacedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleSnapshot selected = await f.Selected("one");
        string old = Path.Combine(f.Root.Root, "saved-one"); Directory.Move(f.File("one"), old); Directory.CreateDirectory(f.File("one"));
        await Refused(f.Repository, selected); Directory.Delete(f.File("one")); Directory.Move(old, f.File("one"));
        selected = await f.Selected("one"); string marker = f.File("one/.git"), saved = f.File("one/saved-marker"); File.Move(marker, saved);
        await File.WriteAllTextAsync(marker, "gitdir: " + f.File(".git") + "\n");
        GitSubmoduleSnapshot invalid = await f.Selected("one");
        Check(!invalid.CanOpen && !invalid.CanUpdate && !invalid.CanSync && !invalid.CanRemove && invalid.ValidationError is not null,
            "A .git pointer to the parent can never authorize a Pull or selected mutation on the parent repository.");
        await Refused(f.Repository, selected); File.Delete(marker); File.Move(saved, marker);
        if (!OperatingSystem.IsWindows())
        {
            selected = await f.Selected("one"); Directory.Move(f.File("one"), old); Directory.CreateSymbolicLink(f.File("one"), old);
            await Refused(f.Repository, selected); Directory.Delete(f.File("one")); Directory.Move(old, f.File("one"));
        }
    }

    private static async Task ConflictAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string zero = new('0', f.NewHead.Length);
        string input = "0 " + zero + "\tone\n160000 " + f.OldHead + " 1\tone\n160000 " + f.NewHead + " 2\tone\n160000 " + f.OldHead + " 3\tone\n";
        Check((await new GitRunner().RunAsync(f.Repository.Path, ["update-index", "--index-info"], standardInput: input)).Success, "Create a real unmerged gitlink.");
        GitSubmoduleSnapshot one = await f.Selected("one");
        Check(one.IsConflicted && one.Status == "Conflicted" && one.Commit is null && one.IndexHead is null && one.RemovalSelection is null
            && !one.CanRemove && one.CanUpdate && one.CanSync && one.CanOpen, "Unmerged rows are visible without an invented stage0/zero commit or unsafe removal selection.");
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index")); Pipes pipes = new(); GitResult result = await f.Repository.UpdateSubmoduleAsync(one, outputReceived: pipes.Receive);
        Check(result.Success && result.StandardError.Contains("Skipping unmerged submodule one", StringComparison.Ordinal)
            && Bytes(index, await File.ReadAllBytesAsync(f.File(".git/index"))) && (await f.Selected("one")).IsConflicted,
            "The native successful skip remains a skip, with conflicts still visible and the index untouched.");
    }

    private static async Task DirtyAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Module("one").CheckoutAsync(f.OldHead, detached: true);
        await File.WriteAllTextAsync(f.File("one/seed.txt"), "valuable dirty checkout\n"); await File.WriteAllTextAsync(f.File("one/untracked"), "keep");
        GitResult result = await f.Repository.UpdateSubmoduleAsync(await f.Selected("one"));
        Check(!result.Success && await f.Module("one").ResolveRevisionAsync("HEAD") == f.OldHead && File.ReadAllText(f.File("one/seed.txt")) == "valuable dirty checkout\n"
            && File.ReadAllText(f.File("one/untracked")) == "keep", "Native update protects dirty tracked and untracked data without an implicit force.");
    }

    private static async Task HookAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.CreateAsync(); await f.OlderCheckouts(); await f.Hook("echo submodule-update-hook >&2\nexit 23\n"); Pipes pipes = new();
        GitResult result = await f.Repository.UpdateSubmoduleAsync(await f.Selected("one"), outputReceived: pipes.Receive);
        Check(!result.Success && await f.Module("one").ResolveRevisionAsync("HEAD") == f.NewHead && await f.Module("two").ResolveRevisionAsync("HEAD") == f.OldHead
            && Encoding.UTF8.GetString(pipes.Error).Contains("submodule-update-hook", StringComparison.Ordinal),
            "A native hook failure retains the actual partially completed checkout and its streamed diagnostics.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); await f.OlderCheckouts(); string marker = Path.Combine(f.Root.Root, "hook-pid");
        await f.Hook("echo $$ > " + Quote(marker) + "\necho live-submodule-update-hook >&2\nsleep 60\n");
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20)); Pipes pipes = new();
        Task<GitResult> pending = f.Repository.UpdateSubmoduleAsync(await f.Selected("one"), cancel.Token, pipes.Receive);
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Native update hook did not stay live."); }
                if (clock.Elapsed > TimeSpan.FromSeconds(8)) { throw new TimeoutException("No native update hook witness."); }
                await Task.Delay(20);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(marker)); cancel.Cancel();
            await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(!LinuxProcessState.IsRunning(pid) && await f.Module("one").ResolveRevisionAsync("HEAD") == f.NewHead
                && await f.Module("two").ResolveRevisionAsync("HEAD") == f.OldHead && pipes.Error.Length > 0,
                "Abort joins native descendants and retains the checkout already performed, without changing the other module.");
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static async Task LinkedBareCancelAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = Path.Combine(f.Root.Root, "linked"); await f.Repository.AddWorktreeAsync(linked, "HEAD", "linked");
        GitRepository repository = await GitRepository.OpenAsync(linked); GitSubmoduleSnapshot selected = (await repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Name == "one");
        Check(!selected.IsInitialized && selected.Commit == f.NewHead && (await repository.UpdateSubmoduleAsync(selected)).Success
            && File.Exists(Path.Combine(linked, "one/seed.txt")), "Linked-worktree management resolves its own index, metadata and selected checkout.");
        Directory.CreateDirectory(Path.Combine(linked, "descendant")); GitRepository descendant = new(Path.Combine(linked, "descendant"));
        Check((await descendant.GetSubmoduleSnapshotsAsync()).Count == 2, "A descendant is normalized to its actual worktree root.");
        using CancellationTokenSource cancel = new(); cancel.Cancel(); selected = (await repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Name == "one");
        await Expect<OperationCanceledException>(() => repository.UpdateSubmoduleAsync(selected, cancel.Token));
        await Expect<OperationCanceledException>(() => repository.SyncSubmoduleAsync(selected, cancel.Token));
        await Expect<OperationCanceledException>(() => repository.OpenSubmoduleRepositoryAsync(selected, cancel.Token));
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root.Root, "bare.git"), bare: true);
        await Expect<InvalidOperationException>(() => bare.GetSubmoduleSnapshotsAsync());
    }

    private static async Task Refused(GitRepository repository, GitSubmoduleSnapshot selected)
    {
        await Expect<InvalidOperationException>(() => repository.UpdateSubmoduleAsync(selected));
        await Expect<InvalidOperationException>(() => repository.SyncSubmoduleAsync(selected));
        await Expect<InvalidOperationException>(() => repository.OpenSubmoduleRepositoryAsync(selected));
    }
    private static Task<GitResult> Native(string cwd, params string[] args) => new GitRunner().RunAsync(cwd, args);
    private static string Line(string value) => value.EndsWith('\n') ? value[..^1] : value;
    private static bool Bytes(byte[] first, byte[] second) => first.SequenceEqual(second);
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task Executable(string path, string text)
    { await File.WriteAllTextAsync(path, text); if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
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
        private Fixture(RepositoryFixture root, RepositoryFixture source, string oldHead, string newHead)
        { Root = root; Source = source; OldHead = oldHead; NewHead = newHead; }
        public RepositoryFixture Root { get; }
        public RepositoryFixture Source { get; }
        public GitRepository Repository => Root.Repository;
        public string OldHead { get; }
        public string NewHead { get; }
        public string File(string name) => Root.File(name);
        public GitRepository Module(string path) => new(File(path));
        public Task<GitResult> Native(params string[] args) => SubmoduleManagementScenarios.Native(Repository.Path, args);
        public async Task<GitSubmoduleSnapshot> Selected(string name) => (await Repository.GetSubmoduleSnapshotsAsync()).Single(item => item.Name == name);
        public async Task OlderCheckouts() { await Module("one").CheckoutAsync(OldHead, detached: true); await Module("two").CheckoutAsync(OldHead, detached: true); }
        public async Task Hook(string body)
        {
            string hooks = Path.Combine(Root.Root, "hooks"); Directory.CreateDirectory(hooks); await Executable(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\n" + body);
            await Module("one").SetConfigAsync("core.hooksPath", hooks);
        }
        public static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture root = await RepositoryFixture.CreateAsync(), source = await RepositoryFixture.CreateAsync();
            string old = await source.Repository.ResolveRevisionAsync("HEAD"); source.Write("seed.txt", "new seed\n");
            await source.Repository.StageAllAsync(); await source.Repository.CommitAsync("New module commit"); string current = await source.Repository.ResolveRevisionAsync("HEAD");
            await root.Repository.AddSubmoduleAsync(source.Repository.Path, "one"); await root.Repository.AddSubmoduleAsync(source.Repository.Path, "two");
            await root.Repository.CommitAsync("Register modules"); await RepositoryFixture.ConfigureAsync(new(root.File("one"))); await RepositoryFixture.ConfigureAsync(new(root.File("two")));
            return new(root, source, old, current);
        }
        public void Dispose() { Root.Dispose(); Source.Dispose(); }
    }
}
