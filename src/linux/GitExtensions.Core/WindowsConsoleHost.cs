using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

/// <summary>A newly opened console host owned by a kernel job before any helper code runs.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConsoleHost : IAsyncDisposable
{
    private readonly WindowsGitProcess _process;
    private WindowsConsoleHost(WindowsGitProcess process) => _process = process;
    public int ProcessId => _process.Id;

    public static WindowsConsoleHost Start(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, GitExecutionEnvironment environment, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("A Windows console requires Windows."); }
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo start = new(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        environment.ApplyTo(start);
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        return new(WindowsGitProcess.Start(start, token, createConsole: true));
    }

    public Task WaitForRootExitAsync() => _process.WaitForExitAsync();
    public Task WaitForEmptyAsync() => _process.WaitForEmptyAsync();
    public int ExitCode => _process.ExitCode;
    public async Task StopAsync()
    {
        if (await _process.StopAsync().ConfigureAwait(false) is Exception error) { throw error; }
        await _process.WaitForExitAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { _process.Dispose(); }
    }
}
