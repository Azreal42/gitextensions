using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Native.Tests;

[TestFixture, Category("PosixTerminal"), NonParallelizable, Platform("Linux,MacOsX")]
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class PosixTerminalTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(25);

    [TestCase(0)]
    [TestCase(73)]
    public async Task NativeTerminalOwnsItsControllingTtyAndPreservesLiteralArgumentsEnvironmentAndExit(int exit)
    {
        using Fixture f = new();
        string[] literal = ["", "spaces ' λ $(literal)", "quote\"", "line\nbreak", "back\\slash\\"];
        await using Session session = new(f.Root, ["terminal", f.Root, exit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. literal]);
        await session.UntilAsync("terminal-prompt>");
        using JsonDocument ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.Root, "terminal-ready.json")));
        JsonElement value = ready.RootElement; int pid = value.GetProperty("pid").GetInt32();
        Assert.That(value.GetProperty("session").GetInt32(), Is.EqualTo(pid));
        Assert.That(value.GetProperty("group").GetInt32(), Is.EqualTo(pid));
        foreach (string stream in new[] { "stdin", "stdout", "stderr" }) { Assert.That(value.GetProperty(stream).GetInt32(), Is.EqualTo(1)); }
        Assert.That(value.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(literal));
        Assert.That(value.GetProperty("cwd").GetString(), Is.EqualTo(NativeProcessOwnershipTests.ExpectedWorkingDirectory(f.Root)));
        Assert.That(value.GetProperty("value").GetString(), Is.EqualTo("captured terminal λ '$()"));
        const string answer = "one ' λ [*] literal";
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"));
        Assert.That(await session.CompleteAsync(), Is.EqualTo(exit));
        using JsonDocument received = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.Root, "terminal-input.json")));
        Assert.That(received.RootElement.GetProperty("line").GetString(), Is.EqualTo(answer));
        Assert.That(session.Output, Does.Contain("terminal-answer=" + answer));
    }

    [Test]
    public async Task PublicInteractiveApiStagesARealGitHunkThroughItsOwnTerminal()
    {
        using Fixture f = new(); string repository = Path.Combine(f.Root, "repository"); Directory.CreateDirectory(repository);
        GitRunner git = new("git");
        async Task<GitResult> Run(params string[] args)
        {
            GitResult result = await git.RunAsync(repository, args, environment: new Dictionary<string, string?>
            { ["GIT_CONFIG_GLOBAL"] = Path.Combine(f.Root, "empty-config"), ["GIT_CONFIG_NOSYSTEM"] = "1" });
            Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        await File.WriteAllTextAsync(Path.Combine(f.Root, "empty-config"), "");
        await Run("init", "-b", "main"); await Run("config", "user.name", "Terminal Test"); await Run("config", "user.email", "terminal@example.invalid");
        await Run("config", "commit.gpgsign", "false"); await Run("config", "core.hooksPath", Path.Combine(f.Root, "no-hooks"));
        string file = Path.Combine(repository, "file.txt"); await File.WriteAllTextAsync(file, "one\nbase\nthree\n");
        await Run("add", "--", "file.txt"); await Run("commit", "-m", "Base");
        await File.WriteAllTextAsync(file, "one\nselected λ\nthree\n");
        await using Session session = new(repository, ["terminal-controller", repository, "git", "-c", "color.ui=false", "add", "--patch", "--", "file.txt"]);
        await session.UntilAsync("Stage this hunk"); await session.Input.WriteAsync("y\n"u8.ToArray());
        await session.UntilAsync("controller-resume>");
        const string resumed = "ordinary input after Git ' λ";
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes(resumed + "\n"));
        Assert.That(await session.CompleteAsync(), Is.EqualTo(0));
        Assert.That(session.Output, Does.Contain("controller-result=0"));
        string restore = session.Output.Split('\n').Select(line => line.TrimEnd('\r')).Single(line => line.StartsWith("terminal-restore:", StringComparison.Ordinal));
        TestContext.WriteLine(restore);
        using JsonDocument settings = JsonDocument.Parse(restore["terminal-restore:".Length..]);
        Assert.That(settings.RootElement.GetProperty("line").GetString(), Is.EqualTo(resumed));
        // Darwin sets PENDIN when returning to canonical mode; the next actual
        // read reprocesses pending input and clears that kernel state. Compare
        // every saved attribute after that read, and retain the immediate
        // returned snapshot in the record for diagnostics.
        Assert.That(settings.RootElement.GetProperty("after").GetString(), Is.EqualTo(settings.RootElement.GetProperty("before").GetString()),
            "The caller's terminal must accept ordinary input with every saved attribute restored.");
        Assert.That((await Run("show", ":file.txt")).StandardOutput, Is.EqualTo("one\nselected λ\nthree\n"));
    }

    [Test]
    public async Task UnreadTerminalOutputCannotBlockCancellationOrOutliveTheStoppedActors()
    {
        using Fixture f = new(); await using Session session = new(f.Root, ["terminal-flood", f.Root], captureOutput: false);
        await UntilFileAsync(Path.Combine(f.Root, "flood-ready.json"));
        using JsonDocument ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.Root, "flood-ready.json")));
        using NativeProcessOwnershipTests.NativeActor child = new(Process.GetProcessById(ready.RootElement.GetProperty("pid").GetInt32()));
        byte[] first = new byte[1];
        Assert.That(await session.OutputStream.ReadAsync(first).AsTask().WaitAsync(Limit), Is.EqualTo(1));
        Assert.That(first[0], Is.EqualTo((byte)'x'));
        Assert.That(File.Exists(Path.Combine(f.Root, "flood-finished.json")), Is.False,
            "The real writer must remain inside its terminal output, beyond the bounded relay and pipe buffers.");
        Assert.That(child.IsRunning, Is.True);
        await session.StopAsync();
        Assert.That(session.Exit.IsCompletedSuccessfully, Is.True); Assert.That(child.IsRunning, Is.False);
    }

    [Test]
    public async Task CancellationDrainsAChildAfterItsTerminalLeaderAndAllItsTtyDescriptorsAreGone()
    {
        using Fixture f = new(); await using Session session = new(f.Root, ["terminal-tree", f.Root]);
        string marker = Path.Combine(f.Root, "terminal-tree.json");
        await UntilFileAsync(marker);
        using JsonDocument ready = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        using NativeProcessOwnershipTests.NativeActor parent = new(Process.GetProcessById(ready.RootElement.GetProperty("parent").GetInt32()));
        using NativeProcessOwnershipTests.NativeActor child = new(Process.GetProcessById(ready.RootElement.GetProperty("child").GetInt32()));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(f.Root, "parent-exit"), "go");
            await parent.WaitForExitAsync().WaitAsync(Limit);
            Assert.That(child.IsRunning, Is.True);
            Assert.That(session.Exit.IsCompleted, Is.False, "A closed PTY is not proof that its live descendant stopped.");
            await session.StopAsync();
            Assert.That(parent.IsRunning, Is.False); Assert.That(child.IsRunning, Is.False);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(f.Root, "release"), "release");
            await session.StopAsync();
            await child.WaitForExitAsync().WaitAsync(Limit);
        }
    }

    private static async Task UntilFileAsync(string path)
    {
        using CancellationTokenSource timeout = new(Limit);
        while (!File.Exists(path)) { await Task.Delay(10, timeout.Token); }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "native terminal ' λ " + Guid.NewGuid().ToString("N"));
        internal Fixture() { Directory.CreateDirectory(Root); }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Session : IAsyncDisposable
    {
        private readonly PosixGitProcess _process;
        private readonly ConcurrentQueue<byte[]> _output = new();
        private readonly Task _outputRead, _errorRead;
        internal Task Exit => _process.WaitForExitAsync();
        internal Stream Input => _process.StandardInput;
        internal Stream OutputStream => _process.StandardOutput;
        internal string Output => Encoding.UTF8.GetString(_output.SelectMany(x => x).ToArray());
        internal Session(string directory, string[] args, bool captureOutput = true)
        {
            ProcessStartInfo start = new(NativeProcessOwnershipTests.Executable) { WorkingDirectory = directory };
            foreach (string arg in new[] { "--native-process-probe" }.Concat(args)) { start.ArgumentList.Add(arg); }
            start.Environment["GITEXTENSIONS_NATIVE_PROBE_VALUE"] = "captured terminal λ '$()";
            _process = PosixGitProcess.Start(start, CancellationToken.None, terminal: true);
            _outputRead = captureOutput ? ReadAsync(_process.StandardOutput) : Task.CompletedTask; _errorRead = ReadAsync(_process.StandardError);
        }
        private async Task ReadAsync(Stream stream)
        {
            byte[] buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer)) != 0) { _output.Enqueue(buffer[..count]); }
        }
        internal async Task UntilAsync(string marker)
        {
            using CancellationTokenSource timeout = new(Limit);
            while (!Output.Contains(marker, StringComparison.Ordinal))
            {
                if (_process.Control.IsFaulted) { await _process.Control; }
                if (Exit.IsCompleted) { await CompleteAsync(); Assert.Fail("Terminal exited before " + marker + ": " + Output); }
                await Task.Delay(10, timeout.Token);
            }
        }
        internal async Task<int> CompleteAsync()
        { await Task.WhenAll(_process.Started, _process.Control, Exit, _outputRead, _errorRead).WaitAsync(Limit); return _process.ExitCode; }
        internal async Task StopAsync()
        {
            Exception? failure = await _process.StopAsync();
            await Task.WhenAll(Exit, _outputRead, _errorRead).WaitAsync(Limit);
            if (failure is not null) { throw failure; }
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally { _process.Dispose(); }
        }
    }
}
