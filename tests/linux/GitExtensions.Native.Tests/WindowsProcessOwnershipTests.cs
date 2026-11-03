using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Native.Tests;

// These kernel contracts are scheduled on Windows, independently of the shared
// portable suite and the complete Linux suites. There is no simulated job backend.
[TestFixture, Category("WindowsNative"), NonParallelizable]
public sealed class WindowsProcessOwnershipTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CancellationOwnsTheActualDescendantAfterTheParentExitsOrItsPipesClose(bool parentExits, bool independentPipes)
    {
        using Fixture f = new(); using CancellationTokenSource cancel = new();
        ConcurrentQueue<GitProcessOutput> observed = new();
        string owned = f.Directory("owned"), unrelated = f.Directory("unrelated");
        using Process other = StartProbe(unrelated, ["hold", unrelated]); _ = other.SafeHandle;
        Process? parent = null, child = null; Task<GitBinaryResult>? operation = null;
        try
        {
            await ReadyAsync(Path.Combine(unrelated, "child-ready.json"));
            operation = f.Runner.RunBinaryAsync(f.Root, ["--native-process-probe", "tree", owned, independentPipes ? "private" : "inherit"], cancel.Token,
                outputReceived: observed.Enqueue);
            await ReadyAsync(Path.Combine(owned, "tree-ready.json"), operation);
            using JsonDocument actors = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(owned, "tree-ready.json")));
            parent = Process.GetProcessById(actors.RootElement.GetProperty("parent").GetInt32()); _ = parent.SafeHandle;
            child = Process.GetProcessById(actors.RootElement.GetProperty("child").GetInt32()); _ = child.SafeHandle;
            if (!independentPipes)
            {
                bool Seen(GitProcessOutputStream stream, string marker) => Encoding.ASCII.GetString(observed.Where(x => x.Stream == stream)
                    .SelectMany(x => x.Bytes.ToArray()).ToArray()).Contains(marker, StringComparison.Ordinal);
                Stopwatch rendering = Stopwatch.StartNew();
                while ((!Seen(GitProcessOutputStream.StandardOutput, "probe-child-output")
                    || !Seen(GitProcessOutputStream.StandardError, "probe-child-error")) && rendering.Elapsed < Limit) { await Task.Delay(10); }
                Assert.That(Seen(GitProcessOutputStream.StandardOutput, "probe-child-output"), Is.True, "The child must hold the actual operation's stdout.");
                Assert.That(Seen(GitProcessOutputStream.StandardError, "probe-child-error"), Is.True, "The child must hold the actual operation's stderr.");
            }
            if (parentExits)
            {
                await File.WriteAllTextAsync(Path.Combine(owned, "allow-parent-exit"), "go");
                await parent.WaitForExitAsync().WaitAsync(Limit);
            }
            Assert.That(child.HasExited, Is.False);
            Assert.That(operation.IsCompleted, Is.False, "EOF and an exited parent do not release a live owned child.");
            cancel.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(Limit));
            Assert.That(parent.HasExited, Is.True); Assert.That(child.HasExited, Is.True);
            Assert.That(other.HasExited, Is.False, "A separate invocation of the same program is outside this private job.");
        }
        finally
        {
            cancel.Cancel();
            // Stable handles belong only to this fixture. Preserve the assertion
            // failure, then drain its actors even if a candidate backend is broken.
            Stop(child); Stop(parent);
            await File.WriteAllTextAsync(Path.Combine(owned, "release"), "release");
            await File.WriteAllTextAsync(Path.Combine(unrelated, "release"), "release");
            try { await other.WaitForExitAsync().WaitAsync(Limit); } finally { Stop(other); }
            if (operation is not null) { try { await operation.WaitAsync(Limit); } catch { } }
            child?.Dispose(); parent?.Dispose();
        }
    }

    [TestCase(0)]
    [TestCase(73)]
    public async Task NativeCreationPreservesLiteralArgumentsEnvironmentAndDuplexBinaryIo(int exitCode)
    {
        using Fixture f = new(); using CancellationTokenSource cancel = new();
        string report = Path.Combine(f.Root, "report.json");
        string[] literal = ["", "spaces ' λ & %PATH% ^ $(literal)", "embedded\"quote", "C:\\literal directory\\", "back\\\"quote\\\\"];
        byte[] input = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i % 253)).ToArray();
        byte[] prefix = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
        Task<GitBinaryResult> operation = f.Runner.RunBinaryAsync(f.Root,
            ["--native-process-probe", "duplex", report, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), .. literal],
            cancel.Token, standardInput: input, environment: new Dictionary<string, string?> { ["GITEXTENSIONS_NATIVE_PROBE_VALUE"] = "captured ' λ & %literal%" });
        try
        {
            GitBinaryResult result = await operation.WaitAsync(Limit);
            Assert.That(result.ExitCode, Is.EqualTo(exitCode)); Assert.That(result.Success, Is.EqualTo(exitCode == 0));
            Assert.That(result.StandardOutput, Is.EqualTo(prefix.Concat(input).ToArray()));
            Assert.That(result.StandardErrorBytes, Is.EqualTo(prefix));
            using JsonDocument observed = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.That(observed.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(literal));
            Assert.That(observed.RootElement.GetProperty("cwd").GetString(), Is.EqualTo(f.Root));
            Assert.That(observed.RootElement.GetProperty("value").GetString(), Is.EqualTo("captured ' λ & %literal%"));
        }
        finally { cancel.Cancel(); try { await operation.WaitAsync(Limit); } catch { } }
    }

    private static async Task ReadyAsync(string path, Task? operation = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!File.Exists(path) && watch.Elapsed < Limit)
        {
            if (operation?.IsCompleted == true) { await operation; Assert.Fail("The native operation exited before its complete readiness record."); }
            await Task.Delay(10);
        }
        Assert.That(File.Exists(path), Is.True, "Native fixture readiness: " + path);
    }

    private static void Stop(Process? process)
    {
        if (process is null) { return; }
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } process.WaitForExit(); }
        catch (InvalidOperationException) { }
    }

    private static Process StartProbe(string workingDirectory, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(Executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--native-process-probe"); foreach (string value in arguments) { start.ArgumentList.Add(value); }
        return Process.Start(start)!;
    }

    private static string Executable
    {
        get
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "GitExtensions.Linux.slnx"))) { root = root.Parent; }
            if (root is null) { throw new DirectoryNotFoundException("The native fixture source root is unavailable."); }
            return Path.Combine(root.FullName, "tests/linux/GitExtensions.Core.Tests/bin/Release/net10.0/GitExtensions.Core.Tests"
                + (OperatingSystem.IsWindows() ? ".exe" : ""));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "native ownership ' λ & " + Guid.NewGuid().ToString("N"));
        internal GitRunner Runner { get; } = new(Executable);
        internal Fixture() { System.IO.Directory.CreateDirectory(Root); }
        internal string Directory(string name) { string path = Path.Combine(Root, name); System.IO.Directory.CreateDirectory(path); return path; }
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
