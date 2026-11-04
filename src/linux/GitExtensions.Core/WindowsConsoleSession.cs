using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>The console I/O of a dedicated Windows helper, with interruptible close acknowledgement.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConsoleSession : IDisposable
{
    private readonly IntPtr[] _original = [GetStdHandle(-10), GetStdHandle(-11), GetStdHandle(-12)];
    private SafeFileHandle? _input, _output;
    private uint _inputCodePage, _outputCodePage;
    private bool _allocated;
    private int _disposed;

    private WindowsConsoleSession() { }
    public static WindowsConsoleSession Open()
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Windows console I/O requires Windows."); }
        WindowsConsoleSession session = new();
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            { if (!AllocConsole()) { throw Error(); } session._allocated = true; }
            session._inputCodePage = GetConsoleCP(); session._outputCodePage = GetConsoleOutputCP();
            session._input = OpenConsole("CONIN$"); session._output = OpenConsole("CONOUT$");
            if (!SetStdHandle(-10, session._input.DangerousGetHandle()) || !SetStdHandle(-11, session._output.DangerousGetHandle())
                || !SetStdHandle(-12, session._output.DangerousGetHandle()) || !SetConsoleCP(65001) || !SetConsoleOutputCP(65001))
            { throw Error(); }
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public Task WaitForCloseInputAsync(CancellationToken token) => Task.Factory.StartNew(() =>
    {
        token.ThrowIfCancellationRequested();
        SafeWaitHandle cancelled = token.WaitHandle.SafeWaitHandle;
        bool inputReference = false, cancellationReference = false;
        try
        {
            _input!.DangerousAddRef(ref inputReference); cancelled.DangerousAddRef(ref cancellationReference);
            IntPtr[] handles = [cancelled.DangerousGetHandle(), _input.DangerousGetHandle()];
            InputRecord[] input = new InputRecord[1];
            while (true)
            {
                uint ready = WaitForMultipleObjects(2, handles, false, uint.MaxValue);
                if (ready == 0) { token.ThrowIfCancellationRequested(); }
                if (ready != 1) { throw Error(); }
                if (!ReadConsoleInput(_input, input, 1, out uint count)) { throw Error(); }
                if (count == 1 && input[0].Type == 1 && input[0].Key.Down != 0 && input[0].Key.Character is '\r' or '\n') { return; }
            }
        }
        finally
        {
            if (cancellationReference) { cancelled.DangerousRelease(); }
            if (inputReference) { _input!.DangerousRelease(); }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        if (_inputCodePage != 0) { _ = SetConsoleCP(_inputCodePage); }
        if (_outputCodePage != 0) { _ = SetConsoleOutputCP(_outputCodePage); }
        for (int i = 0; i < _original.Length; i++) { _ = SetStdHandle(-10 - i, _original[i]); }
        _input?.Dispose(); _output?.Dispose(); if (_allocated) { _ = FreeConsole(); _allocated = false; }
    }
    private static SafeFileHandle OpenConsole(string name)
    {
        SafeFileHandle handle = CreateFile(name, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) { return handle; }
        Win32Exception error = Error(); handle.Dispose(); throw error;
    }
    private static Win32Exception Error() => new(Marshal.GetLastPInvokeError());
    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
    private struct InputRecord { [FieldOffset(0)] internal ushort Type; [FieldOffset(4)] internal KeyEvent Key; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEvent { internal int Down; internal ushort Repeat, VirtualKey, Scan; internal char Character; internal uint Control; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int identifier, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCP(uint value);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleOutputCP(uint value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, [In] IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleInput(SafeFileHandle handle, [Out] InputRecord[] records, uint count, out uint read);
}
