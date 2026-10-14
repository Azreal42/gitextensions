namespace GitExtensions.Linux.Desktop;

/// <summary>A next command requested by a completion hook; the process window executes it serially.</summary>
public sealed record GitProcessCommand(string Description,
    Func<CancellationToken, Action<GitProcessOutput>, Task<GitResult>> Execute);

/// <summary>Native outcome and independent pipe snapshots, frozen before any completion hook runs.</summary>
public sealed class GitProcessAttemptResult
{
    private readonly byte[] _stdout, _stderr;

    internal GitProcessAttemptResult(int number, string description, GitProcessDialogResult outcome,
        byte[] stdout, byte[] stderr)
    {
        Number = number; CommandDescription = description; Outcome = outcome;
        _stdout = stdout.ToArray(); _stderr = stderr.ToArray();
    }

    public int Number { get; }
    public string CommandDescription { get; }
    public GitProcessDialogResult Outcome { get; }
    public GitResult? Result => Outcome.Result;
    public Exception? Error => Outcome.Error;
    public bool WasCancelled => Outcome.WasCancelled;
    public bool CommandStarted => Outcome.CommandStarted;
    // Return owned copies: a caller cannot alter a previous attempt through MemoryMarshal.
    public ReadOnlyMemory<byte> StandardOutputBytes => _stdout.ToArray();
    public ReadOnlyMemory<byte> StandardErrorBytes => _stderr.ToArray();
}
