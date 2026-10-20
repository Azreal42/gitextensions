using System.Diagnostics;

namespace GitExtensions.Linux;

/// <summary>Bounded, process-local diagnostics. Arguments, environment, stdin and output are never retained.</summary>
public static class GitCommandLog
{
    private static readonly object Gate = new();
    private static readonly LinkedList<GitCommandLogEntry> Entries = new();
    private static readonly Dictionary<long, LinkedListNode<GitCommandLogEntry>> ActiveEntries = new();
    private static long _nextId;
    public const int Capacity = 500;
    private static GitCommandLogCapturePolicy _capturePolicy = new(false);
    public static GitCommandLogCapturePolicy CapturePolicy => Volatile.Read(ref _capturePolicy);
    public static void ConfigureCapture(GitCommandLogCapturePolicy policy)
    { ArgumentNullException.ThrowIfNull(policy); Volatile.Write(ref _capturePolicy, policy); }
    internal static GitCommandLogLaunchContext CaptureLaunchContext() => CapturePolicy.Capture();
    public static GitCommandLogProcessContext CaptureProcessContext() => new(CaptureLaunchContext());

    public static GitCommandLogProcessOperation StartProcess(GitCommandLogProcessKind kind, string directory,
        GitCommandLogProcessContext? context = null)
    {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        ArgumentNullException.ThrowIfNull(directory);
        if (directory.Contains('\0')) { throw new ArgumentException("A process directory cannot contain NUL.", nameof(directory)); }
        string command = kind switch
        {
            GitCommandLogProcessKind.UserScript => "script", GitCommandLogProcessKind.Build => "build",
            GitCommandLogProcessKind.GitGui => "git gui", GitCommandLogProcessKind.Gitk => "gitk",
            GitCommandLogProcessKind.OpenResource => "open resource", GitCommandLogProcessKind.Browse => "browse",
            GitCommandLogProcessKind.TerminalLauncher => "terminal launcher", GitCommandLogProcessKind.InteractiveConsole => "interactive console host",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        GitCommandLogLaunchContext capture = context?.Value ?? CaptureLaunchContext();
        Stopwatch elapsed = Stopwatch.StartNew();
        return new(BeginCommand(command, directory, DateTimeOffset.Now, capture), elapsed);
    }

    private static readonly HashSet<string> Verbs = new(["add", "am", "apply", "archive", "bisect", "blame", "branch", "cat-file", "check-attr", "check-ignore", "check-mailmap", "check-ref-format", "checkout", "cherry-pick", "clean", "clone", "commit", "config", "count-objects", "describe", "diff", "diff-tree", "difftool", "fetch", "for-each-ref", "format-patch", "fsck", "gc", "grep", "hash-object", "init", "log", "ls-files", "ls-remote", "ls-tree", "merge", "merge-base", "mergetool", "notes", "pull", "push", "range-diff", "rebase", "reflog", "remote", "reset", "restore", "rev-list", "rev-parse", "revert", "rm", "show", "show-ref", "stash", "status", "submodule", "switch", "symbolic-ref", "tag", "update-index", "update-ref", "version", "worktree"], StringComparer.Ordinal);

    /// <summary>A metadata change is available. Observers run outside the log lock; UI consumers marshal themselves.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<GitCommandLogEntry> Snapshot()
    {
        lock (Gate) { return Array.AsReadOnly(Entries.ToArray()); }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            if (Entries.Count == 0) { return; }
            Entries.Clear(); ActiveEntries.Clear();
        }
        NotifyChanged();
    }

    internal static long Begin(IReadOnlyList<string> arguments, string directory, DateTimeOffset started)
        => BeginWithContext(arguments, directory, started, default);

    internal static long BeginWithContext(IReadOnlyList<string> arguments, string directory, DateTimeOffset started, GitCommandLogLaunchContext context)
    {
        // Git's global options may precede the verb. Only keep a recognized verb,
        // never a positional argument which could contain a credential or key.
        string command = "git";
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is "-c" or "--git-dir" or "--work-tree" or "--namespace" or "-C" or "--config-env") { index++; continue; }
            if (argument.StartsWith('-')) { continue; }
            if (Verbs.Contains(argument)) { command += " " + argument; }
            break;
        }

        return BeginCommand(command, directory, started, context);
    }

    private static long BeginCommand(string command, string directory, DateTimeOffset started, GitCommandLogLaunchContext context)
    {
        long id;
        lock (Gate)
        {
            id = ++_nextId;
            GitCommandLogEntry entry = new(command, directory, started, TimeSpan.Zero, null, false)
            { Id = id, IsRunning = true, CallStack = context.CallStack, IsOnMainThread = context.IsOnMainThread };
            ActiveEntries.Add(id, Entries.AddLast(entry));
            if (Entries.Count > Capacity)
            {
                long removed = Entries.First!.Value.Id;
                Entries.RemoveFirst(); ActiveEntries.Remove(removed);
            }
        }
        NotifyChanged();
        return id;
    }

    internal static void SetProcessId(long id, int processId)
    {
        lock (Gate)
        {
            if (processId <= 0 || !ActiveEntries.TryGetValue(id, out LinkedListNode<GitCommandLogEntry>? node)
                || !node.Value.IsRunning || node.Value.ProcessId is not null) { return; }
            node.Value = node.Value with { ProcessId = processId };
        }
        NotifyChanged();
    }

    internal static void Complete(long id, TimeSpan elapsed, int? exitCode, bool cancelled)
        => CompleteEntry(id, elapsed, exitCode, cancelled, unobserved: false);

    internal static void CompleteUnobserved(long id, TimeSpan elapsed)
        => CompleteEntry(id, elapsed, null, cancelled: false, unobserved: true);

    private static void CompleteEntry(long id, TimeSpan elapsed, int? exitCode, bool cancelled, bool unobserved)
    {
        lock (Gate)
        {
            if (!ActiveEntries.Remove(id, out LinkedListNode<GitCommandLogEntry>? node)) { return; }
            node.Value = node.Value with { Duration = elapsed, ExitCode = exitCode, Cancelled = cancelled, IsRunning = false, IsUnobserved = unobserved };
        }
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        Action? observers = Changed;
        if (observers is null) { return; }
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception) { /* Diagnostics must never change the Git operation or retain observer faults. */ }
        }
    }
}

public sealed record GitCommandLogEntry(string Command, string Directory, DateTimeOffset Started, TimeSpan Duration, int? ExitCode, bool Cancelled)
{
    public long Id { get; init; }
    public int? ProcessId { get; init; }
    public bool IsRunning { get; init; }
    public bool IsUnobserved { get; init; }
    public string? CallStack { get; init; }
    public bool? IsOnMainThread { get; init; }
}
