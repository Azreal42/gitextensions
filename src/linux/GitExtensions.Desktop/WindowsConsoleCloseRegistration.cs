using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitExtensions.Linux.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class WindowsConsoleCloseRegistration : IDisposable
{
    private readonly Handler _handler;
    private readonly CancellationTokenSource _job;
    private readonly Action _reportClose;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closing;
    internal bool IsClosing => Volatile.Read(ref _closing) != 0;

    internal WindowsConsoleCloseRegistration(CancellationTokenSource job, Action reportClose)
    {
        _job = job; _reportClose = reportClose; _handler = Handle;
        if (!SetConsoleCtrlHandler(_handler, true)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }
    private bool Handle(uint signal)
    {
        if (signal is not (2 or 5 or 6)) { return false; } // Close, logoff, shutdown; Console handles Ctrl+C/Break.
        Interlocked.Exchange(ref _closing, 1);
        // Console cleanup can outlive Windows' close-handler timeout. Notify the
        // authenticated parent first so it can stop and drain the outer job.
        try { _reportClose(); } catch (IOException) { }
        try { _job.Cancel(); } catch (ObjectDisposedException) { return true; }
        // Windows gives the handler a bounded cleanup interval. The GUI also
        // owns the outer job and drains it if the OS terminates this helper.
        _drained.Task.GetAwaiter().GetResult(); return true;
    }
    internal void Complete() => _drained.TrySetResult();
    public void Dispose()
    {
        Complete(); _ = SetConsoleCtrlHandler(_handler, false); GC.KeepAlive(_handler);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Handler(uint signal);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(Handler handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
