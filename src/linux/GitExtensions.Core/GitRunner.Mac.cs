using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

public sealed partial class GitRunner
{
    [SupportedOSPlatform("macos")]
    private static async Task<GitBinaryResult> RunMacProcessAsync(ProcessStartInfo start, CancellationToken token,
        byte[]? standardInput, Action<GitProcessOutput>? observer, Action<int>? processStarted, GitStreamCapture? capture)
    {
        MacGitProcess process = MacGitProcess.Start(start, token); Exception? failure = null;
        using MemoryStream? outputBuffer = capture is null ? new() : null, errorBuffer = capture is null ? new() : null;
        Task output = capture is null ? CopyOutputAsync(process.StandardOutput, outputBuffer!, GitProcessOutputStream.StandardOutput, observer)
            : capture.CopyStandardOutputAsync(process.StandardOutput, token);
        Task error = capture is null ? CopyOutputAsync(process.StandardError, errorBuffer!, GitProcessOutputStream.StandardError, observer)
            : capture.CopyStandardErrorAsync(process.StandardError);
        Task<IOException?> input = Task.FromResult<IOException?>(null);
        Task exited = process.WaitForExitAsync(), control = process.Control;
        Task started = NotifyMacStartedAsync(process, processStarted, token);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
        try
        {
            input = WriteInputAsync(process.StandardInput, standardInput, token);
            List<Task> pending = [started, control, exited, input, output, error];
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending.Append(canceled.Task)).ConfigureAwait(false);
                if (completed == canceled.Task) { token.ThrowIfCancellationRequested(); }
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            token.ThrowIfCancellationRequested();
            IOException? inputError = await input.ConfigureAwait(false);
            if (process.ExitCode == 0 && inputError is not null) { throw inputError; }
            if (capture is not null) { return new(process.ExitCode, [], ""); }
            List<string> warnings = []; byte[] errorBytes = errorBuffer!.ToArray();
            return new(process.ExitCode, outputBuffer!.ToArray(), GitOutputText.Utf8(errorBytes, "stderr", warnings))
            { StandardErrorBytes = errorBytes, Warnings = warnings };
        }
        catch (Exception initial)
        {
            failure = initial;
            if (await process.StopAsync().ConfigureAwait(false) is Exception stopFailure)
            {
                failure.Data["GitProcessStopFailure"] = stopFailure;
                try { process.CloseAfterStopFailure(); }
                catch (Exception closeFailure) { failure.Data["GitProcessPipeCloseFailure"] = closeFailure; }
            }
            try { await exited.ConfigureAwait(false); }
            catch (Exception waitFailure) { failure.Data["GitProcessWaitFailure"] = waitFailure; }
            try { await Task.WhenAll(started, control, input, output, error).ConfigureAwait(false); }
            catch { /* The initial failure survives the actual supervisor wait and admitted I/O. */ }
            throw;
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception cleanup) when (failure is not null) { failure.Data["GitProcessDisposeFailure"] = cleanup; }
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task NotifyMacStartedAsync(MacGitProcess process, Action<int>? callback, CancellationToken token)
    {
        int processId = await process.Started.ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); callback?.Invoke(processId);
    }
}
