namespace GitExtensions.Linux.Scripts;

/// <summary>A real successful native start/exec observation, not a command completion.</summary>
public sealed record ScriptProcessStarted(int ProcessId);
public sealed record ScriptProcessResult(int ExitCode, byte[] StandardOutputBytes, byte[] StandardErrorBytes)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Neutral external commands: inherited environment, exact argv, owned cancellation and pipe drainage.</summary>
public sealed class ScriptProcessRunner
{
    public async Task<ScriptProcessResult> RunAsync(string executable, string workingDirectory,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<GitProcessOutput>? outputReceived = null, Action<ScriptProcessStarted>? processStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable); ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        GitBinaryResult result = await new GitRunner(executable).RunExternalBinaryAsync(workingDirectory, arguments,
            cancellationToken, environment, outputReceived, processStarted is null ? null : pid => processStarted(new(pid))).ConfigureAwait(false);
        return new(result.ExitCode, result.StandardOutput, result.StandardErrorBytes);
    }
}
