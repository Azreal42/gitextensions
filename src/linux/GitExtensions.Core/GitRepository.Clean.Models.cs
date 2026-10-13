using GitCommands;

namespace GitExtensions.Linux;

/// <summary>FormCleanupRepository's options. A disabled path filter is represented by an empty list.</summary>
public sealed record GitCleanOptions(CleanMode Mode = CleanMode.All, bool RemoveDirectories = true, bool CleanSubmodules = false)
{
    /// <summary>Raw nonempty textbox lines; no trimming, shell quoting or wildcard expansion by the caller.</summary>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];
    /// <summary>Raw textbox lines. Core converts ASCII spaces to ? and the native directory separator to /; Linux backslashes remain pattern data.</summary>
    public IReadOnlyList<string> ExcludePaths { get; init; } = [];
}

public enum GitCleanCommandScope { Worktree, Submodules }

/// <summary>Each completed native command retains its actual result. Interrupted/unlaunchable commands have no invented exit or output.</summary>
public sealed record GitCleanCommandResult(GitCleanCommandScope Scope, IReadOnlyList<string> Arguments,
    GitResult? Result, bool Cancelled, string? Error)
{
    public int? ExitCode => Result?.ExitCode;
    public string? StandardOutput => Result?.StandardOutput;
    public string? StandardError => Result?.StandardError;
    public bool Success => !Cancelled && Error is null && Result?.Success == true;
}

public sealed record GitCleanResult(bool IsPreview, IReadOnlyList<GitCleanCommandResult> Commands, bool MutationStarted, bool Cancelled)
{
    public bool Success => !Cancelled && Commands.Count != 0 && Commands.All(command => command.Success);
}
