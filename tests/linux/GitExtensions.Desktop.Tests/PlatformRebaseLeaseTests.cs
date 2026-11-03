using System.Diagnostics;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformRebaseLeaseTests
{
    [Test]
    public async Task ARealRebaseExcludesAnotherProcessAndReleasesTheSameLeaseAfterCompletion()
    {
        using PlatformRepositoryTests.Fixture f = await PlatformRepositoryTests.Fixture.CreateAsync();
        string repository = f.Repository.Path;
        await File.WriteAllTextAsync(Path.Combine(repository, "base.txt"), "base\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("base");
        await f.Repository.CreateBranchAsync("topic");
        await File.WriteAllTextAsync(Path.Combine(repository, "topic.txt"), "topic\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("topic change");
        await f.Repository.CheckoutAsync("main");
        await File.WriteAllTextAsync(Path.Combine(repository, "main.txt"), "main\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("main change");
        await f.Repository.CheckoutAsync("topic");
        string gitDirectory = (await f.Repository.GetInfoAsync()).GitDirectory;
        string ready = Path.GetFullPath(Path.Combine(repository, "..", "lease-ready"));
        string release = Path.GetFullPath(Path.Combine(repository, "..", "lease-release"));
        string hook = Path.Combine(gitDirectory, "hooks", "pre-rebase");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        await File.WriteAllTextAsync(hook, "#!/bin/sh\n: > " + Quote(ready)
            + "\nwhile test ! -e " + Quote(release) + "; do sleep 0.01; done\n");
        if (!OperatingSystem.IsWindows())
        { File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Task<GitResult> running = f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" });
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!File.Exists(ready) && !running.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(10); }
            Assert.That(File.Exists(ready), Is.True, running.IsCompleted ? (await running).StandardError : "The real pre-rebase hook did not start.");
            Assert.That(running.IsCompleted, Is.False);
            string lease = Path.Combine(gitDirectory, "gitextensions-rebase.lock");
            Assert.That(File.Exists(lease), Is.True);
            string during = await ProbeAsync(f, lease, "during");
            Assert.That(during, Does.StartWith("refused:"), "The other process must not open the live production lease.");
            Assert.That(running.IsCompleted, Is.False);
            await File.WriteAllTextAsync(release, "release");
            GitResult result = await running.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(result.Success, Is.True, result.StandardError);
            Assert.That(await ProbeAsync(f, lease, "after"), Is.EqualTo("acquired"),
                "The same path must be openable after completion, ruling out an unrelated permissions/path failure.");
            Assert.That(await File.ReadAllTextAsync(Path.Combine(repository, "topic.txt")), Is.EqualTo("topic\n"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(repository, "main.txt")), Is.EqualTo("main\n"));
            Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("topic"));
            Assert.That((await f.Repository.GetLogAsync(1, revision: "HEAD", all: false)).Single().Subject, Is.EqualTo("topic change"));
        }
        finally { await File.WriteAllTextAsync(release, "release"); await running.WaitAsync(TimeSpan.FromSeconds(20)); }
    }

    private static async Task<string> ProbeAsync(PlatformRepositoryTests.Fixture f, string lease, string phase)
    {
        string root = TestDotnetHost.CreateStartInfo().WorkingDirectory;
        string executable = Path.Combine(root, "tests/linux/GitExtensions.Core.Tests/bin/Release/net10.0",
            "GitExtensions.Core.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string report = Path.GetFullPath(Path.Combine(f.Repository.Path, "..", "lease-" + phase + ".txt"));
        ProcessStartInfo start = new(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = f.Repository.Path };
        f.Repository.ExecutionEnvironment.ApplyTo(start);
        foreach (string argument in new[] { "--lease-stream-probe", lease, report }) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(process.ExitCode, Is.Zero, await output + await error);
            return await File.ReadAllTextAsync(report);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(); await Task.WhenAll(output, error);
        }
    }

    private static string Quote(string value)
    {
        if (OperatingSystem.IsWindows()) { value = value.Replace('\\', '/'); }
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
