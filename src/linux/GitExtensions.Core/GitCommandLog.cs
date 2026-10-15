namespace GitExtensions.Linux;

/// <summary>Bounded, process-local diagnostics. Arguments, environment, stdin and output are never retained.</summary>
public static class GitCommandLog
{
    private static readonly object Gate = new();
    private static readonly Queue<GitCommandLogEntry> Entries = new();
    public const int Capacity = 500;
    private static readonly HashSet<string> Verbs = new(["add", "am", "apply", "archive", "bisect", "blame", "branch", "cat-file", "check-attr", "check-ignore", "check-mailmap", "check-ref-format", "checkout", "cherry-pick", "clean", "clone", "commit", "config", "count-objects", "describe", "diff", "difftool", "fetch", "for-each-ref", "format-patch", "fsck", "gc", "grep", "hash-object", "init", "log", "ls-files", "ls-remote", "ls-tree", "merge", "merge-base", "mergetool", "notes", "pull", "push", "range-diff", "rebase", "reflog", "remote", "reset", "restore", "rev-list", "rev-parse", "revert", "rm", "show", "show-ref", "stash", "status", "submodule", "switch", "symbolic-ref", "tag", "update-index", "update-ref", "version", "worktree"], StringComparer.Ordinal);

    public static IReadOnlyList<GitCommandLogEntry> Snapshot()
    {
        lock (Gate) { return Entries.ToArray(); }
    }

    public static void Clear() { lock (Gate) { Entries.Clear(); } }

    internal static void Record(IReadOnlyList<string> arguments, string directory, DateTimeOffset started, TimeSpan elapsed, int? exitCode, bool cancelled)
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
        lock (Gate)
        {
            while (Entries.Count >= Capacity) { Entries.Dequeue(); }
            Entries.Enqueue(new(command, directory, started, elapsed, exitCode, cancelled));
        }
    }
}

public sealed record GitCommandLogEntry(string Command, string Directory, DateTimeOffset Started, TimeSpan Duration, int? ExitCode, bool Cancelled);
