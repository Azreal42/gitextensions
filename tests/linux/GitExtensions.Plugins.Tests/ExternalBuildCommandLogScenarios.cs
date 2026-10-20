using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Plugins.Tests;

internal static class ExternalBuildCommandLogScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("external build journal admits only actual Build decisions while Skip Cancel disabled and failed-update stay neutral", DecisionsAsync),
        ("external build journal captures its original caller before confirmation await and preserves real PID exit output", ContextAndWaitAsync)
    ];
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

    private static async Task DecisionsAsync()
    {
        using global::Fixture repository = await global::Fixture.CreateAsync(); await using PrivateBuild build = new(repository.Root);
        string solution = Path.Combine(repository.Repository.Path, "solution-name-secret.sln"); File.WriteAllText(solution, "solution fixture");
        AutoCompileSubmodulesService service = new(); GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy;
        try
        {
            GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(new(false));
            foreach (AutoCompileDecision decision in new[] { AutoCompileDecision.Skip, AutoCompileDecision.Cancel })
            {
                AutoCompileSubmodulesResult result = await service.AfterSubmoduleUpdateAsync(repository.Repository, true,
                    new(true, build.Executable, "literal-argument-secret"), (_, _) => Task.FromResult(decision));
                Check(result.Builds.Single().Decision == decision && result.Cancelled == (decision == AutoCompileDecision.Cancel)
                    && GitCommandLog.Snapshot().Count == 0 && !File.Exists(build.Marker), "Skip/Cancel admitted or started a build.");
            }
            int prompts = 0;
            await service.AfterSubmoduleUpdateAsync(repository.Repository, false, new(true, build.Executable), (_, _) => { prompts++; return Task.FromResult(AutoCompileDecision.Build); });
            await service.AfterSubmoduleUpdateAsync(repository.Repository, true, new(false, build.Executable), (_, _) => { prompts++; return Task.FromResult(AutoCompileDecision.Build); });
            Check(prompts == 0 && GitCommandLog.Snapshot().Count == 0 && !File.Exists(build.Marker), "Failed update/disabled hook admitted an auxiliary build.");
            Task<AutoCompileSubmodulesResult> operation = service.AfterSubmoduleUpdateAsync(repository.Repository, true,
                new(true, build.Executable, "literal-argument-secret"), (_, _) => Task.FromResult(AutoCompileDecision.Build));
            try
            {
                await build.ReadyAsync(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
                Check(live.Command == "build" && live.ProcessId == build.Pid && live.IsRunning && !operation.IsCompleted, "Build decision lost real running PID.");
                build.Release(); AutoCompileSubmodulesResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
                Check(result.Builds.Single().Result is { ExitCode: 7, StandardOutput: "build-output-secret\n", StandardError: "build-error-secret\n" },
                    "Native build result/streams changed while journaling.");
                GitCommandLogEntry done = GitCommandLog.Snapshot().Single();
                Check(done.Id == live.Id && done.ExitCode == 7 && done.ProcessId == build.Pid && !done.IsRunning && !done.Cancelled && !done.IsUnobserved,
                    "Build native completion was lost or invented.");
                string metadata = JsonSerializer.Serialize(GitCommandLog.Snapshot());
                foreach (string secret in new[] { "solution-name-secret", "literal-argument-secret", "build-output-secret", "build-error-secret", "configured-build-name-secret" })
                { Check(!metadata.Contains(secret, StringComparison.Ordinal), "Raw build data entered journal metadata."); }
            }
            finally { build.Release(); await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        }
        finally { GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(previous); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<AutoCompileSubmodulesResult> CallBeforeConfirmationAwait(AutoCompileSubmodulesService service, GitRepository repository,
        AutoCompileSubmodulesOptions options, Func<AutoCompileBuildRequest, CancellationToken, Task<AutoCompileDecision>> confirm)
        => service.AfterSubmoduleUpdateAsync(repository, true, options, confirm);
    private static async Task ContextAndWaitAsync()
    {
        using global::Fixture repository = await global::Fixture.CreateAsync(); await using PrivateBuild build = new(repository.Root);
        File.WriteAllText(Path.Combine(repository.Repository.Path, "solution.sln"), "solution fixture");
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy; bool ui = true; int captures = 0;
        TaskCompletionSource<AutoCompileDecision> choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource prompted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(new(true, () => { captures++; return ui; }, typeof(ExternalBuildCommandLogScenarios).Assembly));
        Task<AutoCompileSubmodulesResult>? operation = null;
        try
        {
            operation = CallBeforeConfirmationAwait(new(), repository.Repository, new(true, build.Executable), (_, _) => { prompted.TrySetResult(); return choice.Task; });
            await prompted.Task.WaitAsync(TimeSpan.FromSeconds(20)); Check(!operation.IsCompleted && !File.Exists(build.Marker), "Confirmation must actually precede launch.");
            ui = false; choice.SetResult(AutoCompileDecision.Build); await build.ReadyAsync();
            GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
            Check(live.IsOnMainThread == true && live.CallStack?.Contains(nameof(CallBeforeConfirmationAwait), StringComparison.Ordinal) == true && captures == 1,
                "Build context was captured after confirmation await rather than at the original caller.");
            build.Release(); await operation.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { choice.TrySetResult(AutoCompileDecision.Cancel); build.Release(); if (operation is not null) { await operation.WaitAsync(TimeSpan.FromSeconds(20)); } GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(previous); }
    }

    private sealed class PrivateBuild : IAsyncDisposable
    {
        private readonly string _root;
        internal string Executable => Path.Combine(_root, "configured-build-name-secret");
        internal string Marker => Path.Combine(_root, "pid");
        internal int Pid { get; private set; }
        internal PrivateBuild(string root)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private build fixtures require Linux."); }
            _root = Path.Combine(root, "private-build"); Directory.CreateDirectory(_root);
            File.WriteAllText(Executable, """
                #!/usr/bin/python3
                import os,pathlib,sys,time
                root=pathlib.Path(__file__).parent
                (root/'pid.tmp').write_text(str(os.getpid())); (root/'pid.tmp').replace(root/'pid')
                while not (root/'release').exists(): time.sleep(0.01)
                print('build-output-secret'); print('build-error-secret',file=sys.stderr); sys.exit(7)
                """);
            File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal void Release() => File.WriteAllText(Path.Combine(_root, "release"), "release");
        internal async Task ReadyAsync()
        {
            Stopwatch timer = Stopwatch.StartNew(); while (!File.Exists(Marker) && timer.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(10); }
            Check(File.Exists(Marker), "Private native build did not become ready."); Pid = int.Parse(File.ReadAllText(Marker), System.Globalization.CultureInfo.InvariantCulture);
            // Native marker publication may precede the parent's resumed PID callback. Await both within the same budget.
            while (!GitCommandLog.Snapshot().Any(x => x.Command == "build" && x.ProcessId == Pid) && timer.Elapsed < TimeSpan.FromSeconds(20))
            { await Task.Delay(10); }
            Check(GitCommandLog.Snapshot().Any(x => x.Command == "build" && x.ProcessId == Pid), "Build PID was not confirmed in the journal.");
        }
        public async ValueTask DisposeAsync()
        {
            Release();
            if (Pid > 0)
            {
                try { using Process process = Process.GetProcessById(Pid); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (ArgumentException) { }
            }
            Directory.Delete(_root, true);
        }
    }
}
