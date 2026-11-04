using System.Runtime.Versioning;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[Category("PosixConsoleIntegration"), NonParallelizable, Platform("Linux,MacOsX")]
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class PosixInteractiveConsoleIntegrationTests
{
    [Test]
    public async Task RealHelperKeepsItsOperationUntilTheTerminalIsAcknowledged()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        using CancellationTokenSource cancel = new(); using PosixTestTerminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["rev-parse", "--verify", "HEAD"], cancel.Token, terminal);
        try
        {
            await terminal.UntilAsync("Press Enter to close.", operation);
            Assert.That(operation.IsCompleted, Is.False);
            await terminal.SendAsync("\n");
            Assert.That((await operation.WaitAsync(TimeSpan.FromSeconds(20))).ExitCode, Is.Zero, terminal.Output);
            await terminal.StoppedAsync(); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            Assert.That((await fixture.GitAsync("status", "--porcelain")).StandardOutput, Is.Empty);
        }
        finally { await FinishAsync(operation, cancel, terminal); }
    }

    [Test]
    public async Task RealHunkInputStagesTheGitIndexAndThenReturnsTheActualResult()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "selected hunk λ\n");
        using CancellationTokenSource cancel = new(); using PosixTestTerminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["add", "--patch", "--", "file.txt"], cancel.Token, terminal);
        try
        {
            await terminal.UntilAsync("Stage this hunk", operation); await terminal.SendAsync("y\n");
            await terminal.UntilAsync("Press Enter to close.", operation);
            Assert.That(operation.IsCompleted, Is.False);
            await terminal.SendAsync("\n");
            Assert.That((await operation.WaitAsync(TimeSpan.FromSeconds(20))).ExitCode, Is.Zero, terminal.Output);
            await terminal.StoppedAsync(); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("selected hunk λ\n"));
            Assert.That((await fixture.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("base\n"));
        }
        finally { await FinishAsync(operation, cancel, terminal); }
    }

    [Test]
    public async Task CancellingARealHunkDrainsTheHelperBeforeTheIndexLeaseReturns()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "unselected hunk λ\n");
        string index = (await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        using CancellationTokenSource cancel = new(); using PosixTestTerminal terminal = new();
        Task<GitResult> operation = Run(fixture, ["add", "--patch", "--", "file.txt"], cancel.Token, terminal);
        try
        {
            await terminal.UntilAsync("Stage this hunk", operation); cancel.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(20)));
            await terminal.StoppedAsync(); Assert.That(Directory.Exists(terminal.RequestDirectory), Is.False);
            Assert.That((await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("unselected hunk λ\n"));
        }
        finally { await FinishAsync(operation, cancel, terminal); }
    }

    private static Task<GitResult> Run(ReferenceFixture fixture, string[] arguments, CancellationToken token, PosixTestTerminal terminal)
        => InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
            arguments, token, terminal.Start, HelperExecutable());

    private static async Task FinishAsync(Task<GitResult> operation, CancellationTokenSource cancel, PosixTestTerminal terminal)
    {
        cancel.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) { }
        finally { await terminal.StoppedAsync(); }
    }

    private static string HelperExecutable()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "GitExtensions"); if (File.Exists(direct)) { return direct; }
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop"))) { root = root.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(path), Is.True); return path;
    }

}
