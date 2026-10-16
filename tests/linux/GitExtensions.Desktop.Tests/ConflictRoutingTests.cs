using System.Diagnostics;
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
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class ConflictRoutingTests
{
    [AvaloniaTest]
    [TestCase("ConflictMergeTool")]
    [TestCase("ResolveManual")]
    public async Task ResolverGitlinkEntryAndCancellationKeepTheActualConflictAndParentHead(string action)
    {
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        ResolveConflictsDialog resolver = await f.ShowAsync(new ResolveConflictsDialog(f.Repository));
        string entries = await f.Text("ls-files", "--stage", "-z");
        int reloads = 0;
        Field<ListBox>(resolver, "ConflictFiles").PropertyChanged += (_, change) =>
        { if (change.Property == ItemsControl.ItemsSourceProperty) { reloads++; } };
        Assert.That(Field<Button>(resolver, "ResolveBase").IsEnabled, Is.False);
        Assert.That(Field<Button>(resolver, "ResolveOurs").IsEnabled, Is.False);
        Assert.That(Field<Button>(resolver, "ResolveTheirs").IsEnabled, Is.False);
        Pointer(Field<Button>(resolver, action));
        SubmoduleConflictDialog child = await SubmoduleAsync(resolver);
        Assert.That(child.Owner, Is.SameAs(resolver));
        Assert.That(child.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(Field<TextBox>(child, "tbBase").Text, Is.EqualTo(f.Base));
        Assert.That(Field<TextBox>(child, "tbLocal").Text, Is.EqualTo(f.Local));
        Assert.That(Field<TextBox>(child, "tbRemote").Text, Is.EqualTo(f.Remote));
        child.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        if (child.IsVisible) { child.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
        else { resolver.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
        await Until(() => !child.IsVisible && !resolver.IsBusy);
        Assert.That(resolver.LastError, Is.Null, resolver.LastException?.ToString());
        Assert.That(resolver.HasRepositoryChanges, Is.False);
        Assert.That(resolver.Conflicts.Select(conflict => conflict.Path), Is.EqualTo(new[] { Fixture.ModulePath }));
        Assert.That(reloads, Is.EqualTo(1), "Returning from the child refreshes the parent list once, including Cancel.");
        Assert.That(await f.Text("ls-files", "--stage", "-z"), Is.EqualTo(entries));
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("rev-parse", "MERGE_HEAD"), Is.EqualTo(f.ParentIncoming));
    }

    [AvaloniaTest]
    public async Task ResolverStagesTheSelectedChildRevisionOnceAndRefreshesWithoutCommittingOrContinuing()
    {
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        await f.ChildGit("switch", "--detach", f.Resolution);
        string wrapper = await f.CountingGitAsync();
        ResolveConflictsDialog resolver = await f.ShowAsync(new ResolveConflictsDialog(new GitRepository(f.Repository.Path, new GitRunner(wrapper))));
        int reloads = 0;
        Field<ListBox>(resolver, "ConflictFiles").PropertyChanged += (_, change) =>
        { if (change.Property == ItemsControl.ItemsSourceProperty) { reloads++; } };
        Pointer(Field<Button>(resolver, "ResolveManual"));
        SubmoduleConflictDialog child = await SubmoduleAsync(resolver);
        Assert.That(Field<TextBox>(child, "tbCurrent").Text, Is.EqualTo(f.Resolution));
        Pointer(Field<Button>(child, "btStageCurrent"));
        await Until(() => !child.IsVisible && !resolver.IsBusy);
        Assert.That(child.Resolved, Is.True, child.LastError);
        Assert.That(child.LastStageResult?.IsResolved, Is.True);
        Assert.That(resolver.LastError, Is.Null, resolver.LastException?.ToString());
        Assert.That(resolver.HasRepositoryChanges, Is.True);
        Assert.That(resolver.Conflicts, Is.Empty);
        Assert.That(reloads, Is.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(wrapper + ".adds"), Is.EqualTo("add\n"), "The resolver must not stage again after the assistant did so.");
        Assert.That(await f.Text("ls-files", "--stage", "--", Fixture.ModulePath), Is.EqualTo($"160000 {f.Resolution} 0\t{Fixture.ModulePath}"));
        Assert.That(await f.Text("ls-files", "--unmerged", "-z"), Is.Empty);
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("rev-parse", "MERGE_HEAD"), Is.EqualTo(f.ParentIncoming));
        Assert.That(resolver.IsVisible, Is.True, "Only the child closes; continuing or committing remains an explicit parent action.");
    }

    [AvaloniaTest]
    public async Task OrdinaryTextConflictKeepsItsEditorAndStagesTheEditedBytesWithoutAnAutomaticCommit()
    {
        await using Fixture f = await Fixture.CreateTextAsync();
        Task<bool> opened = ConflictedPathDialog.ShowAsync(f.Owner, f.Repository, "file.txt");
        await Until(() => f.Owner.OwnedWindows.OfType<PatchConflictDialog>().Any(window => !window.IsBusy));
        PatchConflictDialog child = f.Owner.OwnedWindows.OfType<PatchConflictDialog>().Single();
        Assert.That(f.Owner.OwnedWindows.OfType<SubmoduleConflictDialog>(), Is.Empty);
        Assert.That(child.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        TextBox editor = Field<TextBox>(child, "PatchConflictText");
        Pointer(editor);
        child.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        child.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
        child.KeyTextInput("resolved routing λ\n");
        Pointer(Field<Button>(child, "SavePatchResolution"));
        Assert.That(await opened.WaitAsync(TimeSpan.FromSeconds(15)), Is.True);
        Assert.That(await f.Text("show", ":file.txt"), Is.EqualTo("resolved routing λ"));
        Assert.That(await f.Text("ls-files", "--unmerged", "-z"), Is.Empty);
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("rev-parse", "MERGE_HEAD"), Is.EqualTo(f.ParentIncoming));
    }

    [AvaloniaTest]
    public async Task PullSelectedGitlinkCanCancelThenResolveWithoutFinishingThePendingMerge()
    {
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        PullDialog pull = await f.ShowAsync(new PullDialog(f.Repository));
        Assert.That(Field<ListBox>(pull, "PullConflicts").SelectedItem, Is.EqualTo(Fixture.ModulePath));
        string entries = await f.Text("ls-files", "--unmerged", "-z");
        Pointer(Field<Button>(pull, "ResolvePullConflict"));
        SubmoduleConflictDialog cancelled = await SubmoduleAsync(pull);
        cancelled.Close();
        await Until(() => !cancelled.IsVisible && !pull.IsBusy);
        Assert.That(await f.Text("ls-files", "--unmerged", "-z"), Is.EqualTo(entries));
        Assert.That(Field<ListBox>(pull, "PullConflicts").Items.Count, Is.EqualTo(1));
        Assert.That(pull.Attempted, Is.False);
        await f.ChildGit("switch", "--detach", f.Resolution);
        Pointer(Field<Button>(pull, "ResolvePullConflict"));
        SubmoduleConflictDialog resolved = await SubmoduleAsync(pull);
        Pointer(Field<Button>(resolved, "btStageCurrent"));
        await Until(() => !resolved.IsVisible && !pull.IsBusy);
        Assert.That(pull.LastError, Is.Null, pull.LastException?.ToString());
        Assert.That(pull.Attempted && pull.HasRepositoryChanges, Is.True);
        Assert.That(pull.Succeeded, Is.False);
        Assert.That(Field<ListBox>(pull, "PullConflicts").Items, Is.Empty);
        Assert.That(Field<Button>(pull, "ContinuePullOperation").IsEnabled, Is.True);
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("rev-parse", "MERGE_HEAD"), Is.EqualTo(f.ParentIncoming));
    }

    [AvaloniaTest]
    public async Task StashWorktreeConflictUsesTheSameAssistantAndRefreshesAfterResolution()
    {
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        StashDialog stash = await f.ShowAsync(new StashDialog(f.Repository));
        Assert.That(Field<ComboBox>(stash, "StashSelection").SelectedIndex, Is.EqualTo(0));
        Pointer(Field<Button>(stash, "ResolveStashConflict"));
        SubmoduleConflictDialog child = await SubmoduleAsync(stash);
        await f.ChildGit("switch", "--detach", f.Resolution);
        Pointer(Field<Button>(child, "btRefresh"));
        await Until(() => !child.IsBusy && Field<TextBox>(child, "tbCurrent").Text == f.Resolution);
        Pointer(Field<Button>(child, "btStageCurrent"));
        await Until(() => !child.IsVisible && !stash.IsBusy);
        Assert.That(stash.LastError, Is.Null, stash.LastException?.ToString());
        Assert.That(stash.HasRepositoryChanges, Is.True);
        Assert.That(Field<Button>(stash, "ResolveStashConflict").IsVisible, Is.False);
        Assert.That(Field<ComboBox>(stash, "StashSelection").SelectedIndex, Is.EqualTo(0));
        Assert.That(await f.Text("ls-files", "--unmerged", "-z"), Is.Empty);
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("rev-parse", "MERGE_HEAD"), Is.EqualTo(f.ParentIncoming));
    }

    [AvaloniaTest]
    public async Task CherryPickGitlinkRecoveryStagesTheChildWithoutAcceptingOrContinuingThePick()
    {
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        await f.Git("merge", "--abort");
        CherryPickDialog pick = await f.ShowAsync(new CherryPickDialog(f.Repository, f.ParentIncoming));
        Pointer(Field<Button>(pick, "ExecuteCherryPick"));
        await Until(() => !pick.IsBusy && Field<Button>(pick, "ResolveCherryPickConflicts").IsEnabled);
        Assert.That(Field<ListBox>(pick, "CherryPickConflictFiles").SelectedItem, Is.EqualTo(Fixture.ModulePath));
        await f.ChildGit("switch", "--detach", f.Resolution);
        Pointer(Field<Button>(pick, "ResolveCherryPickConflicts"));
        SubmoduleConflictDialog child = await SubmoduleAsync(pick);
        Pointer(Field<Button>(child, "btStageCurrent"));
        await Until(() => !child.IsVisible && !pick.IsBusy);
        Assert.That(pick.LastError, Is.Null, pick.LastException?.ToString());
        Assert.That(pick.HasRepositoryChanges, Is.True);
        Assert.That(Field<ListBox>(pick, "CherryPickConflictFiles").Items, Is.Empty);
        Assert.That(Field<Button>(pick, "ContinueCherryPick").IsEnabled, Is.True);
        Assert.That(pick.Applied || pick.SequenceAccepted, Is.False);
        Assert.That(pick.IsVisible, Is.True);
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("ls-files", "--stage", "--", Fixture.ModulePath), Is.EqualTo($"160000 {f.Resolution} 0\t{Fixture.ModulePath}"));
    }

    [AvaloniaTest]
    public async Task MailboxGitlinkRecoveryStagesTheChildAndLeavesNativeAmPendingUntilExplicitContinue()
    {
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        await f.Git("merge", "--abort");
        string patch = await f.IncomingPatchAsync();
        ApplyPatchDialog apply = await f.ShowAsync(new ApplyPatchDialog(f.Repository, patch));
        Pointer(Field<Button>(apply, "ApplyPatch"));
        await Until(() => !apply.IsBusy && apply.AmState?.IsActive == true);
        Assert.That(apply.AmState!.ConflictedPaths, Does.Contain(Fixture.ModulePath));
        await f.ChildGit("switch", "--detach", f.Resolution);
        Pointer(Field<Button>(apply, "ResolvePatchConflicts"));
        SubmoduleConflictDialog child = await SubmoduleAsync(apply);
        Pointer(Field<Button>(child, "btStageCurrent"));
        await Until(() => !child.IsVisible && !apply.IsBusy);
        Assert.That(apply.LastError, Is.Null, apply.LastException?.ToString());
        Assert.That(apply.HasRepositoryChanges, Is.True);
        Assert.That(apply.AmState?.IsActive, Is.True);
        Assert.That(apply.AmState!.ConflictedPaths, Is.Empty);
        Assert.That(Field<Button>(apply, "ContinuePatch").IsEnabled, Is.True);
        Assert.That(apply.IsVisible, Is.True);
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("ls-files", "--stage", "--", Fixture.ModulePath), Is.EqualTo($"160000 {f.Resolution} 0\t{Fixture.ModulePath}"));
    }

    [AvaloniaTest]
    public async Task ClosingTheRoutingOwnerDrainsAnActualGitStageAndReturnsItsPartialEffects()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("The command barrier uses a POSIX shell."); }
        await using Fixture f = await Fixture.CreateGitlinkAsync();
        await f.ChildGit("switch", "--detach", f.Resolution);
        string wrapper = await f.CountingGitAsync(blockAdd: true);
        Task<bool> opened = ConflictedPathDialog.ShowAsync(f.Owner, new GitRepository(f.Repository.Path, new GitRunner(wrapper)), Fixture.ModulePath);
        SubmoduleConflictDialog child = await SubmoduleAsync(f.Owner);
        Pointer(Field<Button>(child, "btStageCurrent"));
        await Until(() => File.Exists(wrapper + ".started"));
        Assert.That(await f.Text("ls-files", "--stage", "--", Fixture.ModulePath), Is.EqualTo($"160000 {f.Resolution} 0\t{Fixture.ModulePath}"), "The native add must complete before the process is held.");
        f.Owner.Close();
        Assert.That(await opened.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "Cancellation cannot hide effects already made by the native add.");
        await Until(() => !child.IsVisible && !child.IsBusy && !f.Owner.IsVisible);
        Assert.That(child.Resolved, Is.False, "Cancellation stops the workflow even if Git already changed the index.");
        Assert.That(f.Owner.OwnedWindows, Is.Empty);
        int pid = int.Parse(await File.ReadAllTextAsync(wrapper + ".pid"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(IsRunning(pid), Is.False, "Returning to the owner must wait for the command process to exit.");
        Assert.That(await f.Head(), Is.EqualTo(f.ParentHead));
        Assert.That(await f.Text("rev-parse", "MERGE_HEAD"), Is.EqualTo(f.ParentIncoming));
        Assert.That(await File.ReadAllTextAsync(wrapper + ".adds"), Is.EqualTo("add\n"));
    }

    private static bool IsRunning(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<SubmoduleConflictDialog> SubmoduleAsync(Window owner)
    {
        await Until(() => owner.OwnedWindows.OfType<SubmoduleConflictDialog>().Any(window => !window.IsBusy));
        SubmoduleConflictDialog child = owner.OwnedWindows.OfType<SubmoduleConflictDialog>().Single();
        Assert.That(child.LastError, Is.Null, child.LastException?.ToString());
        return child;
    }

    private static T Field<T>(Control host, string name) where T : Control
        => host.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

    private static void Pointer(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!;
        Pump(top);
        Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && new Rect(default, top.ClientSize).Contains(center), Is.True, control.Name);
        Assert.That((top.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, control.Name);
        top.MouseMove(center); top.MouseDown(center, MouseButton.Left); top.MouseUp(center, MouseButton.Left); Pump(top);
    }

    private static async Task Until(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > end) { Assert.Fail("Timed out waiting for conflict routing."); }
            await Task.Delay(10);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string ModulePath = "modules/child (routing)";
        private readonly ReferenceFixture _root;
        private readonly List<Window> _windows = [];
        private readonly List<string> _gates = [];
        internal Window Owner { get; } = new() { Width = 1080, Height = 760, RequestedThemeVariant = ThemeVariant.Dark };
        internal GitRepository Repository => _root.Repository;
        internal string Base = "", Local = "", Remote = "", Resolution = "", ParentHead = "", ParentIncoming = "";
        private string ChildPath => _root.PathInRepository(ModulePath);

        private Fixture(ReferenceFixture root) { _root = root; Owner.Show(); }

        internal async Task<T> ShowAsync<T>(T dialog) where T : RepositoryDialog
        {
            _windows.Add(dialog); dialog.RequestedThemeVariant = Owner.ActualThemeVariant;
            _ = dialog.ShowDialog(Owner);
            await dialog.InitializeAsync(); Pump(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            return dialog;
        }

        internal async Task<string> Text(params string[] args) => (await _root.GitAsync(args)).StandardOutput.TrimEnd('\r', '\n');
        internal Task<GitResult> Git(params string[] args) => _root.GitAsync(args);
        internal Task<string> Head() => Text("rev-parse", "HEAD");
        internal async Task ChildGit(params string[] args) { await Native(ChildPath, args); }
        internal async Task<string> IncomingPatchAsync()
        {
            string path = _root.Output("incoming.patch");
            GitResult patch = await _root.GitAsync("format-patch", "--stdout", "--no-signature", "-1", ParentIncoming);
            await File.WriteAllTextAsync(path, patch.StandardOutput); return path;
        }

        private static async Task<GitResult> Native(string directory, params string[] args)
        {
            GitResult result = await new GitRunner().RunAsync(directory, args);
            Assert.That(result.Success, Is.True, result.StandardError);
            return result;
        }

        internal static async Task<Fixture> CreateGitlinkAsync()
        {
            ReferenceFixture root = await ReferenceFixture.CreateAsync();
            Fixture f = new(root);
            try
            {
                string source = root.Output("child-source"); Directory.CreateDirectory(source);
                await Native(source, "init", "--initial-branch=main");
                await Native(source, "config", "user.name", "Conflict Routing");
                await Native(source, "config", "user.email", "conflict-routing@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(source, "base.txt"), "base\n");
                await Native(source, "add", "."); await Native(source, "commit", "-m", "Child base");
                f.Base = (await Native(source, "rev-parse", "HEAD")).StandardOutput.Trim();
                await Native(source, "switch", "--create", "local");
                await File.WriteAllTextAsync(Path.Combine(source, "local.txt"), "local\n");
                await Native(source, "add", "."); await Native(source, "commit", "-m", "Child local");
                f.Local = (await Native(source, "rev-parse", "HEAD")).StandardOutput.Trim();
                await Native(source, "switch", "--create", "remote", f.Base);
                await File.WriteAllTextAsync(Path.Combine(source, "remote.txt"), "remote\n");
                await Native(source, "add", "."); await Native(source, "commit", "-m", "Child remote");
                f.Remote = (await Native(source, "rev-parse", "HEAD")).StandardOutput.Trim();
                await Native(source, "switch", "--create", "resolution", f.Local);
                await Native(source, "merge", "--no-ff", "-m", "Child merge resolution", f.Remote);
                f.Resolution = (await Native(source, "rev-parse", "HEAD")).StandardOutput.Trim();
                await root.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--name", "child", source, ModulePath);
                await f.ChildGit("switch", "--detach", f.Base);
                await root.GitAsync("add", ".gitmodules", ModulePath); await root.GitAsync("commit", "-m", "Pin base");
                await root.GitAsync("switch", "--create", "incoming");
                await f.ChildGit("switch", "--detach", f.Remote);
                await root.GitAsync("add", ModulePath); await root.GitAsync("commit", "-m", "Pin incoming");
                f.ParentIncoming = await f.Head();
                await root.GitAsync("switch", "main");
                await f.ChildGit("switch", "--detach", f.Local);
                await root.GitAsync("add", ModulePath); await root.GitAsync("commit", "-m", "Pin local");
                f.ParentHead = await f.Head();
                GitResult merge = await new GitRunner().RunAsync(f.Repository.Path, ["merge", "--no-edit", "incoming"]);
                Assert.That(merge.ExitCode, Is.EqualTo(1), merge.StandardOutput + merge.StandardError);
                GitConflictPath conflict = (await f.Repository.GetConflictsAsync()).Single();
                Assert.That(conflict.Path, Is.EqualTo(ModulePath));
                Assert.That(conflict.Stages.Select(stage => (stage.Stage, stage.Mode, stage.Hash)), Is.EqualTo(new[] { (1, "160000", f.Base), (2, "160000", f.Local), (3, "160000", f.Remote) }));
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal static async Task<Fixture> CreateTextAsync()
        {
            ReferenceFixture root = await ReferenceFixture.CreateAsync(); Fixture f = new(root);
            try
            {
                await root.GitAsync("switch", "--create", "incoming"); await root.ChangeAsync("theirs\n", "Text incoming");
                f.ParentIncoming = await f.Head(); await root.GitAsync("switch", "main"); await root.ChangeAsync("ours\n", "Text local");
                f.ParentHead = await f.Head();
                GitResult merge = await new GitRunner().RunAsync(f.Repository.Path, ["merge", "--no-edit", "incoming"]);
                Assert.That(merge.ExitCode, Is.EqualTo(1)); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal async Task<string> CountingGitAsync(bool blockAdd = false)
        {
            if (OperatingSystem.IsWindows()) { Assert.Ignore("The native command counter uses a POSIX shell."); return ""; }
            string path = _root.Output("git-counter-" + Guid.NewGuid().ToString("N")); _gates.Add(path);
            string real = File.Exists("/usr/bin/git") ? "/usr/bin/git" : "git";
            string barrier = blockAdd ? $"printf '%s' \"$$\" > {Quote(path + ".pid")}\n: > {Quote(path + ".started")}\nwhile [ ! -e {Quote(path + ".release")} ]; do sleep 0.01; done\n" : "";
            await File.WriteAllTextAsync(path, $"#!/bin/sh\nis_add=false\nfor arg in \"$@\"; do if [ \"$arg\" = add ]; then is_add=true; fi; done\n{Quote(real)} \"$@\"\ncode=$?\nif $is_add; then printf 'add\\n' >> {Quote(path + ".adds")}\n{barrier}fi\nexit \"$code\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        public async ValueTask DisposeAsync()
        {
            foreach (string gate in _gates) { await File.WriteAllTextAsync(gate + ".release", "release\n"); }
            List<Window> windows = [];
            void Collect(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { Collect(child); } windows.Add(window); }
            Collect(Owner);
            foreach (Window window in _windows) { if (!windows.Contains(window)) { Collect(window); } }
            foreach (Window window in windows) { window.Close(); }
            await Until(() => windows.OfType<RepositoryDialog>().All(window => !window.IsBusy));
            Owner.Close(); Dispatcher.UIThread.RunJobs(); _root.Dispose();
        }
    }
}
