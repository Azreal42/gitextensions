using System.Diagnostics;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Native.Tests;

[TestFixture, Category("MacProcessStartup"), NonParallelizable]
public sealed class MacProcessStartupTests
{
    [Test]
    public async Task CancellationBeforeSupervisorHelloStopsItsActualNativeLoaderWithoutDispatchingGit()
    {
        Assert.That(OperatingSystem.IsMacOS(), Is.True, "This kernel contract requires a real Mac host.");
        TimeSpan limit = TimeSpan.FromSeconds(20);
        string root = Path.Combine(Path.GetTempPath(), "native startup ' λ " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using CancellationTokenSource cancel = new();
        NativeProcessOwnershipTests.NativeActor? supervisor = null;
        Task<GitBinaryResult>? operation = null;
        int callbacks = 0;
        try
        {
            string marker = Path.Combine(root, "loader-ready");
            operation = new GitRunner(NativeProcessOwnershipTests.Executable).RunBinaryAsync(root,
                ["--native-process-probe", "hold", root], cancel.Token,
                environment: new Dictionary<string, string?>
                {
                    ["DYLD_INSERT_LIBRARIES"] = Path.Combine(AppContext.BaseDirectory, "native-startup-blocker.dylib"),
                    ["GITEXTENSIONS_NATIVE_STARTUP_MARKER"] = marker,
                    ["GITEXTENSIONS_NATIVE_STARTUP_TEMPORARY"] = marker + ".tmp",
                    ["GITEXTENSIONS_NATIVE_STARTUP_RELEASE"] = Path.Combine(root, "release")
                }, outputReceived: _ => callbacks++);
            Stopwatch watch = Stopwatch.StartNew();
            while (!File.Exists(marker) && watch.Elapsed < limit)
            {
                if (operation.IsCompleted) { await operation; Assert.Fail("The actual loader did not enter its native gate."); }
                await Task.Delay(10);
            }
            Assert.That(File.Exists(marker), Is.True);
            string[] witness = await File.ReadAllLinesAsync(marker);
            Assert.That(witness, Has.Length.EqualTo(3));
            Assert.That(int.Parse(witness[1], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(Environment.ProcessId));
            Assert.That(Path.GetFileName(witness[2]), Is.EqualTo("gitextensions-posix"));
            int processId = int.Parse(witness[0], System.Globalization.CultureInfo.InvariantCulture);
            supervisor = new(Process.GetProcessById(processId));
            Assert.That(supervisor.IsRunning, Is.True);
            Assert.That(File.Exists(Path.Combine(root, "release")), Is.False, "The actual constructor remains gated before main/HELLO.");
            Assert.That(operation.IsCompleted, Is.False);
            Assert.That(callbacks, Is.Zero);
            Assert.That(File.Exists(Path.Combine(root, "child-ready.json")), Is.False);
            cancel.Cancel();
            OperationCanceledException? failure = Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(limit));
            Assert.That(failure!.CancellationToken, Is.EqualTo(cancel.Token));
            Assert.That(supervisor.IsRunning, Is.False, "The stable kernel reference must observe the actual supervisor exit before return.");
            Assert.That(callbacks, Is.Zero);
            Assert.That(File.Exists(Path.Combine(root, "child-ready.json")), Is.False);
        }
        finally
        {
            cancel.Cancel();
            // A candidate backend which fails cancellation can still retire its
            // actual loader cooperatively; cleanup never signals a numeric Mac PID.
            await File.WriteAllTextAsync(Path.Combine(root, "release"), "release");
            if (operation is not null) { try { await operation.WaitAsync(limit); } catch { } }
            supervisor?.Dispose(); Directory.Delete(root, recursive: true);
        }
    }

}
