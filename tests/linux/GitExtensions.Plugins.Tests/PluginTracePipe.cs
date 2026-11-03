using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Plugins.Tests;

// Git itself opens this endpoint. Unix blocks in FIFO open; Windows fills a
// private trace pipe that the test deliberately never reads. Separate instances
// also admit Git's short-lived file-attribute probes before its actual writer.
internal sealed class PluginTracePipe : IAsyncDisposable
{
    private readonly List<WindowsSlot> _windows = [];
    private readonly string _name = "gitextensions-plugin-trace-" + Guid.NewGuid().ToString("N");
    internal string Path { get; }
    internal PluginTracePipe(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            Path = @"\\.\pipe\" + _name;
            try { for (int i = 0; i < 8; i++) { _windows.Add(CreateWindowsSlot()); } }
            catch { foreach (WindowsSlot slot in _windows) { _ = CloseAsync(slot); } throw; }
        }
        else
        {
            Path = System.IO.Path.Combine(root, "trace.fifo");
            if (MkFifo(Path, 384) != 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        }
    }
    [SupportedOSPlatform("windows")]
    private WindowsSlot CreateWindowsSlot()
    {
        NamedPipeServerStream pipe = new(_name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 0);
        try
        {
            if (pipe.InBufferSize > 4096) { throw new InvalidOperationException("The trace pipe buffer exceeds its verified bound."); }
            return new(pipe, pipe.WaitForConnectionAsync());
        }
        catch { pipe.Dispose(); throw; }
    }
    internal async Task AssertClientAsync(int expectedPid)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        int closedProbes = 0;
        while (true)
        {
            foreach (int index in Enumerable.Range(0, _windows.Count))
            {
                WindowsSlot slot = _windows[index];
                if (!slot.Connected.IsCompleted) { continue; }
                await slot.Connected;
                if (!GetClientPid(slot.Pipe.SafePipeHandle, out uint pid))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is not (109 or 232 or 233)) { throw new Win32Exception(error); }
                }
                else if (pid != expectedPid) { throw new InvalidOperationException("The native Git process is not the trace pipe's client."); }
                else if (Peek(slot.Pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                {
                    if (available >= slot.Pipe.InBufferSize)
                    {
                        Console.WriteLine($"Windows Trace2 peer {expectedPid}: unread={available}, buffer={slot.Pipe.InBufferSize}, closed probes={closedProbes}");
                        return;
                    }
                    continue;
                }
                else
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is not (109 or 232 or 233)) { throw new Win32Exception(error); }
                }
                // This connection closed without proving any trace I/O. Keep
                // other listeners ready while replacing the metadata probe.
                await CloseAsync(slot); _windows[index] = CreateWindowsSlot(); closedProbes++;
            }
            await Task.Delay(10, deadline.Token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (WindowsSlot slot in _windows) { await CloseAsync(slot); }
        if (!OperatingSystem.IsWindows()) { File.Delete(Path); }
    }
    private static async Task CloseAsync(WindowsSlot slot)
    {
        slot.Pipe.Dispose();
        try { await slot.Connected.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }
    private sealed record WindowsSlot(NamedPipeServerStream Pipe, Task Connected);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientPid(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", EntryPoint = "PeekNamedPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Peek(SafePipeHandle pipe, IntPtr buffer, uint size,
        IntPtr read, out uint available, IntPtr remaining);
}
