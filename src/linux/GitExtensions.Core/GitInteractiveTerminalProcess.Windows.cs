using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

public static partial class GitInteractiveTerminalProcess
{
    [SupportedOSPlatform("windows")]
    private static async Task<GitResult> RunWindowsAsync(ProcessStartInfo start, CancellationToken token)
    {
        using WindowsConsoleModes modes = WindowsConsoleModes.Capture();
        WindowsGitProcess process = WindowsGitProcess.Start(start, token, inheritConsole: true);
        Task exited = process.WaitForExitAsync(), empty = process.WaitForEmptyAsync();
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
        Exception? failure = null;
        try
        {
            List<Task> pending = [exited, empty];
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending.Append(cancelled.Task)).ConfigureAwait(false);
                if (completed == cancelled.Task) { token.ThrowIfCancellationRequested(); }
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            token.ThrowIfCancellationRequested(); return new(process.ExitCode, "", "");
        }
        catch (Exception error)
        {
            failure = error;
            if (await process.StopAsync().ConfigureAwait(false) is Exception cleanup) { error.Data["InteractiveGitStopFailure"] = cleanup; }
            try { await Task.WhenAll(exited, empty).ConfigureAwait(false); }
            catch { /* Preserve the original failure after joining admitted observations. */ }
            throw;
        }
        finally
        {
            Exception? cleanupFailure = modes.Restore();
            try { process.Dispose(); }
            catch (Exception cleanup)
            { cleanupFailure = cleanupFailure is null ? cleanup : new AggregateException(cleanupFailure, cleanup); }
            if (cleanupFailure is not null)
            {
                if (failure is not null) { failure.Data["InteractiveGitConsoleCleanupFailure"] = cleanupFailure; }
                else { throw cleanupFailure; }
            }
        }
    }
}
