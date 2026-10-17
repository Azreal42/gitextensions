using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptPluginOwnershipTests
{
    [AvaloniaTest]
    public async Task ClosingCommitOnceWhileItsScriptPluginIsBusyCancelsAndDrainsBeforeAnySettingsSave()
    {
        using ReadOnlyGit environment = new();
        using Fixture fixture = await Fixture.CreateAsync("base\n", "working change Ω\n");
        GitRepository repository = CurrentRepository(fixture.Window);
        using GitGate gate = new(fixture.Repository);
        ScriptDefinition script = new() { Name = "Owned statistics", Command = "{plugin.Statistics}", HotkeyCommandIdentifier = 9081 };
        new ScriptsPreferencesStore().Save([script]);
        HotkeyPreferencesStore hotkeys = new(); HotkeyBindings bindings = hotkeys.Load();
        bindings.Set("Scripts", script.HotkeyCommandIdentifier, new(Key.F12, KeyModifiers.Control | KeyModifiers.Alt)); hotkeys.Save(bindings);
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(fixture.Window);
        try
        {
            await commit.InitializeAsync(); await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
            Material before = await Material.Capture(repository); byte[]? preferences = PreferencesBytes();
            commit.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
            commit.KeyReleaseQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
            await PushProcessTestDriver.UntilAsync(commit, () => commit.OwnedWindows.OfType<StatisticsDialog>().Any() && File.Exists(gate.Marker));
            StatisticsDialog plugin = commit.OwnedWindows.OfType<StatisticsDialog>().Single();
            Assert.That(plugin.Owner, Is.SameAs(commit)); Assert.That(plugin.IsBusy, Is.True); Assert.That(commit.IsBusy, Is.True);
            CancellationToken operation = (CancellationToken)typeof(CommitDialog).GetProperty("OperationToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(commit)!;
            using Process actor = gate.OpenActor();
            commit.Close(); // The only close request in the exercised path. No Process OK/ACK or second owner Close.
            await PushProcessTestDriver.UntilAsync(fixture.Window, () => closed.IsCompleted && !commit.IsBusy && !plugin.IsVisible);
            await closed;
            Assert.That(operation.IsCancellationRequested, Is.True);
            Assert.That(actor.HasExited, Is.True, "The normal deferred RepositoryDialog close must wait for the actual Git wrapper and its pipes.");
            Assert.That(commit.IsVisible, Is.False); Assert.That(commit.OwnedWindows, Is.Empty);
            Assert.That(fixture.Window.IsVisible, Is.True); Assert.That(fixture.Window.OwnedWindows, Is.Empty);
            Assert.That(PreferencesBytes(), Is.EqualTo(preferences), "A stopped script cannot save plugin settings after the owned modal returns.");
            await before.AssertSame(repository);
        }
        finally
        {
            CloseStatisticsForCleanup(fixture.Window);
            if (commit.IsVisible) { commit.Close(); }
            await PushProcessTestDriver.UntilAsync(fixture.Window, () => !commit.IsVisible && !commit.IsBusy);
        }
    }

    [AvaloniaTest]
    public async Task ExplicitCancellationClosesAndDrainsTheBusyPluginButKeepsItsCommitOwnerOpen()
    {
        using ReadOnlyGit environment = new();
        using Fixture fixture = await Fixture.CreateAsync("base\n", "working change Ω\n");
        GitRepository repository = CurrentRepository(fixture.Window);
        using GitGate gate = new(fixture.Repository);
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(fixture.Window);
        using CancellationTokenSource cancellation = new(); Task<bool>? execution = null;
        try
        {
            await commit.InitializeAsync(); await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
            Material before = await Material.Capture(repository); byte[]? preferences = PreferencesBytes();
            execution = DesktopScriptExecutor.ExecuteAsync(commit, repository,
                new() { Name = "Owned statistics", Command = "{plugin.Statistics}" }, cancellation.Token);
            await PushProcessTestDriver.UntilAsync(commit, () => commit.OwnedWindows.OfType<StatisticsDialog>().Any() && File.Exists(gate.Marker));
            StatisticsDialog plugin = commit.OwnedWindows.OfType<StatisticsDialog>().Single();
            Assert.That(plugin.Owner, Is.SameAs(commit)); Assert.That(plugin.IsBusy, Is.True);
            using Process actor = gate.OpenActor(); cancellation.Cancel();
            await PushProcessTestDriver.UntilAsync(commit, () => execution.IsCompleted && !plugin.IsVisible && !plugin.IsBusy);
            try { await execution; Assert.Fail("Cancellation must not be reported as successful plugin completion."); }
            catch (OperationCanceledException) { }
            Assert.That(actor.HasExited, Is.True); Assert.That(commit.IsVisible, Is.True); Assert.That(closed.IsCompleted, Is.False);
            Assert.That(commit.IsEffectivelyEnabled, Is.True); Assert.That(commit.OwnedWindows, Is.Empty);
            Assert.That(PreferencesBytes(), Is.EqualTo(preferences));
            await before.AssertSame(repository);
        }
        finally
        {
            cancellation.Cancel();
            CloseStatisticsForCleanup(fixture.Window);
            if (execution is not null)
            {
                await PushProcessTestDriver.UntilAsync(commit, () => execution.IsCompleted);
                try { await execution; } catch (OperationCanceledException) { }
            }
            if (commit.IsVisible) { commit.Close(); }
            await PushProcessTestDriver.UntilAsync(fixture.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    [AvaloniaTest]
    public async Task AnEmptyNavigateToSuffixIsTheOriginalSuccessfulNoOpAfterPreparation()
    {
        using ReadOnlyGit environment = new();
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = CurrentRepository(fixture.Window); Material before = await Material.Capture(repository);
        bool success = await DesktopScriptExecutor.ExecuteAsync(fixture.Window, repository, new() { Name = "Empty navigation", Command = "navigateTo:" });
        Assert.That(success, Is.True); Assert.That(fixture.Window.OwnedWindows, Is.Empty); await before.AssertSame(repository);
    }

    // A fresh profile may legitimately have no preferences.json; null is distinct from an empty file.
    // Do not hide access/IO failures, and never seed a document merely to make the assertion readable.
    private static byte[]? PreferencesBytes()
    {
        try { return File.ReadAllBytes(new PreferencesStore().FilePath); }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static GitRepository CurrentRepository(MainWindow window)
        => (GitRepository)typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void CloseStatisticsForCleanup(Window owner)
    {
        foreach (Window child in owner.OwnedWindows.ToArray())
        {
            CloseStatisticsForCleanup(child);
            if (child is StatisticsDialog) { child.Close(); }
        }
    }

    private sealed record Material(string Head, byte[] Refs, byte[] Index, byte[] IndexEntries, byte[] Config, byte[] Reflog, byte[] File, byte[] Status)
    {
        internal static async Task<Material> Capture(GitRepository repository)
        {
            GitRunner git = new("/usr/bin/git");
            async Task<byte[]> Read(params string[] args)
            {
                GitBinaryResult result = await git.RunBinaryAsync(repository.Path, args);
                Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput;
            }
            string head = System.Text.Encoding.UTF8.GetString(await Read("rev-parse", "HEAD"));
            byte[] refs = await Read("show-ref", "--head"), entries = await Read("ls-files", "--stage", "-z");
            byte[] config = await Read("config", "--local", "--null", "--list"), status = await Read("status", "--porcelain=v2", "-z");
            return new(head, refs, System.IO.File.ReadAllBytes(Path.Combine(repository.Path, ".git", "index")), entries, config,
                System.IO.File.ReadAllBytes(Path.Combine(repository.Path, ".git", "logs", "HEAD")), System.IO.File.ReadAllBytes(Path.Combine(repository.Path, "file.txt")), status);
        }
        internal async Task AssertSame(GitRepository repository)
        {
            Material after = await Capture(repository);
            Assert.Multiple(() =>
            {
                Assert.That(after.Head, Is.EqualTo(Head)); Assert.That(after.Refs, Is.EqualTo(Refs));
                Assert.That(after.IndexEntries, Is.EqualTo(IndexEntries)); Assert.That(after.Index, Is.EqualTo(Index));
                Assert.That(after.Config, Is.EqualTo(Config)); Assert.That(after.Reflog, Is.EqualTo(Reflog));
                Assert.That(after.File, Is.EqualTo(File)); Assert.That(after.Status, Is.EqualTo(Status));
            });
        }
    }

    private sealed class ReadOnlyGit : IDisposable
    {
        private readonly string? _optionalLocks = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        internal ReadOnlyGit() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _optionalLocks);
    }

    private sealed class GitGate : IDisposable
    {
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH"), _gate = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCRIPT_PLUGIN_GATE");
        private readonly string _directory;
        internal string Marker { get; }
        internal GitGate(string repository)
        {
            _directory = Path.Combine(Path.GetDirectoryName(repository)!, "plugin-git"); Directory.CreateDirectory(_directory);
            Marker = Path.Combine(_directory, "shortlog-exited.json"); string wrapper = Path.Combine(_directory, "git");
            File.WriteAllText(wrapper, """
#!/usr/bin/python3
import json, os, signal, subprocess, sys
if "shortlog" not in sys.argv[1:]:
    os.execv("/usr/bin/git", ["git", *sys.argv[1:]])
result = subprocess.run(["/usr/bin/git", *sys.argv[1:]])
if result.returncode != 0:
    sys.exit(result.returncode)
fields = open("/proc/self/stat").read().rsplit(")", 1)[1].split()
marker = os.environ["GITEXTENSIONS_SCRIPT_PLUGIN_GATE"]
with open(marker + ".pending", "x") as output:
    json.dump({"pid": os.getpid(), "sid": os.getsid(0), "pgid": os.getpgid(0), "start_ticks": fields[19], "git_exit": result.returncode}, output)
    output.flush()
    os.fsync(output.fileno())
os.rename(marker + ".pending", marker)
while True:
    signal.pause()
""");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _path);
            Environment.SetEnvironmentVariable("GITEXTENSIONS_SCRIPT_PLUGIN_GATE", Marker);
        }
        internal Process OpenActor()
        {
            using JsonDocument witness = JsonDocument.Parse(File.ReadAllBytes(Marker)); JsonElement root = witness.RootElement;
            int pid = root.GetProperty("pid").GetInt32();
            Assert.That(root.GetProperty("git_exit").GetInt32(), Is.Zero);
            Assert.That(root.GetProperty("sid").GetInt32(), Is.EqualTo(pid)); Assert.That(root.GetProperty("pgid").GetInt32(), Is.EqualTo(pid));
            string stat = File.ReadAllText($"/proc/{pid}/stat"); string[] fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.That(fields[19], Is.EqualTo(root.GetProperty("start_ticks").GetString()));
            Process process = Process.GetProcessById(pid); Assert.That(process.HasExited, Is.False); return process;
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _path); Environment.SetEnvironmentVariable("GITEXTENSIONS_SCRIPT_PLUGIN_GATE", _gate);
            if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
        }
    }
}
