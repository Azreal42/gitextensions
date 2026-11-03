using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Plugins.Tests;

// An actual apphost, renamed like the configured program. The production argv
// reaches Main unchanged; Python, a shell and an interpreter are not involved.
internal static class PluginExecutableFixture
{
    private const string Configuration = "plugin-executable-fixture.json";
    internal static string Install(string directory, string name, string mode)
    {
        Directory.CreateDirectory(directory);
        string source = Path.Combine(AppContext.BaseDirectory, "GitExtensions.Plugins.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            if (Path.GetFullPath(file) == source) { continue; }
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }
        string executable = Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(source, executable);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, File.GetUnixFileMode(source)); }
        File.WriteAllText(Path.Combine(directory, Configuration), JsonSerializer.Serialize(new Settings(mode)));
        return executable;
    }

    internal static bool IsChild => File.Exists(Path.Combine(AppContext.BaseDirectory, Configuration));
    internal static async Task<int> RunChildAsync(string[] arguments)
    {
        string root = AppContext.BaseDirectory;
        Settings settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(root, Configuration)))!;
        if (settings.Mode == "git-log") { return await RunGitAsync(arguments, root); }
        if (settings.Mode == "automation")
        {
            string call = JsonSerializer.Serialize(new { args = arguments, cwd = Directory.GetCurrentDirectory(), pid = Environment.ProcessId });
            await File.AppendAllTextAsync(Path.Combine(root, "calls.jsonl"), call + "\n", new UTF8Encoding(false));
            while (File.Exists(Path.Combine(root, "hold")) && !File.Exists(Path.Combine(root, "release"))) { await Task.Delay(10); }
            await Console.OpenStandardOutput().WriteAsync("fake build output\n"u8.ToArray());
            if (!File.Exists(Path.Combine(root, "fail"))) { return 0; }
            await Console.OpenStandardError().WriteAsync("fake build failure\n"u8.ToArray()); return 7;
        }
        if (settings.Mode == "build")
        {
            await PublishAsync(Path.Combine(root, "pid"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            while (!File.Exists(Path.Combine(root, "release"))) { await Task.Delay(10); }
            await Console.OpenStandardOutput().WriteAsync("build-output-secret\n"u8.ToArray());
            await Console.OpenStandardError().WriteAsync("build-error-secret\n"u8.ToArray()); return 7;
        }
        throw new InvalidDataException("Unknown native plugin fixture mode.");
    }

    private static async Task<int> RunGitAsync(string[] arguments, string root)
    {
        string git = await File.ReadAllTextAsync(Path.Combine(root, "native-git"));
        ProcessStartInfo start = new(git) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        int index = 0;
        while (index < arguments.Length)
        {
            if (arguments[index] is "-c" or "--git-dir" or "--work-tree" or "--namespace" or "-C" or "--config-env") { index += 2; continue; }
            if (arguments[index].StartsWith('-')) { index++; continue; }
            break;
        }
        bool log = index < arguments.Length && arguments[index] == "log";
        if (log)
        {
            start.Environment["GIT_TRACE2_EVENT"] = await File.ReadAllTextAsync(Path.Combine(root, "trace-path"));
            if (OperatingSystem.IsWindows())
            {
                // The reader consumes no bytes. Complete trace records overflow
                // the checked kernel buffer; Unix instead blocks on FIFO open.
                start.Environment["GIT_TRACE2_ENV_VARS"] = string.Join(',', Enumerable.Range(0, 16).Select(i => "GE_PLUGIN_TRACE_" + i));
                foreach (int value in Enumerable.Range(0, 16)) { start.Environment["GE_PLUGIN_TRACE_" + value] = new string('x', 16000); }
            }
        }
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        if (log) { await PublishAsync(Path.Combine(root, "pid.json"), JsonSerializer.Serialize(new { pid = process.Id })); }
        await process.WaitForExitAsync(); return process.ExitCode;
    }

    private static async Task PublishAsync(string path, string value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false)); File.Move(temporary, path);
    }
    private sealed record Settings(string Mode);
}

// Kernel references outlive the PID identity. An exited-but-unreaped Unix
// process is stopped; polling /proc or Process.HasExited cannot prove that.
internal sealed class PluginProcessReference : IDisposable
{
    private readonly SafeFileHandle? _unix;
    private readonly Process _process;
    private bool _exited;
    internal PluginProcessReference(int pid)
    {
        _process = Process.GetProcessById(pid);
        try
        {
            if (OperatingSystem.IsWindows()) { _ = _process.SafeHandle; }
            else
            {
                int descriptor = OpenReference(pid);
                if (descriptor < 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                _unix = new((IntPtr)descriptor, ownsHandle: true);
            }
        }
        catch { _process.Dispose(); throw; }
    }
    internal bool IsRunning
    {
        get
        {
            if (OperatingSystem.IsWindows()) { return !_process.HasExited; }
            if (_exited) { return false; }
            int running = ReferenceIsRunning(_unix!);
            if (running < 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            _exited = running == 0; return !_exited;
        }
    }
    internal string Image
    {
        get
        {
            if (OperatingSystem.IsWindows()) { return _process.MainModule!.FileName; }
            if (OperatingSystem.IsLinux()) { return new FileInfo($"/proc/{_process.Id}/exe").ResolveLinkTarget(true)!.FullName; }
            if (OperatingSystem.IsMacOS())
            {
                byte[] path = new byte[4096];
                int length = ProcPath(_process.Id, path, (uint)path.Length);
                if (length <= 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                return Encoding.UTF8.GetString(path, 0, Array.IndexOf(path, (byte)0));
            }
            throw new PlatformNotSupportedException();
        }
    }
    internal async Task WaitAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (IsRunning) { await Task.Delay(10, deadline.Token); }
    }
    public void Dispose() { _unix?.Dispose(); _process.Dispose(); }
    [DllImport("gitextensions-posix", EntryPoint = "ge_open_process_reference", SetLastError = true)] private static extern int OpenReference(int pid);
    [DllImport("gitextensions-posix", EntryPoint = "ge_process_reference_is_running", SetLastError = true)] private static extern int ReferenceIsRunning(SafeFileHandle descriptor);
    [SupportedOSPlatform("macos")]
    [DllImport("libproc", EntryPoint = "proc_pidpath", SetLastError = true)] private static extern int ProcPath(int pid, [Out] byte[] path, uint size);
}
