using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record ToolsShellRequest(string SocketPath, string Nonce, int ParentProcessId,
    string WorkingDirectory, string[] Arguments, InteractiveFileConsoleEnvironment Environment);

/// <summary>A terminal-owned shell; the GUI acknowledges startup but never owns or cancels its subsequent lifetime.</summary>
internal static class ToolsShellApplication
{
    internal const string Mode = "--tools-terminal";
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return 2; }
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine("Unable to open the Git shell: " + error.Message); return 1; }
    }

    [SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1) { throw new ArgumentException("The terminal requires one private startup request."); }
        string path = Path.GetFullPath(args[0]); FileInfo file = new(path);
        if (file.LinkTarget is not null || file.Length > 1024 * 1024
            || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        { throw new InvalidDataException("The terminal startup request must be a private regular file."); }
        ToolsShellRequest request = JsonSerializer.Deserialize<ToolsShellRequest>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("The terminal startup request is invalid.");
        GitExecutionEnvironment environment = InteractiveFileConsoleEnvironment.WithTerminalType(request.Environment.Restore(), Environment.GetEnvironmentVariable("TERM"));
        using (CancellationTokenSource opening = new(TimeSpan.FromSeconds(30)))
        using (Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(request.SocketPath), opening.Token);
            if (PosixSocketPeerProcess.Authenticate(socket) != request.ParentProcessId)
            { throw new InvalidDataException("The terminal startup parent does not match this request."); }
            using NetworkStream channel = new(socket, ownsSocket: false);
            using StreamReader reader = new(channel, Encoding.UTF8, false, leaveOpen: true);
            using StreamWriter writer = new(channel, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new InteractiveFileConsoleHello(request.Nonce)).AsMemory(), opening.Token);
            if (await reader.ReadLineAsync(opening.Token) != "ACK")
            { throw new IOException("The shell was not acknowledged; no command was started."); }
        }
        // After ACK all managed control sockets are closed. The shell takes this helper's PID and terminal;
        // it keeps its normal job control and survives the GUI without a separate process owner.
        return PosixUserShell.ReplaceCurrentProcess(request.WorkingDirectory, environment, request.Arguments);
    }
}
