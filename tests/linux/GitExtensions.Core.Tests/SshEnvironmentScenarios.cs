using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class SshEnvironmentScenarios
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("SSH native defaults and literal application choice preserve inherited authentication without process mutation", CaptureAsync),
        ("SSH application executable carries real clone fetch push and remote head reads without shell path expansion", TransportAsync),
        ("SSH native Git precedence retains command environment effective config and per-call overrides", PrecedenceAsync),
        ("SSH apply reaches existing repositories while captured operations and scripts retain their executable", SnapshotAsync),
        ("SSH diagnostic observes native command authority without executing command text", DiagnosticsAsync)
    ];
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Tool(string root, string name, string body)
    {
        string path = Path.Combine(root, name); File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static string Ssh(string root, string name, string log)
        => Tool(root, name, "printf '%s\\n' " + Quote(name) + " >> " + Quote(log) + "\n"
            + "if test \"${1-}\" = -V; then printf 'test ssh version\\n' >&2; exit 0; fi\n"
            + "if test \"${1-}\" = -G; then exit 1; fi\n"
            + "for argument do command=$argument; done\nexec /bin/sh -c \"$command\"\n");
    private static Dictionary<string, string?> Values(string root) => new(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/bin:/bin", ["HOME"] = root, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_SSH_VARIANT"] = "simple", ["SSH_AUTH_SOCK"] = "/inherited agent/socket",
        ["SSH_ASKPASS"] = "/inherited askpass", ["SSH_ASKPASS_REQUIRE"] = "prefer"
    };
    private static async Task Run(GitRunner runner, string cwd, params string[] arguments)
    {
        GitResult result = await runner.RunAsync(cwd, arguments);
        Check(result.Success, string.Join(' ', arguments) + ": " + result.StandardError);
    }
    private static Task CaptureAsync()
    {
        string? inherited = Environment.GetEnvironmentVariable("GIT_SSH");
        Dictionary<string, string?> values = Values("/private/home"); values["GIT_SSH"] = "inherited ssh"; values["GIT_SSH_COMMAND"] = "native command";
        GitExecutionEnvironment native = GitExecutionEnvironment.Capture(new(), values);
        GitExecutionEnvironment custom = GitExecutionEnvironment.Capture(new() { SshExecutable = "/literal ssh λ '$()" }, values);
        Check(native.GetVariable("GIT_SSH") == "inherited ssh" && custom.GetVariable("GIT_SSH") == "/literal ssh λ '$()", "SSH executable choice lost literal characters.");
        foreach (string key in new[] { "GIT_SSH_COMMAND", "GIT_SSH_VARIANT", "SSH_AUTH_SOCK", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE" })
        { Check(custom.GetVariable(key) == values[key], "The application changed native authority: " + key); }
        Check(values["GIT_SSH"] == "inherited ssh" && Environment.GetEnvironmentVariable("GIT_SSH") == inherited, "SSH publication mutated its input or process environment.");
        try { _ = GitExecutionEnvironment.Capture(new() { SshExecutable = "ssh\0bad" }); throw new InvalidOperationException("NUL accepted."); }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }
    private static async Task TransportAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string log = Path.Combine(fixture.Root, "ssh.log"), ssh = Ssh(fixture.Root, "ssh client λ ' $(touch unexpected)", log);
        GitExecutionEnvironment context = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git", SshExecutable = ssh }, Values(fixture.Root));
        GitRunner runner = context.CreateRunner(); string bare = Path.Combine(fixture.Root, "remote.git");
        await Run(runner, fixture.Root, "clone", "--bare", fixture.Repository.Path, bare);
        string url = "fixture.invalid:" + bare, clone = Path.Combine(fixture.Root, "ssh clone");
        await Run(runner, fixture.Root, "clone", url, clone);
        await Run(runner, clone, "fetch", "origin");
        await Run(runner, clone, "push", "origin", "HEAD:refs/heads/ssh-pushed");
        GitResult heads = await runner.RunAsync(fixture.Root, ["ls-remote", "--heads", url]);
        Check(heads.Success && heads.StandardOutput.Contains("refs/heads/ssh-pushed", StringComparison.Ordinal), "The selected SSH did not carry a real remote read/push.");
        Check(File.ReadAllLines(log).Length == 4 && File.ReadAllLines(log).All(line => line == Path.GetFileName(ssh)), "A network workflow did not use the selected literal executable.");
        Check(!File.Exists(Path.Combine(clone, "unexpected")) && !File.Exists(Path.Combine(fixture.Root, "unexpected")), "SSH path text was executed by a shell.");
    }
    private static async Task PrecedenceAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string log = Path.Combine(fixture.Root, "precedence.log"), app = Ssh(fixture.Root, "application", log), config = Ssh(fixture.Root, "configured", log), command = Ssh(fixture.Root, "command", log), perCall = Ssh(fixture.Root, "per-call", log);
        string url = "fixture.invalid:" + fixture.Repository.Path;
        Dictionary<string, string?> values = Values(fixture.Root); values["GIT_SSH_COMMAND"] = Quote(command);
        GitExecutionEnvironment context = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git", SshExecutable = app }, values);
        await Run(context.CreateRunner(), fixture.Repository.Path, "-c", "core.sshCommand=" + Quote(config), "ls-remote", url);
        Check(File.ReadAllLines(log).SequenceEqual(["command"]), "GIT_SSH_COMMAND did not outrank core.sshCommand and GIT_SSH.");
        values.Remove("GIT_SSH_COMMAND"); context = GitExecutionEnvironment.Capture(context.Options, values);
        await Run(context.CreateRunner(), fixture.Repository.Path, "-c", "core.sshCommand=" + Quote(config), "ls-remote", url);
        await Run(context.CreateRunner(), fixture.Repository.Path, "ls-remote", url);
        GitResult overridden = await context.CreateRunner().RunAsync(fixture.Repository.Path, ["ls-remote", url], environment: new Dictionary<string, string?> { ["GIT_SSH"] = perCall });
        Check(overridden.Success && File.ReadAllLines(log).SequenceEqual(["command", "configured", "application", "per-call"]), "Git config or per-call SSH authority was overwritten.");
        await Run(context.CreateRunner(), fixture.Repository.Path, "config", "core.sshCommand", Quote(config));
        await Run(context.CreateRunner(), fixture.Repository.Path, "ls-remote", url);
        Check(File.ReadAllLines(log).Last() == "configured", "Effective repository SSH configuration was ignored.");
        Dictionary<string, string?> native = Values(fixture.Root); native["GIT_SSH"] = perCall;
        await Run(GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" }, native).CreateRunner(), fixture.Root, "ls-remote", url);
        Check(File.ReadAllLines(log).Last() == "per-call", "Native default cleared inherited GIT_SSH.");
    }
    private static async Task SnapshotAsync()
    {
        GitEnvironmentOptions previous = GitEnvironment.Options;
        string? oldCommand = Environment.GetEnvironmentVariable("GIT_SSH_COMMAND"), oldVariant = Environment.GetEnvironmentVariable("GIT_SSH_VARIANT");
        Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null); Environment.SetEnvironmentVariable("GIT_SSH_VARIANT", "simple");
        try
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
            string log = Path.Combine(fixture.Root, "snapshots.log"), a = Ssh(fixture.Root, "first", log), b = Ssh(fixture.Root, "second", log);
            string url = "fixture.invalid:" + fixture.Repository.Path;
            GitEnvironment.Configure(new() { Executable = "/usr/bin/git", SshExecutable = a });
            GitExecutionEnvironment snapshot = GitEnvironment.Current;
            GitRepository existing = new(fixture.Repository.Path);
            using (existing.BeginOperation())
            {
                GitEnvironment.Configure(new() { Executable = "/usr/bin/git", SshExecutable = b });
                await Run(new GitRunner(), fixture.Root, "ls-remote", url);
                await Run(new GitRunner(), fixture.Root, "ls-remote", url);
            }
            await Run(new GitRunner(), fixture.Root, "ls-remote", url);
            string echo = Tool(fixture.Root, "show ssh", "printf '%s' \"$GIT_SSH\"\n");
            ScriptProcessResult script = await new ScriptProcessRunner().RunAsync(echo, fixture.Root, [], executionEnvironment: snapshot);
            Check(script.Success && Encoding.UTF8.GetString(script.StandardOutputBytes) == a, "A delayed script reread the new SSH preference.");
            Check(File.ReadAllLines(log).SequenceEqual(["first", "first", "second"]), "An operation mixed SSH choices or a new command missed Apply.");
        }
        finally { GitEnvironment.Configure(previous); Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", oldCommand); Environment.SetEnvironmentVariable("GIT_SSH_VARIANT", oldVariant); }
    }
    private static async Task DiagnosticsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string log = Path.Combine(fixture.Root, "diagnostic.log"), ssh = Ssh(fixture.Root, "selected ssh", log), marker = Path.Combine(fixture.Root, "must-not-run");
        Dictionary<string, string?> values = Values(fixture.Root); values["GIT_SSH_COMMAND"] = "touch " + Quote(marker);
        GitEnvironmentOptions options = new() { Executable = "/usr/bin/git", SshExecutable = ssh };
        GitEnvironmentDiagnostic probe = await GitEnvironmentDiagnostics.ProbeAsync(GitExecutionEnvironment.Capture(options, values), fixture.Repository.Path);
        Check(probe.Ssh.Success && probe.Ssh.Executable == ssh && probe.SshConfiguration?.Source == GitSshCommandSource.GitSshCommand && !File.Exists(marker), "SSH diagnostics executed command text or tested the wrong executable.");
        values.Remove("GIT_SSH_COMMAND");
        await Run(GitExecutionEnvironment.Capture(options, values).CreateRunner(), fixture.Repository.Path, "config", "core.sshCommand", "touch " + Quote(marker));
        probe = await GitEnvironmentDiagnostics.ProbeAsync(GitExecutionEnvironment.Capture(options, values), fixture.Repository.Path);
        Check(probe.SshConfiguration?.Source == GitSshCommandSource.CoreSshCommand && !File.Exists(marker), "SSH diagnostics executed core.sshCommand.");
        await Run(GitExecutionEnvironment.Capture(options, values).CreateRunner(), fixture.Repository.Path, "config", "--unset", "core.sshCommand");
        probe = await GitEnvironmentDiagnostics.ProbeAsync(GitExecutionEnvironment.Capture(options, values), fixture.Repository.Path);
        Check(probe.SshConfiguration?.Source == GitSshCommandSource.GitSsh && File.ReadAllLines(log).Length == 3, "SSH diagnostics confused the configured executable with a command override.");
    }
}
