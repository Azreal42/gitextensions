using System.Diagnostics;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class GitEnvironmentScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("Git environment live options update existing repositories while explicit and frozen runners retain their context", LiveAsync),
        ("Git environment operation scopes retain executable and ambient config through asynchronous changes", OperationAsync),
        ("Git environment askpass probe and native command share one captured executable and HOME", AskpassAsync),
        ("Git environment child HOME and native global XDG overrides retain include context and template paths", HomeAsync),
        ("Git environment diagnostics execute local programs and distinguish launch errors from native failures", DiagnosticsAsync),
        ("Git environment script execution uses the same snapshot without adding Git desktop policies", ScriptAsync)
    ];
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Executable(string directory, string name, string body)
    {
        if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("These native environment scenarios require Linux."); }
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    private static string Wrapper(string root, string name, string log, string? before = null)
        => Executable(root, name, "printf '%s|%s\\n' " + Quote(name) + " \"$HOME\" >> " + Quote(log) + "\n"
            + before + "exec /usr/bin/git \"$@\"\n");
    private static async Task LiveAsync()
    {
        using State state = new(); using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string log = Path.Combine(fixture.Root, "choices"), a = Wrapper(fixture.Root, "git A λ '$()", log), b = Wrapper(fixture.Root, "git B", log);
        GitRepository existing = new(fixture.Repository.Path); GitRunner injected = new("/usr/bin/git");
        GitEnvironment.Configure(new() { Executable = a }); GitRunner frozen = GitEnvironment.Current.CreateRunner();
        await existing.GetStatusAsync();
        Check(File.ReadAllLines(log).All(line => line.StartsWith("git A λ '$()|", StringComparison.Ordinal)), "The existing repository ignored application options.");
        File.WriteAllText(log, ""); GitEnvironment.Configure(new() { Executable = b });
        await existing.GetStatusAsync();
        Check(File.ReadAllLines(log).Length > 0 && File.ReadAllLines(log).All(line => line.StartsWith("git B|", StringComparison.Ordinal)), "An already-open repository retained old options.");
        File.WriteAllText(log, "");
        await frozen.RunAsync(fixture.Repository.Path, ["--version"]);
        await injected.RunAsync(fixture.Repository.Path, ["--version"]);
        Check(File.ReadAllLines(log).Length == 1 && File.ReadAllLines(log)[0].StartsWith("git A λ '$()|", StringComparison.Ordinal), "A configured change replaced an injected/frozen runner or executable characters passed through a shell.");
    }
    private static async Task OperationAsync()
    {
        using State state = new(); using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string log = Path.Combine(fixture.Root, "choices"), a = Wrapper(fixture.Root, "A", log), b = Wrapper(fixture.Root, "B", log);
        string first = Path.Combine(fixture.Root, "first.config"), second = Path.Combine(fixture.Root, "second.config");
        File.WriteAllText(first, "[environment]\nvalue = first\n"); File.WriteAllText(second, "[environment]\nvalue = second\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", first); GitEnvironment.Configure(new() { Executable = a });
        GitRepository repository = new(fixture.Repository.Path);
        using (repository.BeginOperation())
        {
            await Task.Yield(); GitEnvironment.Configure(new() { Executable = b }); Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", second);
            Check((await repository.GetConfigAsync("^environment.value$")).Single().Value == "first", "The scope lost its original native config context.");
            await repository.SetConfigAsync("environment.local", "same-operation");
        }
        Check(File.ReadAllLines(log).All(line => line.StartsWith("A|", StringComparison.Ordinal)), "A multi-command operation mixed executable choices.");
        Check((await repository.GetConfigAsync("^environment.value$")).Single().Value == "second", "A new operation did not recapture ambient config.");
        Check(File.ReadAllLines(log).Last().StartsWith("B|", StringComparison.Ordinal), "A new operation ignored the published executable.");
    }
    private static async Task AskpassAsync()
    {
        using State state = new(); using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string log = Path.Combine(fixture.Root, "choices"), ready = Path.Combine(fixture.Root, "probe-ready"), release = Path.Combine(fixture.Root, "probe-release");
        string gate = "if test \"$1\" = config && test \"${2-}\" = --get && test \"${3-}\" = core.askpass; then\n"
            + "  : > " + Quote(ready) + "\n  while test ! -f " + Quote(release) + "; do /bin/sleep 0.01; done\nfi\n";
        string a = Wrapper(fixture.Root, "A", log, gate), b = Wrapper(fixture.Root, "B", log);
        string home = Path.Combine(fixture.Root, "home A"); Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("GIT_ASKPASS", null); Environment.SetEnvironmentVariable("SSH_ASKPASS", null);
        Environment.SetEnvironmentVariable("DISPLAY", ":git-environment-test"); GitRunner.AskpassFallbackPath = "/usr/bin/true";
        GitEnvironment.Configure(new() { Executable = a, HomeDirectory = home });
        Task<GitResult> command = new GitRunner().RunAsync(fixture.Repository.Path, ["status", "--porcelain"]);
        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!File.Exists(ready) && timeout.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
            Check(File.Exists(ready) && !command.IsCompleted, "The actual askpass config probe did not reach its gate.");
            GitEnvironment.Configure(new() { Executable = b }); Environment.SetEnvironmentVariable("HOME", fixture.Root);
            File.WriteAllText(release, "release"); GitResult result = await command.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Success && File.ReadAllLines(log).Length == 2 && File.ReadAllLines(log).All(line => line == "A|" + home), "The probe and command used different executable/environment snapshots.");
        }
        finally { File.WriteAllText(release, "release"); await command.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    private static async Task HomeAsync()
    {
        using State state = new(); using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string originalHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        string home = Path.Combine(fixture.Root, "child home"), xdg = Path.Combine(fixture.Root, "xdg"); Directory.CreateDirectory(home); Directory.CreateDirectory(Path.Combine(xdg, "git"));
        File.WriteAllText(Path.Combine(home, "template.txt"), "from custom HOME λ\n");
        File.WriteAllText(Path.Combine(home, "included"), "[environment]\norigin = included-home\n");
        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[include]\npath = ~/included\n[commit]\ntemplate = ~/template.txt\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", null); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
        GitEnvironment.Configure(new() { HomeDirectory = home }); GitRepository repository = new(fixture.Repository.Path);
        Check((await repository.GetConfigAsync("^environment.origin$")).Single().Value == "included-home", "Git did not resolve the child HOME include.");
        Check(await repository.GetCommitTemplateAsync() == "from custom HOME λ\n" && Environment.GetEnvironmentVariable("HOME") == originalHome, "Custom HOME escaped to the OS process or template expansion ignored it.");
        string global = Path.Combine(fixture.Root, "override"); File.WriteAllText(global, "[environment]\norigin = override\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
        Check((await repository.GetConfigAsync("^environment.origin$")).Single().Value == "override", "GIT_CONFIG_GLOBAL lost native precedence.");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", null); File.Delete(Path.Combine(home, ".gitconfig"));
        File.WriteAllText(Path.Combine(xdg, "git", "config"), "[environment]\norigin = xdg\n");
        Check((await repository.GetConfigAsync("^environment.origin$")).Single().Value == "xdg", "An existing repository failed to recapture native XDG config.");
        GitToolConfigurationSnapshot snapshot = await repository.GetToolConfigurationAsync();
        Check(snapshot.GetScope(GitToolConfigurationView.Global).CanWrite, "The guarded config reader did not use the same native HOME/XDG context.");
    }
    private static async Task DiagnosticsAsync()
    {
        using State state = new(); using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string tools = Path.Combine(fixture.Root, "tools λ"), marker = Path.Combine(fixture.Root, "sh-executed");
        Executable(tools, "sh", ": > " + Quote(marker) + "\nexec /bin/sh \"$@\"\n");
        Executable(tools, "ssh", "printf 'native ssh diagnostic\\n' >&2\nexit 7\n");
        GitExecutionEnvironment snapshot = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git", ToolsDirectory = tools },
            new Dictionary<string, string?> { ["PATH"] = "", ["GIT_CONFIG_GLOBAL"] = "", ["HOME"] = fixture.Root });
        GitEnvironmentDiagnostic diagnostic = await GitEnvironmentDiagnostics.ProbeAsync(snapshot, fixture.Root);
        Check(diagnostic.Git.Success && diagnostic.Version is not null && diagnostic.Shell.Success && File.Exists(marker), "Diagnostics reported discovery without actual local execution.");
        Check(!diagnostic.Ssh.Success && diagnostic.Ssh.Result?.ExitCode == 7 && diagnostic.Ssh.Error is null && diagnostic.Ssh.Result.StandardError == "native ssh diagnostic\n", "A native failure was confused with a launch error.");
        Check(diagnostic.GlobalConfigCandidates.Count == 0 && GitEnvironmentDiagnostics.MinimumVersion == new Version(2, 43, 0)
            && GitEnvironmentDiagnostics.RecommendedVersion == new Version(2, 53, 0), "Native empty-global precedence or Windows version constants changed.");
        GitEnvironmentDiagnostic missing = await GitEnvironmentDiagnostics.ProbeAsync(GitExecutionEnvironment.Capture(new() { Executable = "/no/such/git-environment-test" }, snapshot.EnvironmentVariables), fixture.Root);
        Check(missing.Git.Result is null && missing.Git.Error is not null, "A failed exec was fabricated as a native Git result.");
        string counterfeit = Executable(fixture.Root, "counterfeit-git", "printf 'git version 9999999999999999999999.1.0\\n'\n");
        GitEnvironmentDiagnostic invalid = await GitEnvironmentDiagnostics.ProbeAsync(GitExecutionEnvironment.Capture(new() { Executable = counterfeit }, snapshot.EnvironmentVariables), fixture.Root);
        Check(invalid.Git.Result?.Success == true && !invalid.Git.Success && invalid.Version is null && invalid.Git.Error is not null,
            "Invalid version output crashed the diagnostic or was reported as valid Git.");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        try { await GitEnvironmentDiagnostics.ProbeAsync(snapshot, fixture.Root, cancelled.Token); throw new InvalidOperationException("Cancelled diagnostics unexpectedly ran."); }
        catch (OperationCanceledException) { }
    }
    private static async Task ScriptAsync()
    {
        using State state = new(); using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string home = Path.Combine(fixture.Root, "script-home"), tool = Executable(fixture.Root, "script tool", "printf '%s|%s|%s' \"$HOME\" \"${GIT_EDITOR-unset}\" \"$1\"\n");
        Environment.SetEnvironmentVariable("GIT_EDITOR", null);
        GitExecutionEnvironment snapshot = GitExecutionEnvironment.Capture(new() { HomeDirectory = home });
        ScriptProcessResult result = await new ScriptProcessRunner().RunAsync(tool, fixture.Root, ["literal λ '$()"], executionEnvironment: snapshot);
        Check(result.Success && System.Text.Encoding.UTF8.GetString(result.StandardOutputBytes) == home + "|unset|literal λ '$()", "A script lost the captured environment/argv or received noninteractive Git policies.");
    }
    private sealed class State : IDisposable
    {
        private readonly GitEnvironmentOptions _options = GitEnvironment.Options;
        private readonly string? _askpass = GitRunner.AskpassFallbackPath;
        private readonly Dictionary<string, string?> _environment = new[] { "HOME", "PATH", "XDG_CONFIG_HOME", "GIT_CONFIG_GLOBAL", "GIT_ASKPASS", "SSH_ASKPASS", "DISPLAY", "GIT_EDITOR" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        internal State() { GitEnvironment.Configure(new()); }
        public void Dispose()
        {
            GitEnvironment.Configure(_options); GitRunner.AskpassFallbackPath = _askpass;
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
        }
    }
}
