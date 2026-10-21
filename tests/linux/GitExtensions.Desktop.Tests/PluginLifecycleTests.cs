using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class PluginLifecycleTests
{
    [AvaloniaTest]
    public async Task PluginMenuAppliesAndReloadsBackgroundSettingsAndWaitsUntilSettingsDialogCloses()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        MainWindow window = fixture.Window;
        DesktopSettingsDialog dialog = await OpenPluginAsync(window, BackgroundFetchService.Descriptor.Name);
        Field<TextBox>(dialog, "_gitCommand").Text = "config --local test.background marker";
        Field<NumericUpDown>(dialog, "_fetchInterval").Value = 0;
        Field<CheckBox>(dialog, "_fetchImmediatelyOnRepoOpening").IsChecked = true;
        await ApplyAsync(dialog);
        Assert.That(await LocalConfigAsync(fixture.Git, fixture.Repository, "test.background"), Is.Empty,
            "The background operation must wait while its settings dialog is open.");
        GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath);
        Assert.Multiple(() =>
        {
            Assert.That(saved.Values[BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_gitCommand"))], Is.EqualTo("config --local test.background marker"));
            Assert.That(saved.Values[BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_fetchInterval"))], Is.EqualTo("0"));
            Assert.That(saved.Values[BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_fetchImmediatelyOnRepoOpening"))], Is.EqualTo("True").IgnoreCase);
        });
        await ClosePluginAsync(window, dialog);
        await UntilAsync(async () => await LocalConfigAsync(fixture.Git, fixture.Repository, "test.background") == "marker");
        Assert.That(window.LastException, Is.Null);
        window.Close();
        GitResult unset = await fixture.Git.RunAsync(fixture.Repository, ["config", "--local", "--unset", "test.background"]);
        Assert.That(unset.Success, Is.True, unset.StandardError);

        MainWindow reloaded = new() { Width = 1440, Height = 900 };
        try
        {
            reloaded.Show();
            await reloaded.OpenRepositoryAsync(fixture.Repository);
            await UntilAsync(async () => await LocalConfigAsync(fixture.Git, fixture.Repository, "test.background") == "marker");
            DesktopSettingsDialog reloadedDialog = await OpenPluginAsync(reloaded, BackgroundFetchService.Descriptor.Name);
            Assert.Multiple(() =>
            {
                Assert.That(Field<TextBox>(reloadedDialog, "_gitCommand").Text, Is.EqualTo("config --local test.background marker"));
                Assert.That(Field<NumericUpDown>(reloadedDialog, "_fetchInterval").Value, Is.Zero);
                Assert.That(Field<CheckBox>(reloadedDialog, "_fetchImmediatelyOnRepoOpening").IsChecked, Is.True);
            });
            await ClosePluginAsync(reloaded, reloadedDialog);
            Assert.That(reloaded.LastException, Is.Null);
        }
        finally { reloaded.Close(); }
    }

    [AvaloniaTest]
    public async Task SwitchingRepositoriesAndClosingTheBrowserStopsPreviousBackgroundTimers()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        string secondPath = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "second repository");
        GitRepository second = await GitRepository.InitAsync(secondPath);
        DesktopSettingsDialog dialog = await OpenPluginAsync(fixture.Window, BackgroundFetchService.Descriptor.Name);
        Field<TextBox>(dialog, "_gitCommand").Text = "config --local --add test.background marker";
        Field<NumericUpDown>(dialog, "_fetchInterval").Value = 5;
        Field<CheckBox>(dialog, "_fetchImmediatelyOnRepoOpening").IsChecked = false;
        await ApplyAsync(dialog);
        await ClosePluginAsync(fixture.Window, dialog);
        await fixture.Window.OpenRepositoryAsync(secondPath);
        Assert.That(fixture.Window.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "RepositoryPath").Text, Is.EqualTo(secondPath));
        Assert.That(fixture.Window.Title, Is.EqualTo(Path.GetFileName(secondPath) + " (main) — Git Extensions"));
        await UntilAsync(async () => await LocalConfigAsync(fixture.Git, secondPath, "test.background") == "marker");
        Assert.That(await LocalConfigAsync(fixture.Git, fixture.Repository, "test.background"), Is.Empty,
            "The timer for the previous repository continued after the repository changed.");
        fixture.Window.Close();
        GitResult unset = await fixture.Git.RunAsync(secondPath, ["config", "--local", "--unset-all", "test.background"]);
        Assert.That(unset.Success, Is.True, unset.StandardError);
        // Wait beyond the configured interval, with both old and current repos
        // observable on disk. No private timer/service implementation is exposed.
        await Task.Delay(TimeSpan.FromMilliseconds(5300));
        Assert.That(await LocalConfigAsync(fixture.Git, secondPath, "test.background"), Is.Empty,
            "A background timer restarted after its browser closed.");
        Assert.That(await LocalConfigAsync(fixture.Git, fixture.Repository, "test.background"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingTheBrowserCancelsAnAlreadyRunningBackgroundGitProcess()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("This lifecycle test uses a configured local Linux executable."); return; }
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        string executable = Path.Combine(fixture.Repository, "background-lifecycle.py");
        string started = Path.Combine(fixture.Repository, "background.started");
        string completed = Path.Combine(fixture.Repository, "background.completed");
        File.WriteAllText(executable, """
            #!/usr/bin/python3
            import os, pathlib, time
            root = pathlib.Path(__file__).parent
            (root / 'background.started').write_text(str(os.getpid()))
            time.sleep(30)
            (root / 'background.completed').write_text('completed')
            """ + "\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitResult alias = await fixture.Git.RunAsync(fixture.Repository, ["config", "--local", "alias.fixture-lifecycle", "!" + ShellQuote(executable)]);
        Assert.That(alias.Success, Is.True, alias.StandardError);
        DesktopSettingsDialog dialog = await OpenPluginAsync(fixture.Window, BackgroundFetchService.Descriptor.Name);
        Field<TextBox>(dialog, "_gitCommand").Text = "fixture-lifecycle";
        Field<NumericUpDown>(dialog, "_fetchInterval").Value = 0;
        Field<CheckBox>(dialog, "_fetchImmediatelyOnRepoOpening").IsChecked = true;
        await ApplyAsync(dialog);
        await ClosePluginAsync(fixture.Window, dialog);
        await UntilAsync(() => Task.FromResult(File.Exists(started) && new FileInfo(started).Length > 0));
        int pid = int.Parse(await File.ReadAllTextAsync(started), System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(IsRunning(pid), Is.True, "The test must close a browser with Git actually running.");
        fixture.Window.Close();
        await UntilAsync(() => Task.FromResult(!IsRunning(pid)));
        Assert.That(File.Exists(completed), Is.False, "Closing left the configured Git operation running to completion.");
    }

    [AvaloniaTest]
    public async Task ProxyMenuMasksPasswordChangesOnlyLocalConfigurationAndExcludesSecretFromPreferences()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        string? previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        string isolatedGlobal = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "isolated-global-config");
        File.WriteAllText(isolatedGlobal, "[http]\n\tproxy = global.example.invalid:3128\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", isolatedGlobal);
        try
        {
            ProxySwitcherDialog dialog = await OpenProxyAsync();
            Field<Button>(dialog, "ProxySettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Task.FromResult(dialog.OwnedWindows.OfType<ProxySwitcherSettingsDialog>().Any()));
            ProxySwitcherSettingsDialog settings = dialog.OwnedWindows.OfType<ProxySwitcherSettingsDialog>().Single();
            const string secret = "proxy-secret-été";
            TextBox password = Field<TextBox>(settings, "ProxyPassword");
            Assert.That(password.PasswordChar, Is.Not.EqualTo('\0'), "The actual Proxy input must mask typed credentials.");
            Field<TextBox>(settings, "ProxyHost").Text = "local.example.invalid";
            Field<TextBox>(settings, "ProxyPort").Text = "8080";
            Field<TextBox>(settings, "ProxyUsername").Text = "alice";
            password.Text = secret;
            Field<Button>(settings, "SaveProxySettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Task.FromResult(dialog.CurrentOptions.Host == "local.example.invalid"));
            Field<CheckBox>(dialog, "ApplyGlobally_CheckBox").IsChecked = false;
            Field<Button>(dialog, "SetProxy_Button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Task.FromResult(!dialog.IsBusy));
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await LocalConfigAsync(fixture.Git, fixture.Repository, "http.proxy"), Is.EqualTo("alice:" + secret + "@local.example.invalid:8080"));
            Assert.That(await File.ReadAllTextAsync(isolatedGlobal), Is.EqualTo("[http]\n\tproxy = global.example.invalid:3128\n"),
                "Local Proxy setting changed global configuration.");
            Assert.That(Field<TextBox>(dialog, "LocalHttpProxy_TextBox").Text, Is.EqualTo("alice:****@local.example.invalid:8080"));
            Assert.That(Field<TextBox>(dialog, "GlobalHttpProxy_TextBox").Text, Is.EqualTo("global.example.invalid:3128"));
            Assert.That(dialog.CurrentValues.Values.Any(value => value?.Contains(secret, StringComparison.Ordinal) == true), Is.False);
            dialog.Close();
            await UntilAsync(() => Task.FromResult(!fixture.Window.OwnedWindows.Contains(dialog) && !fixture.Window.IsBusy
                && new PreferencesStore().Load().PluginSettings.GetValueOrDefault(new ProxySwitcherPlugin().Descriptor.Id.ToString())?.GetValueOrDefault("host") == "local.example.invalid"));
            string preferences = await File.ReadAllTextAsync(new PreferencesStore().FilePath);
            Assert.That(preferences, Does.Not.Contain(secret).And.Not.Contain("\"password\""));
            ProxySwitcherDialog reloaded = await OpenProxyAsync();
            Assert.That(reloaded.CurrentOptions.Password, Is.Empty);
            Assert.That(reloaded.CurrentOptions.Host, Is.EqualTo("local.example.invalid"));
            Field<Button>(reloaded, "ProxySettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Task.FromResult(reloaded.OwnedWindows.OfType<ProxySwitcherSettingsDialog>().Any()));
            ProxySwitcherSettingsDialog reloadedSettings = reloaded.OwnedWindows.OfType<ProxySwitcherSettingsDialog>().Single();
            Assert.That(Field<TextBox>(reloadedSettings, "ProxyPassword").Text, Is.Null.Or.Empty);
            Field<Button>(reloadedSettings, "CancelProxySettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => Task.FromResult(!reloadedSettings.IsVisible));
            reloaded.Close();
            await UntilAsync(() => Task.FromResult(!fixture.Window.OwnedWindows.Contains(reloaded) && !fixture.Window.IsBusy));
            Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());

            async Task<ProxySwitcherDialog> OpenProxyAsync()
            {
                MenuItem plugins = fixture.Window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
                MenuItem item = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == new ProxySwitcherPlugin().Descriptor.Name);
                Assert.That(item.IsEnabled, Is.True); Assert.That(item.Icon, Is.Not.Null);
                plugins.IsSubMenuOpen = true; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); plugins.IsSubMenuOpen = false;
                await UntilAsync(() => Task.FromResult(fixture.Window.OwnedWindows.OfType<ProxySwitcherDialog>().Any(candidate => !candidate.IsBusy)));
                return fixture.Window.OwnedWindows.OfType<ProxySwitcherDialog>().Single();
            }
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobal); }
    }

    private static async Task<DesktopSettingsDialog> OpenPluginAsync(MainWindow owner, string title)
    {
        MenuItem plugins = owner.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
        MenuItem item = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == title);
        Assert.That(item.IsEnabled, Is.True, "The real plugin menu entry must be enabled.");
        Assert.That(item.Icon, Is.Not.Null, "The original plugin icon must remain in the menu.");
        plugins.IsSubMenuOpen = true;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        plugins.IsSubMenuOpen = false;
        await UntilAsync(() => Task.FromResult(owner.OwnedWindows.OfType<DesktopSettingsDialog>().Any()));
        DesktopSettingsDialog dialog = owner.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        await UntilAsync(() => Task.FromResult(dialog.BackgroundFetchSettingsSession.Loaded && Field<Button>(dialog, "ApplyDesktopSettings").IsEnabled));
        Assert.That(dialog.Navigation.SelectedPage.Id, Is.EqualTo("PluginBackgroundFetch"));
        SettingsNavigationTestDriver.Select(dialog, "PluginBackgroundFetch");
        Field<RadioButton>(dialog, "BackgroundFetchGlobalRB").IsChecked = true;
        dialog.UpdateLayout();
        return dialog;
    }

    private static async Task ApplyAsync(DesktopSettingsDialog dialog)
    {
        Field<Button>(dialog, "ApplyDesktopSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Task.FromResult(Field<Button>(dialog, "ApplyDesktopSettings").IsEnabled && !dialog.BackgroundFetchSettingsSession.HasChanges));
        Assert.That(Field<TextBlock>(dialog, "SettingsNotice").Text, Is.Null.Or.Empty);
    }

    private static async Task ClosePluginAsync(MainWindow owner, DesktopSettingsDialog dialog)
    {
        Field<Button>(dialog, "CancelDesktopSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => Task.FromResult(!owner.OwnedWindows.Contains(dialog) && !owner.IsBusy));
        Assert.That(owner.LastException, Is.Null, owner.LastException?.ToString());
    }

    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static async Task<string> LocalConfigAsync(GitRunner runner, string path, string key)
    {
        GitResult result = await runner.RunAsync(path, ["config", "--local", "--get-all", key]);
        Assert.That(result.ExitCode, Is.AnyOf(0, 1), result.StandardError);
        return result.StandardOutput.TrimEnd('\n', '\r');
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!await predicate()) { await Task.Delay(20, timeout.Token); }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static bool IsRunning(int pid)
    {
        // A killed Git grandchild may await the host init's reaping; a zombie
        // cannot continue the configured operation and is already stopped.
        string status = "/proc/" + pid + "/stat";
        try
        {
            if (File.Exists(status) && File.ReadAllText(status).Contains(") Z ", StringComparison.Ordinal)) { return false; }
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
