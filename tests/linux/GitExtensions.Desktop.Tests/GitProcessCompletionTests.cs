using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed partial class GitProcessDialogTests
{
    [AvaloniaTest]
    public async Task CompletedShowAsyncPreservesNormalOkAndAutoCloseAsAcknowledgmentsAfterFinally()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); string before = f.Materials();
        foreach (bool autoClose in new[] { false, true })
        {
            new ProcessDialogPreferencesStore().Save(new(autoClose));
            GitProcessDialog? observed = null; GitResult? native = null;
            Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git log", true,
                async (ct, output) =>
                {
                    observed = owner.OwnedWindows.OfType<GitProcessDialog>().Single();
                    GitResult result = await new GitRunner().RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], ct, outputReceived: output);
                    native = result; return result;
                });
            if (!autoClose)
            {
                GitProcessDialog process = await Ready(owner); await Until(() => !process.IsRunning);
                Assert.That(task.IsCompleted, Is.False); Assert.That(process.Owner, Is.SameAs(owner));
                PushProcessTestDriver.Pointer(Field<Button>(process, "Ok"));
            }
            GitProcessDialogResult outcome = await task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(native!.Success, Is.True); Assert.That(outcome.Success, Is.True);
            Assert.That(outcome.Result, Is.SameAs(native)); Assert.That(outcome.Error, Is.Null);
            Assert.That(outcome.CompletionError, Is.Null); Assert.That(outcome.WasCancelled, Is.False);
            Assert.That(outcome.WorkflowStopRequested, Is.False); Assert.That(outcome.OwnerCloseRequested, Is.False);
            Assert.That(observed, Is.Not.Null); Assert.That(observed!.WorkflowStopRequested, Is.False);
            Assert.That(observed.Outcome!.WorkflowStopRequested, Is.False); Assert.That(observed.IsVisible, Is.False);
            Assert.That(owner.IsVisible, Is.True); Assert.That(owner.OwnedWindows, Is.Empty);
            Assert.That(outcome.Attempts.Count, Is.EqualTo(1));
            Assert.That(outcome.Attempts[0].StandardOutputBytes.ToArray(), Is.EqualTo(native.StandardOutputBytes));
            Assert.That(f.Materials(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task CompletionHookRetriesTheSameWindowBeforeDoneAndKeepsIndependentNativeAttemptBytes()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private executable gate requires Linux."); return; }
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); string before = f.Materials();
        string wrapper = f.Gate("log"); GitProcessDialog? same = null; GitProcessAttemptResult? first = null; int callbacks = 0;
        const string description = "git rev-parse --verify missing-private-ref";
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, description, true,
            (ct, output) => new GitRunner().RunAsync(f.Repository.Path, ["rev-parse", "--verify", "missing-private-ref"], ct, outputReceived: output),
            handleOnExit: async (process, attempt, ct) =>
            {
                callbacks++; same ??= process; Assert.That(process, Is.SameAs(same));
                if (attempt.Number != 1) { return null; }
                first = attempt;
                Window question = new() { Name = "CompletionRetryQuestion", Width = 280, Height = 120 };
                Button yes = new() { Name = "CompletionRetryYes", Content = "Retry", IsDefault = true };
                yes.Click += (_, _) => question.Close(true); question.Content = yes;
                bool retry = await process.ShowCompletionDialogAsync<bool>(question);
                return retry ? new GitProcessCommand("git log retry", (token, output) =>
                    new GitRunner(wrapper).RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], token, outputReceived: output)) : null;
            });
        await Until(() => same?.OwnedWindows.Any(window => window.Name == "CompletionRetryQuestion") == true);
        GitProcessDialog process = same!; Window question = process.OwnedWindows.Single();
        Assert.That(process.Owner, Is.SameAs(owner)); Assert.That(question.Owner, Is.SameAs(process));
        Assert.That(process.IsRunning, Is.True); Assert.That(Field<Button>(process, "Ok").IsEnabled, Is.False);
        GitResult rejectedNative = first!.Result!;
        Assert.That(rejectedNative.Success, Is.False); Assert.That(rejectedNative.StandardError, Is.Not.Empty);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text,
            Is.EqualTo(description + "\n" + rejectedNative.StandardOutput + rejectedNative.StandardError));
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("missing-private-ref").And.Not.Contain("Done"));
        byte[] preserved = first.StandardErrorBytes.ToArray();
        Assert.That(preserved, Is.EqualTo(rejectedNative.StandardErrorBytes));
        Assert.That(MemoryMarshal.TryGetArray(first.StandardErrorBytes, out ArraySegment<byte> copy), Is.True);
        Assert.That(copy.Count, Is.GreaterThan(0)); copy.Array![copy.Offset] ^= 0xff;
        Assert.That(first.StandardErrorBytes.ToArray(), Is.EqualTo(preserved));
        PushProcessTestDriver.Pointer(Field<Button>(question, "CompletionRetryYes"));
        await Until(() => File.Exists(wrapper + ".pids") && process.StandardOutputBytes.Length > 0);
        Assert.That(owner.OwnedWindows.OfType<GitProcessDialog>().Single(), Is.SameAs(process));
        Assert.That(process.CommandDescription, Is.EqualTo("git log retry")); Assert.That(process.IsRunning, Is.True);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Not.Contain("missing-private-ref").And.Not.Contain("Done"));
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Not.Contain(rejectedNative.StandardError));
        Assert.That(first.StandardErrorBytes.ToArray(), Is.EqualTo(preserved));
        File.WriteAllText(wrapper + ".release", "release\n");
        await Until(() => !process.IsRunning); Assert.That(task.IsCompleted, Is.False); Assert.That(callbacks, Is.EqualTo(2));
        PushProcessTestDriver.Pointer(Field<Button>(process, "Ok")); GitProcessDialogResult result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(result.Success, Is.True); Assert.That(result.Attempts.Count, Is.EqualTo(2));
        Assert.That(result.Attempts[0], Is.SameAs(first)); Assert.That(result.Attempts[0].Result!.Success, Is.False);
        Assert.That(result.Result, Is.SameAs(result.Attempts[1].Result));
        Assert.That(result.Attempts[1].StandardOutputBytes.ToArray(), Is.EqualTo(result.Result!.StandardOutputBytes));
        Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task CompletionHookExceptionRetainsNativeSuccessAndPreventsConfiguredAutoClose()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); new ProcessDialogPreferencesStore().Save(new(true));
        InvalidOperationException expected = new("Private completion failure"); GitResult? native = null;
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git log", true,
            async (ct, output) =>
            {
                GitResult result = await new GitRunner().RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], ct, outputReceived: output);
                native = result; return result;
            },
            handleOnExit: (_, _, _) => Task.FromException<GitProcessCommand?>(expected));
        GitProcessDialog process = await Ready(owner); await Until(() => !process.IsRunning);
        Assert.That(task.IsCompleted, Is.False); Assert.That(process.Result, Is.SameAs(native)); Assert.That(native!.Success, Is.True);
        Assert.That(process.Error, Is.Null); Assert.That(process.Outcome!.CompletionError, Is.SameAs(expected));
        Assert.That(process.Outcome.Success, Is.False); Assert.That(Field<TextBlock>(process, "ProcessNotice").Text, Does.Contain(expected.Message));
        PushProcessTestDriver.Pointer(Field<Button>(process, "Ok")); GitProcessDialogResult result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(result.Result, Is.SameAs(native)); Assert.That(result.Attempts.Single().Error, Is.Null);
        Assert.That(result.Attempts.Single().StandardOutputBytes.ToArray(), Is.EqualTo(native.StandardOutputBytes));
    }

    [AvaloniaTest]
    public async Task AbortDuringCompletionHookStopsTheWorkflowWithoutChangingTheFinishedNativeOutcome()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); bool hookEntered = false; int retried = 0;
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git log", true,
            (ct, output) => new GitRunner().RunAsync(f.Repository.Path, ["log", "-1", "--format=%H"], ct, outputReceived: output),
            handleOnExit: async (_, _, ct) =>
            {
                hookEntered = true; await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new GitProcessCommand("forbidden retry", (token, output) =>
                { retried++; return new GitRunner().RunAsync(f.Repository.Path, ["log", "-1"], token, outputReceived: output); });
            });
        await Until(() => hookEntered); GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single();
        GitResult native = process.Result!; Assert.That(native.Success, Is.True); Assert.That(process.IsRunning, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(process, "Abort")); GitProcessDialogResult result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(result.WorkflowStopRequested, Is.True); Assert.That(result.WasCancelled, Is.False);
        Assert.That(result.Result, Is.SameAs(native)); Assert.That(result.Error, Is.Null); Assert.That(result.CompletionError, Is.Null);
        Assert.That(result.Attempts.Count, Is.EqualTo(1)); Assert.That(retried, Is.Zero); Assert.That(owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task OwnerClosingACompletionChildStopsBeforeRetryAndPreservesTheActualGitFailure()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); GitProcessDialog? process = null; int retried = 0;
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git rev-parse missing", true,
            async (ct, output) =>
            {
                string[] args = ["rev-parse", "--verify", "missing-private-ref"];
                GitResult result = await new GitRunner().RunAsync(f.Repository.Path, args, ct, outputReceived: output);
                if (!result.Success) { throw new GitCommandException(args, result); } return result;
            }, handleOnExit: async (window, _, ct) =>
            {
                process = window; Window child = new() { Name = "CompletionOwnerChild", Width = 280, Height = 120, Content = new Button { Content = "Cancel" } };
                await window.ShowCompletionDialogAsync<object?>(child);
                return new GitProcessCommand("forbidden retry", (token, output) =>
                { retried++; return new GitRunner().RunAsync(f.Repository.Path, ["log", "-1"], token, outputReceived: output); });
            });
        await Until(() => process?.OwnedWindows.Any(window => window.Name == "CompletionOwnerChild") == true);
        GitResult native = process!.Result!; owner.Close(); GitProcessDialogResult result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        await Until(() => !owner.IsVisible);
        Assert.That(result.OwnerCloseRequested && result.WorkflowStopRequested, Is.True); Assert.That(result.WasCancelled, Is.False);
        Assert.That(result.Result, Is.SameAs(native)); Assert.That(result.Error, Is.TypeOf<GitCommandException>());
        Assert.That(result.CompletionError, Is.Null); Assert.That(result.Attempts.Count, Is.EqualTo(1)); Assert.That(retried, Is.Zero);
    }
}
