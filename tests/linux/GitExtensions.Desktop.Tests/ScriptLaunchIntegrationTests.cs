using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptLaunchIntegrationTests
{
    [AvaloniaTest]
    public async Task ForegroundLaunchUsesTheCapturedFileValuesAndTheOwnedProcessWindow()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string raw = "quote'\" back\\ slash Ω\nname.txt";
        FileMenuSelection files = new(f.Repository, [new(new(raw, 'M'), null, new("", GitCompareRevisionKind.WorkingTree))], [raw]);
        Task<bool> execution = DesktopScriptExecutor.ExecuteAsync(f.Owner, f.Repository,
            f.Script("import sys,json;open(sys.argv[1],'w').write(json.dumps(sys.argv[2:]))", "{{SelectedRelativePaths}}"), files: files);
        await PushProcessTestDriver.CompleteAsync(execution, f.Owner);
        Assert.That(await execution, Is.True);
        Assert.That(JsonSerializer.Deserialize<string[]>(File.ReadAllText(f.Output)), Is.EqualTo(new[] { raw }));
        Assert.That(f.Owner.OwnedWindows, Is.Empty);
        Assert.That(await f.Git("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task ADeclinedScriptCannotLaunchTheFollowingEventScript()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ScriptDefinition first = f.Script("open(__import__('sys').argv[1],'w').write('unexpected')") with
            { OnEvent = ScriptEvent.BeforeCommit, AskConfirmation = true };
        ScriptDefinition second = first with { Name = "following", HotkeyCommandIdentifier = 9002, AskConfirmation = false };
        new ScriptsPreferencesStore().Save([first, second]);
        Task<bool> execution = DesktopScriptExecutor.RunEventAsync(f.Owner, f.Repository, ScriptEvent.BeforeCommit);
        await Until(() => f.Owner.OwnedWindows.Count != 0);
        Window prompt = f.Owner.OwnedWindows.Single();
        Button no = prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "No");
        PushProcessTestDriver.Pointer(no);
        await PushProcessTestDriver.CompleteAsync(execution, f.Owner);
        Assert.That(await execution, Is.False); Assert.That(File.Exists(f.Output), Is.False);
        Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task BackgroundCommandsStartBeforeTheyExitAndAreDrainedWhenTheirOwnerCloses()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ScriptDefinition script = f.Script("import os,sys,time;open(sys.argv[1],'w').write(str(os.getpid()));time.sleep(60)") with { RunInBackground = true };
        Assert.That(await DesktopScriptExecutor.ExecuteAsync(f.Owner, f.Repository, script), Is.True);
        await Until(() => File.Exists(f.Output) && File.ReadAllText(f.Output).Length > 0);
        int pid = int.Parse(File.ReadAllText(f.Output));
        using Process native = Process.GetProcessById(pid);
        Assert.That(native.HasExited, Is.False);
        f.Owner.Close();
        await Until(() => !f.Owner.IsVisible);
        Assert.That(native.HasExited, Is.True, "Closing completes only after the real child has been waited and its pipes drained.");
    }

    [AvaloniaTest]
    public async Task ACommandThatCannotExecDoesNotReportBackgroundStart()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ScriptDefinition script = new() { Name = "missing", Command = Path.Combine(f.Directory, "missing"), RunInBackground = true };
        Task<bool> execution = DesktopScriptExecutor.ExecuteAsync(f.Owner, f.Repository, script);
        await Until(() => execution.IsCompleted);
        Assert.That(execution.IsFaulted, Is.True);
        try { await execution; Assert.Fail("Missing executable must fail."); }
        catch (System.ComponentModel.Win32Exception) { }
        Assert.That(File.Exists(f.Output), Is.False); Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task BeforeAndAfterEventsCaptureTheCurrentRepositoryHeadSeparately()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ScriptDefinition script = f.Script("import sys;open(sys.argv[1],'a').write(sys.argv[2]+'\\n')", "{{cHash}}") with { OnEvent = ScriptEvent.BeforeCommit };
        new ScriptsPreferencesStore().Save([script]);
        string before = await f.Git("rev-parse", "HEAD");
        Task<bool> first = DesktopScriptExecutor.RunEventAsync(f.Owner, f.Repository, ScriptEvent.BeforeCommit);
        await PushProcessTestDriver.CompleteAsync(first, f.Owner); Assert.That(await first, Is.True);
        await f.Git("commit", "--allow-empty", "-m", "Next");
        string after = await f.Git("rev-parse", "HEAD");
        new ScriptsPreferencesStore().Save([script with { OnEvent = ScriptEvent.AfterCommit }]);
        Task<bool> second = DesktopScriptExecutor.RunEventAsync(f.Owner, f.Repository, ScriptEvent.AfterCommit);
        await PushProcessTestDriver.CompleteAsync(second, f.Owner); Assert.That(await second, Is.True);
        Assert.That(File.ReadAllLines(f.Output), Is.EqualTo(new[] { before, after }));
        Assert.That(before, Is.Not.EqualTo(after));
    }

    [AvaloniaTest]
    public async Task CapturedMessagesDatesAndReferenceKindsMatchTheWindowsProviders()
    {
        await using Fixture f = await Fixture.CreateAsync();
        GitResult created = await new GitRunner().RunAsync(f.Repository.Path, ["commit", "--allow-empty", "-m", "Subject", "-m", "Body"],
            environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = "1970-01-01T00:00:00 +0000", ["GIT_COMMITTER_DATE"] = "1970-01-01T00:00:00 +0000" });
        Assert.That(created.Success, Is.True, created.StandardError);
        await f.Git("update-ref", "refs/custom/keep", "HEAD");
        await f.Git("update-ref", "refs/notes/review", "HEAD");
        GitCommit commit = (await f.Repository.GetLogAsync()).First();
        CapturedScriptContext captured = await ScriptHostContext.CaptureAsync(f.Repository, new(true, commit, [commit]), default);
        Assert.That(captured.Context.SelectedRevision!.Body, Is.EqualTo("Subject\n\nBody\n"));
        Assert.That(captured.Context.SelectedRevision.AuthorDate, Is.EqualTo(DateTime.MaxValue));
        Assert.That(captured.Context.CurrentRevision!.CommitDate, Is.EqualTo(DateTime.MaxValue));
        Assert.That(captured.References.Selected.Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
        Assert.That(captured.Context.SelectedReferences.Select(reference => reference.Kind), Is.EqualTo(new[] { ScriptReferenceKind.LocalBranch }));
    }

    [AvaloniaTest]
    public async Task ArtificialSelectionKeepsItsWindowsIdentityAndNeverReadsAFakeCommitObject()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string hash = GitExtensions.Extensibility.Git.ObjectId.IndexId.ToString();
        GitCommit artificial = new(hash, [], "", "", DateTimeOffset.MaxValue, "Commit index", "");
        CapturedScriptContext captured = await ScriptHostContext.CaptureAsync(f.Repository, new(true, artificial, [artificial]), default);
        Assert.That(captured.Context.SelectedRevision!.Hash, Is.EqualTo(hash));
        Assert.That(captured.Context.SelectedRevision.Subject, Is.EqualTo("Commit index"));
        Assert.That(captured.Context.SelectedRevision.Author, Is.EqualTo("Script Fixture"));
        Assert.That(captured.Context.SelectedRevisions.Single().Hash, Is.EqualTo(hash));
        Assert.That(captured.Context.SelectedReferences, Is.Empty);
        Assert.That(captured.Context.CurrentRevision, Is.Not.Null);
    }

    private static async Task Until(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "scripts-launch-" + Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Directory, "out.json");
        internal GitRepository Repository { get; private set; } = null!;
        internal Window Owner { get; } = new() { Width = 900, Height = 650 };
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture.Directory, "config"));
                System.IO.Directory.CreateDirectory(Path.Combine(fixture.Directory, "repository"));
                fixture.Repository = new GitRepository(Path.Combine(fixture.Directory, "repository"));
                await fixture.Git("init", "-b", "main");
                await fixture.Git("config", "user.name", "Script Fixture");
                await fixture.Git("config", "user.email", "scripts@example.test");
                await fixture.Git("config", "commit.gpgsign", "false");
                await fixture.Git("config", "core.hooksPath", Path.Combine(fixture.Directory, "no-hooks"));
                await fixture.Git("commit", "--allow-empty", "-m", "Initial");
                fixture.Owner.Show(); return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task<string> Git(params string[] arguments)
        {
            GitResult result = await new GitRunner().RunAsync(Repository.Path, arguments);
            Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput.TrimEnd('\r', '\n');
        }
        private static string QuoteLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        internal ScriptDefinition Script(string program, string suffix = "") => new()
        {
            Name = "fixture", Command = "/usr/bin/python3", HotkeyCommandIdentifier = 9001,
            Arguments = "-c " + QuoteLiteral(program) + " " + QuoteLiteral(Output) + " " + suffix
        };
        public async ValueTask DisposeAsync()
        {
            Owner.Close(); await Until(() => !Owner.IsVisible);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldXdg);
            if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); }
        }
    }
}
