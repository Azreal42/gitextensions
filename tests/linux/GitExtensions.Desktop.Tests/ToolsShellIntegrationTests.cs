using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("PosixConsoleIntegration"), NonParallelizable, Platform("Linux,MacOsX")]
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class ToolsShellIntegrationTests
{
    [Test]
    public async Task AUserShellWithoutCapturedLocaleKeepsTheTerminalsUtf8Encoding()
    {
        using Fixture fixture = new(); using PosixTestTerminal terminal = new() { ExitCodePath = fixture.ExitFile };
        Dictionary<string, string?> variables = new(fixture.Environment.EnvironmentVariables, StringComparer.Ordinal);
        foreach (string name in variables.Keys.Where(IsLocaleVariable).ToArray()) { variables.Remove(name); }
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(fixture.Environment.Options, variables);
        string locale = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        string script = "printf '%s\\0' \"$LANG\" \"$(/usr/bin/locale charmap)\" \"$GIT_PAGER\" \"${UNRELATED_BROKER_SECRET-unset}\" > \"$1\"; exit 0";
        await MacToolsShellLauncher.RunCoreAsync(fixture.Repository, environment,
            ["-c", script, "shell", fixture.Record], CancellationToken.None, start =>
            {
                foreach (string name in start.Environment.Keys.Where(IsLocaleVariable).ToArray()) { start.Environment.Remove(name); }
                start.Environment["LANG"] = locale;
                start.Environment["UNRELATED_BROKER_SECRET"] = "must not leak";
                return terminal.Launch(start);
            }, HelperExecutable(), terminal.CreateStartInfo).WaitAsync(TimeSpan.FromSeconds(20));
        await terminal.StoppedAsync();
        Assert.That(await File.ReadAllTextAsync(fixture.ExitFile), Is.EqualTo("0"));
        Assert.That((await File.ReadAllTextAsync(fixture.Record)).Split('\0'),
            Is.EqualTo(new[] { locale, "UTF-8", "user pager λ", "unset", "" }));
        Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
    }

    private static bool IsLocaleVariable(string name)
        => name is "LANG" or "LANGUAGE" || name.StartsWith("LC_", StringComparison.Ordinal);

    [TestCase(false)]
    [TestCase(true)]
    public async Task AcknowledgedShellKeepsItsCapturedEnvironmentInputAndExitAfterTheCallerLeaves(bool inheritTerminalType)
    {
        using Fixture fixture = new(); using CancellationTokenSource caller = new();
        using PosixTestTerminal terminal = new() { ExitCodePath = fixture.ExitFile };
        string expectedDirectory = await PhysicalDirectoryAsync(fixture.Repository);
        string script = "printf 'TOOLS_READY\\n'; IFS= read -r reply; printf '%s\\0' \"$$\" \"$PWD\" \"$CAPTURED_SECRET\" \"$GIT_PAGER\" \"$LC_ALL\" \"$GIT_TERMINAL_PROMPT\" \"$TERM\" \"${UNRELATED_BROKER_SECRET-unset}\" \"$reply\" > \"$1\"; exit 73";
        Dictionary<string, string?> variables = new(fixture.Environment.EnvironmentVariables, StringComparer.Ordinal);
        if (inheritTerminalType) { variables.Remove("TERM"); }
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(fixture.Environment.Options, variables);
        Task<int> opening = MacToolsShellLauncher.RunCoreAsync(fixture.Repository, environment,
            ["-c", script, "shell", fixture.Record], caller.Token, start =>
            {
                start.Environment["TERM"] = "native terminal λ"; start.Environment["UNRELATED_BROKER_SECRET"] = "must not leak";
                return terminal.Launch(start);
            }, HelperExecutable(), terminal.CreateStartInfo);
        try
        {
            int actualHelper = await opening.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            caller.Cancel(); // A closed GUI no longer owns this acknowledged shell.
            await terminal.UntilAsync("TOOLS_READY");
            await terminal.SendAsync("literal reply λ [x]*\n"); await terminal.StoppedAsync();
            Assert.That(await File.ReadAllTextAsync(fixture.ExitFile), Is.EqualTo("73"));
            string[] fields = (await File.ReadAllTextAsync(fixture.Record)).Split('\0');
            Assert.That(fields, Is.EqualTo(new[] { actualHelper.ToString(System.Globalization.CultureInfo.InvariantCulture), expectedDirectory, "literal ' $() λ", "user pager λ", "C", "0", inheritTerminalType ? "native terminal λ" : "captured terminal", "unset", "literal reply λ [x]*", "" }));
        }
        finally
        {
            caller.Cancel();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
            try { await terminal.SendAsync("\n"); } catch (IOException) { } catch (InvalidOperationException) { }
            await terminal.StoppedAsync();
        }
    }

    private static async Task<string> PhysicalDirectoryAsync(string path)
    {
        ProcessStartInfo start = new("/bin/pwd") { UseShellExecute = false, WorkingDirectory = path, RedirectStandardOutput = true };
        start.ArgumentList.Add("-P"); using Process process = Process.Start(start)!;
        string value = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(process.ExitCode, Is.Zero); return value.TrimEnd('\r', '\n');
    }

    [Test]
    public async Task AUserBackgroundJobCanOutliveTheAcknowledgedShellWithoutHoldingItsTerminalOpen()
    {
        using Fixture fixture = new(); using PosixTestTerminal terminal = new() { ExitCodePath = fixture.ExitFile };
        string ready = Path.Combine(fixture.Root, "background-ready"), release = Path.Combine(fixture.Root, "release"), done = Path.Combine(fixture.Root, "background-done");
        string child = "set -e; trap '' HUP; printf ready > \"$1\"; while [ ! -f \"$2\" ]; do sleep 0.02; done; printf done > \"$3\"";
        GitExecutionEnvironment shell = GitExecutionEnvironment.Capture(fixture.Environment.Options with { Executable = "/bin/bash" }, fixture.Environment.EnvironmentVariables);
        Task<int> opening = MacToolsShellLauncher.RunCoreAsync(fixture.Repository, shell,
            ["--noprofile", "--norc", "-m", "-c", "/bin/sh -c \"$1\" child \"$2\" \"$3\" \"$4\" </dev/null >/dev/null 2>&1 & disown; exit 0", "shell", child, ready, release, done],
            CancellationToken.None, terminal.Launch, HelperExecutable(), terminal.CreateStartInfo);
        try
        {
            await opening.WaitAsync(TimeSpan.FromSeconds(20)); await UntilFileAsync(ready);
            await terminal.StoppedAsync(); Assert.That(await File.ReadAllTextAsync(fixture.ExitFile), Is.EqualTo("0"));
            Assert.That(File.Exists(done), Is.False); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release this fixture only");
            await UntilFileAsync(done); await opening.WaitAsync(TimeSpan.FromSeconds(20)); await terminal.StoppedAsync();
        }
    }

    [Test]
    public async Task AFailedNativeExecReportsItsActualFailureAfterThePrivateStartupFilesHaveGone()
    {
        using Fixture fixture = new(); using PosixTestTerminal terminal = new() { ExitCodePath = fixture.ExitFile };
        GitExecutionEnvironment missing = GitExecutionEnvironment.Capture(fixture.Environment.Options with { Executable = Path.Combine(fixture.Root, "missing shell") }, fixture.Environment.EnvironmentVariables);
        await MacToolsShellLauncher.RunCoreAsync(fixture.Repository, missing, [], CancellationToken.None,
            terminal.Launch, HelperExecutable(), terminal.CreateStartInfo).WaitAsync(TimeSpan.FromSeconds(20));
        await terminal.StoppedAsync();
        Assert.That(await File.ReadAllTextAsync(fixture.ExitFile), Is.EqualTo("1"));
        Assert.That(terminal.Output, Does.Contain("Cannot start the selected terminal shell."));
        Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
    }

    private static async Task UntilFileAsync(string path)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!File.Exists(path)) { await Task.Delay(10, timeout.Token); }
    }

    [TestCase("nonce", typeof(InvalidDataException))]
    [TestCase("parent", typeof(IOException))]
    public async Task AnUnauthenticatedHelperCannotStartTheShellAndItsPrivateRequestIsRemoved(string mismatch, Type expectedError)
    {
        using Fixture fixture = new(); using PosixTestTerminal terminal = new() { ExitCodePath = fixture.ExitFile };
        Task opening = MacToolsShellLauncher.RunCoreAsync(fixture.Repository, fixture.Environment,
            ["-c", "printf forbidden > \"$1\"", "shell", fixture.Record], CancellationToken.None, terminal.Launch, HelperExecutable(),
            (executable, arguments) =>
            {
                string requestPath = arguments[1]; ToolsShellRequest request = JsonSerializer.Deserialize<ToolsShellRequest>(File.ReadAllText(requestPath))!;
                File.WriteAllText(requestPath, JsonSerializer.Serialize(mismatch == "nonce" ? request with { Nonce = "wrong nonce" } : request with { ParentProcessId = int.MaxValue }));
                return terminal.CreateStartInfo(executable, arguments);
            });
        Exception? failure = Assert.CatchAsync(async () => await opening.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.That(failure, Is.TypeOf(expectedError));
        await terminal.StoppedAsync();
        Assert.That(File.Exists(fixture.Record), Is.False); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
        Assert.That(await File.ReadAllTextAsync(fixture.ExitFile), Is.EqualTo("1"));
    }

    [Test]
    public void InvalidShellArgumentsAreRefusedBeforeAnyTerminalIsLaunched()
    {
        using Fixture fixture = new(); bool launched = false;
        Assert.CatchAsync<ArgumentException>(async () => await MacToolsShellLauncher.RunCoreAsync(fixture.Repository, fixture.Environment,
            ["bad\0argument"], CancellationToken.None, _ => { launched = true; return null; }));
        Assert.That(launched, Is.False); Assert.That(File.Exists(fixture.Record), Is.False);
    }

    private static string HelperExecutable()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop"))) { root = root.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(path), Is.True); return path;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("tools-shell-'λ-").FullName;
        internal string Repository { get; }
        internal string Record => Path.Combine(Root, "record");
        internal string ExitFile => Path.Combine(Root, "exit");
        internal GitExecutionEnvironment Environment { get; }
        internal Fixture()
        {
            Repository = Path.Combine(Root, "repository [x]* ' λ\nline"); Directory.CreateDirectory(Repository);
            Environment = GitExecutionEnvironment.Capture(new() { Executable = "/bin/sh" }, new Dictionary<string, string?>
            { ["PATH"] = "/usr/bin:/bin", ["HOME"] = Root, ["CAPTURED_SECRET"] = "literal ' $() λ", ["GIT_PAGER"] = "user pager λ", ["LC_ALL"] = "C", ["GIT_TERMINAL_PROMPT"] = "0", ["TERM"] = "captured terminal" });
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
