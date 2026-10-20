using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;
using static GitExtensions.Desktop.Tests.ExternalCommandLogTestProcess;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class InteractiveConsoleCommandLogTests
{
    [Test]
    [TestCase(false)] [TestCase(true)]
    public async Task ARealAuthenticatedConsoleUsesTheHostPidAndGitOutcomeEvenWhenItsTerminalBrokerAlreadyExited(bool nonzero)
    {
        using ReferenceFixture repository = await ReferenceFixture.CreateAsync();
        using CancellationTokenSource cancellation = new(); await using PrivateTerminal terminal = new(); GitCommandLog.Clear();
        string[] args = nonzero ? ["add", "--definitely-invalid-option"] : ["status", "--porcelain"];
        Task<GitResult> operation = InteractiveFileConsoleLauncher.RunCoreAsync(repository.Repository.Path, repository.Repository.GitExecutable,
            args, cancellation.Token, terminal.Start, HelperExecutable());
        try
        {
            await terminal.WaitForAsync("Press Enter to close.", operation); int host = terminal.ObserveHost();
            await terminal.WaitBrokerAsync();
            GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
            Assert.That(live.Command, Is.EqualTo("interactive console host")); Assert.That(live.ProcessId, Is.EqualTo(host)); Assert.That(host, Is.Not.EqualTo(terminal.BrokerPid));
            Assert.That(live.IsRunning, Is.True); Assert.That(live.ExitCode, Is.Null); Assert.That(operation.IsCompleted, Is.False);
            Assert.That(terminal.HostPresent, Is.True); Assert.That(terminal.BrokerExited, Is.True, "This oracle must witness a genuinely detached terminal broker.");
            await terminal.SendAsync("\n"); GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
            await terminal.WaitAsync(); GitCommandLogEntry done = GitCommandLog.Snapshot().Single();
            Assert.That(done.Id, Is.EqualTo(live.Id)); Assert.That(done.ProcessId, Is.EqualTo(host)); Assert.That(done.IsRunning, Is.False);
            Assert.That(done.ExitCode, Is.EqualTo(result.ExitCode)); Assert.That(result.ExitCode, nonzero ? Is.Not.Zero : Is.Zero);
            Assert.That(done.Cancelled, Is.False); Assert.That(done.IsUnobserved, Is.False); Assert.That(terminal.HostPresent, Is.False);
            AssertPrivateChannelsAbsent(terminal);
        }
        finally { await FinishAsync(operation, cancellation, terminal); GitCommandLog.Clear(); }
    }

    [AvaloniaTest]
    public async Task OwnerCloseKeepsTheRealInteractiveMutationGuardUntilDrainAndNeverCallsProtocol130AnObservedGitExit()
    {
        using ReferenceFixture repository = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(repository.File, "pending interactive journal hunk\n");
        string other = repository.PathInRepository("other.txt"); await File.WriteAllTextAsync(other, "other\n");
        Window owner = new(); owner.Show(); using CancellationTokenSource cancellation = new(); await using PrivateTerminal terminal = new(); GitCommandLog.Clear();
        Task<GitResult> operation = repository.Repository.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["file.txt"],
            (args, token) => InteractiveFileConsoleLauncher.RunWithTerminalAsync(owner, repository.Repository.Path, repository.Repository.GitExecutable,
                args, token, terminal.Start, HelperExecutable()), cancellation.Token);
        Task? otherMutation = null;
        try
        {
            await terminal.WaitForAsync("Stage this hunk", operation); int host = terminal.ObserveHost();
            await UntilAsync(() => GitCommandLog.Snapshot().Any(x => x.Command == "interactive console host" && x.ProcessId == host));
            GitCommandLogEntry live = GitCommandLog.Snapshot().Single(x => x.Command == "interactive console host");
            otherMutation = repository.Repository.StageAsync(["other.txt"]);
            Assert.That(await Task.WhenAny(otherMutation, Task.Delay(120)), Is.Not.SameAs(otherMutation));
            owner.Close(); Assert.That(owner.IsVisible, Is.True, "The real owning window must defer closure until native drain.");
            Exception? failure = null;
            try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception error) { failure = error; }
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            await otherMutation.WaitAsync(TimeSpan.FromSeconds(20)); await terminal.WaitAsync(); await UntilAsync(() => !owner.IsVisible);
            GitCommandLogEntry done = GitCommandLog.Snapshot().Single(x => x.Id == live.Id);
            Assert.That(done.ProcessId, Is.EqualTo(host)); Assert.That(done.IsRunning, Is.False); Assert.That(done.Cancelled, Is.True);
            Assert.That(done.ExitCode, Is.Null, "130 is a protocol cancellation code, not a directly observed Git exit.");
            Assert.That(done.IsUnobserved, Is.False); Assert.That(terminal.HostPresent, Is.False); AssertPrivateChannelsAbsent(terminal);
            Assert.That((await repository.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("base\n"));
            Assert.That((await repository.GitAsync("show", ":other.txt")).StandardOutput, Is.EqualTo("other\n"));
        }
        finally
        {
            await FinishAsync(operation, cancellation, terminal);
            if (otherMutation is not null) { await otherMutation.WaitAsync(TimeSpan.FromSeconds(20)); }
            owner.Close(); await UntilAsync(() => !owner.IsVisible); GitCommandLog.Clear();
        }
    }

    [Test]
    public async Task RealHelperLaunchErrorDoesNotPromoteTheProtocolErrorCodeOrMessageToAnObservedExitOrMetadata()
    {
        using ReferenceFixture repository = await ReferenceFixture.CreateAsync();
        using CancellationTokenSource cancellation = new(); await using PrivateTerminal terminal = new(); GitCommandLog.Clear();
        string missing = Path.Combine(repository.Repository.Path, "git-executable-error-secret");
        string missingRepository = Path.Combine(repository.Repository.Path, "absent-repository");
        // The helper rejects the absent directory after ACK, before attempting the private executable.
        // Directory is visible journal metadata; executable/argument sentinels stay in the private request.
        Task<GitResult> operation = InteractiveFileConsoleLauncher.RunCoreAsync(missingRepository, missing, ["status", "argument-secret"],
            cancellation.Token, terminal.Start, HelperExecutable());
        try
        {
            Assert.CatchAsync<IOException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(20))); await terminal.WaitAsync();
            int host = terminal.ExpectedHostPid;
            GitCommandLogEntry done = GitCommandLog.Snapshot().Single(); Assert.That(done.Command, Is.EqualTo("interactive console host"));
            Assert.That(done.ProcessId, Is.EqualTo(host)); Assert.That(done.IsRunning, Is.False); Assert.That(done.ExitCode, Is.Null);
            Assert.That(done.Cancelled, Is.False); Assert.That(done.IsUnobserved, Is.False); AssertPrivateChannelsAbsent(terminal);
            Assert.That(JsonSerializer.Serialize(GitCommandLog.Snapshot()), Does.Not.Contain("git-executable-error-secret").And.Not.Contain("argument-secret"));
        }
        finally { await FinishAsync(operation, cancellation, terminal); GitCommandLog.Clear(); }
    }

    [Test]
    public async Task ARealSameUserPeerWithTheWrongNonceIsNotPublishedAsAnAuthenticatedHost()
    {
        using ReferenceFixture repository = await ReferenceFixture.CreateAsync();
        string root = Path.Combine(Path.GetTempPath(), "journal-invalid-peer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string worker = Path.Combine(root, "peer.py"); Process? peer = null; GitCommandLog.Clear();
        try
        {
            File.WriteAllText(worker, """
                import json,socket,sys,time
                request=json.load(open(sys.argv[1]))
                s=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM); s.connect(request['SocketPath'])
                s.sendall((json.dumps({'Nonce':'wrong-private-nonce-secret'})+'\n').encode())
                time.sleep(0.1); s.close()
                """);
            Assert.CatchAsync<InvalidDataException>(async () => await InteractiveFileConsoleLauncher.RunCoreAsync(repository.Repository.Path, repository.Repository.GitExecutable,
                ["status"], CancellationToken.None, (_, arguments) =>
                {
                    ProcessStartInfo info = new("/usr/bin/python3") { UseShellExecute = false, ArgumentList = { worker, arguments[1] } };
                    peer = Process.Start(info)!; return Process.GetProcessById(peer.Id);
                }, HelperExecutable()).WaitAsync(TimeSpan.FromSeconds(20)));
            await peer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            GitCommandLogEntry done = GitCommandLog.Snapshot().Single(); Assert.That(done.ProcessId, Is.Null);
            Assert.That(done.ExitCode, Is.Null); Assert.That(done.IsRunning, Is.False); Assert.That(done.IsUnobserved, Is.True);
            Assert.That(done.Cancelled, Is.False, "A launched terminal with no authenticated host/result is an unobserved handoff, not an observed process failure.");
            Assert.That(JsonSerializer.Serialize(GitCommandLog.Snapshot()), Does.Not.Contain("wrong-private-nonce-secret"));
        }
        finally { if (peer is not null) { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); peer.Dispose(); } Directory.Delete(root, true); GitCommandLog.Clear(); }
    }

    private static void AssertPrivateChannelsAbsent(PrivateTerminal terminal)
    {
        string material = JsonSerializer.Serialize(GitCommandLog.Snapshot());
        foreach (string value in new[] { terminal.RequestPath!, terminal.Nonce!, "private-terminal-output-secret" })
        { Assert.That(material.Contains(value, StringComparison.Ordinal), Is.False, "Private interactive transport entered journal metadata."); }
    }
    private static async Task FinishAsync(Task<GitResult> operation, CancellationTokenSource cancellation, PrivateTerminal terminal)
    {
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) { } catch (IOException) { }
        finally { await terminal.WaitAsync(); }
    }
    private static string HelperExecutable()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "GitExtensions"); if (File.Exists(direct)) { return direct; }
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src/linux/GitExtensions.Desktop"))) { directory = directory.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(directory?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(path), Is.True, "Build and pin the real Desktop apphost before these protocol tests."); return path;
    }

    private sealed class PrivateTerminal : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-private-terminal-" + Guid.NewGuid().ToString("N"));
        private string HostMarker => Path.Combine(_root, "host.pid");
        internal PrivateTerminal() { Directory.CreateDirectory(_root); }
        internal int ExpectedHostPid => int.Parse(File.ReadAllText(HostMarker), System.Globalization.CultureInfo.InvariantCulture);
        private Process? _pty, _broker;
        private Task _read = Task.CompletedTask;
        private readonly StringBuilder _output = new();
        private int _host; private long _hostStart;
        private string? _helper;
        internal string? RequestPath { get; private set; }
        internal string? Nonce { get; private set; }
        internal int BrokerPid => _broker!.Id;
        internal bool BrokerExited => _broker!.HasExited;
        internal bool HostPresent => _hostStart > 0 && Identity(_host)?.Start == _hostStart;
        private string Output { get { lock (_output) { return _output.ToString(); } } }
        internal Process Start(string executable, IReadOnlyList<string> arguments)
        {
            _helper = Path.GetFullPath(executable); RequestPath = arguments[1];
            InteractiveFileConsoleRequest request = JsonSerializer.Deserialize<InteractiveFileConsoleRequest>(File.ReadAllText(RequestPath))!; Nonce = request.Nonce;
            string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            ProcessStartInfo info = new("/usr/bin/script") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            string command = "printf '%s\\n' \"$$\" > " + Quote(HostMarker) + "; exec " + string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
            foreach (string value in new[] { "--quiet", "--return", "--flush", "--command", command, "/dev/null" })
            { info.ArgumentList.Add(value); }
            info.Environment["TERM"] = "xterm-256color";
            info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            _pty = Process.Start(info)!; _read = Task.WhenAll(ReadAsync(_pty.StandardOutput), ReadAsync(_pty.StandardError));
            // The real broker waits privately until its second Process object has been acquired.
            // Releasing stdin immediately before return leaves its exit independent of the real host job.
            _broker = Process.Start(new ProcessStartInfo("/bin/sh")
                { UseShellExecute = false, RedirectStandardInput = true, ArgumentList = { "-c", "IFS= read -r ready; exit 0" } })!;
            Process observation;
            try { observation = Process.GetProcessById(_broker.Id); }
            finally
            { _broker.StandardInput.WriteLine("ready"); _broker.StandardInput.Flush(); _broker.StandardInput.Close(); }
            return observation;
        }
        private async Task ReadAsync(StreamReader reader)
        {
            char[] buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0) { lock (_output) { _output.Append(buffer, 0, count); } }
        }
        internal async Task WaitForAsync(string text, Task operation)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!Output.Contains(text, StringComparison.Ordinal) && timer.Elapsed < TimeSpan.FromSeconds(20))
            { if (operation.IsCompleted) { await operation; break; } await Task.Delay(10); }
            Assert.That(Output.Contains(text, StringComparison.Ordinal), Is.True, "The real private PTY did not reach the expected protocol stage.");
        }
        internal int ObserveHost()
        {
            Assert.That(_pty, Is.Not.Null);
            IEnumerable<int> Descendants(int pid)
            {
                string path = $"/proc/{pid}/task/{pid}/children"; string? data = null;
                try { data = File.ReadAllText(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
                if (data is null) { yield break; }
                foreach (int child in data.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)))
                { yield return child; foreach (int descendant in Descendants(child)) { yield return descendant; } }
            }
            int[] hosts = Descendants(_pty!.Id).Where(pid =>
            {
                try
                {
                    string[] args = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    return new FileInfo($"/proc/{pid}/exe").LinkTarget == _helper && args.Contains(RequestPath!) && args.Contains(InteractiveFileConsoleProtocol.Mode);
                }
                catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
            }).ToArray();
            Assert.That(hosts, Has.Length.EqualTo(1)); Assert.That(hosts[0], Is.EqualTo(ExpectedHostPid)); _host = hosts[0]; _hostStart = Identity(_host)!.Value.Start; return _host;
        }
        internal Task WaitBrokerAsync() => _broker!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        internal async Task SendAsync(string text) { await _pty!.StandardInput.WriteAsync(text); await _pty.StandardInput.FlushAsync(); }
        internal async Task WaitAsync()
        {
            if (_pty is not null) { await _pty.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await _read.WaitAsync(TimeSpan.FromSeconds(20)); }
            if (_broker is not null) { await WaitBrokerAsync(); }
            if (_hostStart > 0) { await UntilAsync(() => !HostPresent, false); }
            if (RequestPath is not null) { Assert.That(Directory.Exists(Path.GetDirectoryName(RequestPath)), Is.False, "The drained operation must remove request/socket/nonce directory."); }
        }
        private static (long Start, string State)? Identity(int pid)
        {
            try { string stat = File.ReadAllText($"/proc/{pid}/stat"); string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); return (long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture), fields[0]); }
            catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
        }
        public async ValueTask DisposeAsync() { if (_pty is not null && !_pty.HasExited) { _pty.StandardInput.Close(); } await WaitAsync(); _pty?.Dispose(); _broker?.Dispose(); Directory.Delete(_root, true); }
    }
}
