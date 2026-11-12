using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text;
using GitExtensions.Linux;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
internal static class WindowsConsoleProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string root = args[1];
        if (args[0] == "windows-console-observer") { return await ObserveAsync(args); }
        if (args[0] == "windows-console-child")
        {
            uint[] modes = Modes();
            if (!SetConsoleMode(GetStdHandle(-10), modes[0] & ~4u)) { throw Error(); } // Leave ECHO changed, including forced stop.
            await PublishAsync(Path.Combine(root, "console-ready.json"), new
            {
                pid = Environment.ProcessId, modes, arguments = args[3..], cwd = Directory.GetCurrentDirectory(),
                value = Environment.GetEnvironmentVariable("GITEXTENSIONS_NATIVE_PROBE_VALUE")
            });
            string? line = Console.ReadLine();
            await PublishAsync(Path.Combine(root, "console-input.json"), new { line });
            if (!SetConsoleCP(437) || !SetConsoleOutputCP(437)) { throw Error(); }
            return int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (args[0] != "windows-console-controller" || args.Length < 5) { return 2; }
        try
        {
            using PrivateConsole console = new();
            uint[] before = Modes(); uint inputBefore = GetConsoleCP(), outputBefore = GetConsoleOutputCP();
            Environment.SetEnvironmentVariable("GITEXTENSIONS_NATIVE_PROBE_VALUE", "captured console λ '$()");
            GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = args[2] });
            using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
            Task<GitResult> operation = GitInteractiveTerminalProcess.RunAsync(root, environment,
                ["--native-process-probe", "windows-console-child", root, args[4], .. args[5..]], cancel.Token);
            string ready = Path.Combine(root, "console-ready.json");
            while (!File.Exists(ready))
            {
                if (operation.IsCompleted) { await operation; throw new IOException("Console child exited before publishing its actual handles."); }
                await Task.Delay(10, cancel.Token);
            }
            using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(ready));
            using Process actual = Process.GetProcessById(marker.RootElement.GetProperty("pid").GetInt32());
            _ = actual.SafeHandle; // Pin the real child before cancellation or natural exit.
            bool cancelled = false; int? exit = null;
            if (args[3] == "cancel") { cancel.Cancel(); }
            else { SendInput("one ' λ [*] literal\r"); }
            try { exit = (await operation).ExitCode; }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            await actual.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await PublishAsync(Path.Combine(root, "console-result.json"), new
            {
                exit, cancelled, actualChildExited = actual.HasExited, before, after = Modes(),
                inputBefore, inputAfter = GetConsoleCP(), outputBefore, outputAfter = GetConsoleOutputCP()
            });
            return 0;
        }
        catch (Exception error)
        { await PublishAsync(Path.Combine(root, "console-error.json"), new { error = error.ToString() }); return 1; }
    }

    private static uint[] Modes()
    {
        uint[] modes = new uint[3]; int[] identifiers = [-10, -11, -12];
        for (int i = 0; i < identifiers.Length; i++)
        { if (!GetConsoleMode(GetStdHandle(identifiers[i]), out modes[i])) { throw Error(); } }
        return modes;
    }

    private static void SendInput(string value, IntPtr? input = null)
    {
        if (value.Length == 0) { return; }
        if (Marshal.SizeOf<InputRecord>() != 20) { throw new InvalidOperationException("The native console input record ABI differs."); }
        List<InputRecord> records = [];
        foreach (char character in value)
        {
            KeyEvent key = new() { Down = 1, Repeat = 1, VirtualKey = character == '\r' ? (ushort)13 : (ushort)0, Character = character };
            records.Add(new() { Type = 1, Key = key }); key.Down = 0; records.Add(new() { Type = 1, Key = key });
        }
        if (!WriteConsoleInput(input ?? GetStdHandle(-10), records.ToArray(), (uint)records.Count, out uint count) || count != records.Count)
        { throw Error(); }
    }

    private static async Task<int> ObserveAsync(string[] args)
    {
        if (args.Length != 6) { return 2; }
        string result = Path.Combine(args[1], "observer-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // An observer can attach to the host only after releasing its startup console.
            if (!FreeConsole()) { throw Error(); }
            if (!AttachConsole(uint.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture))) { throw Error(); }
            try
            {
                using SafeFileHandle output = PrivateConsole.OpenConsole("CONOUT$"), input = PrivateConsole.OpenConsole("CONIN$");
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                while (true)
                {
                    if (!GetConsoleScreenBufferInfo(output, out ScreenInfo info)) { throw Error(); }
                    int rows = info.Window.Bottom - info.Window.Top + 1;
                    uint length = checked((uint)Math.Min(32_000, (long)info.Size.X * rows));
                    StringBuilder text = new(checked((int)length));
                    if (!ReadConsoleOutputCharacter(output, text, length, new() { Y = info.Window.Top }, out _)) { throw Error(); }
                    if (text.ToString().Contains(args[3], StringComparison.Ordinal)) { break; }
                    await Task.Delay(10, timeout.Token);
                }
                await PublishAsync(result, new { observed = args[3], parent = args[2], action = args[5] });
                if (args[5] == "close")
                { if (!PostMessage(GetConsoleWindow(), 0x0010, IntPtr.Zero, IntPtr.Zero)) { throw Error(); } }
                else { SendInput(args[4], input.DangerousGetHandle()); }
                return 0;
            }
            finally { _ = FreeConsole(); }
        }
        catch (Exception error) { await PublishAsync(result + ".error", new { error = error.ToString() }); return 1; }
    }

    private static async Task PublishAsync<T>(string path, T value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path);
    }

    private sealed class PrivateConsole : IDisposable
    {
        private readonly IntPtr[] _original = [GetStdHandle(-10), GetStdHandle(-11), GetStdHandle(-12)];
        private readonly bool _allocated;
        private SafeFileHandle? _input, _output;
        internal PrivateConsole()
        {
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero)
                { if (!AllocConsole()) { throw Error(); } _allocated = true; }
                _input = OpenConsole("CONIN$"); _output = OpenConsole("CONOUT$");
                if (!SetStdHandle(-10, _input.DangerousGetHandle()) || !SetStdHandle(-11, _output.DangerousGetHandle())
                    || !SetStdHandle(-12, _output.DangerousGetHandle()) || !SetConsoleCP(65001) || !SetConsoleOutputCP(65001))
                { throw Error(); }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            for (int i = 0; i < _original.Length; i++) { _ = SetStdHandle(-10 - i, _original[i]); }
            _input?.Dispose(); _output?.Dispose(); if (_allocated) { _ = FreeConsole(); }
        }
        internal static SafeFileHandle OpenConsole(string name)
        {
            SafeFileHandle result = CreateFile(name, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!result.IsInvalid) { return result; }
            Win32Exception error = Error(); result.Dispose(); throw error;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
    private struct InputRecord { [FieldOffset(0)] internal ushort Type; [FieldOffset(4)] internal KeyEvent Key; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEvent { internal int Down; internal ushort Repeat, VirtualKey, Scan; internal char Character; internal uint Control; }
    [StructLayout(LayoutKind.Sequential)] private struct Coord { internal short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { internal short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenInfo
    { internal Coord Size, Cursor; internal ushort Attributes; internal Rectangle Window; internal Coord Maximum; }
    private static Win32Exception Error() => new(Marshal.GetLastPInvokeError());
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int identifier, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCP(uint value);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleOutputCP(uint value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInput(IntPtr handle, [In] InputRecord[] records, uint count, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(SafeFileHandle handle, out ScreenInfo info);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacter(SafeFileHandle handle, StringBuilder text, uint count, Coord position, out uint read);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
