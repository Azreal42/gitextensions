using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using GitExtensions.Linux;

internal static class UnixSocketDirectoryScenarios
{
    private const string Prefix = "ge-test-";
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("private Unix sockets preserve normal TMPDIR and real Git launch/exec-error cleanup", () => WorkerAsync("normal")),
        ("a Unix socket of exactly107 UTF8 bytes binds without fallback in a fresh process", () => WorkerAsync("boundary")),
        ("a Unix socket of108 UTF8 bytes uses a private short fallback without changing TMPDIR", () => WorkerAsync("over")),
        ("real Git and socket IO work with a long ASCII TMPDIR present at process startup", () => WorkerAsync("long")),
        ("cancellation drains an actual Git alias under a long startup TMPDIR", () => WorkerAsync("cancel")),
        ("UTF8 byte length rather than character count controls fallback before real Git starts", () => WorkerAsync("unicode")),
        ("private Unix directories normalize inherited setgid permissions", () => WorkerAsync("setgid")),
        ("private Unix directories normalize restrictive umask without changing it", () => WorkerAsync("umask")),
        ("an unusable short TMPDIR reports ENOENT instead of silently falling back", () => WorkerAsync("missing")),
        ("an unwritable short TMPDIR reports EACCES instead of silently falling back", () => WorkerAsync("permission")),
        ("private socket cleanup preserves symlink targets and rejects path-like prefixes", CleanupAsync),
    ];
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private static async Task WorkerAsync(string scenario)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        string root = Path.Combine("/tmp", "ges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateMode);
        try
        {
            int suffix = Encoding.UTF8.GetByteCount("/" + Prefix + "XXXXXX/control");
            string temporary = scenario switch
            {
                "boundary" or "over" => Path.Combine(root, new string('b', 107 - suffix - root.Length - 1 + (scenario == "over" ? 1 : 0))),
                "long" or "cancel" => Path.Combine(root, new string('a', 80), new string('b', 80)),
                "unicode" => Path.Combine(root, new string('é', 25)),
                "missing" => Path.Combine(root, "missing", "parent"),
                _ => Path.Combine(root, "temporary"),
            };
            if (scenario != "missing") { Directory.CreateDirectory(temporary, PrivateMode); }
            if (scenario == "setgid")
            { File.SetUnixFileMode(temporary, PrivateMode | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup); }
            if (scenario == "permission") { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing Core test host.");
            ProcessStartInfo start = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable) == "dotnet") { start.ArgumentList.Add(typeof(UnixSocketDirectoryScenarios).Assembly.Location); }
            start.ArgumentList.Add("--unix-socket-directory-worker"); start.ArgumentList.Add(scenario); start.ArgumentList.Add(root);
            start.Environment["TMPDIR"] = temporary;
            using Process child = Process.Start(start) ?? throw new IOException("Cannot start the private socket regression worker.");
            Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } throw; }
            string actualOutput = await output, actualError = await error;
            Check(child.ExitCode == 0, $"Fresh worker {scenario}, PID {child.Id}, actual exit {child.ExitCode}: {actualOutput}\n{actualError}");
            Console.WriteLine(actualOutput.TrimEnd());
            if (scenario != "missing")
            { Check(!Directory.EnumerateFileSystemEntries(temporary).Any(), "The private worker left temporary socket/exec-error files."); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static async Task<int> RunWorkerAsync(string scenario, string root)
    {
        if (!OperatingSystem.IsLinux()) { return 1; }
        string temporary = Environment.GetEnvironmentVariable("TMPDIR") ?? throw new InvalidOperationException("No startup TMPDIR.");
        if (scenario is "missing" or "permission")
        {
            int expected = scenario == "missing" ? 2 : 13;
            try { using LinuxUnixSocketDirectory unexpected = LinuxUnixSocketDirectory.Create(Prefix); throw new InvalidOperationException("An unusable short TMPDIR was ignored."); }
            catch (IOException error) when (error.InnerException is Win32Exception native && native.NativeErrorCode == expected) { }
            Check(Environment.GetEnvironmentVariable("TMPDIR") == temporary, "An allocation error changed TMPDIR.");
            Console.WriteLine($"worker {scenario}: native errno {expected}, no fallback"); return 0;
        }
        int candidateBytes = Encoding.UTF8.GetByteCount(Path.Combine(temporary, Prefix + "XXXXXX", "control"));
        bool fallback = scenario is "long" or "cancel" or "unicode" or "over";
        if (scenario == "unicode")
        { Check(Path.Combine(temporary, Prefix + "XXXXXX", "control").Length <= 107 && candidateBytes > 107, "The Unicode fixture must exceed only the byte bound."); }
        if (scenario == "boundary") { Check(candidateBytes == 107, "Boundary fixture is not107 bytes."); }
        if (scenario == "over") { Check(candidateBytes == 108, "Over-boundary fixture is not108 bytes."); }
        string directory, socketPath;
        uint previousMask = scenario == "umask" ? SetCreationMask(0x1FF) : 0;
        LinuxUnixSocketDirectory allocation;
        try { allocation = LinuxUnixSocketDirectory.Create(Prefix); }
        finally { if (scenario == "umask") { Check(SetCreationMask(previousMask) == 0x1FF, "Allocation changed the process umask."); } }
        using (allocation)
        {
            directory = allocation.DirectoryPath; socketPath = allocation.SocketPath;
            Check(Path.GetDirectoryName(directory) == (fallback ? "/tmp" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporary))), "The selected temporary parent is wrong.");
            Check(Encoding.UTF8.GetByteCount(socketPath) <= 107 && Path.GetFileName(socketPath) == "control", "The allocated socket does not fit sockaddr_un.");
            Check(File.GetUnixFileMode(directory) == PrivateMode && new DirectoryInfo(directory).LinkTarget is null, "The allocation is not a fresh private0700 directory.");
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath)); listener.Listen(1);
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            using Socket peer = await listener.AcceptAsync(timeout.Token);
            Check(await client.SendAsync(new byte[] { 0x5A }, SocketFlags.None, timeout.Token) == 1, "No native socket payload was sent.");
            byte[] bytes = new byte[1];
            Check(await peer.ReceiveAsync(bytes.AsMemory(), SocketFlags.None, timeout.Token) == 1 && bytes[0] == 0x5A, "Native socket payload changed.");
        }
        allocation.Dispose();
        Check(!Directory.Exists(directory) && !File.Exists(socketPath), "Socket allocation did not clean up.");
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(root, ["--version"]);
        Check(result.Success && result.StandardOutput.StartsWith("git version ", StringComparison.Ordinal), "Actual Git never executed successfully.");
        try
        {
            await new GitRunner(Path.Combine(root, "nonexistent-git")).RunAsync(root, ["--version"]);
            throw new InvalidOperationException("An exec failure was fabricated as success.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { }
        if (scenario == "cancel") { await CancelGitAsync(root); }
        Check(Environment.GetEnvironmentVariable("TMPDIR") == temporary && Path.TrimEndingDirectorySeparator(Path.GetTempPath()) == Path.TrimEndingDirectorySeparator(temporary), "Allocation changed TMPDIR or unrelated temporary paths.");
        Console.WriteLine($"worker {scenario}: native socket {Encoding.UTF8.GetByteCount(socketPath)} bytes, fallback={fallback}, real {result.StandardOutput.Trim()}, exec failure preserved");
        return 0;
    }

    private static async Task CancelGitAsync(string root)
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<int> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StringBuilder received = new();
        Task<GitResult> pending = new GitRunner("/usr/bin/git").RunAsync(root,
            ["-c", "alias.socket-wait=!echo $$; exec sleep 60", "socket-wait"], cancellation.Token,
            outputReceived: output =>
            {
                if (output.Stream != GitProcessOutputStream.StandardOutput) { return; }
                received.Append(Encoding.UTF8.GetString(output.Bytes.Span));
                if (received.ToString().Contains('\n') && int.TryParse(received.ToString().Trim(), out int pid)) { ready.TrySetResult(pid); }
            });
        try
        {
            int pid = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Git cancellation returned a fabricated result."); }
            catch (OperationCanceledException) { }
            Check(!LinuxProcessState.IsRunning(pid), "The private alias process survived cancellation.");
        }
        finally { cancellation.Cancel(); try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    private static Task CleanupAsync()
    {
        if (!OperatingSystem.IsLinux()) { return Task.CompletedTask; }
        using LinuxUnixSocketDirectory target = LinuxUnixSocketDirectory.Create(Prefix);
        string sentinel = Path.Combine(target.DirectoryPath, "sentinel"); File.WriteAllText(sentinel, "keep");
        LinuxUnixSocketDirectory nested = LinuxUnixSocketDirectory.Create(Prefix);
        Directory.CreateSymbolicLink(Path.Combine(nested.DirectoryPath, "foreign"), target.DirectoryPath);
        nested.Dispose(); nested.Dispose();
        Check(File.ReadAllText(sentinel) == "keep", "Recursive cleanup followed a nested symlink.");
        using LinuxUnixSocketDirectory allocation = LinuxUnixSocketDirectory.Create(Prefix);
        string moved = allocation.DirectoryPath + "-owned";
        Directory.Move(allocation.DirectoryPath, moved);
        Directory.CreateSymbolicLink(allocation.DirectoryPath, target.DirectoryPath);
        try
        {
            try { allocation.Dispose(); throw new InvalidOperationException("A substituted directory symlink was accepted."); }
            catch (IOException) { }
            Check(File.ReadAllText(sentinel) == "keep", "Cleanup followed a substituted symlink.");
            foreach (string prefix in new[] { "../outside", "a/b", "a\0b", new string('x', 100) })
            {
                try { using LinuxUnixSocketDirectory unexpected = LinuxUnixSocketDirectory.Create(prefix); throw new InvalidOperationException("An unsafe prefix was accepted."); }
                catch (ArgumentException) { }
            }
        }
        finally { Directory.Delete(allocation.DirectoryPath); Directory.Move(moved, allocation.DirectoryPath); }
        return Task.CompletedTask;
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetCreationMask(uint mask);
}
