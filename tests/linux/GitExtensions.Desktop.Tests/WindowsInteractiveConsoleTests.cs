using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("WindowsConsoleIntegration"), NonParallelizable, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class WindowsInteractiveConsoleTests
{
    [TestCase("complete")]
    [TestCase("cancel")]
    [TestCase("close")]
    public async Task ActualConsoleHunkInputCancellationAndWindowCloseRespectGitAndTheOwnedJob(string action)
    {
        using Fixture fixture = await Fixture.CreateAsync(); using CancellationTokenSource cancel = new();
        await File.WriteAllTextAsync(fixture.File, "selected Windows hunk λ\n");
        string original = (await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        GitCommandLog.Clear();
        Task<GitResult> operation = fixture.Repository.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["file.txt"],
            (args, token) => WindowsInteractiveFileConsoleLauncher.RunCoreAsync(fixture.Path, fixture.Environment.Executable,
                args, token, Executable("src/linux/GitExtensions.Desktop", "GitExtensions.exe"), fixture.Environment, null), cancel.Token);
        try
        {
            int host = await UntilHostAsync(operation);
            await ObserveAsync(fixture, host, "Stage this hunk", action == "complete" ? "y\r" : "", action == "close" ? "close" : "input");
            if (action == "complete")
            {
                await ObserveAsync(fixture, host, "Press Enter to close.", "", "input");
                Assert.That(operation.IsCompleted, Is.False, "The helper must retain its operation until the real acknowledgement.");
                await ObserveAsync(fixture, host, "Press Enter to close.", "\r", "input");
                Assert.That((await operation.WaitAsync(TimeSpan.FromSeconds(30))).ExitCode, Is.Zero);
                Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("selected Windows hunk λ\n"));
                Assert.That((await fixture.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("base\n"));
            }
            else
            {
                if (action == "cancel") { cancel.Cancel(); }
                Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.That((await fixture.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(original));
            }
            GitCommandLogEntry entry = GitCommandLog.Snapshot().Single(x => x.Command == "interactive console host");
            Assert.That(entry.ProcessId, Is.EqualTo(host)); Assert.That(entry.IsRunning, Is.False); Assert.That(entry.IsUnobserved, Is.False);
            Assert.That(entry.Cancelled, Is.EqualTo(action != "complete"));
            Assert.That(JsonSerializer.Serialize(entry), Does.Not.Contain("gitextensions-console-"));
        }
        finally
        {
            cancel.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
            GitCommandLog.Clear();
        }
    }

    [AvaloniaTest]
    public async Task OwnerCloseDefersTheWindowAndCompetingMutationUntilTheActualHostJobDrains()
    {
        using Fixture fixture = await Fixture.CreateAsync(); using CancellationTokenSource cancel = new();
        await File.WriteAllTextAsync(fixture.File, "pending Windows hunk λ\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, "other.txt"), "other\n");
        Window owner = new(); owner.Show(); GitCommandLog.Clear();
        Task<GitResult> operation = fixture.Repository.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["file.txt"],
            (args, token) => InteractiveFileConsoleLauncher.RunWithTerminalAsync(owner, fixture.Path, fixture.Environment.Executable,
                args, token, null, Executable("src/linux/GitExtensions.Desktop", "GitExtensions.exe"), fixture.Environment), cancel.Token);
        Task? other = null;
        try
        {
            int host = await UntilHostAsync(operation);
            await ObserveAsync(fixture, host, "Stage this hunk", "", "input");
            other = fixture.Repository.StageAsync(["other.txt"]);
            Assert.That(await Task.WhenAny(other, Task.Delay(120)), Is.Not.SameAs(other));
            owner.Close(); Assert.That(owner.IsVisible, Is.True);
            Assert.CatchAsync<OperationCanceledException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(30)));
            await other.WaitAsync(TimeSpan.FromSeconds(30));
            using CancellationTokenSource closing = new(TimeSpan.FromSeconds(10));
            while (owner.IsVisible) { await Task.Delay(10, closing.Token); }
            GitCommandLogEntry entry = GitCommandLog.Snapshot().Single(x => x.Command == "interactive console host");
            Assert.That(entry.ProcessId, Is.EqualTo(host)); Assert.That(entry.IsRunning, Is.False);
            Assert.That(entry.Cancelled, Is.True); Assert.That(entry.IsUnobserved, Is.False);
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("base\n"));
            Assert.That((await fixture.GitAsync("show", ":other.txt")).StandardOutput, Is.EqualTo("other\n"));
        }
        finally
        {
            cancel.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
            if (other is not null) { await other.WaitAsync(TimeSpan.FromSeconds(30)); }
            if (owner.IsVisible) { owner.Close(); } GitCommandLog.Clear();
        }
    }

    private static async Task<int> UntilHostAsync(Task operation)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (true)
        {
            GitCommandLogEntry? host = GitCommandLog.Snapshot().SingleOrDefault(x => x.Command == "interactive console host");
            if (host?.ProcessId is int pid) { return pid; }
            if (operation.IsCompleted) { await operation; Assert.Fail("The console finished without an authenticated host."); }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task ObserveAsync(Fixture fixture, int host, string marker, string input, string action)
    {
        HashSet<string> before = Directory.EnumerateFiles(fixture.Root, "observer-*.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProcessStartInfo start = new(Executable("tests/linux/GitExtensions.Core.Tests", "GitExtensions.Core.Tests.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--native-process-probe", "windows-console-observer", fixture.Root,
            host.ToString(System.Globalization.CultureInfo.InvariantCulture), marker, input, action }) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), output, error).WaitAsync(TimeSpan.FromSeconds(30));
        string[] records = Directory.EnumerateFiles(fixture.Root, "observer-*.json").Where(x => !before.Contains(x)).ToArray();
        string diagnostics = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(fixture.Root, "observer-*.json.error").Select(System.IO.File.ReadAllText));
        Assert.That(records, Has.Length.EqualTo(1), $"Observer exit code: {process.ExitCode}\n{error.Result}{output.Result}{diagnostics}");
        using JsonDocument observed = JsonDocument.Parse(await File.ReadAllTextAsync(records[0]));
        Assert.That(observed.RootElement.GetProperty("observed").GetString(), Is.EqualTo(marker));
        if (action != "close") { Assert.That(process.ExitCode, Is.Zero, error.Result + output.Result); }
    }

    private static string Executable(string project, string filename)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(System.IO.Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop"))) { root = root.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = System.IO.Path.Combine(root?.FullName ?? "", project, "bin", configuration, "net10.0", filename);
        Assert.That(File.Exists(path), Is.True, "Build the actual console host and observer before running this contract."); return path;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Windows console ' λ " + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "repository");
        internal string File => System.IO.Path.Combine(Path, "file.txt");
        internal GitExecutionEnvironment Environment { get; private set; } = null!;
        internal GitRepository Repository { get; private set; } = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new(); Directory.CreateDirectory(fixture.Path);
            try
            {
                string config = System.IO.Path.Combine(fixture.Root, "empty-config"); await System.IO.File.WriteAllTextAsync(config, "");
                Dictionary<string, string?> variables = new(GitExecutionEnvironment.Capture(new()).EnvironmentVariables, StringComparer.OrdinalIgnoreCase)
                { ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_NOSYSTEM"] = "1", ["XDG_CONFIG_HOME"] = System.IO.Path.Combine(fixture.Root, "config") };
                fixture.Environment = GitExecutionEnvironment.Capture(new(), variables);
                fixture.Repository = new(fixture.Path, fixture.Environment.CreateRunner());
                await fixture.GitAsync("init", "--initial-branch=main");
                await fixture.GitAsync("config", "user.name", "Windows Console Tester");
                await fixture.GitAsync("config", "user.email", "console@example.invalid");
                await fixture.GitAsync("config", "commit.gpgsign", "false");
                string hooks = System.IO.Path.Combine(fixture.Root, "hooks"); Directory.CreateDirectory(hooks);
                await fixture.GitAsync("config", "core.hooksPath", hooks); await fixture.GitAsync("config", "core.autocrlf", "false");
                await System.IO.File.WriteAllTextAsync(fixture.File, "base\n");
                await fixture.GitAsync("add", "--", "file.txt"); await fixture.GitAsync("commit", "-m", "Base"); return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal async Task<GitResult> GitAsync(params string[] arguments)
        {
            GitResult result = await Environment.CreateRunner().RunAsync(Path, arguments);
            Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        public void Dispose()
        {
            if (!Directory.Exists(Root)) { return; }
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            { System.IO.File.SetAttributes(file, System.IO.File.GetAttributes(file) & ~FileAttributes.ReadOnly); }
            Directory.Delete(Root, true);
        }
    }
}
