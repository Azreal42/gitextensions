using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CommitAndPushWorkflowTests
{
    [AvaloniaTest]
    public async Task SuccessfulCommitAndPushUsesTheCheckoutAndClosesAccordingToOriginalPreferences()
    {
        await using Fixture f = await Fixture.CreateAsync(close: true);
        await f.StageChangeAsync(); await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(await f.RemoteHead(), Is.EqualTo(await f.Head()));
        Assert.That(await f.Text("log", "-1", "--format=%s"), Is.EqualTo("Committed for push"));
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
        Assert.That(f.Dialog.OwnedWindows, Is.Empty);
        Assert.That(Field<TextBox>(f.Dialog, "Message").Text, Is.Empty);
    }

    [AvaloniaTest]
    public async Task HookRejectionKeepsTheSuccessfulCommitAndOriginalPushFormCanRetryInTheOwnerTheme()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Executable hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(close: false);
        f.Dialog.RequestedThemeVariant = ThemeVariant.Dark;
        string hook = f.Hook("printf 'private commit push rejection\\n' >&2\nexit 1\n");
        await f.StageChangeAsync(); await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => f.Dialog.OwnedWindows.OfType<PushDialog>().Any(window => window.IsReady && !window.IsBusy));
        PushDialog push = f.Dialog.OwnedWindows.OfType<PushDialog>().Single(); string committed = await f.Head();
        Assert.That(f.Dialog.HasCommitted, Is.True); Assert.That(f.Dialog.PushCompleted, Is.False);
        Assert.That(push.LastError, Does.Contain("private commit push rejection"));
        Assert.That(push.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(push.OwnedWindows, Is.Empty, "A hook rejection must not offer Force recovery.");
        Assert.That(await f.RemoteHead(), Is.EqualTo(f.Initial)); Assert.That(committed, Is.Not.EqualTo(f.Initial));
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
        File.Delete(hook); await PointerClick(Field<Button>(push, "ExecutePush"));
        await Until(() => !push.IsVisible && !f.Dialog.IsBusy);
        Assert.That(f.Dialog.PushCompleted, Is.True, f.Dialog.LastError); Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That(f.Dialog.IsVisible, Is.True, "Both original close preferences were disabled.");
        Assert.That(await f.Head(), Is.EqualTo(committed)); Assert.That(await f.RemoteHead(), Is.EqualTo(committed));
    }

    [AvaloniaTest]
    public async Task ClosingARejectedPushRetainsTheCommitAndOriginalClosePreferenceStillClosesCommit()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Executable hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(close: true);
        f.Hook("printf 'explicit rejected push\\n' >&2\nexit 1\n");
        await f.StageChangeAsync(); await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => f.Dialog.OwnedWindows.OfType<PushDialog>().Any(window => window.IsReady && !window.IsBusy));
        PushDialog push = f.Dialog.OwnedWindows.OfType<PushDialog>().Single(); string committed = await f.Head();
        Press(push, PhysicalKey.Escape);
        await Until(() => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.HasCommitted, Is.True); Assert.That(f.Dialog.PushCompleted, Is.False);
        Assert.That(f.Dialog.LastError, Does.Contain("explicit rejected push"));
        Assert.That(await f.Head(), Is.EqualTo(committed)); Assert.That(await f.RemoteHead(), Is.EqualTo(f.Initial));
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
        Assert.That(Field<TextBox>(f.Dialog, "Message").Text, Is.Empty, "The successful commit consumed the message even though push failed.");
    }

    [AvaloniaTest]
    public async Task NonFastForwardRefusalKeepsTheSuccessfulCommitAndCanLeaveCommitOpen()
    {
        await using Fixture f = await Fixture.CreateAsync(close: false); await f.AdvanceRemoteAsync();
        string remote = await f.RemoteHead(); await f.StageChangeAsync(); await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => f.Dialog.OwnedWindows.Any(window => window.Name == "PushRejectedConfirmation"));
        Window rejection = f.Dialog.OwnedWindows.Single(window => window.Name == "PushRejectedConfirmation");
        string committed = await f.Head(); await PointerClick(Field<Button>(rejection, "PushChoice0"));
        await Until(() => f.Dialog.OwnedWindows.OfType<PushDialog>().Any(window => window.IsReady && !window.IsBusy));
        PushDialog push = f.Dialog.OwnedWindows.OfType<PushDialog>().Single();
        Assert.That(push.Cancelled, Is.True); Assert.That(push.LastError, Does.Contain("[rejected]"));
        Press(push, PhysicalKey.Escape); await Until(() => !f.Dialog.IsBusy);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Dialog.HasCommitted, Is.True); Assert.That(f.Dialog.PushCompleted, Is.False);
        Assert.That(f.Dialog.LastError, Does.Contain("[rejected]"));
        Assert.That(await f.Head(), Is.EqualTo(committed)); Assert.That(await f.RemoteHead(), Is.EqualTo(remote));
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task RejectedCommitAndPushCanPullWithRebaseThenPublishThroughTheExistingRecoveryWorkflow()
    {
        await using Fixture f = await Fixture.CreateAsync(close: true); await f.AdvanceRemoteAsync();
        string remote = await f.RemoteHead(); await f.StageChangeAsync(); await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => f.Dialog.OwnedWindows.Any(window => window.Name == "PushRejectedConfirmation"));
        Window rejection = f.Dialog.OwnedWindows.Single(window => window.Name == "PushRejectedConfirmation");
        await PointerClick(Field<Button>(rejection, "PushChoice2"));
        await Until(() => f.Dialog.OwnedWindows.OfType<PullDialog>().Any(pull => pull.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning)),
            "Recovery Pull completed and awaits its Process acknowledgment", f.DescribeStateAsync);
        PullDialog pull = f.Dialog.OwnedWindows.OfType<PullDialog>().Single();
        GitProcessDialog process = pull.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(process.Outcome!.Success, Is.True); Assert.That(process.Result!.Success, Is.True);
        Assert.That(Field<CheckBox>(process, "KeepDialogOpen").IsChecked, Is.True); Assert.That(process.Owner, Is.SameAs(pull));
        Assert.That(f.Dialog.IsBusy && f.Dialog.HasCommitted && !f.Dialog.PushCompleted, Is.True);
        Assert.That(await f.RemoteHead(), Is.EqualTo(remote), "Publishing the rebased commit must wait for the Pull acknowledgment.");
        await PointerClick(Field<Button>(process, "Ok"));
        await Until(() => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(await f.RemoteHead(), Is.EqualTo(await f.Head()));
        Assert.That(await f.Text("rev-parse", "HEAD^"), Is.EqualTo(remote));
        Assert.That(await f.Text("log", "-1", "--format=%s"), Is.EqualTo("Committed for push"));
        Assert.That(await f.Text("rev-list", "--count", "HEAD"), Is.EqualTo("3"), "Rebase must replay the successful local commit exactly once.");
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(f.Repository.Path, "remote.txt")), Is.EqualTo("remote change\n"));
        Assert.That(File.ReadAllText(Path.Combine(f.Repository.Path, "local.txt")), Is.EqualTo("local change\n"));
    }

    [AvaloniaTest]
    public async Task AmendedCommitUsesForceWithLeaseOnlyWhenTheOriginalPreferenceRequestsIt()
    {
        await using Fixture f = await Fixture.CreateAsync(close: true, forceAmend: true);
        await f.StageChangeAsync(); await PointerClick(Field<CheckBox>(f.Dialog, "Amend"));
        await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => f.Dialog.OwnedWindows.Any());
        Window confirmation = f.Dialog.OwnedWindows.Single();
        Button accept = confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel);
        await PointerClick(accept); await Until(() => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(await f.Head(), Is.Not.EqualTo(f.Initial)); Assert.That(await f.RemoteHead(), Is.EqualTo(await f.Head()));
        Assert.That(await f.Text("rev-list", "--count", "HEAD"), Is.EqualTo("1"), "This was an amend, not an additional commit.");
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task AmendingWithoutTheForcePreferenceOffersRecoveryAndNeverAutomaticallyRewritesRemote()
    {
        await using Fixture f = await Fixture.CreateAsync(close: false, forceAmend: false);
        await f.StageChangeAsync(); await PointerClick(Field<CheckBox>(f.Dialog, "Amend"));
        await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => f.Dialog.OwnedWindows.Any()); Window confirmation = f.Dialog.OwnedWindows.Single();
        await PointerClick(confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
        await Until(() => f.Dialog.OwnedWindows.Any(window => window.Name == "PushRejectedConfirmation"));
        Window rejection = f.Dialog.OwnedWindows.Single(window => window.Name == "PushRejectedConfirmation");
        await PointerClick(Field<Button>(rejection, "PushChoice0"));
        await Until(() => f.Dialog.OwnedWindows.OfType<PushDialog>().Any(window => window.IsReady && !window.IsBusy));
        PushDialog push = f.Dialog.OwnedWindows.OfType<PushDialog>().Single();
        Assert.That(Field<CheckBox>(push, "PushForceWithLease").IsChecked, Is.False);
        Assert.That(await f.RemoteHead(), Is.EqualTo(f.Initial)); string amended = await f.Head(); Assert.That(amended, Is.Not.EqualTo(f.Initial));
        Press(push, PhysicalKey.Escape); await Until(() => !f.Dialog.IsBusy);
        Assert.That(f.Dialog.HasCommitted && !f.Dialog.PushCompleted && f.Dialog.IsVisible, Is.True);
        Assert.That(await f.Head(), Is.EqualTo(amended)); Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task SoftResetThenCommitUsesTheOriginalForcedPushPreferenceAndRestoresTheAmendControlAfterSuccess()
    {
        await using Fixture f = await Fixture.CreateAsync(close: false, forceAmend: true);
        await f.PublishAdditionalCommitAsync(); string published = await f.Head(); await f.Dialog.RefreshAsync();
        await PointerClick(Field<CheckBox>(f.Dialog, "Amend"), f.DescribeGitAsync);
        await Until(() => !f.Dialog.IsBusy && Field<CheckBox>(f.Dialog, "Amend").IsChecked == true,
            "Amend selected before the original Reset soft action", () => f.DescribeStateAsync());
        await PointerClick(Field<Button>(f.Dialog, "ResetSoft"), f.DescribeGitAsync);
        await Until(() => f.Dialog.OwnedWindows.Any(), "Reset soft confirmation opened", () => f.DescribeStateAsync());
        Window confirmation = f.Dialog.OwnedWindows.Single();
        await PointerClick(confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel), f.DescribeGitAsync);
        await Until(() => !f.Dialog.IsBusy && !confirmation.IsVisible && !Field<CheckBox>(f.Dialog, "Amend").IsEnabled,
            "Confirmed soft reset completed and Amend disabled", () => f.DescribeStateAsync());
        Assert.That(Field<CheckBox>(f.Dialog, "Amend").IsEnabled, Is.False);
        Assert.That(await f.Head(), Is.EqualTo(f.Initial)); Assert.That(await f.RemoteHead(), Is.EqualTo(published));
        Assert.That(await f.Text("diff", "--cached", "--name-only"), Is.EqualTo("published.txt"));
        Assert.That(File.ReadAllText(Path.Combine(f.Repository.Path, "published.txt")), Is.EqualTo("published change\n"));
        Field<TextBox>(f.Dialog, "Message").Text = "Replacement after soft reset";
        await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"), f.DescribeGitAsync);
        await Until(() => !f.Dialog.IsBusy, "Replacement commit and force-with-lease push finished", () => f.DescribeStateAsync());
        Assert.That(f.Dialog.HasCommitted && f.Dialog.PushCompleted, Is.True, f.Dialog.LastError);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(Field<CheckBox>(f.Dialog, "Amend").IsEnabled, Is.True);
        Assert.That(await f.RemoteHead(), Is.EqualTo(await f.Head())); Assert.That(await f.Head(), Is.Not.EqualTo(published));
        Assert.That(await f.Text("rev-list", "--count", "HEAD"), Is.EqualTo("2")); Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingCommitDuringThePushCancelsItsProcessWithoutUndoingTheSuccessfulCommit()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Executable hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(close: false);
        string marker = Path.Combine(f.Root, "push-started");
        f.Hook("printf started > '" + marker + "'\nsleep 30\n");
        await f.StageChangeAsync(); await PointerClick(Field<Button>(f.Dialog, "CommitAndPush"));
        await Until(() => File.Exists(marker)); string committed = await f.Head();
        f.Dialog.Close(); await Until(() => !f.Dialog.IsBusy && !f.Dialog.IsVisible);
        Assert.That(f.Dialog.HasCommitted, Is.True); Assert.That(f.Dialog.PushCompleted, Is.False);
        Assert.That(await f.Head(), Is.EqualTo(committed)); Assert.That(await f.RemoteHead(), Is.EqualTo(f.Initial));
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
        Assert.That(f.Dialog.OwnedWindows, Is.Empty);
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task PointerClick(Control control, Func<Task<string>>? diagnostic = null)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!;
        await Until(() =>
        {
            Pump(top); Point? point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
            return point is Point target && new Rect(default, top.ClientSize).Contains(target) && control.IsEffectivelyVisible && control.IsEnabled
                && (top.InputHitTest(target) as Visual)?.GetSelfAndVisualAncestors().Contains(control) == true;
        }, "Real pointer target " + control.Name,
            async () => DescribePointer(control, top) + (diagnostic is null ? "" : "\n" + await diagnostic()));
        Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(new Rect(default, top.ClientSize).Contains(center) && control.IsEffectivelyVisible && control.IsEnabled, Is.True, control.Name);
        Visual? hit = top.InputHitTest(center) as Visual;
        Assert.That(hit?.GetSelfAndVisualAncestors().Contains(control), Is.True, "Pointer must hit " + control.Name);
        top.MouseMove(center); top.MouseDown(center, MouseButton.Left); top.MouseUp(center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition, string step = "Commit/Push condition", Func<Task<string>>? diagnostic = null,
        [CallerArgumentExpression(nameof(condition))] string expression = "")
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        bool reached = condition();
        string detail = "";
        if (!reached)
        {
            try { detail = diagnostic is null ? "No additional state provider." : await diagnostic(); }
            catch (Exception exception) { detail = "Diagnostic capture failed: " + exception; }
            string report = $"Step: {step}\nCondition: {expression}\nElapsed: {timer.Elapsed}\n{detail}";
            string evidence = Path.Combine(TestContext.CurrentContext.WorkDirectory, "commit-push-timeout-" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(evidence, report); TestContext.AddTestAttachment(evidence);
            TestContext.Progress.WriteLine(report);
        }
        Assert.That(reached, Is.True, "Timed out waiting for " + step + ". " + expression + "\n" + detail);
    }
    private static string DescribePointer(Control control, TopLevel top)
    {
        Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        Visual? hit = center is Point point ? top.InputHitTest(point) as Visual : null;
        return $"Pointer: {control.GetType().Name} {control.Name}; bounds={control.Bounds}; center={center}; client={top.ClientSize}; "
            + $"visible={control.IsVisible}/{control.IsEffectivelyVisible}; enabled={control.IsEnabled}/{control.IsEffectivelyEnabled}; "
            + $"contained={center is Point target && new Rect(default, top.ClientSize).Contains(target)}; "
            + $"hit={hit?.GetType().Name}/{(hit as Control)?.Name}; "
            + $"ancestors={string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(item => item.GetType().Name + "/" + (item as Control)?.Name) ?? [])}\n"
            + DescribeWindow(top);
    }
    private static string DescribeWindow(TopLevel top)
    {
        StringBuilder state = new();
        state.AppendLine($"Window: {top.GetType().Name}/{top.Name}; visible={top.IsVisible}; enabled={top.IsEnabled}; client={top.ClientSize}; focus={top.FocusManager?.GetFocusedElement()}");
        if (top is CommitDialog commit) { state.AppendLine($"Commit: busy={commit.IsBusy}; committed={commit.HasCommitted}; pushCompleted={commit.PushCompleted}; error={commit.LastError}"); }
        if (top is PushDialog push) { state.AppendLine($"Push: busy={push.IsBusy}; ready={push.IsReady}; succeeded={push.Succeeded}; cancelled={push.Cancelled}; error={push.LastError}"); }
        foreach (Control child in top.GetVisualDescendants().OfType<Control>().Where(item => item is Button || item.Name is "CommitStatus" or "CommitOutput" or "Message"))
        {
            state.AppendLine($"  {child.GetType().Name}/{child.Name}: bounds={child.Bounds}; visible={child.IsVisible}/{child.IsEffectivelyVisible}; enabled={child.IsEnabled}/{child.IsEffectivelyEnabled}; "
                + (child is CheckBox check ? $"checked={check.IsChecked}" : child is Button button ? $"content={button.Content}" : child is TextBox input ? $"text={input.Text}" : child is TextBlock label ? $"text={label.Text}" : ""));
        }
        if (top is Window window)
        {
            state.AppendLine($"Title: {window.Title}; active={window.IsActive}; owned={window.OwnedWindows.Count}");
            foreach (Window owned in window.OwnedWindows) { state.AppendLine(DescribeWindow(owned)); }
        }
        return state.ToString();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly GitRunner _runner = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-commit-push-ui-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public CommitDialog Dialog { get; private set; } = null!;
        public string Initial { get; private set; } = "";
        private string Remote => Path.Combine(Root, "remote λ.git");
        public static async Task<Fixture> CreateAsync(bool close, bool forceAmend = false)
        {
            Fixture f = new(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(f.Root, "config"));
            Directory.CreateDirectory(f.Root); await f.GitAt(f.Root, "init", "--bare", "-b", "main", f.Remote);
            string path = Path.Combine(f.Root, "client"); Directory.CreateDirectory(path); f.Repository = new(path);
            await f.GitAt(path, "init", "-b", "main"); await f.Identity(path); await f.GitAt(path, "config", "core.hooksPath", Path.Combine(path, ".git", "hooks"));
            File.WriteAllText(Path.Combine(path, "base.txt"), "base\n"); await f.GitAt(path, "add", "."); await f.GitAt(path, "commit", "-m", "Initial");
            await f.GitAt(path, "remote", "add", "origin", f.Remote); await f.GitAt(path, "push", "-u", "origin", "main"); f.Initial = await f.Head();
            CommitPreferencesStore store = new(); store.Save(new() { CloseAfterEachCommit = close, CloseWhenAllChangesCommitted = close, CommitAndPushForcedWhenAmend = forceAmend });
            f.Dialog = new(f.Repository, store) { RequestedThemeVariant = ThemeVariant.Light }; f.Dialog.Show(); await f.Dialog.InitializeAsync(); Pump(f.Dialog);
            Assert.That(f.Dialog.LastError, Is.Null); return f;
        }
        private async Task Identity(string path)
        { await GitAt(path, "config", "user.name", "Commit Push UI"); await GitAt(path, "config", "user.email", "commit-push-ui@example.invalid"); await GitAt(path, "config", "commit.gpgsign", "false"); }
        public async Task StageChangeAsync()
        {
            File.WriteAllText(Path.Combine(Repository.Path, "local.txt"), "local change\n"); await GitAt(Repository.Path, "add", "--", "local.txt");
            await Dialog.RefreshAsync(); await Until(() => !Dialog.IsBusy); Field<TextBox>(Dialog, "Message").Text = "Committed for push"; Pump(Dialog);
        }
        public async Task AdvanceRemoteAsync()
        {
            string peer = Path.Combine(Root, "peer"); await GitAt(Root, "clone", Remote, peer); await Identity(peer);
            File.WriteAllText(Path.Combine(peer, "remote.txt"), "remote change\n"); await GitAt(peer, "add", "."); await GitAt(peer, "commit", "-m", "Remote change"); await GitAt(peer, "push");
        }
        public async Task PublishAdditionalCommitAsync()
        { File.WriteAllText(Path.Combine(Repository.Path, "published.txt"), "published change\n"); await GitAt(Repository.Path, "add", "."); await GitAt(Repository.Path, "commit", "-m", "Published additional commit"); await GitAt(Repository.Path, "push"); }
        public string Hook(string body)
        {
            string path = Path.Combine(Repository.Path, ".git", "hooks", "pre-push"); File.WriteAllText(path, "#!/bin/sh\n" + body);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return path;
        }
        public async Task<string> DescribeStateAsync() => DescribeWindow(Dialog) + "\n" + await DescribeGitAsync();
        public async Task<string> DescribeGitAsync()
        {
            StringBuilder state = new(); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            async Task Read(string label, string directory, params string[] arguments)
            {
                GitResult result = await _runner.RunAsync(directory, new[] { "--no-optional-locks" }.Concat(arguments).ToArray(), timeout.Token);
                state.AppendLine($"Git {label} (exit {result.ExitCode}):\n{result.StandardOutput}{result.StandardError}");
            }
            await Read("client HEAD", Repository.Path, "rev-parse", "HEAD");
            await Read("remote main", Remote, "rev-parse", "refs/heads/main");
            await Read("client status", Repository.Path, "status", "--porcelain=v2");
            await Read("client staged", Repository.Path, "diff", "--cached", "--raw");
            string index = Path.Combine(Repository.Path, ".git", "index");
            state.AppendLine("Index SHA256: " + (File.Exists(index) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(index))) : "absent"));
            return state.ToString();
        }
        public async Task<string> Text(params string[] args) => (await GitAt(Repository.Path, args)).StandardOutput.TrimEnd('\r', '\n');
        public Task<string> Head() => Text("rev-parse", "HEAD");
        public async Task<string> RemoteHead() => (await GitAt(Remote, "rev-parse", "refs/heads/main")).StandardOutput.Trim();
        private async Task<GitResult> GitAt(string path, params string[] args)
        { GitResult result = await _runner.RunAsync(path, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async ValueTask DisposeAsync()
        {
            if (Dialog is not null)
            {
                foreach (Window owned in Dialog.OwnedWindows.ToArray()) { foreach (Window nested in owned.OwnedWindows.ToArray()) { nested.Close(); } owned.Close(); }
                Dialog.Close(); await Until(() => !Dialog.IsBusy); Dialog.Close(); Dispatcher.UIThread.RunJobs();
            }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}
