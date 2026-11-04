using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

/// <summary>Transport a captured shell request to Terminal.app, then release the user terminal after authenticated ACK.</summary>
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
internal static class MacToolsShellLauncher
{
    [SupportedOSPlatform("macos")]
    internal static Task StartAsync(string repositoryPath, GitExecutionEnvironment environment, CancellationToken token,
        Func<ProcessStartInfo, Process?>? launch = null)
    {
        ConsoleStyleTerminal.ValidateNativeSelection();
        GitExecutionEnvironment shell = GitExecutionEnvironment.Capture(environment.Options with { Executable = "/bin/bash" }, environment.EnvironmentVariables);
        return RunCoreAsync(repositoryPath, shell, ["--login", "-i"], token, launch);
    }

    internal static async Task<int> RunCoreAsync(string repositoryPath, GitExecutionEnvironment shell, IReadOnlyList<string> arguments,
        CancellationToken token, Func<ProcessStartInfo, Process?>? launch = null, string? helperExecutable = null,
        Func<string, IReadOnlyList<string>, ProcessStartInfo>? terminalStart = null)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(shell);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath); ArgumentNullException.ThrowIfNull(arguments);
        if (repositoryPath.Contains('\0') || arguments.Any(argument => argument is null || argument.Contains('\0'))
            || shell.EnvironmentVariables.Any(pair => pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value?.Contains('\0') == true))
        { throw new ArgumentException("Terminal launch values are invalid."); }
        if (!Directory.Exists(repositoryPath)) { throw new DirectoryNotFoundException(repositoryPath); }
        GitCommandLogProcessContext journalContext = GitCommandLog.CaptureProcessContext();
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-shell-");
        string requestPath = Path.Combine(directory.DirectoryPath, "request.json");
        string commandPath = Path.Combine(directory.DirectoryPath, "terminal.command");
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(directory.SocketPath));
        File.SetUnixFileMode(directory.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); listener.Listen(1);
        ToolsShellRequest request = new(directory.SocketPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Environment.ProcessId, Path.GetFullPath(repositoryPath), arguments.ToArray(), InteractiveFileConsoleEnvironment.Capture(shell));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > 1024 * 1024) { throw new ArgumentException("The terminal startup request is too large."); }
        using (FileStream file = new(requestPath, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        {
            File.SetUnixFileMode(requestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await file.WriteAsync(bytes, token);
        }
        string executable = helperExecutable ?? Path.Combine(AppContext.BaseDirectory, "GitExtensions");
        if (!File.Exists(executable)) { throw new FileNotFoundException("The terminal helper is unavailable.", executable); }
        string[] helperArguments = [ToolsShellApplication.Mode, requestPath];
        ProcessStartInfo start;
        if (terminalStart is not null) { start = terminalStart(executable, helperArguments); }
        else
        {
            MacInteractiveConsoleTerminal.WriteCommand(commandPath, executable, helperArguments, repositoryPath, shell, preserveTerminalType: true);
            start = MacInteractiveConsoleTerminal.CreateStartInfo(commandPath, shell);
        }
        start.WorkingDirectory = Path.GetFullPath(repositoryPath);
        token.ThrowIfCancellationRequested();
        LoggedExternalProcess process = LoggedExternalProcess.Start(start, GitCommandLogProcessKind.TerminalLauncher, launch, journalContext, token);
        if (!process.HasProcess) { throw new IOException("The terminal launcher did not return a process."); }
        using CancellationTokenSource opening = CancellationTokenSource.CreateLinkedTokenSource(token);
        opening.CancelAfter(TimeSpan.FromSeconds(30));
        Task<Socket> accepted = listener.AcceptAsync(opening.Token).AsTask();
        if (await Task.WhenAny(accepted, process.Completion) == process.Completion && await process.Completion is int exit && exit != 0)
        {
            opening.Cancel();
            try { using Socket unused = await accepted; } catch (OperationCanceledException) when (opening.IsCancellationRequested) { }
            throw new IOException("The terminal launcher exited with code " + exit + ".");
        }
        using Socket connection = await accepted;
        int processId = PosixSocketPeerProcess.Authenticate(connection);
        using NetworkStream channel = new(connection, ownsSocket: false);
        using StreamReader reader = new(channel, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new(channel, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        InteractiveFileConsoleHello hello = await InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleHello>(reader, opening.Token);
        if (hello.Nonce is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Nonce), Encoding.UTF8.GetBytes(request.Nonce)))
        { throw new InvalidDataException("The shell startup handshake did not match this request."); }
        token.ThrowIfCancellationRequested();
        await writer.WriteLineAsync("ACK".AsMemory(), opening.Token);
        // All private files can now be removed. The standalone helper already holds its immutable request.
        return processId;
    }
}
