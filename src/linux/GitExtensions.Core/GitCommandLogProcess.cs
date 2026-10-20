using System.Diagnostics;

namespace GitExtensions.Linux;

/// <summary>Application-selected origins, never an executable name or caller-provided command text.</summary>
public enum GitCommandLogProcessKind { UserScript, Build, GitGui, Gitk, OpenResource, Browse, TerminalLauncher, InteractiveConsole }

/// <summary>An immutable caller capture which may cross an await before a validated launch.</summary>
public sealed class GitCommandLogProcessContext
{
    internal GitCommandLogProcessContext(GitCommandLogLaunchContext value) => Value = value;
    internal GitCommandLogLaunchContext Value { get; }
}

/// <summary>One admitted launch. Completing diagnostics does not control or terminate its process.</summary>
public sealed class GitCommandLogProcessOperation
{
    private readonly long _id;
    private readonly Stopwatch _elapsed;
    internal GitCommandLogProcessOperation(long id, Stopwatch elapsed) { _id = id; _elapsed = elapsed; }
    public void SetProcessId(int processId) => GitCommandLog.SetProcessId(_id, processId);
    public void Complete(int? exitCode, bool cancelled = false)
        => GitCommandLog.Complete(_id, _elapsed.Elapsed, exitCode, cancelled);
    public void CompleteUnobserved() => GitCommandLog.CompleteUnobserved(_id, _elapsed.Elapsed);
}
