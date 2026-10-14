using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitExtensions.Linux;

internal static class PushStreamingScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("push delivers actual hook blob bytes before transfer completes and retains exact final streams", Guard(LiveBytesAsync)),
        ("push null and explicit observers preserve native arguments tracking configuration and checkout", Guard(ObserverEquivalenceAsync)),
        ("streamed non-atomic push retains an accepted ref and the native rejection of another row", Guard(PartialAcceptanceAsync)),
        ("streamed push cancellation after receipt retains remote refs and stops its witnessed process group", Guard(CancellationAfterReceiptAsync)),
        ("a real pre-push reference probe error emits no transfer callbacks and leaves state intact", Guard(ProbeFailureAsync))
    ];

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static Func<Task> Guard(Func<Task> scenario) => async () =>
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private push hooks and procfs witnesses require Linux."); }
        Dictionary<string, string?> previous = [];
        foreach ((string name, string value) in new[] { ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
        { previous[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        try { await scenario(); }
        finally { foreach ((string name, string? value) in previous) { Environment.SetEnvironmentVariable(name, value); } }
    };

    private static async Task LiveBytesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        byte[] payload = [.. Encoding.UTF8.GetBytes(new string('x', 4095) + "éλ😀\rtransfer 12%\r\n"), 0xFF, 0xC3];
        string blob = await f.CommitBlobAsync(payload);
        string marker = Path.Combine(f.Seed.Root, "live-hook.pids"), release = marker + ".release";
        string hooks = Path.Combine(f.Seed.Root, "live client hooks");
        // Git may route hook stdout to stderr. Address the witnessed client pipes explicitly;
        // each producer is still an actual Git cat-file reading the committed binary blob.
        WriteHook(hooks, "pre-push", "/usr/bin/env git cat-file blob " + Quote(blob) + " > \"/proc/$PPID/fd/1\"\n/usr/bin/env git cat-file blob " + Quote(blob)
            + " > \"/proc/$PPID/fd/2\"\n" + Gate(marker, release));
        await f.Local.SetConfigAsync("core.hooksPath", hooks);
        Checkout before = await CaptureCheckoutAsync(f.Local);
        Pipes pipes = new();
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = f.Local.PushAsync(new GitPushRequest("origin", Source: "main", DestinationRef: "live/été"), cleanup.Token, pipes.Receive);
        try
        {
            await UntilAsync(() => File.Exists(marker) && pipes.Output.SequenceEqual(payload) && pipes.Error.AsSpan().IndexOf(payload) >= 0, pending);
            Check(!pending.IsCompleted, "Both real Git blob streams must arrive while the pre-push hook is still gated.");
            Check(!await HasRefAsync(f.Remote, "refs/heads/live/été"), "The pre-push barrier must precede the actual remote ref update.");
            File.WriteAllText(release, "release\n");
            GitResult result = await pending.WaitAsync(Deadline);
            Check(result.Success && pipes.Events > 0, "The released push must finish with its actual successful Git result.");
            SameStreams(pipes, result);
            Check(result.StandardOutputBytes.SequenceEqual(payload), "Reference/config probes must not contaminate the actual push stdout.");
            Check(result.StandardErrorBytes.AsSpan().IndexOf(payload) >= 0 && result.StandardError.Contains("live/été", StringComparison.Ordinal),
                "The raw blob and native transfer/ref diagnostic must both survive.");
            Check(await f.Remote.ResolveRevisionAsync("refs/heads/live/été") == before.Head, "The destination receives the actual committed candidate.");
            await SameCheckoutAsync(f.Local, before);
            Check(ReadMarker(marker).All(pid => !LinuxProcessState.IsRunning(pid)), "The normal hook exit must reap its private sleeper.");
        }
        finally { File.WriteAllText(release, "release\n"); await StopAsync(pending, cleanup); }
    }

    private static async Task ObserverEquivalenceAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.CommitBlobAsync(Encoding.UTF8.GetBytes("candidate with literal branch/tag collision λ\n"));
        await f.Local.CreateTagAsync("main", f.Initial);
        GitRepository plain = await GitRepository.CloneAsync(f.Local.Path, Path.Combine(f.Seed.Root, "plain source"));
        GitRepository observed = await GitRepository.CloneAsync(f.Local.Path, Path.Combine(f.Seed.Root, "observed source"));
        foreach (GitRepository source in new[] { plain, observed })
        {
            await RepositoryFixture.ConfigureAsync(source);
            await source.SetRemoteUrlAsync("origin", f.Remote.Path);
            await source.SetConfigAsync("push.default", "upstream");
            await source.SetConfigAsync("push.followTags", "false");
        }
        Checkout plainBefore = await CaptureCheckoutAsync(plain), observedBefore = await CaptureCheckoutAsync(observed);
        Check(plainBefore.Config.SequenceEqual(observedBefore.Config), "Both sources must start with byte-identical local configuration.");
        GitPushRequest request = new("origin", Source: "main", DestinationRef: "mapped/été", SetUpstream: true);
        GitResult withoutObserver = await plain.PushAsync(request, outputReceived: null);
        Pipes pipes = new();
        GitResult withObserver = await observed.PushAsync(request, outputReceived: pipes.Receive);
        Check(withoutObserver.Success && withObserver.Success, "Both paths must retain the actual successful push result.");
        SameStreams(pipes, withObserver);
        Check(await ReferencesAsync(plain) == await ReferencesAsync(observed), "Observers must preserve the same branch/tag/tracking refs, including the branch/tag name collision.");
        Check(File.ReadAllBytes(ConfigPath(plain)).SequenceEqual(File.ReadAllBytes(ConfigPath(observed))), "Replace tracking must produce the same exact configuration with or without observing output.");
        Check((await observed.GetReferencesAsync()).Single(reference => reference.IsCurrent).Upstream == "origin/mapped/été",
            "The selected destination, rather than the colliding tag or remote main, establishes tracking.");
        Check(await f.Remote.ResolveRevisionAsync("refs/heads/mapped/été") == observedBefore.Head
            && await f.Remote.ResolveRevisionAsync("refs/heads/main") == f.Initial, "The actual branch tip reaches only the selected destination.");
        await SameCheckoutAsync(plain, plainBefore, compareConfig: false);
        await SameCheckoutAsync(observed, observedBefore, compareConfig: false);

        Checkout failureBefore = await CaptureCheckoutAsync(observed);
        string remoteBefore = await ReferencesAsync(f.Remote), refsBefore = await ReferencesAsync(observed);
        GitPushRequest invalid = new("origin", Source: "main", DestinationRef: "invalid..name");
        GitCommandException plainError = await ExpectAsync<GitCommandException>(() => observed.PushAsync(invalid, outputReceived: null));
        Pipes failed = new();
        GitCommandException streamError = await ExpectAsync<GitCommandException>(() => observed.PushAsync(invalid, outputReceived: failed.Receive));
        Check(plainError.Arguments.SequenceEqual(streamError.Arguments)
            && streamError.Arguments.SequenceEqual(new[] { "push", "--progress", "--", "origin", "refs/heads/main:refs/heads/invalid..name" }),
            "The actual native push errors must expose identical qualified arguments and no added force/atomic policy.");
        Check(plainError.Result.ExitCode == streamError.Result.ExitCode && streamError.Result.ExitCode != 0
            && plainError.Result.StandardOutputBytes.SequenceEqual(streamError.Result.StandardOutputBytes)
            && plainError.Result.StandardErrorBytes.SequenceEqual(streamError.Result.StandardErrorBytes), "An explicit observer must preserve native error exit and bytes.");
        SameStreams(failed, streamError.Result);
        Check(failed.Events > 0 && streamError.Result.StandardError.Contains("invalid", StringComparison.Ordinal), "A real push failure must retain its actual diagnostic callbacks.");
        Check(await ReferencesAsync(observed) == refsBefore && await ReferencesAsync(f.Remote) == remoteBefore, "Neither failing path may change any ref.");
        await SameCheckoutAsync(observed, failureBefore);
    }

    private static async Task PartialAcceptanceAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string blob = await f.CommitBlobAsync(Encoding.UTF8.GetBytes("actual update-hook object λ\n"));
        string head = await f.Local.ResolveRevisionAsync("HEAD");
        await f.Remote.RunAsync(["update-ref", "refs/heads/accepted/été", f.Initial]);
        await f.Remote.RunAsync(["update-ref", "refs/heads/refused/été", f.Initial]);
        await f.Local.FetchRemoteAsync("origin");
        string log = Path.Combine(f.Seed.Root, "update-refs.log"), hooks = Path.Combine(f.Seed.Root, "partial server hooks");
        WriteHook(hooks, "update", "printf '%s\\n' \"$1\" >> " + Quote(log) + "\n/usr/bin/env git cat-file blob " + Quote(blob)
            + " >&2\ncase \"$1\" in " + Quote("refs/heads/refused/été") + ") exit 1;; esac\n");
        await f.Remote.SetConfigAsync("core.hooksPath", hooks);
        f.Seed.Write("seed.txt", "uncommitted local bytes\n"); f.Seed.Write("untracked.txt", "keep untracked λ\n");
        Checkout before = await CaptureCheckoutAsync(f.Local);
        Pipes pipes = new();
        GitCommandException failure = await ExpectAsync<GitCommandException>(() => f.Local.PushAsync(new GitPushRequest("origin", GitPushMode.Multiple,
            Actions: [new("main", "accepted/été"), new("main", "refused/été")]), outputReceived: pipes.Receive));
        SameStreams(pipes, failure.Result);
        Check(!failure.Result.Success && failure.Result.StandardError.Contains("[remote rejected]", StringComparison.Ordinal)
            && failure.Result.StandardError.Contains("hook declined", StringComparison.Ordinal)
            && failure.Result.StandardError.Contains("actual update-hook object λ", StringComparison.Ordinal), "The result must retain the native hook rejection and real object output.");
        Check(!failure.Arguments.Contains("--atomic") && !failure.Arguments.Contains("--force"), "An observed multi-row push must retain its existing non-atomic, unforced behavior.");
        Check(File.ReadAllLines(log).Order(StringComparer.Ordinal).SequenceEqual(new[] { "refs/heads/accepted/été", "refs/heads/refused/été" }),
            "Both actual ref updates must reach the server update-hook.");
        Check(await f.Remote.ResolveRevisionAsync("refs/heads/accepted/été") == head
            && await f.Remote.ResolveRevisionAsync("refs/heads/refused/été") == f.Initial
            && await f.Remote.ResolveRevisionAsync("refs/heads/main") == f.Initial, "The accepted update survives the other row's failure without rollback or an unrelated main update.");
        Check(await f.Local.ResolveRevisionAsync("refs/remotes/origin/accepted/été") == head
            && await f.Local.ResolveRevisionAsync("refs/remotes/origin/refused/été") == f.Initial, "Native tracking reflects each actual accepted/refused row independently.");
        await SameCheckoutAsync(f.Local, before);
    }

    private static async Task CancellationAfterReceiptAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        byte[] payload = Encoding.UTF8.GetBytes("received object before cancellation λ\n");
        byte[] objectText = payload[..^1]; // Sideband adds its own line prefix/suffix; the object text stays literal.
        string blob = await f.CommitBlobAsync(payload), marker = Path.Combine(f.Seed.Root, "post-receive.pids"), release = marker + ".release";
        string hooks = Path.Combine(f.Seed.Root, "gated server hooks");
        WriteHook(hooks, "post-receive", "/usr/bin/env git cat-file blob " + Quote(blob) + " >&2\n" + Gate(marker, release));
        await f.Remote.SetConfigAsync("core.hooksPath", hooks);
        Checkout before = await CaptureCheckoutAsync(f.Local);
        Pipes pipes = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = f.Local.PushAsync(new GitPushRequest("origin", Source: "main", DestinationRef: "received/été"), cancellation.Token, pipes.Receive);
        try
        {
            await UntilAsync(() => File.Exists(marker) && pipes.Error.AsSpan().IndexOf(objectText) >= 0, pending);
            Check(!pending.IsCompleted && await f.Remote.ResolveRevisionAsync("refs/heads/received/été") == before.Head,
                "Cancellation must occur while post-receive remains live, after the actual remote ref has changed.");
            int[] required = ReadMarker(marker);
            Actor hook = ReadActor(required[0]) ?? throw new InvalidOperationException("The witnessed post-receive hook is no longer alive.");
            Actor test = ReadActor(Environment.ProcessId) ?? throw new InvalidOperationException("The test process must be alive.");
            Check(hook.Group > 1 && hook.Group != test.Group, "The live push must own a private process group, distinct from the test runner.");
            Actor[] actors = ReadGroup(hook.Group);
            Check(required.All(pid => actors.Any(actor => actor.Pid == pid && actor.IsRunning)), "The hook, real receive-pack parent and private sleeper must all be witnessed alive.");
            Check(ReadArguments(required[1]).Any(argument => argument.Contains("receive-pack", StringComparison.Ordinal))
                && actors.Any(actor => ReadArguments(actor.Pid).Contains("push")), "The group witness must include the actual client push and server receive-pack, not only hook actors.");
            byte[] alreadyObserved = pipes.Error;
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Check(actors.All(Stopped) && ReadGroup(hook.Group).All(actor => !actor.IsRunning),
                "Every witnessed actor and any remaining member of the private group must have stopped before cancellation returns.");
            Check(pipes.Error.AsSpan().StartsWith(alreadyObserved) && alreadyObserved.AsSpan().IndexOf(objectText) >= 0,
                "Cancellation must retain the bytes already observed from the actual received object.");
            Check(await f.Remote.ResolveRevisionAsync("refs/heads/received/été") == before.Head
                && await f.Remote.ResolveRevisionAsync("refs/heads/main") == f.Initial, "Cancellation after receipt must preserve the actual remote update and leave unrelated refs unchanged.");
            await SameCheckoutAsync(f.Local, before);
            // An explicit new operation verifies the released mutation lock; no recovery is implicit in PushAsync.
            await f.Remote.SetConfigAsync("core.hooksPath", "/dev/null");
            GitResult next = await f.Local.PushAsync(new GitPushRequest("origin", Source: "HEAD", DestinationRef: "explicit-after-cancel"));
            Check(next.Success && await f.Remote.ResolveRevisionAsync("refs/heads/explicit-after-cancel") == before.Head,
                "The runner and mutation lock must remain usable after real process cleanup.");
        }
        finally { File.WriteAllText(release, "release\n"); await StopAsync(pending, cancellation); }
    }

    private static async Task ProbeFailureAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string marker = Path.Combine(f.Seed.Root, "must-not-push"), hooks = Path.Combine(f.Seed.Root, "probe guard hooks");
        WriteHook(hooks, "pre-push", "printf 'ran\\n' > " + Quote(marker) + "\n");
        await f.Local.SetConfigAsync("core.hooksPath", hooks);
        Checkout before = await CaptureCheckoutAsync(f.Local);
        string localRefs = await ReferencesAsync(f.Local), remoteRefs = await ReferencesAsync(f.Remote);
        byte[] config = File.ReadAllBytes(ConfigPath(f.Local));
        Pipes pipes = new();
        try
        {
            await File.AppendAllTextAsync(ConfigPath(f.Local), "\n[deliberately-unterminated-private-section\n");
            GitPushRequest request = new("origin", Source: "main", DestinationRef: "must-not-exist");
            GitCommandException failure = await ExpectAsync<GitCommandException>(() => f.Local.PushAsync(request, outputReceived: pipes.Receive));
            GitCommandException plain = await ExpectAsync<GitCommandException>(() => f.Local.PushAsync(request, outputReceived: null));
            Check(failure.Arguments.First() == "for-each-ref" && failure.Result.ExitCode != 0
                && failure.Result.StandardError.Contains("bad config line", StringComparison.Ordinal), "The real reference qualification probe must fail with its native config error.");
            Check(failure.Arguments.SequenceEqual(plain.Arguments) && failure.Result.ExitCode == plain.Result.ExitCode
                && failure.Result.StandardOutputBytes.SequenceEqual(plain.Result.StandardOutputBytes)
                && failure.Result.StandardErrorBytes.SequenceEqual(plain.Result.StandardErrorBytes), "Observed/unobserved requests must preserve the exact pre-push failure.");
            Check(pipes.Events == 0 && pipes.Output.Length == 0 && pipes.Error.Length == 0 && !File.Exists(marker),
                "A failing readonly probe must neither dispatch push nor masquerade as transfer output.");
        }
        finally { File.WriteAllBytes(ConfigPath(f.Local), config); }
        Check(await ReferencesAsync(f.Local) == localRefs && await ReferencesAsync(f.Remote) == remoteRefs, "The rejected pre-push request must preserve every local/remote ref.");
        await SameCheckoutAsync(f.Local, before);
    }

    private sealed class Pipes
    {
        private readonly object _gate = new();
        private readonly MemoryStream _stdout = new(), _stderr = new();
        private int _events;
        public void Receive(GitProcessOutput output)
        { lock (_gate) { (output.Stream == GitProcessOutputStream.StandardOutput ? _stdout : _stderr).Write(output.Bytes.Span); _events++; } }
        public byte[] Output { get { lock (_gate) { return _stdout.ToArray(); } } }
        public byte[] Error { get { lock (_gate) { return _stderr.ToArray(); } } }
        public int Events { get { lock (_gate) { return _events; } } }
    }

    private static void SameStreams(Pipes pipes, GitResult result)
        => Check(pipes.Output.SequenceEqual(result.StandardOutputBytes) && pipes.Error.SequenceEqual(result.StandardErrorBytes),
            "The callbacks must contain exactly the actual final stdout/stderr bytes, in stream order and without probe output.");

    private sealed record Checkout(string Head, byte[] Index, byte[] Config, IReadOnlyDictionary<string, byte[]> Files, string BranchesAndTags);
    private static string ConfigPath(GitRepository repository) => Path.Combine(repository.Path, ".git", "config");
    private static async Task<Checkout> CaptureCheckoutAsync(GitRepository repository)
        => new(await repository.ResolveRevisionAsync("HEAD"), File.ReadAllBytes(Path.Combine(repository.Path, ".git", "index")),
            File.ReadAllBytes(ConfigPath(repository)), WorktreeFiles(repository), await BranchesAndTagsAsync(repository));
    private static async Task SameCheckoutAsync(GitRepository repository, Checkout before, bool compareConfig = true)
    {
        Check(await repository.ResolveRevisionAsync("HEAD") == before.Head && await BranchesAndTagsAsync(repository) == before.BranchesAndTags, "Push must preserve actual HEAD and every local branch/tag.");
        Check(File.ReadAllBytes(Path.Combine(repository.Path, ".git", "index")).SequenceEqual(before.Index), "Push must preserve the raw index.");
        if (compareConfig) { Check(File.ReadAllBytes(ConfigPath(repository)).SequenceEqual(before.Config), "Push must preserve configuration without explicit replace tracking."); }
        IReadOnlyDictionary<string, byte[]> files = WorktreeFiles(repository);
        Check(files.Count == before.Files.Count && before.Files.All(item => files.TryGetValue(item.Key, out byte[]? bytes) && bytes.SequenceEqual(item.Value)), "Push must preserve every worktree/untracked file byte.");
    }
    private static IReadOnlyDictionary<string, byte[]> WorktreeFiles(GitRepository repository)
        => Directory.EnumerateFiles(repository.Path, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(repository.Path, path).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(repository.Path, path), File.ReadAllBytes, StringComparer.Ordinal);
    private static async Task<string> ReferencesAsync(GitRepository repository)
        => string.Join('\n', (await repository.GetReferencesAsync()).OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .Select(reference => reference.Name + "\t" + reference.Hash + "\t" + reference.Upstream));
    private static async Task<string> BranchesAndTagsAsync(GitRepository repository)
        => string.Join('\n', (await repository.GetReferencesAsync()).Where(reference => reference.IsBranch || reference.IsTag).OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .Select(reference => reference.Name + "\t" + reference.Hash));
    private static async Task<bool> HasRefAsync(GitRepository repository, string name) => (await repository.GetReferencesAsync()).Any(reference => reference.Name == name);

    private static void WriteHook(string directory, string name, string body)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private push hooks require Linux."); }
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Gate(string marker, string release)
        => "sleep 60 &\nchild=$!\ntrap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\ntrap 'exit 130' INT TERM\n"
            + "printf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > " + Quote(marker + ".tmp") + "\nmv " + Quote(marker + ".tmp") + " " + Quote(marker)
            + "\nwhile test ! -f " + Quote(release) + "; do kill -0 \"$child\" || exit 124; sleep 0.02; done\n";
    private static int[] ReadMarker(string path)
    {
        int[] pids = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Check(pids.Length == 3 && pids.All(pid => pid > 1), "The hook marker must record its actual PID, receiver/client parent, and sleeper PID.");
        return pids;
    }

    private sealed record Actor(int Pid, int Group, long StartTime, char State)
    { public bool IsRunning => State is not ('Z' or 'X' or 'x'); }
    private static Actor? ReadActor(int pid)
    {
        string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        try
        {
            string stat = File.ReadAllText(path); int closing = stat.LastIndexOf(')');
            if (closing < 0) { throw new FormatException("Incomplete procfs witness."); }
            string[] fields = stat[(closing + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(pid, int.Parse(fields[2], CultureInfo.InvariantCulture), long.Parse(fields[19], CultureInfo.InvariantCulture), fields[0][0]);
        }
        catch (IOException) when (!File.Exists(path)) { return null; }
    }
    private static Actor[] ReadGroup(int group)
        => Directory.EnumerateDirectories("/proc").Select(path => int.TryParse(Path.GetFileName(path), out int pid) ? ReadActor(pid) : null)
            .OfType<Actor>().Where(actor => actor.Group == group).ToArray();
    private static bool Stopped(Actor witness)
    { Actor? current = ReadActor(witness.Pid); return current is null || current.StartTime != witness.StartTime || !current.IsRunning; }
    private static string[] ReadArguments(int pid)
    {
        string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/cmdline";
        try { return Encoding.UTF8.GetString(File.ReadAllBytes(path)).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
        catch (IOException) when (!File.Exists(path)) { return []; }
    }

    private static async Task UntilAsync(Func<bool> condition, Task operation)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && !operation.IsCompleted && timer.Elapsed < Deadline) { await Task.Delay(10); }
        Check(condition(), "The live push witness did not settle before the bounded deadline or the operation ended prematurely.");
    }
    private static async Task StopAsync(Task operation, CancellationTokenSource cleanup)
    { cleanup.Cancel(); try { await operation.WaitAsync(Deadline); } catch (OperationCanceledException) { } }
    private static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + " from the actual operation."); }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture seed, GitRepository remote, string initial) { Seed = seed; Remote = remote; Initial = initial; }
        public RepositoryFixture Seed { get; }
        public GitRepository Local => Seed.Repository;
        public GitRepository Remote { get; }
        public string Initial { get; }
        public static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture seed = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository remote = await GitRepository.InitAsync(Path.Combine(seed.Root, "private remote.git"), bare: true);
                await RepositoryFixture.ConfigureAsync(remote);
                await seed.Repository.AddRemoteAsync("origin", remote.Path);
                await seed.Repository.PushAsync(new GitPushRequest("origin", Source: "main", SetUpstream: true));
                return new(seed, remote, await seed.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { seed.Dispose(); throw; }
        }
        public async Task<string> CommitBlobAsync(byte[] bytes)
        {
            await File.WriteAllBytesAsync(Seed.File("progress blob.bin"), bytes);
            await Local.StageAsync(["progress blob.bin"]); await Local.CommitAsync("Real push streaming candidate");
            return (await Local.RunAsync(["rev-parse", "--verify", "--end-of-options", "HEAD:progress blob.bin"])).StandardOutput.TrimEnd('\r', '\n');
        }
        public void Dispose() => Seed.Dispose();
    }
}
