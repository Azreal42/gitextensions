using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private bool _automaticUpdatesBound;
    private UpdateCheckStateStore? _automaticUpdatesState;
    private Func<DateTimeOffset>? _automaticUpdatesClock;
    private Task _automaticUpdatesCompletion = Task.CompletedTask;
    private CancellationTokenSource? _automaticUpdateCancellation;
    private UpdatesDialog? _updatesDialog;
    internal Task AutomaticUpdatesCompletion => _automaticUpdatesCompletion;

    /// <summary>Production startup opts in explicitly; helpers, Cleanup, smoke and headless construction do not.</summary>
    internal void EnableAutomaticUpdates(UpdateCheckStateStore? state = null, Func<DateTimeOffset>? clock = null)
    {
        if (_automaticUpdatesBound) { return; }
        _automaticUpdatesBound = true;
        _automaticUpdatesState = state ?? new(); _automaticUpdatesClock = clock ?? (() => DateTimeOffset.UtcNow);
        Opened += (_, _) => RequestAutomaticUpdateCheck();
    }

    // Windows retries this admission on revision/dashboard refresh, including a long-lived Browse.
    // The week claim, and one outstanding owner, prevent duplicate network work.
    private void RequestAutomaticUpdateCheck()
    {
        if (!_automaticUpdatesBound || !_opened || !IsVisible || _lifetime.IsCancellationRequested
            || !_automaticUpdatesCompletion.IsCompleted || _updatesDialog is { IsVisible: true }) { return; }
        _automaticUpdatesCompletion = CheckAutomaticUpdatesAsync(_automaticUpdatesState!, _automaticUpdatesClock!);
    }

    private async Task CheckAutomaticUpdatesAsync(UpdateCheckStateStore state, Func<DateTimeOffset> clock)
    {
        using WindowWork work = TrackWindowWork();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _automaticUpdateCancellation = cancellation;
        try
        {
            DesktopPreferences preferences = _preferencesStore.Load();
            if (!preferences.CheckForUpdates || !state.TryClaim(clock(), cancellation.Token)) { return; }
            LinuxUpdateSearch found;
            // The background client retires before UI admission. The dialog receives this result
            // and owns a fresh client for explicit subsequent searches/downloads, without a second GET.
            using (ILinuxUpdateClient client = UpdatesClientFactory())
            { found = await client.FindAsync(LinuxReleaseVersion.Parse(DesktopVersion)!, preferences.CheckForReleaseCandidates, cancellation.Token); }
            if (found.Release is null) { return; }
            while (IsVisible && !cancellation.IsCancellationRequested)
            {
                preferences = _preferencesStore.Load();
                if (!preferences.CheckForUpdates || (!preferences.CheckForReleaseCandidates && (found.Release.IsPrerelease || found.Release.Version.IsPrerelease))) { return; }
                if (IsActive && !_busy && OwnedWindows.Count == 0) { break; }
                await Task.Delay(100, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsVisible) { await ShowUpdatesDialogAsync(preferences.CheckForReleaseCandidates, found, cancellation.Token); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Windows does not interrupt startup for an unsuccessful background search.
            if (!_lifetime.IsCancellationRequested) { AppendOutput("Updates: " + error.Message); }
        }
        finally { if (ReferenceEquals(_automaticUpdateCancellation, cancellation)) { _automaticUpdateCancellation = null; } }
    }

    private async Task ShowUpdatesAsync()
    {
        _automaticUpdateCancellation?.Cancel();
        DesktopPreferences preferences = _preferencesStore.Load();
        await ShowUpdatesDialogAsync(preferences.CheckForReleaseCandidates);
    }

    private async Task ShowUpdatesDialogAsync(bool includePrereleases, LinuxUpdateSearch? initial = null, CancellationToken token = default)
    {
        if (_lifetime.IsCancellationRequested || token.IsCancellationRequested || !IsVisible) { return; }
        if (_updatesDialog is { IsVisible: true } current) { current.Activate(); return; }
        using WindowWork work = TrackWindowWork();
        using ILinuxUpdateClient client = UpdatesClientFactory();
        UpdatesDialog dialog = new(client, DesktopVersion, OpenWebAsync, _translations, includePrereleases, initial) { RequestedThemeVariant = ActualThemeVariant };
        _updatesDialog = dialog;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        using CancellationTokenRegistration close = cancellation.Token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
        try { await dialog.ShowDialog(this); }
        finally
        {
            await dialog.Retirement;
            if (ReferenceEquals(_updatesDialog, dialog)) { _updatesDialog = null; }
        }
    }
}
