using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Avatars;
using NUnit.Framework;
using DesktopProgram = GitExtensions.Linux.Desktop.Program;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AvatarActivationTests
{
    [AvaloniaTest]
    public async Task HeadlessAndInformationalOrRealEditorHelperPathsNeverCreateAnAvatarRuntime()
    {
        using AvatarSettingsTestProfile profile = new(); Application? current = Application.Current;
        App testApp = current as App ?? throw new AssertionException("The actual test builder must use the public dormant App constructor.");
        Assert.That(testApp.ExistingAvatarRuntime, Is.Null); Assert.That(App.PeekAvatarRuntime(), Is.Null);
        CultureInfo? oldDefault = CultureInfo.DefaultThreadCurrentUICulture; string? oldHelper = Environment.GetEnvironmentVariable("GITEXTENSIONS_REBASE_DIRECTORY");
        string? oldAskpass = GitRunner.AskpassUiCulture; TextWriter oldOutput = Console.Out; using StringWriter output = new();
        App dormant = new(), armedHeadless = new(true); Window? first = null, second = null;
        try
        {
            first = dormant.CreateDesktopStartupWindow(DesktopStartupRequest.Browse);
            second = armedHeadless.CreateDesktopStartupWindow(DesktopStartupRequest.Browse);
            Assert.That(dormant.ExistingAvatarRuntime, Is.Null); Assert.That(armedHeadless.ExistingAvatarRuntime, Is.Null,
                "Even an armed constructor cannot activate without a real classic desktop lifetime.");
            Assert.That(AvatarWindowLifetime.For(first).HasBinding, Is.False); Assert.That(AvatarWindowLifetime.For(second).HasBinding, Is.False);
            App.BindExistingAvatarRuntime(first); Assert.That(AvatarWindowLifetime.For(first).HasBinding, Is.False);
            byte[] before = File.ReadAllBytes(profile.Desktop.FilePath); Console.SetOut(output);
            Assert.That(DesktopProgram.Main(["--version"]), Is.Zero); Assert.That(DesktopProgram.Main(["--help"]), Is.Zero);
            string state = profile.Output("editor-state"); Directory.CreateDirectory(state);
            const string plan = "pick 1234567 Literal helper plan Ω\n"; File.WriteAllText(Path.Combine(state, "todo"), plan);
            Environment.SetEnvironmentVariable("GITEXTENSIONS_REBASE_DIRECTORY", state); string target = profile.Output("git-rebase-todo");
            Assert.That(DesktopProgram.Main(["--write-rebase-plan", target]), Is.Zero); Assert.That(File.ReadAllText(target), Is.EqualTo(plan));
            Assert.That(output.ToString(), Does.Contain("Git Extensions").And.Contain("Usage:"));
            Assert.That(Application.Current, Is.SameAs(current)); Assert.That(App.PeekAvatarRuntime(), Is.Null);
            Assert.That(testApp.ExistingAvatarRuntime, Is.Null); Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(before));
            Assert.That(Directory.Exists(Path.Combine(profile.Output("cache"), "gitextensions", "shared-avatars-v1")), Is.False);
        }
        finally
        {
            Console.SetOut(oldOutput);
            try
            {
                if (second is not null) { await AvatarUiTestDriver.Close(second); }
                if (first is not null) { await AvatarUiTestDriver.Close(first); }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITEXTENSIONS_REBASE_DIRECTORY", oldHelper);
                CultureInfo.DefaultThreadCurrentUICulture = oldDefault; GitRunner.AskpassUiCulture = oldAskpass;
            }
        }
    }

    [AvaloniaTest]
    public async Task ShutdownCancelsARealInitialsRenderAlreadyQueuedUnderBoundedUiPressureAndDrainsItsTask()
    {
        Assert.That(Dispatcher.UIThread.CheckAccess(), Is.True);
        using OfflineAvatarFixture f = new(); TaskCompletionSource<(Task<AvatarPng?> Task, bool OnUi)> queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Initials = (request, token) =>
        {
            // This is the real production renderer and its real queued dispatcher operation.
            Task<AvatarPng?> render = AvatarImageCodec.RenderInitialsAsync(request, token);
            queued.TrySetResult((render, Dispatcher.UIThread.CheckAccess())); return render;
        };
        await using AvatarService service = f.Create(); AvatarApplicationLifetime application = new(service);
        AvatarOwnerScope owner = service.CreateOwnerScope("queued-real-render"); Task<AvatarReadResult> read = owner.ReadAsync(OfflineAvatars.Request());
        Task<AvatarPng?>? actual = null;
        try
        {
            // Do not pump the UI before the real off-UI invocation has returned its queued task.
            // The timeout is a failure bound, not a sleep or a number of render frames.
            var observed = queued.Task.WaitAsync(OfflineAvatars.Limit).GetAwaiter().GetResult(); actual = observed.Task;
            Assert.That(observed.OnUi, Is.False); Assert.That(actual.IsCompleted, Is.False);
            Task shutdown = application.ShutdownAsync(); Assert.That(service.Snapshot.IsStopping, Is.True);
            // Simulate the Program finally after its UI loop: cancellation and the real
            // raw-render drain must complete without running another dispatcher callback.
            shutdown.WaitAsync(OfflineAvatars.Limit).GetAwaiter().GetResult();
            Assert.That(actual.IsCanceled, Is.True); Assert.CatchAsync<OperationCanceledException>(async () => await actual.WaitAsync(OfflineAvatars.Limit));
            Assert.That(read.IsCompleted, Is.True, "Shutdown must drain the already admitted consumer as well as its renderer.");
            try
            {
                // Worker retirement may win the race with owner cancellation. Both are
                // valid, but a Ready image after shutdown is never accepted.
                AvatarReadResult result = await read;
                Assert.That(result.Outcome, Is.EqualTo(AvatarReadOutcome.GenerationRetired)); Assert.That(result.Image, Is.Null);
            }
            catch (OperationCanceledException) { }
            Assert.That(owner.IsClosed, Is.True); Assert.That(f.Memory.Count, Is.Zero); Assert.That(f.Disk.Commits, Is.Zero);
            Assert.That(f.InitialsCalls, Is.EqualTo(1)); Assert.That(f.UserFallbackCalls, Is.Zero);
        }
        finally
        {
            await application.ShutdownAsync().WaitAsync(OfflineAvatars.Limit);
            if (actual is null && queued.Task.IsCompletedSuccessfully) { actual = queued.Task.Result.Task; }
            if (actual is not null) { try { await actual.WaitAsync(OfflineAvatars.Limit); } catch (OperationCanceledException) { } }
            try { await read.WaitAsync(OfflineAvatars.Limit); } catch (OperationCanceledException) { }
        }
    }
}
