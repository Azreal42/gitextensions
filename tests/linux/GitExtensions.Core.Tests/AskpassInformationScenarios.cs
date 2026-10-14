using System.Text;
using GitExtensions.Linux;

internal static class AskpassInformationScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Git information requests preserve native bytes without an askpass config subprocess", InformationAsync),
        ("Git information requests retain literal global option values and directory context", GlobalOptionsAsync),
        ("invalid Git version options keep the actual native failure and observer bytes", InvalidInformationAsync),
        ("askpass command probes retain global config and literal version-like directory values", CredentialContextAsync)
    ];

    private static async Task InformationAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.Create();
        foreach (string[] args in new[]
        {
            new[] { "--version" }, new[] { "-v" }, new[] { "--help" }, new[] { "-h" },
            new[] { "--exec-path" }, new[] { "--html-path" }, new[] { "--man-path" }, new[] { "--info-path" },
            new[] { "version", "--build-options" }, new[] { "help" }
        })
        {
            await f.Compare(args);
            Check(f.Arguments().Single().SequenceEqual(args), "Only the actual native information request may run; no config probe.");
        }
    }

    private static async Task GlobalOptionsAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.Create();
        string parent = Path.Combine(f.Root, "literal '雪;$(value) ");
        string child = Path.Combine(parent, "--version"); Directory.CreateDirectory(child);
        await f.Compare(["--no-pager", "-C", parent, "-C", "--version", "-c", "test.caption=-v", "--version", "--build-options"]);
        string[] actual = f.Arguments().Single();
        Check(actual.Contains(parent) && actual.Count(value => value == "--version") == 2,
            "A global option's literal value must not be mistaken for the terminal information request.");
    }

    private static async Task InvalidInformationAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.Create();
        GitResult result = await f.Compare(["--no-pager", "-c", "core.askpass=" + f.Helper, "--version", "--invalid-version-option"]);
        Check(result.ExitCode != 0 && result.StandardErrorBytes.Length > 0,
            "The failed native information request must remain failed with its original diagnostic.");
        Check(f.Arguments().Count == 1, "Native failure must not cause an extra askpass probe or retry.");
    }

    private static async Task CredentialContextAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.Create();
        string directory = Path.Combine(f.Root, "--version"); Directory.CreateDirectory(directory);
        string[] args = ["--no-pager", "-C", directory, "-c", "credential.helper=", "-c", "core.askpass=" + f.Helper, "credential", "fill"];
        const string request = "protocol=https\nhost=private.example.invalid\n\n";
        GitResult result = await f.Runner.RunAsync(f.Repository.Path, args, standardInput: request,
            environment: new Dictionary<string, string?> { ["GITEXTENSIONS_ARGS_DIRECTORY"] = f.Logs, ["WAYLAND_DISPLAY"] = "private-test-display" });
        Check(result.Success && result.StandardOutput.Contains("username=explicit-user\n", StringComparison.Ordinal),
            "The command-local explicit helper must retain precedence after a literal -C value named --version.");
        IReadOnlyList<string[]> calls = f.Arguments();
        Check(calls.Count == 2 && calls.Count(call => call.SequenceEqual(args)) == 1,
            "A real credential command still probes the helper once and runs the original command once.");
        string[] probe = calls.Single(call => !call.SequenceEqual(args));
        Check(probe.Take(args.Length - 2).SequenceEqual(args.Take(args.Length - 2))
            && probe.Skip(args.Length - 2).SequenceEqual(new[] { "config", "--get", "core.askpass" }),
            "The config probe must preserve global options and their literal values, excluding command arguments.");
    }

    private static void Check(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }

    private sealed class Fixture : IDisposable
    {
        private readonly RepositoryFixture _repository;
        private readonly string? _fallback;
        private readonly Dictionary<string, string?> _environment;
        public string Root => _repository.Root;
        public GitRepository Repository => _repository.Repository;
        public string Logs { get; }
        public string Helper { get; }
        public GitRunner Runner { get; }

        private Fixture(RepositoryFixture repository)
        {
            if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("This fixture records POSIX Git processes."); }
            _repository = repository; _fallback = GitRunner.AskpassFallbackPath;
            _environment = new[] { "GIT_ASKPASS", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "DISPLAY", "WAYLAND_DISPLAY" }
                .ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (string name in _environment.Keys) { Environment.SetEnvironmentVariable(name, null); }
            Logs = Path.Combine(Root, "argv"); Directory.CreateDirectory(Logs);
            Helper = Path.Combine(Root, "helper literal '雪");
            File.WriteAllText(Helper, "#!/bin/sh\nprintf 'explicit-user\\n'\n");
            File.SetUnixFileMode(Helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string recorder = Path.Combine(Root, "record real git");
            File.WriteAllText(recorder, "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"$GITEXTENSIONS_ARGS_DIRECTORY/$$.args\"\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(recorder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Runner = new(recorder); GitRunner.AskpassFallbackPath = Helper;
        }

        public static async Task<Fixture> Create() => new(await RepositoryFixture.CreateAsync());

        public async Task<GitResult> Compare(string[] args)
        {
            foreach (string file in Directory.GetFiles(Logs)) { File.Delete(file); }
            GitRunner.AskpassFallbackPath = null;
            GitResult native;
            try { native = await new GitRunner("/usr/bin/git").RunAsync(Repository.Path, args); }
            finally { GitRunner.AskpassFallbackPath = Helper; }
            using MemoryStream stdout = new(), stderr = new();
            GitResult observed = await Runner.RunAsync(Repository.Path, args,
                environment: new Dictionary<string, string?> { ["GITEXTENSIONS_ARGS_DIRECTORY"] = Logs, ["WAYLAND_DISPLAY"] = "private-test-display" },
                outputReceived: chunk => (chunk.Stream == GitProcessOutputStream.StandardOutput ? stdout : stderr).Write(chunk.Bytes.Span));
            Check(observed.ExitCode == native.ExitCode && observed.StandardOutputBytes.SequenceEqual(native.StandardOutputBytes)
                && observed.StandardErrorBytes.SequenceEqual(native.StandardErrorBytes), "Fallback handling must preserve the real Git result byte for byte.");
            Check(stdout.ToArray().SequenceEqual(observed.StandardOutputBytes) && stderr.ToArray().SequenceEqual(observed.StandardErrorBytes),
                "The observer must receive exactly the original information request's native streams.");
            return observed;
        }

        public IReadOnlyList<string[]> Arguments() => Directory.GetFiles(Logs).Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))
            .Split('\0').SkipLast(1).ToArray()).ToArray();

        public void Dispose()
        {
            GitRunner.AskpassFallbackPath = _fallback;
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            _repository.Dispose();
        }
    }
}
