using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ExternalCommandLogTestProcess;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class ScriptExternalCommandLogTests
{
    [AvaloniaTest]
    [TestCase("foreground")]
    [TestCase("background")]
    [TestCase("navigate")]
    public async Task ActualScriptModesPublishTheirOneNeutralProcessAndCaptureTheUiCallerBeforePreparationAwaits(string mode)
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        profile.Desktop.Save(profile.Desktop.Load() with { StartWithRecentWorkingDir = false, CheckForUpdates = false });
        MainWindow owner = new(); owner.Show(); await owner.OpenRepositoryAsync(profile.Repository!.Path); await UntilAsync(() => !owner.IsBusy);
        GitRepository repository = (GitRepository)typeof(MainWindow).GetField("_repository", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        string revision = (await repository.GetLogAsync()).First().Hash;
        await using ExternalCommandLogTestProcess child = new(stdout: mode == "navigate" ? revision : "stdout-secret");
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy;
        Task<bool>? execution = null;
        try
        {
            GitCommandLog.Clear();
            GitCommandLog.ConfigureCapture(new(true, () => Dispatcher.UIThread.CheckAccess(), typeof(ScriptExternalCommandLogTests).Assembly));
            ScriptDefinition script = new()
            {
                Name = "script-display-name-secret", Command = (mode == "navigate" ? "navigateTo:" : "") + child.Executable,
                Arguments = "\"argument-secret\"", RunInBackground = mode == "background", AskConfirmation = true
            };
            execution = StartFromOriginalUiCaller(owner, repository, script);
            // A real owned confirmation leaves the original call stack before launch is allowed.
            await UntilAsync(() => owner.OwnedWindows.Any(window => window.GetVisualDescendants().OfType<Button>().Any(button => button.Content as string == "Yes")));
            Window confirmation = owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<Button>().Any(button => button.Content as string == "Yes"));
            Assert.That(File.Exists(child.Marker), Is.False);
            PushProcessTestDriver.Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Yes"));
            await child.ReadyAsync(); await UntilAsync(() => GitCommandLog.Snapshot().Any(x => x.Command == "script" && x.ProcessId == child.Pid));
            GitCommandLogEntry live = GitCommandLog.Snapshot().Single(x => x.Command == "script");
            Assert.That(live.IsRunning, Is.True); Assert.That(live.IsOnMainThread, Is.True);
            Assert.That(live.CallStack, Does.Contain(nameof(StartFromOriginalUiCaller)), "The original caller must survive the genuine preparation await.");
            Assert.That(live.CallStack!.Length, Is.LessThanOrEqualTo(GitCommandLogCapturePolicy.MaxCallStackLength));
            Assert.That(live.Command, Does.Not.Contain(script.Name)); Assert.That(live.ProcessId, Is.EqualTo(child.Pid));
            if (mode == "background") { Assert.That(await execution.WaitAsync(TimeSpan.FromSeconds(20)), Is.True); }
            else { Assert.That(execution.IsCompleted, Is.False); }
            child.Release();
            await PushProcessTestDriver.CompleteAsync(execution, owner); Assert.That(await execution, Is.True);
            await child.WaitAsync(); await UntilAsync(() => !GitCommandLog.Snapshot().Single(x => x.Id == live.Id).IsRunning);
            GitCommandLogEntry done = GitCommandLog.Snapshot().Single(x => x.Id == live.Id);
            Assert.That(done.ExitCode, Is.Zero); Assert.That(done.Cancelled, Is.False); Assert.That(done.IsUnobserved, Is.False);
            Assert.That(GitCommandLog.Snapshot().Count(x => x.Command == "script"), Is.EqualTo(1));
            if (mode == "navigate")
            {
                ListBox history = owner.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "RevisionHistory");
                Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(revision));
            }
            string material = System.Text.Json.JsonSerializer.Serialize(GitCommandLog.Snapshot());
            foreach (string secret in new[] { "argument-secret", "script-display-name-secret", "user-script-name-secret", "stdout-secret", "stderr-secret" })
            { Assert.That(material.Contains(secret, StringComparison.Ordinal), Is.False, "A script content channel entered journal metadata."); }
        }
        finally
        {
            child.Release();
            if (execution is not null) { await PushProcessTestDriver.CompleteAsync(execution, owner); }
            owner.Close(); await UntilAsync(() => !owner.IsVisible); GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(previous);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<bool> StartFromOriginalUiCaller(Window owner, GitRepository repository, ScriptDefinition script)
        => DesktopScriptExecutor.ExecuteAsync(owner, repository, script);

    [AvaloniaTest]
    public async Task BackgroundScriptOwnerCloseStillOwnsCancellationAndNativeDrainWhileDetachedExternalWaitsRemainIndependent()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ExternalCommandLogTestProcess child = new(); Window owner = new(); owner.Show(); GitCommandLog.Clear();
        ScriptDefinition script = new() { Name = "private-background-script", Command = child.Executable, RunInBackground = true };
        try
        {
            Assert.That(await DesktopScriptExecutor.ExecuteAsync(owner, profile.Repository!, script), Is.True);
            await child.ReadyAsync(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single(x => x.Command == "script");
            owner.Close(); await UntilAsync(() => !owner.IsVisible);
            Assert.That(child.StillPresent, Is.False, "Scripts retain their existing owner cancellation/drain semantics.");
            GitCommandLogEntry ended = GitCommandLog.Snapshot().Single(x => x.Id == live.Id);
            Assert.That(ended.IsRunning, Is.False); Assert.That(ended.Cancelled, Is.True); Assert.That(ended.ExitCode, Is.Null); Assert.That(ended.IsUnobserved, Is.False);
        }
        finally { child.Release(); owner.Close(); await UntilAsync(() => !owner.IsVisible); GitCommandLog.Clear(); }
    }
}
