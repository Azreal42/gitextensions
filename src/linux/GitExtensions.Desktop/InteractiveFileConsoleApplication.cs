using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal static class InteractiveFileConsoleApplication
{
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        { Console.Error.WriteLine("Interactive Git requires a supported native terminal."); return 2; }
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception exception) { Console.Error.WriteLine("Unable to run interactive Git: " + exception.Message); return 1; }
    }
    [SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1) { throw new ArgumentException("The interactive Git console requires one private request file."); }
        string path = Path.GetFullPath(args[0]);
        if (new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 1024 * 1024
            || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        { throw new InvalidDataException("The interactive Git console request must be a private regular file."); }
        InteractiveFileConsoleRequest request = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("The interactive Git console request is invalid.");
        GitExecutionEnvironment environment = request.RestoreExecutionEnvironment();
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(request.SocketPath));
        await PosixSocketPeerProcess.SendCurrentAsync(socket);
        using NetworkStream channel = new(socket, ownsSocket: false);
        using StreamReader reader = new(channel, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new(channel, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await InteractiveFileConsoleProtocol.WriteAsync(writer, new InteractiveFileConsoleHello(request.Nonce));
        if (await reader.ReadLineAsync() != "ACK") { throw new IOException("Interactive Git was not acknowledged; no Git command was started."); }
        using CancellationTokenSource job = new();
        using CancellationTokenSource controlLifetime = new();
        using PosixSignalRegistration hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { context.Cancel = true; job.Cancel(); });
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; job.Cancel(); });
        void Interrupt(object? sender, ConsoleCancelEventArgs context) { context.Cancel = true; job.Cancel(); }
        Console.CancelKeyPress += Interrupt;
        Task control = WatchParentAsync(reader, job, controlLifetime.Token);
        InteractiveFileConsoleResult result;
        GitResult? completed = null;
        try
        {
            Console.WriteLine("Git Extensions — interactive Git");
            Console.WriteLine(request.RepositoryPath);
            Console.WriteLine("Use Git's displayed choices. Closing this console cancels and waits for this Git operation.");
            completed = await GitInteractiveTerminalProcess.RunAsync(request.RepositoryPath, environment, request.Arguments, job.Token);
            Console.WriteLine(); Console.WriteLine("Git exited with code " + completed.ExitCode + ". Press Enter to close.");
            await InteractiveFileConsoleProtocol.WaitForCloseInputAsync(job.Token);
            result = new(completed.ExitCode, false, null);
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            result = completed is null ? new(130, true, null) : new(completed.ExitCode, false, null);
            Console.WriteLine(completed is null ? "Interactive Git cancelled; private processes stopped." : "Console closed after Git completed.");
        }
        catch (Exception exception)
        { result = new(1, false, exception.Message); Console.Error.WriteLine(exception.Message); }
        finally
        {
            Console.CancelKeyPress -= Interrupt;
            controlLifetime.Cancel();
            try { await control; } catch (OperationCanceledException) when (controlLifetime.IsCancellationRequested) { }
        }
        try { await InteractiveFileConsoleProtocol.WriteAsync(writer, result); }
        catch (IOException) { /* The owner is gone; Git has already been drained. */ }
        return result.ExitCode;
    }
    private static async Task WatchParentAsync(StreamReader reader, CancellationTokenSource job, CancellationToken token)
    {
        try { _ = await reader.ReadLineAsync(token); }
        catch (IOException) { }
        finally { if (!token.IsCancellationRequested) { job.Cancel(); } }
    }
}
