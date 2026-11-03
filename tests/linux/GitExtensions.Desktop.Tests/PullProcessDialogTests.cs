using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class PullProcessDialogTests
{
    [AvaloniaTest]
    public async Task EveryFetchScopeStreamsNativeBytesToItsVisibleOwnerBeforeCompletionAndWaitsForAcknowledgment()
    {
        foreach ((GitPullAction action, string? branch) in new[]
        {
            (GitPullAction.Fetch, (string?)null), (GitPullAction.Fetch, "main"),
            (GitPullAction.FetchAll, (string?)null), (GitPullAction.FetchPruneAll, (string?)null)
        })
        {
            await using Fixture f = await Fixture.Create(); string remote = await f.Advance(); string gate = f.Gate("fetch");
            PullDialog pull = await f.Pull(new(f.Repository.Path, new GitRunner(gate)), action, branch, visible: false);
            Task execution = pull.ExecuteAsync(f.Owner);
            GitProcessDialog process = await f.Started(gate, f.Owner);
            await Until(() => process.StandardErrorBytes.Length > 0);
            Assert.That(pull.IsVisible, Is.False); Assert.That(process.Owner, Is.SameAs(f.Owner));
            Assert.That(process.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            Assert.That(process.IsRunning, Is.True); Assert.That(execution.IsCompleted, Is.False);
            Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("From "));
            Assert.That(await f.Repository.ResolveRevisionAsync("refs/remotes/origin/main"), Is.EqualTo(remote));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
            f.Release(gate); await Until(() => !process.IsRunning);
            Assert.That(process.Result!.Success, Is.True); Assert.That(execution.IsCompleted, Is.False);
            Assert.That(process.StandardOutputBytes.ToArray(), Is.EqualTo(process.Result.StandardOutputBytes));
            Assert.That(process.StandardErrorBytes.ToArray(), Is.EqualTo(process.Result.StandardErrorBytes));
            Click(process, "Ok"); await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(pull.Succeeded, Is.True, pull.LastError); Assert.That(pull.LastProcess!.Result, Is.SameAs(process.Result));
            Assert.That(pull.LastProcess.OwnerCloseRequested, Is.False, "An explicit OK acknowledges the command without closing its owner.");
            Assert.That(f.Owner.OwnedWindows, Is.Empty);
        }
    }

    [AvaloniaTest]
    public async Task PullKeepsItsStashAndNativePullOutputVisibleAndDoesNotAskToPopUntilTheProcessIsAcknowledged()
    {
        await using Fixture f = await Fixture.Create(); string remote = await f.Advance();
        await File.WriteAllTextAsync(f.Root.File, "saved café work\n"); new PullPreferencesStore().Save(new(AutoStash: true));
        string gate = f.Gate("pull"); PullDialog pull = await f.Pull(new(f.Repository.Path, new GitRunner(gate)));
        Task execution = pull.ExecuteAsync(pull); GitProcessDialog process = await f.Started(gate, pull);
        await Until(() => Field<TextBox>(process, "ProcessOutput").Text?.Contains("Saved working directory", StringComparison.Ordinal) == true);
        GitStash saved = (await f.Repository.GetStashesAsync()).Single();
        Assert.That(pull.PendingAutoStash!.Hash, Is.EqualTo(saved.Hash));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote));
        Assert.That(pull.OwnedWindows, Is.EquivalentTo(new[] { process }));
        f.Release(gate); await Until(() => !process.IsRunning);
        Assert.That(process.Result!.Success, Is.True); Assert.That(execution.IsCompleted, Is.False);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("Saved working directory").And.Contain("Updating ").And.EndWith("Done"));
        Assert.That(process.Result.StandardOutput, Does.Not.Contain("Saved working directory"), "The last native Pull result must not become a composite stash/Pull result.");
        Click(process, "Ok"); await Until(() => pull.OwnedWindows.Any(window => window.Name == "ConfirmPopPullAutoStash"));
        Assert.That(pull.Succeeded, Is.False); Window pop = pull.OwnedWindows.Single(); Click(pop, "KeepPullAutoStash");
        await execution.WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(pull.Succeeded, Is.True, pull.LastError);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(saved.Hash));
        Assert.That(Field<TextBox>(pull, "PullOutput").Text, Does.Contain("Saved working directory").And.Contain("Updating "));
    }

    [AvaloniaTest]
    public async Task AbortAfterActualStashCreationRetainsItsExactCheckpointAndNeverStartsPullOrPop()
    {
        await using Fixture f = await Fixture.Create(); await f.Advance(); await File.WriteAllTextAsync(f.Root.File, "saved before interruption\n");
        new PullPreferencesStore().Save(new(AutoStash: true, AutoPopStashAfterPull: true));
        string gate = f.Gate("stash push"); PullDialog pull = await f.Pull(new(f.Repository.Path, new GitRunner(gate)));
        Task execution = pull.ExecuteAsync(pull); GitProcessDialog process = await f.Started(gate, pull);
        await Until(() => process.StandardOutputBytes.Length > 0); GitStash saved = (await f.Repository.GetStashesAsync()).Single();
        Assert.That(pull.PendingAutoStash, Is.Null, "The stash runner is still waiting; recovery identifies its checkpoint after interruption.");
        Click(process, "Abort"); await execution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(pull.Cancelled, Is.True); Assert.That(pull.Succeeded, Is.False); Assert.That(pull.LastError, Is.EqualTo("Operation cancelled"));
        Assert.That(pull.LastProcess!.WasCancelled, Is.True); Assert.That(pull.LastProcess.Result, Is.Null);
        Assert.That(pull.PendingAutoStash!.Hash, Is.EqualTo(saved.Hash)); Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(saved.Hash));
        Assert.That(File.Exists(gate + ".pull-started"), Is.False); Assert.That(pull.OwnedWindows, Is.Empty);
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("base\n"));
        Assert.That(Field<TextBox>(pull, "PullOutput").Text, Does.Contain("Saved working directory"));
        AssertStopped(gate);
    }

    [AvaloniaTest]
    public async Task AbortAfterRealPullEffectsKeepsThoseEffectsAndTheStashWithoutRunningTheNextPhase()
    {
        await using Fixture f = await Fixture.Create(); string remote = await f.Advance(); await File.WriteAllTextAsync(f.Root.File, "saved while pulling\n");
        new PullPreferencesStore().Save(new(AutoStash: true, AutoPopStashAfterPull: true));
        string gate = f.Gate("pull"); PullDialog pull = await f.Pull(new(f.Repository.Path, new GitRunner(gate)));
        Task execution = pull.ExecuteAsync(pull); GitProcessDialog process = await f.Started(gate, pull);
        GitStash saved = (await f.Repository.GetStashesAsync()).Single();
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote));
        Click(process, "Abort"); await execution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(pull.Cancelled, Is.True); Assert.That(pull.Succeeded, Is.False); Assert.That(pull.LastProcess!.Result, Is.Null);
        Assert.That(pull.PendingAutoStash!.Hash, Is.EqualTo(saved.Hash)); Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(saved.Hash));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote), "Interrupting the runner must preserve Git's completed changes.");
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("base\n"), "A remembered Yes must not reapply the stash after Abort.");
        Assert.That(pull.OwnedWindows, Is.Empty); Assert.That(Field<TextBox>(pull, "PullOutput").Text, Does.Contain("Updating "));
        AssertStopped(gate);
    }

    [AvaloniaTest]
    public async Task RetryingPullWithoutAutoStashDoesNotApplyTheCheckpointFromItsInterruptedAttempt()
    {
        await using Fixture f = await Fixture.Create(); string remote = await f.Advance(); await File.WriteAllTextAsync(f.Root.File, "saved interrupted attempt\n");
        new PullPreferencesStore().Save(new(AutoStash: true, AutoPopStashAfterPull: true));
        string gate = f.Gate("stash push"); PullDialog pull = await f.Pull(new(f.Repository.Path, new GitRunner(gate)));
        Task interrupted = pull.ExecuteAsync(pull); GitProcessDialog process = await f.Started(gate, pull);
        await Until(() => process.StandardOutputBytes.Length > 0); Click(process, "Abort"); await interrupted.WaitAsync(TimeSpan.FromSeconds(15));
        GitStash saved = (await f.Repository.GetStashesAsync()).Single(); Assert.That(pull.PendingAutoStash!.Hash, Is.EqualTo(saved.Hash));
        Click(pull, "PullAutoStash"); Assert.That(Field<CheckBox>(pull, "PullAutoStash").IsChecked, Is.False);
        new ProcessDialogPreferencesStore().Save(new(true));
        await pull.ExecuteAsync(pull).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(pull.Succeeded, Is.True, pull.LastError); Assert.That(pull.Cancelled, Is.False); Assert.That(pull.PendingAutoStash, Is.Null);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(saved.Hash), "The previous checkpoint remains available for its explicit Stash workflow.");
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote));
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("base\n"), "A remembered Yes must not apply another attempt's stash.");
        Assert.That(Field<TextBox>(pull, "PullOutput").Text, Does.Not.Contain("Saved working directory")); Assert.That(pull.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ABlockedReadOnlyStashCheckpointTimesOutAndPreservesTheOriginalObserverFailure()
    {
        await using Fixture f = await Fixture.Create(); await File.WriteAllTextAsync(f.Root.File, "stash with blocked recovery\n");
        string gate = f.Gate("stash push", blockCheckpoint: true); InvalidOperationException original = new("native stash observer failure");
        GitRepository repository = new(f.Repository.Path, new GitRunner(gate)); bool checkpoint = false;
        Task<GitPullExecutionResult> execution = repository.PullWithOptionsAsync(GitPullMode.Merge, "origin", null, new(AutoStash: true),
            outputReceived: _ => throw original, autoStashCreated: _ => checkpoint = true);
        await Until(() => File.Exists(gate + ".checkpoint-started"));
        Assert.That(execution.IsCompleted, Is.False, "The independent readonly checkpoint is deliberately blocked.");
        Exception? actual = null;
        try { await execution.WaitAsync(TimeSpan.FromSeconds(15)); } catch (InvalidOperationException error) { actual = error; }
        Assert.That(actual, Is.SameAs(original), "A checkpoint timeout cannot replace the native observer failure.");
        Assert.That(checkpoint, Is.False, "An unreadable checkpoint must not invent a stash reference.");
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1)); Assert.That(File.Exists(gate + ".pull-started"), Is.False);
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        AssertStopped(gate + ".checkpoint");
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerWhileKeepAwaitsAcknowledgmentPreservesTheCompletedNativeResultAndSkipsPop()
    {
        await using Fixture f = await Fixture.Create(); string remote = await f.Advance(); await File.WriteAllTextAsync(f.Root.File, "saved owner-close work\n");
        new PullPreferencesStore().Save(new(AutoStash: true, AutoPopStashAfterPull: true));
        PullDialog pull = await f.Pull(f.Repository, visible: false); Task execution = pull.ExecuteAsync(f.Owner);
        await Until(() => f.Owner.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
        GitProcessDialog process = f.Owner.OwnedWindows.OfType<GitProcessDialog>().Single(); GitResult native = process.Result!;
        Assert.That(native.Success, Is.True); Assert.That(execution.IsCompleted, Is.False);
        f.Owner.Close(); await execution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(pull.Cancelled, Is.True); Assert.That(pull.Succeeded, Is.False);
        Assert.That(pull.LastProcess!.Result, Is.SameAs(native), "Closing after completion must retain the actual exit result.");
        Assert.That(pull.LastProcess.WasCancelled, Is.False, "The command already finished; only its remaining workflow was cancelled.");
        Assert.That(pull.LastProcess.OwnerCloseRequested, Is.True);
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(pull.PendingAutoStash!.Hash));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote));
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("base\n"));
    }

    [AvaloniaTest]
    public async Task AStashCheckpointCallbackFailureCannotReplaceTheOriginalNativeObserverFailure()
    {
        await using Fixture f = await Fixture.Create(); await File.WriteAllTextAsync(f.Root.File, "stash with callback failure\n");
        string gate = f.Gate("stash push"); InvalidOperationException original = new("native stash observer failure");
        GitRepository repository = new(f.Repository.Path, new GitRunner(gate)); GitStash? checkpoint = null;
        Task<GitPullExecutionResult> execution = repository.PullWithOptionsAsync(GitPullMode.Merge, "origin", null, new(AutoStash: true),
            outputReceived: _ => throw original, autoStashCreated: stash => { checkpoint = stash; throw new IOException("checkpoint callback failure"); });
        Exception? actual = null;
        try { await execution.WaitAsync(TimeSpan.FromSeconds(15)); } catch (InvalidOperationException error) { actual = error; }
        Assert.That(actual, Is.SameAs(original)); Assert.That(checkpoint!.Hash, Is.EqualTo((await f.Repository.GetStashesAsync()).Single().Hash));
        Assert.That(File.Exists(gate + ".pull-started"), Is.False); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
    }

    [AvaloniaTest]
    public async Task FailedRebaseWaitsForProcessAcknowledgmentBeforeOpeningRecoveryAndKeepsItsAutoStash()
    {
        await using Fixture f = await Fixture.Create();
        await f.Commit(f.Repository, "file.txt", "local conflict\n");
        await f.Commit(f.Writer, "file.txt", "remote conflict\n"); await f.Writer.PushAsync("origin", "main");
        await f.Commit(f.Repository, "saved.txt", "tracked baseline\n"); string local = await f.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(f.Root.PathInRepository("saved.txt"), "saved work\n");
        new PullPreferencesStore().Save(new(AutoStash: true)); new ProcessDialogPreferencesStore().Save(new(true));
        PullDialog pull = await f.Pull(f.Repository, GitPullAction.Rebase); Task execution = pull.ExecuteAsync(pull);
        await Until(() => pull.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
        GitProcessDialog process = pull.OwnedWindows.OfType<GitProcessDialog>().Single(); GitResult native = process.Result!;
        Assert.That(native.Success, Is.False); Assert.That(execution.IsCompleted, Is.False);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("CONFLICT").And.Contain("Saved working directory"));
        Assert.That(pull.OwnedWindows, Is.EquivalentTo(new[] { process }));
        GitStash saved = (await f.Repository.GetStashesAsync()).Single(); Click(process, "Ok");
        await Until(() => pull.OwnedWindows.OfType<RebaseDialog>().Any(dialog => dialog.State?.HasConflicts == true && !dialog.IsBusy));
        RebaseDialog recovery = pull.OwnedWindows.OfType<RebaseDialog>().Single(); Assert.That(pull.LastProcess!.Result, Is.SameAs(native));
        Click(recovery, "AbortRebase"); await execution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(pull.Cancelled, Is.True); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(local));
        Assert.That((await f.Repository.GetStashesAsync()).Single().Hash, Is.EqualTo(saved.Hash));
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Window window, string name)
    {
        Pump(window); Control control = Field<Control>(window, name); Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible, Is.True, name);
        Point point = control.TranslatePoint(new Point(Math.Min(12, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!condition()) { await Task.Delay(20, timeout.Token); } }
    private static void AssertStopped(string gate)
    {
        foreach (string pid in File.ReadAllText(gate + ".pids").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { try { using Process process = Process.GetProcessById(int.Parse(pid, CultureInfo.InvariantCulture)); Assert.That(process.HasExited, Is.True, pid); } catch (ArgumentException) { } }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<PullDialog> _pulls = [];
        internal ReferenceFixture Root { get; }
        internal GitRepository Repository => Root.Repository;
        internal GitRepository Writer { get; }
        internal string Initial { get; }
        internal Window Owner { get; } = new() { Width = 850, Height = 650, RequestedThemeVariant = ThemeVariant.Dark };
        private Fixture(ReferenceFixture root, GitRepository writer, string initial) { Root = root; Writer = writer; Initial = initial; Owner.Show(); }
        internal static async Task<Fixture> Create()
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private gated native Git process scenario requires Linux."); }
            ReferenceFixture root = await ReferenceFixture.CreateAsync();
            try
            {
                string server = await root.CreateRemoteAsync(); await root.GitAsync("branch", "--set-upstream-to", "origin/main");
                GitRepository writer = await GitRepository.CloneAsync(server, root.Output("writer"));
                await writer.SetConfigAsync("user.name", "Pull Process Tester"); await writer.SetConfigAsync("user.email", "pull-process@example.invalid");
                await writer.SetConfigAsync("commit.gpgsign", "false"); await writer.SetConfigAsync("core.hooksPath", root.Output("no-hooks"));
                return new(root, writer, await root.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { root.Dispose(); throw; }
        }
        internal async Task Commit(GitRepository repository, string file, string content)
        { await File.WriteAllTextAsync(Path.Combine(repository.Path, file), content); await repository.StageAllAsync(); await repository.CommitAsync("Change " + file); }
        internal async Task<string> Advance()
        { await Commit(Writer, "remote.txt", "native remote change\n"); await Writer.PushAsync("origin", "main"); return await Writer.ResolveRevisionAsync("HEAD"); }
        internal async Task<PullDialog> Pull(GitRepository repository, GitPullAction action = GitPullAction.Merge, string? branch = null, bool visible = true)
        {
            PullDialog pull = new(repository, action, "origin", branch) { RequestedThemeVariant = Owner.ActualThemeVariant }; _pulls.Add(pull);
            if (visible) { _ = pull.ShowDialog(Owner); }
            await pull.InitializeAsync(); Assert.That(pull.LastError, Is.Null); return pull;
        }
        internal string Gate(string command, bool blockCheckpoint = false)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private Git process gate requires Linux."); }
            string gate = Root.Output("gated-git-" + Guid.NewGuid().ToString("N")); static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
            string checkpoint = blockCheckpoint ? "case \" $* \" in *' stash push '*) : > " + Quote(gate + ".active") + ";; *' stash list '*) if test -f " + Quote(gate + ".active") + "; then\n sleep 60 &\n child=$!\n printf '%s %s\\n' \"$$\" \"$child\" > " + Quote(gate + ".checkpoint.pids") + "\n : > " + Quote(gate + ".checkpoint-started") + "\n wait\n fi;; esac\n" : "";
            File.WriteAllText(gate, "#!/bin/sh\n" + checkpoint + "case \" $* \" in *' pull '*) : > " + Quote(gate + ".pull-started") + ";; esac\ncase \" $* \" in *" + Quote(" " + command + " ") + "*)\n /usr/bin/env git \"$@\"\n status=$?\n sleep 60 &\n child=$!\n"
                + TestSignalFile.ShellPids(gate + ".pids", "$$", "$child")
                + " while test ! -f " + Quote(gate + ".release") + "; do sleep 0.02; done\n kill \"$child\" 2>/dev/null || :\n wait \"$child\" 2>/dev/null || :\n exit \"$status\";;\n *) exec /usr/bin/env git \"$@\";;\nesac\n");
            File.SetUnixFileMode(gate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return gate;
        }
        internal void Release(string gate) => File.WriteAllText(gate + ".release", "release\n");
        internal async Task<GitProcessDialog> Started(string gate, Window owner)
        { await Until(() => File.Exists(gate + ".pids") && owner.OwnedWindows.OfType<GitProcessDialog>().Any()); GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single(); Pump(process); return process; }
        public async ValueTask DisposeAsync()
        {
            static void CloseOwned(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseOwned(child); child.Close(); } }
            foreach (PullDialog pull in _pulls) { CloseOwned(pull); if (pull.IsVisible) { pull.Close(); } }
            CloseOwned(Owner); Owner.Close(); await Until(() => _pulls.All(pull => !pull.IsBusy) && !Owner.IsVisible);
            Dispatcher.UIThread.RunJobs(); Root.Dispose();
        }
    }
}
