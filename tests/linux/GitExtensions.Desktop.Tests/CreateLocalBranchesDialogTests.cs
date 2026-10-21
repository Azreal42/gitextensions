using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CreateLocalBranchesDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task CompactSourceFormUsesThePluginCaptionsAndEscapingADraftCannotWrite(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.CurrentUICulture);
        string before = await Materials(f);
        await using Lease s = Lease.Open(f.Repository, dark);
        CreateLocalBranchesDialog dialog = s.Dialog;
        TextBox remote = Field<TextBox>(dialog, "_NO_TRANSLATE_Remote"); Button create = Field<Button>(dialog, "button1");
        Assert.That(dialog.Title, Is.EqualTo(translations.Translate("CreateLocalBranchesForm", "$this", "Text", "Create local tracking branches")));
        Assert.That(((AccessText)create.Content!).Text, Is.EqualTo(translations.Translate("CreateLocalBranchesForm", "button1", "Text", "Create local tracking branches")));
        TextBlock label = Field<TextBlock>(dialog, "label1");
        Assert.That(label.Text, Is.EqualTo(translations.Translate("CreateLocalBranchesForm", "label1", "Text", "Remote to create tracking branches for")));
        Assert.That(remote.Text, Is.EqualTo("origin")); Assert.That(create.IsDefault, Is.False, "The Windows source has no AcceptButton.");
        Assert.That(create.IsKeyboardFocusWithin, Is.True); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(s.Owner.ActualThemeVariant));
        Assert.That(dialog.CanResize || dialog.CanMinimize || dialog.CanMaximize || dialog.ShowInTaskbar, Is.False);
        Assert.That(dialog.Icon, Is.Not.Null); Assert.That(dialog.SizeToContent, Is.EqualTo(SizeToContent.WidthAndHeight));
        Assert.That(dialog.MinWidth, Is.EqualTo(518 * 96d / 120)); Assert.That(dialog.MinHeight, Is.EqualTo(79 * 96d / 120));
        Assert.That(dialog.ClientSize.Width, Is.GreaterThanOrEqualTo(dialog.MinWidth - .5)); Assert.That(dialog.ClientSize.Height, Is.GreaterThanOrEqualTo(dialog.MinHeight - .5));
        Assert.That(dialog.GetVisualDescendants().OfType<TextBox>().Count(), Is.EqualTo(1));
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible), Is.EqualTo(1));
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Name == "OperationStatus"), Is.False);
        // Preserve the failed geometry and frame before the existing compact-layout assertions run.
        TestContext.Progress.WriteLine($"CreateLocal geometry: language={language} dark={dark} client={dialog.ClientSize} bounds={dialog.Bounds} desired={dialog.DesiredSize} width={dialog.Width} height={dialog.Height} min={dialog.MinWidth}x{dialog.MinHeight} sizeToContent={dialog.SizeToContent}");
        foreach (Control control in new Control[] { label, remote, create, Field<Grid>(dialog, "CreateLocalBranchesRemote"), Field<Grid>(dialog, "CreateLocalBranchesLayout") })
        {
            Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
            string columns = control is Grid grid ? string.Join(",", grid.ColumnDefinitions.Select(column => $"{column.Width}/{column.ActualWidth}")) : "";
            TestContext.Progress.WriteLine($"  {control.Name}: absolute={bounds} local={control.Bounds} desired={control.DesiredSize} margin={control.Margin} min={control.MinWidth}x{control.MinHeight} columns={columns}");
        }
        Capture(dialog, "create-local-branches-geometry-" + language + (dark ? "-dark.png" : "-light.png"), passive: true);
        foreach (Control control in new Control[] { label, remote, create })
        {
            Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
            Assert.That(bounds.Width > 0 && bounds.Height > 0 && new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, control.Name);
        }
        Assert.That(label.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
        Assert.That(label.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(label.Bounds.Width + .5));
        Control caption = (Control)create.Content!;
        Assert.That(new Rect(default, create.Bounds.Size).Contains(new Rect(caption.TranslatePoint(default, create)!.Value, caption.Bounds.Size)), Is.True);
        Capture(dialog, "create-local-branches-" + language + (dark ? "-dark.png" : "-light.png"));
        Edit(dialog, "a remote draft"); Press(dialog, PhysicalKey.Escape);
        await s.Modal.WaitAsync(TimeSpan.FromSeconds(5)); await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(dialog.LastResult, Is.Null); Assert.That(dialog.MayHaveRepositoryChanges, Is.False);
        Assert.That(await Materials(f), Is.EqualTo(before)); Assert.That(s.Owner.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task AllRemoteBranchesUseTheRealEngineAndTheOwnedSummaryMustBeAcknowledgedBeforeClosing()
    {
        using Fixture f = await Fixture.CreateAsync();
        string old = await f.Repository.ResolveRevisionAsync("HEAD"); await f.CreateRemoteAsync();
        await f.ChangeAsync("local advanced\n", "Local advanced"); string current = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.GitAsync("branch", "existing");
        await f.GitAsync("update-ref", "refs/remotes/origin/existing", old);
        await f.GitAsync("update-ref", "refs/remotes/origin/feature/été", old);
        await f.GitAsync("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        byte[] worktree = File.ReadAllBytes(f.File), index = File.ReadAllBytes(f.PathInRepository(".git/index"));
        await using Lease s = Lease.Open(f.Repository); Pointer(Field<Button>(s.Dialog, "button1"));
        Window summary = await Message(s.Dialog);
        PluginResult result = s.Dialog.LastResult!;
        Assert.That(result.Success, Is.True); Assert.That(result.RepositoryChanged, Is.True);
        Assert.That(result.Text, Is.EqualTo("1 tracking branch(es) created; 0 failed."));
        Assert.That(summary.Owner, Is.SameAs(s.Dialog)); Assert.That(summary.Title, Is.EqualTo("Information"));
        Assert.That(Field<TextBlock>(summary, "MessageDialogText").Text, Is.EqualTo(result.Text));
        Assert.That(summary.GetVisualDescendants().OfType<Image>().Single(image => image.Source is not null).Tag, Is.EqualTo("information.png"));
        Assert.That(s.Dialog.IsBusy && s.Dialog.IsVisible, Is.True); Assert.That(s.Modal.IsCompleted || s.Dialog.WindowWorkCompletion.IsCompleted, Is.False);
        IReadOnlyList<GitReference> refs = await f.Repository.GetReferencesAsync();
        Assert.That(refs.Single(reference => reference.Name == "refs/heads/feature/été").Upstream, Is.EqualTo("origin/feature/été"));
        Assert.That(await f.Repository.ResolveRevisionAsync("existing"), Is.EqualTo(current));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(current));
        Assert.That(refs.Any(reference => reference.Name == "refs/heads/HEAD"), Is.False);
        Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That(File.ReadAllBytes(f.File), Is.EqualTo(worktree)); Assert.That(File.ReadAllBytes(f.PathInRepository(".git/index")), Is.EqualTo(index));
        Pointer(Field<Button>(summary, "MessageDialogAccept")); await s.Modal.WaitAsync(TimeSpan.FromSeconds(5));
        await s.Dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(s.Dialog.HasRepositoryChanges, Is.True);
        Assert.That(s.Dialog.LastError, Is.Null); Assert.That(s.Owner.OwnedWindows, Is.Empty);

        await using Lease repeated = Lease.Open(f.Repository); Pointer(Field<Button>(repeated.Dialog, "button1"));
        Window unchanged = await Message(repeated.Dialog);
        Assert.That(repeated.Dialog.LastResult!.Text, Is.EqualTo("0 tracking branch(es) created; 0 failed."));
        Assert.That(repeated.Dialog.HasRepositoryChanges, Is.False); Assert.That(repeated.Dialog.LastResult.RepositoryChanged, Is.False);
        Pointer(Field<Button>(unchanged, "MessageDialogAccept")); await repeated.Modal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    [TestCase("")] [TestCase("missing")]
    public async Task AnInvalidRemoteShowsAnOwnedErrorAndKeepsTheLiteralDraftForRetry(string remote)
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); string before = await Materials(f);
        await using Lease s = Lease.Open(f.Repository); Edit(s.Dialog, remote); Pointer(Field<Button>(s.Dialog, "button1"));
        Window error = await Message(s.Dialog);
        Assert.That(error.Owner, Is.SameAs(s.Dialog)); Assert.That(error.Title, Is.EqualTo("Error"));
        Assert.That(s.Dialog.LastException, Is.TypeOf<ArgumentException>()); Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Is.EqualTo(s.Dialog.LastError));
        Assert.That(Field<TextBox>(s.Dialog, "_NO_TRANSLATE_Remote").Text, Is.EqualTo(remote));
        Assert.That(s.Dialog.HasRepositoryChanges, Is.False); Assert.That(await Materials(f), Is.EqualTo(before));
        if (remote.Length == 0) { Press(error, PhysicalKey.Escape); } else { Pointer(Field<Button>(error, "MessageDialogAccept")); }
        await Until(s.Dialog, () => !s.Dialog.IsBusy);
        Assert.That(s.Dialog.IsVisible, Is.True); Assert.That(Field<Button>(s.Dialog, "button1").IsEnabled, Is.True);
        Edit(s.Dialog, "origin"); Pointer(Field<Button>(s.Dialog, "button1")); Window success = await Message(s.Dialog);
        Assert.That(s.Dialog.LastError, Is.Null); Assert.That(s.Dialog.LastResult!.Success, Is.True);
        Assert.That(s.Dialog.LastResult.RepositoryChanged, Is.False); Assert.That(await Materials(f), Is.EqualTo(before));
        Pointer(Field<Button>(success, "MessageDialogAccept")); await s.Modal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    public async Task AConfiguredRemoteWithoutCachedBranchesDoesNotReportAFictitiousSuccess()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.GitAsync("remote", "add", "origin", f.Output("unused.git")); string before = await Materials(f);
        await using Lease s = Lease.Open(f.Repository); Pointer(Field<Button>(s.Dialog, "button1")); Window error = await Message(s.Dialog);
        Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Is.EqualTo("No remote branches found."));
        Assert.That(s.Dialog.LastResult!.Table!.Rows, Is.Empty); Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
        Pointer(Field<Button>(error, "MessageDialogAccept")); await Until(s.Dialog, () => !s.Dialog.IsBusy);
        Assert.That(s.Dialog.IsVisible, Is.True); Assert.That(await Materials(f), Is.EqualTo(before));
        Press(s.Dialog, PhysicalKey.Escape); await s.Modal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    public async Task PartialGitFailuresKeepActualResultsAndRetryOnlyCreatesThePreviouslyFailedBranch()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        await f.GitAsync("update-ref", "refs/remotes/origin/a-good", "HEAD");
        await f.GitAsync("branch", "z-collision"); await f.GitAsync("update-ref", "refs/remotes/origin/z-collision/child", "HEAD");
        await using Lease s = Lease.Open(f.Repository); Pointer(Field<Button>(s.Dialog, "button1")); Window error = await Message(s.Dialog);
        PluginResult partial = s.Dialog.LastResult!;
        Assert.That(partial.Success, Is.False); Assert.That(partial.RepositoryChanged && s.Dialog.HasRepositoryChanges, Is.True);
        Assert.That(partial.Text, Is.EqualTo("1 tracking branch(es) created; 1 failed."));
        Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Does.StartWith(partial.Text).And.Contain("z-collision/child"));
        Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/a-good"), Is.True);
        Pointer(Field<Button>(error, "MessageDialogAccept")); await Until(s.Dialog, () => !s.Dialog.IsBusy);
        Assert.That(s.Dialog.IsVisible, Is.True); Assert.That(Field<TextBox>(s.Dialog, "_NO_TRANSLATE_Remote").Text, Is.EqualTo("origin"));
        await f.GitAsync("branch", "-D", "z-collision"); Pointer(Field<Button>(s.Dialog, "button1")); Window summary = await Message(s.Dialog);
        PluginResult retried = s.Dialog.LastResult!;
        Assert.That(retried.Success, Is.True); Assert.That(retried.Text, Is.EqualTo("1 tracking branch(es) created; 0 failed."));
        Assert.That(retried.Table!.Rows.Single(row => row.Key == "refs/remotes/origin/a-good").Cells[2], Is.EqualTo("Existing"));
        Assert.That((await f.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/z-collision/child").Upstream, Is.EqualTo("origin/z-collision/child"));
        Pointer(Field<Button>(summary, "MessageDialogAccept")); await s.Modal.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(s.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingDuringAnActualGitBranchCommandDrainsItsPidAndPreservesAnyAcquiredEffects(bool afterEffect)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The real process lifetime oracle requires Linux procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); await f.GitAsync("update-ref", "refs/remotes/origin/topic", "HEAD");
        string helper = f.Output("slow-tracking-git"), marker = helper + ".entered";
        string quoted = ShellQuote(marker);
        string hold = "printf '%s\\n' \"$$\" > " + quoted + "; while test ! -f " + quoted + ".release; do sleep 0.02; done\n";
        string script = "#!/bin/sh\ntrack=false\nfor arg in \"$@\"; do test \"$arg\" = --track && track=true; done\n";
        script += afterEffect
            ? "/usr/bin/git \"$@\"\ncode=$?\nif $track && test \"$code\" -eq 0; then " + hold + "fi\nexit \"$code\"\n"
            : "if $track; then " + hold + "fi\nexec /usr/bin/git \"$@\"\n";
        await File.WriteAllTextAsync(helper, script); File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using Lease s = Lease.Open(new GitRepository(f.Repository.Path, new GitRunner(helper)));
        try
        {
            Pointer(Field<Button>(s.Dialog, "button1")); await Until(s.Dialog, () => File.Exists(marker));
            string pid = File.ReadAllText(marker).Trim(), started = StartIdentity(pid)!; Assert.That(started, Is.Not.Null);
            Task pending = s.Dialog.WindowWorkCompletion;
            Assert.That(s.Dialog.IsBusy, Is.True); Assert.That(pending.IsCompleted, Is.False);
            Assert.That(Field<TextBox>(s.Dialog, "_NO_TRANSLATE_Remote").IsEnabled, Is.False);
            s.Dialog.Close(); await s.Modal.WaitAsync(TimeSpan.FromSeconds(10)); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(StartIdentity(pid), Is.Not.EqualTo(started), "The owned Git wrapper PID/start, including zombies, must be retired before the modal returns.");
            Assert.That(s.Dialog.IsVisible, Is.False); Assert.That(s.Owner.IsVisible, Is.True); Assert.That(s.Owner.OwnedWindows, Is.Empty);
            Assert.That(s.Dialog.LastResult, Is.Null, "An interrupted native Task cannot manufacture a completed plugin result.");
            Assert.That(s.Dialog.HasRepositoryChanges, Is.False); Assert.That(s.Dialog.MayHaveRepositoryChanges, Is.True);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/topic"), Is.EqualTo(afterEffect));
            for (int i = 0; i < 3; i++) { await Task.Yield(); Pump(s.Owner); }
            Assert.That(s.Owner.OwnedWindows, Is.Empty, "A canceled completion cannot open a late success/error modal.");
        }
        finally { await File.WriteAllTextAsync(marker + ".release", "release\n"); }
    }

    [AvaloniaTest]
    public async Task OwnerCloseDuringAnOwnedSuccessMessageDrainsItWithoutDiscardingTheActualMutation()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); await f.GitAsync("update-ref", "refs/remotes/origin/topic", "HEAD");
        await using Lease s = Lease.Open(f.Repository); Pointer(Field<Button>(s.Dialog, "button1")); Window summary = await Message(s.Dialog);
        Task pending = s.Dialog.WindowWorkCompletion;
        Assert.That(s.Dialog.HasRepositoryChanges, Is.True); Assert.That(pending.IsCompleted, Is.False);
        s.Owner.Close(); await s.Modal.WaitAsync(TimeSpan.FromSeconds(10)); await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(s.Owner, () => !s.Owner.IsVisible);
        Assert.That(summary.IsVisible || s.Dialog.IsVisible, Is.False); Assert.That(s.Owner.OwnedWindows, Is.Empty);
        Assert.That(s.Dialog.LastResult is { Success: true, RepositoryChanged: true }, Is.True); Assert.That(s.Dialog.HasRepositoryChanges, Is.True);
        Assert.That((await f.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/topic").Upstream, Is.EqualTo("origin/topic"));
    }

    [AvaloniaTest]
    public async Task TheRealPluginsMenuUsesTheSmallAllBranchesWindowAndPreservesLegacySettingsAndNeighbors()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        await f.GitAsync("update-ref", "refs/remotes/origin/wanted", "HEAD"); await f.GitAsync("update-ref", "refs/remotes/origin/topic", "HEAD");
        CreateLocalBranchesPlugin plugin = new(); PreferencesStore preferences = new();
        preferences.Save(new DesktopPreferences { CheckForUpdates = false, PluginSettings = new()
        {
            [plugin.Descriptor.Id.ToString()] = new() { ["remote"] = "legacy", ["branches"] = "origin/wanted", ["Future"] = "keep" },
            ["neighbor"] = new() { ["key"] = "untouched" }
        } });
        MainWindow main = new() { Width = 1200, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(main, () => !main.IsBusy);
            byte[] before = File.ReadAllBytes(preferences.FilePath);
            MenuItem plugins = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
            MenuItem entry = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == plugin.Descriptor.Name);
            Assert.That(entry.IsEnabled, Is.True); Assert.That(entry.Icon, Is.Not.Null);
            Pointer(plugins); Pointer(entry);
            await Until(main, () => main.OwnedWindows.OfType<CreateLocalBranchesDialog>().Any(window => window.IsVisible));
            CreateLocalBranchesDialog dialog = main.OwnedWindows.OfType<CreateLocalBranchesDialog>().Single();
            Assert.That(main.OwnedWindows.OfType<PluginDialog>(), Is.Empty); Assert.That(Field<TextBox>(dialog, "_NO_TRANSLATE_Remote").Text, Is.EqualTo("origin"));
            Pointer(Field<Button>(dialog, "button1")); Window summary = await Message(dialog);
            Assert.That(dialog.LastResult!.Text, Is.EqualTo("2 tracking branch(es) created; 0 failed."));
            Pointer(Field<Button>(summary, "MessageDialogAccept"));
            await Until(main, () => !main.OwnedWindows.Contains(dialog) && !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
            Assert.That(main.LastException, Is.Null); Assert.That(File.ReadAllBytes(preferences.FilePath), Is.EqualTo(before));
            IReadOnlyList<GitReference> refs = await f.Repository.GetReferencesAsync();
            Assert.That(refs.Where(reference => reference.IsBranch).Select(reference => reference.ShortName), Does.Contain("topic").And.Contain("wanted"));
            TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
            Assert.That(TreeNodes(tree.Items.OfType<TreeViewItem>()).SelectMany(node => (node.Header as StackPanel)?.Children.OfType<TextBlock>().Select(text => text.Text) ?? []), Does.Contain("topic"));
        }
        finally
        {
            foreach (Window child in main.OwnedWindows.ToArray()) { child.Close(); }
            main.Close(); await Until(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
            await main.CommitInformationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static IEnumerable<TreeViewItem> TreeNodes(IEnumerable<TreeViewItem> roots)
    { foreach (TreeViewItem root in roots) { yield return root; foreach (TreeViewItem child in TreeNodes(root.Items.OfType<TreeViewItem>())) { yield return child; } } }
    private static async Task<string> Materials(Fixture f)
        => (await f.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n" + string.Join('\n',
            new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(f.PathInRepository(path)))));
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string? StartIdentity(string pid)
    {
        try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pointer(Control target) => PushProcessTestDriver.Pointer(target);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); Pump(window); }
    private static void Edit(CreateLocalBranchesDialog dialog, string value)
    {
        Pointer(Field<TextBox>(dialog, "_NO_TRANSLATE_Remote")); Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Press(dialog, PhysicalKey.Backspace);
        if (value.Length > 0) { dialog.KeyTextInput(value); } Pump(dialog); Assert.That(Field<TextBox>(dialog, "_NO_TRANSLATE_Remote").Text, Is.EqualTo(value));
    }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(TopLevel root, Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) { Pump(root); await Task.Delay(10); }
        Pump(root);
        if (!condition() && root is Window failed && failed.IsVisible) { Capture(failed, "create-local-branches-timeout-" + Guid.NewGuid().ToString("N") + ".png"); }
        Assert.That(condition(), Is.True, "Actual dialog workflow timed out; top=" + root.Name + " visible=" + root.IsVisible
            + (root is CreateLocalBranchesDialog dialog ? " busy=" + dialog.IsBusy + " error=" + dialog.LastError + " exception=" + dialog.LastException : ""));
    }
    private static async Task<Window> Message(CreateLocalBranchesDialog dialog)
    {
        await Until(dialog, () => dialog.OwnedWindows.Any(window => window.Name == "MessageDialog" && window.IsVisible));
        Window message = dialog.OwnedWindows.Single(window => window.Name == "MessageDialog" && window.IsVisible); Pump(message); return message;
    }
    private static void Capture(Window dialog, string name, bool passive = false)
    {
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); using var frame = passive ? dialog.GetLastRenderedFrame() : dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Lease : IAsyncDisposable
    {
        internal Window Owner { get; }
        internal CreateLocalBranchesDialog Dialog { get; }
        internal Task Modal { get; }
        private Lease(GitRepository repository, bool dark)
        {
            Owner = new() { Width = 800, Height = 500, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Owner.Show(); Dialog = new(repository); Modal = Dialog.ShowDialog(Owner); Pump(Dialog);
        }
        internal static Lease Open(GitRepository repository, bool dark = false) => new(repository, dark);
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); }
            Dialog.Close(); await Until(Owner, () => !Dialog.IsVisible);
            await Dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(10)); await Modal.WaitAsync(TimeSpan.FromSeconds(10));
            Owner.Close(); Pump(Owner);
        }
    }
}
