using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[Category("PosixConsoleIntegration"), NonParallelizable, Platform("Linux,MacOsX")]
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class PosixInteractiveConsoleIntegrationTests
{
    [Test]
    public async Task RealHelperKeepsItsOperationUntilTheTerminalIsAcknowledged()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        using CancellationTokenSource cancel = new(); using Terminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["rev-parse", "--verify", "HEAD"], cancel.Token, terminal);
        try
        {
            await terminal.UntilAsync("Press Enter to close.", operation);
            Assert.That(operation.IsCompleted, Is.False);
            await terminal.SendAsync("\n");
            Assert.That((await operation.WaitAsync(TimeSpan.FromSeconds(20))).ExitCode, Is.Zero, terminal.Output);
            await terminal.StoppedAsync(); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            Assert.That((await fixture.GitAsync("status", "--porcelain")).StandardOutput, Is.Empty);
        }
        finally { await FinishAsync(operation, cancel, terminal); }
    }

    [Test]
    public async Task RealHunkInputStagesTheGitIndexAndThenReturnsTheActualResult()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "selected hunk λ\n");
        using CancellationTokenSource cancel = new(); using Terminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["add", "--patch", "--", "file.txt"], cancel.Token, terminal);
        try
        {
            await terminal.UntilAsync("Stage this hunk", operation); await terminal.SendAsync("y\n");
            await terminal.UntilAsync("Press Enter to close.", operation);
            Assert.That(operation.IsCompleted, Is.False);
            await terminal.SendAsync("\n");
            Assert.That((await operation.WaitAsync(TimeSpan.FromSeconds(20))).ExitCode, Is.Zero, terminal.Output);
            await terminal.StoppedAsync(); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("selected hunk λ\n"));
            Assert.That((await fixture.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("base\n"));
        }
        finally { await FinishAsync(operation, cancel, terminal); }
    }

    [Test]
    public async Task CancellingARealHunkDrainsTheHelperBeforeTheIndexLeaseReturns()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "unselected hunk λ\n");
        string index = (await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        using CancellationTokenSource cancel = new(); using Terminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["add", "--patch", "--", "file.txt"], cancel.Token, terminal);
        try
        {
            await terminal.UntilAsync("Stage this hunk", operation); cancel.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(20)));
            await terminal.StoppedAsync(); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            Assert.That((await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("unselected hunk λ\n"));
        }
        finally { await FinishAsync(operation, cancel, terminal); }
    }

    private static Task<GitResult> Run(ReferenceFixture fixture, string[] arguments, CancellationToken token, Terminal terminal)
        => InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
            arguments, token, terminal.Start, HelperExecutable());

    private static async Task FinishAsync(Task<GitResult> operation, CancellationTokenSource cancel, Terminal terminal)
    {
        cancel.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) { }
        finally { await terminal.StoppedAsync(); }
    }

    private static string HelperExecutable()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "GitExtensions"); if (File.Exists(direct)) { return direct; }
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop"))) { root = root.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(path), Is.True); return path;
    }

    private sealed class Terminal : IDisposable
    {
        private Process? _process;
        private Task _output = Task.CompletedTask, _error = Task.CompletedTask;
        private readonly ConcurrentQueue<string> _text = new();
        internal string Output => string.Concat(_text);
        internal string? RequestDirectory { get; private set; }
        internal Process Start(string executable, IReadOnlyList<string> arguments)
        {
            string requestPath = arguments[1]; RequestDirectory = Path.GetDirectoryName(requestPath);
            InteractiveFileConsoleRequest request = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(File.ReadAllText(requestPath))!;
            Assert.That(Encoding.UTF8.GetByteCount(request.SocketPath), Is.LessThanOrEqualTo(OperatingSystem.IsMacOS() ? 103 : 107));
            Assert.That(File.GetUnixFileMode(RequestDirectory!), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            ProcessStartInfo start = new("/usr/bin/script")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            if (OperatingSystem.IsMacOS())
            {
                start.ArgumentList.Add("/dev/null"); start.ArgumentList.Add(executable);
                foreach (string arg in arguments) { start.ArgumentList.Add(arg); }
            }
            else
            {
                start.ArgumentList.Add("-e"); start.ArgumentList.Add("-c");
                start.ArgumentList.Add(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
                start.ArgumentList.Add("/dev/null");
            }
            _process = Process.Start(start)!;
            _output = ReadAsync(_process.StandardOutput); _error = ReadAsync(_process.StandardError);
            return Process.GetProcessById(_process.Id);
        }
        private async Task ReadAsync(StreamReader reader)
        {
            char[] buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer)) != 0) { _text.Enqueue(new string(buffer, 0, count)); }
        }
        internal async Task UntilAsync(string marker, Task operation)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while (!Output.Contains(marker, StringComparison.Ordinal))
            {
                if (operation.IsCompleted) { await operation; Assert.Fail("Console completed before " + marker + ": " + Output); }
                await Task.Delay(10, timeout.Token);
            }
        }
        internal async Task SendAsync(string value)
        { await _process!.StandardInput.WriteAsync(value); await _process.StandardInput.FlushAsync(); }
        internal async Task StoppedAsync()
        {
            if (_process is not null) { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            await Task.WhenAll(_output, _error).WaitAsync(TimeSpan.FromSeconds(20));
        }
        public void Dispose() => _process?.Dispose();
        private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
