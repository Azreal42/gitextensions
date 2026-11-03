using System.Diagnostics;
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
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Category("CrossPlatform")]
public sealed class UpdatesDialogTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task OpeningSearchesWithoutNavigatingAndDistinguishesNoPublicationFromNoNewVersion(bool compatible)
    {
        await using DialogHost f = new();
        Assert.Multiple(() =>
        {
            Assert.That(f.Client.Searches.Count, Is.EqualTo(1));
            Assert.That(f.Client.Searches[0].Current.Text, Is.EqualTo("7.2.1-linux-alpha+installed"));
            Assert.That(f.Client.Searches[0].IncludePrereleases, Is.True);
            Assert.That(Field<ProgressBar>(f.Dialog, "progressBar1").IsVisible, Is.True);
            Assert.That(Field<ProgressBar>(f.Dialog, "progressBar1").IsIndeterminate, Is.True);
            Assert.That(Field<CheckBox>(f.Dialog, "IncludePrereleases").IsEnabled, Is.False);
            Assert.That(Field<Button>(f.Dialog, "btnUpdateNow").IsVisible, Is.False);
            Assert.That(f.Opened, Is.Empty);
        });
        f.Client.Searches[0].Result.SetResult(new(null, compatible)); await Complete(f.Dialog);
        Assert.Multiple(() =>
        {
            Assert.That(Field<TextBlock>(f.Dialog, "UpdateLabel").Text, Is.EqualTo(compatible
                ? "No updates found" : OperatingSystem.IsLinux()
                    ? "No compatible Linux package has been published on the fork releases."
                    : "No compatible package for this system has been published on the fork releases."));
            Assert.That(Field<ProgressBar>(f.Dialog, "progressBar1").IsVisible, Is.False);
            Assert.That(Field<CheckBox>(f.Dialog, "IncludePrereleases").IsEnabled, Is.True);
            Assert.That(Field<Button>(f.Dialog, "linkChangeLog").IsVisible, Is.False);
            Assert.That(Field<Button>(f.Dialog, "linkDirectDownload").IsVisible, Is.False);
            Assert.That(Field<Button>(f.Dialog, "btnUpdateNow").IsVisible, Is.False);
            Assert.That(f.Opened, Is.Empty);
        });
    }

    [AvaloniaTest]
    [TestCase("en-US", false)] [TestCase("en-US", true)]
    [TestCase("fr-FR", false)] [TestCase("fr-FR", true)]
    public async Task AvailableReleaseUsesTheCompactOwnedTranslatedFormAndOnlyRealLinkClicksNavigate(string language, bool dark)
    {
        await using DialogHost f = new(language, dark);
        LinuxUpdateRelease release = Release(); f.Client.Searches[0].Result.SetResult(new(release, true)); await Complete(f.Dialog);
        TextBlock label = Field<TextBlock>(f.Dialog, "UpdateLabel"); Button download = Field<Button>(f.Dialog, "btnUpdateNow");
        Button log = Field<Button>(f.Dialog, "linkChangeLog"), direct = Field<Button>(f.Dialog, "linkDirectDownload");
        Assert.Multiple(() =>
        {
            Assert.That(f.Dialog.Owner, Is.SameAs(f.Owner)); Assert.That(f.Dialog.ShowInTaskbar, Is.False);
            Assert.That(f.Dialog.ActualThemeVariant, Is.EqualTo(f.Owner.ActualThemeVariant));
            Assert.That(f.Dialog.Width, Is.EqualTo(462)); Assert.That(f.Dialog.CanResize, Is.False);
            Assert.That(f.Dialog.ClientSize.Height, Is.InRange(135, 250));
            Assert.That(f.Dialog.Title, Is.EqualTo(language == "fr-FR" ? "Vérifier les mises à jour" : "Check for update"));
            Assert.That(label.Text, Does.StartWith(language == "fr-FR" ? "Une nouvelle version " : "There is a new version "));
            Assert.That(label.Text, Does.Contain(release.Version.Text));
            Assert.That(label.TextLayout.Height, Is.LessThanOrEqualTo(label.Bounds.Height + 1));
            Assert.That(((AccessText)download.Content!).Text, Is.EqualTo(language == "fr-FR" ? "Télécharger la _mise à jour…" : "Download _update…"));
            Assert.That(((AccessText)Field<CheckBox>(f.Dialog, "IncludePrereleases").Content!).Text,
                Is.EqualTo(language == "fr-FR" ? "Inclure les préversions" : "Include pre-releases"));
            Assert.That(download.IsEnabled && log.IsEnabled && direct.IsEnabled, Is.True);
            Assert.That(f.Opened, Is.Empty, "Search completion, focus and layout cannot launch a browser or installer.");
        });
        foreach (Control control in new Control[] { label, download, log, direct })
        {
            Rect bounds = new(control.TranslatePoint(default, f.Dialog)!.Value, control.Bounds.Size);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(bounds), Is.True, control.Name + " must fit the compact window.");
        }
        PushProcessTestDriver.Pointer(label); Assert.That(f.Opened, Is.Empty);
        PushProcessTestDriver.Pointer(log); await Complete(f.Dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { release.ReleaseUri.AbsoluteUri }));
        PushProcessTestDriver.Pointer(direct); await Complete(f.Dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { release.ReleaseUri.AbsoluteUri, release.DownloadUri.AbsoluteUri }));
        Assert.That(f.Client.Downloads, Is.Empty); Assert.That(f.Dialog.IsVisible, Is.True);
        Capture(f.Dialog, $"updates-{language}-{(dark ? "Dark" : "Light")}.png");
    }

    [AvaloniaTest]
    public async Task AReleaseWithoutVerificationOffersItsLinksAndNeverEnablesTheManagedDownload()
    {
        await using DialogHost f = new(); LinuxUpdateRelease release = Release() with { Sha256 = null };
        f.Client.Searches[0].Result.SetResult(new(release, true)); await Complete(f.Dialog);
        Assert.That(Field<TextBlock>(f.Dialog, "UpdateLabel").Text, Does.Contain("Verification information is unavailable. Use Direct Download."));
        Assert.That(Field<Button>(f.Dialog, "btnUpdateNow").IsVisible, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "linkDirectDownload")); await Complete(f.Dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { release.DownloadUri.AbsoluteUri })); Assert.That(f.Client.Downloads, Is.Empty);
    }

    [AvaloniaTest]
    public async Task DownloadProgressAndCompletionNeverOpenThePackageUntilASecondRealClick()
    {
        await using DialogHost f = new(); LinuxUpdateRelease release = Release();
        f.Client.Searches[0].Result.SetResult(new(release, true)); await Complete(f.Dialog);
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "btnUpdateNow"));
        Assert.That(f.Client.Downloads.Count, Is.EqualTo(1)); Assert.That(f.Client.Downloads[0].Release, Is.SameAs(release));
        Assert.That(Field<Button>(f.Dialog, "btnUpdateNow").IsEnabled, Is.False);
        Assert.That(Field<Button>(f.Dialog, "linkDirectDownload").IsEnabled, Is.False);
        f.Client.Downloads[0].Progress!.Report(43);
        await Until(() => Field<ProgressBar>(f.Dialog, "progressBar1").Value == 43, f.Dialog);
        Assert.That(Field<ProgressBar>(f.Dialog, "progressBar1").IsIndeterminate, Is.False); Assert.That(f.Opened, Is.Empty);
        string package = Path.Combine(Path.GetTempPath(), "update cache", release.FileName);
        f.Client.Downloads[0].Result.SetResult(package); await Complete(f.Dialog);
        Assert.Multiple(() =>
        {
            Assert.That(Field<ProgressBar>(f.Dialog, "progressBar1").IsVisible, Is.False);
            Assert.That(Field<TextBlock>(f.Dialog, "UpdateLabel").Text, Does.Contain(package));
            Assert.That(((AccessText)Field<Button>(f.Dialog, "btnUpdateNow").Content!).Text, Is.EqualTo("_Open package…"));
            Assert.That(f.Opened, Is.Empty, "A completed download cannot implicitly launch installation.");
        });
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "btnUpdateNow")); await Complete(f.Dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { package })); Assert.That(f.Client.Downloads.Count, Is.EqualTo(1));
        Assert.That(f.Dialog.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task ChangingThePrereleaseChoiceStartsAnotherSearchAndDropsTheOldAvailableRelease()
    {
        await using DialogHost f = new(current: "7.2.1");
        Assert.That(f.Client.Searches[0].IncludePrereleases, Is.False);
        f.Client.Searches[0].Result.SetResult(new(Release(), true)); await Complete(f.Dialog);
        PushProcessTestDriver.Pointer(Field<CheckBox>(f.Dialog, "IncludePrereleases"));
        Assert.That(f.Client.Searches.Count, Is.EqualTo(2)); Assert.That(f.Client.Searches[1].IncludePrereleases, Is.True);
        Assert.That(Field<Button>(f.Dialog, "linkDirectDownload").IsVisible, Is.False);
        Assert.That(Field<Button>(f.Dialog, "btnUpdateNow").IsVisible, Is.False); Assert.That(f.Opened, Is.Empty);
        f.Client.Searches[1].Result.SetResult(new(null, true)); await Complete(f.Dialog);
        Assert.That(Field<TextBlock>(f.Dialog, "UpdateLabel").Text, Is.EqualTo("No updates found")); Assert.That(f.Client.Downloads, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AFailedSearchOrDownloadShowsAnOwnedErrorAndCanBeRetriedByPointer(bool downloading)
    {
        await using DialogHost f = new();
        if (downloading)
        {
            f.Client.Searches[0].Result.SetResult(new(Release(), true)); await Complete(f.Dialog);
            PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "btnUpdateNow"));
            f.Client.Downloads[0].Result.SetException(new IOException("The test package checksum did not match."));
        }
        else { f.Client.Searches[0].Result.SetException(new IOException("The test release service is unavailable.")); }
        await DismissError(f.Dialog, downloading ? "The test package" : "The test release"); await Complete(f.Dialog);
        Button retry = Field<Button>(f.Dialog, "btnUpdateNow");
        Assert.That(((AccessText)retry.Content!).Text, Is.EqualTo("_Retry")); Assert.That(retry.IsEnabled, Is.True);
        PushProcessTestDriver.Pointer(retry);
        if (downloading) { Assert.That(f.Client.Downloads.Count, Is.EqualTo(2)); f.Client.Downloads[1].Result.SetResult("/tmp/retried.deb"); }
        else { Assert.That(f.Client.Searches.Count, Is.EqualTo(2)); f.Client.Searches[1].Result.SetResult(new(null, true)); }
        await Complete(f.Dialog); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Opened, Is.Empty);
    }

    [AvaloniaTest]
    public async Task LinkFailureRetryWithMissingDigestRepeatsTheFailedLinkAndNeverStartsAPackageDownload()
    {
        await using DialogHost f = new(); LinuxUpdateRelease release = Release() with { Sha256 = null };
        f.Client.Searches[0].Result.SetResult(new(release, true)); await Complete(f.Dialog);
        f.OpenFailure = new IOException("The test browser launcher is unavailable.");
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "linkChangeLog"));
        await DismissError(f.Dialog, "The test browser launcher"); await Complete(f.Dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { release.ReleaseUri.AbsoluteUri }));
        f.OpenFailure = null; PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "btnUpdateNow")); await Complete(f.Dialog);
        Assert.That(f.Opened, Is.EqualTo(new[] { release.ReleaseUri.AbsoluteUri, release.ReleaseUri.AbsoluteUri }));
        Assert.That(f.Client.Downloads, Is.Empty); Assert.That(f.Client.Searches.Count, Is.EqualTo(1));
        Assert.That(Field<Button>(f.Dialog, "btnUpdateNow").IsVisible, Is.False); Assert.That(f.Dialog.IsVisible, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false, false)] [TestCase(false, true)]
    [TestCase(true, false)] [TestCase(true, true)]
    public async Task ClosingTheDialogOrOwnerCancelsAndDrainsAnActiveSearchOrDownload(bool downloading, bool closeOwner)
    {
        await using DialogHost f = new();
        if (downloading)
        {
            f.Client.Searches[0].Result.SetResult(new(Release(), true)); await Complete(f.Dialog);
            PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "btnUpdateNow"));
        }
        CancellationToken token = downloading ? f.Client.Downloads[0].Token : f.Client.Searches[0].Token;
        if (closeOwner) { f.Owner.Close(); } else { PressEscape(f.Dialog); }
        await Until(() => !f.Dialog.IsVisible && f.Dialog.Retirement.IsCompleted, f.Owner); await f.Dialog.Retirement;
        Assert.That(token.IsCancellationRequested, Is.True); Assert.That(f.Client.ActiveCalls, Is.Zero);
        Assert.That(f.Owner.OwnedWindows, Is.Empty); Assert.That(f.Dialog.OwnedWindows, Is.Empty);
        Assert.That(f.Opened, Is.Empty); Assert.That(f.Owner.IsVisible, Is.EqualTo(!closeOwner));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AClientIgnoringCancellationIsStillDrainedAndItsLateResultCannotChangeTheClosedDialog(bool downloading)
    {
        await using DialogHost f = new(ignoreCancellation: true);
        if (downloading)
        {
            f.Client.Searches[0].Result.SetResult(new(Release(), true)); await Complete(f.Dialog);
            PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "btnUpdateNow"));
        }
        string? before = Field<TextBlock>(f.Dialog, "UpdateLabel").Text;
        f.Dialog.Close(); Pump(f.Owner);
        Assert.That(f.Dialog.Retirement.IsCompleted, Is.False, "Closing cannot claim the outstanding client call has ended.");
        Assert.That(f.Client.ActiveCalls, Is.EqualTo(1));
        if (downloading) { f.Client.Downloads[0].Result.SetResult("/tmp/late-package.deb"); }
        else { f.Client.Searches[0].Result.SetResult(new(Release(), true)); }
        await Until(() => f.Dialog.Retirement.IsCompleted, f.Owner); await f.Dialog.Retirement;
        Assert.That(f.Client.ActiveCalls, Is.Zero); Assert.That(f.Dialog.IsVisible, Is.False);
        Assert.That(Field<TextBlock>(f.Dialog, "UpdateLabel").Text, Is.EqualTo(before)); Assert.That(f.Opened, Is.Empty);
    }

    internal static LinuxUpdateRelease Release() => new(LinuxReleaseVersion.Parse("8.0.0-linux-alpha.1")!,
        new Uri("https://github.com/Azreal42/gitextensions/releases/tag/v8.0.0-linux-alpha.1"),
        new Uri("https://github.com/Azreal42/gitextensions/releases/download/v8.0.0-linux-alpha.1/gitextensions_8.0.0~linux-alpha.1_amd64.deb"),
        "gitextensions_8.0.0~linux-alpha.1_amd64.deb", 42, new string('a', 64));
    internal static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    internal static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    internal static async Task Until(Func<bool> condition, TopLevel root)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { Pump(root); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The update window transition must complete within five seconds."); Pump(root);
    }
    private static async Task Complete(UpdatesDialog dialog)
    { await Until(() => dialog.PendingOperation.IsCompleted, dialog); await dialog.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5)); Pump(dialog); }
    private static async Task DismissError(UpdatesDialog dialog, string message)
    {
        await Until(() => dialog.OwnedWindows.Any(window => window.Name == "MessageDialog"), dialog);
        Window error = dialog.OwnedWindows.Single(); Assert.That(error.Owner, Is.SameAs(dialog));
        Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Does.StartWith(message));
        PushProcessTestDriver.Pointer(Field<Button>(error, "MessageDialogAccept"));
    }
    private static void PressEscape(Window window)
    { window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); } Pump(window); }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class DialogHost : IAsyncDisposable
    {
        internal Window Owner { get; }
        internal UpdatesDialog Dialog { get; }
        internal UpdatesTestClient Client { get; }
        internal List<string> Opened { get; } = [];
        internal Exception? OpenFailure { get; set; }
        private readonly Task _modal;
        internal DialogHost(string language = "en-US", bool dark = false, string current = "7.2.1-linux-alpha+installed", bool ignoreCancellation = false)
        {
            Client = new() { IgnoreCancellation = ignoreCancellation };
            Owner = new() { Width = 900, Height = 600, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Owner.Show();
            TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            Dialog = new(Client, current, target => { Opened.Add(target); return OpenFailure is { } error ? Task.FromException(error) : Task.CompletedTask; }, translations)
                { RequestedThemeVariant = Owner.RequestedThemeVariant };
            _modal = Dialog.ShowDialog(Owner); Pump(Dialog);
        }
        public async ValueTask DisposeAsync()
        {
            Dialog.Close(); Client.CancelPending(); await Until(() => Dialog.Retirement.IsCompleted && _modal.IsCompleted, Owner);
            await Dialog.Retirement; await _modal; Client.Dispose(); Owner.Close(); Pump(Owner);
        }
    }
}

/// <summary>Controlled asynchronous boundaries; no HTTP, Git command, package file or external launcher is used.</summary>
internal sealed class UpdatesTestClient : ILinuxUpdateClient
{
    internal sealed record Search(LinuxReleaseVersion Current, bool IncludePrereleases, CancellationToken Token, TaskCompletionSource<LinuxUpdateSearch> Result);
    internal sealed record Download(LinuxUpdateRelease Release, IProgress<int>? Progress, CancellationToken Token, TaskCompletionSource<string> Result);
    internal bool IgnoreCancellation { get; init; }
    internal List<Search> Searches { get; } = [];
    internal List<Download> Downloads { get; } = [];
    internal int ActiveCalls { get; private set; }
    internal bool Disposed { get; private set; }
    internal bool DisposedWhileActive { get; private set; }
    public Task<LinuxUpdateSearch> FindAsync(LinuxReleaseVersion current, bool includePrereleases, CancellationToken token)
    {
        TaskCompletionSource<LinuxUpdateSearch> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Searches.Add(new(current, includePrereleases, token, result)); return Track(result.Task, token);
    }
    public Task<string> DownloadAsync(LinuxUpdateRelease release, IProgress<int>? progress, CancellationToken token)
    {
        TaskCompletionSource<string> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Downloads.Add(new(release, progress, token, result)); return Track(result.Task, token);
    }
    private async Task<T> Track<T>(Task<T> pending, CancellationToken token)
    {
        ActiveCalls++;
        try { return await (IgnoreCancellation ? pending : pending.WaitAsync(token)); }
        finally { ActiveCalls--; }
    }
    internal void CancelPending()
    {
        foreach (Search call in Searches) { call.Result.TrySetCanceled(); }
        foreach (Download call in Downloads) { call.Result.TrySetCanceled(); }
    }
    public void Dispose() { DisposedWhileActive |= ActiveCalls != 0; Disposed = true; }
}
