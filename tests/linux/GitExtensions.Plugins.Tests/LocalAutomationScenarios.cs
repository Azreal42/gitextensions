using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitExtensions.Linux;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Plugins.Tests;

public static class LocalAutomationScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("proxy preserves literal credentials and local/global precedence, redacts display, and unsets idempotently", ProxyScopesAsync),
        ("Gource launches a configured executable without a shell and preserves quoted arguments and directory", GourceArgumentsAsync),
        ("Gource exports distinct authors at original size and retains avatars until the visualizer exits", GourceAvatarsAsync),
        ("Gource validates arguments, startup and cancellation without leaving temporary avatars", GourceFailuresAsync),
        ("background lifecycle enforces the minimum interval, waits for foreground Git and stops after reconfiguration", BackgroundLifecycleAsync),
        ("background one-shot refresh uses fetch stderr and custom-command semantics while ignoring failures", BackgroundRefreshAsync),
        ("background fetching updates a real local bare remote and suppresses refresh when already up to date", BackgroundLocalFetchAsync),
        ("background fetch visits initialized nested submodules before the independently configured root command", BackgroundSubmodulesAsync),
        ("background unregister cancels its running operation and prevents refresh of a closed repository", BackgroundCancellationAsync),
        ("auto compile only follows a successful enabled submodule update and builds a single literal solution", AutoCompileSingleAsync),
        ("auto compile retains per-solution Build Skip Cancel and stops remaining prompts on Cancel", AutoCompileDecisionsAsync),
        ("auto compile places solution placeholders safely and reports unsuccessful build output", AutoCompileArgumentsAsync),
        ("auto compile records a missing executable and cancels a running auxiliary process", AutoCompileFailureAsync)
    ];

    private static async Task ProxyScopesAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        string? previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(fixture.Root, "global config"));
        try
        {
            ProxySwitcherPlugin plugin = new();
            const string password = "secret:$(touch SHOULD_NOT_EXIST)@été";
            Dictionary<string, string?> global = Values(("operation", "set"), ("host", "proxy.example.invalid"), ("username", "alice"),
                ("password", password), ("port", "8080"), ("global", "true"));
            PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository), global);
            string expected = "alice:" + password + "@proxy.example.invalid:8080";
            Check((await fixture.Repository.GetConfigAsync("^http\\.proxy$", GitConfigScope.Global)).Single().Value == expected,
                "Proxy credentials were expanded, escaped or truncated before reaching Git.");
            ProxySwitcherState state = await ProxySwitcherPlugin.GetStateAsync(fixture.Repository);
            Check(state.ApplyGlobally && state.GlobalProxy == "alice:****@proxy.example.invalid:8080", "Global selection or redaction differs.");
            Check(!JsonSerializer.Serialize(result).Contains(password, StringComparison.Ordinal), "Plugin result leaked the proxy password.");
            Check(!File.Exists(Path.Combine(fixture.Repository.Path, "SHOULD_NOT_EXIST")), "Proxy value passed through a shell.");
            await plugin.ExecuteAsync(new(fixture.Repository), Values(("operation", "set"), ("host", "local.example.invalid"), ("port", "")));
            state = await ProxySwitcherPlugin.GetStateAsync(fixture.Repository);
            Check(!state.ApplyGlobally && state.EffectiveProxy == "local.example.invalid" && state.GlobalProxy.Contains("proxy.example", StringComparison.Ordinal),
                "Repository setting did not override the global proxy.");
            await plugin.ExecuteAsync(new(fixture.Repository), Values(("operation", "unset")));
            state = await ProxySwitcherPlugin.GetStateAsync(fixture.Repository);
            Check(state.ApplyGlobally && state.EffectiveProxy == state.GlobalProxy, "Unsetting a local proxy did not expose the global setting.");
            await plugin.ExecuteAsync(new(fixture.Repository), Values(("operation", "unset"), ("global", "true")));
            await plugin.ExecuteAsync(new(fixture.Repository), Values(("operation", "unset"), ("global", "true")));
            Check((await ProxySwitcherPlugin.GetStateAsync(fixture.Repository)).EffectiveProxy.Length == 0, "Proxy removal was not idempotent.");
            await ThrowsAsync<ArgumentException>(() => plugin.ExecuteAsync(new(fixture.Repository), Values(("operation", "set"), ("host", "example"), ("port", "65536"))));
            Check((await fixture.Repository.GetConfigAsync("^http\\.proxy$")).Count == 0, "Invalid proxy partly changed configuration.");
            Check(ProxySwitcherPlugin.HidePassword("http://user:pass@server:80") == "http://user:****@server:80", "URI scheme masking swallowed the username.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobal); }
    }

    private static async Task GourceArgumentsAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        using FakeExecutable executable = new(fixture.Root);
        string directory = Path.Combine(fixture.Root, "working été directory");
        Directory.CreateDirectory(directory);
        const string literal = "$(touch SENTINEL);`echo x`|cat";
        GourceLaunch launch = await new GourcePlugin().LaunchAsync(fixture.Repository,
            new(executable.Path, "--title \"Repository été\" --literal " + Quote(literal) + " --empty \"\" --escaped a\\ b", directory));
        await WaitForAsync(() => executable.Calls.Count == 1);
        Invocation call = executable.Calls.Single();
        Check(call.Arguments.SequenceEqual(new[] { "--title", "Repository été", "--literal", literal, "--empty", "", "--escaped", "a b" }),
            "Arguments changed at the executable boundary.");
        Check(call.WorkingDirectory == PluginTestFiles.PhysicalPath(directory) && launch.ProcessId == call.ProcessId, "Configured native working directory or process was lost.");
        Check(!File.Exists(Path.Combine(directory, "SENTINEL")), "Gource invoked a command shell.");
    }

    private static async Task GourceAvatarsAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        await fixture.CommitAsync("old author", "Same Name <old@example.invalid>");
        await fixture.CommitAsync("new author", "Same Name <new@example.invalid>");
        await fixture.CommitAsync("unsafe author", "Bad/Name <unsafe@example.invalid>");
        await fixture.CommitAsync("provider failure", "Bad Avatar <bad@example.invalid>");
        using FakeExecutable executable = new(fixture.Root, hold: true);
        AvatarProvider provider = new();
        string avatarRoot = Path.Combine(fixture.Root, "avatars ' quoted été");
        GourceLaunch launch = await new GourcePlugin(provider, avatarRoot).LaunchAsync(fixture.Repository,
            new(executable.Path, "--hide filenames --user-image-dir \"$(AVATARS)\""));
        await WaitForAsync(() => executable.Calls.Count == 1);
        Invocation call = executable.Calls.Single();
        string directory = launch.AvatarDirectory ?? throw new InvalidOperationException("Avatar directory was not returned.");
        Check(call.Arguments.SequenceEqual(new[] { "--hide", "filenames", "--user-image-dir", directory }), "Avatar path was split or re-parsed after replacement.");
        Check(File.Exists(Path.Combine(directory, "Same Name.png")) && Directory.Exists(directory), "Avatars were removed while Gource was running.");
        Check(provider.Calls.Count(item => item.Name == "Same Name") == 1 && provider.Calls.Single(item => item.Name == "Same Name").Email == "new@example.invalid",
            "Avatar author deduplication lost the latest author email.");
        Check(provider.Calls.All(item => item.Size == 90 && item.Name != "Bad/Name") && !File.Exists(Path.Combine(directory, "Bad Avatar.png")),
            "Original avatar size, unsafe filename exclusion, or per-avatar failure handling was lost.");
        executable.Release();
        await WaitForAsync(() => !Directory.Exists(directory));
    }

    private static async Task GourceFailuresAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        string avatarRoot = Path.Combine(fixture.Root, "avatars");
        GourcePlugin plugin = new(new AvatarProvider(), avatarRoot);
        await ThrowsAsync<ArgumentException>(() => plugin.LaunchAsync(fixture.Repository, new("missing", "--title \"unterminated")));
        await ThrowsAsync<System.ComponentModel.Win32Exception>(() => plugin.LaunchAsync(fixture.Repository, new(Path.Combine(fixture.Root, "missing executable"))));
        Check(!Directory.Exists(avatarRoot) || Directory.GetDirectories(avatarRoot).Length == 0, "Failed startup left avatar files behind.");
        await ThrowsAsync<DirectoryNotFoundException>(() => plugin.LaunchAsync(fixture.Repository, new("missing", WorkingDirectory: Path.Combine(fixture.Root, "missing directory"))));
        Check(Directory.GetDirectories(avatarRoot).Length == 0, "Invalid working directory left avatars behind.");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => plugin.LaunchAsync(fixture.Repository, new(), cancellation.Token));
    }

    private static async Task BackgroundLifecycleAsync()
    {
        Check(BackgroundFetchOptions.FromValues(Values(("interval", "40000"))).FetchEverySeconds == 40000,
            "Settings import imposed a day-based helper's unrelated limit on the interval in seconds.");
        Check(BackgroundFetchOptions.FromValues(Values(("interval", "-1"))).FetchEverySeconds == -1,
            "Non-positive intervals must preserve the original disabled setting semantics.");
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        ManualTimeProvider clock = new();
        await using BackgroundFetchService service = new(clock);
        int calls = 0;
        bool busy = true;
        BackgroundFetchContext context = new(fixture.Repository, () => busy, RunGitAsync: (_, _, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(new GitResult(0, "", "")); });
        await service.RegisterAsync(context, new(FetchEverySeconds: 1));
        await WaitForAsync(() => clock.TimerCount == 1);
        clock.Advance(TimeSpan.FromSeconds(4));
        Check(calls == 0, "Interval below five seconds was not clamped.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => clock.NextDue == clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        Check(calls == 0, "Background Git started while a foreground operation was running.");
        busy = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => calls == 1 && clock.TimerCount == 1);
        clock.Advance(TimeSpan.FromSeconds(4));
        Check(calls == 1, "Subsequent interval was below five seconds.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => calls == 2 && clock.TimerCount == 1);
        await service.ReconfigureAsync(new());
        clock.Advance(TimeSpan.FromHours(1));
        Check(calls == 2 && clock.TimerCount == 0, "Disabled settings left the previous timer running.");
        await service.RegisterAsync(context, new(FetchImmediatelyOnRepositoryOpening: true));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls == 3 && clock.TimerCount == 0, "Interval zero plus fetch-on-opening must fetch exactly once.");
        await service.UnregisterAsync();
        await ThrowsAsync<InvalidOperationException>(() => service.ReconfigureAsync(new()));
    }

    private static async Task BackgroundRefreshAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        await using BackgroundFetchService service = new();
        int refreshes = 0;
        async Task RunAsync(string command, GitResult result, bool autoRefresh = true)
        {
            await service.RegisterAsync(new(fixture.Repository, RequestRefreshAsync: _ => { refreshes++; return Task.CompletedTask; },
                RunGitAsync: (_, _, _) => Task.FromResult(result)), new(command, AutoRefresh: autoRefresh, FetchImmediatelyOnRepositoryOpening: true));
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await RunAsync("", new(0, "From stdout", ""));
        Check(refreshes == 0, "Fetch inspected stdout instead of stderr.");
        await RunAsync("fetch --all", new(0, "", "From local-remote\nnew commits"));
        Check(refreshes == 1, "Changed fetch failed to refresh.");
        await RunAsync("status --short", new(0, "", ""));
        Check(refreshes == 2, "Successful non-fetch command did not refresh.");
        await RunAsync("fetch", new(0, "", "From local-remote"), autoRefresh: false);
        await RunAsync("fetch", new(1, "", "From failed-remote"));
        Check(refreshes == 2, "Disabled or unsuccessful fetch refreshed the view.");
        await service.RegisterAsync(new(fixture.Repository, RunGitAsync: (_, _, _) => throw new IOException("background failure")),
            new(FetchImmediatelyOnRepositoryOpening: true));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(service.LastError is IOException, "Background failure escaped its lifecycle task or was not observable.");
    }

    private static async Task BackgroundLocalFetchAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        string bare = Path.Combine(fixture.Root, "local bare remote.git");
        await fixture.RunnerAsync(fixture.Root, "clone", "--bare", fixture.Repository.Path, bare);
        GitRepository clone = await GitRepository.CloneAsync(bare, Path.Combine(fixture.Root, "clone"), recursive: false);
        await fixture.Repository.RunAsync(["remote", "add", "origin", bare]);
        await fixture.CommitAsync("remote advanced");
        await fixture.Repository.RunAsync(["push", "origin", "main"]);
        int refreshes = 0;
        await using BackgroundFetchService service = new();
        BackgroundFetchContext context = new(clone, RequestRefreshAsync: _ => { refreshes++; return Task.CompletedTask; });
        BackgroundFetchOptions options = new(AutoRefresh: true, FetchImmediatelyOnRepositoryOpening: true);
        await service.RegisterAsync(context, options);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(refreshes == 1 && await clone.ResolveRevisionAsync("origin/main") == await fixture.Repository.ResolveRevisionAsync("HEAD"),
            "Background fetch did not update a real local remote-tracking ref and notify the host.");
        await service.RegisterAsync(context, options);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(refreshes == 1 && service.LastResult?.Success == true, "Up-to-date fetch unnecessarily refreshed or failed.");
    }

    private static async Task BackgroundSubmodulesAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        GitRepository childSource = await fixture.NewRepositoryAsync("child source");
        GitRepository grandSource = await fixture.NewRepositoryAsync("grand source");
        await childSource.AddSubmoduleAsync(grandSource.Path, "nested grand");
        await fixture.CommitAsync("nested submodule", repository: childSource);
        await fixture.Repository.AddSubmoduleAsync(childSource.Path, "modules child");
        await fixture.Repository.UpdateSubmodulesAsync(recursive: true);
        await fixture.Repository.RunAsync(["config", "--file", ".gitmodules", "submodule.missing.path", "uninitialized"]);
        await fixture.Repository.RunAsync(["config", "--file", ".gitmodules", "submodule.missing.url", grandSource.Path]);
        ConcurrentQueue<(string Path, string[] Arguments)> calls = new();
        await using BackgroundFetchService service = new();
        await service.RegisterAsync(new(fixture.Repository, RunGitAsync: (repository, arguments, _) =>
        { calls.Enqueue((repository.Path, arguments.ToArray())); return Task.FromResult(new GitResult(0, "", "")); }),
            new("status --short", FetchAllSubmodules: true, FetchImmediatelyOnRepositoryOpening: true));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        (string Path, string[] Arguments)[] run = calls.ToArray();
        Check(run.Length == 3 && run[0].Path.EndsWith("modules child", StringComparison.Ordinal)
            && run[1].Path.EndsWith("nested grand", StringComparison.Ordinal), "Initialized nested submodule visitation was lost or uninitialized module was fetched.");
        Check(run.Take(2).All(item => item.Arguments.SequenceEqual(new[] { "fetch", "--all" }))
            && run[2].Path == fixture.Repository.Path && run[2].Arguments.SequenceEqual(new[] { "status", "--short" }),
            "Submodule fetch must be hardcoded independently from the root command, and precede it.");
    }

    private static async Task BackgroundCancellationAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        await using BackgroundFetchService service = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopped = false;
        int refreshes = 0;
        await service.RegisterAsync(new(fixture.Repository, RequestRefreshAsync: _ => { refreshes++; return Task.CompletedTask; },
            RunGitAsync: async (_, _, token) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { stopped = true; throw; }
                return new(0, "", "From a closed repository");
            }), new(AutoRefresh: true, FetchImmediatelyOnRepositoryOpening: true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.UnregisterAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(stopped && refreshes == 0 && service.Completion.IsCompletedSuccessfully, "Unregister left a fetch or stale refresh running.");
    }

    private static async Task AutoCompileSingleAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        using FakeExecutable executable = new(fixture.Root);
        string solution = Path.Combine(fixture.Repository.Path, "submodule été", "single [*] $(literal).SLN");
        Directory.CreateDirectory(Path.GetDirectoryName(solution)!);
        File.WriteAllText(solution, "A fake builder does not parse solution content.");
        AutoCompileSubmodulesService service = new();
        int prompts = 0;
        Task<AutoCompileDecision> Confirm(AutoCompileBuildRequest request, CancellationToken token)
        {
            prompts++;
            Check(request.SolutionPath == solution && request.AllSolutions.SequenceEqual(new[] { solution }), "Single solution prompt omitted its path or full list.");
            return Task.FromResult(AutoCompileDecision.Build);
        }
        await service.AfterSubmoduleUpdateAsync(fixture.Repository, false, new(true, executable.Path), Confirm);
        await service.AfterSubmoduleUpdateAsync(fixture.Repository, true, new(false, executable.Path), Confirm);
        Check(prompts == 0 && executable.Calls.Count == 0, "Failed or disabled submodule update launched a build.");
        AutoCompileSubmodulesResult result = await service.AfterSubmoduleUpdateAsync(fixture.Repository, true, new(true, executable.Path), Confirm);
        Check(prompts == 1 && result.Builds.Single().Result?.Success == true && !result.Cancelled, "Single solution was skipped by the historical loop boundary defect.");
        Invocation call = executable.Calls.Single();
        Check(call.Arguments.SequenceEqual(new[] { solution, "/p:Configuration=Debug" }) && call.WorkingDirectory == PluginTestFiles.PhysicalPath(fixture.Repository.Path),
            "Default original build arguments or literal solution path changed.");
    }

    private static async Task AutoCompileDecisionsAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        using FakeExecutable executable = new(fixture.Root);
        foreach (string name in new[] { "a.sln", "b.sln", "c.sln", "d.sln" }) File.WriteAllText(Path.Combine(fixture.Repository.Path, name), "solution");
        Queue<AutoCompileDecision> decisions = new([AutoCompileDecision.Build, AutoCompileDecision.Skip, AutoCompileDecision.Cancel]);
        List<string> prompted = [];
        AutoCompileSubmodulesResult result = await new AutoCompileSubmodulesService().AfterSubmoduleUpdateAsync(fixture.Repository, true,
            new(true, executable.Path), (request, _) =>
            {
                Check(request.AllSolutions.Count == 4, "Confirmation omitted another discovered solution.");
                prompted.Add(Path.GetFileName(request.SolutionPath));
                return Task.FromResult(decisions.Dequeue());
            });
        Check(result.Cancelled && result.Builds.Select(item => item.Decision).SequenceEqual(new[] { AutoCompileDecision.Build, AutoCompileDecision.Skip, AutoCompileDecision.Cancel }),
            "Build/Skip/Cancel decisions were ignored.");
        Check(prompted.SequenceEqual(new[] { "d.sln", "c.sln", "b.sln" }) && executable.Calls.Count == 1, "Cancel did not stop remaining prompts or Skip ran the builder.");
    }

    private static async Task AutoCompileArgumentsAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        using FakeExecutable executable = new(fixture.Root, fail: true);
        string solution = Path.Combine(fixture.Repository.Path, "quoted ' été $(touch SENTINEL).sln");
        File.WriteAllText(solution, "solution");
        AutoCompileSubmodulesResult result = await new AutoCompileSubmodulesService().AfterSubmoduleUpdateAsync(fixture.Repository, true,
            new(true, executable.Path, "build \"$(SOLUTION)\" --configuration \"Debug été\" \"$(touch SENTINEL)\""), (_, _) => Task.FromResult(AutoCompileDecision.Build));
        Invocation call = executable.Calls.Single();
        Check(call.Arguments.SequenceEqual(new[] { "build", solution, "--configuration", "Debug été", "$(touch SENTINEL)" }),
            "Solution substitution was split, evaluated or prepended despite explicit placeholder.");
        Check(result.Builds.Single().Result is { ExitCode: 7, StandardOutput: "fake build output\n", StandardError: "fake build failure\n" },
            "Failed build lost its exit code or captured output.");
        Check(!File.Exists(Path.Combine(fixture.Repository.Path, "SENTINEL")), "Build arguments passed through a shell.");
    }

    private static async Task AutoCompileFailureAsync()
    {
        using AutomationFixture fixture = await AutomationFixture.CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Repository.Path, "solution.sln"), "solution");
        AutoCompileSubmodulesService service = new();
        AutoCompileSubmodulesResult result = await service.AfterSubmoduleUpdateAsync(fixture.Repository, true, new(Enabled: true), (_, _) => Task.FromResult(AutoCompileDecision.Build));
        Check(result.Builds.Single() is { Result: null, Error: not null }, "Missing executable did not produce a reviewable build error.");
        using FakeExecutable executable = new(fixture.Root, hold: true);
        using CancellationTokenSource cancellation = new();
        Task<AutoCompileSubmodulesResult> operation = service.AfterSubmoduleUpdateAsync(fixture.Repository, true, new(true, executable.Path),
            (_, _) => Task.FromResult(AutoCompileDecision.Build), cancellation.Token);
        await WaitForAsync(() => executable.Calls.Count == 1);
        int pid = executable.Calls.Single().ProcessId;
        using PluginProcessReference process = new(pid);
        Check(process.Image == PluginTestFiles.PhysicalPath(executable.Path), "The pinned build actor is not this fixture's executable.");
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => operation);
        Check(!process.IsRunning, "Cancellation returned before the pinned build process exited.");
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static Dictionary<string, string?> Values(params (string Key, string Value)[] items) => items.ToDictionary(item => item.Key, item => (string?)item.Value);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(5, deadline.Token); }
    }

    private sealed class AutomationFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions automation " + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        private readonly GitRunner _runner = new();
        public static async Task<AutomationFixture> CreateAsync()
        {
            AutomationFixture fixture = new();
            Directory.CreateDirectory(fixture.Root);
            fixture.Repository = await fixture.NewRepositoryAsync("repository");
            return fixture;
        }
        public async Task<GitRepository> NewRepositoryAsync(string name)
        {
            GitRepository repository = await GitRepository.InitAsync(Path.Combine(Root, name));
            await repository.SetConfigAsync("user.name", "Automation Test");
            await repository.SetConfigAsync("user.email", "automation@example.invalid");
            await repository.SetConfigAsync("commit.gpgsign", "false");
            await repository.SetConfigAsync("core.hooksPath", PluginTestProfile.EmptyHooks(repository));
            await repository.SetConfigAsync("core.autocrlf", "false");
            await CommitAsync("Initial commit", repository: repository);
            return repository;
        }
        public async Task CommitAsync(string message, string? author = null, GitRepository? repository = null)
        {
            repository ??= Repository;
            await repository.StageAllAsync();
            await repository.RunAsync(author is null ? ["commit", "--allow-empty", "-m", message] : ["commit", "--allow-empty", "--author=" + author, "-m", message]);
        }
        public async Task RunnerAsync(string directory, params string[] arguments)
        { GitResult result = await _runner.RunAsync(directory, arguments); Check(result.Success, result.StandardError); }
        public void Dispose() => PluginTestFiles.DeleteDirectory(Root);
    }

    private sealed record Invocation([property: JsonPropertyName("args")] string[] Arguments,
        [property: JsonPropertyName("cwd")] string WorkingDirectory, [property: JsonPropertyName("pid")] int ProcessId);

    private sealed class FakeExecutable : IDisposable
    {
        private readonly string _directory;
        public string Path { get; }
        public FakeExecutable(string root, bool hold = false, bool fail = false)
        {
            _directory = System.IO.Path.Combine(root, "fake executable été " + Guid.NewGuid().ToString("N"));
            Path = PluginExecutableFixture.Install(_directory, "configured program", "automation");
            if (hold) { File.WriteAllText(System.IO.Path.Combine(_directory, "hold"), ""); }
            if (fail) { File.WriteAllText(System.IO.Path.Combine(_directory, "fail"), ""); }
        }
        public IReadOnlyList<Invocation> Calls
        {
            get
            {
                string file = System.IO.Path.Combine(_directory, "calls.jsonl");
                if (!File.Exists(file)) { return []; }
                List<Invocation> calls = [];
                using StreamReader reader = new(File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                while (reader.ReadLine() is string line)
                {
                    try { if (JsonSerializer.Deserialize<Invocation>(line) is { } call) { calls.Add(call); } }
                    catch (JsonException) { /* A line being written will be read on the next poll. */ }
                }
                return calls;
            }
        }
        public void Release() => File.WriteAllText(System.IO.Path.Combine(_directory, "release"), "");
        public void Dispose()
        {
            Release();
            foreach (Invocation call in Calls)
            {
                try
                {
                    using Process process = Process.GetProcessById(call.ProcessId);
                    if (!process.WaitForExit(20000)) { throw new TimeoutException("The released private fixture did not exit."); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private sealed class AvatarProvider : IGourceAvatarProvider
    {
        public ConcurrentQueue<(string Email, string Name, int Size)> Calls { get; } = new();
        public Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue((email, authorName, imageSize));
            if (authorName == "Bad Avatar") { throw new IOException("No avatar"); }
            return Task.FromResult<byte[]?>(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jG4QAAAAASUVORK5CYII="));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }
        public int TimerCount { get { lock (_gate) { return _timers.Count(timer => timer.Due.HasValue); } } }
        public DateTimeOffset? NextDue { get { lock (_gate) { return _timers.Select(timer => timer.Due).Min(); } } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] ready;
            lock (_gate)
            {
                _now += elapsed;
                ready = _timers.Where(timer => timer.Due <= _now).ToArray();
                foreach (ManualTimer timer in ready) { timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : null; }
            }
            foreach (ManualTimer timer in ready) { timer.Fire(); }
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public TimeSpan Period { get; private set; }
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) { return false; }
                    Period = period;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    return true;
                }
            }
            public void Fire() { if (!_disposed) { callback(state); } }
            public void Dispose() { lock (owner._gate) { _disposed = true; Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
