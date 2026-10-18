using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private DesktopStartupArguments? _startupArgumentsToRetry;
    private string? _startupDirectoryToRetry;

    private async Task CheckStartupEnvironmentAsync()
    {
        if (_lifetime.IsCancellationRequested || !IsVisible) { return; }
        using GitEnvironmentSettingsSession scan = new(new GitEnvironmentPreferencesStore(), _translations, () => _repository, _lifetime.Token);
        // A window can close while a startup scan is still draining its native probes.
        using WindowWork work = TrackWindowWork();
        await scan.ScanAsync();
        if (!_lifetime.IsCancellationRequested && IsVisible && scan.HasProblems)
        { await ShowSettingsPageAsync(showChecklist: true); }
    }

    private async Task RetryStartupRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_startupArgumentsToRetry is not { } arguments || _lifetime.IsCancellationRequested) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable operation = GitEnvironment.BeginOperation();
        DesktopStartupRequest prepared = await DesktopStartupDispatcher.PrepareAsync(arguments, _startupDirectoryToRetry ?? Environment.CurrentDirectory, cancellationToken);
        BrowseRequest request = prepared.BrowseRequest ?? throw new InvalidOperationException("The deferred Browse request could not be prepared.");
        bool wasFileHistory = IsFileHistoryBrowse;
        _browseRequest = request; _browseSelectionApplied = false;
        if (!wasFileHistory && IsFileHistoryBrowse) { InitializeFileHistoryBrowse(); }
        await OpenRepositoryAsync(request.RepositoryPath ?? throw new InvalidOperationException("The deferred Browse repository is unavailable."));
        cancellationToken.ThrowIfCancellationRequested();
        if (_repository is null) { throw LastException ?? new InvalidOperationException("The deferred repository could not be opened."); }
        _startupArgumentsToRetry = null; _startupDirectoryToRetry = null;
    }
}
