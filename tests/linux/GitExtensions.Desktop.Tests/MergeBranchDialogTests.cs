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
public class MergeBranchDialogTests
{
    [AvaloniaTest]
    public Task OriginalMergeChoicesInLight() => OriginalMergeChoicesAndHelpRemainCompactAndDisableIncompatibleSquash(false);
    [AvaloniaTest]
    public Task OriginalMergeChoicesInDark() => OriginalMergeChoicesAndHelpRemainCompactAndDisableIncompatibleSquash(true);
    private async Task OriginalMergeChoicesAndHelpRemainCompactAndDisableIncompatibleSquash(bool dark)
    {
        using F f = await F.Create(); MergeBranchDialog dialog = await f.Show("topic", dark);
        Assert.That(Field<RadioButton>(dialog, "MergeFastForward").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(dialog, "MergeNoCommit").IsChecked, Is.False);
        Assert.That(Field<StackPanel>(dialog, "MergeAdvancedPanel").IsVisible, Is.False);
        Assert.That(Field<Image>(dialog, "OriginalCommandHelpImage").Bounds.Size, Is.EqualTo(new Size(289, 373)));
        Capture(dialog, "merge-" + (dark ? "dark" : "light") + ".png");
        Click(Field<CheckBox>(dialog, "MergeAdvanced")); Click(Field<CheckBox>(dialog, "MergeSquash"));
        Assert.That(dialog.ReadOptions().Squash, Is.True);
        Click(Field<RadioButton>(dialog, "MergeNoFastForward"));
        Assert.That(Field<CheckBox>(dialog, "MergeSquash").IsEnabled, Is.False); Assert.That(dialog.ReadOptions().Squash, Is.False);
        Click(Field<CheckBox>(dialog, "MergeUnrelated")); Click(Field<CheckBox>(dialog, "MergeCustomMessage"));
        Field<TextBox>(dialog, "MergeMessage").Text = "Custom message";
        Click(Field<CheckBox>(dialog, "MergeAdvanced"));
        Assert.That(dialog.ReadOptions().AllowUnrelatedHistories, Is.False); Assert.That(dialog.ReadOptions().Message, Is.Null);
        Assert.That(Field<StackPanel>(dialog, "MergeAdvancedPanel").IsVisible, Is.False);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(f.Initial));
    }
    [AvaloniaTest]
    public async Task NoCommitPointerLeavesNativeMergePendingAndRemembersOnlyTheOriginalDurableOptions()
    {
        using F f = await F.Create(); MergeBranchDialog dialog = await f.Show("topic");
        Click(Field<RadioButton>(dialog, "MergeNoFastForward")); Click(Field<CheckBox>(dialog, "MergeNoCommit")); Click(Field<Button>(dialog, "ExecuteMerge"));
        await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.Result?.Result.Success, Is.True, dialog.LastError); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(f.Initial)); Assert.That(await f.Text("show", ":topic.txt"), Is.EqualTo("topic"));
        Assert.That(System.IO.File.Exists(f.File(".git/MERGE_HEAD")), Is.True);
        MergeBranchDialog reopened = await f.Show("topic");
        Assert.That(reopened.ReadOptions().NoCommit, Is.True); Assert.That(reopened.ReadOptions().AllowFastForward, Is.False);
        Assert.That(Field<CheckBox>(reopened, "MergeAdvanced").IsChecked, Is.Not.True);
        Assert.That(reopened.OwnedWindows, Is.Empty, "Merely opening a pending merge never starts a new mutation.");
    }
    [AvaloniaTest]
    public async Task MergeDefaultsToTrackingAndFastForwardUsesTheActualEditedReference()
    {
        using F f = await F.Create(); await f.Git("remote", "add", "origin", f.Root); await f.Git("update-ref", "refs/remotes/origin/topic", f.Topic); await f.Git("config", "branch.main.remote", "origin"); await f.Git("config", "branch.main.merge", "refs/heads/topic");
        MergeBranchDialog dialog = await f.Show(null); Assert.That(Field<ComboBox>(dialog, "MergeRevision").Text, Is.EqualTo("origin/topic"));
        Field<ComboBox>(dialog, "MergeRevision").Text = f.Topic;
        Click(Field<Button>(dialog, "ExecuteMerge")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(f.Topic)); Assert.That(dialog.Result?.HeadChanged, Is.True);
        Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }
    [AvaloniaTest]
    public async Task ConflictCallbackReceivesNoCommitContractAndAClosedResolverCanBeReopenedWithoutMutation()
    {
        using F f = await F.Create(conflict: true); bool? offerCommit = null;
        MergeBranchDialog dialog = await f.Show("topic", callback: (_, _, offer, _) => { offerCommit = offer; return Task.CompletedTask; });
        Click(Field<CheckBox>(dialog, "MergeNoCommit")); Click(Field<Button>(dialog, "ExecuteMerge")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(offerCommit, Is.False); Assert.That(dialog.Result?.ConflictedPaths, Is.EqualTo(new[] { "base.txt" }));
        string head = await f.Text("rev-parse", "HEAD"), index = await f.Text("ls-files", "--stage");
        ResolveConflictsDialog resolver = new(f.Repository); f.Windows.Add(resolver); resolver.Show(f.Owner); await resolver.InitializeAsync();
        Assert.That(resolver.Conflicts.Single().Path, Is.EqualTo("base.txt")); Assert.That(Field<Button>(resolver, "ResolveOurs").IsEnabled, Is.True);
        Click(Field<Button>(resolver, "CloseConflicts")); Assert.That(await f.Text("ls-files", "--stage"), Is.EqualTo(index));
        resolver = new(f.Repository); f.Windows.Add(resolver); resolver.Show(f.Owner); await resolver.InitializeAsync();
        f.Write("base.txt", "resolved by editor\n"); Click(Field<Button>(resolver, "ResolveManual")); await Until(() => !resolver.IsBusy && resolver.Conflicts.Count == 0);
        Assert.That(await f.Text("show", ":base.txt"), Is.EqualTo("resolved by editor")); Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(head));
        Assert.That(System.IO.File.Exists(f.File(".git/MERGE_HEAD")), Is.True, "Resolving files alone must not create a commit or consume native operation metadata.");
    }
    [AvaloniaTest]
    public async Task AnIncompatibleMessageLeavesMergeOpenAndRepositoryUnchanged()
    {
        using F f = await F.Create(); await f.Git("config", "i18n.commitencoding", "iso-8859-1"); MergeBranchDialog dialog = await f.Show("topic");
        Click(Field<CheckBox>(dialog, "MergeAdvanced")); Click(Field<CheckBox>(dialog, "MergeCustomMessage")); Field<TextBox>(dialog, "MergeMessage").Text = "λ";
        Click(Field<Button>(dialog, "ExecuteMerge")); await Until(() => !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.Result, Is.Null); Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(f.Initial)); Assert.That(await f.Text("status", "--porcelain"), Is.Empty);
    }
    [AvaloniaTest]
    public async Task RejectedMergeHookKeepsItsErrorVisibleAndNeverCallsPostProcessing()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Executable hook requires Linux."); return; }
        using F f = await F.Create(); f.Write(".git/hooks/pre-merge-commit", "#!/bin/sh\necho merge-hook-rejected >&2\nexit 7\n");
        System.IO.File.SetUnixFileMode(f.File(".git/hooks/pre-merge-commit"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        bool postProcessing = false; MergeBranchDialog dialog = new(f.Repository, "topic", afterSubmoduleUpdate: (_, _) => { postProcessing = true; return Task.CompletedTask; });
        f.Windows.Add(dialog); await dialog.InitializeAsync(); _ = dialog.ShowDialog(f.Owner); Pump(dialog);
        Click(Field<RadioButton>(dialog, "MergeNoFastForward")); Click(Field<Button>(dialog, "ExecuteMerge")); await Until(() => !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(postProcessing, Is.False); Assert.That(dialog.Result, Is.Null); Assert.That(dialog.LastError, Does.Contain("merge-hook-rejected"));
        Assert.That(await f.Text("rev-parse", "HEAD"), Is.EqualTo(f.Initial)); Assert.That(System.IO.File.Exists(f.File(".git/MERGE_HEAD")), Is.True);
    }
    private static T Field<T>(Control host, string name) where T : Control => host.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top); Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(control.IsEnabled && control.IsEffectivelyVisible && new Rect(default, top.ClientSize).Contains(center), Is.True, control.Name);
        Assert.That((top.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, control.Name);
        top.MouseMove(center); top.MouseDown(center, MouseButton.Left); top.MouseUp(center, MouseButton.Left); Pump(top);
    }
    private static async Task Until(Func<bool> condition)
    { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > end) { Assert.Fail("Timed out waiting for merge workflow."); } await Task.Delay(10); } }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = System.IO.Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class F : IDisposable
    {
        private readonly Dictionary<string, string?> _old = new();
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-merge-ui-" + Guid.NewGuid().ToString("N"));
        public Window Owner { get; } = new() { Width = 900, Height = 600 };
        public List<Window> Windows { get; } = [];
        public string Initial = "", Topic = "";
        public GitRepository Repository => new(Root);
        private F()
        {
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Root + ".config"), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1") })
            { _old[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            Directory.CreateDirectory(Root);
        }
        public string File(string path) => System.IO.Path.Combine(Root, path);
        public void Write(string path, string text) => System.IO.File.WriteAllText(File(path), text);
        public async Task<GitResult> Git(params string[] args)
        { GitResult result = await new GitRunner().RunAsync(Root, args); if (!result.Success) { throw new GitCommandException(args, result); } return result; }
        public async Task<string> Text(params string[] args) => (await Git(args)).StandardOutput.TrimEnd('\r', '\n');
        public static async Task<F> Create(bool conflict = false)
        {
            F f = new(); await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Merge UI"); await f.Git("config", "user.email", "merge@example.invalid"); f.Write("base.txt", "base\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Base"); f.Initial = await f.Text("rev-parse", "HEAD");
            await f.Git("checkout", "-b", "topic"); f.Write(conflict ? "base.txt" : "topic.txt", conflict ? "theirs\n" : "topic\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Topic"); f.Topic = await f.Text("rev-parse", "HEAD"); await f.Git("checkout", "main");
            if (conflict) { f.Write("base.txt", "ours\n"); await f.Git("add", "."); await f.Git("commit", "-m", "Ours"); }
            f.Owner.Show(); return f;
        }
        public async Task<MergeBranchDialog> Show(string? revision, bool dark = false, Func<Window, GitRepository, bool, CancellationToken, Task>? callback = null)
        {
            MergeBranchDialog dialog = new(Repository, revision, callback) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Windows.Add(dialog); await dialog.InitializeAsync(); _ = dialog.ShowDialog(Owner); Pump(dialog); return dialog;
        }
        public void Dispose()
        { foreach (Window window in Windows) { window.Close(); } Owner.Close(); Dispatcher.UIThread.RunJobs(); foreach ((string name, string? value) in _old) { Environment.SetEnvironmentVariable(name, value); } if (Directory.Exists(Root)) { Directory.Delete(Root, true); } if (Directory.Exists(Root + ".config")) { Directory.Delete(Root + ".config", true); } }
    }
}
