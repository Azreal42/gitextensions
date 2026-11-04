using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;

internal static class NativeProcessProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
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
            using StreamReader resumed = new(Console.OpenStandardInput(), Encoding.UTF8, false);
            string? line = await resumed.ReadLineAsync();
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

    private static async Task<string> TerminalSettingsAsync()
    {
        ProcessStartInfo start = new("stty") { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-g");
        using Process process = Process.Start(start)!;
        string result = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) { throw new IOException("The independent terminal-state oracle failed."); }
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
