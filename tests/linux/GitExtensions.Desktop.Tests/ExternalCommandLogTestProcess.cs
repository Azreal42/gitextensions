using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using Avalonia.Threading;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

// This is a real private executable boundary, never a replacement recorder or fake Process.
[SupportedOSPlatform("linux")]
internal sealed class ExternalCommandLogTestProcess : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "external-log-desktop-" + Guid.NewGuid().ToString("N"));
    internal string Executable => Path.Combine(Root, "user-script-name-secret");
    internal string Marker => Path.Combine(Root, "pid.json");
    private string ReleaseFile => Path.Combine(Root, "release");
    private readonly bool _fast;
    private Process? _process;
    private Task _reads = Task.CompletedTask;
    internal ProcessStartInfo? Requested { get; private set; }
    internal int Pid { get; private set; }
    internal long StartTicks { get; private set; }
    internal string Output { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal ExternalCommandLogTestProcess(int exit = 0, bool fast = false, string stdout = "stdout-secret")
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private process fixtures require Linux."); }
        Directory.CreateDirectory(Root); _fast = fast;
        string code = """
            #!/usr/bin/python3
            import json, os, pathlib, sys, time
            root = pathlib.Path(__file__).parent
            (root/'pid.tmp').write_text(json.dumps({'pid':os.getpid(),'args':sys.argv[1:]}))
            (root/'pid.tmp').replace(root/'pid.json')
            # A fast worker may exit only after Start has acquired the observer's Process object.
            if FAST: sys.stdin.buffer.read(1)
            while not FAST and not (root/'release').exists(): time.sleep(0.01)
            print(OUT)
            print('stderr-secret',file=sys.stderr)
            sys.exit(EXIT)
            """.Replace("FAST", fast ? "True" : "False", StringComparison.Ordinal)
                .Replace("OUT", JsonSerializer.Serialize(stdout), StringComparison.Ordinal)
                .Replace("EXIT", exit.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        File.WriteAllText(Executable, code); File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal ProcessStartInfo Info(params string[] arguments)
    {
        ProcessStartInfo info = new(Executable) { WorkingDirectory = Root, UseShellExecute = false };
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        info.Environment["EXTERNAL_PRIVATE"] = "environment-secret"; return info;
    }
    internal Process Start(ProcessStartInfo requested)
    {
        Requested = requested;
        ProcessStartInfo info = Info(requested.ArgumentList.ToArray());
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.RedirectStandardInput = _fast;
        _process = Process.Start(info) ?? throw new IOException("The private process did not start.");
        Pid = _process.Id; StartTicks = Identity(Pid) ?? 0;
        _reads = ReadAsync(_process);
        // The private fast worker is still blocked on stdin while this second Process object is acquired.
        // Release immediately before returning, preserving the real exit/registration race in the observer.
        Process observation;
        try
        {
            observation = Process.GetProcessById(Pid);
            // ExitCode requires this component's process handle, even when the Unix child wait state is shared.
            // Acquire it before either real gate can release the child; the original component still owns its pipes.
            _ = observation.SafeHandle;
        }
        finally
        {
            if (_fast)
            { _process.StandardInput.Write("R"); _process.StandardInput.Flush(); _process.StandardInput.Close(); }
        }
        return observation;
    }
    private async Task ReadAsync(Process process)
    {
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error); Output = await output; Error = await error;
    }
    internal async Task ReadyAsync()
    {
        await UntilAsync(() => File.Exists(Marker));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Marker));
        int actual = document.RootElement.GetProperty("pid").GetInt32();
        if (Pid == 0) { Pid = actual; StartTicks = Identity(Pid) ?? 0; }
        Assert.That(actual, Is.EqualTo(Pid)); Assert.That(StartTicks, Is.GreaterThan(0)); Assert.That(StillPresent, Is.True);
    }
    internal bool StillPresent => StartTicks > 0 && Identity(Pid) == StartTicks;
    internal string[] Arguments()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Marker));
        return document.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
    }
    internal void Release() { if (Directory.Exists(Root)) { File.WriteAllText(ReleaseFile, "release"); } }
    internal async Task WaitAsync()
    {
        if (_process is not null) { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await _reads.WaitAsync(TimeSpan.FromSeconds(20)); }
        if (StartTicks > 0) { await UntilAsync(() => !StillPresent); }
    }
    internal static async Task UntilAsync(Func<bool> condition, bool pump = true)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20))
        { if (pump && Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.RunJobs(); } await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Private L24 witness did not settle within its deadline.");
    }
    private static long? Identity(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            return long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
    }
    public async ValueTask DisposeAsync()
    {
        Release();
        // Teardown waits/reaps only this owned private child; it never kills an external user app.
        await WaitAsync(); _process?.Dispose(); Directory.Delete(Root, true);
    }
}
