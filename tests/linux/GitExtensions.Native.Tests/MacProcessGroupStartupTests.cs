using System.Diagnostics;
using System.Globalization;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Native.Tests;

[TestFixture, Category("MacProcessStartup"), NonParallelizable]
public sealed class MacProcessGroupStartupTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Test]
    public async Task RunBinaryAsync_should_start_when_Darwin_rejects_duplicate_group_creation()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new(Limit);
        GitBinaryResult result = await fixture.RunAsync(cancellation.Token, hold: false).WaitAsync(Limit);
        Assert.That(File.Exists(fixture.Ready), Is.True, "The actual child's group call must pass through the native fixture.");
        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.StandardError, Is.Empty);
    }

    [Test]
    public async Task RunBinaryAsync_should_cancel_before_the_child_creates_its_group()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        NativeProcessOwnershipTests.NativeActor? child = null;
        Task<GitBinaryResult> operation = fixture.RunAsync(cancellation.Token, hold: true);
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!File.Exists(fixture.Ready) && timer.Elapsed < Limit)
            {
                if (operation.IsCompleted) { await operation; Assert.Fail("Git ran before its private group was created."); }
                await Task.Delay(10);
            }
            Assert.That(File.Exists(fixture.Ready), Is.True, "The actual child must be held before setpgid and exec.");
            int[] identity = (await File.ReadAllTextAsync(fixture.Ready)).Split(' ')
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            child = new(Process.GetProcessById(identity[0]));
            Assert.That(identity[1], Is.Not.EqualTo(identity[0]), "The private group must not exist yet.");
            Assert.That(child.IsRunning, Is.True);
            Assert.That(operation.IsCompleted, Is.False);
            cancellation.Cancel();
            await Assert.ThatAsync(async () => await operation.WaitAsync(Limit), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(child.IsRunning, Is.False, "Cancellation must drain the pinned child even before the group exists.");
        }
        finally
        {
            cancellation.Cancel();
            await File.WriteAllTextAsync(fixture.Release, "release");
            try { await operation.WaitAsync(Limit); } catch { }
            if (child is not null) { await child.WaitForExitAsync().WaitAsync(Limit); child.Dispose(); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "native group ' λ " + Guid.NewGuid().ToString("N"));
        internal string Ready => Path.Combine(_root, "ready");
        internal string Release => Path.Combine(_root, "release");
        internal Fixture() => Directory.CreateDirectory(_root);
        internal Task<GitBinaryResult> RunAsync(CancellationToken cancellation, bool hold)
        {
            Assert.That(OperatingSystem.IsMacOS(), Is.True, "This contract requires a real Mac host.");
            return new GitRunner("/usr/bin/true").RunBinaryAsync(_root, [], cancellation,
                environment: new Dictionary<string, string?>
                {
                    ["DYLD_INSERT_LIBRARIES"] = Path.Combine(AppContext.BaseDirectory, "native-process-group-fixture.dylib"),
                    ["GITEXTENSIONS_GROUP_FIXTURE_READY"] = Ready,
                    ["GITEXTENSIONS_GROUP_FIXTURE_TEMPORARY"] = Ready + ".tmp",
                    ["GITEXTENSIONS_GROUP_FIXTURE_RELEASE"] = hold ? Release : null
                });
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
