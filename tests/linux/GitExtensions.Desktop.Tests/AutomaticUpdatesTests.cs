using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.UpdatesDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AutomaticUpdatesTests
{
    [AvaloniaTest]
    public async Task TheDefaultHeadlessBrowseNeverArmsUpdateChecksImplicitly()
    {
        await using Host f = new(enable: false);
        Assert.That(f.Main.AutomaticUpdatesCompletion.IsCompletedSuccessfully, Is.True);
        Assert.That(f.Clients, Is.Empty); Assert.That(File.Exists(f.State.FilePath), Is.False);
    }

    [AvaloniaTest]
    public async Task DisablingWeeklyChecksMakesNoClientAndWritesNoAttemptTime()
    {
        await using Host f = new(enabledPreference: false);
        await Finish(f); Assert.That(f.Clients, Is.Empty); Assert.That(File.Exists(f.State.FilePath), Is.False);
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ACheckClaimsTheWeekBeforeHttpAndSilentlyRetiresWhenThereIsNoNewVersionOrAnError(bool fail)
    {
        await using Host f = new(); UpdatesTestClient client = f.Clients.Single();
        Assert.That(File.Exists(f.State.FilePath), Is.True); byte[] claimed = File.ReadAllBytes(f.State.FilePath);
        Assert.That(client.Searches.Single().IncludePrereleases, Is.False); Assert.That(client.Disposed, Is.False);
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Opened, Is.Empty);
        if (fail) { client.Searches[0].Result.SetException(new IOException("private update request failed")); }
        else { client.Searches[0].Result.SetResult(new(null, false)); }
        await Finish(f);
        Assert.That(client.Disposed, Is.True); Assert.That(client.DisposedWhileActive, Is.False);
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Main.LastException, Is.Null); Assert.That(f.Opened, Is.Empty);
        Assert.That(File.ReadAllBytes(f.State.FilePath), Is.EqualTo(claimed));
        await f.Restart(); await Finish(f);
        Assert.That(f.Clients.Count, Is.EqualTo(1), "A second application window in the same week cannot repeat even a failed request.");
    }

    [AvaloniaTest]
    public async Task AnInvalidMarkerSkipsBackgroundHttpWithoutRepairingItOrShowingAModal()
    {
        await using Host f = new(enable: false);
        Directory.CreateDirectory(Path.GetDirectoryName(f.State.FilePath)!); File.WriteAllText(f.State.FilePath, "{broken state");
        await f.Restart(); await Finish(f);
        Assert.That(f.Clients, Is.Empty); Assert.That(File.ReadAllText(f.State.FilePath), Is.EqualTo("{broken state"));
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Main.LastException, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AnAvailableVersionOpensTheOwnedPrefetchedDialogWithoutRepeatingTheGet(bool prerelease)
    {
        await using Host f = new(includePrereleases: prerelease);
        UpdatesTestClient background = f.Clients.Single(); Assert.That(background.Searches[0].IncludePrereleases, Is.EqualTo(prerelease));
        LinuxUpdateRelease release = Available(prerelease); background.Searches[0].Result.SetResult(new(release, true));
        UpdatesDialog dialog = await Notification(f);
        Assert.That(background.Disposed, Is.True); Assert.That(background.DisposedWhileActive, Is.False);
        Assert.That(dialog.Owner, Is.SameAs(f.Main)); Assert.That(f.Clients.Count, Is.EqualTo(2));
        Assert.That(f.Clients[1].Searches, Is.Empty, "The dialog must present the completed result, not fetch the release list a second time.");
        Assert.That(Field<TextBlock>(dialog, "UpdateLabel").Text, Does.Contain(release.Version.Text));
        Assert.That(Field<CheckBox>(dialog, "IncludePrereleases").IsChecked, Is.EqualTo(prerelease));
        Assert.That(f.Opened, Is.Empty); Assert.That(f.Main.AutomaticUpdatesCompletion.IsCompleted, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "linkChangeLog")); await Until(() => dialog.PendingOperation.IsCompleted, dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { release.ReleaseUri.AbsoluteUri }));
        dialog.Close(); await Finish(f);
        Assert.That(f.Clients[1].Disposed, Is.True); Assert.That(f.Clients[1].DisposedWhileActive, Is.False); Assert.That(f.Main.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task AnExistingModalKeepsTheNotificationPendingAndTheLatestPreferenceCanWithdrawIt()
    {
        await using Host f = new(); Window blocker = new() { Name = "ExistingModal", Width = 300, Height = 120 };
        Task modal = blocker.ShowDialog(f.Main); Pump(blocker);
        f.Clients[0].Searches[0].Result.SetResult(new(Available(false), true));
        await Until(() => f.Clients[0].Disposed, blocker);
        Assert.That(f.Main.OwnedWindows.OfType<UpdatesDialog>(), Is.Empty); Assert.That(f.Clients.Count, Is.EqualTo(1));
        f.SetPreferences(f.Store.Load() with { CheckForUpdates = false });
        await Finish(f); Assert.That(blocker.IsVisible, Is.True); Assert.That(f.Clients.Count, Is.EqualTo(1));
        blocker.Close(); await modal; Assert.That(f.Main.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task TheNotificationWaitsUntilTheExistingModalCloses()
    {
        await using Host f = new(); Window blocker = new() { Name = "ExistingModal", Width = 300, Height = 120 };
        Task modal = blocker.ShowDialog(f.Main); Pump(blocker);
        f.Clients[0].Searches[0].Result.SetResult(new(Available(false), true)); await Until(() => f.Clients[0].Disposed, blocker);
        Assert.That(f.Main.OwnedWindows.OfType<UpdatesDialog>(), Is.Empty); Assert.That(f.Clients.Count, Is.EqualTo(1));
        blocker.Close(); await modal; f.Main.Activate(); Pump(f.Main);
        UpdatesDialog dialog = await Notification(f); dialog.Close(); await Finish(f);
        Assert.That(f.Main.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ChangingReleaseCandidateConsentWhileWaitingRejectsEvenAGitHubPrereleaseWithAStableTag()
    {
        await using Host f = new(includePrereleases: true);
        f.SetPreferences(f.Store.Load() with { CheckForReleaseCandidates = false });
        f.Clients[0].Searches[0].Result.SetResult(new(Available(false) with { IsPrerelease = true }, true));
        await Finish(f); Assert.That(f.Clients.Count, Is.EqualTo(1)); Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerCancelsButWaitsForAnIgnoringClientBeforeTheActualWindowCloses()
    {
        await using Host f = new(ignoreCancellation: true); UpdatesTestClient background = f.Clients.Single();
        f.Main.Close(); Pump(f.Main);
        Assert.That(background.Searches[0].Token.IsCancellationRequested, Is.True); Assert.That(background.Disposed, Is.False);
        Assert.That(f.Main.IsVisible, Is.True, "The real Closing lease keeps the owner until the client operation has actually retired.");
        Assert.That(f.Main.AutomaticUpdatesCompletion.IsCompleted, Is.False);
        background.Searches[0].Result.SetResult(new(Available(false), true));
        await Until(() => !f.Main.IsVisible && f.Main.AutomaticUpdatesCompletion.IsCompleted, f.Main);
        Assert.That(background.Disposed, Is.True); Assert.That(background.DisposedWhileActive, Is.False);
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerAlsoClosesAndDrainsAnAlreadyDisplayedUpdateNotification()
    {
        await using Host f = new(); f.Clients[0].Searches[0].Result.SetResult(new(Available(false), true));
        UpdatesDialog dialog = await Notification(f); f.Main.Close();
        await Until(() => !f.Main.IsVisible && f.Main.AutomaticUpdatesCompletion.IsCompleted, f.Main);
        Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.Retirement.IsCompletedSuccessfully, Is.True);
        Assert.That(f.Clients.All(client => client.Disposed && !client.DisposedWhileActive), Is.True); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    public async Task AnExplicitHelpCheckSupersedesTheBackgroundCheckAndUsesTheLatestSavedReleaseCandidateChoice()
    {
        await using Host f = new(ignoreCancellation: true); UpdatesTestClient background = f.Clients.Single();
        f.SetPreferences(f.Store.Load() with { CheckForReleaseCandidates = true });
        MenuItem help = f.Main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "HelpMenu");
        PushProcessTestDriver.Pointer(help); Pump(f.Main);
        PushProcessTestDriver.Pointer(help.Items.OfType<MenuItem>().Single(item => item.Name == "checkForUpdatesToolStripMenuItem"));
        await Until(() => f.Main.OwnedWindows.OfType<UpdatesDialog>().Any(), f.Main);
        UpdatesDialog dialog = f.Main.OwnedWindows.OfType<UpdatesDialog>().Single();
        Assert.That(f.Clients.Count, Is.EqualTo(2)); Assert.That(f.Clients[1].Searches.Single().IncludePrereleases, Is.True);
        Assert.That(background.Searches[0].Token.IsCancellationRequested, Is.True); Assert.That(background.Disposed, Is.False);
        background.Searches[0].Result.SetResult(new(Available(false), true)); await Finish(f);
        Assert.That(background.Disposed, Is.True); Assert.That(f.Clients.Count, Is.EqualTo(2));
        Assert.That(f.Main.OwnedWindows.OfType<UpdatesDialog>().Single(), Is.SameAs(dialog));
        f.Clients[1].Searches[0].Result.SetResult(new(null, false)); await Until(() => dialog.PendingOperation.IsCompleted, dialog);
        dialog.Close(); await Until(() => f.Clients[1].Disposed, f.Main); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    public async Task RealDashboardF5RechecksALongLivedWindowAfterAWeekButNeverDuplicatesAnOutstandingRequest()
    {
        await using Host f = new(); UpdatesTestClient first = f.Clients.Single();
        f.Now = f.Now.AddDays(8); PressF5(f.Main); await Task.Delay(150); Pump(f.Main);
        Assert.That(f.Clients.Count, Is.EqualTo(1), "Even after a week, an outstanding request keeps its single owner.");
        first.Searches[0].Result.SetResult(new(null, false)); await Finish(f);
        PressF5(f.Main); await Until(() => f.Clients.Count == 2, f.Main);
        Assert.That(f.Clients[1].Searches[0].IncludePrereleases, Is.False);
        f.Clients[1].Searches[0].Result.SetResult(new(null, false)); await Finish(f);
        byte[] state = File.ReadAllBytes(f.State.FilePath); PressF5(f.Main); await Task.Delay(150); Pump(f.Main);
        Assert.That(f.Clients.Count, Is.EqualTo(2)); Assert.That(File.ReadAllBytes(f.State.FilePath), Is.EqualTo(state));
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    public async Task TheActualAdvancedApplyActivatesWeeklyChecksAndTheSavedReleaseCandidateChoiceWithoutRestart()
    {
        await using Host f = new(enabledPreference: false);
        MenuItem tools = f.Main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "ToolsMenu");
        PushProcessTestDriver.Pointer(tools); Pump(f.Main);
        PushProcessTestDriver.Pointer(tools.Items.OfType<MenuItem>().Single(item => item.Name == "settingsToolStripMenuItem"));
        await Until(() => f.Main.OwnedWindows.OfType<DesktopSettingsDialog>().Any(), f.Main);
        DesktopSettingsDialog settings = f.Main.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        SettingsNavigationTestDriver.Select(settings, "Advanced");
        AdvancedSettingsUiDriver.Set(settings, "chkCheckForUpdates", true); AdvancedSettingsUiDriver.Set(settings, "chkCheckForRCVersions", true);
        // Opening Settings asynchronously loads its detailed pages. Yield to that
        // work before the real click; pumping synchronously cannot complete it.
        await Until(() => AdvancedSettingsUiDriver.Field<Button>(settings, "ApplyDesktopSettings").IsEnabled, settings);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "ApplyDesktopSettings"));
        await Until(() => f.Clients.Count == 1 && settings.Navigation.IsEnabled, settings);
        Assert.That(f.Store.Load().CheckForUpdates && f.Store.Load().CheckForReleaseCandidates, Is.True);
        Assert.That(f.Clients[0].Searches[0].IncludePrereleases, Is.True); Assert.That(File.Exists(f.State.FilePath), Is.True);
        Assert.That(f.Main.OwnedWindows.OfType<UpdatesDialog>(), Is.Empty, "Applying Settings cannot replace its still-open modal with an update notification.");
        f.Clients[0].Searches[0].Result.SetResult(new(null, false)); await Finish(f);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings"));
        await Until(() => !settings.IsVisible, f.Main); Assert.That(f.Opened, Is.Empty);
    }

    private static void PressF5(Window main)
    { main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None); Pump(main); }

    private static LinuxUpdateRelease Available(bool prerelease)
    {
        string version = prerelease ? "8.0.0-linux-alpha.1" : "8.0.0"; string package = "gitextensions_" + version.Replace('-', '~') + "_amd64.deb";
        return new(LinuxReleaseVersion.Parse(version)!, new(LinuxUpdateClient.RepositoryUrl + "/releases/tag/v" + version),
            new(LinuxUpdateClient.RepositoryUrl + "/releases/download/v" + version + "/" + package), package, 42, new string('a', 64), prerelease);
    }
    private static async Task<UpdatesDialog> Notification(Host f)
    {
        f.Main.Activate(); Pump(f.Main);
        await Until(() => f.Main.OwnedWindows.OfType<UpdatesDialog>().Any(), f.Main);
        UpdatesDialog dialog = f.Main.OwnedWindows.OfType<UpdatesDialog>().Single(); Pump(dialog); return dialog;
    }
    private static async Task Finish(Host f)
    { await Until(() => f.Main.AutomaticUpdatesCompletion.IsCompleted, f.Main); await f.Main.AutomaticUpdatesCompletion; Pump(f.Main); }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "automatic-updates-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _oldCulture = CultureInfo.CurrentUICulture;
        private readonly bool _ignoreCancellation;
        internal MainWindow Main { get; private set; } = null!;
        internal PreferencesStore Store { get; }
        internal UpdateCheckStateStore State { get; }
        internal List<UpdatesTestClient> Clients { get; } = [];
        internal List<string> Opened { get; } = [];
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-20T10:00:00Z", CultureInfo.InvariantCulture);
        internal Host(bool enabledPreference = true, bool includePrereleases = false, bool enable = true, bool ignoreCancellation = false)
        {
            Directory.CreateDirectory(_temporary); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_temporary, "config"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US"); _ignoreCancellation = ignoreCancellation;
            Store = new(); State = new(Path.Combine(_temporary, "state"));
            Store.Save(new() { Language = "en-US", CheckForUpdates = enabledPreference, CheckForReleaseCandidates = includePrereleases });
            Show(enable);
        }
        internal void SetPreferences(DesktopPreferences preferences) => Store.Save(preferences);
        private void Show(bool enable)
        {
            Main = new MainWindow(); Main.ExternalLauncher = info => { Opened.Add(info.ArgumentList.Single()); return null; };
            Main.UpdatesClientFactory = () => { UpdatesTestClient client = new() { IgnoreCancellation = _ignoreCancellation }; Clients.Add(client); return client; };
            if (enable) { Main.EnableAutomaticUpdates(State, () => Now); }
            Main.Show(); Main.Activate(); Pump(Main);
        }
        internal async Task Restart()
        {
            Main.Close(); await Until(() => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted, Main); await Main.WindowWorkCompletion;
            Show(true);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window child in Main.OwnedWindows.ToArray()) { child.Close(); }
                Main.Close(); foreach (UpdatesTestClient client in Clients) { client.CancelPending(); }
                await Until(() => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted && Main.AutomaticUpdatesCompletion.IsCompleted, Main);
                await Main.WindowWorkCompletion; await Main.AutomaticUpdatesCompletion;
                Assert.That(Clients.All(client => client.Disposed && !client.DisposedWhileActive), Is.True);
            }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig); CultureInfo.CurrentUICulture = _oldCulture; if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, true); } }
        }
    }
}
