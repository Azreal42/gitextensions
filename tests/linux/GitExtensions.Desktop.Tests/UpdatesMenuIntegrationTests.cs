using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.UpdatesDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class UpdatesMenuIntegrationTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task HelpUpdatesOpensTheOwnedFormWithoutLaunchingABrowserAndDisposesTheClientOnlyAfterRetirement(bool dark)
    {
        await using MenuHost f = new(dark);
        UpdatesDialog dialog = await OpenUpdates(f.Main); UpdatesTestClient client = f.Clients.Single();
        Assert.Multiple(() =>
        {
            Assert.That(dialog.Owner, Is.SameAs(f.Main)); Assert.That(dialog.ShowInTaskbar, Is.False);
            Assert.That(dialog.ActualThemeVariant, Is.EqualTo(f.Main.ActualThemeVariant));
            Assert.That(client.Searches.Count, Is.EqualTo(1)); Assert.That(client.Disposed, Is.False);
            Assert.That(f.Launches, Is.Empty, "Opening the menu and update dialog cannot launch a browser.");
        });
        LinuxUpdateRelease release = Release(); client.Searches[0].Result.SetResult(new(release, true));
        await Until(() => dialog.PendingOperation.IsCompleted, dialog); await dialog.PendingOperation;
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "linkChangeLog"));
        await Until(() => dialog.PendingOperation.IsCompleted, dialog); await dialog.PendingOperation;
        Assert.That(f.Launches.Count, Is.EqualTo(1));
        Assert.That(f.Launches[0].FileName, Is.EqualTo("xdg-open")); Assert.That(f.Launches[0].UseShellExecute, Is.False);
        Assert.That(f.Launches[0].ArgumentList, Is.EqualTo(new[] { release.ReleaseUri.AbsoluteUri }));
        dialog.Close(); await Until(() => client.Disposed && f.Main.OwnedWindows.Count == 0, f.Main);
        Assert.That(dialog.Retirement.IsCompletedSuccessfully, Is.True); Assert.That(client.DisposedWhileActive, Is.False);
        Assert.That(f.Main.IsVisible, Is.True); Assert.That(f.Main.LastException, Is.Null);
        UpdatesDialog reopened = await OpenUpdates(f.Main);
        Assert.That(reopened, Is.Not.SameAs(dialog)); Assert.That(f.Clients.Count, Is.EqualTo(2));
        Assert.That(f.Launches.Count, Is.EqualTo(1)); reopened.Close();
        await Until(() => f.Clients[1].Disposed && f.Main.OwnedWindows.Count == 0, f.Main);
        Assert.That(f.Clients[1].Searches[0].Token.IsCancellationRequested, Is.True);
        Assert.That(f.Clients[1].DisposedWhileActive, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingUpdatesOrItsOwnerWaitsForATokenIgnoringClientBeforeDisposal(bool closeOwner)
    {
        await using MenuHost f = new(false, ignoreCancellation: true);
        UpdatesDialog dialog = await OpenUpdates(f.Main); UpdatesTestClient client = f.Clients.Single();
        if (closeOwner) { f.Main.Close(); } else { dialog.Close(); }
        Pump(f.Main);
        Assert.That(dialog.IsVisible, Is.False); Assert.That(client.Searches[0].Token.IsCancellationRequested, Is.True);
        Assert.That(client.Disposed, Is.False); Assert.That(client.ActiveCalls, Is.EqualTo(1));
        client.Searches[0].Result.SetResult(new(Release(), true));
        await Until(() => client.Disposed, f.Main);
        Assert.That(client.DisposedWhileActive, Is.False); Assert.That(client.ActiveCalls, Is.Zero);
        Assert.That(dialog.Retirement.IsCompletedSuccessfully, Is.True); Assert.That(f.Launches, Is.Empty);
        Assert.That(f.Main.OwnedWindows, Is.Empty); Assert.That(f.Main.LastException, Is.Null);
        Assert.That(f.Main.IsVisible, Is.EqualTo(!closeOwner));
    }

    private static async Task<UpdatesDialog> OpenUpdates(MainWindow main)
    {
        MenuItem help = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "HelpMenu");
        PushProcessTestDriver.Pointer(help); Pump(main);
        MenuItem action = help.Items.OfType<MenuItem>().Single(item => item.Name == "checkForUpdatesToolStripMenuItem");
        Assert.That(action.Icon, Is.Not.Null); PushProcessTestDriver.Pointer(action);
        await Until(() => main.OwnedWindows.OfType<UpdatesDialog>().Any(), main);
        UpdatesDialog dialog = main.OwnedWindows.OfType<UpdatesDialog>().Single(); Pump(dialog); return dialog;
    }

    private sealed class MenuHost : IAsyncDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "updates-menu-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _oldCulture = CultureInfo.CurrentUICulture;
        internal MainWindow Main { get; }
        internal List<ProcessStartInfo> Launches { get; } = [];
        internal List<UpdatesTestClient> Clients { get; } = [];
        internal MenuHost(bool dark, bool ignoreCancellation = false)
        {
            Assert.That(Program.RepositoryPath, Is.Null, "This fixture starts Browse without a repository or Git process.");
            Directory.CreateDirectory(_temporary); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _temporary);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                Main = new MainWindow(); Main.ExternalLauncher = info => { Launches.Add(info); return null; };
                Main.UpdatesClientFactory = () => { UpdatesTestClient client = new() { IgnoreCancellation = ignoreCancellation }; Clients.Add(client); return client; };
                Main.Show(); Main.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Pump(Main);
            }
            catch { RestoreProfile(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window child in Main.OwnedWindows.ToArray()) { child.Close(); }
                foreach (UpdatesTestClient client in Clients) { client.CancelPending(); }
                await Until(() => Clients.All(client => client.Disposed), Main);
                Main.Close(); await Until(() => !Main.IsVisible && Main.CommitInformationCompletion.IsCompleted, Main);
                try { await Main.CommitInformationCompletion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
                Pump(Main);
            }
            finally { RestoreProfile(); }
        }
        private void RestoreProfile()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig); CultureInfo.CurrentUICulture = _oldCulture;
            if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, recursive: true); }
        }
    }
}
