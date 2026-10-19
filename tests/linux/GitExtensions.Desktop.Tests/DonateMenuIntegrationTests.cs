using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DonateMenuIntegrationTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task HelpDonateOpensTheOwnedOriginalDialogAndOnlyItsBadgeInvokesTheCapturedLauncher(bool dark)
    {
        await using MenuHost f = new(dark);
        DonateDialog donate = await OpenDonateAsync(f.Main);
        Assert.Multiple(() =>
        {
            Assert.That(donate.Owner, Is.SameAs(f.Main)); Assert.That(donate.ShowInTaskbar, Is.False);
            Assert.That(donate.ActualThemeVariant, Is.EqualTo(f.Main.ActualThemeVariant));
            Assert.That(f.Launches, Is.Empty, "Opening Help and Donate must not open a browser.");
            Assert.That(donate.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1));
        });
        PushProcessTestDriver.Pointer(Field<Button>(donate, "pbxDonate")); Pump(donate);
        Assert.That(f.Launches.Count, Is.EqualTo(1));
        Assert.That(f.Launches[0].FileName, Is.EqualTo("xdg-open"));
        Assert.That(f.Launches[0].UseShellExecute, Is.False);
        Assert.That(f.Launches[0].ArgumentList, Is.EqualTo(new[] { "https://opencollective.com/gitextensions" }));
        Assert.That(donate.IsVisible, Is.True); PressEscape(donate);
        await UntilAsync(() => !donate.IsVisible && f.Main.OwnedWindows.Count == 0, f.Main);
        Assert.That(f.Main.IsVisible, Is.True); Assert.That(f.Main.LastException, Is.Null); Assert.That(f.Launches.Count, Is.EqualTo(1));
        // A completed menu action must leave Help usable, with no late browser invocation.
        DonateDialog reopened = await OpenDonateAsync(f.Main); Assert.That(reopened, Is.Not.SameAs(donate));
        Assert.That(f.Launches.Count, Is.EqualTo(1)); PressEscape(reopened);
        await UntilAsync(() => !reopened.IsVisible, f.Main); Assert.That(f.Main.IsVisible, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DonationLauncherFailureShowsOneOwnedErrorAndKeepsDonateAndBrowseUsable(bool dark)
    {
        await using MenuHost f = new(dark);
        f.Main.ExternalLauncher = info => { f.Launches.Add(info); throw new InvalidOperationException("The test browser launcher is unavailable."); };
        DonateDialog donate = await OpenDonateAsync(f.Main); Assert.That(f.Launches, Is.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(donate, "pbxDonate"));
        await UntilAsync(() => donate.OwnedWindows.Any(window => window.Name == "MessageDialog"), donate);
        Window error = donate.OwnedWindows.Single(window => window.Name == "MessageDialog"); Pump(error);
        Assert.Multiple(() =>
        {
            Assert.That(error.Owner, Is.SameAs(donate)); Assert.That(error.ActualThemeVariant, Is.EqualTo(donate.ActualThemeVariant));
            Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Is.EqualTo("The test browser launcher is unavailable."));
            Assert.That(error.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1));
            Assert.That(donate.IsVisible, Is.True); Assert.That(f.Main.IsVisible, Is.True); Assert.That(f.Launches.Count, Is.EqualTo(1));
        });
        PushProcessTestDriver.Pointer(Field<Button>(error, "MessageDialogAccept"));
        await UntilAsync(() => !error.IsVisible, donate); Assert.That(donate.IsVisible, Is.True);
        PressEscape(donate); await UntilAsync(() => !donate.IsVisible && f.Main.OwnedWindows.Count == 0, f.Main);
        Assert.That(f.Main.IsVisible, Is.True); Assert.That(f.Launches.Count, Is.EqualTo(1));
    }

    private static async Task<DonateDialog> OpenDonateAsync(MainWindow main)
    {
        MenuItem help = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "HelpMenu");
        PushProcessTestDriver.Pointer(help); Pump(main);
        MenuItem action = help.Items.OfType<MenuItem>().Single(item => item.Name == "donateToolStripMenuItem");
        PushProcessTestDriver.Pointer(action);
        await UntilAsync(() => main.OwnedWindows.OfType<DonateDialog>().Any(), main);
        DonateDialog donate = main.OwnedWindows.OfType<DonateDialog>().Single(); Pump(donate); return donate;
    }
    private static T Field<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void PressEscape(Window window) { window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task UntilAsync(Func<bool> condition, TopLevel root)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { Pump(root); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real modal/menu transition must complete within five seconds.");
    }

    /// <summary>A real dashboard window and an empty private profile; no repository fixture or Git command is needed.</summary>
    private sealed class MenuHost : IAsyncDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "donate-menu-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _oldCulture = CultureInfo.CurrentUICulture;
        internal MainWindow Main { get; }
        internal List<ProcessStartInfo> Launches { get; } = [];
        internal MenuHost(bool dark)
        {
            Assert.That(Program.RepositoryPath, Is.Null, "This fixture intentionally starts Browse without a repository.");
            Directory.CreateDirectory(_temporary); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _temporary);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                Main = new MainWindow(); Main.ExternalLauncher = info => { Launches.Add(info); return null; };
                Main.Show(); Main.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Pump(Main);
            }
            catch { RestoreProfile(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window child in Main.OwnedWindows.ToArray()) { child.Close(); }
                Main.Close(); await UntilAsync(() => !Main.IsVisible && Main.CommitInformationCompletion.IsCompleted, Main);
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
