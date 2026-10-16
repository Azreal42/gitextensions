using System.Diagnostics;
using System.Text;
using GitExtensions.Linux;

internal static class AskpassModesScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("published askpass launcher preserves confirmation notification and textual modes with exact protocol streams", ModesAsync),
        ("askpass mode hints coexist with explicit Git SSH and configured helpers without overriding their precedence", ExplicitHelpersAsync),
        ("askpass permission and notification cancellation drains the native Git helper and its waiting descendant without response leakage", CancellationAsync),
    ];

    private static async Task ModesAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync();
        string prompt = "Fictitious confirmation λ\n' & $(touch " + Quote(f.InjectionMarker) + ") (yes/no)?";
        foreach ((string? hint, string action, int code, string output) in new (string?, string, int, string)[]
        {
            ("confirm", "PermissionYes", 0, ""), ("confirm", "PermissionNo", 1, ""), ("confirm", "Cancel", 1, ""),
            ("none", "Notification", 0, ""), (null, "Yes", 0, "yes"), (null, "No", 0, "no"),
            ("Unrecognized-Case", "No", 0, "no"), ("", "Yes", 0, "yes")
        })
        {
            Pipes pipes = new();
            GitResult result = await f.Run(hint, action, prompt, pipes: pipes);
            string[] actual = f.Witness();
            Check(result.ExitCode == code && result.StandardOutput == output && result.StandardError.Length == 0
                && Bytes(pipes.Output, result.StandardOutputBytes) && pipes.Error.Length == 0,
                "Only the neighboring fixture apphost's exact native status and protocol bytes reach the Git runner and its observer.");
            Check(actual[0] == prompt && actual[1] == (hint ?? "<unset>") && actual[2] == "fr-FR"
                && actual[3] == f.Helper && actual[4] == f.Helper && actual[5] == "force" && actual[6] == "fallback",
                "The actual published launcher forwards one literal prompt, its original SSH hint and the UI culture to its sibling apphost.");
            Check(!File.Exists(f.InjectionMarker), "Prompt shell syntax remains data all the way through Git and the published launcher.");
        }
    }

    private static async Task ExplicitHelpersAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync();
        foreach (string hint in new[] { "confirm", "none" })
        {
            Dictionary<string, string?> extra = new() { ["SSH_ASKPASS"] = f.ExplicitHelper, ["SSH_ASKPASS_REQUIRE"] = "prefer" };
            GitResult result = await f.Run(hint, "PermissionYes", "Synthetic explicit confirmation", extra);
            string[] actual = f.Witness();
            Check(result.Success && result.StandardOutput.Length == 0 && result.StandardError.Length == 0
                && actual[1] == hint && actual[3] == "<unset>" && actual[4] == f.ExplicitHelper && actual[5] == "prefer" && actual[6] == "explicit",
                "An explicit SSH helper keeps its own executable and hint without being superseded by a Git fallback.");
            extra["GIT_ASKPASS"] = f.ExplicitHelper;
            result = await f.Run(hint, "PermissionYes", "Synthetic explicit confirmation", extra); actual = f.Witness();
            Check(result.Success && actual[3] == f.ExplicitHelper && actual[4] == f.ExplicitHelper && actual[1] == hint,
                "Explicit Git and SSH helpers coexist with the original transport mode.");

            await f.Repository.SetConfigAsync("core.askpass", f.ExplicitHelper);
            result = await f.Run(hint, "PermissionYes", "Synthetic configured confirmation"); actual = f.Witness();
            Check(result.Success && actual[3] == "<unset>" && actual[4] == f.Helper && actual[1] == hint,
                "Repository core.askpass prevents injection of GIT_ASKPASS while the SSH fallback still preserves the transport hint.");
            await f.Repository.UnsetConfigAsync("core.askpass");
            result = await f.Run(hint, "PermissionYes", "Synthetic command-local confirmation", coreAskpass: f.ExplicitHelper); actual = f.Witness();
            Check(result.Success && actual[3] == "<unset>" && actual[4] == f.Helper && actual[1] == hint,
                "Command-local core.askpass likewise retains its native precedence without removing SSH mode information.");
        }
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync();
        foreach (string hint in new[] { "confirm", "none" })
        {
            File.Delete(f.WaitingPids); File.Delete(f.GitPid); Pipes pipes = new();
            using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
            Task<GitResult> pending = f.Run(hint, "Wait", "Fictitious waiting confirmation", token: cancel.Token, pipes: pipes);
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                while (!File.Exists(f.WaitingPids) || new FileInfo(f.WaitingPids).Length == 0)
                {
                    if (pending.IsCompleted) { await pending; throw new InvalidOperationException("The mode-specific helper did not remain alive."); }
                    if (clock.Elapsed > TimeSpan.FromSeconds(8)) { throw new TimeoutException("No mode-specific waiting helper witness."); }
                    await Task.Delay(20);
                }
                int[] descendants = (await File.ReadAllTextAsync(f.WaitingPids)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                int nativeGit = int.Parse(await File.ReadAllTextAsync(f.GitPid));
                Check(descendants.Length == 2 && descendants.All(LinuxProcessState.IsRunning) && LinuxProcessState.IsRunning(nativeGit)
                    && f.Witness()[1] == hint, "A real Git process and the requested mode's helper plus descendant are live before Abort.");
                cancel.Cancel();
                await ExpectCancellation(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Check(!LinuxProcessState.IsRunning(nativeGit) && descendants.All(pid => !LinuxProcessState.IsRunning(pid))
                    && pipes.Output.Length == 0 && pipes.Error.Length == 0,
                    "Abort joins Git and all waiting descendants without publishing a response or diagnostic carrying prompt data.");
            }
            finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
        }
    }

    private static async Task ExpectCancellation(Func<Task> action)
    { try { await action(); } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected cancellation, not a synthetic helper result."); }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static bool Bytes(byte[] one, byte[] two) => one.AsSpan().SequenceEqual(two);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private sealed class Pipes
    {
        private readonly MemoryStream _output = new(), _error = new(); private readonly object _gate = new();
        public byte[] Output { get { lock (_gate) { return _output.ToArray(); } } }
        public byte[] Error { get { lock (_gate) { return _error.ToArray(); } } }
        public void Receive(GitProcessOutput output)
        { lock (_gate) { (output.Stream == GitProcessOutputStream.StandardOutput ? _output : _error).Write(output.Bytes.Span); } }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private sealed class Fixture : IDisposable
    {
        private const string Alias = "alias.askpass-mode=!exec \"$SSH_ASKPASS\" \"$GITEXTENSIONS_MODE_TEST_PROMPT\"";
        private readonly RepositoryFixture _root;
        private readonly string? _originalFallback = GitRunner.AskpassFallbackPath, _originalCulture = GitRunner.AskpassUiCulture;
        private readonly Dictionary<string, string?> _originalEnvironment;
        private readonly string _witness;
        private readonly GitRunner _runner;
        public GitRepository Repository => _root.Repository;
        public string Helper { get; }
        public string ExplicitHelper { get; }
        public string InjectionMarker { get; }
        public string WaitingPids { get; }
        public string GitPid { get; }

        private Fixture(RepositoryFixture root)
        {
            _root = root;
            _originalEnvironment = new[] { "GIT_ASKPASS", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "DISPLAY", "WAYLAND_DISPLAY", "SSH_ASKPASS_PROMPT", "GITEXTENSIONS_UI_CULTURE" }
                .ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (string name in _originalEnvironment.Keys) { Environment.SetEnvironmentVariable(name, null); }
            string directory = Path.Combine(root.Root, "askpass modes 'λ");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _witness = Path.Combine(directory, "witness"); InjectionMarker = Path.Combine(directory, "prompt-was-executed");
            WaitingPids = Path.Combine(directory, "waiting-pids"); GitPid = Path.Combine(directory, "git-pid");
            Helper = MakeHelper(Path.Combine(directory, "native helper"), "fallback");
            ExplicitHelper = MakeHelper(Path.Combine(directory, "explicit helper"), "explicit");
            string git = Path.Combine(directory, "native git wrapper");
            WriteExecutable(git, "#!/bin/sh\numask 077\nfor arg in \"$@\"; do if [ \"$arg\" = askpass-mode ]; then printf '%s' \"$$\" > \"$GITEXTENSIONS_MODE_TEST_GIT_PID\"; fi; done\nexec /usr/bin/git \"$@\"\n");
            _runner = new(git); GitRunner.AskpassFallbackPath = Helper; GitRunner.AskpassUiCulture = "fr-FR";
        }

        public static async Task<Fixture> CreateAsync() => new(await RepositoryFixture.CreateAsync());
        public string[] Witness() => Encoding.UTF8.GetString(File.ReadAllBytes(_witness)).Split('\0').SkipLast(1).ToArray();
        public Task<GitResult> Run(string? hint, string action, string prompt, IReadOnlyDictionary<string, string?>? extra = null,
            string? coreAskpass = null, CancellationToken token = default, Pipes? pipes = null)
        {
            Dictionary<string, string?> environment = new()
            {
                ["GIT_ASKPASS"] = null, ["SSH_ASKPASS"] = null, ["SSH_ASKPASS_REQUIRE"] = null,
                ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = "fixture-wayland", ["SSH_ASKPASS_PROMPT"] = hint,
                ["GITEXTENSIONS_UI_CULTURE"] = null, ["GITEXTENSIONS_MODE_TEST_PROMPT"] = prompt,
                ["GITEXTENSIONS_MODE_TEST_ACTION"] = action, ["GITEXTENSIONS_MODE_TEST_WITNESS"] = _witness,
                ["GITEXTENSIONS_MODE_TEST_WAITING_PIDS"] = WaitingPids, ["GITEXTENSIONS_MODE_TEST_GIT_PID"] = GitPid,
            };
            if (extra is not null) { foreach (KeyValuePair<string, string?> setting in extra) { environment[setting.Key] = setting.Value; } }
            // An absent helper must be absent from the ProcessStartInfo environment
            // dictionary as well: a deliberately supplied null entry is still an
            // explicit setting to ApplyAskpassAsync's helper-precedence detection.
            foreach (string name in environment.Where(setting => setting.Value is null).Select(setting => setting.Key).ToArray())
            { environment.Remove(name); }
            List<string> arguments = ["-c", Alias];
            if (coreAskpass is not null) { arguments.AddRange(["-c", "core.askpass=" + coreAskpass]); }
            arguments.Add("askpass-mode");
            return _runner.RunAsync(Repository.Path, arguments, token, environment: environment, outputReceived: pipes is null ? null : pipes.Receive);
        }

        private static string MakeHelper(string directory, string id)
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string helper = Path.Combine(directory, "gitextensions-askpass");
            string source = Path.GetFullPath("../../../../../../src/linux/GitExtensions.Desktop/gitextensions-askpass", AppContext.BaseDirectory);
            File.Copy(source, helper); File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            WriteExecutable(Path.Combine(directory, "GitExtensions"), """
                #!/bin/sh
                umask 077
                [ "$#" = 2 ] && [ "$1" = --askpass ] || exit 97
                printf '%s\0' "$2" "${SSH_ASKPASS_PROMPT-<unset>}" "${GITEXTENSIONS_UI_CULTURE-<unset>}" "${GIT_ASKPASS-<unset>}" "${SSH_ASKPASS-<unset>}" "${SSH_ASKPASS_REQUIRE-<unset>}" '__ID__' > "$GITEXTENSIONS_MODE_TEST_WITNESS"
                case "$GITEXTENSIONS_MODE_TEST_ACTION" in
                    PermissionYes|Notification) exit 0 ;;
                    PermissionNo|Cancel) exit 1 ;;
                    Yes) printf yes; exit 0 ;;
                    No) printf no; exit 0 ;;
                    Wait)
                        sleep 60 &
                        waiting_child=$!
                        printf '%s %s' "$$" "$waiting_child" > "$GITEXTENSIONS_MODE_TEST_WAITING_PIDS"
                        wait "$waiting_child"
                        exit 1
                        ;;
                esac
                exit 96
                """.Replace("__ID__", id, StringComparison.Ordinal) + "\n");
            return helper;
        }
        private static void WriteExecutable(string path, string text)
        {
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose()
        {
            GitRunner.AskpassFallbackPath = _originalFallback; GitRunner.AskpassUiCulture = _originalCulture;
            foreach (KeyValuePair<string, string?> setting in _originalEnvironment) { Environment.SetEnvironmentVariable(setting.Key, setting.Value); }
            _root.Dispose();
        }
    }
}
