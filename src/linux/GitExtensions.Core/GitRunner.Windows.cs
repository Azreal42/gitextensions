using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

public sealed partial class GitRunner
{
    [SupportedOSPlatform("windows")]
    private static async Task<GitBinaryResult> RunWindowsProcessAsync(ProcessStartInfo start, CancellationToken token,
        byte[]? standardInput, Action<GitProcessOutput>? observer, Action<int>? processStarted, GitStreamCapture? capture)
    {
        using WindowsGitProcess process = WindowsGitProcess.Start(start, token);
        using MemoryStream? outputBuffer = capture is null ? new() : null;
        using MemoryStream? errorBuffer = capture is null ? new() : null;
        Task output = capture is null ? CopyOutputAsync(process.StandardOutput, outputBuffer!, GitProcessOutputStream.StandardOutput, observer)
            : capture.CopyStandardOutputAsync(process.StandardOutput, token);
        Task error = capture is null ? CopyOutputAsync(process.StandardError, errorBuffer!, GitProcessOutputStream.StandardError, observer)
            : capture.CopyStandardErrorAsync(process.StandardError);
        Task<IOException?> input = Task.FromResult<IOException?>(null);
        Task exited = process.WaitForExitAsync(), empty = Task.CompletedTask;
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
        try
        {
            processStarted?.Invoke(process.Id);
            input = WriteInputAsync(process.StandardInput, standardInput, token);
            List<Task> pending = [exited, input, output, error];
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending.Append(canceled.Task)).ConfigureAwait(false);
                if (completed == canceled.Task) { token.ThrowIfCancellationRequested(); }
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            empty = process.WaitForEmptyAsync();
            if (await Task.WhenAny(empty, canceled.Task).ConfigureAwait(false) == canceled.Task) { token.ThrowIfCancellationRequested(); }
            await empty.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            IOException? inputError = await input.ConfigureAwait(false);
            if (process.ExitCode == 0 && inputError is not null) { throw inputError; }
            if (capture is not null) { return new(process.ExitCode, [], ""); }
            List<string> warnings = []; byte[] errorBytes = errorBuffer!.ToArray();
            return new(process.ExitCode, outputBuffer!.ToArray(), GitOutputText.Utf8(errorBytes, "stderr", warnings))
            { StandardErrorBytes = errorBytes, Warnings = warnings };
        }
        catch (Exception failure)
        {
            if (await process.StopAsync().ConfigureAwait(false) is Exception stopFailure)
            {
                failure.Data["GitProcessStopFailure"] = stopFailure;
                try { process.CloseAfterStopFailure(); }
                catch (Exception closeFailure) { failure.Data["GitProcessPipeCloseFailure"] = closeFailure; }
            }
            try { await exited.ConfigureAwait(false); }
            catch (Exception waitFailure) { failure.Data["GitProcessWaitFailure"] = waitFailure; }
            try { await Task.WhenAll(input, output, error, empty).ConfigureAwait(false); }
            catch { /* Keep the original failure after the owned job and redirected streams have stopped. */ }
            throw;
        }
    }
}
