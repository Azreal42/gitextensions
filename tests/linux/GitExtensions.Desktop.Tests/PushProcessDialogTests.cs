using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public partial class PushDialogTests
{
    [AvaloniaTest]
    public async Task PushProcessShowsRealBlobBytesBeforeTransferCompletesAndWaitsForActualOkInBothThemes()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private hook/procfs requires Linux."); return; }
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using Fixture f = await Fixture.CreateAsync(); await f.CommitAsync("published.txt", "published\n", "Publish with progress");
            byte[] blob = Encoding.UTF8.GetBytes("Private live Git café Ω\nReceiving objects:  25%\rReceiving objects: 100%\n");
            string blobFile = Path.Combine(f.Root, "native-output"); await File.WriteAllBytesAsync(blobFile, blob);
            string oid = await f.GitText("hash-object", "-w", "--", blobFile);
            string marker = Path.Combine(f.Root, "live-started"), release = Path.Combine(f.Root, "release");
            string hooks = PushHook(f, "pre-push", "/usr/bin/git cat-file blob " + oid + " > /proc/$PPID/fd/2\n"
                + "printf started > " + ShellQuote(marker) + "\nwhile test ! -f " + ShellQuote(release) + "; do sleep 0.02; done\n");
            await f.Git("config", "core.hooksPath", hooks);
            byte[] index = await File.ReadAllBytesAsync(Path.Combine(f.Client, ".git/index")); string head = await f.Head();
            PushDialog push = await f.ShowAsync(); push.RequestedThemeVariant = theme;
            Task task = push.ExecuteAsync(push); await Until(() => File.Exists(marker));
            GitProcessDialog process = push.OwnedWindows.OfType<GitProcessDialog>().Single();
            await Until(() => process.StandardErrorBytes.Span.IndexOf(blob) >= 0);
            Assert.That(process.Owner, Is.SameAs(push)); Assert.That(process.ActualThemeVariant, Is.EqualTo(theme));
            Assert.That(process.IsRunning, Is.True); Assert.That(task.IsCompleted, Is.False);
            Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("café Ω"));
            Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
            Capture(process, "push-process-live-" + theme + ".png");
            await File.WriteAllTextAsync(release, "release\n"); await Until(() => !process.IsRunning);
            Assert.That(task.IsCompleted, Is.False); Assert.That(Field<CheckBox>(process, "KeepDialogOpen").IsChecked, Is.True);
            Assert.That(Field<Button>(process, "Ok").IsEnabled, Is.True); Assert.That(Field<Button>(process, "Abort").IsEnabled, Is.False);
            Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(head));
            Capture(process, "push-process-finished-" + theme + ".png");
            PushProcessTestDriver.Pointer(Field<Button>(process, "Ok")); await task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(push.Succeeded, Is.True, push.LastError); Assert.That(push.LastProcess!.Attempts.Count, Is.EqualTo(1));
            Assert.That(push.LastProcess.Attempts[0].StandardErrorBytes.ToArray(), Is.EqualTo(push.LastProcess.Result!.StandardErrorBytes));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(f.Client, ".git/index")), Is.EqualTo(index));
            Assert.That(await f.Head(), Is.EqualTo(head));
        }
    }

    [AvaloniaTest]
    public async Task RejectedPushOwnsRecoveryBeforeDoneAndRetriesTheSameProcessWithAClearedConsole()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceRemoteAsync(); await f.CommitAsync("local.txt", "local\n", "Local replay");
        string? remote = await f.RemoteRef("zeta", "main"); string enable = Path.Combine(f.Root, "enable-retry-gate"), release = Path.Combine(f.Root, "release-retry"), marker = Path.Combine(f.Root, "retry-started");
        string hooks = PushHook(f, "pre-push", "if test -f " + ShellQuote(enable) + "; then\n printf started > " + ShellQuote(marker)
            + "\n while test ! -f " + ShellQuote(release) + "; do sleep 0.02; done\nfi\n");
        await f.Git("config", "core.hooksPath", hooks);
        PushDialog push = await f.ShowAsync(); Task task = push.ExecuteAsync(push);
        await Until(() => PushProcessTestDriver.Windows(push).Any(window => window.Name == "PushRejectedConfirmation"));
        GitProcessDialog process = push.OwnedWindows.OfType<GitProcessDialog>().Single(); Window question = process.OwnedWindows.Single();
        Assert.That(question.Owner, Is.SameAs(process)); Assert.That(process.IsRunning, Is.True);
        Assert.That(Field<Button>(process, "Ok").IsEnabled, Is.False); Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("[rejected]").And.Not.Contain("Done"));
        GitProcessAttemptResult rejected = process.Outcome!.Attempts.Single(); byte[] rejectedBytes = rejected.StandardErrorBytes.ToArray();
        await File.WriteAllTextAsync(enable, "enable\n"); PushProcessTestDriver.Pointer(Field<Button>(question, "PushChoice2"));
        await Until(() => process.OwnedWindows.OfType<PullDialog>().Any(pull => pull.OwnedWindows.OfType<GitProcessDialog>().Any(child => !child.IsRunning)));
        PullDialog pull = process.OwnedWindows.OfType<PullDialog>().Single(); GitProcessDialog pullProcess = pull.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(pull.Owner, Is.SameAs(process)); Assert.That(pullProcess.Owner, Is.SameAs(pull));
        Assert.That(push.OwnedWindows.OfType<GitProcessDialog>().Single(), Is.SameAs(process));
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(remote));
        PushProcessTestDriver.Pointer(Field<Button>(pullProcess, "Ok")); await Until(() => File.Exists(marker));
        Assert.That(push.OwnedWindows.OfType<GitProcessDialog>().Single(), Is.SameAs(process));
        Assert.That(process.IsRunning, Is.True); Assert.That(process.OwnedWindows, Is.Empty);
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Not.Contain("[rejected]").And.Not.Contain("Done"));
        Assert.That(rejected.StandardErrorBytes.ToArray(), Is.EqualTo(rejectedBytes));
        await File.WriteAllTextAsync(release, "release\n"); await Until(() => !process.IsRunning);
        PushProcessTestDriver.Pointer(Field<Button>(process, "Ok")); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(push.Succeeded, Is.True, push.LastError); Assert.That(push.LastProcess!.Attempts.Count, Is.EqualTo(2));
        Assert.That(push.LastProcess.Attempts[0], Is.SameAs(rejected)); Assert.That(push.LastProcess.Attempts[1].Result!.Success, Is.True);
        Assert.That(Field<TextBox>(push, "PushOutput").Text, Does.Contain("[rejected]").And.Contain("main -> main"));
        Assert.That(await f.GitText("rev-parse", "HEAD^"), Is.EqualTo(remote)); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(await f.Head()));
    }

    [AvaloniaTest]
    public async Task ClosingQuickPushOwnerWhileRecoveryQuestionIsOpenCannotRetryOrOpenFallbackOptions()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceRemoteAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        string head = await f.Head(); string? remote = await f.RemoteRef("zeta", "main");
        Window owner = new() { Width = 800, Height = 600 }; f.Windows.Add(owner); owner.Show();
        PushDialog push = new(f.Repository); f.Windows.Add(push); Task task = push.PushAndShowWhenFailedAsync(owner);
        await Until(() => PushProcessTestDriver.Windows(owner).Any(window => window.Name == "PushRejectedConfirmation"));
        GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single(); GitResult native = process.Result!;
        Assert.That(push.IsVisible, Is.False); Assert.That(process.OwnedWindows.Single().Owner, Is.SameAs(process));
        owner.Close(); await task.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => !owner.IsVisible);
        Assert.That(push.IsVisible, Is.False); Assert.That(push.Cancelled, Is.True); Assert.That(push.Succeeded, Is.False);
        Assert.That(push.LastProcess!.OwnerCloseRequested && push.LastProcess.WorkflowStopRequested, Is.True);
        Assert.That(push.LastProcess.Result, Is.SameAs(native)); Assert.That(push.LastProcess.WasCancelled, Is.False);
        Assert.That(push.LastProcess.Attempts.Count, Is.EqualTo(1)); Assert.That(await f.Head(), Is.EqualTo(head));
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(remote));
    }

    [AvaloniaTest]
    public async Task ClosingOwnerAfterPublishedPushBeforeOkStopsTheRemainingWorkflowButKeepsTheNativeSuccess()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.CommitAsync("local.txt", "local\n", "Published before close"); string head = await f.Head();
        Window owner = new() { Width = 800, Height = 600 }; f.Windows.Add(owner); owner.Show();
        PushDialog push = new(f.Repository); f.Windows.Add(push); Task task = push.PushAndShowWhenFailedAsync(owner);
        await Until(() => owner.OwnedWindows.OfType<GitProcessDialog>().Any(window => !window.IsRunning));
        GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single(); GitResult native = process.Result!;
        Assert.That(native.Success, Is.True); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(head));
        owner.Close(); await task.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => !owner.IsVisible);
        Assert.That(push.LastProcess!.Result, Is.SameAs(native)); Assert.That(push.LastProcess.WasCancelled, Is.False);
        Assert.That(push.LastProcess.WorkflowStopRequested && push.LastProcess.OwnerCloseRequested, Is.True);
        Assert.That(push.IsVisible, Is.False); Assert.That(push.Cancelled, Is.True); Assert.That(push.LastProcess.Attempts.Count, Is.EqualTo(1));
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(head)); Assert.That(await f.Head(), Is.EqualTo(head));
    }

    [AvaloniaTest]
    public async Task AbortDuringLiveQuickPushWaitsForItsHookActorsAndDoesNotShowFallback()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private hook/procfs requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await f.CommitAsync("local.txt", "local\n", "Cancelled push"); string head = await f.Head();
        string actors = Path.Combine(f.Root, "push-actors"); string hooks = PushHook(f, "pre-push",
            "/usr/bin/git cat-file blob HEAD:local.txt > /proc/$PPID/fd/2\nsleep 60 &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + ShellQuote(actors) + "\nwait\n");
        await f.Git("config", "core.hooksPath", hooks);
        Window owner = new() { Width = 800, Height = 600 }; f.Windows.Add(owner); owner.Show();
        PushDialog push = new(f.Repository); f.Windows.Add(push); Task task = push.PushAndShowWhenFailedAsync(owner);
        await Until(() => File.Exists(actors) && File.ReadAllText(actors).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2);
        int[] pids = (await File.ReadAllTextAsync(actors)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Dictionary<int, long> stamps = pids.ToDictionary(pid => pid, PushActorStart);
        GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single();
        await Until(() => process.StandardErrorBytes.Span.IndexOf(Encoding.UTF8.GetBytes("local\n")) >= 0);
        Assert.That(process.IsRunning, Is.True); PushProcessTestDriver.Pointer(Field<Button>(process, "Abort")); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(pids.Any(pid => PushActorAlive(pid, stamps[pid])), Is.False, "The actual hook and sleeper must have stopped before the UI operation returns.");
        Assert.That(push.Cancelled, Is.True); Assert.That(push.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.True);
        Assert.That(push.LastProcess!.WasCancelled && push.LastProcess.WorkflowStopRequested, Is.True); Assert.That(push.LastProcess.Result, Is.Null);
        Assert.That(push.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "PushOutput").Text,
            Does.Contain("local\n"), "Observed bytes remain available without an invented cancelled Git result.");
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial)); Assert.That(await f.Head(), Is.EqualTo(head));
    }

    [AvaloniaTest]
    public async Task PartiallyRejectedMultiplePushKeepsAcceptedRefsAndNeverOffersAutomaticRecovery()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private server hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await f.CommitAsync("local.txt", "local\n", "Partial push"); string head = await f.Head();
        await f.Git("branch", "accepted"); await f.Git("branch", "rejected");
        string hooks = PushHook(f, "update", "if test \"$1\" = refs/heads/rejected; then printf 'private denied reference\\n' >&2; exit 1; fi\n", "server-hooks");
        await f.RemoteGit("zeta", "config", "core.hooksPath", hooks); new ProcessDialogPreferencesStore().Save(new(true));
        PushDialog push = await f.ShowAsync(); Field<TabControl>(push, "PushTabs").SelectedIndex = 2; Pump(push);
        foreach (Grid row in Rows(push)) { foreach (CheckBox action in row.Children.OfType<CheckBox>()) { action.IsChecked = false; } }
        Row(push, "accepted").Children.OfType<CheckBox>().First().IsChecked = true;
        Row(push, "rejected").Children.OfType<CheckBox>().First().IsChecked = true;
        Task task = push.ExecuteAsync(push); await Until(() => push.OwnedWindows.OfType<GitProcessDialog>().Any(window => !window.IsRunning));
        GitProcessDialog process = push.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(task.IsCompleted, Is.False, "An error must remain visible despite success-auto-close preference.");
        Assert.That(process.OwnedWindows, Is.Empty); Assert.That(process.Result!.Success, Is.False);
        Assert.That(await f.RemoteRef("zeta", "accepted"), Is.EqualTo(head)); Assert.That(await f.RemoteRef("zeta", "rejected"), Is.Null);
        PushProcessTestDriver.Pointer(Field<Button>(process, "Ok")); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(push.Succeeded, Is.False); Assert.That(push.HasRepositoryChanges, Is.True); Assert.That(push.LastProcess!.Attempts.Count, Is.EqualTo(1));
        Assert.That(push.LastError, Does.Contain("rejected")); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
        Assert.That(await f.Head(), Is.EqualTo(head));
    }

    [AvaloniaTest]
    public async Task ClosingPushOwnerDuringNestedPullStopsBeforeAnySecondPush()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private upload-pack gate requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceRemoteAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        string head = await f.Head(); string? remote = await f.RemoteRef("zeta", "main"); string marker = Path.Combine(f.Root, "upload-started");
        string wrapper = Path.Combine(f.Root, "private-upload-pack");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nsleep 60 &\nchild=$!\nprintf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > "
            + ShellQuote(marker + ".tmp") + "\nmv " + ShellQuote(marker + ".tmp") + " " + ShellQuote(marker)
            + "\nwait \"$child\"\nexec /usr/bin/git upload-pack \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await f.Git("config", "remote.zeta.uploadpack", wrapper);
        byte[] index = await File.ReadAllBytesAsync(Path.Combine(f.Client, ".git/index"));
        byte[] config = await File.ReadAllBytesAsync(Path.Combine(f.Client, ".git/config"));
        string refs = await f.GitText("for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)");
        Dictionary<string, byte[]> files = Directory.EnumerateFiles(f.Client, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(f.Client, path).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(f.Client, path), File.ReadAllBytes, StringComparer.Ordinal);
        PushDialog push = await f.ShowAsync(); Task task = push.ExecuteAsync(push);
        await Until(() => PushProcessTestDriver.Windows(push).Any(window => window.Name == "PushRejectedConfirmation"));
        GitProcessDialog process = push.OwnedWindows.OfType<GitProcessDialog>().Single();
        PushProcessTestDriver.Pointer(Field<Button>(process.OwnedWindows.Single(), "PushChoice3")); await Until(() => File.Exists(marker));
        PullDialog pull = process.OwnedWindows.OfType<PullDialog>().Single();
        GitProcessDialog pullProcess = pull.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(pullProcess.IsRunning, Is.True); Assert.That(pullProcess.Owner, Is.SameAs(pull));
        Assert.That(pull.Owner, Is.SameAs(process)); Assert.That(process.Owner, Is.SameAs(push));
        Assert.That(process.ClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerWindowOnly));
        GitResult nativePush = process.Result!;
        int[] pids = (await File.ReadAllTextAsync(marker)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.That(pids.Length, Is.EqualTo(3)); Dictionary<int, long> stamps = pids.ToDictionary(pid => pid, PushActorStart);
        // One owner close only. No OK, second close, refocus or retry can release this operation.
        push.Close(); await task.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => !push.IsVisible);
        Assert.That(pids.Any(pid => PushActorAlive(pid, stamps[pid])), Is.False, "The gated fetch actors must stop before the owners finish closing.");
        Assert.That(pullProcess.IsRunning || process.IsRunning || pull.IsBusy || push.IsBusy, Is.False);
        Assert.That(pullProcess.IsVisible || pull.IsVisible || process.IsVisible || push.IsVisible, Is.False);
        Assert.That(push.OwnedWindows, Is.Empty); Assert.That(process.OwnedWindows, Is.Empty); Assert.That(pull.OwnedWindows, Is.Empty);
        Assert.That(pull.LastProcess!.WasCancelled, Is.True); Assert.That(pull.LastProcess.Result, Is.Null); Assert.That(pull.Cancelled, Is.True);
        Assert.That(push.LastProcess!.WorkflowStopRequested && push.LastProcess.OwnerCloseRequested, Is.True);
        Assert.That(push.LastProcess.WasCancelled, Is.False); Assert.That(push.LastProcess.Result, Is.SameAs(nativePush));
        Assert.That(push.LastProcess.Attempts.Count, Is.EqualTo(1)); Assert.That(push.LastProcess.Result!.Success, Is.False);
        Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(remote));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(f.Client, ".git/index")), Is.EqualTo(index));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(f.Client, ".git/config")), Is.EqualTo(config));
        Assert.That(await f.GitText("for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)"), Is.EqualTo(refs));
        Dictionary<string, byte[]> afterFiles = Directory.EnumerateFiles(f.Client, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(f.Client, path).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(f.Client, path), File.ReadAllBytes, StringComparer.Ordinal);
        Assert.That(afterFiles.Keys, Is.EquivalentTo(files.Keys));
        foreach ((string path, byte[] bytes) in files) { Assert.That(afterFiles[path], Is.EqualTo(bytes), path); }
    }

    [AvaloniaTest]
    public async Task ClosingProcessDuringItsRecoveryQuestionStopsQuickPushWithoutClosingTheVisibleOwner()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceRemoteAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        string? remote = await f.RemoteRef("zeta", "main");
        Window owner = new() { Width = 800, Height = 600 }; f.Windows.Add(owner); owner.Show();
        PushDialog push = new(f.Repository); f.Windows.Add(push); Task task = push.PushAndShowWhenFailedAsync(owner);
        await Until(() => PushProcessTestDriver.Windows(owner).Any(window => window.Name == "PushRejectedConfirmation"));
        GitProcessDialog process = owner.OwnedWindows.OfType<GitProcessDialog>().Single(); GitResult native = process.Result!;
        process.Close(); await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(owner.IsVisible, Is.True); Assert.That(push.IsVisible, Is.False); Assert.That(push.Cancelled, Is.True);
        Assert.That(push.LastProcess!.WorkflowStopRequested, Is.True); Assert.That(push.LastProcess.WasCancelled, Is.False);
        Assert.That(push.LastProcess.Result, Is.SameAs(native)); Assert.That(push.LastProcess.Attempts.Count, Is.EqualTo(1));
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(remote));
    }

    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string PushHook(Fixture f, string name, string body, string directory = "process-hooks")
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        string hooks = Path.Combine(f.Root, directory); Directory.CreateDirectory(hooks); string path = Path.Combine(hooks, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return hooks;
    }
    private static long PushActorStart(int pid)
    { string text = File.ReadAllText("/proc/" + pid + "/stat"); return long.Parse(text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19]); }
    private static bool PushActorAlive(int pid, long start)
    {
        try
        {
            string text = File.ReadAllText("/proc/" + pid + "/stat"); string[] fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return long.Parse(fields[19]) == start && fields[0] is not ("Z" or "X" or "x");
        }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
}
