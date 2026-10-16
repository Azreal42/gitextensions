using System.Diagnostics;
using System.Text;
using GitExtensions.Linux;

internal static class SubmoduleAddScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("submodule add stages native gitlink and gitmodules with exact literal arguments and output", AddAsync),
        ("submodule add trims only branch and preserves dot branch native behavior", BranchAsync),
        ("submodule add accepts an existing Git checkout and retains its dirty untracked files", ExistingAsync),
        ("submodule add force overrides ignored paths without discarding existing checkout files", ForceAsync),
        ("submodule add force reuses native retained module storage", ReuseAsync),
        ("submodule add refuses root outside metadata occupied and alias destinations without mutation", ValidationAsync),
        ("submodule add revalidates parent identity immediately before native mutation", RevalidationAsync),
        ("submodule add and suggestions operate at linked and descendant repository roots and reject bare", LinkedBareAsync),
        ("submodule branch suggestions resolve raw last remote URL before insteadOf without editing index", RelativeAsync),
        ("submodule branch suggestions honor upstream dot detached and sole remote version behavior", DefaultRemoteAsync),
        ("submodule branch suggestions preserve URL colon slash and interior dot path semantics", RelativeEdgesAsync),
        ("submodule branch suggestions run transports from current repository with literal sources", SuggestionsAsync),
        ("submodule add native errors return diagnostics while legacy wrapper still throws", FailureAsync),
        ("submodule add hook failure retains native module checkout and partial registration state", HookAsync),
        ("submodule add cancellation joins live native hook processes and retains partial effects", CancellationAsync),
        ("submodule add pre-cancellation and invalid values cannot change repository files", PreCancellationAsync),
    ];

    private static async Task AddAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        string path = "modules/été \\ '$(marker); child"; string head = await f.Repository.ResolveRevisionAsync("HEAD");
        string source = Path.Combine(child.Root, "source été '$();\nchild.git"); await Bare(child, source, "source-literal");
        f.Write("unrelated.txt", "keep\n"); Pipes pipes = new();
        GitResult result = await f.Repository.AddSubmoduleAsync(new(source, path), outputReceived: pipes.Receive);
        Check(result.Success, result.StandardError);
        GitSubmodule module = (await f.Repository.GetSubmodulesAsync()).Single();
        Check(module.Path == path && module.Url == source && module.ExpectedHead == await child.Repository.ResolveRevisionAsync("HEAD"), "Native registration retains the literal pathname, URL and gitlink identity.");
        GitResult index = await Native(f.Repository.Path, "ls-files", "--stage", "-z");
        Check(index.StandardOutput.Contains("160000 ", StringComparison.Ordinal) && index.StandardOutput.Contains("\t" + path + "\0", StringComparison.Ordinal)
            && index.StandardOutput.Contains("\t.gitmodules\0", StringComparison.Ordinal), "Native add stages both gitlink and .gitmodules.");
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && File.Exists(f.File("unrelated.txt")) && !File.Exists(f.File("marker")), "No implicit commit or shell evaluation occurs.");
        Check(Bytes(pipes.Output, result.StandardOutputBytes) && Bytes(pipes.Error, result.StandardErrorBytes) && pipes.Error.Length > 0, "Streamed bytes match all retained native output.");
        using RepositoryFixture oracle = await RepositoryFixture.CreateAsync(); string newline = "newline\nchild";
        GitResult expected = await Native(oracle.Repository.Path, "submodule", "add", "--", source, newline);
        GitResult actual = await f.Repository.AddSubmoduleAsync(new(source, newline));
        Check(!expected.Success && actual.ExitCode == expected.ExitCode && Directory.Exists(f.File(newline))
            && Directory.Exists(f.File(".git/modules/" + newline)) && actual.StandardError.Contains("invalid key (newline)", StringComparison.Ordinal),
            "A destination newline retains Git's actual configuration refusal and partial clone, without claiming successful registration or destructive cleanup.");
    }

    private static async Task BranchAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        string branch = "feature/été-$(marker);'"; await child.Repository.RunAsync(["checkout", "-b", branch]);
        child.Write("branch.txt", "branch\n"); await child.Repository.StageAllAsync(); await child.Repository.CommitAsync("Branch");
        GitResult result = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "branch", "  " + branch + " \t"));
        Check(result.Success && (await GitRepository.OpenAsync(f.File("branch"))).Info is not null, result.StandardError);
        GitRepository added = await GitRepository.OpenAsync(f.File("branch"));
        Check((await added.GetStatusAsync()).Branch == branch && await added.ResolveRevisionAsync("HEAD") == await child.Repository.ResolveRevisionAsync("HEAD"), "The exact trimmed branch selects the native checkout.");
        Check((await Native(f.Repository.Path, "config", "--file", ".gitmodules", "--get", "submodule.branch.branch")).StandardOutput == branch + "\n", "Native Git records the selected branch.");
        using RepositoryFixture oracle = await RepositoryFixture.CreateAsync();
        GitResult expected = await Native(oracle.Repository.Path, "submodule", "add", "-b", ".", "--", child.Repository.Path, "dot");
        GitResult actual = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "dot", " . "));
        Check(actual.ExitCode == expected.ExitCode && Directory.Exists(f.File("dot")) == Directory.Exists(oracle.File("dot")), "Dot is passed through, including native Git's possible partial failure.");
        await GitRepository.CloneAsync(child.Repository.Path, f.File("existing-dot"), recursive: false);
        GitResult existing = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "existing-dot", "."));
        Check(existing.Success && (await Native(f.Repository.Path, "config", "--file", ".gitmodules", "--get", "submodule.existing-dot.branch")).StandardOutput == ".\n",
            "For an existing checkout Git accepts dot as an update-tracking branch without trying to create a dot branch.");
    }

    private static async Task ExistingAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        GitRepository existing = await GitRepository.CloneAsync(child.Repository.Path, f.File("existing"), recursive: false);
        await File.AppendAllTextAsync(f.File("existing/seed.txt"), "dirty\n"); await File.WriteAllTextAsync(f.File("existing/untracked"), "keep");
        GitResult result = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "existing"));
        Check(result.Success && File.ReadAllText(f.File("existing/untracked")) == "keep" && File.ReadAllText(f.File("existing/seed.txt")).Contains("dirty", StringComparison.Ordinal)
            && await existing.ResolveRevisionAsync("HEAD") == await child.Repository.ResolveRevisionAsync("HEAD"), "Existing valid repositories are registered without a clone or checkout reset.");
    }

    private static async Task ForceAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        f.Write(".gitignore", "ignored\n"); await GitRepository.CloneAsync(child.Repository.Path, f.File("ignored"), recursive: false);
        await File.WriteAllTextAsync(f.File("ignored/untracked"), "keep");
        GitResult refused = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "ignored"));
        Check(!refused.Success && File.Exists(f.File("ignored/untracked")), "Without force Git retains the ignored repository.");
        GitResult result = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "ignored", Force: true));
        Check(result.Success && File.ReadAllText(f.File("ignored/untracked")) == "keep", "Add force consents to ignored path registration, not data discard.");
    }

    private static async Task ReuseAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        Check((await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "module"))).Success, "Initial add.");
        await f.Repository.RunAsync(["submodule", "deinit", "-f", "--", "module"]); await f.Repository.RunAsync(["rm", "-f", "--", "module"]);
        GitResult refused = await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "module"));
        Check(!refused.Success && Directory.Exists(f.File(".git/modules/module")), "Native retained module repository requires explicit force.");
        Check((await f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "module", Force: true))).Success
            && File.Exists(f.File("module/seed.txt")), "Native -f reactivates retained module storage.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index")); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        foreach (string path in new[] { ".", "..", "../outside", f.Repository.Path, ".git/module", ".gitmodules", "x/../../outside" })
        { await Expect<ArgumentException>(() => f.Repository.AddSubmoduleAsync(new(child.Repository.Path, path, Force: true))); }
        f.Write("occupied/sentinel", "keep");
        await Expect<ArgumentException>(() => f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "occupied", Force: true)));
        if (!OperatingSystem.IsWindows())
        {
            string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(f.File("alias"), outside); Directory.CreateSymbolicLink(f.File("inside-alias"), f.Repository.Path);
            Directory.CreateSymbolicLink(f.File("metadata-alias"), f.File(".git"));
            foreach (string path in new[] { "alias/child", "inside-alias/child", "metadata-alias/child", "alias" })
            { await Expect<ArgumentException>(() => f.Repository.AddSubmoduleAsync(new(child.Repository.Path, path, Force: true))); }
            Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "Aliases cannot direct a clone outside the selected working tree.");
        }
        Check(Bytes(index, await File.ReadAllBytesAsync(f.File(".git/index"))) && !File.Exists(f.File(".gitmodules")) && await f.Repository.ResolveRevisionAsync("HEAD") == head
            && File.ReadAllText(f.File("occupied/sentinel")) == "keep", "Every rejected destination preserves index, files and source HEAD.");
    }

    private static async Task RevalidationAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        Directory.CreateDirectory(f.File("parent")); string wrapper = Path.Combine(f.Root, "replace-parent"), counter = Path.Combine(f.Root, "counter"), invoked = Path.Combine(f.Root, "native-add-entered");
        // The second discovery begins after the initial destination observation.
        string script = "#!/bin/sh\ncase \"$*\" in\n *'rev-parse --absolute-git-dir'*) n=0; test ! -f " + Quote(counter) + " || n=$(cat " + Quote(counter) + "); n=$((n+1)); echo $n > " + Quote(counter) + "; if test $n -eq 2; then mv " + Quote(f.File("parent")) + " " + Quote(f.File("old-parent")) + "; mkdir " + Quote(f.File("parent")) + "; fi ;;\n *'submodule add'*) echo entered > " + Quote(invoked) + " ;;\nesac\nexec git \"$@\"\n";
        await Executable(wrapper, script); GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper));
        await Expect<InvalidOperationException>(() => observed.AddSubmoduleAsync(new(child.Repository.Path, "parent/child")));
        Check(!File.Exists(invoked) && !Directory.Exists(f.File("parent/child")) && Directory.Exists(f.File("old-parent")), "A replaced parent is refused before invoking the native mutation.");
    }

    private static async Task LinkedBareAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        string linked = Path.Combine(f.Root, "linked"); await f.Repository.AddWorktreeAsync(linked, "HEAD", "linked");
        GitRepository repository = await GitRepository.OpenAsync(linked); byte[] mainIndex = await File.ReadAllBytesAsync(f.File(".git/index"));
        Check((await repository.AddSubmoduleAsync(new(child.Repository.Path, "child"))).Success && File.Exists(Path.Combine(linked, ".gitmodules"))
            && !File.Exists(f.File(".gitmodules")) && Bytes(mainIndex, await File.ReadAllBytesAsync(f.File(".git/index"))), "A linked checkout receives its own gitlink and .gitmodules.");
        Directory.CreateDirectory(f.File("nested")); GitRepository descendant = new(f.File("nested"));
        Check((await descendant.AddSubmoduleAsync(new(child.Repository.Path, "at-root"))).Success && File.Exists(f.File("at-root/seed.txt"))
            && !Directory.Exists(f.File("nested/at-root")), "Path validation and execution use the discovered repository root.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        await Expect<InvalidOperationException>(() => bare.AddSubmoduleAsync(new(child.Repository.Path, "child")));
        await Expect<InvalidOperationException>(() => bare.GetSubmoduleRemoteBranchesAsync(child.Repository.Path));
    }

    private static async Task RelativeAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        string a = Path.Combine(f.Root, "a"), b = Path.Combine(f.Root, "b"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        await Bare(child, Path.Combine(a, "child.git"), "from-a"); await Bare(child, Path.Combine(b, "child.git"), "from-b");
        await f.Repository.RunAsync(["remote", "add", "origin", Path.Combine(a, "super.git")]); await f.Repository.RunAsync(["config", "--add", "remote.origin.url", Path.Combine(b, "super.git")]);
        byte[] before = await File.ReadAllBytesAsync(f.File(".git/index"));
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git")).Contains("from-b"), "The last raw URL, not remote get-url's first URL, determines the sibling.");
        await f.Repository.SetConfigAsync("remote.origin.url", "alias:super.git");
        await f.Repository.SetConfigAsync("url." + Path.Combine(a, "super.git") + ".insteadOf", "alias:super.git");
        await f.Repository.SetConfigAsync("url." + Path.Combine(b, "child.git") + ".insteadOf", "alias:child.git");
        IReadOnlyList<string> heads = await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git");
        Check(heads.Contains("from-b") && !heads.Contains("from-a"), "The combined sibling is rewritten, never the remote base before combination.");
        Check(Bytes(before, await File.ReadAllBytesAsync(f.File(".git/index"))) && !File.Exists(f.File(".gitmodules")), "Suggestions do not stage or register anything.");
        // Pass rewrite config explicitly so Git clone children use it, as with a user's global config.
        string global = Path.Combine(f.Root, "rewrite.gitconfig");
        await File.WriteAllTextAsync(global, "[url \"" + Path.Combine(b, "child.git") + "\"]\n\tinsteadOf = alias:child.git\n");
        await f.Repository.SetConfigAsync("include.path", global);
        string wrapper = Path.Combine(f.Root, "rewrite-git");
        await Executable(wrapper, "#!/bin/sh\nexec git -c " + Quote("include.path=" + global) + " \"$@\"\n");
        GitResult add = await new GitRepository(f.Repository.Path, new GitRunner(wrapper)).AddSubmoduleAsync(new("../child.git", "relative"));
        Check(add.Success, add.StandardError);
        Check((await Native(f.Repository.Path, "config", "--file", ".gitmodules", "--get", "submodule.relative.url")).StandardOutput == "../child.git\n", "Relative source spelling is retained verbatim in .gitmodules.");
    }

    private static async Task DefaultRemoteAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        string a = Path.Combine(f.Root, "a"), b = Path.Combine(f.Root, "b"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        await Bare(child, Path.Combine(a, "child.git"), "from-a"); await Bare(child, Path.Combine(b, "child.git"), "from-b");
        await Bare(child, Path.Combine(f.Root, "child.git"), "from-cwd");
        await f.Repository.RunAsync(["remote", "add", "origin", Path.Combine(a, "super.git")]); await f.Repository.RunAsync(["remote", "add", "upstream", Path.Combine(b, "super.git")]);
        await f.Repository.SetConfigAsync("branch.main.remote", "upstream");
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git")).Contains("from-b"), "Configured branch remote applies even without merge.");
        await f.Repository.RunAsync(["checkout", "--detach"]);
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git")).Contains("from-a"), "Detached HEAD defaults to origin.");
        await f.Repository.RunAsync(["checkout", "main"]); await f.Repository.SetConfigAsync("branch.main.remote", ".");
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git")).Contains("from-cwd"), "Dot tracking without a URL resolves against the superproject.");
        await f.Repository.UnsetConfigAsync("branch.main.remote"); await f.Repository.RunAsync(["remote", "remove", "origin"]);
        GitResult version = await Native(f.Repository.Path, "--version"); Version git = Version.Parse(version.StandardOutput.Split(' ')[2].Trim());
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git")).Contains(git >= new Version(2, 51) ? "from-b" : "from-cwd"), "Sole-remote fallback follows the installed Git version.");
    }

    private static async Task RelativeEdgesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        await Bare(child, f.File("child.git"), "inside-root"); await Bare(child, Path.Combine(f.Root, "child.git"), "outside-root");
        await f.Repository.SetConfigAsync("remote.origin.url", ".");
        string other = Path.Combine(f.Root, "other.git"); await Bare(child, other, "wrong-remote-alias");
        await f.Repository.RunAsync(["remote", "add", "child.git", other]);
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git")).Contains("inside-root"), "Explicit remote URL dot is distinct from a missing URL.");
        await f.Repository.SetConfigAsync("remote.origin.url", "");
        await Expect<ArgumentException>(() => f.Repository.GetSubmoduleRemoteBranchesAsync("../child.git"));
        await f.Repository.SetConfigAsync("remote.origin.url", f.File("base")); Directory.CreateDirectory(f.File("base/foo"));
        await Bare(child, f.File("base/bar.git"), "interior-dots");
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("./foo/../bar.git/")).Contains("interior-dots"), "Interior dot segments and a trailing URL slash retain native path semantics.");
    }

    private static async Task SuggestionsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        await child.Repository.RunAsync(["branch", "feature/été-$(literal);'"]);
        await f.Repository.SetConfigAsync("url." + child.Repository.Path + ".insteadOf", "ssh://example.invalid/team/child");
        IReadOnlyList<string> branches = await f.Repository.GetSubmoduleRemoteBranchesAsync("ssh://example.invalid/team/child");
        Check(branches.Contains("main") && branches.Contains("feature/été-$(literal);'") && !File.Exists(f.File("literal")), "Repository-local rewrite configuration and literal refnames are honored.");
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync(new Uri(child.Repository.Path).AbsoluteUri)).SequenceEqual(await f.Repository.GetSubmoduleRemoteBranchesAsync(child.Repository.Path)), "Absolute path and file URL query the same native heads.");
        Check((await f.Repository.GetSubmoduleRemoteBranchesAsync("")).Count == 0, "An empty dropdown source has no suggestions.");
        await Expect<GitCommandException>(() => f.Repository.GetSubmoduleRemoteBranchesAsync(Path.Combine(f.Root, "missing")));
        await Bare(child, f.File("child"), "unqualified-child"); Directory.CreateDirectory(f.File("local")); await Bare(child, f.File("local/child"), "unqualified-nested-child");
        foreach (string source in new[] { "child", "local/child" })
        {
            await Expect<ArgumentException>(() => f.Repository.GetSubmoduleRemoteBranchesAsync(source));
            GitResult native = await Native(f.Repository.Path, "submodule", "add", "--", source, "unqualified");
            Check(!native.Success && !Directory.Exists(f.File("unqualified")) && !File.Exists(f.File(".gitmodules")),
                "A real unqualified child repository cannot produce misleading suggestions when native submodule add refuses its source spelling.");
        }
    }

    private static async Task FailureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string missing = Path.Combine(f.Root, "missing"); Pipes pipes = new();
        GitResult failed = await f.Repository.AddSubmoduleAsync(new(missing, "failure"), outputReceived: pipes.Receive);
        Check(!failed.Success && pipes.Error.Length > 0 && Bytes(pipes.Error, failed.StandardErrorBytes), "The process dialog receives native nonzero diagnostics.");
        await Expect<GitCommandException>(() => f.Repository.AddSubmoduleAsync(missing, "legacy-failure"));
        Check(!File.Exists(f.File(".gitmodules")), "Native failure does not invent successful registration.");
    }

    private static async Task HookAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        GitRepository hooked = await Hooked(f, "echo native-submodule-hook >&2\nexit 27\n"); Pipes pipes = new();
        GitResult result = await hooked.AddSubmoduleAsync(new(child.Repository.Path, "hook"), outputReceived: pipes.Receive);
        Check(!result.Success && Directory.Exists(f.File("hook")) && Directory.Exists(f.File(".git/modules/hook"))
            && Encoding.UTF8.GetString(pipes.Error).Contains("native-submodule-hook", StringComparison.Ordinal), "Native hooks run; partial cloned storage is retained with its diagnostics.");
        Check(!File.Exists(f.File(".gitmodules")), "No manual registration is fabricated after hook failure.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        string marker = Path.Combine(f.Root, "hook-pid"); GitRepository hooked = await Hooked(f, "echo $$ > " + Quote(marker) + "\necho live-submodule-hook >&2\nsleep 60\n");
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20)); Pipes pipes = new();
        Task<GitResult> pending = hooked.AddSubmoduleAsync(new(child.Repository.Path, "cancelled"), cancel.Token, pipes.Receive);
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Native hook did not stay live."); }
                if (clock.Elapsed > TimeSpan.FromSeconds(8)) { throw new TimeoutException("No native hook witness."); }
                await Task.Delay(20);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(marker)); cancel.Cancel();
            await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(!LinuxProcessState.IsRunning(pid) && Directory.Exists(f.File(".git/modules/cancelled")) && pipes.Error.Length > 0,
                "Cancellation drains output and joins native descendants without deleting partial module storage.");
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static async Task PreCancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index")); using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "cancelled"), cancel.Token));
        await Expect<OperationCanceledException>(() => f.Repository.GetSubmoduleRemoteBranchesAsync(child.Repository.Path, cancel.Token));
        await Expect<ArgumentException>(() => f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "bad\0path")));
        await Expect<ArgumentException>(() => f.Repository.AddSubmoduleAsync(new("bad\0source", "bad")));
        await Expect<ArgumentException>(() => f.Repository.AddSubmoduleAsync(new(child.Repository.Path, "bad", "bad\0branch")));
        Check(Bytes(index, await File.ReadAllBytesAsync(f.File(".git/index"))) && !Directory.Exists(f.File("cancelled")) && !File.Exists(f.File(".gitmodules")), "Validation and pre-cancellation have no mutation effects.");
    }

    private static async Task Bare(RepositoryFixture source, string destination, string branch)
    { Check((await Native(source.Root, "clone", "--bare", "--", source.Repository.Path, destination)).Success, "Bare fixture."); Check((await Native(source.Root, "--git-dir=" + destination, "branch", branch)).Success, "Marker branch."); }
    private static Task<GitResult> Native(string cwd, params string[] args) => new GitRunner().RunAsync(cwd, args);
    private static async Task<GitRepository> Hooked(RepositoryFixture f, string body)
    {
        string template = Path.Combine(f.Root, "template"); Directory.CreateDirectory(Path.Combine(template, "hooks"));
        await Executable(Path.Combine(template, "hooks", "post-checkout"), "#!/bin/sh\n" + body);
        string wrapper = Path.Combine(f.Root, "hook-git");
        await Executable(wrapper, "#!/bin/sh\nexec git -c " + Quote("init.templateDir=" + template) + " -c " + Quote("core.hooksPath=" + Path.Combine(template, "hooks")) + " \"$@\"\n");
        return new(f.Repository.Path, new GitRunner(wrapper));
    }
    private static async Task Executable(string path, string script)
    { await File.WriteAllTextAsync(path, script); if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } }
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static bool Bytes(byte[] first, byte[] second) => first.SequenceEqual(second);
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
}
