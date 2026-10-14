namespace GitExtensions.Linux;

public enum GitHistoryRemovalStep
{
    RewriteHistory,
    EnumerateBackupReferences,
    DeleteBackupReference,
    ExpireReflogs,
    GarbageCollect,
}

/// <summary>A native command and its actual outcome. Cancellation or launch failure has no invented exit code.</summary>
public sealed record GitHistoryRemovalStepResult(GitHistoryRemovalStep Step, IReadOnlyList<string> Arguments,
    GitResult? Result, bool Cancelled, string? Error)
{
    public int? ExitCode => Result?.ExitCode;
    public string StandardOutput => Result?.StandardOutput ?? PartialStandardOutput;
    public string StandardError => Result?.StandardError ?? PartialStandardError;
    /// <summary>Output witnessed before an interrupted command; retained even when its exit cannot be reported.</summary>
    public string PartialStandardOutput { get; init; } = "";
    public string PartialStandardError { get; init; } = "";
    public byte[] StandardOutputBytes => Result?.StandardOutputBytes ?? PartialStandardOutputBytes;
    public byte[] StandardErrorBytes => Result?.StandardErrorBytes ?? PartialStandardErrorBytes;
    public byte[] PartialStandardOutputBytes { get; init; } = [];
    public byte[] PartialStandardErrorBytes { get; init; } = [];
    public bool Success => !Cancelled && Error is null && Result?.Success == true;
}

/// <summary>The host confirms rewriting branch history and immediately expiring backups/reflogs and pruning objects.</summary>
public sealed record GitHistoryRemovalResult(IReadOnlyList<string> Paths, IReadOnlyList<GitHistoryRemovalStepResult> Steps,
    bool MutationStarted, bool Cancelled, string? Error)
{
    public bool Success => !Cancelled && Error is null && Steps.Count != 0
        && Steps[^1].Step == GitHistoryRemovalStep.GarbageCollect && Steps.All(step => step.Success);
    public IReadOnlyList<string> Errors
    {
        get
        {
            List<string> errors = [];
            if (Error is not null) { errors.Add(Error); }
            errors.AddRange(Steps.Where(step => !step.Success && !step.Cancelled).Select(step => step.Error ?? step.StandardError));
            return errors.ToArray();
        }
    }
}
