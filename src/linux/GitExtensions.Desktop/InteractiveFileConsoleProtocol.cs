using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux.Desktop;

internal sealed record InteractiveFileConsoleRequest(string SocketPath, string Nonce, string RepositoryPath, string GitExecutable, string[] Arguments)
{
    // Optional for helpers/requests produced before environment snapshots were transported.
    public InteractiveFileConsoleEnvironment? ExecutionEnvironment { get; init; }
    internal GitExecutionEnvironment RestoreExecutionEnvironment()
    {
        GitExecutionEnvironment environment = ExecutionEnvironment?.Restore()
            ?? GitExecutionEnvironment.Capture(new() { Executable = GitExecutable });
        if (environment.Executable != GitExecutable)
        { throw new InvalidDataException("The interactive Git executable differs from its captured environment."); }
        return environment;
    }
}

/// <summary>Only effective child environment data, copied before a terminal or helper can outlive its caller.</summary>
internal sealed class InteractiveFileConsoleEnvironment
{
    [JsonConstructor]
    public InteractiveFileConsoleEnvironment(GitEnvironmentOptions options, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(environmentVariables);
        GitExecutionEnvironment captured = GitExecutionEnvironment.Capture(options, environmentVariables);
        Options = captured.Options; EnvironmentVariables = captured.EnvironmentVariables;
    }
    public GitEnvironmentOptions Options { get; }
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }
    internal static InteractiveFileConsoleEnvironment Capture(GitExecutionEnvironment environment)
        => new(environment.Options, environment.EnvironmentVariables);
    internal GitExecutionEnvironment Restore() => GitExecutionEnvironment.Capture(Options, EnvironmentVariables);
}
internal sealed record InteractiveFileConsoleHello(string Nonce);
internal sealed record InteractiveFileConsoleResult(int ExitCode, bool Cancelled, string? Error);

internal static class InteractiveFileConsoleProtocol
{
    internal const string Mode = "--interactive-file-console";
    internal static async Task WriteAsync<T>(StreamWriter writer, T value)
    { await writer.WriteLineAsync(JsonSerializer.Serialize(value)); await writer.FlushAsync(); }
    internal static async Task<T> ReadAsync<T>(StreamReader reader, CancellationToken token = default)
    {
        string line = await reader.ReadLineAsync(token) ?? throw new IOException("The interactive Git console closed its control channel.");
        if (line.Length > 1024 * 1024) { throw new InvalidDataException("The interactive Git console record is too large."); }
        return JsonSerializer.Deserialize<T>(line) ?? throw new InvalidDataException("The interactive Git console record is invalid.");
    }
    internal static int Peer(Socket socket)
    {
        uint size = 12; Credentials credentials = default;
        if (GetCredentials(socket.Handle.ToInt32(), 1, 17, ref credentials, ref size) != 0 || size != 12
            || credentials.User != EffectiveUser() || credentials.Pid <= 1)
        { throw new IOException("The interactive Git console peer could not be authenticated."); }
        return credentials.Pid;
    }
    internal static SafeFileHandle PinProcess(int pid)
    {
        long descriptor = Syscall(434, pid, 0);
        if (descriptor < 0) { throw new IOException("The interactive Git console process could not be pinned."); }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }
    internal static Task WaitForCloseInputAsync(CancellationToken token) => Task.Run(() =>
    {
        // Console.In is synchronized and its ReadLineAsync cannot interrupt an active TTY read.
        // Poll only this helper's inherited stdin; cancellation never signals the terminal emulator.
        byte[] value = new byte[1];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PollDescriptor descriptor = new() { Descriptor = 0, Events = 1 };
            int ready = Poll(ref descriptor, 1, 100);
            if (ready < 0 && Marshal.GetLastPInvokeError() == 4) { continue; }
            if (ready < 0) { throw new IOException("Cannot read the interactive Git terminal."); }
            if (ready == 0) { continue; }
            if ((descriptor.Returned & 1) == 0) { return; } // Terminal closed or stdin reached EOF.
            long count = ReadInput(0, value, 1);
            if (count == 0 || count == 1 && value[0] == (byte)'\n') { return; }
            if (count < 0 && Marshal.GetLastPInvokeError() != 4) { throw new IOException("Cannot read the interactive Git terminal."); }
        }
    });
    internal static Task WaitProcessAsync(SafeFileHandle handle) => Task.Run(() =>
    {
        PollDescriptor descriptor = new() { Descriptor = handle.DangerousGetHandle().ToInt32(), Events = 1 };
        int result;
        do { result = Poll(ref descriptor, 1, -1); } while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        if (result < 0) { throw new IOException("The interactive Git console process could not be awaited."); }
        GC.KeepAlive(handle);
    });
    [StructLayout(LayoutKind.Sequential)] private struct Credentials { internal int Pid; internal uint User, Group; }
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { internal int Descriptor; internal short Events, Returned; }
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)] private static extern int GetCredentials(int fd, int level, int option, ref Credentials value, ref uint length);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint EffectiveUser();
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long Syscall(long number, int pid, int flags);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)] private static extern long ReadInput(int descriptor, [Out] byte[] value, nuint count);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptors, uint count, int timeout);
}
