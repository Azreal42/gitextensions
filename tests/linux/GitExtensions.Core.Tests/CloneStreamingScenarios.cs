using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitExtensions.Linux;

internal static class CloneStreamingScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("clone observes real Git blob bytes before completion and retains exact native streams", LiveBytesAsync),
        ("observed normal shallow bare and no-checkout clones preserve native options and literal destinations", OptionsAsync),
        ("clone native failures retain identical observed and unobserved arguments and bytes", ErrorsAsync),
        ("clone cancellation stops its actual private transport actors and preserves source materials", CancellationAsync),
        ("clone validation and precancellation launch nothing and create no destination parent", ValidationAsync),
        ("clone observer failure stops the private transport without replacing the original exception", ObserverFailureAsync)
    ];

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static async Task LiveBytesAsync()
    {
        using Fixture f = await Fixture.Create();
        byte[] payload = [.. Encoding.UTF8.GetBytes(new string('x', 4095) + "éλ😀\rclone progress 12%\r\n"), 0xFF, 0xC3];
        string blob = await f.Blob(payload); Gate gate = f.Transport(blob); string before = f.Materials();
        Pipes pipes = new(); string destination = f.Destination("live literal '雪;$(data)");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = GitRepository.CloneCommandAsync(f.SshSource, destination, new(RecurseSubmodules: false), cancellation.Token, pipes.Receive);
        try
        {
            await Until(() => File.Exists(gate.Marker) && pipes.Error.AsSpan().IndexOf(payload) >= 0, pending);
            Actor[] actors = gate.Actors();
            Check(!pending.IsCompleted, "The actual clone must remain gated after its Git-produced object bytes arrive.");
            Check(File.Exists(Path.Combine(destination, ".git", "HEAD")), "Native clone initialization is a real partial effect before the transport finishes.");
            File.WriteAllText(gate.Release, "release\n"); GitResult result = await pending.WaitAsync(Deadline);
            Check(result.Success && pipes.Events > 0, "The released native clone must return its actual successful result.");
            SameStreams(pipes, result); Check(result.StandardErrorBytes.AsSpan().IndexOf(payload) >= 0, "The raw bytes must not be replaced by UI text or a probe.");
            GitRepository clone = await GitRepository.OpenAsync(destination);
            Check(await clone.ResolveRevisionAsync("HEAD") == f.Head, "The cloned HEAD must be the source's real tip.");
            Check(f.Materials() == before, "Cloning and discovery must not change source refs, raw index, config, files or object bytes.");
            Check(actors.All(actor => !actor.Alive()), "The real transport and sleeper must have stopped after normal completion.");
        }
        finally { File.WriteAllText(gate.Release, "release\n"); cancellation.Cancel(); await Stop(pending); }
    }

    private static async Task OptionsAsync()
    {
        using Fixture f = await Fixture.Create(); string before = f.Materials();
        foreach ((string mode, GitCloneOptions options) in new[]
        {
            ("normal", new GitCloneOptions(RecurseSubmodules: false, Branch: "main")),
            ("shallow", new GitCloneOptions(RecurseSubmodules: false, Branch: "main", Depth: 1)),
            ("bare", new GitCloneOptions(Bare: true, RecurseSubmodules: false)),
            ("no-checkout", new GitCloneOptions(RecurseSubmodules: false, NoCheckout: true)),
            ("topic", new GitCloneOptions(RecurseSubmodules: false, Branch: "topic"))
        })
        {
            string plainPath = f.Destination(mode + " plain '雪;$()"), observedPath = f.Destination(mode + " observed '雪;$()");
            GitRepository plain = await GitRepository.CloneAsync(f.FileSource, plainPath, options);
            Pipes pipes = new(); GitResult result = await GitRepository.CloneCommandAsync(f.FileSource, observedPath, options, outputReceived: pipes.Receive);
            Check(result.Success && pipes.Events > 0, "The observed native command must return real clone output for " + mode);
            SameStreams(pipes, result); GitRepository observed = await GitRepository.OpenAsync(observedPath);
            Check(plain.IsBare == options.Bare && observed.IsBare == options.Bare, "Discovery must retain the bare option in either path.");
            Check(await Refs(plain) == await Refs(observed), "Observers must not change any cloned branch/tag ref for " + mode);
            Check(await plain.ResolveRevisionAsync("HEAD") == await observed.ResolveRevisionAsync("HEAD"), "Both APIs must select the same actual HEAD.");
            Check(File.ReadAllBytes(Path.Combine(plain.Info!.GitDirectory, "config")).SequenceEqual(File.ReadAllBytes(Path.Combine(observed.Info!.GitDirectory, "config"))),
                "Both clones of the same source/options must have byte-identical local configuration.");
            if (options.Depth is not null)
            {
                Check((await observed.RunAsync(["rev-parse", "--is-shallow-repository"])).StandardOutput.Trim() == "true", "Depth must really produce a shallow clone over file transport.");
                Check((await observed.GetReferencesAsync()).Any(reference => reference.Name == "refs/remotes/origin/topic"), "--no-single-branch must retain the other source branch.");
                Check((await observed.RunAsync(["rev-list", "--count", "HEAD"])).StandardOutput.Trim() == "1", "The selected main history must have the actual requested depth.");
            }
            string file = Path.Combine(observedPath, "seed.txt");
            if (options.Bare || options.NoCheckout) { Check(!File.Exists(file), "Bare/no-checkout must not create a working file."); }
            else { Check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(plainPath, "seed.txt"))), "Working bytes must match the unobserved clone."); }
        }
        // The original positional bool wrapper remains callable, and still opens discovery metadata.
        GitRepository legacy = await GitRepository.CloneAsync(f.FileSource, f.Destination("legacy"), recursive: false);
        Check(legacy.Info is not null && !legacy.IsBare && await legacy.ResolveRevisionAsync("HEAD") == f.Head, "The existing positional clone API must remain compatible.");
        Check(f.Materials() == before, "All clone modes must leave the source materials unchanged.");
    }

    private static async Task ErrorsAsync()
    {
        using Fixture f = await Fixture.Create(); string before = f.Materials();
        string destination = f.Destination("failed same destination"); GitCloneOptions options = new(RecurseSubmodules: false, Branch: "missing-private-branch");
        GitCommandException plain = await Expect<GitCommandException>(() => GitRepository.CloneCommandAsync(f.FileSource, destination, options));
        Pipes pipes = new(); GitCommandException observed = await Expect<GitCommandException>(() => GitRepository.CloneCommandAsync(f.FileSource, destination, options, outputReceived: pipes.Receive));
        Check(plain.Arguments.SequenceEqual(observed.Arguments)
            && observed.Arguments.SequenceEqual(new[] { "clone", "-v", "--progress", "--branch=missing-private-branch", "--", f.FileSource, destination }),
            "The observer must preserve the exact native options and literal source/destination arguments.");
        Check(plain.Result.ExitCode == observed.Result.ExitCode && !observed.Result.Success
            && plain.Result.StandardOutputBytes.SequenceEqual(observed.Result.StandardOutputBytes)
            && plain.Result.StandardErrorBytes.SequenceEqual(observed.Result.StandardErrorBytes), "Both paths must retain the same actual pre-transfer failure bytes/exit.");
        SameStreams(pipes, observed.Result);
        string occupied = f.Destination("existing"); Directory.CreateDirectory(occupied); File.WriteAllBytes(Path.Combine(occupied, "keep"), [0, 1, 0xFF]);
        GitCommandException error = await Expect<GitCommandException>(() => GitRepository.CloneCommandAsync(f.FileSource, occupied, new(), outputReceived: pipes.Receive));
        Check(!error.Result.Success && File.ReadAllBytes(Path.Combine(occupied, "keep")).SequenceEqual(new byte[] { 0, 1, 0xFF }), "A native nonempty-directory failure cannot remove existing files.");
        Check(f.Materials() == before, "Failed clones must not alter the source.");
    }

    private static async Task CancellationAsync()
    {
        using Fixture f = await Fixture.Create(); byte[] payload = Encoding.UTF8.GetBytes("actual object before cancelled clone λ\n"); string blob = await f.Blob(payload);
        Gate gate = f.Transport(blob); string before = f.Materials(); string destination = f.Destination("cancelled"); Pipes pipes = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = GitRepository.CloneCommandAsync(f.SshSource, destination, new(RecurseSubmodules: false), cancellation.Token, pipes.Receive);
        try
        {
            await Until(() => File.Exists(gate.Marker) && pipes.Error.AsSpan().IndexOf(payload) >= 0, pending); Actor[] actors = gate.Actors();
            Check(actors.Length == 3, "The marker must witness the client, transport and sleeper PIDs.");
            Check(actors.All(actor => actor.Alive()), "Every recorded transport actor must be alive before cancellation.");
            Check(actors.All(actor => actor.Group == actors[0].Group) && actors[0].Group != Actor.Read(Environment.ProcessId).Group,
                "The actual clone and its transport must share only this invocation's private group.");
            Check(Directory.Exists(Path.Combine(destination, ".git")), "Record the native partial initialization before asking Git to stop.");
            byte[] observed = pipes.Error; cancellation.Cancel(); await Expect<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Check(actors.All(actor => !actor.Alive()), "Cancellation returns only after the actual clone/transport/sleeper have stopped.");
            Check(pipes.Error.AsSpan().StartsWith(observed), "Already observed real bytes must remain intact after interruption.");
            Check(f.Materials() == before, "Cancellation must preserve source refs/index/config/worktree and objects.");
            // Preserve Git's actual post-cancellation destination, whether it removed its new
            // directory or retained partial metadata. No helper cleans it or claims rollback.
            string after = Tree(destination); await Task.Delay(50); Check(Tree(destination) == after, "Stopped actors cannot keep mutating the remaining partial destination.");
        }
        finally { cancellation.Cancel(); await Stop(pending); }
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.Create(); string before = f.Materials(); string parent = Path.Combine(f.Seed.Root, "must-not-create");
        string destination = Path.Combine(parent, "destination"); Pipes pipes = new();
        foreach (GitCloneOptions options in new[] { new GitCloneOptions(Depth: 0), new GitCloneOptions(NoCheckout: true, Branch: "main") })
        { await Expect<ArgumentException>(() => GitRepository.CloneCommandAsync(f.FileSource, destination, options, outputReceived: pipes.Receive)); }
        using CancellationTokenSource stopped = new(); stopped.Cancel();
        await Expect<OperationCanceledException>(() => GitRepository.CloneCommandAsync(f.FileSource, destination, new(), stopped.Token, pipes.Receive));
        Check(!Directory.Exists(parent) && pipes.Events == 0, "Validation/precancellation must precede directory creation and every output callback.");
        Check(f.Materials() == before, "Invalid requests must preserve all source materials.");
    }

    private static async Task ObserverFailureAsync()
    {
        using Fixture f = await Fixture.Create(); string blob = await f.Blob(Encoding.UTF8.GetBytes("actual object at observer barrier λ\n"));
        Gate gate = f.Transport(blob); string before = f.Materials(); InvalidOperationException expected = new("private clone observer failure");
        string fault = gate.Marker + ".fail";
        int callbacks = 0;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = GitRepository.CloneCommandAsync(f.SshSource, f.Destination("observer failed"), new(RecurseSubmodules: false), cancellation.Token, _ =>
        {
            Interlocked.Increment(ref callbacks);
            if (File.Exists(fault)) { throw expected; }
        });
        try
        {
            await Until(() => File.Exists(gate.Marker), pending); Actor[] actors = gate.Actors();
            // Produce another real Git read on the same stderr pipe after the marker.
            File.WriteAllText(fault, "enable observer fault\n");
            File.WriteAllText(gate.Release, "release\n");
            InvalidOperationException error = await Expect<InvalidOperationException>(() => pending.WaitAsync(Deadline));
            Check(ReferenceEquals(error, expected) && callbacks > 0, "The callback's original exception must survive native group cleanup.");
            Check(actors.All(actor => !actor.Alive()), "Observer failure must stop all recorded transport actors before returning.");
            Check(f.Materials() == before, "A failed observer must not change the source.");
        }
        finally { cancellation.Cancel(); await Stop(pending, expected); }
    }

    private sealed class Pipes
    {
        private readonly object _gate = new(); private readonly MemoryStream _stdout = new(), _stderr = new(); private int _events;
        public void Receive(GitProcessOutput chunk) { lock (_gate) { (chunk.Stream == GitProcessOutputStream.StandardOutput ? _stdout : _stderr).Write(chunk.Bytes.Span); _events++; } }
        public byte[] Output { get { lock (_gate) { return _stdout.ToArray(); } } }
        public byte[] Error { get { lock (_gate) { return _stderr.ToArray(); } } }
        public int Events { get { lock (_gate) { return _events; } } }
    }
    private static void SameStreams(Pipes pipes, GitResult result)
        => Check(pipes.Output.SequenceEqual(result.StandardOutputBytes) && pipes.Error.SequenceEqual(result.StandardErrorBytes), "All callbacks must match exactly the final native pipe bytes, with no discovery output.");
    private static async Task<string> Refs(GitRepository repository) => (await repository.RunAsync(["for-each-ref", "--format=%(refname)%00%(objectname)"])).StandardOutput;
    private static string Tree(string path) => !Directory.Exists(path) ? "<absent>" : string.Join('\n', Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))));
    private static async Task Until(Func<bool> condition, Task operation)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && !operation.IsCompleted && timer.Elapsed < Deadline) { await Task.Delay(10); } Check(condition(), "The real clone barrier did not settle within the bounded deadline."); }
    private static async Task<T> Expect<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + " from the actual command."); }
    private static async Task Stop(Task operation, Exception? expected = null)
    { try { await operation.WaitAsync(Deadline); } catch (OperationCanceledException) { } catch (Exception error) when (ReferenceEquals(error, expected)) { } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed record Actor(int Pid, long Start, int Group)
    {
        internal static Actor Read(int pid)
        {
            string text = File.ReadAllText("/proc/" + pid + "/stat"); string[] fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(pid, long.Parse(fields[19], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture));
        }
        internal bool Alive()
        {
            try { Actor now = Read(Pid); return now.Start == Start && LinuxProcessState.IsRunning(Pid); }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
    }
    private sealed record Gate(string Marker, string Release)
    {
        internal Actor[] Actors() => File.ReadAllText(Marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];
        private Fixture(RepositoryFixture seed) { Seed = seed; }
        public RepositoryFixture Seed { get; }
        public string Head { get; private set; } = "";
        public string FileSource => new Uri(Seed.Repository.Path).AbsoluteUri;
        public string SshSource => "ssh://clone-fixture.invalid/private.git";
        public string Destination(string name) => Path.Combine(Seed.Root, "destinations", name);
        public static async Task<Fixture> Create()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private transport/procfs fixture requires Linux."); }
            RepositoryFixture seed = await RepositoryFixture.CreateAsync(); Fixture f = new(seed);
            foreach ((string name, string value) in new[] { ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0"), ("GIT_ALLOW_PROTOCOL", "file:ssh"), ("GIT_TERMINAL_PROMPT", "0") }) { f.Set(name, value); }
            try
            {
                await seed.Repository.CreateBranchAsync("topic", checkout: false);
                for (int index = 0; index < 2; index++)
                { seed.Write("seed.txt", "clone candidate " + index + "\n"); await seed.Repository.StageAsync(["seed.txt"]); await seed.Repository.CommitAsync("Clone source " + index); }
                f.Head = await seed.Repository.ResolveRevisionAsync("HEAD"); return f;
            }
            catch { f.Dispose(); throw; }
        }
        private void Set(string name, string? value)
        { _environment.TryAdd(name, Environment.GetEnvironmentVariable(name)); Environment.SetEnvironmentVariable(name, value); }
        public async Task<string> Blob(byte[] bytes)
        { GitBinaryResult result = await new GitRunner().RunBinaryAsync(Seed.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes); Check(result.Success, result.StandardError); return Encoding.UTF8.GetString(result.StandardOutput).Trim(); }
        public Gate Transport(string blob)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string script = Path.Combine(Seed.Root, "private transport"), marker = script + ".actors", release = script + ".release";
            File.WriteAllText(script, "#!/bin/sh\nset -eu\nsleep 60 &\nchild=$!\ntrap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\n"
                + "/usr/bin/git -C " + Quote(Seed.Repository.Path) + " cat-file blob " + Quote(blob) + " >&2\n"
                + "printf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > " + Quote(marker + ".tmp") + "\nmv " + Quote(marker + ".tmp") + " " + Quote(marker)
                + "\nwhile test ! -f " + Quote(release) + "; do sleep 0.02; done\n/usr/bin/git -C " + Quote(Seed.Repository.Path) + " cat-file blob " + Quote(blob) + " >&2\n"
                + "kill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\ntrap - 0\nexec /usr/bin/git upload-pack " + Quote(Seed.Repository.Path) + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Set("GIT_SSH", script); Set("GIT_SSH_COMMAND", null); Set("GIT_SSH_VARIANT", "ssh"); return new(marker, release);
        }
        public string Materials() => Tree(Seed.Repository.Path);
        public void Dispose() { foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); } Seed.Dispose(); }
    }
}
