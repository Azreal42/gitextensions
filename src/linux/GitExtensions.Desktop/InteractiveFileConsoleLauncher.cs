using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux.Desktop;

/// <summary>A private terminal job, whose authenticated helper owns Git even when the terminal client detaches.</summary>
public static class InteractiveFileConsoleLauncher
{
    public static async Task<GitResult> RunAsync(Window owner, string repositoryPath, string gitExecutable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => await RunWithTerminalAsync(owner, repositoryPath, gitExecutable, arguments, cancellationToken, null);

    public static Task<GitResult> RunAsync(Window owner, string repositoryPath, GitExecutionEnvironment environment,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return RunWithTerminalAsync(owner, repositoryPath, environment.Executable, arguments, cancellationToken, null,
            executionEnvironment: environment);
    }

    internal static async Task<GitResult> RunWithTerminalAsync(Window owner, string repositoryPath, string gitExecutable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken, Func<string, IReadOnlyList<string>, Process>? startTerminal, string? helperExecutable = null,
        GitExecutionEnvironment? executionEnvironment = null, GitCommandLogProcessContext? journalContext = null)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Interactive file actions require a Linux terminal."); }
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(owner);
        ValidateRequest(repositoryPath, gitExecutable, arguments);
        journalContext ??= GitCommandLog.CaptureProcessContext();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool closeRequested = false;
        void Closing(object? sender, WindowClosingEventArgs args)
        { closeRequested = true; args.Cancel = true; lifetime.Cancel(); }
        owner.Closing += Closing;
        try { return await RunCoreAsync(repositoryPath, gitExecutable, arguments, lifetime.Token, startTerminal, helperExecutable, executionEnvironment, journalContext); }
        finally
        {
            owner.Closing -= Closing;
            if (closeRequested) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        }
    }

    [SupportedOSPlatform("linux")]
    internal static async Task<GitResult> RunCoreAsync(string repositoryPath, string gitExecutable,
        IReadOnlyList<string> arguments, CancellationToken token, Func<string, IReadOnlyList<string>, Process>? startTerminal = null, string? helperExecutable = null,
        GitExecutionEnvironment? executionEnvironment = null, GitCommandLogProcessContext? journalContext = null)
    {
        token.ThrowIfCancellationRequested();
        ValidateRequest(repositoryPath, gitExecutable, arguments);
        journalContext ??= GitCommandLog.CaptureProcessContext();
        executionEnvironment ??= GitExecutionEnvironment.Capture(new() { Executable = gitExecutable });
        if (executionEnvironment.Executable != gitExecutable)
        { throw new ArgumentException("The interactive Git executable must match its captured environment.", nameof(executionEnvironment)); }
        using LinuxUnixSocketDirectory socketDirectory = LinuxUnixSocketDirectory.Create("ge-interactive-");
        string directory = socketDirectory.DirectoryPath;
        string socketPath = socketDirectory.SocketPath, requestPath = Path.Combine(directory, "request.json");
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Process? terminal = null;
        Socket? connection = null;
        SafeFileHandle? peer = null;
        StreamWriter? writer = null;
        Task? peerExit = null;
        GitCommandLogProcessOperation? journal = null;
        bool authenticated = false, reported = false;
        Exception? failure = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(1);
            InteractiveFileConsoleRequest request = new(socketPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Path.GetFullPath(repositoryPath), gitExecutable, arguments.ToArray())
            { ExecutionEnvironment = InteractiveFileConsoleEnvironment.Capture(executionEnvironment) };
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false), token);
            File.SetUnixFileMode(requestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string executable = helperExecutable ?? Path.Combine(AppContext.BaseDirectory, "GitExtensions");
            if (!File.Exists(executable)) { throw new FileNotFoundException("The interactive Git console application is unavailable.", executable); }
            token.ThrowIfCancellationRequested();
            journal = GitCommandLog.StartProcess(GitCommandLogProcessKind.InteractiveConsole, repositoryPath, journalContext);
            terminal = startTerminal is not null ? startTerminal(executable, [InteractiveFileConsoleProtocol.Mode, requestPath])
                : StartTerminal(executable, [InteractiveFileConsoleProtocol.Mode, requestPath], executionEnvironment);
            using CancellationTokenSource opening = CancellationTokenSource.CreateLinkedTokenSource(token);
            opening.CancelAfter(TimeSpan.FromSeconds(30));
            try { connection = await listener.AcceptAsync(opening.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("The terminal did not open the interactive Git console. Check that a terminal emulator is installed and can start."); }
            int peerPid = InteractiveFileConsoleProtocol.Peer(connection);
            peer = InteractiveFileConsoleProtocol.PinProcess(peerPid);
            peerExit = InteractiveFileConsoleProtocol.WaitProcessAsync(peer);
            using NetworkStream channel = new(connection, ownsSocket: false);
            using StreamReader reader = new(channel, Encoding.UTF8, false, leaveOpen: true);
            writer = new StreamWriter(channel, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            InteractiveFileConsoleHello hello = await InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleHello>(reader, opening.Token);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Nonce), Encoding.UTF8.GetBytes(request.Nonce)))
            { throw new InvalidDataException("The interactive Git console handshake did not match this operation."); }
            authenticated = true;
            journal.SetProcessId(peerPid); // The nonce-authenticated console host, not its terminal broker or Git child.
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync("ACK"); // Git must not start before this authenticated acknowledgement.
            Task<InteractiveFileConsoleResult> resultRead = InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleResult>(reader);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(resultRead, cancelled.Task) == cancelled.Task)
            { await writer.WriteLineAsync("CANCEL"); }
            InteractiveFileConsoleResult result = await resultRead;
            await peerExit; // Keep the caller's mutation lock until the helper has drained Git and actually exited.
            await terminal.WaitForExitAsync(CancellationToken.None);
            reported = true;
            journal.Complete(result.Cancelled || result.Error is not null ? null : result.ExitCode, result.Cancelled);
            if (result.Cancelled) { throw new OperationCanceledException("Interactive Git was cancelled after its private processes stopped.", token); }
            if (result.Error is not null) { throw new IOException(result.Error); }
            return new(result.ExitCode, "", result.ExitCode == 0 ? "" : "Interactive Git exited with code " + result.ExitCode + ". See the terminal output.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            // EOF is also cancellation for an authenticated helper. Never kill a terminal server or user terminal.
            if (writer is not null)
            { try { await writer.WriteLineAsync("CANCEL"); } catch (IOException) { } catch (ObjectDisposedException) { } }
            connection?.Dispose();
            try
            {
                if (peerExit is not null) { await peerExit; }
            }
            finally
            {
                if (journal is not null && !reported)
                {
                    if (terminal is null) { journal.Complete(null, failure is OperationCanceledException); }
                    else if (authenticated && failure is OperationCanceledException && peerExit?.IsCompletedSuccessfully == true)
                    { journal.Complete(null, cancelled: true); }
                    else { journal.CompleteUnobserved(); }
                }
                writer?.Dispose(); peer?.Dispose(); terminal?.Dispose();
                listener.Dispose();
            }
        }
    }

    private static void ValidateRequest(string repositoryPath, string gitExecutable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (repositoryPath.Contains('\0') || gitExecutable.Contains('\0'))
        { throw new ArgumentException("Interactive launch values cannot contain NUL."); }
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0')) { throw new ArgumentException("Interactive arguments cannot contain NUL.", nameof(arguments)); }
        }
    }

    private static Process StartTerminal(string executable, IReadOnlyList<string> arguments, GitExecutionEnvironment environment)
    {
        ProcessStartInfo info = ConsoleStyleTerminal.CreateStartInfo(executable, arguments, environment, waitForExit: true);
        return Process.Start(info) ?? throw new IOException("The interactive Git terminal could not be started.");
    }
}
