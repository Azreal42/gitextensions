using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace GitExtensions.Linux.Desktop;

[SupportedOSPlatform("windows")]
internal static class WindowsInteractiveFileConsoleApplication
{
    internal static int Run(string[] args)
    {
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine("Unable to run interactive Git: " + error.Message); return 1; }
    }
    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--pipe" || !args[1].StartsWith("gitextensions-console-", StringComparison.Ordinal)
            || args[1].Length > 128 || args[2].Length != 64 || !int.TryParse(args[3], out int parent) || parent <= 1)
        { throw new ArgumentException("The interactive console requires its private channel and parent identity."); }
        _ = Convert.FromHexString(args[2]);
        using WindowsConsoleSession console = WindowsConsoleSession.Open();
        using NamedPipeClientStream pipe = new(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        using CancellationTokenSource opening = new(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(opening.Token);
        WindowsInteractiveFileConsoleProtocol.AuthenticateServer(pipe.SafePipeHandle, parent);
        using StreamReader reader = new(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        InteractiveFileConsoleRequest request = await InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleRequest>(reader, opening.Token);
        if (request.SocketPath != args[1] || !WindowsInteractiveFileConsoleProtocol.SameNonce(request.Nonce, args[2]))
        { throw new InvalidDataException("The private console request does not match its launched channel."); }
        GitExecutionEnvironment environment = request.RestoreExecutionEnvironment();
        await WindowsInteractiveFileConsoleProtocol.WriteAsync(writer, new InteractiveFileConsoleHello(request.Nonce), opening.Token);
        if (await reader.ReadLineAsync(opening.Token) != "ACK")
        { throw new IOException("Interactive Git was not acknowledged; no Git command was started."); }
        using CancellationTokenSource job = new(), controlLifetime = new();
        using WindowsConsoleCloseRegistration close = new(job);
        void Interrupt(object? sender, ConsoleCancelEventArgs context) { context.Cancel = true; job.Cancel(); }
        Console.CancelKeyPress += Interrupt;
        Task control = WatchParentAsync(reader, job, controlLifetime.Token);
        GitResult? completed = null;
        InteractiveFileConsoleResult result;
        try
        {
            Console.WriteLine("Git Extensions — interactive Git"); Console.WriteLine(request.RepositoryPath);
            Console.WriteLine("Use Git's displayed choices. Closing this console cancels this Git operation.");
            completed = await GitInteractiveTerminalProcess.RunAsync(request.RepositoryPath, environment, request.Arguments, job.Token);
            Console.WriteLine(); Console.WriteLine("Git exited with code " + completed.ExitCode + ". Press Enter to close.");
            await console.WaitForCloseInputAsync(job.Token);
            result = new(completed.ExitCode, false, null);
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            result = completed is null ? new(130, true, null) : new(completed.ExitCode, false, null);
            if (!close.IsClosing)
            { Console.WriteLine(completed is null ? "Interactive Git cancelled; owned processes stopped." : "Console closed after Git completed."); }
        }
        catch (Exception) when (close.IsClosing && job.IsCancellationRequested)
        { result = completed is null ? new(130, true, null) : new(completed.ExitCode, false, null); }
        catch (Exception error) { result = new(1, false, error.Message); Console.Error.WriteLine(error.Message); }
        finally
        {
            Console.CancelKeyPress -= Interrupt; controlLifetime.Cancel();
            try { await control; } catch (OperationCanceledException) when (controlLifetime.IsCancellationRequested) { }
        }
        try { await WindowsInteractiveFileConsoleProtocol.WriteAsync(writer, result); }
        catch (IOException) { /* The parent is gone; the inner Git job has already drained. */ }
        finally { close.Complete(); }
        return result.ExitCode;
    }
    private static async Task WatchParentAsync(StreamReader reader, CancellationTokenSource job, CancellationToken token)
    {
        try { _ = await reader.ReadLineAsync(token); }
        catch (IOException) { }
        finally { if (!token.IsCancellationRequested) { job.Cancel(); } }
    }
}
