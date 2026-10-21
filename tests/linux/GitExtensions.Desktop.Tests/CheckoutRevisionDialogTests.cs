using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;
using BrowserFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class CheckoutRevisionDialogTests
{
    [AvaloniaTest]
    public Task OriginalCompactChromeAndCommitCountEnglishLight() => ChromeAsync("en", ThemeVariant.Light);
    [AvaloniaTest]
    public Task OriginalCompactChromeAndCommitCountFrenchDark() => ChromeAsync("fr", ThemeVariant.Dark);
    private static async Task ChromeAsync(string culture, ThemeVariant theme)
    {
        using Fixture f = await Fixture.CreateAsync(); string initial = await f.Repository.ResolveRevisionAsync("HEAD"); await f.ChangeAsync("next\n", "next");
        CultureInfo original = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CheckoutRevisionDialog dialog = new(f.Repository, (_, _, _) => throw new InvalidOperationException("Rendering must not execute checkout"), initial) { RequestedThemeVariant = theme };
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Pump(dialog);
            Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.SelectedRevision, Is.EqualTo(initial)); Assert.That(dialog.Force, Is.False);
            Assert.That(Field<TextBox>(dialog, "CheckoutRevision").Text, Is.EqualTo(initial[..8]));
            Assert.That(dialog.ClientSize.Width, Is.EqualTo(481).Within(1)); Assert.That(dialog.ClientSize.Height, Is.EqualTo(131).Within(1));
            Assert.That(Field<TextBlock>(dialog, "CheckoutRevisionCommitCount").Text, Is.EqualTo("(+1-0)"));
            Button checkout = Field<Button>(dialog, "ExecuteCheckoutRevision"); Assert.That(checkout.IsDefault, Is.True);
            Assert.That(checkout.GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("checkout.png"));
            Assert.That(Field<Button>(dialog, "ChooseCheckoutRevisionCommit").Bounds.Width, Is.EqualTo(25));
            Assert.That(Field<TextBox>(dialog, "CheckoutRevision").Bounds.Width, Is.GreaterThan(100));
            Capture(dialog, "classic-checkout-revision-" + culture + "-" + theme.Key + ".png");
        }
        finally { dialog.Close(); CultureInfo.CurrentUICulture = original; }
    }

    [AvaloniaTest]
    public async Task EmptyZeroAndUnknownRevisionStayOpenAndNeverReachTheExecutor()
    {
        using Fixture f = await Fixture.CreateAsync(); string head = await f.Repository.ResolveRevisionAsync("HEAD"); int calls = 0;
        CheckoutRevisionDialog dialog = new(f.Repository, (_, _, _) => { calls++; return Task.FromResult(new CheckoutRevisionOutcome(null, true, head, head, false, false)); }, head); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            foreach (string input in new[] { "", new string('0', 40), new string('0', 64), "missing-revision" })
            {
                Field<TextBox>(dialog, "CheckoutRevision").Text = input; await dialog.ExecuteAsync();
                Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty); Assert.That(dialog.LastNativeResult, Is.Null);
                Assert.That(calls, Is.Zero); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RealCommitPickerCanReplaceThePreselectedRevisionWithoutCheckingOut()
    {
        using Fixture f = await Fixture.CreateAsync(); string older = await f.Repository.ResolveRevisionAsync("HEAD"); await f.ChangeAsync("new\n", "new"); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        CheckoutRevisionDialog dialog = new(f.Repository, (_, _, _) => throw new InvalidOperationException("Picker must not execute"), head); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "ChooseCheckoutRevisionCommit");
            await Until(() => dialog.OwnedWindows.OfType<RevertRevisionPicker>().Any(window => window.IsVisible && !window.IsBusy
                && Field<ListBox>(window, "RevertChooseRevision").Items.OfType<GitCommit>().Any(commit => commit.Hash == older)
                && Field<Button>(window, "ChooseRevertCommit").IsEffectivelyEnabled), dialog);
            RevertRevisionPicker picker = dialog.OwnedWindows.OfType<RevertRevisionPicker>().Single();
            ListBox choices = Field<ListBox>(picker, "RevertChooseRevision");
            choices.SelectedItem = choices.Items.OfType<GitCommit>().Single(commit => commit.Hash == older); Click(picker, "ChooseRevertCommit");
            await Until(() => !dialog.IsBusy, dialog);
            Assert.That(dialog.SelectedRevision, Is.EqualTo(older)); Assert.That(Field<TextBox>(dialog, "CheckoutRevision").Text, Is.EqualTo(older[..8]));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head)); Assert.That(dialog.LastNativeResult, Is.Null);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CoreCompatibleDefaultRefusesDirtyChangesAndExplicitForceResetsOnlyThisAttempt()
    {
        using Fixture f = await Fixture.CreateAsync(); string first = await f.Repository.ResolveRevisionAsync("HEAD"); await f.ChangeAsync("second\n", "second"); string second = await f.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(f.File, "dirty\n"); ReferencePreferences preferences = new ReferencePreferencesStore().Load();
        GitResult refused = await f.Repository.CheckoutRevisionAttemptAsync(first);
        Assert.That(refused.Success, Is.False); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(second)); Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("dirty\n"));
        GitResult forced = await f.Repository.CheckoutRevisionAttemptAsync(first, force: true);
        Assert.That(forced.Success, Is.True, forced.StandardError); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(first));
        Assert.That((await f.Repository.GetStatusAsync()).IsDetached, Is.True); Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("base\n"));
        Assert.That(new ReferencePreferencesStore().Load(), Is.EqualTo(preferences));
    }

    [AvaloniaTest]
    public async Task NativeFalseRunsAfterAndTheVisibleForceRetrySucceedsWithAnotherBeforeAfterPair()
    {
        using Fixture f = await Fixture.CreateAsync(); string first = await f.Repository.ResolveRevisionAsync("HEAD"); await f.ChangeAsync("second\n", "second"); await File.WriteAllTextAsync(f.File, "dirty\n");
        Configure(f, "", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, first);
        try
        {
            Task refused = dialog.ExecuteAsync(); await Finish(refused, dialog);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastNativeResult?.Success, Is.False); Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>());
            Assert.That(Log(f), Is.EqualTo(new[] { "before", "after" })); Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("dirty\n"));
            Field<CheckBox>(dialog, "ForceCheckoutRevision").IsChecked = true; Task forced = dialog.ExecuteAsync(); await Finish(forced, dialog);
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.Outcome!.NativeResult!.Success, Is.True); Assert.That(dialog.IsVisible, Is.False);
            Assert.That(Log(f), Is.EqualTo(new[] { "before", "after", "before", "after" })); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(first));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task BeforeVetoPreservesItsOwnMutationWithoutCheckoutAfterOrSubmodulePrompt()
    {
        using Fixture f = await Fixture.CreateAsync(); string first = await f.Repository.ResolveRevisionAsync("HEAD"); await f.ChangeAsync("second\n", "second"); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        Configure(f, "git update-ref refs/heads/script-effect HEAD\nexit 7", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, first);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Finish(execution, dialog);
            Assert.That(dialog.Outcome!.BeforeVetoed, Is.True); Assert.That(dialog.LastNativeResult, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head)); Assert.That(await f.Repository.ResolveRevisionAsync("script-effect"), Is.EqualTo(head));
            Assert.That(Log(f), Is.EqualTo(new[] { "before" })); Assert.That(dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>(), Is.Empty);
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task AFalseAfterScriptDoesNotRelabelTheSuccessfulNativeCheckoutAsFailure()
    {
        using Fixture f = await Fixture.CreateAsync(); string first = await f.Repository.ResolveRevisionAsync("HEAD"); await f.ChangeAsync("second\n", "second");
        Configure(f, "", "exit 9"); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, first);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Finish(execution, dialog);
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.LastNativeResult!.Success, Is.True); Assert.That(Log(f), Is.EqualTo(new[] { "before", "after" }));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(first));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task LaunchFailureAfterBeforeIsNotACompletedFalseAndNeverRunsAfter()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux native launcher fixture."); return; }
        using Fixture f = await Fixture.CreateAsync(); string wrapper = await WrapperAsync(f, ""); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        Configure(f, "mv " + Quote(wrapper) + " " + Quote(wrapper + ".off"), ""); GitRepository repository = new(f.Repository.Path, new GitRunner(wrapper)); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(repository, host, head);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Finish(execution, dialog);
            Assert.That(dialog.LastException, Is.TypeOf<System.ComponentModel.Win32Exception>()); Assert.That(dialog.LastNativeResult, Is.Null); Assert.That(Log(f), Is.EqualTo(new[] { "before" }));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingDuringBeforeCancelsAndDrainsTheNativeScriptWithoutCheckoutOrAfter()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux native process fixture."); return; }
        using Fixture f = await Fixture.CreateAsync(); string head = await f.Repository.ResolveRevisionAsync("HEAD"); string ready = f.Output("ready"), pid = f.Output("pid");
        Configure(f, "printf '%s' $$ > " + Quote(pid) + "\nprintf ready > " + Quote(ready) + "\nsleep 30", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, head);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Until(() => File.Exists(ready), dialog); int native = int.Parse(await File.ReadAllTextAsync(pid), CultureInfo.InvariantCulture);
            dialog.Close(); await Finish(execution, dialog); Assert.That(dialog.IsVisible || dialog.IsBusy, Is.False); AssertExited(native);
            Assert.That(Log(f), Is.EqualTo(new[] { "before" })); Assert.That(dialog.LastNativeResult, Is.Null); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingDuringNativeCheckoutDrainsTheActualChildAndDoesNotInventAfter()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux native process fixture."); return; }
        using Fixture f = await Fixture.CreateAsync(); string head = await f.Repository.ResolveRevisionAsync("HEAD"); string ready = f.Output("ready"), pid = f.Output("pid");
        string wrapper = await WrapperAsync(f, "for arg in \"$@\"; do\n if test \"$arg\" = switch; then printf '%s' $$ > " + Quote(pid) + "; printf ready > " + Quote(ready) + "; sleep 30; fi\ndone\n");
        Configure(f, "", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(new(f.Repository.Path, new GitRunner(wrapper)), host, head);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Until(() => File.Exists(ready), dialog); int native = int.Parse(await File.ReadAllTextAsync(pid), CultureInfo.InvariantCulture);
            dialog.Close(); await Finish(execution, dialog); AssertExited(native); Assert.That(dialog.LastNativeResult, Is.Null); Assert.That(Log(f), Is.EqualTo(new[] { "before" }));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task SubmodulesAreProposedAfterSuccessAndRealHeadChangeButNeverForSameHead()
    {
        using Fixture f = await Fixture.CreateAsync(); ModuleState module = await ModulesAsync(f); Configure(f, "", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, module.NewParent);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any(), dialog);
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(module.NewParent)); Assert.That(Log(f), Is.EqualTo(new[] { "before" }));
            Click(dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single(), "SkipCheckoutSubmodules"); await Finish(execution, dialog);
            Assert.That(dialog.Succeeded, Is.True); Assert.That(dialog.Outcome!.SubmodulesUpdated, Is.False); Assert.That(await module.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(module.OldHead));
            CheckoutRevisionDialog same = await OpenAsync(f.Repository, host, module.NewParent);
            try { Task unchanged = same.ExecuteAsync(); await Finish(unchanged, same); Assert.That(same.Succeeded, Is.True); Assert.That(same.Outcome!.HeadBefore, Is.EqualTo(same.Outcome!.HeadAfter)); }
            finally { same.Close(); }
            Assert.That(Log(f), Is.EqualTo(new[] { "before", "after", "before", "after" }));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerDuringItsSubmodulePromptDrainsTheChildAndPreservesCompletedCheckoutWithoutAfter()
    {
        using Fixture f = await Fixture.CreateAsync(); ModuleState module = await ModulesAsync(f); Configure(f, "", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, module.NewParent);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any(), dialog);
            CheckoutSubmodulePrompt child = dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single(); dialog.Close(); await Finish(execution, dialog);
            Assert.That(dialog.IsVisible || dialog.IsBusy || child.IsVisible, Is.False); Assert.That(Log(f), Is.EqualTo(new[] { "before" }));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(module.NewParent)); Assert.That(await module.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(module.OldHead));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task UpdatingSubmodulesUsesTheExistingChildWorkflowBeforeTheOneAfterEvent()
    {
        using Fixture f = await Fixture.CreateAsync(); ModuleState module = await ModulesAsync(f); Configure(f, "", ""); MainWindow host = new(); host.Show(); CheckoutRevisionDialog dialog = await OpenAsync(f.Repository, host, module.NewParent);
        try
        {
            Task execution = dialog.ExecuteAsync(); await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any(), dialog);
            Click(dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single(), "UpdateCheckoutSubmodules"); await Finish(execution, dialog);
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(dialog.Outcome!.SubmodulesUpdated, Is.True); Assert.That(await module.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(module.NewHead));
            Assert.That(Log(f), Is.EqualTo(new[] { "before", "after" }));
        }
        finally { dialog.Close(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task RealRevisionMenuOpensTheDedicatedDialogAndCancelRestoresFirstClickAndArrowFocus()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "base\n", createMerge: true); MainWindow main = f.Window;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit[] real = history.Items.OfType<GitCommit>().Where(commit => CheckoutRevisionRequest.IsActualCommit(commit.Hash)).ToArray();
        GitCommit selected = real[0]; history.SelectedItem = selected; Pump(main);
        ContextMenu menu = history.ContextMenu!; menu.Open(history); Pump(main);
        MenuItem checkout = menu.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCheckout"); checkout.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<CheckoutRevisionDialog>().Any(d => !d.IsBusy), main);
        CheckoutRevisionDialog dialog = main.OwnedWindows.OfType<CheckoutRevisionDialog>().Single(); Assert.That(dialog.SelectedRevision, Is.EqualTo(selected.Hash)); dialog.Close();
        await Until(() => !main.IsBusy, main); Pump(main); Assert.That(history.SelectedItem, Is.SameAs(selected));
        ListBoxItem other = history.GetVisualDescendants().OfType<ListBoxItem>().Single(row => ReferenceEquals(row.DataContext, real[1]));
        GitCommit target = (GitCommit)other.DataContext!; Point point = other.TranslatePoint(new Point(100, other.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left); Pump(main); Assert.That(history.SelectedItem, Is.SameAs(target)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        main.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); Pump(main);
        main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); Pump(main); Assert.That(history.SelectedItem, Is.SameAs(target));
    }

    [AvaloniaTest]
    public async Task CompatibleBrowserEntryOpensTheSamePreselectedDialogWithoutExecutingCheckout()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "base\n", createMerge: true); MainWindow main = f.Window;
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit selected = history.Items.OfType<GitCommit>().Where(commit => CheckoutRevisionRequest.IsActualCommit(commit.Hash)).Skip(1).First();
        GitRepository repository = new(f.Repository); history.SelectedItem = selected; string before = await repository.ResolveRevisionAsync("HEAD");
        Task entry = (Task)typeof(MainWindow).GetMethod("CheckoutCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!;
        await Until(() => main.OwnedWindows.OfType<CheckoutRevisionDialog>().Any(d => !d.IsBusy), main);
        CheckoutRevisionDialog dialog = main.OwnedWindows.OfType<CheckoutRevisionDialog>().Single();
        Assert.That(dialog.SelectedRevision, Is.EqualTo(selected.Hash)); Assert.That(dialog.Force, Is.False); Assert.That(dialog.LastNativeResult, Is.Null);
        Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before)); dialog.Close(); await Finish(entry, main);
        Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
    }

    private static CheckoutRevisionExecutor Executor(MainWindow host, GitRepository repository) => (owner, request, token) =>
        (Task<CheckoutRevisionOutcome>)typeof(MainWindow).GetMethod("CheckoutRevisionWithScriptEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [repository, request.Revision, token, request.Force, owner])!;
    private static async Task<CheckoutRevisionDialog> OpenAsync(GitRepository repository, MainWindow host, string revision)
    { CheckoutRevisionDialog dialog = new(repository, Executor(host, repository), revision); dialog.Show(); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog; }
    private static void Configure(Fixture f, string before, string after)
    {
        new ProcessDialogPreferencesStore().Save(new(true)); List<ScriptDefinition> definitions = [];
        foreach ((ScriptEvent trigger, string name, string extra) in new[] { (ScriptEvent.BeforeCheckout, "before", before), (ScriptEvent.AfterCheckout, "after", after) })
        {
            string file = f.Output(name + ".sh"); File.WriteAllText(file, "#!/bin/sh\nset -eu\nprintf '%s\\n' " + Quote(name) + " >> " + Quote(f.Output("events.log")) + "\n" + extra + "\n");
            definitions.Add(new() { Name = name, Command = "/bin/sh", Arguments = "\"" + file + "\"", OnEvent = trigger });
        }
        new ScriptsPreferencesStore().Save(definitions);
    }
    private static string[] Log(Fixture f) => File.Exists(f.Output("events.log")) ? File.ReadAllLines(f.Output("events.log")) : [];
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task<string> WrapperAsync(Fixture f, string before)
    {
        string file = f.Output("git-wrapper.sh"); await File.WriteAllTextAsync(file, "#!/bin/sh\nset -eu\n" + before + "\nexec /usr/bin/git \"$@\"\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return file;
    }
    private sealed record ModuleState(GitRepository Repository, string OldHead, string NewHead, string NewParent);
    private static async Task<ModuleState> ModulesAsync(Fixture f)
    {
        f.SetEnvironment("GIT_ALLOW_PROTOCOL", "file"); string source = f.Output("module-source"); GitRepository module = await GitRepository.InitAsync(source);
        await module.SetConfigAsync("user.name", "Module Tester"); await module.SetConfigAsync("user.email", "module@example.invalid"); await module.SetConfigAsync("commit.gpgsign", "false");
        string file = Path.Combine(source, "file.txt"); await File.WriteAllTextAsync(file, "old\n"); await module.StageAllAsync(); await module.CommitAsync("old module"); string oldHead = await module.ResolveRevisionAsync("HEAD");
        await f.Repository.RunAsync(["submodule", "add", "--", source, "module"]); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("module base"); string oldParent = await f.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(file, "new\n"); await module.StageAllAsync(); await module.CommitAsync("new module"); string newHead = await module.ResolveRevisionAsync("HEAD");
        GitRepository child = new(f.PathInRepository("module")); await child.FetchAsync("origin", prune: false); await child.CheckoutRevisionAttemptAsync(newHead);
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("module target"); string target = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.CheckoutRevisionAttemptAsync(oldParent); await child.CheckoutRevisionAttemptAsync(oldHead);
        return new(child, oldHead, newHead, target);
    }
    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Window owner, string name) => Field<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<Window> Descendants(Window owner)
    { foreach (Window child in owner.OwnedWindows.ToArray()) { yield return child; foreach (Window nested in Descendants(child)) { yield return nested; } } }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task Until(Func<bool> predicate, Window owner)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(20);
        while (!predicate() && DateTime.UtcNow < until)
        {
            Pump(owner); foreach (Window child in Descendants(owner).ToArray())
            { Pump(child); if (child is GitProcessDialog { IsRunning: false } process && process.IsVisible) { Click(process, "Ok"); } }
            await Task.Delay(10);
        }
        Assert.That(predicate(), Is.True, "The owned checkout workflow did not complete/drain.");
    }
    private static async Task Finish(Task task, Window owner) { await Until(() => task.IsCompleted, owner); await task; }
    private static void AssertExited(int pid) { try { using Process process = Process.GetProcessById(pid); Assert.That(process.HasExited, Is.True); } catch (ArgumentException) { } }
    private static void Capture(Window window, string name)
    {
        Pump(window); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}
