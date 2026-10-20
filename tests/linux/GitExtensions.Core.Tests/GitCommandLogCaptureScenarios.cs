using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class GitCommandLogCaptureScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("journal capture policy defaults preserve six positional fields and independent unknown UI metadata", Guard(PolicyAsync)),
        ("journal symbol projection excludes unapproved dynamic source and closed generic payloads", Guard(SymbolPrivacyAsync)),
        ("journal stack projection enforces frame character and 500 entry retention bounds", Guard(BoundsAsync)),
        ("journal capture isolates throwing UI probes and projection faults without retaining their text", Guard(FaultsAsync)),
        ("journal native UI probe remains independent of disabled stack capture", Guard(UiWithoutStackAsync)),
        ("journal capture reads one immutable policy across reentrant and concurrent publication", Guard(PolicyPublicationAsync)),
        ("journal caller and UI are frozen before a real askpass await while capture changes from on to off", Guard(() => AskpassToggleAsync(true))),
        ("journal caller and UI are frozen before a real askpass await while capture changes from off to on", Guard(() => AskpassToggleAsync(false))),
        ("journal real running PID and completion preserve the original captured metadata", Guard(LifecycleAsync)),
        ("journal cancellation and Clear never restore captured metadata for removed real operations", Guard(ClearAndCancelAsync)),
        ("journal capture keeps the original all channel privacy sentinels under real Git execution", Guard(OriginalSecretsWithCaptureAsync)),
        ("journal external callbacks and rejected calls never invoke the capture policy", Guard(ExternalAndRejectedAsync))
    ];

    private static readonly Assembly Host = typeof(GitCommandLogCaptureScenarios).Assembly;
    private static int _depthWitness;
    private static Func<Task> Guard(Func<Task> action) => async () =>
    {
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy;
        string? fallback = GitRunner.AskpassFallbackPath;
        Dictionary<string, string?> environment = new[] { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_OPTIONAL_LOCKS", "GIT_ASKPASS", "SSH_ASKPASS", "DISPLAY", "WAYLAND_DISPLAY" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        GitCommandLog.ConfigureCapture(new(false)); GitRunner.AskpassFallbackPath = null;
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null"); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        foreach (string name in new[] { "GIT_ASKPASS", "SSH_ASKPASS", "DISPLAY", "WAYLAND_DISPLAY" }) { Environment.SetEnvironmentVariable(name, null); }
        GitCommandLog.Clear();
        try { await action(); }
        finally
        {
            GitCommandLog.ConfigureCapture(previous); GitCommandLog.Clear(); GitRunner.AskpassFallbackPath = fallback;
            foreach ((string name, string? value) in environment) { Environment.SetEnvironmentVariable(name, value); }
        }
    };
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

    private static Task PolicyAsync()
    {
        GitCommandLogCapturePolicy initial = GitCommandLog.CapturePolicy;
        Check(!initial.CaptureCallStacks && initial.UiThreadProbe is null && initial.ApprovedHostAssembly is null, "The capture default was not opt-in/unknown.");
        GitCommandLogEntry six = new("git status", "/metadata", DateTimeOffset.UnixEpoch, TimeSpan.Zero, null, false);
        var (command, directory, started, duration, exit, cancelled) = six;
        Check(command == six.Command && directory == six.Directory && started == six.Started && duration == six.Duration
            && exit is null && !cancelled && six.CallStack is null && six.IsOnMainThread is null, "The six-position contract changed.");
        ExpectSync<ArgumentNullException>(() => GitCommandLog.ConfigureCapture(null!));
        Check(ReferenceEquals(initial, GitCommandLog.CapturePolicy), "Rejected publication changed the policy.");
        int probes = 0; Func<bool?> probe = () => { probes++; return true; };
        GitCommandLogCapturePolicy disabled = new(false, probe, Host); GitCommandLog.ConfigureCapture(disabled);
        Check(ReferenceEquals(disabled, GitCommandLog.CapturePolicy) && ReferenceEquals(disabled.UiThreadProbe, probe)
            && ReferenceEquals(disabled.ApprovedHostAssembly, Host), "The immutable host policy was not retained.");
        GitCommandLogEntry entry = Admit(Capture());
        Check(probes == 1 && entry.IsOnMainThread == true && entry.CallStack is null, "Disabled stacks must still observe the caller probe.");
        GitCommandLog.ConfigureCapture(new(false));
        Check(Admit(Capture()).IsOnMainThread is null, "No host probe must remain unknown, not false.");
        foreach (PropertyInfo property in typeof(GitCommandLogCapturePolicy).GetProperties())
        { Check(property.SetMethod is null, "A policy property can mutate an in-flight policy."); }
        return Task.CompletedTask;
    }

    private static Task SymbolPrivacyAsync()
    {
        const string sourceSecret = "/source/identity-private/source-location-secret.cs";
        MethodInfo ordinary = typeof(GitCommandLogCaptureScenarios).GetMethod(nameof(VisibleSymbol), BindingFlags.Static | BindingFlags.NonPublic)!;
        StackFrame sourceFrame = new SymbolFrame(ordinary, sourceSecret);
        Check(sourceFrame.GetFileName() == sourceSecret, "The positive file-location witness was absent.");
        StackTrace real = RealSourceTrace();
        Check(real.GetFrames().Any(frame => !string.IsNullOrEmpty(frame.GetFileName())), "The compiled source/PDB witness was absent; do not waive the privacy oracle.");
        Check(Project(new(true), new SuppliedTrace(sourceFrame)) is null, "An unapproved host frame escaped by namespace/name.");
        GitCommandLogCapturePolicy approved = new(true, approvedHostAssembly: Host);
        string text = Project(approved, new SuppliedTrace(sourceFrame))!;
        Check(text.Contains(nameof(VisibleSymbol), StringComparison.Ordinal) && !text.Contains(sourceSecret, StringComparison.Ordinal)
            && !text.Contains("source-location-secret", StringComparison.Ordinal), "Source location reached the symbol projection.");
        string realText = Project(approved, real)!;
        foreach (string file in real.GetFrames().Select(frame => frame.GetFileName()).Where(file => !string.IsNullOrEmpty(file)).Cast<string>())
        { Check(!realText.Contains(file, StringComparison.Ordinal), "A real source/PDB path was retained."); }
        Check(!realText.Contains(".cs:", StringComparison.Ordinal) && !realText.Contains(" in /", StringComparison.Ordinal), "Raw StackTrace source formatting was retained.");
        MethodInfo closed = typeof(GenericSymbol<ClosedGenericPayloadMustNeverAppear>).GetMethod(nameof(GenericSymbol<int>.VisibleMethod))!
            .MakeGenericMethod(typeof(ClosedGenericMethodArgumentMustNeverAppear));
        string generic = Project(approved, new SuppliedTrace(new SymbolFrame(closed, sourceSecret)))!;
        Check(generic.Contains("GenericSymbol`1", StringComparison.Ordinal) && generic.Contains("VisibleMethod", StringComparison.Ordinal)
            && !generic.Contains(nameof(ClosedGenericPayloadMustNeverAppear), StringComparison.Ordinal)
            && !generic.Contains(nameof(ClosedGenericMethodArgumentMustNeverAppear), StringComparison.Ordinal), "Closed generic runtime identities escaped.");
        MethodInfo system = typeof(string).GetMethod(nameof(string.ToString), Type.EmptyTypes)!;
        Check(Project(approved, new SuppliedTrace(new SymbolFrame(system, sourceSecret))) is null, "An unapproved real framework assembly was projected.");
        AssemblyBuilder dynamic = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(typeof(GitCommandLog).Assembly.GetName().Name!), AssemblyBuilderAccess.Run);
        ModuleBuilder module = dynamic.DefineDynamicModule("metadata-secret-module");
        TypeBuilder type = module.DefineType("GitExtensions.Linux.DynamicTypeWithPrivatePayload", TypeAttributes.Public);
        MethodBuilder method = type.DefineMethod("RuntimeNameWithPrivatePayload", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret); Type dynamicType = type.CreateType()!;
        Check(Project(approved, new SuppliedTrace(new SymbolFrame(dynamicType.GetMethod(method.Name)!, sourceSecret))) is null,
            "A dynamic assembly spoofing the real Core name was trusted.");
        ExpectSync<ArgumentException>(() => _ = new GitCommandLogCapturePolicy(true, approvedHostAssembly: dynamic));
        return Task.CompletedTask;
    }

    private static Task BoundsAsync()
    {
        GitCommandLogCapturePolicy policy = new(true, () => true, Host);
        MethodInfo ordinary = typeof(GitCommandLogCaptureScenarios).GetMethod(nameof(VisibleSymbol), BindingFlags.Static | BindingFlags.NonPublic)!;
        string frames = Project(policy, new SuppliedTrace(Enumerable.Repeat(new SymbolFrame(ordinary, "/private-source"), 100).ToArray()))!;
        Check(frames.Split('\n').Count(line => line.StartsWith("at ", StringComparison.Ordinal)) == GitCommandLogCapturePolicy.MaxFrames
            && frames.EndsWith("[truncated]", StringComparison.Ordinal), "Frame count was not strictly bounded.");
        MethodInfo longSymbol = typeof(LongTrustedTypeNameThatMakesTheQualifiedSymbolExceedTheAllowedCharacterBudgetWithoutUsingRuntimeInputOrSecrets
            .AnotherLongTrustedNestedTypeNameThatMakesTheQualifiedSymbolExceedTheAllowedCharacterBudgetWithoutUsingRuntimeInputOrSecrets)
            .GetMethod("VeryLongMethodNameThatStillMustRemainWithinThePerFrameBudgetAndNeverExtendTheRetainedStackBeyondItsExplicitOverallCharacterBudget")!;
        string bounded = Project(policy, new SuppliedTrace(Enumerable.Repeat(new SymbolFrame(longSymbol, "/source-private-sentinel"), 100).ToArray()))!;
        Check(bounded.Length <= GitCommandLogCapturePolicy.MaxCallStackLength && bounded.EndsWith("[truncated]", StringComparison.Ordinal), "Total stack budget was exceeded.");
        Check(bounded.Split('\n').Where(line => line != "[truncated]").All(line => line.Length <= GitCommandLogCapturePolicy.MaxFrameLength), "A single frame exceeded its budget.");
        Check(bounded.Split('\n').Count(line => line.StartsWith("at ", StringComparison.Ordinal)) < GitCommandLogCapturePolicy.MaxFrames,
            "Long names did not exercise the overall character budget independently of frame count.");
        GitCommandLog.ConfigureCapture(policy);
        object context = DeepCaller(80); GitCommandLogEntry first = Admit(context); long evicted = first.Id;
        Check(first.CallStack is { Length: > 0 } && first.CallStack.Length <= GitCommandLogCapturePolicy.MaxCallStackLength
            && first.CallStack.EndsWith("[truncated]", StringComparison.Ordinal), "Real recursive capture did not obey the frame limit.");
        for (int index = 0; index < 700; index++) { Admit(context); }
        IReadOnlyList<GitCommandLogEntry> retained = GitCommandLog.Snapshot();
        Check(retained.Count == 500 && retained.All(entry => entry.CallStack!.Length <= GitCommandLogCapturePolicy.MaxCallStackLength)
            && retained.Sum(entry => (long)entry.CallStack!.Length) <= 500L * GitCommandLogCapturePolicy.MaxCallStackLength, "The retained stack contribution exceeded its bound.");
        Pid(evicted, 123); Complete(evicted); Check(GitCommandLog.Snapshot().All(entry => entry.Id != evicted), "Eviction restored an old captured entry.");
        GitCommandLog.Clear(); foreach (GitCommandLogEntry entry in retained) { Pid(entry.Id, 456); Complete(entry.Id); }
        Check(GitCommandLog.Snapshot().Count == 0, "Late capture metadata resurrected after Clear.");
        return Task.CompletedTask;
    }

    private static async Task FaultsAsync()
    {
        const string fault = "capture-probe-exception-private-payload";
        Check(Project(new(true, approvedHostAssembly: Host), new ThrowingTrace(fault)) is null, "Projection exceptions were exposed instead of isolated.");
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear();
        GitCommandLog.ConfigureCapture(new(true, () => throw new InvalidOperationException(fault), Host));
        GitResult result = await new GitRunner().RunAsync(fixture.Repository.Path, ["--version"]);
        GitCommandLogEntry entry = GitCommandLog.Snapshot().Single();
        Check(result.Success && entry.IsOnMainThread is null && entry.CallStack is { Length: > 0 }, "A failed probe broke Git or prevented independent stack capture.");
        Check(!JsonSerializer.Serialize(entry).Contains(fault, StringComparison.Ordinal) && !entry.ToString().Contains(fault, StringComparison.Ordinal), "A diagnostic fault message was retained.");
    }

    private static async Task UiWithoutStackAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear();
        foreach (bool? value in new bool?[] { true, false, null })
        {
            int calls = 0; GitCommandLog.ConfigureCapture(new(false, () => { calls++; return value; }, Host));
            Check((await new GitRunner().RunAsync(fixture.Repository.Path, ["--version"])).Success, "Actual Git information request failed.");
            GitCommandLogEntry entry = GitCommandLog.Snapshot().Last();
            Check(calls == 1 && entry.IsOnMainThread == value && entry.CallStack is null, "Disabled projection lost or invented UI metadata.");
        }
        GitCommandLog.ConfigureCapture(new(true));
        Check((await new GitRunner().RunAsync(fixture.Repository.Path, ["--version"])).Success, "Actual Core-only capture failed.");
        GitCommandLogEntry core = GitCommandLog.Snapshot().Last();
        Check(core.IsOnMainThread is null && core.CallStack is { Length: > 0 } && core.CallStack.Contains("GitRunner", StringComparison.Ordinal)
            && !core.CallStack.Contains(nameof(GitCommandLogCaptureScenarios), StringComparison.Ordinal), "The Core did not filter the unapproved calling host.");
    }

    private static async Task PolicyPublicationAsync()
    {
        GitCommandLogCapturePolicy off = new(false, () => false, Host);
        GitCommandLogCapturePolicy on = new(true, () => true, Host);
        GitCommandLog.ConfigureCapture(new(true, () => { GitCommandLog.ConfigureCapture(off); return true; }, Host));
        GitCommandLogEntry original = Admit(Capture());
        Check(original.IsOnMainThread == true && original.CallStack is { Length: > 0 } && ReferenceEquals(GitCommandLog.CapturePolicy, off),
            "A reentrant toggle tore the UI and stack choices of the current capture.");
        GitCommandLogEntry next = Admit(Capture()); Check(next.IsOnMainThread == false && next.CallStack is null, "A later capture missed the published policy.");
        using ManualResetEventSlim start = new(); ConcurrentBag<object> captures = [];
        Task first = Task.Run(() => { start.Wait(); for (int index = 0; index < 4096; index++) { GitCommandLog.ConfigureCapture(on); } });
        Task second = Task.Run(() => { start.Wait(); for (int index = 0; index < 4096; index++) { GitCommandLog.ConfigureCapture(off); } });
        Task reader = Task.Run(() => { start.Wait(); for (int index = 0; index < 128; index++) { captures.Add(Capture()); } });
        start.Set(); await Task.WhenAll(first, second, reader).WaitAsync(TimeSpan.FromSeconds(15));
        foreach (object context in captures)
        {
            GitCommandLogEntry entry = Admit(context);
            Check(entry.IsOnMainThread is not null && (entry.CallStack is not null) == entry.IsOnMainThread,
                "Concurrent publication mixed fields from different policy instances.");
        }
    }

    private static async Task AskpassToggleAsync(bool initiallyEnabled)
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear();
        using NativeGate gate = new(fixture, probe: true); GitRunner.AskpassFallbackPath = gate.Helper;
        TaskCompletionSource<Task<GitResult>> returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread origin = new(() =>
        {
            try
            {
                int thread = Environment.CurrentManagedThreadId;
                GitCommandLog.ConfigureCapture(new(initiallyEnabled, () => Environment.CurrentManagedThreadId == thread, Host));
                returned.TrySetResult(LaunchCallerBeforeFirstAwait(gate));
            }
            catch (Exception error) { returned.TrySetException(error); }
        });
        Task<GitResult>? operation = null;
        try
        {
            origin.Start(); operation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(origin.Join(TimeSpan.FromSeconds(5)), "The call did not return its Task while the real probe was waiting.");
            await gate.Ready(); Check(!operation.IsCompleted, "Askpass did not cross a real asynchronous boundary.");
            GitCommandLogEntry probe = GitCommandLog.Snapshot().Single();
            Check(probe.Command == "git config" && probe.IsRunning && probe.IsOnMainThread == true
                && (probe.CallStack is not null) == initiallyEnabled, "The real config probe did not carry its own launch context.");
            GitCommandLog.ConfigureCapture(new(!initiallyEnabled, () => false, Host));
            gate.Release(); Check((await operation.WaitAsync(TimeSpan.FromSeconds(15))).Success, "The real main Git command failed.");
            GitCommandLogEntry first = GitCommandLog.Snapshot().Single(entry => entry.Command == "git status");
            Check(first.IsOnMainThread == true && (first.CallStack is not null) == initiallyEnabled,
                "The main command recaptured the later policy or worker instead of its synchronous callpoint.");
            if (initiallyEnabled)
            { Check(first.CallStack!.Contains(nameof(LaunchCallerBeforeFirstAwait), StringComparison.Ordinal), "The original caller vanished across the askpass await."); }
            Check(!LinuxProcessState.IsRunning(gate.ProcessId), "The real probe process survived the completed operation.");
            Check((await gate.Run()).Success, "The next operation failed after the toggle.");
            GitCommandLogEntry next = GitCommandLog.Snapshot().Last(entry => entry.Command == "git status");
            Check(next.Id > first.Id && next.IsOnMainThread == false && (next.CallStack is not null) != initiallyEnabled,
                "The next launch did not observe the newly published policy.");
            Check(!File.Exists(gate.HelperCalled), "A local status/config probe unexpectedly invoked authentication.");
        }
        finally
        {
            gate.Release(); if (operation is not null) { await operation.WaitAsync(TimeSpan.FromSeconds(15)); }
            if (origin.IsAlive) { Check(origin.Join(TimeSpan.FromSeconds(5)), "The managed caller remained active."); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<GitResult> LaunchCallerBeforeFirstAwait(NativeGate gate) => gate.Run();

    private static async Task LifecycleAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); using NativeGate gate = new(fixture, probe: false);
        GitCommandLog.ConfigureCapture(new(true, () => true, Host)); ConcurrentQueue<GitCommandLogEntry> observations = [];
        void Changed() { foreach (GitCommandLogEntry entry in GitCommandLog.Snapshot()) { observations.Enqueue(entry); } }
        GitCommandLog.Changed += Changed; Task<GitResult>? operation = null;
        try
        {
            operation = gate.Run(); await gate.Ready(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
            GitCommandLogEntry initial = observations.First(); IReadOnlyList<GitCommandLogEntry> before = GitCommandLog.Snapshot();
            Check(initial.IsRunning && initial.ProcessId is null && initial.CallStack is { Length: > 0 } && initial.IsOnMainThread == true,
                "Start did not carry caller metadata before PID.");
            Check(live.ProcessId == gate.ProcessId && ReferenceEquals(initial.CallStack, live.CallStack), "PID replaced or recaptured the stack.");
            GitCommandLog.ConfigureCapture(new(false, () => false, Host)); gate.Release();
            Check((await operation.WaitAsync(TimeSpan.FromSeconds(15))).Success, "Real completion failed.");
            GitCommandLogEntry complete = GitCommandLog.Snapshot().Single();
            Check(complete.Id == live.Id && complete.ProcessId == live.ProcessId && complete.IsOnMainThread == true
                && ReferenceEquals(complete.CallStack, live.CallStack) && !complete.IsRunning && complete.ExitCode == 0,
                "Completion changed frozen metadata after the global toggle.");
            Check(before[0].IsRunning && before[0].ExitCode is null && !LinuxProcessState.IsRunning(gate.ProcessId), "A retained snapshot mutated or the real actor was not drained.");
        }
        finally { GitCommandLog.Changed -= Changed; gate.Release(); if (operation is not null) { await operation.WaitAsync(TimeSpan.FromSeconds(15)); } }
    }

    private static async Task ClearAndCancelAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        foreach (bool clear in new[] { false, true })
        {
            GitCommandLog.Clear(); using NativeGate gate = new(fixture, probe: false); using CancellationTokenSource cancellation = new();
            GitCommandLog.ConfigureCapture(new(true, () => false, Host)); Task<GitResult>? operation = null;
            try
            {
                operation = gate.Run(cancellation.Token); await gate.Ready(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
                Check(live.CallStack is { Length: > 0 } && live.IsOnMainThread == false, "The cancellation fixture had no captured context.");
                if (clear) { GitCommandLog.Clear(); }
                cancellation.Cancel(); await Expect<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
                Check(!LinuxProcessState.IsRunning(gate.ProcessId), "The cancelled actual process was not retired.");
                if (clear) { Check(GitCommandLog.Snapshot().Count == 0, "A cancelled captured entry resurrected after Clear."); }
                else
                {
                    GitCommandLogEntry end = GitCommandLog.Snapshot().Single();
                    Check(end.Cancelled && !end.IsRunning && end.ExitCode is null && end.Id == live.Id
                        && end.IsOnMainThread == live.IsOnMainThread && ReferenceEquals(end.CallStack, live.CallStack), "Cancellation lost immutable capture metadata or invented exit status.");
                }
            }
            finally
            {
                gate.Release(); if (operation is not null) { try { await operation.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { } }
            }
        }
    }

    private static async Task OriginalSecretsWithCaptureAsync()
    {
        ConcurrentQueue<GitCommandLogEntry> observed = []; GitCommandLog.ConfigureCapture(new(true, approvedHostAssembly: Host));
        void Changed() { foreach (GitCommandLogEntry entry in GitCommandLog.Snapshot()) { observed.Enqueue(entry); } }
        GitCommandLog.Changed += Changed;
        try
        {
            // Execute the unchanged original real-channel scenario with capture enabled.
            await GitCommandLogScenarios.All.Single(test => test.Name == "command journal keeps all argument environment input output exception and executable sentinels out").Run();
            Check(observed.Any(entry => entry.CallStack is { Length: > 0 }), "The privacy scenario did not actually run with capture enabled.");
            string text = JsonSerializer.Serialize(observed.ToArray()) + string.Join("\n", observed);
            foreach (string secret in new[] { "journal-argument-secret", "journal-environment-secret", "journal-stdin-secret", "journal-stdout-secret", "journal-stderr-secret",
                "journal-exception-secret", "journal-executable-secret", "journal-missing-ref-secret", "extraHeader", "Authorization", "JOURNAL_PRIVATE_ENV", "GitCommandLogCaptureScenarios.cs" })
            { Check(!text.Contains(secret, StringComparison.Ordinal), "The opt-in stack retained a forbidden channel or source path."); }
        }
        finally { GitCommandLog.Changed -= Changed; }
    }

    private static async Task ExternalAndRejectedAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitCommandLog.Clear(); int probes = 0, changed = 0;
        GitCommandLog.ConfigureCapture(new(true, () => { probes++; throw new InvalidOperationException("external-probe-must-not-run"); }, Host));
        void Changed() => changed++;
        GitCommandLog.Changed += Changed;
        try
        {
            using CancellationTokenSource before = new(); before.Cancel();
            await Expect<OperationCanceledException>(() => new GitRunner().RunAsync(fixture.Repository.Path, ["--version"], before.Token));
            await Expect<ArgumentException>(() => new GitRunner().RunAsync(fixture.Repository.Path, ["status", "bad\0argument"]));
            int callback = 0, pid = 0;
            ScriptProcessResult result = await new ScriptProcessRunner().RunAsync("/bin/sh", fixture.Root, ["-c", "printf 'literal external output'; exit 7"],
                processStarted: process => { callback++; pid = process.ProcessId; });
            Check(result.ExitCode == 7 && callback == 1 && pid > 0 && !LinuxProcessState.IsRunning(pid), "External wait/callback semantics changed.");
            InvalidOperationException original = new("external-callback-private-fault"); int failingPid = 0;
            InvalidOperationException actual = await Expect<InvalidOperationException>(() => new ScriptProcessRunner().RunAsync("/bin/sh", fixture.Root,
                ["-c", "while :; do sleep 1; done"], processStarted: process => { failingPid = process.ProcessId; throw original; }));
            Check(ReferenceEquals(actual, original) && failingPid > 0 && !LinuxProcessState.IsRunning(failingPid), "External callback fault was replaced or not drained.");
            Check(probes == 0 && changed == 0 && GitCommandLog.Snapshot().Count == 0, "External or rejected work entered the capture/journal policy.");
        }
        finally { GitCommandLog.Changed -= Changed; }
    }

    private static object Capture() => typeof(GitCommandLog).GetMethod("CaptureLaunchContext", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
    private static GitCommandLogEntry Admit(object context)
    {
        long id = (long)typeof(GitCommandLog).GetMethod("BeginWithContext", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { new[] { "status" }, "/allowed-directory", DateTimeOffset.UnixEpoch, context })!;
        return GitCommandLog.Snapshot().Single(entry => entry.Id == id);
    }
    private static string? Project(GitCommandLogCapturePolicy policy, StackTrace trace)
        => (string?)typeof(GitCommandLogCapturePolicy).GetMethod("Project", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(policy, new object[] { trace });
    private static void Pid(long id, int pid) => typeof(GitCommandLog).GetMethod("SetProcessId", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { id, pid });
    private static void Complete(long id) => typeof(GitCommandLog).GetMethod("Complete", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object?[] { id, TimeSpan.Zero, 0, false });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object DeepCaller(int remaining)
    {
        object value = remaining == 0 ? Capture() : DeepCaller(remaining - 1);
        Interlocked.Add(ref _depthWitness, remaining); return value;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StackTrace RealSourceTrace() => new(fNeedFileInfo: true);
    private static void VisibleSymbol() { }
    private sealed class ClosedGenericPayloadMustNeverAppear { }
    private sealed class ClosedGenericMethodArgumentMustNeverAppear { }
    private sealed class GenericSymbol<T>
    { public static void VisibleMethod<U>() { } }
    private sealed class LongTrustedTypeNameThatMakesTheQualifiedSymbolExceedTheAllowedCharacterBudgetWithoutUsingRuntimeInputOrSecrets
    {
        internal sealed class AnotherLongTrustedNestedTypeNameThatMakesTheQualifiedSymbolExceedTheAllowedCharacterBudgetWithoutUsingRuntimeInputOrSecrets
        { public static void VeryLongMethodNameThatStillMustRemainWithinThePerFrameBudgetAndNeverExtendTheRetainedStackBeyondItsExplicitOverallCharacterBudget() { } }
    }
    private sealed class SymbolFrame(MethodBase method, string source) : StackFrame(source, 77, 3)
    { public override MethodBase GetMethod() => method; }
    private sealed class SuppliedTrace(params StackFrame[] frames) : StackTrace
    {
        public override int FrameCount => frames.Length;
        public override StackFrame? GetFrame(int index) => frames[index];
    }
    private sealed class ThrowingTrace(string secret) : StackTrace
    {
        public override int FrameCount => 1;
        public override StackFrame? GetFrame(int index) => throw new InvalidOperationException(secret);
    }
    private static T ExpectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch elapsed = Stopwatch.StartNew(); while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); }
        Check(condition(), "The private native capture fixture did not settle.");
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static void WriteExecutable(string path, string program)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The capture native fixture requires Linux."); }
        File.WriteAllText(path, program); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private sealed class NativeGate : IDisposable
    {
        private readonly RepositoryFixture _fixture;
        private readonly string _wrapper, _marker, _release;
        internal string Helper { get; }
        internal string HelperCalled { get; }
        internal int ProcessId { get; private set; }
        internal NativeGate(RepositoryFixture fixture, bool probe)
        {
            _fixture = fixture; string prefix = Path.Combine(fixture.Root, "capture-native-" + Guid.NewGuid().ToString("N"));
            _wrapper = prefix + ".git"; _marker = prefix + ".pid"; _release = prefix + ".release";
            Helper = prefix + ".askpass"; HelperCalled = prefix + ".helper-called";
            WriteExecutable(Helper, "#!/bin/sh\nprintf 'unexpected' > " + Quote(HelperCalled) + "\nexit 91\n");
            string match = probe ? "test \"$1\" = config && test \"$2\" = --get && test \"$3\" = core.askpass" : "test \"$1\" = status";
            WriteExecutable(_wrapper, "#!/bin/sh\nif " + match + "; then\nprintf '%s\\n' \"$$\" > " + Quote(_marker + ".tmp")
                + "\nmv " + Quote(_marker + ".tmp") + " " + Quote(_marker) + "\nwhile test ! -f " + Quote(_release)
                + "; do sleep 0.02; done\nfi\nexec /usr/bin/git \"$@\"\n");
        }
        internal Task<GitResult> Run(CancellationToken token = default) => new GitRunner(_wrapper).RunAsync(_fixture.Repository.Path, ["status", "--porcelain"], token,
            environment: new Dictionary<string, string?> { ["DISPLAY"] = ":private-capture-probe" });
        internal async Task Ready()
        {
            await Until(() => File.Exists(_marker)); ProcessId = int.Parse(File.ReadAllText(_marker));
            await Until(() => GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId == ProcessId));
            Check(LinuxProcessState.IsRunning(ProcessId), "The gate did not observe the actual exec-confirmed process.");
        }
        internal void Release() => File.WriteAllText(_release, "release\n");
        public void Dispose() => Release();
    }
}
