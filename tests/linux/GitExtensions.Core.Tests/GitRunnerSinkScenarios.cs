using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static class GitRunnerSinkScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("Git stdout sink delivers actual binary Git blobs sequentially without a stdout result snapshot", Guard(BinaryBlobAsync)),
        ("Git stdout sink drains simultaneous large stdin stdout and stderr with bounded diagnostics", Guard(DuplexAsync)),
        ("Git stdout sink preserves native failure and an honestly truncated UTF8 stderr prefix while legacy capture remains complete", Guard(BoundedErrorAsync)),
        ("Git stdout sink cancellation stops the exact native actor but waits for its genuinely retained callback", Guard(() => HeldCallbackAsync(cancel: true))),
        ("Git stdout sink exception stops blocked native stdin and preserves the exact callback fault after drainage", Guard(() => HeldCallbackAsync(cancel: false))),
        ("Git stdout sink preserves explicit execution environment literal arguments and journal ownership", Guard(EnvironmentAndJournalAsync)),
        ("Git stdout sink pre-cancellation and invalid sink or arguments admit no native actor or callback", Guard(RejectedAsync)),
        ("Git stdout sink retains a native empty nonzero result without inventing output", Guard(EmptyFailureAsync))
    ];

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static Func<Task> Guard(Func<Task> scenario) => async () =>
    {
        Dictionary<string, string?> previous = new[] { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_OPTIONAL_LOCKS", "DISPLAY", "WAYLAND_DISPLAY" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        string? fallback = GitRunner.AskpassFallbackPath;
        GitCommandLogCapturePolicy capture = GitCommandLog.CapturePolicy;
        GitRunner.AskpassFallbackPath = null; GitCommandLog.ConfigureCapture(new(false)); GitCommandLog.Clear();
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1"); Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        Environment.SetEnvironmentVariable("DISPLAY", null); Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        try { await scenario(); }
        finally
        {
            GitRunner.AskpassFallbackPath = fallback; GitCommandLog.ConfigureCapture(capture); GitCommandLog.Clear();
            foreach ((string name, string? value) in previous) { Environment.SetEnvironmentVariable(name, value); }
        }
    };
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private static async Task BinaryBlobAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        byte[] bytes = [.. Encoding.UTF8.GetBytes(new string('x', 4095) + "éλ😀\r\n"), 0, 0xFF, 0xC3, .. Payload(1024 * 1024)];
        GitBinaryResult saved = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes);
        Check(saved.Success, saved.StandardError); string hash = Encoding.UTF8.GetString(saved.StandardOutput).Trim();
        using MemoryStream actual = new(); int active = 0, calls = 0;
        GitStreamResult result = await new GitRunner().RunStreamingAsync(f.Repository.Path, ["cat-file", "blob", hash], async (chunk, token) =>
        {
            Check(Interlocked.Increment(ref active) == 1, "The runner admitted overlapping stdout callbacks.");
            try { Interlocked.Increment(ref calls); await Task.Yield(); token.ThrowIfCancellationRequested(); actual.Write(chunk.Span); }
            finally { Interlocked.Decrement(ref active); }
        });
        Check(result.Success && result.StandardOutputByteCount == bytes.LongLength && actual.ToArray().SequenceEqual(bytes),
            "The real Git blob bytes or streamed byte count changed.");
        Check(calls > 1 && active == 0 && result.StandardErrorBytes.Length == 0 && !result.StandardErrorTruncated && result.Warnings.Count == 0,
            "The sink was not incremental or retained invented diagnostics.");
        Check(typeof(GitStreamResult).GetProperty("StandardOutput") is null && typeof(GitStreamResult).GetProperty("StandardOutputBytes") is null,
            "A streamed result must not expose a retained stdout snapshot.");
    }

    private static async Task DuplexAsync()
    {
        RequireLinux(); using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string actor = Actor(f, "duplex", """
            def error_writer():
                remaining=3*1024*1024
                while remaining:
                    count=os.write(2,b'e'*min(8192,remaining)); remaining-=count
            error=threading.Thread(target=error_writer); error.start()
            while True:
                chunk=os.read(0,8192)
                if not chunk: break
                view=memoryview(chunk)
                while view:
                    count=os.write(1,view); view=view[count:]
            error.join()
            """);
        byte[] input = Payload(8 * 1024 * 1024); using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int calls = 0; using CancellationTokenSource cleanup = new();
        Task<GitStreamResult> command = new GitRunner(actor).RunStreamingAsync(f.Repository.Path, ["log"], (chunk, _) =>
        { hash.AppendData(chunk.Span); calls++; return ValueTask.CompletedTask; }, cleanup.Token, standardInput: input);
        try
        {
            GitStreamResult result = await command.WaitAsync(Limit);
            Check(result.Success && calls > 1 && result.StandardOutputByteCount == input.LongLength && hash.GetHashAndReset().SequenceEqual(SHA256.HashData(input)),
                "Large simultaneous stdin/stdout lost bytes or deadlocked.");
            Check(result.StandardErrorTruncated && result.StandardErrorBytes.Length == 65536 && result.StandardErrorBytes.All(value => value == (byte)'e'),
                "Large stderr was not drained independently into the bounded prefix.");
            Identity identity = ReadIdentity(actor); Check(Retired(identity), "Duplex returned before its exact native actor was reaped.");
        }
        finally { await DrainCleanupAsync(command, cleanup); }
    }

    private static async Task BoundedErrorAsync()
    {
        RequireLinux(); using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string actor = Actor(f, "bounded-error", """
            os.write(1,bytes([0,255,13,10,195]))
            payload=b'e'*65535+bytes([195,169])+b'DISCARDED-TAIL-WITNESS'+b'x'*(3*1024*1024)
            view=memoryview(payload)
            while view:
                count=os.write(2,view); view=view[count:]
            sys.exit(37)
            """);
        using MemoryStream output = new(); GitRunner runner = new(actor); using CancellationTokenSource cleanup = new();
        Task<GitStreamResult> command = runner.RunStreamingAsync(f.Repository.Path, ["log"], (chunk, _) =>
        { output.Write(chunk.Span); return ValueTask.CompletedTask; }, cleanup.Token);
        try
        {
            GitStreamResult result = await command.WaitAsync(Limit);
            Check(result.ExitCode == 37 && !result.Success && result.StandardOutputByteCount == 5 && output.ToArray().SequenceEqual(new byte[] { 0, 255, 13, 10, 195 }),
                "Streaming changed the native nonzero exit or binary stdout.");
            Check(result.StandardErrorTruncated && result.StandardErrorBytes.Length == 65536 && result.StandardErrorBytes[^1] == 195,
                "The stderr byte bound or exact prefix changed.");
            Check(result.StandardError.EndsWith("\\xC3", StringComparison.Ordinal) && !result.StandardError.Contains("DISCARDED-TAIL-WITNESS", StringComparison.Ordinal)
                && result.Warnings.Any(warning => warning.Contains("drained and discarded, not retained", StringComparison.Ordinal)),
                "An intentionally cut codepoint or discarded bytes were misrepresented.");
            Identity first = ReadIdentity(actor); Check(Retired(first), "Streaming failure returned before native retirement.");
            Task<GitBinaryResult> legacyCommand = runner.RunBinaryAsync(f.Repository.Path, ["log"], cleanup.Token);
            try
            {
                GitBinaryResult legacy = await legacyCommand.WaitAsync(Limit);
                Check(legacy.ExitCode == 37 && legacy.StandardOutput.SequenceEqual(output.ToArray()) && legacy.StandardErrorBytes.Length > 3 * 1024 * 1024
                    && legacy.StandardErrorBytes.AsSpan(0, 65536).SequenceEqual(result.StandardErrorBytes)
                    && legacy.StandardError.Contains("DISCARDED-TAIL-WITNESS", StringComparison.Ordinal),
                    "The new sink changed the existing full binary-capture contract.");
                Check(Retired(ReadIdentity(actor)), "The legacy comparison fixture leaked its actor.");
            }
            finally { await DrainCleanupAsync(legacyCommand, cleanup); }
        }
        finally { await DrainCleanupAsync(command, cleanup); }
    }

    private static async Task HeldCallbackAsync(bool cancel)
    {
        RequireLinux(); using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string actor = Actor(f, cancel ? "held-cancel" : "held-fault", """
            def error_writer():
                remaining=1024*1024
                while remaining:
                    count=os.write(2,b'e'*min(8192,remaining)); remaining-=count
            error=threading.Thread(target=error_writer); error.start()
            remaining=8*1024*1024
            while remaining:
                count=os.write(1,b'x'*min(8192,remaining)); remaining-=count
            error.join()
            """);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelledInSink = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(); InvalidOperationException sentinel = new("retained stdout callback witness");
        int active = 0, calls = 0; bool borrowedStable = false;
        GitCommandLog.Clear();
        Task<GitStreamResult> command = new GitRunner(actor).RunStreamingAsync(f.Repository.Path, ["log"], async (chunk, token) =>
        {
            Check(Interlocked.Increment(ref active) == 1, "A held stdout callback did not apply backpressure.");
            try
            {
                Interlocked.Increment(ref calls); byte[] snapshot = chunk.ToArray();
                using CancellationTokenRegistration registration = token.Register(() => cancelledInSink.TrySetResult());
                entered.TrySetResult();
                await release.Task; // Deliberately retain the actual callback even when its token is cancelled.
                borrowedStable = snapshot.AsSpan().SequenceEqual(chunk.Span);
                if (!cancel) { throw sentinel; }
            }
            finally { Interlocked.Decrement(ref active); }
        }, cancellation.Token, standardInput: Payload(8 * 1024 * 1024));
        try
        {
            await entered.Task.WaitAsync(Limit); await Until(() => File.Exists(actor + ".identity.json")); Identity identity = ReadIdentity(actor);
            await Until(() => GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId == identity.Pid));
            Check(!Retired(identity) && !command.IsCompleted && Volatile.Read(ref calls) == 1 && Volatile.Read(ref active) == 1,
                "The positive witness requires an active native producer and exactly one retained callback.");
            if (cancel)
            {
                cancellation.Cancel(); await cancelledInSink.Task.WaitAsync(Limit); await Until(() => Retired(identity));
                Check(!command.IsCompleted && Volatile.Read(ref active) == 1
                    && GitCommandLog.Snapshot().Single(entry => entry.ProcessId == identity.Pid).IsRunning,
                    "Cancellation abandoned a retained callback or completed its journal before real callback drainage.");
                release.TrySetResult();
                try { await command.WaitAsync(Limit); throw new InvalidOperationException("Cancellation fabricated a stream result."); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                Check(command.IsCanceled, "The cancelled stream did not retain its cancellation outcome.");
            }
            else
            {
                release.TrySetResult();
                try { await command.WaitAsync(Limit); throw new InvalidOperationException("The sink's exact fault disappeared."); }
                catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
                Check(command.IsFaulted, "The native stop hid the original sink failure.");
            }
            Check(borrowedStable && active == 0 && calls == 1 && Retired(identity),
                "Completion must include the actual callback, borrowed-memory lifetime and exact native PID/start retirement.");
            GitCommandLogEntry entry = GitCommandLog.Snapshot().Single(item => item.ProcessId == identity.Pid);
            Check(!entry.IsRunning && entry.Cancelled == cancel, "Journal ownership completed before the actual stream outcome.");
        }
        finally
        {
            release.TrySetResult(); cancellation.Cancel();
            try { await command; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
        }
    }

    private static async Task EnvironmentAndJournalAsync()
    {
        RequireLinux(); using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string actor = Actor(f, "environment", """
            data={'args':sys.argv[1:],'cwd':os.getcwd(),'input':sys.stdin.buffer.read().hex(),
                'environment':{key:os.environ.get(key) for key in ['H33_EXPLICIT_VALUE','GIT_TERMINAL_PROMPT','GIT_PAGER','GIT_EDITOR','GIT_SEQUENCE_EDITOR','LC_ALL']}}
            sys.stdout.buffer.write(json.dumps(data).encode('utf-8'))
            """);
        const string literal = "literal $(not-executed) ; [*]"; const string value = "explicit-value-not-for-the-journal";
        GitExecutionEnvironment context = GitExecutionEnvironment.Capture(new() { Executable = actor });
        GitCommandLog.Clear(); using MemoryStream bytes = new(); using CancellationTokenSource cleanup = new();
        Task<GitStreamResult> command = new GitRunner(context).RunStreamingAsync(f.Repository.Path, ["log", literal], (chunk, _) =>
        { bytes.Write(chunk.Span); return ValueTask.CompletedTask; }, cleanup.Token, standardInput: [0, 255, 66],
            environment: new Dictionary<string, string?> { ["H33_EXPLICIT_VALUE"] = value });
        try
        {
            GitStreamResult result = await command.WaitAsync(Limit);
            using JsonDocument json = JsonDocument.Parse(bytes.ToArray()); JsonElement root = json.RootElement, environment = root.GetProperty("environment");
            Check(result.Success && root.GetProperty("args").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "log", literal })
                && root.GetProperty("cwd").GetString() == f.Repository.Path && root.GetProperty("input").GetString() == "00ff42",
                "The shared launcher changed literal argv, cwd or binary stdin.");
            Check(environment.GetProperty("H33_EXPLICIT_VALUE").GetString() == value && environment.GetProperty("GIT_TERMINAL_PROMPT").GetString() == "0"
                && environment.GetProperty("GIT_PAGER").GetString() == "cat" && environment.GetProperty("GIT_EDITOR").GetString() == "true"
                && environment.GetProperty("GIT_SEQUENCE_EDITOR").GetString() == "true" && environment.GetProperty("LC_ALL").GetString() == "C.UTF-8",
                "The streaming branch bypassed the existing explicit Git environment policy.");
            Identity identity = ReadIdentity(actor); GitCommandLogEntry entry = GitCommandLog.Snapshot().Single();
            Check(Retired(identity) && entry.ProcessId == identity.Pid && !entry.IsRunning && !entry.Cancelled && entry.ExitCode == 0 && entry.Command == "git log",
                "The streaming branch bypassed journal admission, owned PID or true completion.");
            string logged = JsonSerializer.Serialize(entry);
            Check(!logged.Contains(value, StringComparison.Ordinal) && !logged.Contains(literal, StringComparison.Ordinal),
                "The journal retained stdout, environment or literal arguments.");
        }
        finally { await DrainCleanupAsync(command, cleanup); }
    }

    private static async Task RejectedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); int calls = 0;
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        try
        {
            await new GitRunner().RunStreamingAsync(f.Repository.Path, ["log"], (_, _) => { calls++; return ValueTask.CompletedTask; }, cancelled.Token);
            throw new InvalidOperationException("Pre-cancellation unexpectedly launched a Git stream.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        try { await new GitRunner().RunStreamingAsync(f.Repository.Path, ["log"], null!); throw new InvalidOperationException("A null sink was accepted."); }
        catch (ArgumentNullException) { }
        try { await new GitRunner().RunStreamingAsync(f.Repository.Path, ["log", "bad\0argument"], (_, _) => { calls++; return ValueTask.CompletedTask; }); throw new InvalidOperationException("A NUL argument was accepted."); }
        catch (ArgumentException) { }
        Check(calls == 0 && GitCommandLog.Snapshot().Count == 0, "A rejected stream admitted a callback or native journal entry.");
    }

    private static async Task EmptyFailureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); int calls = 0;
        GitStreamResult result = await new GitRunner().RunStreamingAsync(f.Repository.Path, ["rev-parse", "--quiet", "--verify", "refs/heads/absent-sink-fixture"],
            (_, _) => { calls++; return ValueTask.CompletedTask; });
        Check(result.ExitCode == 1 && !result.Success && calls == 0 && result.StandardOutputByteCount == 0
            && result.StandardErrorBytes.Length == 0 && result.StandardError == "" && !result.StandardErrorTruncated && result.Warnings.Count == 0,
            "The sink fabricated bytes or changed an empty native failure into an exception/success.");
    }

    private static async Task DrainCleanupAsync(Task command, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        // Main-path oracles own the failure. Cleanup still awaits the real task, never a cancelled consumer wait.
        try { await command; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) when (command.IsFaulted) { }
    }

    private static byte[] Payload(int count)
    { byte[] result = new byte[count]; for (int i = 0; i < count; i++) { result[i] = (byte)(i * 17); } return result; }
    private static void RequireLinux()
    { if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private stdout-sink actors require Linux procfs and Python3."); } }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < Limit) { await Task.Delay(10); }
        Check(condition(), "The exact stdout-sink witness did not settle within fifteen seconds.");
    }

    private readonly record struct Identity(int Pid, string Started);
    private static Identity ReadIdentity(string actor)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(actor + ".identity.json"));
        return new(json.RootElement.GetProperty("pid").GetInt32(), json.RootElement.GetProperty("start_ticks").GetString()!);
    }
    private static bool Retired(Identity identity)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + identity.Pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            // A zombie with the same start identity remains ours until the native engine reaps it.
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] != identity.Started;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
    }

    private static string Actor(RepositoryFixture fixture, string name, string body)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The native sink actor requires Linux."); }
        string path = Path.Combine(fixture.Root, "sink-" + name + " actor");
        string script = $$"""
            #!/usr/bin/python3
            import json,os,sys,threading
            from pathlib import Path
            deadline=threading.Timer(30,lambda:os._exit(124)); deadline.daemon=True; deadline.start()
            marker=Path({{JsonSerializer.Serialize(path + ".identity.json")}})
            fields=Path('/proc/self/stat').read_text().rsplit(') ',1)[1].split()
            pending=Path(str(marker)+'.pending')
            pending.write_text(json.dumps({'pid':os.getpid(),'start_ticks':fields[19]}))
            os.replace(pending,marker)
            """ + "\n" + body + "\n";
        File.WriteAllText(path, script, new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
