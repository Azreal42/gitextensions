using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace GitExtensions.Linux.Desktop;

[SupportedOSPlatform("windows")]
internal static class WindowsInteractiveFileConsoleLauncher
{
    internal static async Task<GitResult> RunCoreAsync(string repositoryPath, string gitExecutable,
        IReadOnlyList<string> arguments, CancellationToken token, string? helperExecutable,
        GitExecutionEnvironment? environment, GitCommandLogProcessContext? journalContext)
    {
        token.ThrowIfCancellationRequested();
        ConsoleStyleTerminal.ValidateNativeSelection();
        environment ??= GitExecutionEnvironment.Capture(new() { Executable = gitExecutable });
        if (environment.Executable != gitExecutable) { throw new ArgumentException("Interactive Git must use its captured executable.", nameof(environment)); }
        string executable = helperExecutable ?? Path.Combine(AppContext.BaseDirectory, "GitExtensions.exe");
        if (!File.Exists(executable)) { throw new FileNotFoundException("The interactive console application is unavailable.", executable); }
        string name = "gitextensions-console-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        InteractiveFileConsoleRequest request = new(name, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Path.GetFullPath(repositoryPath), gitExecutable, arguments.ToArray())
        { ExecutionEnvironment = InteractiveFileConsoleEnvironment.Capture(environment) };
        GitCommandLogProcessOperation journal = GitCommandLog.StartProcess(GitCommandLogProcessKind.InteractiveConsole,
            repositoryPath, journalContext ?? GitCommandLog.CaptureProcessContext());
        WindowsConsoleHost? host = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        Task<InteractiveFileConsoleResult>? resultRead = null;
        bool authenticated = false, reported = false, drained = false;
        Exception? failure = null;
        try
        {
            host = WindowsConsoleHost.Start(executable,
                [InteractiveFileConsoleProtocol.Mode, "--pipe", name, request.Nonce,
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], repositoryPath, environment, token);
            Task rootExit = host.WaitForRootExitAsync();
            using CancellationTokenSource opening = CancellationTokenSource.CreateLinkedTokenSource(token);
            opening.CancelAfter(TimeSpan.FromSeconds(30));
            Task connected = pipe.WaitForConnectionAsync(opening.Token);
            if (await Task.WhenAny(connected, rootExit) == rootExit && !connected.IsCompleted)
            {
                opening.Cancel();
                try { await connected; } catch (OperationCanceledException) when (opening.IsCancellationRequested) { }
                throw new IOException("The interactive console exited before opening its private channel.");
            }
            await connected;
            WindowsInteractiveFileConsoleProtocol.AuthenticateClient(pipe.SafePipeHandle, host.ProcessId);
            reader = new(pipe, Encoding.UTF8, false, leaveOpen: true);
            writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await WindowsInteractiveFileConsoleProtocol.WriteAsync(writer, request, opening.Token);
            InteractiveFileConsoleHello hello = await InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleHello>(reader, opening.Token);
            if (!WindowsInteractiveFileConsoleProtocol.SameNonce(hello.Nonce, request.Nonce))
            { throw new InvalidDataException("The interactive console handshake did not match this operation."); }
            authenticated = true; journal.SetProcessId(host.ProcessId);
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("ACK".AsMemory(), opening.Token);
            resultRead = InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleResult>(reader);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(resultRead, cancelled.Task) == cancelled.Task)
            { await writer.WriteLineAsync("CANCEL"); }
            InteractiveFileConsoleResult result = await resultRead;
            await rootExit; await host.WaitForEmptyAsync(); drained = true;
            reported = true;
            journal.Complete(result.Cancelled || result.Error is not null ? null : result.ExitCode, result.Cancelled);
            if (result.Cancelled) { throw new OperationCanceledException("Interactive Git cancelled after the owned processes stopped.", token); }
            if (result.Error is not null) { throw new IOException(result.Error); }
            return new(result.ExitCode, "", result.ExitCode == 0 ? "" : "Interactive Git exited with code " + result.ExitCode + ". See the console output.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                if (host is not null) { await host.DisposeAsync(); drained = true; }
            }
            catch (Exception cleanup) when (failure is not null) { failure.Data["InteractiveConsoleHostStopFailure"] = cleanup; }
            finally
            {
                if (!reported)
                {
                    if (host is null) { journal.Complete(null, failure is OperationCanceledException); }
                    else if (authenticated && failure is OperationCanceledException && drained) { journal.Complete(null, cancelled: true); }
                    else { journal.CompleteUnobserved(); }
                }
                try { writer?.Dispose(); }
                catch (IOException cleanup) { if (failure is not null) { failure.Data["InteractiveConsoleWriterCleanupFailure"] = cleanup; } }
                catch (ObjectDisposedException cleanup) { if (failure is not null) { failure.Data["InteractiveConsoleWriterCleanupFailure"] = cleanup; } }
                pipe.Dispose();
                if (resultRead is not null)
                {
                    try { await resultRead; }
                    catch (Exception cleanup) when (failure is not null) { failure.Data["InteractiveConsoleReadCleanupFailure"] = cleanup; }
                }
                reader?.Dispose();
            }
        }
    }
}
