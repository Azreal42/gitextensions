using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using BrowserFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SimplePluginSettingsIntegrationTests
{
    [AvaloniaTest]
    public async Task StatisticsInvocationUsesAppliedXmlButItsTemporaryControlsAndCloseCannotWriteDefaults()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("line of text\n", "line of text\n");
        DesktopSettingsDialog settings = await Settings(f.Window); SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.Statistics;
        SettingsNavigationTestDriver.Select(settings, entry.PageId); Pointer(Field<RadioButton>(settings, "StatisticsGlobalRB"));
        Edit(settings, "_codeFiles", "*.txt"); Pointer(Field<Button>(settings, "ApplyDesktopSettings")); await Saved(settings);
        Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Until(f.Window, () => !settings.IsVisible);
        byte[] xml = File.ReadAllBytes(DetailedRepositorySettings.GlobalPath), json = File.ReadAllBytes(new PreferencesStore().FilePath);
        StatisticsDialog dialog = await Plugin<StatisticsDialog>(f.Window, "Statistics");
        Assert.That(dialog.CurrentOptions.CodeFiles, Is.EqualTo("*.txt")); Assert.That(dialog.LastReport!.Lines.Code, Is.EqualTo(1));
        Field<Expander>(dialog, "StatisticsSettings").IsExpanded = true; Pump(dialog); Edit(dialog, "StatisticsCodeFiles", "*.none");
        Pointer(Field<Button>(dialog, "RefreshStatistics")); await Until(dialog, () => !dialog.IsBusy);
        Assert.That(dialog.LastReport!.Lines.Code, Is.Zero); Pointer(Field<Button>(dialog, "CloseStatistics")); await Until(f.Window, () => !dialog.IsVisible && !f.Window.IsBusy);
        Assert.That(File.ReadAllBytes(DetailedRepositorySettings.GlobalPath), Is.EqualTo(xml)); Assert.That(File.ReadAllBytes(new PreferencesStore().FilePath), Is.EqualTo(json));
        StatisticsDialog reopened = await Plugin<StatisticsDialog>(f.Window, "Statistics");
        Assert.That(reopened.CurrentOptions.CodeFiles, Is.EqualTo("*.txt")); Assert.That(reopened.LastReport!.Lines.Code, Is.EqualTo(1));
        reopened.Close(); await Until(f.Window, () => !reopened.IsVisible && !f.Window.IsBusy);
    }

    [AvaloniaTest]
    public async Task ManualAutoCompileWorksWhenDisabledButApplyAndThePostUpdateHookDoNotBuild()
    {
        RequireLinux(); using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        using LocalTool tool = new(f.Repository); File.WriteAllText(Path.Combine(f.Repository, "a.sln"), "fixture"); File.WriteAllText(Path.Combine(f.Repository, "b.sln"), "fixture");
        GitRepository repository = Repository(f.Window); SimplePluginSettingsSession session = await AutoCompile(repository, tool.Path);
        object service = typeof(MainWindow).GetField("_autoCompile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window)!;
        Task automatic = (Task)typeof(MainWindow).GetMethod("AfterSubmoduleUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Window, [repository, f.Window, CancellationToken.None])!;
        await automatic.WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(tool.Runs(), Is.Empty); Assert.That(f.Window.OwnedWindows, Is.Empty);
        DesktopSettingsDialog settings = await Settings(f.Window); SettingsNavigationTestDriver.Select(settings, SimplePluginSettingsCatalog.AutoCompile.PageId);
        Pointer(Field<RadioButton>(settings, "AutoCompileSubmodulesLocalRB")); Assert.That(Field<CheckBox>(settings, "_msBuildEnabled").IsChecked, Is.False);
        Edit(settings, "_msBuildArguments", "--applied"); Pointer(Field<Button>(settings, "ApplyDesktopSettings")); await Saved(settings);
        Assert.That(tool.Runs(), Is.Empty); Assert.That(settings.OwnedWindows, Is.Empty, "Apply must not execute the configured compiler.");
        Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Until(f.Window, () => !settings.IsVisible);
        ClickPlugin(f.Window, "Auto compile submodules"); Window first = await BuildPrompt(f.Window);
        Assert.That(first.Owner, Is.SameAs(f.Window)); Assert.That(f.Window.IsBusy, Is.True);
        Assert.That(first.CanMinimize || first.CanMaximize || first.ShowInTaskbar, Is.False);
        Assert.That(first.ActualThemeVariant, Is.EqualTo(f.Window.ActualThemeVariant));
        Pointer(Field<Button>(first, "AutoCompileBuild"));
        await Until(f.Window, () => f.Window.OwnedWindows.Any(window => window.Name == "AutoCompileBuildConfirmation" && !ReferenceEquals(window, first)));
        Window second = f.Window.OwnedWindows.Single(window => window.Name == "AutoCompileBuildConfirmation" && !ReferenceEquals(window, first));
        Pointer(Field<Button>(second, "AutoCompileSkip")); await Until(f.Window, () => !f.Window.IsBusy && !f.Window.OwnedWindows.Any(window => window.IsVisible));
        await f.Window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(tool.Runs(), Has.Count.EqualTo(1)); Assert.That(tool.Runs()[0].Arguments.Skip(1), Is.EqualTo(new[] { "--applied" }));
        Assert.That(Path.GetFileName(tool.Runs()[0].Arguments[0]), Is.EqualTo("b.sln"));
        ClickPlugin(f.Window, "Auto compile submodules"); Window cancelled = await BuildPrompt(f.Window); Pointer(Field<Button>(cancelled, "AutoCompileCancel"));
        await Until(f.Window, () => !f.Window.IsBusy && !f.Window.OwnedWindows.Any(window => window.IsVisible));
        Assert.That(tool.Runs(), Has.Count.EqualTo(1)); Assert.That(f.Window.LastException, Is.Null);
        Assert.That(typeof(MainWindow).GetField("_autoCompile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window), Is.SameAs(service));
        SimplePluginSettingsSession fresh = new(SimplePluginSettingsCatalog.AutoCompile, repository); await fresh.InitializeAsync(default);
        Assert.That(fresh.AutoCompileOptions().Enabled, Is.False); Assert.That(session.AutoCompileOptions().Enabled, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AnActualOwnedAutoCompileScriptStopsOnItsTokenOrOwnerWithoutAnOrphanPrompt(bool closeOwner)
    {
        RequireLinux(); using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "change\n"); using LocalTool tool = new(f.Repository);
        File.WriteAllText(Path.Combine(f.Repository, "one.sln"), "fixture"); GitRepository repository = Repository(f.Window); await AutoCompile(repository, tool.Path);
        const int scriptId = 9128;
        ScriptDefinition script = new() { Name = "Manual build owner", Command = "{plugin.Auto compile submodules}", HotkeyCommandIdentifier = scriptId };
        if (closeOwner)
        {
            new ScriptsPreferencesStore().Save([script]); // Register the actual command before binding it.
            HotkeyPreferencesStore hotkeys = new(); HotkeyBindings bindings = hotkeys.Load();
            bindings.Set("Scripts", scriptId, new(Key.F12, KeyModifiers.Control | KeyModifiers.Alt)); hotkeys.Save(bindings);
        }
        CommitDialog commit = new(repository) { RequestedThemeVariant = ThemeVariant.Dark }; Task closed = commit.ShowDialog(f.Window);
        using CancellationTokenSource cancellation = new(); Task<bool>? execution = null;
        try
        {
            await commit.InitializeAsync(); await Until(f.Window, () => !commit.IsBusy);
            if (closeOwner)
            {
                commit.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
                commit.KeyReleaseQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
            }
            else { execution = DesktopScriptExecutor.ExecuteAsync(commit, repository, script, cancellation.Token); }
            Window prompt = await BuildPrompt(commit); Assert.That(prompt.Owner, Is.SameAs(commit));
            Assert.That(prompt.CanMinimize || prompt.CanMaximize || prompt.ShowInTaskbar, Is.False);
            Assert.That(prompt.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            if (closeOwner)
            {
                Assert.That(commit.IsBusy, Is.True, "The physical script hotkey must exercise the real busy-owner close/drain path.");
                CancellationToken owned = (CancellationToken)typeof(CommitDialog).GetProperty("OperationToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(commit)!;
                commit.Close();
                await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy && !prompt.IsVisible);
                Assert.That(owned.IsCancellationRequested, Is.True);
            }
            else
            {
                cancellation.Cancel(); await Until(f.Window, () => execution!.IsCompleted && !prompt.IsVisible);
                try { _ = await execution!.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("The stopped token cannot become a successful script continuation."); }
                catch (OperationCanceledException) { }
            }
            await f.Window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); Pump(f.Window);
            Assert.That(commit.IsVisible, Is.EqualTo(!closeOwner)); Assert.That(commit.OwnedWindows.Any(window => window.IsVisible), Is.False);
            Assert.That(tool.Runs(), Is.Empty); Assert.That(f.Window.IsVisible, Is.True);
            Assert.That(f.Window.OwnedWindows.SelectMany(window => window.OwnedWindows).Any(window => window.Name == "AutoCompileBuildConfirmation"), Is.False);
        }
        finally
        {
            cancellation.Cancel(); foreach (Window child in commit.OwnedWindows.ToArray()) { child.Close(); }
            if (execution is not null) { await Until(f.Window, () => execution.IsCompleted); try { await execution; } catch (OperationCanceledException) { } }
            if (commit.IsVisible) { commit.Close(); } await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    [AvaloniaTest]
    public async Task GourceAcceptedLaunchSurvivesPartialXmlFailureAndRetryWritesOnlyWithoutASecondLaunch()
    {
        RequireLinux(); using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n"); using LocalTool tool = new(f.Repository);
        SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.Gource; GitRepository repository = Repository(f.Window);
        SimplePluginSettingsSession seed = new(entry, repository); await seed.InitializeAsync(default);
        seed.Set(GitDistributedSettingsScope.Local, entry.Definition.Find("_gourcePath"), "/old/not-executable");
        seed.Set(GitDistributedSettingsScope.Global, entry.Definition.Find("_gourceArguments"), "--before"); await seed.SaveFilesAsync(default);
        OptionalFileSnapshot json = OptionalFileSnapshot.Capture(new PreferencesStore().FilePath);
        GourceDialog dialog = await Plugin<GourceDialog>(f.Window, "Gource"); Edit(dialog, "GourcePath", tool.Path); Edit(dialog, "Arguments", "--accepted");
        using DetailedSettingsTestScope.BlockedFile block = new(DetailedRepositorySettings.GlobalPath);
        Pointer(Field<Button>(dialog, "StartGource")); await Until(dialog, () => !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastLaunch, Is.Not.Null);
        await Until(dialog, () => tool.Runs().Count == 1); int pid = dialog.LastLaunch!.ProcessId;
        string localPath = Path.Combine((await repository.GetInfoAsync()).CommonDirectory, GitDistributedSettingsSnapshot.FileName);
        GitXmlSettingsSnapshot local = await GitXmlSettingsStore.LoadAsync(localPath);
        Assert.That(local.Values[entry.Definition.Key(entry.Definition.Find("_gourcePath"))], Is.EqualTo(tool.Path));
        byte[] acceptedLocal = File.ReadAllBytes(localPath);
        Assert.That(Field<TextBox>(dialog, "GourcePath").IsReadOnly, Is.True);
        Assert.That(Field<Button>(dialog, "GourceBrowse").IsEnabled, Is.False);
        block.Restore(); Pointer(Field<Button>(dialog, "StartGource"));
        await Until(f.Window, () => !dialog.IsVisible && !f.Window.IsBusy);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        Assert.That(dialog.LastLaunch!.ProcessId, Is.EqualTo(pid)); Assert.That(tool.Runs(), Has.Count.EqualTo(1), "Retry must not launch a second accepted command.");
        Assert.That(File.ReadAllBytes(localPath), Is.EqualTo(acceptedLocal));
        GitXmlSettingsSnapshot global = await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath);
        Assert.That(global.Values[entry.Definition.Key(entry.Definition.Find("_gourceArguments"))], Is.EqualTo("--accepted"));
        json.AssertUnchanged();
    }

    [AvaloniaTest]
    public async Task GourceCancelCannotPersistEditedPathArgumentsOrWorkingDirectory()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        string global = DetailedRepositorySettings.GlobalPath; byte[]? xml = File.Exists(global) ? File.ReadAllBytes(global) : null;
        OptionalFileSnapshot json = OptionalFileSnapshot.Capture(new PreferencesStore().FilePath); GourceDialog dialog = await Plugin<GourceDialog>(f.Window, "Gource");
        Edit(dialog, "GourcePath", "/unsaved/gource"); Edit(dialog, "Arguments", "--unsaved"); Edit(dialog, "WorkingDir", "/unsaved/invocation"); dialog.Close();
        await Until(f.Window, () => !dialog.IsVisible && !f.Window.IsBusy);
        Assert.That(File.Exists(global), Is.EqualTo(xml is not null)); if (xml is not null) { Assert.That(File.ReadAllBytes(global), Is.EqualTo(xml)); }
        json.AssertUnchanged();
        GourceDialog reopened = await Plugin<GourceDialog>(f.Window, "Gource"); Assert.That(reopened.CurrentOptions.Executable, Is.Empty);
        Assert.That(reopened.CurrentOptions.WorkingDirectory, Is.EqualTo(f.Repository)); Assert.That(reopened.CurrentOptions.Arguments, Is.EqualTo(new GourceOptions().Arguments));
        reopened.Close(); await Until(f.Window, () => !reopened.IsVisible && !f.Window.IsBusy);
    }

    [AvaloniaTest]
    public async Task ARealFindMenuShowsTheOwnedWarningForAnUnsupportedParsedValueAndNeverOpensOrWritesTheScanner()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n"); await UnsupportedFind(Repository(f.Window));
        byte[] xml = File.ReadAllBytes(DetailedRepositorySettings.GlobalPath); OptionalFileSnapshot json = OptionalFileSnapshot.Capture(new PreferencesStore().FilePath);
        ClickPlugin(f.Window, "Find large files"); Window message = await Message(f.Window);
        Assert.That(message.Owner, Is.SameAs(f.Window));
        Assert.That(Field<TextBlock>(message, "MessageDialogText").Text, Is.EqualTo(SimplePluginSettingsSession.UnsupportedThresholdMessage));
        Assert.That(message.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null), Is.True);
        Assert.That(f.Window.OwnedWindows.OfType<FindLargeFilesDialog>().Any(), Is.False);
        Pointer(Field<Button>(message, "MessageDialogAccept")); await Until(f.Window, () => !message.IsVisible);
        await f.Window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(f.Window.OwnedWindows.OfType<FindLargeFilesDialog>().Any(), Is.False);
        Assert.That(File.ReadAllBytes(DetailedRepositorySettings.GlobalPath), Is.EqualTo(xml)); json.AssertUnchanged();
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task FindWarningStopsAnActualOwnedScriptBeforeItsTokenOrBusyOwnerCanContinue(bool closeOwner)
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "change\n"); GitRepository repository = Repository(f.Window); await UnsupportedFind(repository);
        const int scriptId = 9129;
        ScriptDefinition script = new() { Name = "Find unsupported owner", Command = "{plugin.Find large files}", HotkeyCommandIdentifier = scriptId };
        if (closeOwner)
        {
            new ScriptsPreferencesStore().Save([script]); HotkeyPreferencesStore store = new(); HotkeyBindings bindings = store.Load();
            bindings.Set("Scripts", scriptId, new(Key.F12, KeyModifiers.Control | KeyModifiers.Alt)); store.Save(bindings);
        }
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(f.Window);
        using CancellationTokenSource cancellation = new(); Task<bool>? execution = null;
        try
        {
            await commit.InitializeAsync(); await Until(f.Window, () => !commit.IsBusy);
            byte[] xml = File.ReadAllBytes(DetailedRepositorySettings.GlobalPath); OptionalFileSnapshot json = OptionalFileSnapshot.Capture(new PreferencesStore().FilePath);
            if (closeOwner)
            {
                commit.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
                commit.KeyReleaseQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt);
            }
            else { execution = DesktopScriptExecutor.ExecuteAsync(commit, repository, script, cancellation.Token); }
            Window message = await Message(commit); Assert.That(message.Owner, Is.SameAs(commit));
            Assert.That(Field<TextBlock>(message, "MessageDialogText").Text, Is.EqualTo(SimplePluginSettingsSession.UnsupportedThresholdMessage));
            if (closeOwner)
            {
                Assert.That(commit.IsBusy, Is.True); commit.Close();
                await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy && !message.IsVisible);
            }
            else
            {
                cancellation.Cancel(); await Until(f.Window, () => execution!.IsCompleted && !message.IsVisible);
                try { _ = await execution!.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("The cancelled warning cannot resume the owned script."); }
                catch (OperationCanceledException) { }
            }
            await f.Window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); Pump(f.Window);
            Assert.That(commit.IsVisible, Is.EqualTo(!closeOwner)); Assert.That(commit.OwnedWindows.Any(window => window.IsVisible), Is.False);
            Assert.That(f.Window.OwnedWindows.OfType<FindLargeFilesDialog>().Any(), Is.False);
            Assert.That(File.ReadAllBytes(DetailedRepositorySettings.GlobalPath), Is.EqualTo(xml)); json.AssertUnchanged();
        }
        finally
        {
            cancellation.Cancel(); foreach (Window child in commit.OwnedWindows.ToArray()) { child.Close(); }
            if (execution is not null) { await Until(f.Window, () => execution.IsCompleted); try { await execution; } catch (OperationCanceledException) { } }
            if (commit.IsVisible) { commit.Close(); } await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    [AvaloniaTest]
    public async Task GourceOwnerCloseWaitsForANoncooperativeAcceptedWriteAndRetainsTheLaunchSnapshot()
    {
        RequireLinux(); using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n"); using LocalTool tool = new(f.Repository);
        Window owner = new() { Width = 800, Height = 600 }; owner.Show();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken writing = default; GourceOptions? captured = null; bool drained = false;
        GourceDialog dialog = new(Repository(f.Window), new Dictionary<string, string?> { ["executable"] = tool.Path, ["arguments"] = "--snapshot" },
            saveAcceptedSettings: async (accepted, token) =>
            {
                captured = accepted; writing = token; entered.TrySetResult();
                try { await release.Task; token.ThrowIfCancellationRequested(); }
                finally { drained = true; }
            });
        Task closed = dialog.ShowDialog(owner);
        try
        {
            await dialog.InitializeAsync(); Pointer(Field<Button>(dialog, "StartGource")); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            GourceOptions accepted = captured ?? throw new AssertionException("The owned writer must capture accepted options before signaling entry.");
            Assert.That(dialog.IsBusy, Is.True); Assert.That(accepted.Arguments, Is.EqualTo("--snapshot"));
            owner.Close(); Pump(owner);
            Assert.That(writing.IsCancellationRequested, Is.True); Assert.That(closed.IsCompleted, Is.False);
            Assert.That(owner.IsVisible && dialog.IsVisible, Is.True); Assert.That(drained, Is.False);
            release.TrySetResult(); await Until(owner, () => closed.IsCompleted && !owner.IsVisible && !dialog.IsBusy); await closed;
            Assert.That(drained, Is.True); Assert.That(dialog.LastLaunch, Is.Not.Null);
            Assert.That(accepted.Executable, Is.EqualTo(tool.Path)); await Until(f.Window, () => tool.Runs().Count == 1);
        }
        finally
        {
            release.TrySetResult(); if (dialog.IsVisible) { dialog.Close(); }
            await Until(owner, () => closed.IsCompleted && !dialog.IsBusy); await closed; owner.Close();
        }
    }

    private static async Task UnsupportedFind(GitRepository repository)
    {
        SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.FindLargeFiles; SimplePluginSettingsSession session = new(entry, repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Global, entry.Definition.Fields.Single(), "NaN"); await session.SaveFilesAsync(default);
    }
    private static async Task<Window> Message(Window owner)
    {
        await Until(owner, () => owner.OwnedWindows.Any(window => window.IsVisible && window.Name == "MessageDialog"));
        return owner.OwnedWindows.Single(window => window.IsVisible && window.Name == "MessageDialog");
    }

    private static GitRepository Repository(MainWindow main) => (GitRepository)typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static async Task<SimplePluginSettingsSession> AutoCompile(GitRepository repository, string executable)
    {
        SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.AutoCompile; SimplePluginSettingsSession session = new(entry, repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Local, entry.Definition.Find("_msBuildEnabled"), "false"); session.Set(GitDistributedSettingsScope.Local, entry.Definition.Find("_msBuildPath"), executable);
        session.Set(GitDistributedSettingsScope.Local, entry.Definition.Find("_msBuildArguments"), "--before"); await session.SaveFilesAsync(default); return session;
    }
    private static async Task<DesktopSettingsDialog> Settings(MainWindow main)
    {
        main.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        await Until(main, () => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        await Until(settings, () => settings.SimplePluginSettingsSessions.All(session => session.Loaded) && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled); return settings;
    }
    private static Task Saved(DesktopSettingsDialog settings) => Until(settings, () => settings.SimplePluginSettingsSessions.All(session => !session.HasChanges)
        && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled && string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
    private static async Task<T> Plugin<T>(MainWindow main, string name) where T : RepositoryDialog
    {
        ClickPlugin(main, name); await Until(main, () => main.OwnedWindows.OfType<T>().Any(window => window.IsVisible));
        T dialog = main.OwnedWindows.OfType<T>().Single(window => window.IsVisible); await Until(dialog, () => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static void ClickPlugin(MainWindow main, string name)
    {
        MenuItem plugins = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
        Pointer(plugins); MenuItem item = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == name); Assert.That(item.IsEnabled, Is.True); Pointer(item);
    }
    private static async Task<Window> BuildPrompt(Window owner)
    {
        await Until(owner, () => owner.OwnedWindows.Any(window => window.IsVisible && window.Name == "AutoCompileBuildConfirmation"));
        return owner.OwnedWindows.Single(window => window.IsVisible && window.Name == "AutoCompileBuildConfirmation");
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Edit(Window owner, string name, string text)
    {
        TextBox box = Field<TextBox>(owner, name); box.BringIntoView(); Pump(owner); Pointer(box);
        owner.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); owner.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control); owner.KeyTextInput(text); Pump(owner); Assert.That(box.Text, Is.EqualTo(text));
    }
    private static void Pointer(Control control) { control.BringIntoView(); if (TopLevel.GetTopLevel(control) is Window owner) { Pump(owner); } PushProcessTestDriver.Pointer(control); }
    private static void Pump(Window owner)
    {
        Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); }
        foreach (Window child in owner.OwnedWindows.Where(window => window.IsVisible).ToArray()) { child.UpdateLayout(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(condition(), Is.True, "The real plugin/defaults workflow did not settle within fifteen seconds.");
    }
    private static void RequireLinux() { if (OperatingSystem.IsWindows()) { Assert.Ignore("Uses a private local Linux executable."); } }

    private sealed record OptionalFileSnapshot(string Path, byte[]? Bytes)
    {
        internal static OptionalFileSnapshot Capture(string path) => new(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
        internal void AssertUnchanged()
        {
            Assert.That(File.Exists(Path), Is.EqualTo(Bytes is not null), "The workflow must preserve whether the preferences file exists.");
            if (Bytes is not null) { Assert.That(File.ReadAllBytes(Path), Is.EqualTo(Bytes)); }
        }
    }

    private sealed record ToolRun(int Pid, string[] Arguments);
    private sealed class LocalTool : IDisposable
    {
        internal string Path { get; }
        private readonly string _log;
        internal LocalTool(string directory)
        {
            Path = System.IO.Path.Combine(directory, "simple-plugin-local-tool.py"); _log = Path + ".jsonl";
            File.WriteAllText(Path, "#!/usr/bin/python3\nimport os,sys,json,pathlib\n" +
                "with pathlib.Path(" + JsonSerializer.Serialize(_log) + ").open('a') as f: f.write(json.dumps({'Pid':os.getpid(),'Arguments':sys.argv[1:]})+'\\n')\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        internal List<ToolRun> Runs() => !File.Exists(_log) ? [] : File.ReadAllLines(_log).Where(line => line.Length != 0)
            .Select(line => JsonSerializer.Deserialize<ToolRun>(line)!).ToList();
        public void Dispose()
        {
            foreach (ToolRun run in Runs())
            {
                try { using Process process = Process.GetProcessById(run.Pid); Assert.That(process.WaitForExit(5000), Is.True, "The private local tool must exit normally."); }
                catch (ArgumentException) { }
            }
        }
    }
}
