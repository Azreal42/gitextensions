using System.Diagnostics;
using System.Text;
using GitCommands.Git;
using GitExtensions.Linux;

internal static partial class GitProcessStreamingScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("streamed raw Git blobs and every UTF8 boundary preserve bytes and escaped invalid sequences", Guard(BytesAndDecoderAsync)),
        ("stdout and stderr arrive before real Git wrapper completion and remain byte-exact", Guard(LivePipesAsync)),
        ("stream cancellation terminates descendants after a real bisect good and retains native effects", Guard(CancelledGoodAsync)),
        ("stream pre-cancellation starts no child and emits no invented output", Guard(PrecancelledAsync)),
        ("a failed stream observer stops the real process and retains the original failure", Guard(ObserverFailureAsync)),
        ("observer failure interrupts blocked large stdin and a real cat-file producer without leaks", Guard(LargeInputObserverAsync)),
        ("concurrent explicit observers never receive another command's bytes", Guard(IsolationAsync)),
        ("bisect streaming excludes readonly state and revision resolution probes", Guard(BisectBoundariesAsync)),
        ("late cancellation stops orphaned stdout and stderr holders after their real Git parent exits", Guard(OrphanCancellationAsync)),
        ("a late observer exception stops a private orphaned process group and preserves its exact fault", Guard(OrphanObserverAsync)),
        ("observed and unobserved bisect preserve literal magic and wildcard filenames identically", Guard(LiteralBisectPathsAsync)),
        ("Git launch resolves the parent PATH before a conflicting child PATH with literal arguments", Guard(ParentExecutablePathAsync)),
        ("Git launch resolves a relative executable before changing the child working directory", Guard(RelativeExecutablePathAsync)),
        ("missing and non-executable Git programs retain native launch errors without callbacks or effects", Guard(LaunchErrorsAsync)),
        ("a missing script interpreter retains Process.Start's native exec error without Git output", Guard(MissingInterpreterAsync)),
        ("an invalid executable format retains Process.Start's ENOEXEC instead of becoming a Git exit", Guard(InvalidFormatAsync)),
        ("graceful checkout cancellation lets actual Git remove its owned lock and preserves a concurrent foreign Git lock", Guard(GracefulCheckoutWithForeignGitAsync)),
        ("graceful cancellation cleans the real orphaned checkout owner after its direct leader has exited", Guard(GracefulOrphanCheckoutAsync)),
        ("a checkout observer fault retains its exact exception and native bytes while SIGINT cleans the owned index lock", Guard(GracefulObserverCheckoutAsync)),
        ("witnessed SIGINT-ignoring actors with and without pipes survive the grace period then all stop before cancellation returns", Guard(IgnoredInterruptFallsBackAsync)),
        ("cancellation stops the actual native launcher blocked before HELLO and postACK before exec without dispatching Git", Guard(BlockedNativeLaunchCancellationAsync)),
        ("progressive cancellation signals both independent orphaned Git pipes and observes their actual natural exit130", Guard(IndependentOrphanPipesAsync))
    ];
    private static Func<Task> Guard(Func<Task> test) => async () =>
    {
        Dictionary<string, string?> previous = [];
        foreach ((string name, string value) in new[] { ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
        { previous[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        try { await test(); } finally { foreach ((string name, string? value) in previous) { Environment.SetEnvironmentVariable(name, value); } }
    };
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<GitResult> Git(RepositoryFixture f, params string[] arguments)
    { GitResult result = await new GitRunner().RunAsync(f.Repository.Path, arguments); if (!result.Success) { throw new GitCommandException(arguments, result); } return result; }
    private static async Task<string> Head(RepositoryFixture f) => (await Git(f, "rev-parse", "HEAD")).StandardOutput.Trim();
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); } Check(condition(), "Streaming fixture did not settle within ten seconds."); }
    private static string Wrapper(RepositoryFixture f, string name, bool gateBeforeGit = false, string? gatedCommand = null)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This process-tree fixture requires Linux."); }
        string executable = Path.Combine(f.Root, name), marker = Path.Combine(f.Root, name + ".pids"), release = Path.Combine(f.Root, name + ".release");
        string gate = "sleep 60 &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + Quote(marker) + "\n";
        string native = "/usr/bin/env git \"$@\"\nstatus=$?\n/usr/bin/env git rev-parse --verify deliberately-missing-private-ref >&2 || :\n";
        string passthrough = gatedCommand is null ? "" : "case \" $* \" in *" + Quote(" " + gatedCommand + " ") + "*) ;; *) exec /usr/bin/env git \"$@\";; esac\n";
        File.WriteAllText(executable, "#!/bin/sh\n" + passthrough + (gateBeforeGit ? gate + native : native + gate)
            + "while test ! -f " + Quote(release) + "; do sleep 0.02; done\nkill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\nexit \"$status\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return executable;
    }
    private sealed class Pipes
    {
        private readonly object _gate = new(); private readonly MemoryStream _out = new(), _err = new();
        internal void Receive(GitProcessOutput output) { lock (_gate) { (output.Stream == GitProcessOutputStream.StandardOutput ? _out : _err).Write(output.Bytes.Span); } }
        internal byte[] Output { get { lock (_gate) { return _out.ToArray(); } } } internal byte[] Error { get { lock (_gate) { return _err.ToArray(); } } }
    }
    private static async Task BytesAndDecoderAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        byte[] bytes = [.. Encoding.UTF8.GetBytes(new string('x', 4095) + "éλ😀\rprogress  12%\r\n"), 0xFF, 0xC3];
        GitBinaryResult objectResult = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes);
        Check(objectResult.Success, objectResult.StandardError); string hash = Encoding.UTF8.GetString(objectResult.StandardOutput).Trim(); Pipes pipes = new();
        GitResult result = await new GitRunner().RunAsync(f.Repository.Path, ["cat-file", "blob", hash], outputReceived: pipes.Receive);
        Check(result.Success && result.StandardOutputBytes.SequenceEqual(bytes) && pipes.Output.SequenceEqual(bytes) && pipes.Error.Length == 0, "Raw Git streams changed.");
        int start = 4089;
        for (int split = start; split <= bytes.Length; split++)
        {
            GitProcessTextDecoder decoder = new(); string text = decoder.Append(bytes.AsSpan(0, split)) + decoder.Append(bytes.AsSpan(split), flush: true);
            Check(text == result.StandardOutput, "A split UTF8 character or invalid byte was replaced at " + split);
        }
        GitProcessTextDecoder single = new(); StringBuilder individual = new(); foreach (byte value in bytes) { individual.Append(single.Append(new[] { value })); }
        individual.Append(single.Append([], flush: true)); Check(individual.ToString() == result.StandardOutput && result.StandardOutput.EndsWith("\\xFF\\xC3", StringComparison.Ordinal), "Single-byte delivery or EOF lost data.");
    }
    private static async Task LivePipesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string wrapper = Wrapper(f, "live-git"); Pipes pipes = new();
        Task<GitResult> command = new GitRunner(wrapper).RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], outputReceived: pipes.Receive);
        try
        {
            await Until(() => File.Exists(wrapper + ".pids") && pipes.Output.Length > 0 && pipes.Error.Length > 0);
            Check(!command.IsCompleted, "Output was only delivered after process completion."); File.WriteAllText(wrapper + ".release", "release\n");
            GitResult result = await command.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Success && pipes.Output.SequenceEqual(result.StandardOutputBytes) && pipes.Error.SequenceEqual(result.StandardErrorBytes), "Final pipe snapshots or exit code changed.");
            Check(result.StandardOutput.Trim() == await Head(f) && result.StandardError.Contains("Needed a single revision", StringComparison.Ordinal), "The observed output must come from actual Git commands.");
        }
        finally { File.WriteAllText(wrapper + ".release", "release\n"); await command.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    private static async Task CancelledGoodAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await Head(f); await f.Repository.StartBisectAsync();
        string wrapper = Wrapper(f, "cancel-git", gatedCommand: "bisect good"); Pipes pipes = new(); using CancellationTokenSource token = new(); GitRepository gated = new(f.Repository.Path, new GitRunner(wrapper));
        Task<GitResult> command = gated.MarkBisectAsync(GitBisectOption.Good, hash, token.Token, pipes.Receive);
        try
        {
            await Until(() => File.Exists(wrapper + ".pids") && pipes.Output.Length > 0); int[] pids = File.ReadAllText(wrapper + ".pids").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); token.Cancel();
            try { await command; throw new InvalidOperationException("Cancellation returned a fabricated result."); } catch (OperationCanceledException) { }
            await Until(() => pids.All(pid => !LinuxProcessState.IsRunning(pid))); GitBisectState state = await f.Repository.GetBisectStateAsync();
            Check(state.GoodRevisions.Contains(hash) && state.BadRevision is null && await Head(f) == hash, "The real Good effect was rolled back or another mark was dispatched.");
            Check(pipes.Output.Length > 0 && pipes.Error.Length > 0, "Cancellation discarded already observed native bytes.");
        }
        finally { token.Cancel(); try { await command; } catch (OperationCanceledException) { } }
    }
    private static async Task PrecancelledAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string before = await Head(f); int events = 0; using CancellationTokenSource token = new(); token.Cancel();
        try { await f.Repository.StartBisectAsync(cancellationToken: token.Token, outputReceived: _ => events++); throw new InvalidOperationException("Expected cancellation."); } catch (OperationCanceledException) { }
        Check(events == 0 && !(await f.Repository.GetBisectStateAsync()).IsActive && await Head(f) == before, "Pre-cancellation launched or mutated Git.");
    }
    private static async Task ObserverFailureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string wrapper = Wrapper(f, "observer-failure-git", gateBeforeGit: true);
        InvalidOperationException sentinel = new("observer failure witness");
        try { await new GitRunner(wrapper).RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], outputReceived: _ => throw sentinel); throw new InvalidOperationException("Observer failure was hidden."); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
        int[] pids = File.ReadAllText(wrapper + ".pids").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        await Until(() => pids.All(pid => !LinuxProcessState.IsRunning(pid))); Check(!(await f.Repository.GetBisectStateAsync()).IsActive, "The observer fixture must remain readonly.");
    }
    private static async Task IsolationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); Pipes hash = new(), subject = new();
        Task<GitResult> first = new GitRunner().RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], outputReceived: hash.Receive);
        Task<GitResult> second = new GitRunner().RunAsync(f.Repository.Path, ["log", "-1", "--format=%s"], outputReceived: subject.Receive);
        GitResult[] results = await Task.WhenAll(first, second);
        Check(hash.Output.SequenceEqual(results[0].StandardOutputBytes) && subject.Output.SequenceEqual(results[1].StandardOutputBytes)
            && Encoding.UTF8.GetString(subject.Output).Trim() == "Initial" && !hash.Output.SequenceEqual(subject.Output), "Observers leaked between invocations.");
    }
    private static async Task LargeInputObserverAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private pipe fixture requires Linux."); }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        byte[] blob = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024));
        GitBinaryResult saved = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: blob);
        Check(saved.Success, saved.StandardError); string hash = Encoding.UTF8.GetString(saved.StandardOutput).Trim();
        string wrapper = Path.Combine(f.Root, "duplex-git"), marker = wrapper + ".pids", ready = wrapper + ".ready";
        // An asynchronous /bin/sh list can replace fd 0 before applying <&0. Preserve the actual pipe first.
        File.WriteAllText(wrapper, "#!/bin/sh\nexec 3<&0\n( while test ! -f " + Quote(ready) + "; do sleep 0.01; done\n exec /usr/bin/env git \"$@\" ) <&3 3<&- &\nchild=$!\nexec 3<&-\nprintf '%s %s\\n' \"$$\" \"$child\" > " + Quote(marker)
            + "\nprintf 'ready\\n' > " + Quote(ready) + "\nwait \"$child\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        InvalidOperationException sentinel = new("duplex observer witness"); using CancellationTokenSource cleanup = new();
        Task<GitResult> command = new GitRunner(wrapper).RunAsync(f.Repository.Path, ["cat-file", "--batch"], cleanup.Token,
            standardInput: string.Concat(Enumerable.Repeat(hash + "\n", 120000)), outputReceived: _ => throw sentinel);
        try
        {
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Observer failure was lost or stdin completed without any Git output."); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
        }
        finally
        {
            cleanup.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
            catch (OperationCanceledException) { }
        }
        int[] pids = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Check(pids.Length == 2, "Both the wrapper and its real Git producer must be witnessed before output begins.");
        await Until(() => pids.All(pid => !LinuxProcessState.IsRunning(pid)));
        Check((await Git(f, "cat-file", "-s", hash)).StandardOutput.Trim() == blob.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "A pipe fault corrupted the real object.");
    }
    private static async Task BisectBoundariesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); Pipes pipes = new();
        GitResult started = await f.Repository.StartBisectAsync(outputReceived: pipes.Receive);
        Check(pipes.Output.SequenceEqual(started.StandardOutputBytes) && pipes.Error.SequenceEqual(started.StandardErrorBytes), "Readonly start guards were streamed as command output.");
        Pipes rejected = new();
        try { await f.Repository.MarkBisectAsync(GitBisectOption.Good, "missing-private-ref", outputReceived: rejected.Receive); throw new InvalidOperationException("Expected invalid revision."); }
        catch (GitCommandException) { }
        Check(rejected.Output.Length == 0 && rejected.Error.Length == 0, "A readonly revision-resolution probe was delivered to the mutation observer.");
        GitBisectState state = await f.Repository.GetBisectStateAsync(); Check(state.IsActive && state.GoodRevisions.Count == 0 && state.BadRevision is null, "Rejected Good changed the session.");
        Pipes reset = new(); GitResult result = await f.Repository.ResetBisectAsync(outputReceived: reset.Receive);
        Check(reset.Output.SequenceEqual(result.StandardOutputBytes) && reset.Error.SequenceEqual(result.StandardErrorBytes), "Reset streams differ from the native result.");
    }
}
