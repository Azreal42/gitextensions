using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using System.Net.Sockets;

internal static class NativeProcessProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 3 && args[0] == "socket-peer")
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return 2; }
            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(args[2]));
            int owner = GitExtensions.Linux.PosixSocketPeerProcess.Authenticate(socket);
            await GitExtensions.Linux.PosixSocketPeerProcess.SendCurrentAsync(socket);
            await PublishAsync(Path.Combine(args[1], "peer-ready.json"), new { pid = Environment.ProcessId, owner });
            await UntilFileAsync(Path.Combine(args[1], "close-socket"));
            socket.Dispose();
            await PublishAsync(Path.Combine(args[1], "peer-closed.json"), new { pid = Environment.ProcessId });
            await UntilFileAsync(Path.Combine(args[1], "release"));
            return 0;
        }
        if (args.Length >= 3 && args[0] == "terminal")
        {
            using FileStream controlling = new("/dev/tty", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            await PublishAsync(Path.Combine(args[1], "terminal-ready.json"), new
            {
                pid = Environment.ProcessId, session = GetSession(0), group = GetGroup(),
                stdin = IsTerminal(0), stdout = IsTerminal(1), stderr = IsTerminal(2),
                arguments = args[3..], cwd = Directory.GetCurrentDirectory(),
                value = Environment.GetEnvironmentVariable("GITEXTENSIONS_NATIVE_PROBE_VALUE")
            });
            await Console.OpenStandardOutput().WriteAsync("terminal-prompt>"u8.ToArray());
            using StreamReader input = new(Console.OpenStandardInput(), Encoding.UTF8, false);
            string? line = await input.ReadLineAsync();
            await PublishAsync(Path.Combine(args[1], "terminal-input.json"), new { line });
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("terminal-answer=" + line + "\n"));
            return int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (args.Length >= 3 && args[0] == "terminal-controller")
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return 2; }
            string before = await TerminalSettingsAsync();
            GitExtensions.Linux.GitResult result = await GitExtensions.Linux.GitInteractiveTerminalProcess.RunAsync(args[1], args[2], args[3..]);
            string returned = await TerminalSettingsAsync();
            await Console.OpenStandardOutput().WriteAsync("\ncontroller-resume>"u8.ToArray());
            string? line = await ReadTerminalLineAsync();
            string after = await TerminalSettingsAsync();
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("\nterminal-restore:" + JsonSerializer.Serialize(new { before, returned, after, line }) + "\n"));
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("controller-result=" + result.ExitCode + "\n"));
            return result.ExitCode;
        }
        if (args.Length == 2 && args[0] == "terminal-flood")
        {
            await PublishAsync(Path.Combine(args[1], "flood-ready.json"), new { pid = Environment.ProcessId });
            await Console.OpenStandardOutput().WriteAsync(Enumerable.Repeat((byte)'x', 32 * 1024 * 1024).ToArray());
            await PublishAsync(Path.Combine(args[1], "flood-finished.json"), new { pid = Environment.ProcessId });
            return 0;
        }
        if (args.Length == 2 && args[0] == "terminal-hold")
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return 2; }
            using PosixSignalRegistration hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
            await PublishAsync(Path.Combine(args[1], "terminal-child.json"), new { pid = Environment.ProcessId });
            await UntilFileAsync(Path.Combine(args[1], "release")); return 0;
        }
        if (args.Length == 2 && args[0] == "terminal-tree")
        {
            ProcessStartInfo start = new(Environment.ProcessPath!)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string value in new[] { "--native-process-probe", "terminal-hold", args[1] }) { start.ArgumentList.Add(value); }
            using Process child = Process.Start(start)!;
            await UntilFileAsync(Path.Combine(args[1], "terminal-child.json"));
            await PublishAsync(Path.Combine(args[1], "terminal-tree.json"), new { parent = Environment.ProcessId, child = child.Id });
            while (!File.Exists(Path.Combine(args[1], "parent-exit")) && !File.Exists(Path.Combine(args[1], "release"))) { await Task.Delay(10); }
            return 0;
        }
        if (args.Length == 2 && args[0] == "hold")
        {
            await Console.OpenStandardOutput().WriteAsync("probe-child-output\n"u8.ToArray());
            await Console.OpenStandardError().WriteAsync("probe-child-error\n"u8.ToArray());
            await PublishAsync(Path.Combine(args[1], "child-ready.json"), new { pid = Environment.ProcessId });
            await UntilFileAsync(Path.Combine(args[1], "release")); return 0;
        }
        if (args.Length == 3 && args[0] == "tree")
        {
            bool independentPipes = args[2] == "private";
            ProcessStartInfo child = new(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                // One redirected handle makes Process populate STARTF_USESTDHANDLES
                // on Windows; the two nonredirected outputs then inherit our real pipes.
                RedirectStandardInput = true,
                RedirectStandardOutput = independentPipes, RedirectStandardError = independentPipes
            };
            foreach (string value in new[] { "--native-process-probe", "hold", args[1] }) { child.ArgumentList.Add(value); }
            using Process process = Process.Start(child)!;
            await UntilFileAsync(Path.Combine(args[1], "child-ready.json"));
            await PublishAsync(Path.Combine(args[1], "tree-ready.json"), new { parent = Environment.ProcessId, child = process.Id });
            while (!File.Exists(Path.Combine(args[1], "allow-parent-exit")) && !File.Exists(Path.Combine(args[1], "release")))
            { await Task.Delay(10); }
            return 0;
        }
        if (args.Length >= 3 && args[0] == "duplex")
        {
            byte[] prefix = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
            Stream output = Console.OpenStandardOutput(), error = Console.OpenStandardError();
            await output.WriteAsync(prefix); await error.WriteAsync(prefix);
            using MemoryStream input = new(); await Console.OpenStandardInput().CopyToAsync(input);
            await output.WriteAsync(input.ToArray());
            await PublishAsync(args[1], new { arguments = args[3..], cwd = Directory.GetCurrentDirectory(),
                value = Environment.GetEnvironmentVariable("GITEXTENSIONS_NATIVE_PROBE_VALUE") });
            return int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        return 2;
    }

    [DllImport("libc", EntryPoint = "isatty")] private static extern int IsTerminal(int descriptor);
    [DllImport("libc", EntryPoint = "getsid")] private static extern int GetSession(int pid);
    [DllImport("libc", EntryPoint = "getpgrp")] private static extern int GetGroup();
    [DllImport("libc", EntryPoint = "ttyname_r")]
    private static extern int TerminalName(int descriptor, [Out] byte[] buffer, nuint count);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern long ReadInput(int descriptor, [Out] byte[] buffer, nuint count);

    private static Task<string?> ReadTerminalLineAsync() => Task.Run(() =>
    {
        // The caller's real canonical read must not ask Console to enter its
        // own application mode while the oracle measures the restored state.
        List<byte> line = []; byte[] value = new byte[1];
        while (true)
        {
            long count = ReadInput(0, value, 1);
            if (count < 0 && Marshal.GetLastPInvokeError() == 4) { continue; }
            if (count < 0) { throw new IOException("The resumed terminal input could not be read."); }
            if (count == 0) { return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray()); }
            if (value[0] == (byte)'\n') { return Encoding.UTF8.GetString(line.ToArray()); }
            line.Add(value[0]);
        }
    });

    private static async Task<string> TerminalSettingsAsync()
    {
        byte[] name = new byte[4096];
        int error = TerminalName(0, name, (nuint)name.Length);
        if (error != 0) { throw new IOException("The independent oracle cannot identify its terminal: " + error); }
        string terminal = Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));
        // Process configures the console when any standard stream is inherited.
        // Redirect all three and query the explicit device, so measuring the
        // attributes does not itself change them before stty observes them.
        ProcessStartInfo start = new("stty")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-g");
        start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-F"); start.ArgumentList.Add(terminal);
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, errors, process.WaitForExitAsync()); string result = output.Result;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(result))
        { throw new IOException("The independent terminal-state oracle failed: " + errors.Result); }
        return result.Trim();
    }

    private static async Task UntilFileAsync(string path)
    { while (!File.Exists(path)) { await Task.Delay(10); } }

    private static async Task PublishAsync<T>(string path, T value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path);
    }
}
