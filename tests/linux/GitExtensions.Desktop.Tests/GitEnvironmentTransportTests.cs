using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class GitEnvironmentTransportTests
{
    [Test]
    public async Task ConsoleRequestRoundtripCopiesItsEnvironmentAndLegacyJsonIgnoresApplicationPreferences()
    {
        using State state = new(); using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string global = fixture.Output("transport.config"); File.WriteAllText(global, "[transport]\nvalue = captured λ\n");
        Dictionary<string, string?> inherited = new(GitEnvironment.Current.EnvironmentVariables) { ["GIT_CONFIG_GLOBAL"] = global };
        GitExecutionEnvironment captured = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" }, inherited);
        InteractiveFileConsoleRequest request = new("socket", "nonce", fixture.Repository.Path, captured.Executable, ["--version"])
        { ExecutionEnvironment = InteractiveFileConsoleEnvironment.Capture(captured) };
        string json = JsonSerializer.Serialize(request);
        inherited["GIT_CONFIG_GLOBAL"] = "/dev/null";
        GitEnvironment.Configure(new() { Executable = "/no/such/application-git", HomeDirectory = "/no/such/application-home" });
        InteractiveFileConsoleRequest restored = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(json)!;
        GitResult value = await restored.RestoreExecutionEnvironment().CreateRunner().RunAsync(fixture.Repository.Path, ["config", "--global", "--get", "transport.value"]);
        Assert.That(value.Success, Is.True, value.StandardError); Assert.That(value.StandardOutput, Is.EqualTo("captured λ\n"));
        Assert.That(restored.ExecutionEnvironment!.EnvironmentVariables["GIT_CONFIG_GLOBAL"], Is.EqualTo(global));
        string oldJson = JsonSerializer.Serialize(new { request.SocketPath, request.Nonce, request.RepositoryPath, request.GitExecutable, request.Arguments });
        InteractiveFileConsoleRequest old = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(oldJson)!;
        Assert.That(old.ExecutionEnvironment, Is.Null);
        GitResult legacy = await old.RestoreExecutionEnvironment().CreateRunner().RunAsync(fixture.Repository.Path, ["--version"]);
        Assert.That(legacy.Success, Is.True, legacy.StandardError);
        Assert.That(old.RestoreExecutionEnvironment().HomeDirectory, Is.EqualTo(Environment.GetEnvironmentVariable("HOME")));
        (string socket, string nonce, string repository, string git, string[] arguments) = old;
        Assert.That((socket, nonce, repository, git), Is.EqualTo((request.SocketPath, request.Nonce, request.RepositoryPath, request.GitExecutable)));
        Assert.That(arguments, Is.EqualTo(request.Arguments));
    }

    [AvaloniaTest]
    public Task ForegroundScriptRetainsTheGitChoiceAndHomeCapturedBeforeItsConfirmation()
        => ScriptSnapshotAsync(background: false);

    [AvaloniaTest]
    public Task BackgroundScriptRetainsTheGitChoiceAndHomeCapturedBeforeItsConfirmation()
        => ScriptSnapshotAsync(background: true);

    private static async Task ScriptSnapshotAsync(bool background)
    {
        using State state = new(); using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string log = fixture.Output("script-environment"), home = fixture.Output("captured home"); Directory.CreateDirectory(home);
        string first = Wrapper(fixture.Output("Git A λ '$()"), log, "A"), second = Wrapper(fixture.Output("Git B"), log, "B");
        GitEnvironment.Configure(new() { Executable = first, HomeDirectory = home });
        CapturedScriptContext captured = await ScriptHostContext.CaptureAsync(fixture.Repository, new(), default);
        Assert.That(captured.Context.GitExecutable, Is.EqualTo(first));
        Assert.That(captured.Context.ExecutionEnvironment!.HomeDirectory, Is.EqualTo(home));
        Window owner = new() { Width = 900, Height = 650 }; owner.Show(); Task<bool>? execution = null;
        try
        {
            execution = DesktopScriptExecutor.ExecuteAsync(owner, fixture.Repository,
                new() { Name = "Captured Git", Command = "git", Arguments = "--version", AskConfirmation = true, RunInBackground = background });
            await Until(() => owner.OwnedWindows.Any());
            Window question = owner.OwnedWindows.Single();
            GitEnvironment.Configure(new() { Executable = second }); File.WriteAllText(log, "");
            Button yes = question.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Yes");
            PushProcessTestDriver.Pointer(yes);
            if (background) { await Until(() => execution.IsCompleted); }
            else { await PushProcessTestDriver.CompleteAsync(execution, owner); }
            Assert.That(await execution, Is.True);
            await Until(() => File.Exists(log) && File.ReadAllText(log).Length > 0);
            Assert.That(File.ReadAllLines(log), Is.EqualTo(new[] { "A|" + home }));
        }
        finally
        {
            owner.Close(); await Until(() => !owner.IsVisible);
            if (execution is not null) { try { await execution; } catch (OperationCanceledException) { } }
        }
    }

    [Test]
    public async Task StartupDiscoveryUsesOneSnapshotAndItsReturnedDefaultRepositoryRemainsLive()
    {
        using State state = new(); using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string log = fixture.Output("startup-environment"), ready = fixture.Output("startup-ready"), release = fixture.Output("startup-release");
        string first = Wrapper(fixture.Output("Git A"), log, "A", "if test ! -f " + Quote(ready) + "; then\n: > " + Quote(ready)
            + "\nwhile test ! -f " + Quote(release) + "; do /bin/sleep 0.01; done\nfi\n");
        string second = Wrapper(fixture.Output("Git B"), log, "B");
        GitEnvironment.Configure(new() { Executable = first });
        Task<DesktopStartupRequest> preparing = DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", fixture.Repository.Path, "-commit=HEAD"]), fixture.Repository.Path);
        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!File.Exists(ready) && timeout.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(10); }
            Assert.That(File.Exists(ready), Is.True, "The real discovery command did not reach its gate.");
            GitEnvironment.Configure(new() { Executable = second });
            File.WriteAllText(release, "release"); DesktopStartupRequest prepared = await preparing;
            Assert.That(prepared.BrowseRequest!.SelectedRevision!.Kind, Is.EqualTo(GitCompareRevisionKind.Commit));
            Assert.That(File.ReadAllLines(log).All(line => line.StartsWith("A|", StringComparison.Ordinal)), Is.True);
            File.WriteAllText(log, ""); await prepared.Repository!.GetStatusAsync();
            Assert.That(File.ReadAllLines(log).Length, Is.GreaterThan(0));
            Assert.That(File.ReadAllLines(log).All(line => line.StartsWith("B|", StringComparison.Ordinal)), Is.True);
        }
        finally { File.WriteAllText(release, "release"); await preparing; }
    }

    [Test]
    public Task RealConsoleRestoresTheSnapshotDespiteADifferentTerminalEnvironmentAndMalformedProfile()
        => ConsoleSnapshotAsync(legacy: false);

    [Test]
    public Task LegacyConsoleApiAndOldJsonStillLaunchTheActualHelperWithItsInheritedEnvironment()
        => ConsoleSnapshotAsync(legacy: true);

    private static async Task ConsoleSnapshotAsync(bool legacy)
    {
        using State state = new(); using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string log = fixture.Output("console-environment"), home = fixture.Output("console-home"); Directory.CreateDirectory(home);
        string wrapper = Wrapper(fixture.Output("git console λ '$()"), log, "console");
        fixture.SetEnvironment("HOME", home);
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = wrapper });
        string profile = Path.Combine(environment.GetVariable("XDG_CONFIG_HOME")!, "gitextensions", "git-environment.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!); File.WriteAllText(profile, "{ malformed application profile");
        using Terminal terminal = new(legacy);
        using CancellationTokenSource cancellation = new();
        Task<GitResult> running = legacy
            ? InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, wrapper, ["--version"], cancellation.Token, terminal.Start, HelperExecutable())
            : InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, wrapper, ["--version"], cancellation.Token, terminal.Start, HelperExecutable(), environment);
        try
        {
            await terminal.WaitForAsync("Press Enter to close.");
            Assert.That(terminal.Output, Does.Contain("git version "));
            Assert.That(File.ReadAllLines(log), Is.EqualTo(new[] { "console|" + home }));
            await terminal.SendAsync("\n"); GitResult result = await running.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(result.Success, Is.True, result.StandardError);
        }
        finally
        {
            if (!running.IsCompleted) { cancellation.Cancel(); await terminal.SendAsync("\n"); }
            try { await running.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static string Wrapper(string path, string log, string name, string? before = null)
    {
        File.WriteAllText(path, "#!/bin/sh\nprintf '%s|%s\\n' " + Quote(name) + " \"$HOME\" >> " + Quote(log) + "\n" + before + "exec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20))
        { if (Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.RunJobs(); } await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The environment transport scenario did not settle.");
    }
    private static string HelperExecutable()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop"))) { root = root.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(path), Is.True, "Build the Desktop apphost before running its real console helper."); return path;
    }
    private sealed class Terminal(bool legacy) : IDisposable
    {
        private readonly StringBuilder _output = new(); private Task _read = Task.CompletedTask;
        private Process? _process;
        internal string Output { get { lock (_output) { return _output.ToString(); } } }
        internal Process Start(string executable, IReadOnlyList<string> arguments)
        {
            if (legacy)
            {
                string requestPath = arguments[1];
                InteractiveFileConsoleRequest request = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(File.ReadAllText(requestPath))!;
                File.WriteAllText(requestPath, JsonSerializer.Serialize(new { request.SocketPath, request.Nonce, request.RepositoryPath, request.GitExecutable, request.Arguments }));
            }
            ProcessStartInfo start = new("/usr/bin/script") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string value in new[] { "--quiet", "--return", "--flush", "--command", string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)), "/dev/null" }) { start.ArgumentList.Add(value); }
            start.Environment["TERM"] = "xterm-256color";
            start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            if (!legacy) { start.Environment["HOME"] = "/deliberately-different-terminal-home"; start.Environment["GIT_CONFIG_GLOBAL"] = "/no/terminal/config"; }
            _process = Process.Start(start)!; _read = Task.WhenAll(ReadAsync(_process.StandardOutput), ReadAsync(_process.StandardError));
            return Process.GetProcessById(_process.Id);
        }
        private async Task ReadAsync(StreamReader reader)
        {
            char[] buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0) { lock (_output) { _output.Append(buffer, 0, count); } }
        }
        internal async Task WaitForAsync(string text)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!Output.Contains(text, StringComparison.Ordinal) && timeout.Elapsed < TimeSpan.FromSeconds(20))
            { if (_process?.HasExited == true) { break; } await Task.Delay(10); }
            Assert.That(Output, Does.Contain(text));
        }
        internal async Task SendAsync(string text) { if (_process is not null && !_process.HasExited) { await _process.StandardInput.WriteAsync(text); await _process.StandardInput.FlushAsync(); } }
        public void Dispose() { if (_process?.HasExited == true) { _read.GetAwaiter().GetResult(); } TestContext.Out.WriteLine(Output); _process?.Dispose(); }
    }
    private sealed class State : IDisposable
    {
        private readonly GitEnvironmentOptions _previous = GitEnvironment.Options;
        internal State() { GitEnvironment.Configure(new()); }
        public void Dispose() => GitEnvironment.Configure(_previous);
    }
}
