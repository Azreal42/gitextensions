using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>Preserves the caller's console modes even when its Git job must be terminated.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConsoleModes : IDisposable
{
    private readonly List<(SafeFileHandle Handle, uint Mode)> _modes = [];
    private uint _inputCodePage, _outputCodePage;

    internal static WindowsConsoleModes Capture()
    {
        WindowsConsoleModes snapshot = new();
        try
        {
            foreach (int identifier in new[] { -10, -11, -12 })
            {
                IntPtr source = GetStdHandle(identifier), current = GetCurrentProcess();
                if (source == IntPtr.Zero || source == (IntPtr)(-1))
                { throw new Win32Exception(6, "Interactive Git requires the caller's attached console."); }
                if (!DuplicateHandle(current, source, current, out SafeFileHandle handle, 0, false, 2))
                { Win32Exception error = Error("Cannot reserve the caller's console modes."); handle?.Dispose(); throw error; }
                if (!GetConsoleMode(handle, out uint mode))
                { Win32Exception error = Error("Cannot read the caller's console modes."); handle.Dispose(); throw error; }
                snapshot._modes.Add((handle, mode));
            }
            snapshot._inputCodePage = GetConsoleCP(); snapshot._outputCodePage = GetConsoleOutputCP();
            if (snapshot._inputCodePage == 0 || snapshot._outputCodePage == 0)
            { throw Error("Cannot read the caller's console encoding."); }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    internal Exception? Restore()
    {
        List<Exception> errors = [];
        foreach ((SafeFileHandle handle, uint mode) in _modes)
        { if (!SetConsoleMode(handle, mode)) { errors.Add(Error("Cannot restore the caller's console mode.")); } }
        if (!SetConsoleCP(_inputCodePage)) { errors.Add(Error("Cannot restore the caller's input encoding.")); }
        if (!SetConsoleOutputCP(_outputCodePage)) { errors.Add(Error("Cannot restore the caller's output encoding.")); }
        return errors.Count == 0 ? null : new AggregateException("The interactive console could not be restored.", errors);
    }

    public void Dispose()
    { foreach (var (handle, _) in _modes) { handle.Dispose(); } _modes.Clear(); }
    private static Win32Exception Error(string message)
    { int code = Marshal.GetLastPInvokeError(); return new(code, message + " " + new Win32Exception(code).Message); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(SafeFileHandle handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCP(uint codePage);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint codePage);
}
