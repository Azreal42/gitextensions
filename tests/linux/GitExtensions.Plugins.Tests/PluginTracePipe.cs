using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Plugins.Tests;

// Git itself opens this endpoint. Unix blocks in FIFO open; Windows blocks in
// trace writes into a small kernel buffer that the test deliberately never reads.
internal sealed class PluginTracePipe : IDisposable
{
    private readonly NamedPipeServerStream? _windows;
    private readonly Task? _connected;
    internal string Path { get; }
    internal PluginTracePipe(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            string name = "gitextensions-plugin-trace-" + Guid.NewGuid().ToString("N");
            Path = @"\\.\pipe\" + name;
            _windows = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 0);
            if (_windows.InBufferSize > 4096) { throw new InvalidOperationException("The trace pipe buffer exceeds its verified bound."); }
            _connected = _windows.WaitForConnectionAsync();
        }
        else
        {
            Path = System.IO.Path.Combine(root, "trace.fifo");
            if (MkFifo(Path, 384) != 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        }
    }
    internal async Task AssertClientAsync(int expectedPid)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await _connected!.WaitAsync(TimeSpan.FromSeconds(20));
        if (!GetClientPid(_windows!.SafePipeHandle, out uint pid)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        if (pid != expectedPid) { throw new InvalidOperationException("The native Git process is not the trace pipe's client."); }
    }
    public void Dispose() { _windows?.Dispose(); if (!OperatingSystem.IsWindows()) { File.Delete(Path); } }
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientPid(SafePipeHandle pipe, out uint pid);
}
