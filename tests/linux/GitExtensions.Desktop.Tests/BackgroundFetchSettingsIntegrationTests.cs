using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using BrowserFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BackgroundFetchSettingsIntegrationTests
{
    [AvaloniaTest]
    public async Task TheRealPluginMenuUsesScopedSettingsAndReconfiguresTheSameServiceOnlyAfterApply()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        object service = typeof(MainWindow).GetField("_backgroundFetch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window)!;
        PreferencesStore store = new();
        Dictionary<string, Dictionary<string, string?>> legacy = new()
        {
            [BackgroundFetchService.Descriptor.Id.ToString()] = new() { ["arguments"] = "fetch --all", ["future-background-field"] = "preserve" },
            ["00000000-0000-0000-0000-000000000123"] = new() { ["future-provider"] = "keep λ" }
        };
        store.Save(store.Load() with { PluginSettings = legacy });
        DesktopSettingsDialog settings = await OpenPlugin(f.Window);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("PluginBackgroundFetch"));
        Pointer(Field<RadioButton>(settings, "BackgroundFetchLocalRB"));
        Edit(settings, "_gitCommand", "config --local test.background-scoped marker");
        Choose(settings, "_fetchImmediatelyOnRepoOpening", true);
        Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await Until(settings, () => !settings.BackgroundFetchSettingsSession.HasChanges && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled
            && string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(DetailedRepositorySettings.GlobalPath, await new GitRepository(f.Repository).GetInfoAsync());
        string local = snapshot.Local!.Path; byte[] xml = File.ReadAllBytes(local);
        Assert.That(snapshot.Local.Values[BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_gitCommand"))], Is.EqualTo("config --local test.background-scoped marker"));
        Assert.That(await Config(f, "test.background-scoped"), Is.Empty, "An immediate worker must still wait while this plugin's Settings operation owns the foreground gate.");
        Assert.That(typeof(MainWindow).GetField("_backgroundFetch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window), Is.SameAs(service));
        Edit(settings, "_gitCommand", "config --local test.must-not-run unsaved");
        Pointer(Field<Button>(settings, "CancelDesktopSettings"));
        await Until(f.Window, () => !settings.IsVisible && !f.Window.IsBusy);
        await UntilAsync(f.Window, async () => await Config(f, "test.background-scoped") == "marker");
        Assert.That(File.ReadAllBytes(local), Is.EqualTo(xml)); Assert.That(await Config(f, "test.must-not-run"), Is.Empty);
        Assert.That(store.Load().PluginSettings, Is.EqualTo(legacy));
        DesktopSettingsDialog reopened = await OpenPlugin(f.Window); Pointer(Field<RadioButton>(reopened, "BackgroundFetchLocalRB"));
        Assert.That(Field<TextBox>(reopened, "_gitCommand").Text, Is.EqualTo("config --local test.background-scoped marker"));
        Pointer(Field<Button>(reopened, "CancelDesktopSettings")); await Until(f.Window, () => !reopened.IsVisible && !f.Window.IsBusy);
        Assert.That(f.Window.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task ARealXmlConflictLeavesTheServiceAndPeerBytesUnchangedAndSelectsThePluginPage()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        DesktopSettingsDialog settings = await OpenPlugin(f.Window); Pointer(Field<RadioButton>(settings, "BackgroundFetchGlobalRB"));
        Edit(settings, "_gitCommand", "config --local test.conflict must-not-run"); Choose(settings, "_fetchImmediatelyOnRepoOpening", true);
        GitXmlSettingsSnapshot before = await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath);
        await GitXmlSettingsStore.SaveChangesAsync(before, new Dictionary<string, string?> { ["peer.after-settings-open"] = "keep" });
        byte[] peer = File.ReadAllBytes(before.Path);
        Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await Until(settings, () => Field<Button>(settings, "ApplyDesktopSettings").IsEnabled && !string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("PluginBackgroundFetch"));
        Assert.That(settings.BackgroundFetchSettingsSession.HasChanges, Is.True);
        Assert.That(Field<TextBox>(settings, "_gitCommand").Text, Is.EqualTo("config --local test.conflict must-not-run"));
        Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Until(f.Window, () => !settings.IsVisible && !f.Window.IsBusy);
        Assert.That(File.ReadAllBytes(before.Path), Is.EqualTo(peer)); Assert.That(await Config(f, "test.conflict"), Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AnActualCommitOwnedPluginScriptKeepsItsTokenOwnerAndDrainsWithoutWritingOnCancelOrOwnerClose(bool closeOwner)
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "working change\n");
        GitRepository repository = (GitRepository)typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window)!;
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(f.Window);
        using CancellationTokenSource cancellation = new(); Task<bool>? execution = null;
        string global = DetailedRepositorySettings.GlobalPath; byte[]? before = File.Exists(global) ? File.ReadAllBytes(global) : null;
        try
        {
            await commit.InitializeAsync(); await Until(f.Window, () => !commit.IsBusy);
            ScriptDefinition script = new() { Name = "Background Settings owner test", Command = "{plugin.Periodic background fetch}" };
            execution = DesktopScriptExecutor.ExecuteAsync(commit, repository, script, cancellation.Token);
            await Until(f.Window, () => commit.OwnedWindows.OfType<DesktopSettingsDialog>().Any(dialog => dialog.IsVisible));
            DesktopSettingsDialog settings = commit.OwnedWindows.OfType<DesktopSettingsDialog>().Single(dialog => dialog.IsVisible);
            await Until(f.Window, () => settings.BackgroundFetchSettingsSession.Loaded && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled);
            Assert.That(settings.Owner, Is.SameAs(commit)); Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("PluginBackgroundFetch"));
            CancellationToken owned = settings.GitEnvironmentSession!.CancellationToken;
            Pointer(Field<RadioButton>(settings, "BackgroundFetchGlobalRB")); Edit(settings, "_gitCommand", "config --local test.cancelled never");
            if (closeOwner) { commit.Close(); } else { cancellation.Cancel(); }
            await Until(f.Window, () => execution.IsCompleted && !settings.IsVisible && (!closeOwner || closed.IsCompleted));
            try { _ = await execution.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("A cancelled/closed owner must not report successful script completion."); }
            catch (OperationCanceledException) { }
            Assert.That(owned.IsCancellationRequested, Is.True); Assert.That(commit.IsVisible, Is.EqualTo(!closeOwner));
            Assert.That(commit.OwnedWindows.Any(dialog => dialog.IsVisible), Is.False); Assert.That(f.Window.IsVisible, Is.True);
            Assert.That(File.Exists(global), Is.EqualTo(before is not null));
            if (before is not null) { Assert.That(File.ReadAllBytes(global), Is.EqualTo(before)); }
            Assert.That(await Config(f, "test.cancelled"), Is.Empty);
        }
        finally
        {
            cancellation.Cancel();
            foreach (DesktopSettingsDialog child in commit.OwnedWindows.OfType<DesktopSettingsDialog>().ToArray()) { child.Close(); }
            if (execution is not null) { await Until(f.Window, () => execution.IsCompleted); try { await execution; } catch (OperationCanceledException) { } }
            if (commit.IsVisible) { commit.Close(); }
            await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    private static async Task<DesktopSettingsDialog> OpenPlugin(MainWindow owner)
    {
        MenuItem plugins = owner.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
        Pointer(plugins); MenuItem item = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == BackgroundFetchService.Descriptor.Name);
        Assert.That(item.IsEnabled, Is.True); Assert.That(item.Icon, Is.Not.Null); Pointer(item);
        await Until(owner, () => owner.OwnedWindows.OfType<DesktopSettingsDialog>().Any(dialog => dialog.IsVisible));
        DesktopSettingsDialog settings = owner.OwnedWindows.OfType<DesktopSettingsDialog>().Single(dialog => dialog.IsVisible);
        await Until(settings, () => settings.BackgroundFetchSettingsSession.Loaded && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled); return settings;
    }
    private static async Task<string> Config(BrowserFixture f, string key)
    {
        GitResult result = await f.Git.RunAsync(f.Repository, ["config", "--local", "--get-all", key]);
        Assert.That(result.ExitCode, Is.AnyOf(0, 1)); return result.StandardOutput.TrimEnd('\r', '\n');
    }
    private static void Choose(Window owner, string name, bool value)
    { CheckBox box = Field<CheckBox>(owner, name); for (int count = 0; count < 3 && box.IsChecked != value; count++) { Pointer(box); } Assert.That(box.IsChecked, Is.EqualTo(value)); }
    private static void Edit(Window owner, string name, string value)
    {
        TextBox text = Field<TextBox>(owner, name); text.BringIntoView(); Pump(owner); Pointer(text);
        owner.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); owner.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
        owner.KeyTextInput(value); Pump(owner); Assert.That(text.Text, Is.EqualTo(value));
    }
    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Control control) { control.BringIntoView(); PushProcessTestDriver.Pointer(control); }
    private static void Pump(Window owner)
    {
        Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); }
        foreach (Window child in owner.OwnedWindows.Where(child => child.IsVisible).ToArray()) { child.UpdateLayout(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Window owner, Func<bool> predicate)
    {
        Stopwatch clock = Stopwatch.StartNew(); while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(predicate(), Is.True, "The real Background Settings/owner workflow did not settle within fifteen seconds.");
    }
    private static async Task UntilAsync(Window owner, Func<Task<bool>> predicate)
    {
        Stopwatch clock = Stopwatch.StartNew(); bool done = false;
        while (!(done = await predicate()) && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(owner); await Task.Delay(10); }
        Assert.That(done, Is.True, "The real background Git operation did not reach its observable local result.");
    }
}
