using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class InteractiveFileConsoleTests
{
    [Test]
    public async Task NativeAddPatchNoThenYesAndCheckoutPatchNoThenYesUseTheIndexAndLiteralNames()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A Linux PTY is required."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string name = "odd'; touch INJECTED #.txt";
        string path = fixture.PathInRepository(name);
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");
        await fixture.GitAsync("add", "--", name); await fixture.GitAsync("commit", "-m", "Literal file");
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(path, "one\nINDEX SELECTED\nthree\n");
        await RunAnswer(fixture, GitInteractiveFileAction.AddPatch, name, "n");
        Assert.That((await fixture.GitAsync("show", ":" + name)).StandardOutput, Is.EqualTo("one\ntwo\nthree\n"));
        await RunAnswer(fixture, GitInteractiveFileAction.AddPatch, name, "y");
        Assert.That((await fixture.GitAsync("show", ":" + name)).StandardOutput, Is.EqualTo(await File.ReadAllTextAsync(path)));
        await File.WriteAllTextAsync(path, "one\nWORKTREE EDIT\nthree\n");
        await RunAnswer(fixture, GitInteractiveFileAction.ResetPatch, name, "n");
        Assert.That(await File.ReadAllTextAsync(path), Does.Contain("WORKTREE EDIT"));
        await RunAnswer(fixture, GitInteractiveFileAction.ResetPatch, name, "y");
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("one\nINDEX SELECTED\nthree\n"), "checkout --patch without tree-ish restores the index, not HEAD.");
        Assert.That((await fixture.GitAsync("show", ":" + name)).StandardOutput, Is.EqualTo(await File.ReadAllTextAsync(path)));
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That(File.Exists(fixture.PathInRepository("INJECTED")), Is.False);
    }

    [Test]
    public async Task RealPromptKeepsMutationGuardUntilHelperHasExitedAndCancellationDrainsIt()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A Linux PTY is required."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "real pending hunk\n");
        string other = fixture.PathInRepository("other.txt"); await File.WriteAllTextAsync(other, "other\n");
        byte[] index = await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index"));
        using CancellationTokenSource cancellation = new(); using TestTerminal terminal = new();
        Task<GitResult> operation = fixture.Repository.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["file.txt"],
            (args, token) => InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable, args, token,
                terminal.Start, HelperExecutable()), cancellation.Token);
        await terminal.WaitFor("Stage this hunk");
        Task otherMutation = fixture.Repository.StageAsync(["other.txt"]);
        Assert.That(await Task.WhenAny(otherMutation, Task.Delay(120)), Is.Not.SameAs(otherMutation), "The real pending Git prompt must still own the repository mutation lock.");
        cancellation.Cancel();
        Exception? failure = null;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception exception) { failure = exception; }
        Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
        await otherMutation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(terminal.Process!.HasExited, Is.True);
        Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("base\n"));
        Assert.That((await fixture.GitAsync("show", ":other.txt")).StandardOutput, Is.EqualTo("other\n"));
        Assert.That(index, Is.Not.Empty);
    }

    [Test]
    public async Task NativeNonzeroExitIsPreservedAndNoTerminalIsAReadableFailure()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A Linux PTY is required."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync(); using TestTerminal terminal = new();
        Task<GitResult> operation = InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
            ["add", "--definitely-invalid-option"], CancellationToken.None, terminal.Start, HelperExecutable());
        await terminal.WaitFor("Press Enter to close."); await terminal.Send("\n");
        GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(result.ExitCode, Is.Not.Zero); Assert.That(result.Success, Is.False); Assert.That(terminal.Output, Does.Contain("unknown option"));
        Assert.That(terminal.Process!.ExitCode, Is.EqualTo(result.ExitCode));
        string? old = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            Exception? missing = null;
            try { await InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
                ["add", "--patch", "--", "file.txt"], CancellationToken.None, helperExecutable: HelperExecutable()); }
            catch (Exception exception) { missing = exception; }
            Assert.That(missing, Is.TypeOf<FileNotFoundException>());
            Assert.That(missing!.Message, Does.Contain("terminal emulator"));
        }
        finally { Environment.SetEnvironmentVariable("PATH", old); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellingOnlyTheCompletedConsolePreservesTheActualGitExit(bool fail)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A Linux PTY is required."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        using CancellationTokenSource cancellation = new(); using TestTerminal terminal = new();
        IReadOnlyList<string> arguments = fail ? ["add", "--definitely-invalid-option"] : ["status", "--porcelain"];
        Task<GitResult> operation = InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable,
            arguments, cancellation.Token, terminal.Start, HelperExecutable());
        await terminal.WaitFor("Press Enter to close.");
        Assert.That(operation.IsCompleted, Is.False, "The native command has finished but the console is still awaiting acknowledgement.");
        cancellation.Cancel();
        GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(result.ExitCode, Is.EqualTo(fail ? 129 : 0), terminal.Output);
        Assert.That(result.Success, Is.EqualTo(!fail));
        Assert.That(terminal.Process!.HasExited, Is.True);
        Assert.That(terminal.Process.ExitCode, Is.EqualTo(result.ExitCode));
        Assert.That(terminal.Output, Does.Contain("Console closed after Git completed."));
        Assert.That((await fixture.GitAsync("status", "--porcelain")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingOwnerAfterGitCompletedPreservesTheStagedPatchAndSuccess()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A Linux PTY is required."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "accepted before closing console\n");
        using TestTerminal terminal = new(); Window owner = new(); owner.Show();
        Task<GitResult> operation = fixture.Repository.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["file.txt"],
            (args, token) => InteractiveFileConsoleLauncher.RunWithTerminalAsync(owner, fixture.Repository.Path, fixture.Repository.GitExecutable,
                args, token, terminal.Start, HelperExecutable()));
        await terminal.WaitFor("Stage this hunk"); await terminal.Send("y\n");
        await terminal.WaitFor("Press Enter to close.");
        Assert.That(operation.IsCompleted, Is.False);
        owner.Close(); Assert.That(owner.IsVisible, Is.True);
        GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(result.ExitCode, Is.Zero, terminal.Output);
        for (int i = 0; i < 100 && owner.IsVisible; i++) { await Task.Delay(10); }
        Assert.That(owner.IsVisible, Is.False);
        Assert.That(terminal.Process!.HasExited, Is.True);
        Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("accepted before closing console\n"));
    }

    [AvaloniaTest]
    public async Task ClosingOwnerWaitsForTheRealInteractiveGitAndDoesNotAcceptThePatch()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A Linux PTY is required."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.File, "owner cancelled hunk\n");
        // TestTerminal runs the same CLI apphost in a genuine PTY; the injected factory is transport-only.
        using TestTerminal terminal = new(); Window owner = new(); owner.Show();
        Task<GitResult> operation = fixture.Repository.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["file.txt"],
            (args, token) => InteractiveFileConsoleLauncher.RunWithTerminalAsync(owner, fixture.Repository.Path, fixture.Repository.GitExecutable,
                args, token, terminal.Start, HelperExecutable()));
        await terminal.WaitFor("Stage this hunk"); owner.Close();
        Assert.That(owner.IsVisible, Is.True, "Closing is deferred while the owned native process drains.");
        Exception? failure = null;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception exception) { failure = exception; }
        Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
        for (int i = 0; i < 100 && owner.IsVisible; i++) { await Task.Delay(10); }
        Assert.That(owner.IsVisible, Is.False); Assert.That(terminal.Process!.HasExited, Is.True);
        Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("base\n"));
    }

    [SupportedOSPlatform("linux")]
    private static async Task RunAnswer(ReferenceFixture fixture, GitInteractiveFileAction action, string path, string answer)
    {
        using TestTerminal terminal = new();
        Task<GitResult> operation = fixture.Repository.RunInteractiveFileActionAsync(action, [path], (args, token) =>
            InteractiveFileConsoleLauncher.RunCoreAsync(fixture.Repository.Path, fixture.Repository.GitExecutable, args, token, terminal.Start, HelperExecutable()));
        await terminal.WaitFor(action == GitInteractiveFileAction.AddPatch ? "Stage this hunk" : "Discard this hunk");
        await terminal.Send(answer + "\n"); await terminal.WaitFor("Press Enter to close."); await terminal.Send("\n");
        GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(result.Success, Is.True, result.StandardError + terminal.Output); Assert.That(terminal.Process!.ExitCode, Is.Zero);
    }
    private static string HelperExecutable()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "GitExtensions"); if (File.Exists(direct)) { return direct; }
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src/linux/GitExtensions.Desktop"))) { directory = directory.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string source = Path.Combine(directory?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(source), Is.True, "Build the Desktop apphost before testing its real interactive console."); return source;
    }
    private sealed class TestTerminal : IDisposable
    {
        private readonly StringBuilder _output = new(); private Task _read = Task.CompletedTask;
        internal Process? Process { get; private set; }
        internal string Output { get { lock (_output) { return _output.ToString(); } } }
        internal Process Start(string executable, IReadOnlyList<string> args)
        {
            string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            ProcessStartInfo info = new("/usr/bin/script") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { "--quiet", "--return", "--flush", "--command", string.Join(" ", new[] { executable }.Concat(args).Select(Quote)), "/dev/null" }) { info.ArgumentList.Add(arg); }
            info.Environment["TERM"] = "xterm-256color";
            info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            Process = System.Diagnostics.Process.Start(info)!; _read = Task.WhenAll(Read(Process.StandardOutput), Read(Process.StandardError));
            // The launcher owns its Process handle; the fixture retains a separate handle for exit assertions.
            return System.Diagnostics.Process.GetProcessById(Process.Id);
        }
        private async Task Read(StreamReader reader)
        {
            char[] buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) { lock (_output) { _output.Append(buffer, 0, count); } }
        }
        internal async Task WaitFor(string value)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!Output.Contains(value, StringComparison.Ordinal) && elapsed.Elapsed < TimeSpan.FromSeconds(20))
            { if (Process?.HasExited == true) { break; } await Task.Delay(10); }
            Assert.That(Output, Does.Contain(value));
        }
        internal async Task Send(string value) { await Process!.StandardInput.WriteAsync(value); await Process.StandardInput.FlushAsync(); }
        public void Dispose()
        {
            if (Process?.HasExited == true) { _read.GetAwaiter().GetResult(); }
            if (Process is not null && !Process.HasExited) { Process.StandardInput.Close(); }
            TestContext.Out.WriteLine(Output);
            Process?.Dispose();
        }
    }
}
