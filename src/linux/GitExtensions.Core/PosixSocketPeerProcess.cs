using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>A same-user Unix socket peer tracked by a native process reference acquired before ACK.</summary>
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class PosixSocketPeerProcess : IDisposable
{
    private readonly SafeFileHandle _reference;
    private readonly Task _exit;
    public int ProcessId { get; }

    private PosixSocketPeerProcess(SafeFileHandle reference, int processId)
    {
        _reference = reference; ProcessId = processId;
        _exit = Task.Factory.StartNew(() =>
        {
            if (Wait(reference) != 0) { throw Error("Cannot await the interactive console process."); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public Task WaitForExitAsync() => _exit;

    public static int Authenticate(Socket socket)
    {
        EnsurePlatform();
        ArgumentNullException.ThrowIfNull(socket);
        int pid = Peer(socket.SafeHandle);
        if (pid < 0) { throw Error("Cannot authenticate the interactive console peer."); }
        return pid;
    }

    public static async Task SendCurrentAsync(Socket socket, CancellationToken token = default)
    {
        _ = Authenticate(socket);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Send(socket.SafeHandle) == 0) { return; }
            int error = Marshal.GetLastPInvokeError();
            if (!WouldBlock(error)) { throw Error("Cannot transfer the console process reference.", error); }
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    public static async Task<PosixSocketPeerProcess> ReceiveAsync(Socket socket, CancellationToken token = default)
    {
        EnsurePlatform();
        ArgumentNullException.ThrowIfNull(socket);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int descriptor = Receive(socket.SafeHandle, out int pid);
            if (descriptor >= 0) { return new(new((IntPtr)descriptor, ownsHandle: true), pid); }
            int error = Marshal.GetLastPInvokeError();
            if (!WouldBlock(error)) { throw Error("Cannot receive the console process reference.", error); }
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _reference.Dispose();
    private static void EnsurePlatform()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        { throw new PlatformNotSupportedException("Unix console peers require a supported POSIX process backend."); }
    }
    private static bool WouldBlock(int error) => error == (OperatingSystem.IsMacOS() ? 35 : 11);
    private static IOException Error(string message, int? error = null) => new(message, new Win32Exception(error ?? Marshal.GetLastPInvokeError()));
    [DllImport("gitextensions-posix", EntryPoint = "ge_socket_peer_process", SetLastError = true)]
    private static extern int Peer(SafeSocketHandle socket);
    [DllImport("gitextensions-posix", EntryPoint = "ge_send_current_process_reference", SetLastError = true)]
    private static extern int Send(SafeSocketHandle socket);
    [DllImport("gitextensions-posix", EntryPoint = "ge_receive_process_reference", SetLastError = true)]
    private static extern int Receive(SafeSocketHandle socket, out int processId);
    [DllImport("gitextensions-posix", EntryPoint = "ge_wait_process_reference", SetLastError = true)]
    private static extern int Wait(SafeFileHandle reference);
}
