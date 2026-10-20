using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class GitCommandLogScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("command journal retains its six-field constructor and immutable launch identities through PID and completion", Guard(SnapshotsAsync)),
        ("command journal publishes real running and confirmed PID before a gated Git command finishes", Guard(LiveAsync)),
        ("command journal keeps launch order when real Git commands finish in reverse order", Guard(ReverseCompletionAsync)),
        ("command journal preserves real zero nonzero launch failure and precancellation outcomes", Guard(OutcomesAsync)),
        ("command journal cancellation drains real actors and records no invented exit status", Guard(CancellationAsync)),
        ("command journal Clear while real Git is running never resurrects it at completion", Guard(ClearRunningAsync)),
        ("command journal eviction and concurrent Clear reject every late PID and completion", Guard(CapacityAndClearAsync)),
        ("command journal observers run outside the lock and cannot turn successful Git into failure", Guard(ObserversAsync)),
        ("external process callbacks retain exact success and throw semantics without Git journal entries", Guard(ExternalCallbacksAsync)),
        ("command journal keeps all argument environment input output exception and executable sentinels out", Guard(SecretsAsync))
    ];

    private static Func<Task> Guard(Func<Task> scenario) => async () =>
    {
        Dictionary<string, string?> previous = [];
        foreach ((string name, string value) in new[] { ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
        { previous[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        GitCommandLog.Clear();
        try { await scenario(); }
        finally { GitCommandLog.Clear(); foreach ((string name, string? value) in previous) { Environment.SetEnvironmentVariable(name, value); } }
    };
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    // The recorder is deliberately internal. Pure lifetime races use reflection rather than expanding its production API.
    private static object? Recorder(string name, params object?[] arguments)
        => typeof(GitCommandLog).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
    private static long Begin(params string[] arguments) => (long)Recorder("Begin", arguments, "/private/metadata-directory", DateTimeOffset.UnixEpoch)!;
    private static void Pid(long id, int pid) => Recorder("SetProcessId", id, pid);
    private static void Complete(long id, int? exit = 0, bool cancelled = false) => Recorder("Complete", id, TimeSpan.FromMilliseconds(7), exit, cancelled);

    private static Task SnapshotsAsync()
    {
        DateTimeOffset started = DateTimeOffset.UnixEpoch;
        GitCommandLogEntry legacy = new("git status", "/private", started, TimeSpan.FromSeconds(1), 7, false);
        var (command, directory, date, duration, exit, cancelled) = legacy;
        Check(command == legacy.Command && directory == legacy.Directory && date == started && duration == TimeSpan.FromSeconds(1)
            && exit == 7 && !cancelled, "The existing positional construction/deconstruction contract changed.");
        long id = Begin("-c", "http.extraHeader=not-retained", "status"); IReadOnlyList<GitCommandLogEntry> initial = GitCommandLog.Snapshot();
        Check(initial.Count == 1 && initial[0].Id == id && id > 0 && initial[0].IsRunning && initial[0].ProcessId is null
            && initial[0].ExitCode is null && !initial[0].Cancelled && initial[0].Duration == TimeSpan.Zero, "Begin metadata.");
        if (initial is IList<GitCommandLogEntry> mutable)
        {
            try { mutable[0] = legacy; throw new InvalidOperationException("Snapshot permits collection mutation."); }
            catch (NotSupportedException) { }
        }
        Pid(id, Environment.ProcessId); IReadOnlyList<GitCommandLogEntry> running = GitCommandLog.Snapshot();
        Pid(id, Environment.ProcessId + 1); Complete(id, 7); IReadOnlyList<GitCommandLogEntry> ended = GitCommandLog.Snapshot();
        Check(initial[0].ProcessId is null && initial[0].IsRunning && running[0].IsRunning && running[0].ExitCode is null,
            "A retained snapshot changed when the current entry advanced.");
        Check(running[0].Id == id && ended[0].Id == id && ended[0].ProcessId == Environment.ProcessId && !ended[0].IsRunning
            && ended[0].ExitCode == 7 && ended[0].Duration == TimeSpan.FromMilliseconds(7), "PID/identity/completion metadata.");
        GitCommandLogEntry altered = ended[0] with { ExitCode = 99 };
        Check(altered.ExitCode == 99 && GitCommandLog.Snapshot()[0].ExitCode == 7, "A record copy mutated the journal.");
        Complete(id, 99); Pid(id, 2); Check(GitCommandLog.Snapshot()[0] == ended[0], "Late duplicate update changed a completed operation.");
        GitCommandLog.Clear(); long next = Begin("--version"); Check(next > id && GitCommandLog.Snapshot()[0].Command == "git", "Clear reused an identity or changed the verb allowlist.");
        return Task.CompletedTask;
    }

    private static async Task LiveAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); using Gate gate = new(fixture, "live");
        ConcurrentQueue<GitCommandLogEntry> changes = new();
        void Changed() { foreach (GitCommandLogEntry entry in GitCommandLog.Snapshot()) { changes.Enqueue(entry); } }
        GitCommandLog.Changed += Changed;
        Task<GitResult>? operation = null;
        try
        {
            operation = gate.Run(); await gate.Ready(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
            Check(!operation.IsCompleted && live.IsRunning && live.ProcessId == gate.ProcessId && live.ExitCode is null && LinuxProcessState.IsRunning(gate.ProcessId),
                "Running must expose the actual confirmed PID before release, not a completion-only record.");
            Check(changes.TryPeek(out GitCommandLogEntry? first) && first is { IsRunning: true, ProcessId: null } && first.Id == live.Id, "Start was not published before PID.");
            IReadOnlyList<GitCommandLogEntry> before = GitCommandLog.Snapshot(); gate.Release(); GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(15));
            GitCommandLogEntry done = GitCommandLog.Snapshot().Single();
            Check(result.ExitCode == 0 && done.Id == live.Id && done.ProcessId == gate.ProcessId && !done.IsRunning && done.ExitCode == result.ExitCode
                && done.Duration > TimeSpan.Zero && !done.Cancelled, "Real command completion metadata.");
            Check(before[0].IsRunning && before[0].ExitCode is null && !LinuxProcessState.IsRunning(gate.ProcessId), "Snapshot mutation or leaked actor after actual wait.");
            Check(changes.Any(entry => entry.Id == done.Id && entry.IsRunning && entry.ProcessId == gate.ProcessId)
                && changes.Last().IsRunning == false, "PID or final notification missing.");
        }
        finally
        {
            GitCommandLog.Changed -= Changed; gate.Release(); if (operation is not null) { await operation.WaitAsync(TimeSpan.FromSeconds(15)); }
        }
    }

    private static async Task ReverseCompletionAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear();
        using Gate first = new(fixture, "first"), second = new(fixture, "second");
        Task<GitResult> a = first.Run(); Task<GitResult>? b = null;
        try
        {
            await first.Ready(); long firstId = GitCommandLog.Snapshot().Single().Id; b = second.Run(); await second.Ready();
            long[] order = GitCommandLog.Snapshot().Select(entry => entry.Id).ToArray(); Check(order.Length == 2 && order[0] == firstId && order[1] > firstId, "Launch order/identity.");
            second.Release(); Check((await b.WaitAsync(TimeSpan.FromSeconds(15))).Success, "Second real Git command.");
            IReadOnlyList<GitCommandLogEntry> partial = GitCommandLog.Snapshot();
            Check(partial.Select(entry => entry.Id).SequenceEqual(order) && partial[0].IsRunning && !partial[1].IsRunning && !a.IsCompleted,
                "Completing the newer process reordered or completed the older entry.");
            first.Release(); Check((await a.WaitAsync(TimeSpan.FromSeconds(15))).Success, "First real Git command.");
            Check(GitCommandLog.Snapshot().Select(entry => entry.Id).SequenceEqual(order) && GitCommandLog.Snapshot().All(entry => !entry.IsRunning), "Completion order replaced launch order.");
        }
        finally { first.Release(); second.Release(); await a.WaitAsync(TimeSpan.FromSeconds(15)); if (b is not null) { await b.WaitAsync(TimeSpan.FromSeconds(15)); } }
    }

    private static async Task OutcomesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear();
        GitResult good = await new GitRunner().RunAsync(fixture.Repository.Path, ["--version"]);
        GitResult bad = await new GitRunner().RunAsync(fixture.Repository.Path, ["rev-parse", "--verify", "command-journal-deliberately-missing-ref"]);
        Check(good.ExitCode == 0 && bad.ExitCode != 0, "Require native success and native nonzero.");
        Win32Exception launch = await Expect<Win32Exception>(() => new GitRunner(Path.Combine(fixture.Root, "missing-git-executable")).RunAsync(fixture.Repository.Path, ["status"]));
        GitCommandLogEntry[] entries = GitCommandLog.Snapshot().ToArray();
        Check(entries.Length == 3 && entries.Select(entry => entry.ExitCode).SequenceEqual(new int?[] { good.ExitCode, bad.ExitCode, null })
            && entries.All(entry => !entry.IsRunning && !entry.Cancelled && entry.Duration >= TimeSpan.Zero)
            && entries.Take(2).All(entry => entry.ProcessId > 0) && entries[2].ProcessId is null, "Native exit or failed launch was invented/lost.");
        Check(launch.NativeErrorCode != 0, "Preserve native launch error.");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel(); int count = entries.Length;
        await Expect<OperationCanceledException>(() => new GitRunner().RunAsync(fixture.Repository.Path, ["status"], cancelled.Token));
        Check(GitCommandLog.Snapshot().Count == count, "Pre-cancelled work admitted a fake process.");
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); using Gate gate = new(fixture, "cancel");
        using CancellationTokenSource cancel = new(); Task<GitResult> operation = gate.Run(cancel.Token);
        try
        {
            await gate.Ready(); int pid = gate.ProcessId; GitCommandLogEntry running = GitCommandLog.Snapshot().Single();
            cancel.Cancel(); await Expect<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
            GitCommandLogEntry ended = GitCommandLog.Snapshot().Single();
            Check(ended.Id == running.Id && ended.ProcessId == pid && !ended.IsRunning && ended.Cancelled && ended.ExitCode is null,
                "Cancellation invented exit zero or changed process identity.");
            Check(!LinuxProcessState.IsRunning(pid) && !LinuxProcessState.IsRunning(gate.ChildId), "Cancellation returned before owned actors stopped.");
        }
        finally { cancel.Cancel(); gate.Release(); try { await operation.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { } }
    }

    private static async Task ClearRunningAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); using Gate gate = new(fixture, "clear");
        Task<GitResult> operation = gate.Run();
        try
        {
            await gate.Ready(); long removed = GitCommandLog.Snapshot().Single().Id; GitCommandLog.Clear();
            Check(!operation.IsCompleted && GitCommandLog.Snapshot().Count == 0, "Clear must not stop real Git.");
            gate.Release(); Check((await operation.WaitAsync(TimeSpan.FromSeconds(15))).Success, "Real Git after Clear failed.");
            Check(GitCommandLog.Snapshot().Count == 0, "Completion resurrected a cleared entry.");
            long next = Begin("status"); Check(next > removed, "Clear reset the identity sequence.");
        }
        finally { gate.Release(); await operation.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    private static async Task CapacityAndClearAsync()
    {
        long evicted = Begin("status");
        for (int i = 0; i < GitCommandLog.Capacity; i++) { Begin("diff"); }
        IReadOnlyList<GitCommandLogEntry> full = GitCommandLog.Snapshot();
        Check(full.Count == 500 && full.All(entry => entry.Id != evicted) && full.Select(entry => entry.Id).SequenceEqual(full.Select(entry => entry.Id).Order()), "Capacity/order at launch.");
        Pid(evicted, 123); Complete(evicted); Check(GitCommandLog.Snapshot().Count == 500 && GitCommandLog.Snapshot().All(entry => entry.Id != evicted), "Eviction resurrected a process.");
        GitCommandLog.Clear(); ConcurrentBag<long> ids = []; int admitted = 0, boundFailed = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] workers = Enumerable.Range(0, 700).Select(_ => Task.Run(async () =>
        {
            long id = Begin("log"); ids.Add(id); Interlocked.Increment(ref admitted);
            if (GitCommandLog.Snapshot().Count > 500) { Interlocked.Exchange(ref boundFailed, 1); }
            await release.Task; Pid(id, Environment.ProcessId); Complete(id);
            if (GitCommandLog.Snapshot().Count > 500) { Interlocked.Exchange(ref boundFailed, 1); }
        })).ToArray();
        try
        {
            await Until(() => Volatile.Read(ref admitted) == workers.Length); Check(GitCommandLog.Snapshot().Count == 500, "Concurrent launch capacity.");
            GitCommandLog.Clear(); release.TrySetResult(); await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
            Check(boundFailed == 0 && ids.Distinct().Count() == workers.Length && GitCommandLog.Snapshot().Count == 0,
                "Concurrent Clear/eviction violated capacity, duplicated ids or resurrected updates.");
        }
        finally { release.TrySetResult(); await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    private static async Task ObserversAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); int calls = 0, locked = 0;
        void Throwing() => throw new InvalidOperationException("journal-observer-exception-sentinel");
        void Safe()
        {
            Interlocked.Increment(ref calls);
            Task<IReadOnlyList<GitCommandLogEntry>> snapshot = Task.Run(GitCommandLog.Snapshot);
            if (!snapshot.Wait(TimeSpan.FromSeconds(2))) { Interlocked.Exchange(ref locked, 1); }
        }
        GitCommandLog.Changed += Throwing; GitCommandLog.Changed += Safe;
        try
        {
            GitResult result = await new GitRunner().RunAsync(fixture.Repository.Path, ["status", "--porcelain"]);
            Check(result.Success && calls == 3 && locked == 0, "A throwing observer suppressed another subscriber, held the log lock, or changed Git.");
            Check(!JsonSerializer.Serialize(GitCommandLog.Snapshot()).Contains("journal-observer-exception-sentinel", StringComparison.Ordinal), "Observer fault retained.");
        }
        finally { GitCommandLog.Changed -= Throwing; GitCommandLog.Changed -= Safe; }
        GitCommandLog.Clear(); int cleared = 0;
        void ClearAtStart()
        {
            if (GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId is null)) { Interlocked.Increment(ref cleared); GitCommandLog.Clear(); }
        }
        GitCommandLog.Changed += ClearAtStart;
        try
        {
            GitResult result = await new GitRunner().RunAsync(fixture.Repository.Path, ["status", "--porcelain"]);
            Check(result.Success && cleared == 1 && GitCommandLog.Snapshot().Count == 0, "Reentrant Clear at Begin resurrected the later PID/final or recursed on an empty log.");
        }
        finally { GitCommandLog.Changed -= ClearAtStart; }
    }

    private static async Task ExternalCallbacksAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); int changed = 0, started = 0, pid = 0;
        void Changed() => Interlocked.Increment(ref changed); GitCommandLog.Changed += Changed;
        try
        {
            ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/bin/sh", fixture.Root,
                ["-c", "printf '%s' \"$GIT_EDITOR\"; printf native-stderr >&2; exit 7"],
                environment: new Dictionary<string, string?> { ["GIT_EDITOR"] = "external-editor-sentinel" },
                processStarted: process => { pid = process.ProcessId; started++; });
            Check(result.ExitCode == 7 && started == 1 && pid > 0 && !LinuxProcessState.IsRunning(pid)
                && Encoding.UTF8.GetString(result.StandardOutputBytes) == "external-editor-sentinel"
                && Encoding.UTF8.GetString(result.StandardErrorBytes) == "native-stderr", "External callback, neutral environment, streams or wait changed.");
            InvalidOperationException original = new("external-started-fault-sentinel"); int failedPid = 0, failedCalls = 0;
            InvalidOperationException actual = await Expect<InvalidOperationException>(() => new ScriptProcessRunner().RunAsync("/bin/sh", fixture.Root,
                ["-c", "while :; do sleep 1; done"], processStarted: process => { failedPid = process.ProcessId; failedCalls++; throw original; }));
            Check(ReferenceEquals(actual, original) && failedCalls == 1 && failedPid > 0 && !LinuxProcessState.IsRunning(failedPid),
                "A thrown external started callback was swallowed/replaced or its child leaked.");
            int failedLaunchCalls = 0;
            await Expect<Win32Exception>(() => new ScriptProcessRunner().RunAsync(Path.Combine(fixture.Root, "missing-external"), fixture.Root, [], processStarted: _ => failedLaunchCalls++));
            Check(failedLaunchCalls == 0 && changed == 0 && GitCommandLog.Snapshot().Count == 0, "External work entered the Git journal or published a fake exec callback.");
        }
        finally { GitCommandLog.Changed -= Changed; }
    }

    private static async Task SecretsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear();
        const string argument = "journal-argument-secret", environment = "journal-environment-secret", input = "journal-stdin-secret",
            output = "journal-stdout-secret", error = "journal-stderr-secret", exception = "journal-exception-secret", executable = "journal-executable-secret", missing = "journal-missing-ref-secret";
        string wrapper = Path.Combine(fixture.Root, executable);
        WriteExecutable(wrapper, "#!/bin/sh\nIFS= read -r input\nprintf '%s\\n' \"$input\" \"$JOURNAL_PRIVATE_ENV\" " + Quote(output)
            + "\nprintf '%s\\n' " + Quote(error) + " >&2\nexec /usr/bin/env git \"$@\"\n");
        GitResult result = await new GitRunner(wrapper).RunAsync(fixture.Repository.Path, ["-c", "http.extraHeader=Authorization: " + argument, "status", "--porcelain"],
            standardInput: input + "\n", environment: new Dictionary<string, string?> { ["JOURNAL_PRIVATE_ENV"] = environment });
        Check(result.Success && result.StandardOutput.Contains(input, StringComparison.Ordinal) && result.StandardOutput.Contains(environment, StringComparison.Ordinal)
            && result.StandardOutput.Contains(output, StringComparison.Ordinal) && result.StandardError.Contains(error, StringComparison.Ordinal), "Actual secret channels did not reach the native operation.");
        InvalidOperationException original = new(exception);
        InvalidOperationException actual = await Expect<InvalidOperationException>(() => new GitRunner(wrapper).RunAsync(fixture.Repository.Path, ["status"],
            standardInput: input + "\n", outputReceived: _ => throw original));
        Check(ReferenceEquals(actual, original), "Stream observer exception semantics changed.");
        GitResult bad = await new GitRunner().RunAsync(fixture.Repository.Path, ["show", "--no-patch", "--format=%H", missing]); Check(!bad.Success && bad.StandardError.Contains(missing, StringComparison.Ordinal), "Actual native failure sentinel.");
        await Expect<Win32Exception>(() => new GitRunner(Path.Combine(fixture.Root, executable + "-missing")).RunAsync(fixture.Repository.Path, ["status", argument]));
        long unknown = Begin(argument, "status"); Check(GitCommandLog.Snapshot().Last().Id == unknown && GitCommandLog.Snapshot().Last().Command == "git", "Unknown command retained its first positional value."); Complete(unknown);
        long known = Begin("--config-env", "http.extraHeader=JOURNAL_PRIVATE_ENV", "-C", "/not-retained", "status");
        Check(GitCommandLog.Snapshot().Last().Command == "git status", "Global option values were misidentified as command metadata."); Complete(known);
        string json = JsonSerializer.Serialize(GitCommandLog.Snapshot()), text = string.Join("\n", GitCommandLog.Snapshot());
        foreach (string secret in new[] { argument, environment, input, output, error, exception, executable, missing, "extraHeader", "Authorization", "JOURNAL_PRIVATE_ENV" })
        { Check(!json.Contains(secret, StringComparison.Ordinal) && !text.Contains(secret, StringComparison.Ordinal), "A non-metadata channel was retained by the journal."); }
        Check(GitCommandLog.Snapshot().All(entry => entry.Command is "git" or "git status" or "git show"), "Unrecognized descriptor escaped the allowlist.");
    }

    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected native " + typeof(T).Name);
    }
    private static async Task Until(Func<bool> predicate)
    {
        Stopwatch clock = Stopwatch.StartNew(); while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); }
        Check(predicate(), "Private journal process fixture did not settle.");
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static void WriteExecutable(string path, string program)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private native command fixture requires Linux."); }
        File.WriteAllText(path, program); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private sealed class Gate : IDisposable
    {
        private readonly RepositoryFixture _fixture;
        private readonly string _wrapper, _marker, _release;
        internal int ProcessId { get; private set; }
        internal int ChildId { get; private set; }
        internal Gate(RepositoryFixture fixture, string name)
        {
            _fixture = fixture; _wrapper = Path.Combine(fixture.Root, "journal-" + name); _marker = _wrapper + ".pids"; _release = _wrapper + ".release";
            WriteExecutable(_wrapper, "#!/bin/sh\nsleep 60 &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + Quote(_marker + ".tmp")
                + "\nmv " + Quote(_marker + ".tmp") + " " + Quote(_marker) + "\nwhile test ! -f " + Quote(_release)
                + "; do sleep 0.02; done\nkill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\nexec /usr/bin/env git \"$@\"\n");
        }
        internal Task<GitResult> Run(CancellationToken token = default) => new GitRunner(_wrapper).RunAsync(_fixture.Repository.Path, ["status", "--porcelain"], token);
        internal async Task Ready()
        {
            await Until(() => File.Exists(_marker) && GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId > 0));
            int[] pids = File.ReadAllText(_marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            ProcessId = pids[0]; ChildId = pids[1];
            await Until(() => GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId == ProcessId));
            Check(LinuxProcessState.IsRunning(ProcessId) && LinuxProcessState.IsRunning(ChildId), "Gate did not witness its actual actors.");
        }
        internal void Release() => File.WriteAllText(_release, "release\n");
        public void Dispose() => Release();
    }
}
