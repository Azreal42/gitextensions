using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

/// <summary>Runs interactive Git under native process ownership using the caller's terminal I/O.</summary>
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos"), SupportedOSPlatform("windows")]
public static partial class GitInteractiveTerminalProcess
{
    public static async Task<GitResult> RunAsync(string repositoryPath, string gitExecutable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(repositoryPath, gitExecutable, arguments, null, cancellationToken).ConfigureAwait(false);

    public static Task<GitResult> RunAsync(string repositoryPath, GitExecutionEnvironment environment, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(repositoryPath, environment.Executable, arguments, environment, cancellationToken);

    private static async Task<GitResult> RunCoreAsync(string repositoryPath, string gitExecutable, IReadOnlyList<string> arguments,
        GitExecutionEnvironment? environment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        { throw new PlatformNotSupportedException("Interactive Git requires a supported native terminal backend."); }
        if (!Directory.Exists(repositoryPath)) { throw new DirectoryNotFoundException(repositoryPath); }
        ProcessStartInfo start = new(gitExecutable) { WorkingDirectory = repositoryPath, UseShellExecute = false };
        environment?.ApplyTo(start);
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        // Preserve the real user's editor and terminal input; only pagers are
        // disabled. Native argv and exec errors do not pass through a shell.
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "1";
        start.Environment["LC_ALL"] = "C.UTF-8";
        if (OperatingSystem.IsWindows()) { return await RunWindowsAsync(start, cancellationToken).ConfigureAwait(false); }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        { return await RunPosixAsync(start, cancellationToken).ConfigureAwait(false); }
        throw new PlatformNotSupportedException("Interactive Git requires a supported native terminal backend.");
    }

    [SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    private static async Task<GitResult> RunPosixAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        PosixGitProcess process = PosixGitProcess.Start(start, cancellationToken, terminal: true, inheritConsole: true);
        Task exited = process.WaitForExitAsync();
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => cancelled.TrySetResult());
        Exception? failure = null;
        try
        {
            List<Task> pending = [process.Started, process.Control, exited];
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending.Append(cancelled.Task)).ConfigureAwait(false);
                if (completed == cancelled.Task) { cancellationToken.ThrowIfCancellationRequested(); }
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, "", "");
        }
        catch (Exception error)
        {
            failure = error;
            if (await process.StopAsync().ConfigureAwait(false) is Exception cleanup) { error.Data["InteractiveGitStopFailure"] = cleanup; }
            try { await Task.WhenAll(process.Started, process.Control, exited).ConfigureAwait(false); }
            catch { /* Keep the original error after all admitted native operations finish. */ }
            throw;
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception cleanup) when (failure is not null) { failure.Data["InteractiveGitDisposeFailure"] = cleanup; }
        }
    }

}
