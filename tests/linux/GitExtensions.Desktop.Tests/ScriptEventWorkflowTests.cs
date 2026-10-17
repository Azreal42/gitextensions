using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class ScriptEventWorkflowTests
{
    [AvaloniaTest]
    public async Task BeforeCommitNativeFailurePreservesItsMutationAndStopsCommitAndAfter()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); string original = await f.Repository.ResolveRevisionAsync("HEAD");
        new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforeCommit, "before", "git update-ref refs/heads/script-effect HEAD\nexit 7"), (ScriptEvent.AfterCommit, "after", ""));
        await File.WriteAllTextAsync(f.File, "staged work\n"); await f.Repository.StageAllAsync();
        CommitDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Field<TextBox>(dialog, "Message").Text = "must not commit";
            Click(dialog, "Commit"); await Finish(() => !dialog.IsBusy, dialog);
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(original));
            Assert.That(await f.Repository.ResolveRevisionAsync("refs/heads/script-effect"), Is.EqualTo(original));
            Assert.That(dialog.HasCommitted, Is.False); Assert.That(dialog.ScriptMayHaveChangedRepository, Is.True);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "before" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CommitAndPushContinuesAfterFalseAfterCommitAndPreservesEventOrder()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); string remote = await f.CreateRemoteAsync();
        await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        new ProcessDialogPreferencesStore().Save(new(true));
        new CommitPreferencesStore().Save(new() { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false });
        Configure(f, (ScriptEvent.BeforeCommit, "commit-before", ""), (ScriptEvent.AfterCommit, "commit-after", "exit 4"),
            (ScriptEvent.BeforePush, "push-before", ""), (ScriptEvent.AfterPush, "push-after", ""));
        await File.WriteAllTextAsync(f.File, "commit and push\n"); await f.Repository.StageAllAsync();
        CommitDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Field<TextBox>(dialog, "Message").Text = "Event commit";
            Click(dialog, "CommitAndPush"); await Finish(() => !dialog.IsBusy, dialog);
            Assert.That(dialog.HasCommitted && dialog.PushCompleted, Is.True, dialog.LastError);
            GitResult published = await new GitRunner().RunAsync(remote, ["rev-parse", "refs/heads/main"]);
            Assert.That(published.StandardOutput.Trim(), Is.EqualTo(await f.Repository.ResolveRevisionAsync("HEAD")));
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "commit-before", "commit-after", "push-before", "push-after" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task PullOrdersBothPairsAndFalseAfterFetchDoesNotVetoAfterPull()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); await f.CreateRemoteAsync(); await TrackMain(f);
        new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforePull, "pull-before", ""), (ScriptEvent.BeforeFetch, "fetch-before", ""),
            (ScriptEvent.AfterFetch, "fetch-after", "exit 9"), (ScriptEvent.AfterPull, "pull-after", ""));
        PullDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Task execution = dialog.ExecuteAsync(dialog); await Finish(() => execution.IsCompleted, dialog); await execution;
            Assert.That(dialog.GitSucceeded && dialog.Succeeded, Is.True, dialog.LastError);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "pull-before", "fetch-before", "fetch-after", "pull-after" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BeforeFetchVetoAfterBeforePullNeverStartsAutostashOrGitPull()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); await f.CreateRemoteAsync(); await TrackMain(f);
        await File.WriteAllTextAsync(f.File, "keep dirty work\n"); new PullPreferencesStore().Save(new(AutoStash: true));
        new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforePull, "pull-before", ""), (ScriptEvent.BeforeFetch, "fetch-before", "exit 5"), (ScriptEvent.AfterPull, "pull-after", ""));
        PullDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Task execution = dialog.ExecuteAsync(dialog); await Finish(() => execution.IsCompleted, dialog); await execution;
            Assert.That(dialog.Attempted, Is.False); Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.Succeeded, Is.False);
            Assert.That(await f.Repository.GetStashesAsync(), Is.Empty); Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("keep dirty work\n"));
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "pull-before", "fetch-before" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FetchOnlyDoesNotEmitPullEvents()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); await f.CreateRemoteAsync();
        new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforePull, "pull-before", ""), (ScriptEvent.BeforeFetch, "fetch-before", ""),
            (ScriptEvent.AfterFetch, "fetch-after", ""), (ScriptEvent.AfterPull, "pull-after", ""));
        PullDialog dialog = new(f.Repository, GitPullAction.Fetch); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Task execution = dialog.ExecuteAsync(dialog); await Finish(() => execution.IsCompleted, dialog); await execution;
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(ReadLog(f), Is.EqualTo(new[] { "fetch-before", "fetch-after" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BranchCheckoutStashesBeforeBeforeScriptAndRetainsStashWhenVetoed()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); await f.GitAsync("branch", "topic");
        await File.WriteAllTextAsync(f.File, "checkout dirty work\n"); new ReferencePreferencesStore().Save(new(CheckoutChanges: LocalChangesAction.Stash));
        new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforeCheckout, "checkout-before", "git rev-parse --verify refs/stash > " + Quote(f.Output("observed-stash")) + "\nexit 8"),
            (ScriptEvent.AfterCheckout, "checkout-after", ""));
        GitReference topic = (await f.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/topic");
        CheckoutBranchDialog dialog = new(f.Repository, topic); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "CheckoutBranch"); await Finish(() => !dialog.IsBusy, dialog);
            GitStash saved = (await f.Repository.GetStashesAsync()).Single();
            Assert.That((await File.ReadAllTextAsync(f.Output("observed-stash"))).Trim(), Is.EqualTo(saved.Hash));
            Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main")); Assert.That(dialog.Succeeded, Is.False);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "checkout-before" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RevisionNativeFalseEmitsAfterWhileLaunchExceptionsDoNotBecomeFalse()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforeCheckout, "before", ""), (ScriptEvent.AfterCheckout, "after", ""));
        Window owner = new(); owner.Show();
        try
        {
            using ScriptWorkflowScope scope = new(owner, f.Repository, default);
            Assert.That(await scope.RunAsync(ScriptEvent.BeforeCheckout), Is.True);
            GitResult native = await f.Repository.CheckoutRevisionAttemptAsync("missing-native-revision", scope.Token);
            Assert.That(native.Success, Is.False);
            await scope.RunAsync(ScriptEvent.AfterCheckout);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "before", "after" }));
            GitRepository unavailable = new(f.Repository.Path, new GitRunner(f.Output("does-not-exist")));
            await ObserveExceptionAsync<System.ComponentModel.Win32Exception>(() => unavailable.CheckoutRevisionAttemptAsync("HEAD"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task TagCreatedLocallySurvivesBeforePushVetoWithoutPublishing()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); string remote = await f.CreateRemoteAsync();
        new ProcessDialogPreferencesStore().Save(new(true)); Configure(f, (ScriptEvent.BeforePush, "push-before", "exit 3"), (ScriptEvent.AfterPush, "push-after", ""));
        CreateTagDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Field<TextBox>(dialog, "CreateTagName").Text = "kept-local"; Field<CheckBox>(dialog, "PushCreatedTag").IsChecked = true;
            Click(dialog, "CreateTag"); await Finish(() => !dialog.IsBusy, dialog);
            Assert.That(dialog.CreatedTag, Is.EqualTo("kept-local"));
            Assert.That(await f.Repository.ResolveRevisionAsync("refs/tags/kept-local"), Is.EqualTo(await f.Repository.ResolveRevisionAsync("HEAD")));
            GitResult remoteTag = await new GitRunner().RunAsync(remote, ["show-ref", "--verify", "--quiet", "refs/tags/kept-local"]);
            Assert.That(remoteTag.Success, Is.False); Assert.That(ReadLog(f), Is.EqualTo(new[] { "push-before" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RecognizedMergeConflictRunsAfterWithoutPretendingGitSucceeded()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); await f.GitAsync("switch", "-c", "topic");
        await f.ChangeAsync("topic conflict\n", "topic"); await f.GitAsync("switch", "main"); await f.ChangeAsync("main conflict\n", "main");
        new ProcessDialogPreferencesStore().Save(new(true)); Configure(f, (ScriptEvent.BeforeMerge, "merge-before", ""), (ScriptEvent.AfterMerge, "merge-after", ""));
        MergeBranchDialog dialog = new(f.Repository, "topic", resolveConflictsAsync: (_, _, _, _) => Task.CompletedTask); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "ExecuteMerge"); await Finish(() => !dialog.IsBusy, dialog);
            Assert.That(dialog.Result, Is.Not.Null); Assert.That(dialog.Result!.Result.Success, Is.False);
            Assert.That((await f.Repository.GetStatusAsync()).Changes.Any(change => change.IsConflict), Is.True);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "merge-before", "merge-after" }));
        }
        finally { dialog.Close(); }

        // The Pull consumer has its own recognized-conflict boundary and must retain the native failure.
        GitResult abortedMerge = await f.Repository.ManageOperationAsync("merge", GitOperationAction.Abort);
        Assert.That(abortedMerge.Success, Is.True, abortedMerge.StandardError);
        await f.CreateRemoteAsync(); await TrackMain(f); await AdvanceRemoteAndDiverge(f, conflict: true);
        Configure(f, (ScriptEvent.BeforePull, "pull-before", ""), (ScriptEvent.BeforeFetch, "fetch-before", ""),
            (ScriptEvent.AfterFetch, "fetch-after", ""), (ScriptEvent.AfterPull, "pull-after", ""));
        PullDialog pull = new(f.Repository, GitPullAction.Merge, resolveConflictsAsync: (_, _, _, _) => Task.CompletedTask); pull.Show();
        try
        {
            await pull.InitializeAsync(); Task execution = pull.ExecuteAsync(pull);
            await Finish(() => execution.IsCompleted, pull); await execution;
            Assert.That(pull.GitSucceeded || pull.Succeeded || pull.Cancelled, Is.False);
            GitResult native = pull.LastProcess!.Result!;
            Assert.That(native.Success, Is.False);
            Assert.That(pull.LastException, Is.TypeOf<GitCommandException>());
            GitCommandException failure = (GitCommandException)pull.LastException!;
            Assert.That(failure.Result, Is.SameAs(native));
            Assert.That(pull.LastError, Is.EqualTo(failure.Message));
            Assert.That(Field<TextBox>(pull, "PullOutput").Text, Does.Contain("CONFLICT"));
            Assert.That((await f.Repository.GetStatusAsync()).Changes.Any(change => change.IsConflict), Is.True);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "merge-before", "merge-after", "pull-before", "fetch-before", "fetch-after", "pull-after" }));
            PushProcessTestDriver.Pointer(Field<Button>(pull, "AbortPullOperation"));
            await Finish(() => !pull.IsBusy, pull);
            Assert.That(pull.LastError, Is.Null);
            Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "merge-before", "merge-after", "pull-before", "fetch-before", "fetch-after", "pull-after" }));
        }
        finally { pull.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingOwnerDuringBeforeDrainsNativeProcessAndNeverStartsAfter()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); new ProcessDialogPreferencesStore().Save(new(true));
        string pidFile = f.Output("script-pid"), ready = f.Output("script-ready");
        Configure(f, (ScriptEvent.BeforeFetch, "before", "printf '%s' $$ > " + Quote(pidFile) + "\nprintf ready > " + Quote(ready) + "\nsleep 30"), (ScriptEvent.AfterFetch, "after", ""));
        Window owner = new(); owner.Show();
        Task<bool> run;
        using (ScriptWorkflowScope scope = new(owner, f.Repository, default))
        {
            run = scope.RunAsync(ScriptEvent.BeforeFetch);
            await Finish(() => File.Exists(ready), owner, acknowledge: false);
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            owner.Close(); await Finish(() => run.IsCompleted, owner, acknowledge: false);
            try { Assert.That(await run, Is.False); } catch (OperationCanceledException) { }
            Assert.That(Alive(pid), Is.False, "Owner closure must wait for the actual native child to stop.");
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "before" }));
        }
        owner.Close();
    }

    [AvaloniaTest]
    public async Task RemoteBatchPlanRevalidatesDestinationAfterAnEarlierRemoteSucceeded()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); string first = await f.CreateRemoteAsync();
        string second = f.Output("second.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, second); await f.GitAsync("remote", "add", "second", second);
        await f.GitAsync("branch", "topic"); await f.GitAsync("push", "origin", "topic"); await f.GitAsync("push", "second", "topic"); await f.Repository.FetchAllAsync();
        GitRemoteBranchDeletionPreview[] selected = (await f.Repository.GetRemoteBranchDeletionPreviewAsync()).Where(branch => branch.BranchName == "topic").ToArray();
        Assert.That(selected.Length, Is.EqualTo(2)); GitRemoteBranchDeletionPlan plan = await f.Repository.PrepareRemoteBranchDeletionAsync(new(selected));
        GitRemoteBranchDeletionBatchResult deleted = await f.Repository.DeleteRemoteBranchBatchAsync(plan, "origin"); Assert.That(deleted.Success, Is.True, deleted.ErrorMessage);
        await f.GitAsync("remote", "set-url", "--push", "second", f.Output("changed-destination.git"));
        await ObserveExceptionAsync<InvalidOperationException>(() => f.Repository.DeleteRemoteBranchBatchAsync(plan, "second"));
        Assert.That((await new GitRunner().RunAsync(first, ["show-ref", "--verify", "--quiet", "refs/heads/topic"])).Success, Is.False);
        Assert.That((await new GitRunner().RunAsync(second, ["show-ref", "--verify", "--quiet", "refs/heads/topic"])).Success, Is.True);
    }

    [AvaloniaTest]
    public async Task RejectedPushPullRecoveryAndRetryRunOnePushPairAroundTheChildPullPair()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); string remote = await f.CreateRemoteAsync(); await TrackMain(f);
        await AdvanceRemoteAndDiverge(f, conflict: false); await f.GitAsync("config", "core.editor", "true");
        new ProcessDialogPreferencesStore().Save(new(true));
        new PushPreferencesStore().Save(new(DontConfirmNewBranch: true, DontConfirmAddTracking: true, RejectedAction: GitPullAction.Merge));
        Configure(f, (ScriptEvent.BeforePush, "push-before", ""), (ScriptEvent.AfterPush, "push-after", ""),
            (ScriptEvent.BeforePull, "pull-before", ""), (ScriptEvent.BeforeFetch, "fetch-before", ""),
            (ScriptEvent.AfterFetch, "fetch-after", ""), (ScriptEvent.AfterPull, "pull-after", ""));
        PushDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Task execution = dialog.ExecuteAsync(dialog); await Finish(() => execution.IsCompleted, dialog); await execution;
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
            Assert.That(dialog.LastProcess!.Attempts.Count, Is.EqualTo(2));
            Assert.That(dialog.LastProcess.Attempts[0].Result?.Success, Is.False);
            Assert.That(dialog.LastProcess.Attempts[1].Result?.Success, Is.True);
            Assert.That((await new GitRunner().RunAsync(remote, ["rev-parse", "refs/heads/main"])).StandardOutput.Trim(), Is.EqualTo(await f.Repository.ResolveRevisionAsync("HEAD")));
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "push-before", "pull-before", "fetch-before", "fetch-after", "pull-after", "push-after" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingInitialRebaseRecoveryThenSkippingLaterEmitsThePendingAfterPairOnce()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); await f.CreateRemoteAsync(); await TrackMain(f);
        string remote = await AdvanceRemoteAndDiverge(f, conflict: true); await f.GitAsync("config", "core.editor", "true");
        new ProcessDialogPreferencesStore().Save(new(true));
        Configure(f, (ScriptEvent.BeforePull, "pull-before", ""), (ScriptEvent.BeforeFetch, "fetch-before", ""),
            (ScriptEvent.AfterFetch, "fetch-after", ""), (ScriptEvent.AfterPull, "pull-after", ""));
        PullDialog dialog = new(f.Repository, GitPullAction.Rebase); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Task initial = dialog.ExecuteAsync(dialog);
            await Finish(() => dialog.OwnedWindows.OfType<RebaseDialog>().Any(window => window.State?.HasConflicts == true && !window.IsBusy), dialog);
            RebaseDialog first = dialog.OwnedWindows.OfType<RebaseDialog>().Single(); first.Close();
            await Finish(() => initial.IsCompleted, dialog); await initial;
            Assert.That(dialog.Succeeded || dialog.Cancelled, Is.False); Assert.That(dialog.GitSucceeded, Is.False);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "pull-before", "fetch-before" }));
            Click(dialog, "ResolvePullConflict");
            await Finish(() => dialog.OwnedWindows.OfType<RebaseDialog>().Any(window => window.State?.HasConflicts == true && !window.IsBusy), dialog);
            RebaseDialog late = dialog.OwnedWindows.OfType<RebaseDialog>().Single(); Click(late, "SkipRebase");
            await Finish(() => !dialog.IsBusy, dialog);
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.GitSucceeded, Is.False, "Recovery must not rewrite the original native Pull failure.");
            Assert.That((await f.Repository.GetRebaseStateAsync()).IsActive, Is.False); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote));
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "pull-before", "fetch-before", "fetch-after", "pull-after" }));
            // Initialization and the guarded no-operation Continue path must not replay the consumed After pair.
            await dialog.InitializeAsync(); Assert.That(Field<Button>(dialog, "ContinuePullOperation").IsEnabled, Is.False);
            Click(dialog, "ContinuePullOperation"); await Finish(() => !dialog.IsBusy, dialog);
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "pull-before", "fetch-before", "fetch-after", "pull-after" }));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task PrevalidatedThreeRemoteStepsRetainFirstDeletionWhenSecondNativeBeforeScriptVetoes()
    {
        using ReferenceFixture f = await ReferenceFixture.CreateAsync(); string first = await f.CreateRemoteAsync();
        Dictionary<string, string> remotes = new() { ["origin"] = first };
        foreach (string name in new[] { "second", "third" })
        {
            string directory = f.Output(name + ".git"); await f.GitAsync("clone", "--bare", f.Repository.Path, directory);
            await f.GitAsync("remote", "add", name, directory); remotes[name] = directory;
        }
        await f.GitAsync("branch", "topic"); foreach (string name in remotes.Keys) { await f.GitAsync("push", name, "topic"); } await f.Repository.FetchAllAsync();
        GitRemoteBranchDeletionPreview[] selected = (await f.Repository.GetRemoteBranchDeletionPreviewAsync()).Where(branch => branch.BranchName == "topic").ToArray();
        GitRemoteBranchDeletionPlan plan = await f.Repository.PrepareRemoteBranchDeletionAsync(new(selected));
        string counter = Quote(f.Output("before-count"));
        string veto = """
            n=0
            if test -f @COUNTER@; then read n < @COUNTER@; fi
            n=$((n+1))
            printf '%s\n' "$n" > @COUNTER@
            if test "$n" -eq 2; then exit 6; fi
            """.Replace("@COUNTER@", counter, StringComparison.Ordinal);
        Configure(f, (ScriptEvent.BeforePush, "before", veto), (ScriptEvent.AfterPush, "after", ""));
        new ProcessDialogPreferencesStore().Save(new(true)); Window owner = new(); owner.Show();
        GitRemoteBranchDeletionBatchResult[] batches = remotes.Keys.Select(name => new GitRemoteBranchDeletionBatchResult(name,
            selected.Where(branch => branch.Remote == name).ToArray(), GitRemoteBranchDeletionBatchStatus.NotAttempted)).ToArray();
        try
        {
            Task execute = Execute(); await Finish(() => execute.IsCompleted, owner); await execute;
            Assert.That(batches.Select(batch => batch.Status), Is.EqualTo(new[] { GitRemoteBranchDeletionBatchStatus.Succeeded, GitRemoteBranchDeletionBatchStatus.NotAttempted, GitRemoteBranchDeletionBatchStatus.NotAttempted }));
            foreach ((string name, string directory) in remotes)
            { Assert.That((await new GitRunner().RunAsync(directory, ["show-ref", "--verify", "--quiet", "refs/heads/topic"])).Success, Is.EqualTo(name != "origin")); }
            Assert.That(ReadLog(f), Is.EqualTo(new[] { "before", "after", "before" }));
            Assert.That((await File.ReadAllTextAsync(f.Output("before-count"))).Trim(), Is.EqualTo("2"));
        }
        finally { owner.Close(); }

        async Task Execute()
        {
            // Bounded Core-phase + real UI-event integration: no mock runner and no invented result for an unattempted remote.
            using ScriptWorkflowScope scripts = new(owner, f.Repository, default);
            for (int index = 0; index < batches.Length; index++)
            {
                if (!await scripts.RunAsync(ScriptEvent.BeforePush)) { break; }
                batches[index] = await f.Repository.DeleteRemoteBranchBatchAsync(plan, batches[index].Remote, scripts.Token);
                Assert.That(batches[index].Success, Is.True, batches[index].ErrorMessage);
                await scripts.RunAsync(ScriptEvent.AfterPush);
            }
        }
    }

    private static async Task<string> AdvanceRemoteAndDiverge(ReferenceFixture f, bool conflict)
    {
        await f.GitAsync("switch", "-c", "remote-writer");
        await File.WriteAllTextAsync(conflict ? f.File : f.PathInRepository("remote-only.txt"), "remote content\n");
        await f.GitAsync("add", "."); await f.GitAsync("commit", "-m", "Remote advance"); await f.GitAsync("push", "origin", "remote-writer:main");
        string remote = await f.Repository.ResolveRevisionAsync("HEAD"); await f.GitAsync("switch", "main");
        await f.ChangeAsync("local content\n", "Local divergence"); return remote;
    }

    private static async Task TrackMain(ReferenceFixture f)
    { await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main"); }
    private static void Configure(ReferenceFixture f, params (ScriptEvent Event, string Marker, string Extra)[] definitions)
    {
        List<ScriptDefinition> scripts = [];
        foreach (var definition in definitions)
        {
            string file = f.Output("event-" + scripts.Count + ".sh");
            File.WriteAllText(file, "#!/bin/sh\nset -eu\nprintf '%s\\n' " + Quote(definition.Marker) + " >> " + Quote(f.Output("events.log")) + "\n" + definition.Extra + "\n");
            scripts.Add(new() { Name = definition.Marker, Command = "/bin/sh", Arguments = "\"" + file + "\"", OnEvent = definition.Event });
        }
        new ScriptsPreferencesStore().Save(scripts);
    }
    private static async Task ObserveExceptionAsync<T>(Func<Task> action) where T : Exception
    {
        Exception? actual = null;
        try { await action(); } catch (Exception error) { actual = error; }
        Assert.That(actual, Is.TypeOf<T>());
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string[] ReadLog(ReferenceFixture f) => File.Exists(f.Output("events.log")) ? File.ReadAllLines(f.Output("events.log")) : [];
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Finish(Func<bool> completed, Window owner, bool acknowledge = true)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(20);
        while (!completed() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (Window window in Descendants(owner).ToArray())
            {
                window.UpdateLayout();
                if (acknowledge && window is GitProcessDialog { IsRunning: false } process && process.IsVisible)
                { Click(process, "Ok"); }
            }
            await Task.Delay(10);
        }
        Assert.That(completed(), Is.True, "The event workflow did not complete and drain.");
    }
    private static IEnumerable<Window> Descendants(Window owner)
    { foreach (Window child in owner.OwnedWindows.ToArray()) { yield return child; foreach (Window nested in Descendants(child)) { yield return nested; } } }
    private static bool Alive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
