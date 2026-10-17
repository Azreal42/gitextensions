using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

/// <summary>Runs Git with a genuine private controlling terminal while inheriting the caller's terminal I/O.</summary>
[SupportedOSPlatform("linux")]
public static class GitInteractiveTerminalProcess
{
    public static async Task<GitResult> RunAsync(string repositoryPath, string gitExecutable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        const string script = "/usr/bin/script";
        if (!File.Exists(script)) { throw new FileNotFoundException("Interactive Git requires the util-linux script executable.", script); }
        if (!Directory.Exists(repositoryPath)) { throw new DirectoryNotFoundException(repositoryPath); }
        string command = string.Join(" ", new[] { gitExecutable }.Concat(arguments).Select(Quote));
        ProcessStartInfo start = new(script) { WorkingDirectory = repositoryPath, UseShellExecute = false };
        foreach (string value in new[] { "--quiet", "--return", "--flush", "--command", command, "/dev/null" }) { start.ArgumentList.Add(value); }
        // script allocates a new PTY/controlling terminal after the private exec helper creates its session.
        // Do not apply the noninteractive runner's GIT_EDITOR=true or close stdin: 'e' must launch the real editor.
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "1";
        start.Environment["LC_ALL"] = "C.UTF-8";
        using GitProcessLaunch launch = GitProcessLaunch.Configure(start);
        using GitProcessGroup group = new();
        using Process process = new() { StartInfo = start };
        process.Start();
        Task control = Task.CompletedTask;
        Task exited = process.WaitForExitAsync(CancellationToken.None);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => cancelled.TrySetResult());
        try
        {
            group.CaptureStartedChild(process);
            control = launch.ObserveExecAsync(process.Id, group, cancellationToken);
            List<Task> pending = [control, exited];
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending.Append(cancelled.Task)).ConfigureAwait(false);
                if (completed == cancelled.Task) { cancellationToken.ThrowIfCancellationRequested(); }
                await completed.ConfigureAwait(false); pending.Remove(completed);
                if (completed == exited) { launch.ParentExited(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, "", "");
        }
        finally
        {
            launch.CancelUnacknowledgedHandshake();
            Exception? cleanup = await group.StopAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            launch.ParentExited();
            try { await control.ConfigureAwait(false); }
            catch when (cancellationToken.IsCancellationRequested || cleanup is not null) { }
            if (cleanup is not null) { throw new IOException("The private interactive Git process could not be drained.", cleanup); }
        }
    }

    // Every argument, including executable and hostile filenames, is one POSIX shell word.
    internal static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) { throw new ArgumentException("Interactive Git arguments cannot contain NUL.", nameof(value)); }
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
