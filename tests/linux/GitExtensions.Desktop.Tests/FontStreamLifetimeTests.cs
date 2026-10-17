using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GitExtensions.FontStreamRegression;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FontStreamLifetimeTests
{
    [Test, Platform("Linux")]
    public async Task RegularFontExportsActualSourceBytesUnderGcInAnIsolatedProcess()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Linux only."); }
        string fixture = Directory.CreateTempSubdirectory("gitextensions-font-stream-").FullName;
        if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(fixture, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        TestContext.Progress.WriteLine("Private font-stream evidence: " + fixture);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        try
        {
            string testDirectory = TestContext.CurrentContext.TestDirectory;
            string probeDirectory = Path.Combine(testDirectory, "font-stream-probe");
            string probe = Path.Combine(probeDirectory, "FontStreamRegression.dll");
            Assert.That(File.Exists(probe), Is.True, "Build must copy the complete current probe subtree, which Rootfull pins recursively.");
            BinaryPin[] before = ProbeProtocol.Snapshot(probeDirectory);
            string backendHash = ProbeProtocol.HashFile(Path.Combine(testDirectory, "Avalonia.Skia.dll"));
            string nativeHash = ProbeProtocol.NativeHash(testDirectory);
            Assert.That(ProbeProtocol.HashFile(Path.Combine(probeDirectory, "Avalonia.Skia.dll")), Is.EqualTo(backendHash));
            Assert.That(ProbeProtocol.NativeHash(probeDirectory), Is.EqualTo(nativeHash));
            string runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            string runtime = Path.Combine(runtimeRoot, "dotnet");
            Assert.That(File.Exists(runtime), Is.True, "The actual test runtime host is required; no PATH fallback.");
            Dictionary<string, string> environment = PrivateEnvironment(fixture, runtimeRoot);
            ChildResult matching = await RunChild("/usr/bin/fc-match", ["--format", "%{family[0]}\n%{file}\n", ":style=Regular"],
                environment, fixture, "fontconfig", TimeSpan.FromSeconds(5), deadline.Token);
            Assert.That(matching.ExitCode, Is.Zero, "The actual fontconfig process must succeed.");
            string[] fields = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(matching.Stdout)).Split('\n');
            Assert.That(fields.Length, Is.EqualTo(3), "Expected exactly one native family/file result and its final newline.");
            Assert.That(fields[2], Is.Empty);
            Assert.That(string.IsNullOrWhiteSpace(fields[0]) || fields[0].Contains('\r'), Is.False);
            Assert.That(fields[0].Length, Is.LessThanOrEqualTo(1024), "Bound the qualification metadata, not the product's font catalogue.");
            Assert.That(Path.IsPathFullyQualified(fields[1]), Is.True);
            string origin = new FileInfo(fields[1]).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fields[1];
            byte[] bytes = ProbeProtocol.ReadFont(origin);
            string fontHash = ProbeProtocol.Hash(bytes), font = Path.Combine(fixture, "source.font");
            using (FileStream copy = ProbeProtocol.CreatePrivate(font)) { await copy.WriteAsync(bytes, deadline.Token); }
            Assert.That(ProbeProtocol.ReadFont(origin), Is.EqualTo(bytes), "The actual installed source must remain stable during the fixture copy.");
            Assert.That(ProbeProtocol.ReadFont(font), Is.EqualTo(bytes));
            ProbeProtocol.WriteJson(Path.Combine(fixture, "font-source.json"), new
            {
                family = fields[0], fontconfigPath = fields[1], resolvedOrigin = origin,
                bytes = bytes.Length, sha256 = fontHash, privateCopy = font,
                fontconfigOwnWait = Path.Combine(fixture, "fontconfig-wait.json")
            });
            ProbeInput input = new(1, fields[0], font, bytes.LongLength, fontHash, backendHash, nativeHash, before);
            string inputPath = Path.Combine(fixture, "input.json"), receiptPath = Path.Combine(fixture, "receipt.json");
            ProbeProtocol.WriteJson(inputPath, input);
            ChildResult child = await RunChild(runtime, [probe, inputPath, receiptPath], environment, fixture,
                "probe", TimeSpan.FromSeconds(30), deadline.Token);
            Assert.That(child.ExitCode, Is.Zero,
                "The native probe must exit normally. Raw exit code is not a signal classification. Evidence: " + fixture);
            Assert.That(File.Exists(receiptPath), Is.True, "No receipt means no success, even if the child disappeared.");
            ProbeReceipt receipt = JsonSerializer.Deserialize<ProbeReceipt>(File.ReadAllBytes(receiptPath))!;
            Assert.That(receipt, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Status, Is.EqualTo("SUCCESS"), receipt.Error);
                Assert.That(receipt.Pid, Is.EqualTo(child.Pid));
                Assert.That(receipt.InputSha256, Is.EqualTo(ProbeProtocol.HashFile(inputPath)));
                Assert.That(receipt.Exports, Is.EqualTo(ProbeProtocol.Exports));
                Assert.That(receipt.VerifiedBytes, Is.EqualTo(bytes.LongLength * ProbeProtocol.Exports));
                Assert.That(receipt.GcPasses, Is.GreaterThan(0));
                Assert.That(receipt.WorkerAllocatedBytes, Is.GreaterThan(0));
                Assert.That(receipt.GcOverlap && receipt.WorkerJoined && receipt.BinaryInputsUnchanged, Is.True);
                Assert.That(receipt.WorkerEndedEarly, Is.False);
                Assert.That(receipt.WorkerError, Is.Null);
                Assert.That(receipt.CollectionsAfter.Where((count, generation) => count > receipt.CollectionsBefore[generation]).Any(), Is.True);
                Assert.That(receipt.NativeSha256, Is.EqualTo(nativeHash));
                Assert.That(receipt.RuntimeSha256, Is.EqualTo(ProbeProtocol.HashFile(runtime)));
                Assert.That(receipt.Modules.Single(module => module.Name == "Avalonia.Skia").Sha256, Is.EqualTo(backendHash));
            });
            Assert.That(ProbeProtocol.Snapshot(probeDirectory), Is.EqualTo(before), "Every probe path/type/mode/byte pin must remain unchanged.");
            Assert.That(ProbeProtocol.ReadFont(origin), Is.EqualTo(bytes));
            Assert.That(ProbeProtocol.ReadFont(font), Is.EqualTo(bytes));
            ProbeProtocol.WriteJson(Path.Combine(fixture, "parent-success.json"), new
            {
                child.Pid, child.ExitCode, actualOwnWait = true, streamsDrained = true,
                inputSha256 = ProbeProtocol.HashFile(inputPath), receiptSha256 = ProbeProtocol.HashFile(receiptPath),
                binaryPins = before, sourceSha256 = fontHash
            });
        }
        finally
        {
            // Retain the private fixture on success and failure for Root's independent review.
            foreach (string path in Directory.EnumerateFiles(fixture, "*.json").Concat(Directory.EnumerateFiles(fixture, "*.bin")))
            { TestContext.AddTestAttachment(path, "Private font-stream receipt/raw output"); }
        }
    }

    private static Dictionary<string, string> PrivateEnvironment(string fixture, string runtimeRoot)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin", ["LC_ALL"] = "C.UTF-8", ["DOTNET_ROOT"] = runtimeRoot,
            ["DOTNET_TieredCompilation"] = "0", ["DOTNET_ReadyToRun"] = "0"
        };
        foreach ((string variable, string name) in new[] { ("HOME", "home"), ("XDG_CONFIG_HOME", "config"),
            ("XDG_CACHE_HOME", "cache"), ("XDG_DATA_HOME", "data"), ("TMPDIR", "tmp"), ("DOTNET_CLI_HOME", "cli") })
        { string path = Path.Combine(fixture, name); Directory.CreateDirectory(path); environment[variable] = path; }
        string? marker = Environment.GetEnvironmentVariable("GITEXTENSIONS_VALIDATION_RUN");
        if (Path.IsPathFullyQualified(marker ?? "") && Directory.Exists(marker)) { environment["GITEXTENSIONS_VALIDATION_RUN"] = marker!; }
        return environment;
    }

    private sealed record ChildResult(int Pid, int ExitCode, string Stdout, string Stderr);

    private static async Task<ChildResult> RunChild(string executable, string[] arguments,
        Dictionary<string, string> environment, string fixture, string name, TimeSpan timeout, CancellationToken cancellation)
    {
        ProcessStartInfo start = new(executable)
        { WorkingDirectory = fixture, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach ((string key, string value) in environment) { start.Environment[key] = value; }
        foreach (string value in arguments) { start.ArgumentList.Add(value); }
        using Process process = Process.Start(start) ?? throw new IOException("Could not start the owned child.");
        int pid = process.Id; long? birth = null;
        try { birth = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        string stdout = Path.Combine(fixture, name + ".stdout.bin"), stderr = Path.Combine(fixture, name + ".stderr.bin");
        using CancellationTokenSource readers = new();
        Task output = Drain(process.StandardOutput.BaseStream, stdout, readers.Token);
        Task errors = Drain(process.StandardError.BaseStream, stderr, readers.Token);
        bool waited = false, stoppedForTimeout = false; Exception? failure = null;
        long began = Stopwatch.GetTimestamp();
        try { await process.WaitForExitAsync().WaitAsync(timeout, cancellation); waited = true; }
        catch (Exception error)
        {
            failure = error; stoppedForTimeout = error is TimeoutException or OperationCanceledException;
            try
            {
                if (!process.HasExited) { process.Kill(); }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); waited = true;
            }
            catch (Exception cleanup) { failure = new AggregateException(error, cleanup); }
        }
        bool drained = false, readersRetired = false; string? retirementFailure = null;
        try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(3)); drained = true; readersRetired = true; }
        catch (Exception error)
        {
            failure ??= error; readers.Cancel();
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception retirement) { retirementFailure = retirement.GetType().Name + ": " + retirement.Message; }
            readersRetired = output.IsCompleted && errors.IsCompleted;
            // Observe even a late fault if cancellation could not retire a reader within the bound.
            foreach (Task reader in new[] { output, errors })
            { _ = reader.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
        }
        int? exit = waited ? process.ExitCode : null;
        ProbeProtocol.WriteJson(Path.Combine(fixture, name + "-wait.json"), new
        {
            pid, birthUtcTicksObserved = birth, executable, executableSha256 = ProbeProtocol.HashFile(executable), arguments,
            beganTicks = began, endedTicks = Stopwatch.GetTimestamp(), actualOwnWaitCompleted = waited,
            stopwatchFrequency = Stopwatch.Frequency,
            actualExitCode = exit, stoppedForTimeout, streamsDrained = drained,
            readersRetired, retirementFailure, stdoutTaskState = output.Status.ToString(), stderrTaskState = errors.Status.ToString(),
            stdout, stderr, failure = failure?.GetType().Name + (failure is null ? "" : ": " + failure.Message),
            environment // This is a newly constructed private allowlist, never the inherited host environment.
        });
        if (failure is not null) { throw new IOException("Owned child failed/timed out; evidence: " + fixture, failure); }
        ProbeProtocol.Require(waited && drained && exit.HasValue, "Actual wait and drain are required.");
        return new(pid, exit!.Value, stdout, stderr);
    }

    private static async Task Drain(Stream source, string destination, CancellationToken cancellation)
    {
        using FileStream target = ProbeProtocol.CreatePrivate(destination); byte[] buffer = new byte[8192]; int total = 0;
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellation);
            if (count == 0) { return; }
            if (total + count > ProbeProtocol.MaxLogBytes) { throw new IOException("Raw child output exceeded its finite 512 KiB budget."); }
            await target.WriteAsync(buffer.AsMemory(0, count), cancellation); total += count;
        }
    }
}
