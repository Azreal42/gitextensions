using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

// Run this fixture in a new testhost for each TMPDIR. Path.GetTempPath() is process state;
// changing the environment of a running NUnit host would not exercise startup reliably.
[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class InteractiveFileConsoleSocketTests
{
    [Test]
    public async Task RealHelperUsesAPrivateBoundedSocketAndCleansUpAfterSuccess()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        byte[] index = await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index"));
        using CancellationTokenSource cancellation = new();
        using SocketTerminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["rev-parse", "--verify", "HEAD"], cancellation.Token, terminal);
        try
        {
            await terminal.WaitForAsync("Press Enter to close.", operation);
            Assert.That(operation.IsCompleted, Is.False, "The authenticated helper still awaits acknowledgement.");
            terminal.ObservePrivateProcesses();
            await terminal.SendAsync("\n");
            GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(result.ExitCode, Is.Zero, terminal.Output);
            await terminal.AssertStoppedAndCleanedAsync(0);
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index")), Is.EqualTo(index));
        }
        finally { await FinishAsync(operation, cancellation, terminal); }
    }

    [Test]
    public async Task CancellingARealHunkDrainsTheHelperAndLeavesTheIndexUntouched()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "a pending hunk under the startup TMPDIR\n");
        string index = (await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        using CancellationTokenSource cancellation = new();
        using SocketTerminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["add", "--patch", "--", "file.txt"], cancellation.Token, terminal);
        try
        {
            await terminal.WaitForAsync("Stage this hunk", operation);
            terminal.ObservePrivateProcesses();
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(20)));
            await terminal.AssertStoppedAndCleanedAsync(130);
            Assert.That((await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("base\n"));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("a pending hunk under the startup TMPDIR\n"));
        }
        finally { await FinishAsync(operation, cancellation, terminal); }
    }

    [Test]
    public async Task CancellingAfterGitCompletedPreservesItsResultAndRemovesTheSocket()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        using CancellationTokenSource cancellation = new();
        using SocketTerminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["rev-parse", "--verify", "HEAD"], cancellation.Token, terminal);
        try
        {
            await terminal.WaitForAsync("Press Enter to close.", operation);
            terminal.ObservePrivateProcesses();
            cancellation.Cancel();
            GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(result.ExitCode, Is.Zero, terminal.Output);
            Assert.That(terminal.Output, Does.Contain("Console closed after Git completed."));
            await terminal.AssertStoppedAndCleanedAsync(0);
        }
        finally { await FinishAsync(operation, cancellation, terminal); }
    }

    [Test]
    public async Task TerminalStartFailureCleansTheBoundSocketAndPrivateRequest()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string? directory = null;
        IOException expected = new("Transport failed before starting a helper.");
        Exception? actual = null;
        try
        {
            await InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
                ["status", "--porcelain"], CancellationToken.None, (_, arguments) =>
                {
                    directory = InspectRequest(arguments);
                    throw expected;
                }, HelperExecutable());
        }
        catch (Exception exception) { actual = exception; }
        Assert.That(actual, Is.SameAs(expected));
        Assert.That(directory, Is.Not.Null, "The socket must have bound before terminal startup was attempted.");
        Assert.That(Directory.Exists(directory), Is.False);
        Assert.That((await fixture.GitAsync("status", "--porcelain")).StandardOutput, Is.Empty);
    }

    private static Task<GitResult> Run(ReferenceFixture fixture, IReadOnlyList<string> arguments,
        CancellationToken token, SocketTerminal terminal)
        => InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
            arguments, token, terminal.Start, HelperExecutable());

    private static async Task FinishAsync(Task<GitResult> operation, CancellationTokenSource cancellation, SocketTerminal terminal)
    {
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) { }
        finally { await terminal.WaitForExitAsync(); }
    }

    private static string InspectRequest(IReadOnlyList<string> arguments)
    {
        Assert.That(arguments, Has.Count.EqualTo(2));
        Assert.That(arguments[0], Is.EqualTo(InteractiveFileConsoleProtocol.Mode));
        string requestPath = arguments[1];
        string directory = Path.GetDirectoryName(requestPath)!;
        InteractiveFileConsoleRequest request = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(File.ReadAllText(requestPath))!;
        Assert.That(request.SocketPath, Is.EqualTo(Path.Combine(directory, "control")));
        Assert.That(Encoding.UTF8.GetByteCount(request.SocketPath), Is.LessThanOrEqualTo(107));
        Assert.That(File.GetUnixFileMode(directory), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        Assert.That(File.GetUnixFileMode(requestPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(File.GetUnixFileMode(request.SocketPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(new DirectoryInfo(directory).LinkTarget, Is.Null);
        Assert.That(new FileInfo(requestPath).LinkTarget, Is.Null);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        // The fixture is invoked with an ordinary short TMPDIR or an intentionally overlong one.
        // Both cases must exercise the launcher, not mutate the testhost's environment in flight.
        bool longTemp = Encoding.UTF8.GetByteCount(Path.Combine(temp, Path.GetFileName(directory), "control")) > 107;
        Assert.That(Path.GetDirectoryName(directory), Is.EqualTo(longTemp ? "/tmp" : temp));
        Assert.That(Path.GetFileName(directory), Does.StartWith("ge-interactive-"));
        TestContext.Out.WriteLine(JsonSerializer.Serialize(new { temp, directory, socket = request.SocketPath, longTemp }));
        return directory;
    }

    private static string HelperExecutable()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "GitExtensions");
        if (File.Exists(direct)) { return direct; }
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src/linux/GitExtensions.Desktop"))) { directory = directory.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.Combine(directory?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(executable), Is.True, "Build the real Desktop apphost before these console tests.");
        return executable;
    }

    private sealed record ProcessIdentity(int Pid, long StartTicks, int Parent, string State, string? Executable, string[] Arguments);

    private sealed class SocketTerminal : IDisposable
    {
        private readonly StringBuilder _output = new();
        private readonly Dictionary<int, ProcessIdentity> _identities = [];
        private Task _read = Task.CompletedTask;
        private Process? _terminal;
        private string? _directory;
        private string? _helperExecutable;
        internal string Output { get { lock (_output) { return _output.ToString(); } } }

        internal Process Start(string executable, IReadOnlyList<string> arguments)
        {
            _directory = InspectRequest(arguments);
            _helperExecutable = Path.GetFullPath(executable);
            string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            ProcessStartInfo start = new("/usr/bin/script")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "--quiet", "--return", "--flush", "--command", string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)), "/dev/null" })
            { start.ArgumentList.Add(argument); }
            start.Environment["TERM"] = "xterm-256color";
            start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            _terminal = Process.Start(start)!;
            ProcessIdentity? identity = Identity(_terminal.Id);
            if (identity is not null) { _identities.Add(identity.Pid, identity); }
            _read = Task.WhenAll(ReadAsync(_terminal.StandardOutput), ReadAsync(_terminal.StandardError));
            return Process.GetProcessById(_terminal.Id);
        }

        private async Task ReadAsync(StreamReader reader)
        {
            char[] buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            { lock (_output) { _output.Append(buffer, 0, count); } }
        }

        internal async Task WaitForAsync(string text, Task<GitResult> operation)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!Output.Contains(text, StringComparison.Ordinal) && timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (operation.IsCompleted) { await operation; break; }
                if (_terminal?.HasExited == true) { break; }
                await Task.Delay(10);
            }
            Assert.That(Output, Does.Contain(text));
        }

        internal async Task SendAsync(string text)
        { await _terminal!.StandardInput.WriteAsync(text); await _terminal.StandardInput.FlushAsync(); }

        internal void ObservePrivateProcesses()
        {
            ProcessIdentity[] processes = Directory.EnumerateDirectories("/proc").Select(path => int.TryParse(Path.GetFileName(path), out int pid) ? Identity(pid) : null)
                .OfType<ProcessIdentity>().ToArray();
            bool added;
            do
            {
                added = false;
                foreach (ProcessIdentity identity in processes)
                {
                    if (_identities.ContainsKey(identity.Parent) && _identities.TryAdd(identity.Pid, identity)) { added = true; }
                }
            } while (added);
            Assert.That(_identities.Values.Any(identity => identity.Executable == _helperExecutable
                && identity.Arguments.Contains(InteractiveFileConsoleProtocol.Mode)
                && identity.Arguments.Contains(Path.Combine(_directory!, "request.json"))), Is.True,
                "The observed descendants must include this real apphost and its exact private request, not only script and a shell.");
            TestContext.Out.WriteLine("private-console-processes: " + JsonSerializer.Serialize(_identities.Values));
        }

        internal async Task WaitForExitAsync()
        {
            if (_terminal is null) { return; }
            await _terminal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await _read.WaitAsync(TimeSpan.FromSeconds(20));
        }

        internal async Task AssertStoppedAndCleanedAsync(int expectedExit)
        {
            await WaitForExitAsync();
            Assert.That(_terminal!.ExitCode, Is.EqualTo(expectedExit), Output);
            foreach (ProcessIdentity identity in _identities.Values)
            {
                ProcessIdentity? current = Identity(identity.Pid);
                bool absent = current is null || current.StartTicks != identity.StartTicks;
                Assert.That(absent || current!.State == "Z", Is.True, "Private PID/start still running: " + JsonSerializer.Serialize(current));
                TestContext.Out.WriteLine(JsonSerializer.Serialize(new { identity.Pid, identity.StartTicks, stopped = true, absent, state = absent ? null : current!.State }));
            }
            Assert.That(_directory, Is.Not.Null);
            Assert.That(Directory.Exists(_directory), Is.False, "Request, socket and private directory must be removed after actual process exit.");
            TestContext.Out.WriteLine("private-console-exit: " + expectedExit);
        }

        private static ProcessIdentity? Identity(int pid)
        {
            try
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                string[] arguments = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
                string? executable = new FileInfo($"/proc/{pid}/exe").LinkTarget;
                return new(pid, long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), fields[0], executable, arguments);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Dispose()
        {
            TestContext.Out.WriteLine(Output);
            _terminal?.Dispose();
        }
    }
}
