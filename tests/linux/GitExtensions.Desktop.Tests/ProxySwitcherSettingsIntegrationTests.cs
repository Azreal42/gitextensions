using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using BrowserFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ProxySwitcherSettingsIntegrationTests
{
    [AvaloniaTest]
    public async Task TheRealSettingsRequestPersistsFourAcceptedDefaultsAndSetAloneWritesGitWithMaskedPreviewAndLog()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        string? previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"); string global = Path.Combine(Path.GetDirectoryName(f.Repository)!, "proxy-test-global-config");
        File.WriteAllText(global, "[http]\n\tproxy = global.invalid:3128\n"); Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
        try
        {
            const string secret = "h31-explicit-secret-été"; GitRepository repo = ProxySettingsTestDriver.Repository(f.Window);
            ProxySwitcherDialog first = await ProxySettingsTestDriver.OpenProxy(f.Window);
            Assert.That(first.CurrentOptions.Password, Is.Empty); ListBox rows = ProxySettingsTestDriver.Commits(f.Window);
            object source = rows.ItemsSource!; object?[] items = rows.Items.Cast<object?>().ToArray();
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(first, "ProxySettings"));
            DesktopSettingsDialog settings = await ProxySettingsTestDriver.Settings(f.Window);
            Assert.That(first.SettingsRequested, Is.True); Assert.That(first.IsVisible, Is.False);
            Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo(ProxySwitcherSettingsCatalog.PageId)); Assert.That(settings.Owner, Is.SameAs(f.Window));
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<RadioButton>(settings, "ProxyLocalRB"));
            ProxySettingsTestDriver.Edit(settings, "Username", "alice"); ProxySettingsTestDriver.Edit(settings, "Password", secret);
            ProxySettingsTestDriver.Edit(settings, "HttpProxy", "local.invalid"); ProxySettingsTestDriver.Edit(settings, "HttpProxyPort", "8080");
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(settings, "ApplyDesktopSettings")); await ProxySettingsTestDriver.Saved(settings);
            Assert.That(await repo.GetConfigAsync("^http\\.proxy$", GitConfigScope.Local), Is.Empty, "Apply must not set Git's proxy.");
            Assert.That(File.ReadAllText(global), Is.EqualTo("[http]\n\tproxy = global.invalid:3128\n"));
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(settings, "CancelDesktopSettings"));
            await ProxySettingsTestDriver.Until(f.Window, () => !settings.IsVisible && !f.Window.IsBusy);
            Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(rows.Items.Cast<object?>(), Is.EqualTo(items));
            ProxySwitcherDialog dialog = await ProxySettingsTestDriver.OpenProxy(f.Window);
            Assert.That(dialog.CurrentOptions.Password, Is.EqualTo(secret)); Assert.That(dialog.CurrentOptions.Host, Is.EqualTo("local.invalid"));
            ProxySettingsTestDriver.Field<CheckBox>(dialog, "ApplyGlobally_CheckBox").IsChecked = false;
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(dialog, "SetProxy_Button")); await ProxySettingsTestDriver.Until(dialog, () => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null); Assert.That((await repo.GetConfigAsync("^http\\.proxy$", GitConfigScope.Local)).Single().Value, Is.EqualTo("alice:" + secret + "@local.invalid:8080"));
            Assert.That(ProxySettingsTestDriver.Field<TextBox>(dialog, "LocalHttpProxy_TextBox").Text, Is.EqualTo("alice:****@local.invalid:8080"));
            Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command.Contains(secret, StringComparison.Ordinal) || entry.CallStack?.Contains(secret, StringComparison.Ordinal) == true), Is.False);
            string json = File.Exists(new PreferencesStore().FilePath) ? File.ReadAllText(new PreferencesStore().FilePath) : ""; Assert.That(json, Does.Not.Contain(secret).And.Not.Contain("\"password\""));
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(dialog, "UnsetProxy_Button")); await ProxySettingsTestDriver.Until(dialog, () => !dialog.IsBusy);
            Assert.That(await repo.GetConfigAsync("^http\\.proxy$", GitConfigScope.Local), Is.Empty); Assert.That(File.ReadAllText(global), Is.EqualTo("[http]\n\tproxy = global.invalid:3128\n"));
            dialog.Close(); await ProxySettingsTestDriver.Until(f.Window, () => !dialog.IsVisible && !f.Window.IsBusy);
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous); }
    }

    [AvaloniaTest]
    public async Task InspectSettingsCancelAndCloseCannotCreateXmlOrJsonOrReloadTheHistory()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        ProxySettingsTestDriver.ProfileBytes[] before = await ProxySettingsTestDriver.Profiles(ProxySettingsTestDriver.Repository(f.Window));
        ListBox rows = ProxySettingsTestDriver.Commits(f.Window); object source = rows.ItemsSource!; object?[] items = rows.Items.Cast<object?>().ToArray();
        ProxySwitcherDialog proxy = await ProxySettingsTestDriver.OpenProxy(f.Window);
        ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(proxy, "ProxySettings")); DesktopSettingsDialog settings = await ProxySettingsTestDriver.Settings(f.Window);
        ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<RadioButton>(settings, "ProxyGlobalRB")); ProxySettingsTestDriver.Edit(settings, "Password", "cancelled-new-secret");
        TextBox password = ProxySettingsTestDriver.Field<TextBox>(settings, "Password");
        ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(settings, "CancelDesktopSettings")); await ProxySettingsTestDriver.Until(f.Window, () => !settings.IsVisible && !f.Window.IsBusy);
        Assert.That(password.Text, Is.Null.Or.Empty); foreach (ProxySettingsTestDriver.ProfileBytes file in before) { file.AssertUnchanged(); }
        Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(rows.Items.Cast<object?>(), Is.EqualTo(items));
        ProxySwitcherDialog reopened = await ProxySettingsTestDriver.OpenProxy(f.Window); Assert.That(reopened.CurrentOptions.Password, Is.Empty);
        reopened.Close(); await ProxySettingsTestDriver.Until(f.Window, () => !reopened.IsVisible && !f.Window.IsBusy);
        foreach (ProxySettingsTestDriver.ProfileBytes file in before) { file.AssertUnchanged(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AnActualOwnedScriptSettingsRequestRetainsItsOwnerAndStopsOnTokenOrOwnerClose(bool closeOwner)
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "change\n"); GitRepository repo = ProxySettingsTestDriver.Repository(f.Window);
        const int code = 9131; ScriptDefinition script = new() { Name = "H31 Proxy settings owner", Command = "{plugin.Proxy Switcher}", HotkeyCommandIdentifier = code };
        if (closeOwner)
        {
            new ScriptsPreferencesStore().Save([script]); HotkeyPreferencesStore store = new(); HotkeyBindings hotkeys = store.Load();
            hotkeys.Set("Scripts", code, new(Key.F12, KeyModifiers.Control | KeyModifiers.Alt)); store.Save(hotkeys);
        }
        CommitDialog commit = new(repo); Task closed = commit.ShowDialog(f.Window); using CancellationTokenSource cancellation = new(); Task<bool>? execution = null;
        try
        {
            await commit.InitializeAsync(); await ProxySettingsTestDriver.Until(f.Window, () => !commit.IsBusy);
            ProxySettingsTestDriver.ProfileBytes[] before = await ProxySettingsTestDriver.Profiles(repo);
            if (closeOwner)
            { commit.KeyPressQwerty(PhysicalKey.F12, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Alt); commit.KeyReleaseQwerty(PhysicalKey.F12, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Alt); }
            else { execution = DesktopScriptExecutor.ExecuteAsync(commit, repo, script, cancellation.Token); }
            await ProxySettingsTestDriver.Until(commit, () => commit.OwnedWindows.OfType<ProxySwitcherDialog>().Any(window => window.IsVisible && !window.IsBusy));
            ProxySwitcherDialog proxy = commit.OwnedWindows.OfType<ProxySwitcherDialog>().Single(window => window.IsVisible);
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(proxy, "ProxySettings")); DesktopSettingsDialog settings = await ProxySettingsTestDriver.Settings(commit);
            Assert.That(settings.Owner, Is.SameAs(commit)); Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo(ProxySwitcherSettingsCatalog.PageId));
            ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<RadioButton>(settings, "ProxyLocalRB")); ProxySettingsTestDriver.Edit(settings, "Password", "owned-unsaved-secret");
            if (closeOwner)
            { Assert.That(commit.IsBusy, Is.True); commit.Close(); await ProxySettingsTestDriver.Until(f.Window, () => closed.IsCompleted && !commit.IsBusy && !settings.IsVisible); }
            else
            {
                cancellation.Cancel(); await ProxySettingsTestDriver.Until(f.Window, () => execution!.IsCompleted && !settings.IsVisible);
                try { _ = await execution!.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Cancellation must not resume the owned plugin script."); } catch (OperationCanceledException) { }
            }
            await f.Window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            foreach (ProxySettingsTestDriver.ProfileBytes file in before) { file.AssertUnchanged(); }
            Assert.That(commit.OwnedWindows.Any(window => window.IsVisible), Is.False); Assert.That(commit.IsVisible, Is.EqualTo(!closeOwner));
        }
        finally
        {
            cancellation.Cancel(); foreach (Window child in commit.OwnedWindows.ToArray()) { child.Close(); }
            if (execution is not null) { await ProxySettingsTestDriver.Until(f.Window, () => execution.IsCompleted); try { await execution; } catch (OperationCanceledException) { } }
            if (commit.IsVisible) { commit.Close(); } await ProxySettingsTestDriver.Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }
}

internal static class ProxySettingsTestDriver
{
    internal static GitRepository Repository(MainWindow main) => (GitRepository)typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    internal static ListBox Commits(MainWindow main) => (ListBox)typeof(MainWindow).GetField("_commits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    internal static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal static void Pointer(Control control) { control.BringIntoView(); if (TopLevel.GetTopLevel(control) is Window owner) { Pump(owner); } PushProcessTestDriver.Pointer(control); }
    internal static void Edit(Window owner, string name, string value)
    {
        TextBox editor = Field<TextBox>(owner, name); Pointer(editor);
        owner.KeyPressQwerty(PhysicalKey.A, Avalonia.Input.RawInputModifiers.Control); owner.KeyReleaseQwerty(PhysicalKey.A, Avalonia.Input.RawInputModifiers.Control);
        owner.KeyPressQwerty(PhysicalKey.Backspace, Avalonia.Input.RawInputModifiers.None); owner.KeyReleaseQwerty(PhysicalKey.Backspace, Avalonia.Input.RawInputModifiers.None);
        if (value.Length != 0) { owner.KeyTextInput(value); } Pump(owner); Assert.That(editor.Text, Is.EqualTo(value));
    }
    internal static async Task<ProxySwitcherDialog> OpenProxy(MainWindow main)
    {
        MenuItem menu = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins"); Pointer(menu);
        Pointer(menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Proxy Switcher"));
        await Until(main, () => main.OwnedWindows.OfType<ProxySwitcherDialog>().Any(window => window.IsVisible && !window.IsBusy));
        return main.OwnedWindows.OfType<ProxySwitcherDialog>().Single(window => window.IsVisible);
    }
    internal static async Task<DesktopSettingsDialog> Settings(Window owner)
    {
        await Until(owner, () => owner.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        DesktopSettingsDialog settings = owner.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        await Until(settings, () => settings.ProxySwitcherSettingsSession.Loaded && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled); return settings;
    }
    internal static async Task<DesktopSettingsDialog> OpenStandaloneSettings(GitRepository repository, Window owner)
    {
        DesktopPreferences initial = new PreferencesStore().Load();
        DesktopSettingsDialog dialog = new(initial, TranslationCatalog.LoadDefault(), true,
            detailedSettings: new(repository, initial), initialPage: ProxySwitcherSettingsCatalog.PageId);
        _ = dialog.ShowDialog<bool>(owner); await Until(dialog, () => dialog.ProxySwitcherSettingsSession.Loaded && Field<Button>(dialog, "ApplyDesktopSettings").IsEnabled); return dialog;
    }
    internal static async Task WithSettings(GitRepository repository, Func<DesktopSettingsDialog, Task> action)
    {
        Window owner = new() { Width = 1080, Height = 760 }; owner.Show(); DesktopSettingsDialog? settings = null;
        try { settings = await OpenStandaloneSettings(repository, owner); await action(settings); }
        finally
        {
            if (settings is { IsVisible: true }) { settings.Close(); await Until(owner, () => !settings.IsVisible); }
            owner.Close();
        }
    }
    internal static Task Saved(DesktopSettingsDialog settings) => Until(settings, () => !settings.ProxySwitcherSettingsSession.HasChanges && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled && string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
    internal static void Pump(Window owner)
    {
        Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); }
        foreach (Window child in owner.OwnedWindows.Where(window => window.IsVisible).ToArray()) { child.UpdateLayout(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    internal static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(condition(), Is.True, "The real H31 Proxy workflow did not settle within fifteen seconds.");
    }
    internal sealed record ProfileBytes(string Path, byte[]? Bytes)
    {
        internal void AssertUnchanged() { Assert.That(File.Exists(Path), Is.EqualTo(Bytes is not null)); if (Bytes is not null) { Assert.That(File.ReadAllBytes(Path), Is.EqualTo(Bytes)); } }
    }
    internal static async Task<ProfileBytes[]> Profiles(GitRepository repository)
    {
        GitRepositoryInfo info = await repository.GetInfoAsync();
        return new[] { DetailedRepositorySettings.GlobalPath, Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName), Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName), new PreferencesStore().FilePath }
            .Distinct(StringComparer.Ordinal).Select(path => new ProfileBytes(path, File.Exists(path) ? File.ReadAllBytes(path) : null)).ToArray();
    }
}
