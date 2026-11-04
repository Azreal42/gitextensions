using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
internal sealed class PosixTestTerminal : IDisposable
{
    private Process? _process;
    private Task _output = Task.CompletedTask, _error = Task.CompletedTask;
    private readonly ConcurrentQueue<string> _text = new();
    internal string Output => string.Concat(_text);
    internal string? RequestDirectory { get; private set; }
    internal string? ExitCodePath { get; init; }
    internal Process Start(string executable, IReadOnlyList<string> arguments)
        => Launch(CreateStartInfo(executable, arguments));

    internal ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        string requestPath = arguments[1]; RequestDirectory = Path.GetDirectoryName(requestPath);
        using JsonDocument request = JsonDocument.Parse(File.ReadAllText(requestPath));
        Assert.That(Encoding.UTF8.GetByteCount(request.RootElement.GetProperty("SocketPath").GetString()!), Is.LessThanOrEqualTo(OperatingSystem.IsMacOS() ? 103 : 107));
        Assert.That(File.GetUnixFileMode(RequestDirectory!), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        Assert.That(File.GetUnixFileMode(requestPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        if (ExitCodePath is not null)
        {
            string command = string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
            command += "; result=$?; printf '%s' \"$result\" > " + Quote(ExitCodePath) + "; exit \"$result\"";
            executable = "/bin/sh"; arguments = ["-c", command];
        }
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
        return start;
    }
    internal Process Launch(ProcessStartInfo start)
    {
        _process = Process.Start(start)!;
        _output = ReadAsync(_process.StandardOutput); _error = ReadAsync(_process.StandardError);
        return Process.GetProcessById(_process.Id);
    }
    private async Task ReadAsync(StreamReader reader)
    {
        char[] buffer = new char[1024]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0) { _text.Enqueue(new string(buffer, 0, count)); }
    }
    internal async Task UntilAsync(string marker, Task? operation = null)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!Output.Contains(marker, StringComparison.Ordinal))
        {
            if (operation?.IsCompleted == true) { await operation; Assert.Fail("Console completed before " + marker + ": " + Output); }
            if (_process?.HasExited == true)
            {
                await Task.WhenAll(_output, _error); Assert.That(Output, Does.Contain(marker)); return;
            }
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
