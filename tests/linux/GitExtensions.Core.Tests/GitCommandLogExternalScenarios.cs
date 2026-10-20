using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class GitCommandLogExternalScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("external journal opt-in keeps neutral argv environment streams and one actual parent PID", Guard(NeutralAsync)),
        ("external journal admits exactly the closed origins and rejects an unknown origin before launch", Guard(OriginsAsync)),
        ("external journal captures caller stack and UI once before await and retains off-policy neutrality", Guard(ContextAsync)),
        ("external journal preserves launch order under real reverse completion", Guard(ReverseAsync)),
        ("external journal real completion cannot resurrect Clear or capacity eviction", Guard(ClearAndEvictionAsync)),
        ("external journal keeps real fast zero nonzero failed exec callback fault and pre-cancel outcomes", Guard(OutcomesAsync)),
        ("external journal owned cancellation drains its actual parent and descendant without invented exit", Guard(CancelAsync)),
        ("external journal default neutral launches remain excluded and do not even capture diagnostics", Guard(DefaultExcludedAsync)),
        ("external journal retains six positional fields and safely distinguishes an unobserved handoff", Guard(CompatibilityAsync))
    ];

    private static Func<Task> Guard(Func<Task> run) => async () =>
    {
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy;
        GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(new(false));
        try { await run(); }
        finally { GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(previous); }
    };
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private static GitCommandLogEntry Entry() => GitCommandLog.Snapshot().Single();
    private static async Task<T> Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task NeutralAsync()
    {
        await using Gate gate = new("neutral", exit: 7, child: true);
        ConcurrentQueue<GitCommandLogEntry> observations = new();
        void Changed() { foreach (GitCommandLogEntry entry in GitCommandLog.Snapshot()) { observations.Enqueue(entry); } }
        GitCommandLog.Changed += Changed;
        Task<ScriptProcessResult>? run = null;
        try
        {
            run = gate.Run(environment: new Dictionary<string, string?>
            {
                ["GIT_EDITOR"] = "neutral-editor-secret", ["GIT_TERMINAL_PROMPT"] = "neutral-prompt-secret",
                ["GIT_PAGER"] = "neutral-pager-secret", ["LC_ALL"] = "C", ["EXTERNAL_PRIVATE"] = "environment-secret"
            });
            await gate.ReadyAsync(); GitCommandLogEntry live = Entry();
            Check(live.Command == "script" && live.IsRunning && live.ProcessId == gate.Pid && live.ExitCode is null && !run.IsCompleted,
                "Opted-in external launch lacks its running real PID before release.");
            Check(observations.TryPeek(out GitCommandLogEntry? first) && first.ProcessId is null && first.IsRunning && first.Id == live.Id,
                "Admission must precede actual PID confirmation.");
            Check(gate.ChildPid > 0 && gate.ChildPid != gate.Pid && Alive(gate.ChildPid), "Require an actual private child, not a fabricated PID.");
            IReadOnlyList<GitCommandLogEntry> snapshot = GitCommandLog.Snapshot();
            gate.Release(); ScriptProcessResult result = await run.WaitAsync(TimeSpan.FromSeconds(20));
            GitCommandLogEntry done = Entry();
            Check(result.ExitCode == 7 && Encoding.UTF8.GetString(result.StandardErrorBytes) == "stderr-secret\n", "Native exit or error stream changed.");
            JsonElement values = JsonDocument.Parse(result.StandardOutputBytes).RootElement;
            Check(values.GetProperty("args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "argument-secret", "$(touch SHOULD_NOT_EXIST)", "", "été\nline" }),
                "Exact external argv was altered or sent through a shell.");
            Check(values.GetProperty("editor").GetString() == "neutral-editor-secret" && values.GetProperty("prompt").GetString() == "neutral-prompt-secret"
                && values.GetProperty("pager").GetString() == "neutral-pager-secret" && values.GetProperty("private").GetString() == "environment-secret",
                "Admission enabled Git environment/authentication policy on a neutral external command.");
            Check(!File.Exists(Path.Combine(gate.Root, "SHOULD_NOT_EXIST")), "Literal external argv was evaluated.");
            Check(done.Id == live.Id && done.ProcessId == gate.Pid && !done.IsRunning && done.ExitCode == 7 && !done.Cancelled && !done.IsUnobserved
                && done.Duration > TimeSpan.Zero && snapshot[0].IsRunning && snapshot[0].ExitCode is null,
                "External finalization or immutable snapshots differ.");
            Check(GitCommandLog.Snapshot().Count == 1 && !Alive(gate.Pid) && !Alive(gate.ChildPid), "Descendant was admitted or real wait/drain did not finish.");
            string json = JsonSerializer.Serialize(GitCommandLog.Snapshot());
            foreach (string secret in new[] { "argument-secret", "environment-secret", "stderr-secret", "neutral-editor-secret", "neutral-prompt-secret", "neutral-pager-secret", "EXTERNAL_PRIVATE", "user-script-name-secret", "SHOULD_NOT_EXIST" })
            { Check(!json.Contains(secret, StringComparison.Ordinal), "A non-metadata external channel entered the journal."); }
        }
        finally { GitCommandLog.Changed -= Changed; gate.Release(); if (run is not null) { await run.WaitAsync(TimeSpan.FromSeconds(20)); } }
    }

    private static async Task OriginsAsync()
    {
        (GitCommandLogProcessKind Kind, string Label)[] expected =
        [
            (GitCommandLogProcessKind.UserScript, "script"), (GitCommandLogProcessKind.Build, "build"),
            (GitCommandLogProcessKind.GitGui, "git gui"), (GitCommandLogProcessKind.Gitk, "gitk"),
            (GitCommandLogProcessKind.OpenResource, "open resource"), (GitCommandLogProcessKind.Browse, "browse"),
            (GitCommandLogProcessKind.TerminalLauncher, "terminal launcher"), (GitCommandLogProcessKind.InteractiveConsole, "interactive console host")
        ];
        Check(Enum.GetValues<GitCommandLogProcessKind>().ToHashSet().SetEquals(expected.Select(x => x.Kind)), "Unreviewed process origins entered the closed set.");
        foreach (var item in expected)
        {
            GitCommandLog.Clear();
            ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "exit 0"], journalKind: item.Kind);
            Check(result.ExitCode == 0 && Entry().Command == item.Label && Entry().ProcessId > 0 && Entry().ExitCode == 0 && !Entry().IsRunning,
                "Closed external origin lost its constant descriptor or actual completion.");
        }
        GitCommandLog.Clear(); int callbacks = 0;
        await Throws<ArgumentOutOfRangeException>(() => new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "exit 0"],
            processStarted: _ => callbacks++, journalKind: (GitCommandLogProcessKind)int.MaxValue));
        Check(callbacks == 0 && GitCommandLog.Snapshot().Count == 0, "An invalid origin started or published work.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GitCommandLogProcessContext CaptureAtCaller() => GitCommandLog.CaptureProcessContext();
    private static async Task ContextAsync()
    {
        int captures = 0; bool ui = true;
        GitCommandLog.ConfigureCapture(new(true, () => { captures++; return ui; }, typeof(GitCommandLogExternalScenarios).Assembly));
        GitCommandLogProcessContext context = CaptureAtCaller();
        Check(captures == 1, "Caller context was not captured synchronously.");
        ui = false;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ScriptProcessResult> run = Task.Run(async () =>
        {
            await gate.Task;
            return await new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "exit 0"],
                journalKind: GitCommandLogProcessKind.UserScript, journalContext: context);
        });
        gate.SetResult(); await run.WaitAsync(TimeSpan.FromSeconds(20));
        Check(captures == 1 && Entry().IsOnMainThread == true && Entry().CallStack?.Contains(nameof(CaptureAtCaller), StringComparison.Ordinal) == true,
            "External process recaptured stack/UI after await instead of using the captured caller context.");
        Check(Entry().CallStack!.Length <= GitCommandLogCapturePolicy.MaxCallStackLength, "External stack exceeded existing privacy bounds.");
        GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(new(false, () => false));
        await new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "exit 0"], journalKind: GitCommandLogProcessKind.UserScript);
        Check(Entry().CallStack is null && Entry().IsOnMainThread == false, "Off policy acquired a stack or lost observed UI metadata.");
    }

    private static async Task ReverseAsync()
    {
        await using Gate first = new("first"); await using Gate second = new("second");
        Task<ScriptProcessResult> a = first.Run(); Task<ScriptProcessResult>? b = null;
        try
        {
            await first.ReadyAsync(); b = second.Run(); await second.ReadyAsync();
            long[] order = GitCommandLog.Snapshot().Select(x => x.Id).ToArray();
            Check(order.Length == 2 && order[0] < order[1], "External launch order/identity was lost.");
            second.Release(); await b.WaitAsync(TimeSpan.FromSeconds(20));
            Check(GitCommandLog.Snapshot().Select(x => x.Id).SequenceEqual(order) && GitCommandLog.Snapshot()[0].IsRunning && !GitCommandLog.Snapshot()[1].IsRunning && !a.IsCompleted,
                "Reverse external completion reordered or prematurely finished the older process.");
            first.Release(); await a.WaitAsync(TimeSpan.FromSeconds(20));
            Check(GitCommandLog.Snapshot().Select(x => x.Id).SequenceEqual(order), "External finalization replaced launch order.");
        }
        finally { first.Release(); second.Release(); await a.WaitAsync(TimeSpan.FromSeconds(20)); if (b is not null) { await b.WaitAsync(TimeSpan.FromSeconds(20)); } }
    }

    private static async Task ClearAndEvictionAsync()
    {
        foreach (bool evict in new[] { false, true })
        {
            GitCommandLog.Clear(); await using Gate gate = new(evict ? "evict" : "clear"); Task<ScriptProcessResult> run = gate.Run();
            try
            {
                await gate.ReadyAsync(); long removed = Entry().Id;
                if (evict)
                {
                    for (int i = 0; i < GitCommandLog.Capacity; i++)
                    { GitCommandLog.StartProcess(GitCommandLogProcessKind.GitGui, gate.Root).Complete(0); }
                    Check(GitCommandLog.Snapshot().Count == GitCommandLog.Capacity && GitCommandLog.Snapshot().All(x => x.Id != removed), "External capacity eviction failed.");
                }
                else { GitCommandLog.Clear(); }
                long[] retained = GitCommandLog.Snapshot().Select(x => x.Id).ToArray();
                Check(!run.IsCompleted && Alive(gate.Pid), "Clear/eviction stopped the real process.");
                gate.Release(); await run.WaitAsync(TimeSpan.FromSeconds(20));
                Check(GitCommandLog.Snapshot().Select(x => x.Id).SequenceEqual(retained), "Late external completion resurrected Clear/eviction.");
            }
            finally { gate.Release(); await run.WaitAsync(TimeSpan.FromSeconds(20)); }
        }
    }

    private static async Task OutcomesAsync()
    {
        foreach (int code in new[] { 0, 7 })
        {
            ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "exit " + code], journalKind: GitCommandLogProcessKind.UserScript);
            Check(result.ExitCode == code && GitCommandLog.Snapshot().Last() is { ProcessId: > 0, IsRunning: false, IsUnobserved: false } entry && entry.ExitCode == code,
                "Fast real external exit was invented or lost.");
        }
        int missingCalls = 0;
        await Throws<Win32Exception>(() => new ScriptProcessRunner().RunAsync("/not-existing-private-external-" + Guid.NewGuid().ToString("N"), Path.GetTempPath(), [],
            processStarted: _ => missingCalls++, journalKind: GitCommandLogProcessKind.UserScript));
        Check(missingCalls == 0 && GitCommandLog.Snapshot().Last() is { IsRunning: false, ProcessId: null, ExitCode: null, IsUnobserved: false }, "Failed exec was reported as observed success or handoff.");
        await using Gate gate = new("callback-fault"); int failedPid = 0; InvalidOperationException expected = new("callback-exception-secret");
        InvalidOperationException actual = await Throws<InvalidOperationException>(() => gate.Run(started: process => { failedPid = process.ProcessId; throw expected; }));
        Check(ReferenceEquals(expected, actual) && failedPid > 0 && !Alive(failedPid) && GitCommandLog.Snapshot().Last() is { IsRunning: false, ExitCode: null, IsUnobserved: false },
            "External started callback fault changed identity/exception or returned before native drain.");
        using CancellationTokenSource cancellation = new(); cancellation.Cancel(); int count = GitCommandLog.Snapshot().Count;
        await Throws<OperationCanceledException>(() => new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "exit 0"], cancellation.Token,
            journalKind: GitCommandLogProcessKind.UserScript));
        Check(GitCommandLog.Snapshot().Count == count, "Pre-cancelled external work admitted a fake launch.");
        int captures = 0; GitCommandLog.ConfigureCapture(new(false, () => { captures++; return false; }));
        await Throws<ArgumentException>(() => new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["bad\0argument"], journalKind: GitCommandLogProcessKind.UserScript));
        await Throws<OperationCanceledException>(() => new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), [], cancellation.Token, journalKind: GitCommandLogProcessKind.UserScript));
        Check(captures == 0 && GitCommandLog.Snapshot().Count == count, "Validation/pre-cancellation invoked capture or admitted work before a valid launch.");
        await using Gate outputGate = new("output-fault");
        InvalidOperationException outputError = new("external-output-callback-secret");
        Task<ScriptProcessResult> outputRun = outputGate.Run(output: _ => throw outputError);
        try
        {
            await outputGate.ReadyAsync(); outputGate.Release();
            InvalidOperationException outputActual = await Throws<InvalidOperationException>(() => outputRun.WaitAsync(TimeSpan.FromSeconds(20)));
            Check(ReferenceEquals(outputActual, outputError) && !Alive(outputGate.Pid) && !GitCommandLog.Snapshot().Last().IsRunning,
                "Opted-in external output callback fault changed exception or returned before drain.");
        }
        finally { outputGate.Release(); try { await outputRun.WaitAsync(TimeSpan.FromSeconds(20)); } catch (InvalidOperationException error) when (ReferenceEquals(error, outputError)) { } }
        Check(!JsonSerializer.Serialize(GitCommandLog.Snapshot()).Contains("callback-exception-secret", StringComparison.Ordinal) && !JsonSerializer.Serialize(GitCommandLog.Snapshot()).Contains("external-output-callback-secret", StringComparison.Ordinal), "Callback fault text entered metadata.");
    }

    private static async Task CancelAsync()
    {
        await using Gate gate = new("cancel", child: true); using CancellationTokenSource cancellation = new();
        Task<ScriptProcessResult> run = gate.Run(cancellation.Token);
        try
        {
            await gate.ReadyAsync(); long id = Entry().Id; cancellation.Cancel();
            await Throws<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
            Check(Entry().Id == id && Entry().ProcessId == gate.Pid && Entry().Cancelled && !Entry().IsRunning && Entry().ExitCode is null && !Entry().IsUnobserved,
                "Owned external cancellation invented an exit or changed identity.");
            Check(!Alive(gate.Pid) && !Alive(gate.ChildPid), "External cancellation returned before actual parent/child stopped.");
        }
        finally { cancellation.Cancel(); gate.Release(); try { await run.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { } }
    }

    private static async Task DefaultExcludedAsync()
    {
        int probes = 0; GitCommandLog.ConfigureCapture(new(true, () => { probes++; return true; }, typeof(GitCommandLogExternalScenarios).Assembly));
        int started = 0;
        ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/bin/sh", Path.GetTempPath(), ["-c", "printf default-output; exit 7"], processStarted: _ => started++);
        Check(result.ExitCode == 7 && started == 1 && Encoding.UTF8.GetString(result.StandardOutputBytes) == "default-output"
            && probes == 0 && GitCommandLog.Snapshot().Count == 0, "A generic/probe external launch was implicitly admitted or captured.");
    }

    private static Task CompatibilityAsync()
    {
        DateTimeOffset started = DateTimeOffset.UnixEpoch;
        GitCommandLogEntry legacy = new("git status", "/metadata-directory", started, TimeSpan.FromSeconds(1), 7, false);
        var (command, directory, date, duration, exit, cancelled) = legacy;
        Check(command == "git status" && directory == legacy.Directory && date == started && duration == legacy.Duration && exit == 7 && !cancelled && !legacy.IsUnobserved,
            "The six-field constructor/deconstruction/default changed.");
        GitCommandLogProcessOperation operation = GitCommandLog.StartProcess(GitCommandLogProcessKind.OpenResource, "/metadata-directory");
        long id = Entry().Id; operation.CompleteUnobserved(); operation.SetProcessId(123); operation.Complete(0);
        Check(Entry().Id == id && Entry().IsUnobserved && !Entry().IsRunning && Entry().ProcessId is null && Entry().ExitCode is null && !Entry().Cancelled,
            "Unobserved handoff invented PID/exit or accepted late finalization.");
        return Task.CompletedTask;
    }

    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(10); }
        Check(condition(), "Private external process did not reach its bounded witness.");
    }
    private static bool Alive(int pid)
    {
        if (pid <= 0) { return false; }
        try { string[] stat = File.ReadAllText($"/proc/{pid}/stat").Split(')')[^1].Trim().Split(' '); return stat[0] != "Z"; }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    private sealed class Gate : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "external-journal-" + Guid.NewGuid().ToString("N"));
        internal string Executable => Path.Combine(Root, "user-script-name-secret");
        private string Marker => Path.Combine(Root, "pids.json");
        private string ReleaseFile => Path.Combine(Root, "release");
        internal int Pid { get; private set; }
        internal int ChildPid { get; private set; }
        internal Gate(string label, int exit = 0, bool child = false)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private external journal fixtures require Linux."); }
            Directory.CreateDirectory(Root);
            File.WriteAllText(Executable, """
                #!/usr/bin/python3
                import json, os, pathlib, subprocess, sys, time
                root = pathlib.Path(__file__).parent
                child = subprocess.Popen(['/bin/sleep','60']) if CHILD else None
                record = root / 'pids.json'
                (root / 'pids.tmp').write_text(json.dumps({'pid': os.getpid(), 'child': child.pid if child else 0}))
                (root / 'pids.tmp').replace(record)
                while not (root / 'release').exists(): time.sleep(0.01)
                if child:
                    child.terminate(); child.wait()
                print(json.dumps({'args':sys.argv[1:], 'editor':os.getenv('GIT_EDITOR'), 'prompt':os.getenv('GIT_TERMINAL_PROMPT'), 'pager':os.getenv('GIT_PAGER'), 'private':os.getenv('EXTERNAL_PRIVATE')}))
                print('stderr-secret', file=sys.stderr)
                sys.exit(EXIT)
                """.Replace("CHILD", child ? "True" : "False", StringComparison.Ordinal).Replace("EXIT", exit.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal Task<ScriptProcessResult> Run(CancellationToken token = default, IReadOnlyDictionary<string, string?>? environment = null, Action<ScriptProcessStarted>? started = null, Action<GitProcessOutput>? output = null)
            => new ScriptProcessRunner().RunAsync(Executable, Root, ["argument-secret", "$(touch SHOULD_NOT_EXIST)", "", "été\nline"], token,
                environment: environment, processStarted: started, outputReceived: output, journalKind: GitCommandLogProcessKind.UserScript);
        internal async Task ReadyAsync()
        {
            await Until(() => File.Exists(Marker));
            using JsonDocument values = JsonDocument.Parse(File.ReadAllBytes(Marker)); Pid = values.RootElement.GetProperty("pid").GetInt32(); ChildPid = values.RootElement.GetProperty("child").GetInt32();
            await Until(() => GitCommandLog.Snapshot().Any(entry => entry.ProcessId == Pid));
            Check(Alive(Pid), "Require an actual running process at the gate.");
        }
        internal void Release() { if (Directory.Exists(Root)) { File.WriteAllText(ReleaseFile, "release"); } }
        public async ValueTask DisposeAsync()
        {
            Release();
            if (Pid > 0) { await Until(() => !Alive(Pid)); }
            Directory.Delete(Root, true);
        }
    }
}
