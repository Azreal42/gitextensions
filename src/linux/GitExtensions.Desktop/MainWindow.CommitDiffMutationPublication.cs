using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // Frontier of already-started RunAsync leases, never WindowWorkCompletion
    // (which also contains the independent CommitDiff session returning here).
    private Task _repositoryOperationCompletion = Task.CompletedTask;

    private async Task RefreshAfterModelessCommitDiffAsync(GitRepository repository, Window? originalOwner, CancellationToken rootLifetime)
    {
        bool IsCurrent() => WindowKind == MainWindowKind.Browse && IsVisible && !_commitInfoClosing
            && !_lifetime.IsCancellationRequested && !rootLifetime.IsCancellationRequested
            && ReferenceEquals(_repository, repository) && ReferenceEquals(Owner as Window, originalOwner)
            && (originalOwner is null || originalOwner.IsVisible);
        if (!IsCurrent()) { return; }
        // Both the root's Retire lease and the receiving Browse own this wait.
        // Closing either window cancels it before a new refresh can be admitted.
        using WindowWork receipt = TrackWindowWork();
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, rootLifetime);
        try
        {
            while (IsCurrent() && !_repositoryOperationCompletion.IsCompleted)
            {
                // Only already-started RunAsync leases are awaited. Production
                // bodies/finally do not await this modeless Retire; a modal Log
                // or CommitDiff waits its own child's work, not the root's leases.
                Task previous = _repositoryOperationCompletion;
                await previous.WaitAsync(wait.Token);
            }
            if (!IsCurrent()) { return; }
            // No await between the final admission and RunAsync: another UI
            // operation cannot claim _busy in between and silently drop receipt.
            await RunAsync("Refresh after commit diff", async current =>
            {
                if (!IsCurrent() || !ReferenceEquals(current, repository)) { return; }
                // RunAsync already links the receiver lifetime. Add only the
                // independent root's cancellation while this refresh owns CTS.
                CancellationTokenSource operation = _operation!;
                using CancellationTokenRegistration rootClose = rootLifetime.Register(operation.Cancel);
                wait.Token.ThrowIfCancellationRequested();
                await RefreshDataAsync(current);
            }, refresh: false);
            // We never await _repositoryOperationCompletion again after starting
            // our own refresh. The child's May receipt remains true on failure.
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
    }
}
