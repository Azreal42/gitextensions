using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class RemoteHeadsScenarios
{
    private const string CreatedBranch = "feature/été-$(touch${IFS}remote-heads-executed);雪";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("remote heads read live server advancement creation and deletion without fetching or changing local materials", FreshServerAsync),
        ("remote heads distinguish an empty server from populated local and remote tracking refs", EmptyServerAsync),
        ("remote heads retain injected executable HOME global configuration cwd and literal origin arguments", CapturedContextAsync),
        ("remote heads preserve full native SHA256 ids and Unicode literal paths and branch names", Sha256Async),
        ("remote heads retain native transport errors and exact streamed diagnostics instead of returning local refs", NativeFailureAsync),
        ("remote heads invalid input and precancellation launch no command and preserve repository bytes", ValidationAsync),
        ("remote heads stream real private upload pack output and diagnostics before normal completion", LiveTransportAsync),
        ("remote heads cancellation drains witnessed native Git transport and sleeper actors without local fallback", TransportCancellationAsync),
        ("remote heads reject malformed and duplicate injected records instead of returning partial refs", InvalidRecordsAsync)
    ];

    private static async Task FreshServerAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string originalLocal = Tree(f.Local.Path), advanced = await f.AdvanceServerAsync();
        Check(Tree(f.Local.Path) == originalLocal, "Independent server changes must not update the clone's stale refs or object store.");
        string before = f.Materials(); Pipes pipes = new();
        IReadOnlyList<GitReference> heads = await f.Local.GetRemoteHeadsAsync("origin", outputReceived: pipes.Receive);
        ExpectedAdvancedHeads(heads, advanced);
        Check(Encoding.UTF8.GetString(pipes.Output) == (await f.Git(f.Local, "ls-remote", "--heads", "--", "origin")).StandardOutput,
            "The observer must receive the actual complete native advertisement, not local for-each-ref output.");
        Check(await f.Local.ResolveRevisionAsync("refs/remotes/origin/main") == f.Initial
            && await f.Local.ResolveRevisionAsync("refs/remotes/origin/obsolete") == f.Initial
            && !(await f.Runner.RunAsync(f.Local.Path, ["show-ref", "--verify", "refs/remotes/origin/" + CreatedBranch])).Success,
            "Remote tracking refs stay stale, including the deleted branch, and no new tracking ref is created.");
        Check(!(await f.Runner.RunAsync(f.Local.Path, ["cat-file", "-e", advanced + "^{commit}"])).Success,
            "The newly advertised commit must remain absent from the clone's object database; this API must not fetch.");
        ExpectedAdvancedHeads(await f.Server.GetRemoteHeadsAsync(f.Server.Path), advanced);
        Check(f.Materials() == before, "Both clone and bare callers leave every local/server/writer ref, index, config, object and working file byte unchanged.");
    }

    private static async Task EmptyServerAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string empty = Path.Combine(f.Owner.Root, "empty server λ.git");
        await f.GitAt(f.Owner.Root, "init", "--bare", "--initial-branch=main", empty);
        await f.Git(f.Local, "remote", "add", "empty", empty);
        string before = f.Materials(), serverBefore = Tree(empty); Pipes pipes = new();
        Check((await f.Local.GetRemoteHeadsAsync("empty", outputReceived: pipes.Receive)).Count == 0,
            "A successful empty advertisement is empty even though main/local-only and origin tracking refs exist locally.");
        Check(pipes.Output.Length == 0 && pipes.Error.Length == 0 && f.Materials() == before && Tree(empty) == serverBefore,
            "An empty server produces no invented output or repository effects.");
    }

    private static async Task CapturedContextAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string home = Path.Combine(f.Owner.Root, "captured HOME λ '$()"), global = Path.Combine(f.Owner.Root, "captured global config");
        Directory.CreateDirectory(home); await File.WriteAllTextAsync(global, "[remoteheads]\ncontext = captured\n");
        await f.GitAt(f.Owner.Root, "config", "--file", global, "remote.origin.url", f.Server.Path);
        await f.Git(f.Local, "config", "--unset-all", "remote.origin.url");
        string cwdFile = Path.Combine(f.Owner.Root, "command.cwd"), envFile = cwdFile + ".environment", argvFile = cwdFile + ".argv";
        string wrapper = f.Executable("captured git λ '$()", "for arg do\n  if test \"$arg\" = ls-remote; then\n"
            + "    pwd -P > " + Quote(cwdFile) + "\n"
            + "    printf '%s\\0%s\\0%s\\0' \"$HOME\" \"$GIT_CONFIG_GLOBAL\" \"$REMOTE_HEADS_CONTEXT\" > " + Quote(envFile) + "\n"
            + "    printf '%s\\0' \"$@\" > " + Quote(argvFile) + "\n  fi\ndone\nexec /usr/bin/git \"$@\"\n");
        GitRunner runner = f.CreateRunner(wrapper, new Dictionary<string, string?>
        { ["HOME"] = home, ["GIT_CONFIG_GLOBAL"] = global, ["REMOTE_HEADS_CONTEXT"] = "captured λ '$()" });
        GitRepository captured = new(f.Local.Path, runner); GitEnvironmentOptions previous = GitEnvironment.Options;
        string forbidden = Path.Combine(f.Owner.Root, "wrong-runner-invoked");
        string replacement = f.Executable("unselected application git", ": > " + Quote(forbidden) + "\nexit 71\n");
        string before = f.Materials(); byte[] globalBefore = await File.ReadAllBytesAsync(global);
        try
        {
            GitEnvironment.Configure(new() { Executable = replacement, HomeDirectory = Path.Combine(f.Owner.Root, "wrong HOME") });
            IReadOnlyList<GitReference> heads = await captured.GetRemoteHeadsAsync("origin");
            Check(heads.Count == 2 && heads.All(head => head.Hash == f.Initial), "The injected Git executable must resolve the real origin from its captured global config; no origin URL remains locally.");
            Check((await File.ReadAllTextAsync(cwdFile)).TrimEnd('\n') == f.Local.Path,
                "Native alias resolution must execute from the selected repository, independently of the test's cwd.");
            Check(NullFields(await File.ReadAllBytesAsync(envFile)).SequenceEqual([home, global, "captured λ '$()"]),
                "The actual child must receive the frozen HOME, global config and custom environment values.");
            string[] arguments = NullFields(await File.ReadAllBytesAsync(argvFile));
            int command = Array.IndexOf(arguments, "ls-remote");
            Check(command >= 0 && arguments[command..].SequenceEqual(["ls-remote", "--heads", "--", "origin"]),
                "The remote alias is one literal argument after --, with no branch-list or fetch fallback.");
            Check(!File.Exists(forbidden) && f.Materials() == before && (await File.ReadAllBytesAsync(global)).SequenceEqual(globalBefore)
                && !Directory.EnumerateFileSystemEntries(home).Any(), "Application option changes cannot replace an injected runner or mutate its captured config/HOME.");
        }
        finally { GitEnvironment.Configure(previous); }
    }

    private static async Task Sha256Async()
    {
        using Fixture f = await Fixture.CreateAsync(sha256: true); string advanced = await f.AdvanceServerAsync();
        string before = f.Materials();
        Check(f.Initial.Length == 64 && advanced.Length == 64
            && (await f.Git(f.Server, "rev-parse", "--show-object-format")).StandardOutput.Trim() == "sha256",
            "This case requires an actual native SHA256 server and clone, not fabricated wide records.");
        IReadOnlyList<GitReference> byAlias = await f.Local.GetRemoteHeadsAsync("origin");
        IReadOnlyList<GitReference> byLiteralPath = await f.Local.GetRemoteHeadsAsync(f.Server.Path);
        ExpectedAdvancedHeads(byAlias, advanced); ExpectedAdvancedHeads(byLiteralPath, advanced);
        Check(byAlias.All(reference => reference.Hash.Length == 64 && reference.Sha1Id is null)
            && byAlias.Select(reference => (reference.Name, reference.Hash)).SequenceEqual(byLiteralPath.Select(reference => (reference.Name, reference.Hash))),
            "Exact Unicode/metacharacter branch names and full object ids survive alias and literal-path requests without false SHA1 conversion.");
        Check(!File.Exists(Path.Combine(f.Local.Path, "remote-heads-executed")) && !File.Exists(Path.Combine(f.Owner.Root, "remote-heads-executed"))
            && f.Materials() == before, "Literal path/ref characters never execute shell substitutions or modify Git materials.");
    }

    private static async Task NativeFailureAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string missing = Path.Combine(f.Owner.Root, "missing remote λ '$() [x].git");
        await f.Git(f.Local, "remote", "set-url", "origin", missing);
        string before = f.Materials(); Pipes pipes = new();
        GitCommandException error = await Expect<GitCommandException>(() => f.Local.GetRemoteHeadsAsync("origin", outputReceived: pipes.Receive));
        Check(!error.Result.Success && error.Result.StandardErrorBytes.Length > 0 && error.Arguments.TakeLast(4).SequenceEqual(["ls-remote", "--heads", "--", "origin"]),
            "A real missing repository must retain its failing native ls-remote command instead of returning local references.");
        Check(pipes.Output.SequenceEqual(error.Result.StandardOutputBytes) && pipes.Error.SequenceEqual(error.Result.StandardErrorBytes)
            && f.Materials() == before && !Directory.Exists(missing), "Failure retains every real pipe byte and creates no remote path, fetch ref, config edit or local index change.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string journal = Path.Combine(f.Owner.Root, "unexpected native command");
        string wrapper = f.Executable("unstarted git", ": > " + Quote(journal) + "\nexec /usr/bin/git \"$@\"\n");
        GitRepository guarded = new(f.Local.Path, f.CreateRunner(wrapper)); Pipes pipes = new(); string before = f.Materials();
        foreach (string remote in new[] { "", " \t", "--upload-pack=unexpected", "origin\0other" })
        { await Expect<ArgumentException>(() => guarded.GetRemoteHeadsAsync(remote, outputReceived: pipes.Receive)); }
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        OperationCanceledException error = await Expect<OperationCanceledException>(() => guarded.GetRemoteHeadsAsync("origin", cancelled.Token, pipes.Receive));
        Check(error.CancellationToken == cancelled.Token && !File.Exists(journal) && pipes.Events == 0 && f.Materials() == before,
            "Validation and caller precancellation precede every native process/output callback, leaving real local refs/index/config intact.");
    }

    private static async Task LiveTransportAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        byte[] payload = [.. Encoding.UTF8.GetBytes("native upload-pack helper blob λ\r\n"), 0xFF, 0xC3];
        Transport transport = await f.TransportAsync(payload); Pipes pipes = new();
        string before = f.Materials(); byte[] expected = (await f.Git(f.Local, "ls-remote", "--heads", "--", "origin")).StandardOutputBytes;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<IReadOnlyList<GitReference>> pending = transport.Repository.GetRemoteHeadsAsync("private-transport", cancellation.Token, pipes.Receive);
        try
        {
            await Until(() => File.Exists(transport.Marker) && pipes.Error.AsSpan().SequenceEqual(payload), pending);
            Actor[] actors = transport.Actors(); CheckPrivateActors(actors);
            Check(!pending.IsCompleted && pipes.Output.Length == 0, "The real SSH transport must be blocked before its server advertisement, after streaming native blob diagnostics.");
            await File.WriteAllTextAsync(transport.Release, "release\n");
            IReadOnlyList<GitReference> heads = await pending.WaitAsync(Deadline);
            Check(heads.Count == 2 && heads.All(reference => reference.Hash == f.Initial), "Releasing the helper must run actual upload-pack and return the server's branch identities.");
            Check(pipes.Output.SequenceEqual(expected) && pipes.Error.SequenceEqual(payload.Concat(payload)),
                "The observer receives the exact native advertisement and both binary diagnostic emissions, preserving undecodable stderr bytes.");
            Check(actors.All(actor => !actor.Alive()) && f.Materials() == before,
                "Normal completion waits for the real transport actors and leaves local and remote materials byte identical.");
        }
        finally { cancellation.Cancel(); await Drain(pending); }
    }

    private static async Task TransportCancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); byte[] payload = Encoding.UTF8.GetBytes("native transport before cancelled remote heads λ\n");
        Transport transport = await f.TransportAsync(payload); string before = f.Materials(); Pipes pipes = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<IReadOnlyList<GitReference>> pending = transport.Repository.GetRemoteHeadsAsync("private-transport", cancellation.Token, pipes.Receive);
        try
        {
            await Until(() => File.Exists(transport.Marker) && pipes.Error.AsSpan().SequenceEqual(payload), pending);
            Actor[] actors = transport.Actors(); CheckPrivateActors(actors); byte[] observed = pipes.Error;
            Check(!pending.IsCompleted && pipes.Output.Length == 0, "Cancellation requires a live native Git/transport barrier, not an already finished command.");
            cancellation.Cancel();
            OperationCanceledException error = await Expect<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Check(error.CancellationToken == cancellation.Token && pending.IsCanceled && actors.All(actor => !actor.Alive()),
                "Cancellation retains the caller's token and returns only after the witnessed native client, helper and sleeper stop.");
            Check(pipes.Error.AsSpan().StartsWith(observed) && pipes.Output.Length == 0 && f.Materials() == before,
                "Already observed real diagnostics remain available; no local fallback, fetch or repository mutation is manufactured.");
            string stopped = f.Materials(); await Task.Delay(50);
            Check(f.Materials() == stopped, "Drained transport actors cannot continue changing repository materials after cancellation returns.");
        }
        finally { cancellation.Cancel(); await Drain(pending); }
    }

    private static async Task InvalidRecordsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string good = f.Initial + "\trefs/heads/main\n";
        // Deliberate parser injection only: the successful server scenarios above use real Git advertisements.
        string[] malformed =
        [
            good + "not-a-record\n",
            good + "not-an-object-id\trefs/heads/other\n",
            good + f.Initial + "\trefs/tags/not-a-head\n",
            good + f.Initial + "\trefs/heads/\n",
            good + f.Initial + "\trefs/heads/other\textra-field\n",
            good + f.Initial + "\trefs/heads/bad\rname\n",
            good + good,
            good + new string('0', f.Initial.Length) + "\trefs/heads/main\n"
        ];
        string payload = Path.Combine(f.Owner.Root, "injected advertisement"), journal = payload + ".calls";
        string wrapper = f.Executable("parser-only injected git", "for arg do\n  if test \"$arg\" = ls-remote; then\n"
            + "    printf 'injected\\n' >> " + Quote(journal) + "\n    cat " + Quote(payload) + "\n    exit 0\n  fi\ndone\nexec /usr/bin/git \"$@\"\n");
        GitRepository injected = new(f.Local.Path, f.CreateRunner(wrapper)); string before = f.Materials();
        foreach (string record in malformed)
        {
            await File.WriteAllTextAsync(payload, record, new UTF8Encoding(false)); Pipes pipes = new();
            await Expect<InvalidDataException>(() => injected.GetRemoteHeadsAsync("origin", outputReceived: pipes.Receive));
            Check(pipes.Output.SequenceEqual(Encoding.UTF8.GetBytes(record)) && pipes.Error.Length == 0,
                "Invalid records must remain observable while the API refuses the entire result, including an earlier valid row.");
        }
        Check(File.ReadAllLines(journal).Length == malformed.Length && f.Materials() == before,
            "Each malformed/duplicate advertisement is actually consumed once without fallback reads or repository effects.");
    }

    private static void ExpectedAdvancedHeads(IReadOnlyList<GitReference> heads, string hash)
    {
        string[] expected = new[] { "refs/heads/main", "refs/heads/" + CreatedBranch }.Order(StringComparer.Ordinal).ToArray();
        Check(heads.Select(reference => reference.Name).SequenceEqual(expected) && heads.All(reference => reference.Hash == hash
            && reference.RevisionHash == hash && reference.IsBranch && !reference.IsCurrent && reference.Upstream is null && reference.SymbolicTarget is null),
            "Only live server heads belong in the sorted result, with exact full hashes and no local checkout/upstream metadata or tags.");
    }

    private static void CheckPrivateActors(Actor[] actors)
    {
        Check(actors.Length == 3 && actors.All(actor => actor.Alive()), "The marker must witness a live native Git client, transport helper and sleeper.");
        Check(actors.All(actor => actor.Group == actors[0].Group) && actors[0].Group != Actor.Read(Environment.ProcessId).Group,
            "Only this invocation's actual native actors share the private process group.");
    }

    private static string[] NullFields(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static string Tree(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + " from remote head discovery."); }
    private static async Task Until(Func<bool> condition, Task operation)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && !operation.IsCompleted && timer.Elapsed < Deadline) { await Task.Delay(10); } Check(condition(), "The real remote-head transport barrier did not settle within the bounded deadline."); }
    private static async Task Drain(Task operation)
    { try { await operation.WaitAsync(Deadline); } catch (OperationCanceledException) { } }

    private sealed class Pipes
    {
        private readonly object _gate = new(); private readonly MemoryStream _stdout = new(), _stderr = new(); private int _events;
        public void Receive(GitProcessOutput chunk) { lock (_gate) { (chunk.Stream == GitProcessOutputStream.StandardOutput ? _stdout : _stderr).Write(chunk.Bytes.Span); _events++; } }
        public byte[] Output { get { lock (_gate) { return _stdout.ToArray(); } } }
        public byte[] Error { get { lock (_gate) { return _stderr.ToArray(); } } }
        public int Events { get { lock (_gate) { return _events; } } }
    }

    private sealed record Actor(int Pid, long Start, int Group)
    {
        internal static Actor Read(int pid)
        {
            string stat = File.ReadAllText("/proc/" + pid + "/stat"); string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(pid, long.Parse(fields[19], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture));
        }
        internal bool Alive()
        {
            try { return Read(Pid).Start == Start && LinuxProcessState.IsRunning(Pid); }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
    }

    private sealed record Transport(GitRepository Repository, string Marker, string Release)
    {
        internal Actor[] Actors() => File.ReadAllText(Marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment;
        private Fixture(RepositoryFixture owner)
        {
            Owner = owner;
            _environment = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" }).EnvironmentVariables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (string name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_SSH_COMMAND", "GIT_SSH" }) { _environment[name] = null; }
            _environment["HOME"] = Path.Combine(owner.Root, "private home"); Directory.CreateDirectory(_environment["HOME"]!);
            _environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; _environment["GIT_CONFIG_NOSYSTEM"] = "1"; _environment["GIT_CONFIG_COUNT"] = "0";
            _environment["GIT_OPTIONAL_LOCKS"] = "0"; _environment["GIT_TERMINAL_PROMPT"] = "0"; _environment["GIT_ALLOW_PROTOCOL"] = "file:ssh"; _environment["DISPLAY"] = "";
            Runner = CreateRunner("/usr/bin/git");
        }
        internal RepositoryFixture Owner { get; }
        internal GitRunner Runner { get; }
        internal GitRepository Writer { get; private set; } = null!;
        internal GitRepository Server { get; private set; } = null!;
        internal GitRepository Local { get; private set; } = null!;
        internal string Initial { get; private set; } = "";
        internal static async Task<Fixture> CreateAsync(bool sha256 = false)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("These real transport/procfs regressions require Linux."); }
            RepositoryFixture owner = await RepositoryFixture.CreateAsync(seed: false); Fixture f = new(owner);
            try
            {
                string writer = sha256 ? Path.Combine(owner.Root, "writer SHA256 λ") : owner.Repository.Path;
                if (sha256) { await f.GitAt(owner.Root, "init", "--object-format=sha256", "--initial-branch=main", writer); }
                f.Writer = new(writer, f.Runner); await RepositoryFixture.ConfigureAsync(f.Writer);
                await f.Git(f.Writer, "symbolic-ref", "HEAD", "refs/heads/main");
                await File.WriteAllTextAsync(Path.Combine(writer, "seed.txt"), "remote heads source\n");
                await f.Writer.StageAsync(["seed.txt"]); await f.Writer.CommitAsync("Remote heads initial");
                f.Initial = await f.Writer.ResolveRevisionAsync("HEAD");
                await f.Git(f.Writer, "update-ref", "refs/heads/obsolete", f.Initial);
                await f.Git(f.Writer, "tag", "-a", "excluded-tag", "-m", "Do not advertise as a head");
                string server = Path.Combine(owner.Root, "server bare [x]* λ '$(touch${IFS}remote-heads-executed).git");
                await f.GitAt(owner.Root, "clone", "--bare", "--no-hardlinks", "--", writer, server); f.Server = new(server, f.Runner);
                string local = Path.Combine(owner.Root, "local clone [x]* λ '$()");
                await f.GitAt(owner.Root, "clone", "--no-hardlinks", "--", server, local); f.Local = new(local, f.Runner);
                await RepositoryFixture.ConfigureAsync(f.Local);
                await f.Git(f.Local, "update-ref", "refs/heads/local-only", f.Initial);
                await f.Git(f.Local, "update-ref", "refs/remotes/origin/stale", f.Initial);
                await File.WriteAllTextAsync(Path.Combine(local, "stage.txt"), "staged witness\n"); await f.Local.StageAsync(["stage.txt"]);
                await File.AppendAllTextAsync(Path.Combine(local, "stage.txt"), "unstaged witness\n");
                await File.AppendAllTextAsync(Path.Combine(local, "seed.txt"), "unstaged seed witness\n");
                await File.WriteAllTextAsync(Path.Combine(local, "untracked λ '$()"), "untracked witness\n");
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal GitRunner CreateRunner(string executable, IReadOnlyDictionary<string, string?>? overrides = null)
        {
            Dictionary<string, string?> values = new(_environment, StringComparer.Ordinal);
            if (overrides is not null) { foreach ((string name, string? value) in overrides) { values[name] = value; } }
            return GitExecutionEnvironment.Capture(new() { Executable = executable }, values).CreateRunner();
        }
        internal Task<GitResult> Git(GitRepository repository, params string[] arguments) => GitAt(repository.Path, arguments);
        internal async Task<GitResult> GitAt(string directory, params string[] arguments)
        {
            GitResult result = await Runner.RunAsync(directory, arguments);
            if (!result.Success) { throw new GitCommandException(arguments, result); }
            return result;
        }
        internal async Task<string> AdvanceServerAsync()
        {
            await File.AppendAllTextAsync(Path.Combine(Writer.Path, "seed.txt"), "server advancement λ\n");
            await Writer.StageAsync(["seed.txt"]); await Writer.CommitAsync("Server changed independently of clone");
            string advanced = await Writer.ResolveRevisionAsync("HEAD");
            await Git(Writer, "push", "--", Server.Path, "HEAD:refs/heads/main", "HEAD:refs/heads/" + CreatedBranch, ":refs/heads/obsolete");
            Check((await Git(Server, "rev-parse", "refs/heads/main")).StandardOutput.Trim() == advanced
                && (await Git(Server, "rev-parse", "refs/heads/" + CreatedBranch)).StandardOutput.Trim() == advanced
                && !(await Runner.RunAsync(Server.Path, ["show-ref", "--verify", "refs/heads/obsolete"])).Success,
                "The server advance, branch creation and branch deletion must be real native effects before the read.");
            return advanced;
        }
        internal string Executable(string name, string body)
        {
            if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Private transport helpers require Unix executable modes."); }
            string file = Path.Combine(Owner.Root, name); File.WriteAllText(file, "#!/bin/sh\nset -eu\n" + body);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return file;
        }
        internal async Task<Transport> TransportAsync(byte[] payload)
        {
            GitBinaryResult blob = await Runner.RunBinaryAsync(Server.Path, ["hash-object", "-w", "--stdin"], standardInput: payload);
            Check(blob.Success, blob.StandardError); string hash = Encoding.UTF8.GetString(blob.StandardOutput).Trim();
            string marker = Path.Combine(Owner.Root, "transport.actors"), release = marker + ".release";
            string helper = Executable("private ssh transport λ '$()", "sleep 60 &\nchild=$!\n"
                + "trap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\n"
                + "/usr/bin/git -C " + Quote(Server.Path) + " cat-file blob " + Quote(hash) + " >&2\n"
                + "printf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > " + Quote(marker + ".tmp") + "\n"
                + "mv " + Quote(marker + ".tmp") + " " + Quote(marker) + "\n"
                + "while test ! -f " + Quote(release) + "; do sleep 0.02; done\n"
                + "/usr/bin/git -C " + Quote(Server.Path) + " cat-file blob " + Quote(hash) + " >&2\n"
                + "kill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\ntrap - 0\n"
                + "exec /usr/bin/git upload-pack " + Quote(Server.Path) + "\n");
            await Git(Local, "remote", "add", "private-transport", "ssh://remote-heads-fixture.invalid/unused.git");
            GitRunner runner = CreateRunner("/usr/bin/git", new Dictionary<string, string?>
            { ["GIT_SSH"] = helper, ["GIT_SSH_COMMAND"] = null, ["GIT_SSH_VARIANT"] = "ssh" });
            return new(new(Local.Path, runner), marker, release);
        }
        internal string Materials() => "LOCAL\n" + Tree(Local.Path) + "\nSERVER\n" + Tree(Server.Path) + "\nWRITER\n" + Tree(Writer.Path);
        public void Dispose() => Owner.Dispose();
    }
}
