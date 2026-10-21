using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GitCommands;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // Frontier of already-started RunAsync leases, never WindowWorkCompletion
    // (which also contains independent sessions and notification observers).
    private Task _repositoryOperationCompletion = Task.CompletedTask;

    private RepositoryChangeContext? _repositoryChanges;
    private GitRepository? _repositoryChangeSource;
    private bool _repositoryChangePublicationPending;
    private Task _repositoryChangePublication = Task.CompletedTask;

    private MainWindow? ModelessRoot => (ModelessApplicationLifetime
        ?? Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    private RepositoryChangeContext? RepositoryChangesFor(GitRepository repository)
    {
        if (IsStandaloneDiff)
        { return ReferenceEquals(_repositoryChangeSource, repository) ? _repositoryChanges : null; }
        if (!ReferenceEquals(_repository, repository)) { return null; }
        Window? owner = Owner as Window;
        if (_repositoryChanges is null || !_repositoryChanges.BelongsTo(this, repository, owner))
        { _repositoryChanges = new(this, repository, owner, (ModelessRoot ?? this)._lifetime.Token); }
        return _repositoryChanges;
    }

    private void PublishStandaloneRepositoryChangeWhenIdle(GitRepository repository)
    {
        if (_repositoryChangePublicationPending || RepositoryChangesFor(repository) is not { } context) { return; }
        _repositoryChangePublicationPending = true;
        _repositoryChangePublication = context.ObserveWhenIdleAsync(this);
    }

    // A GitUICommands notification context belongs to one Browse, not to a
    // filesystem path or the independent window which happens to be active.
    // Sources retain it after Log closes, as Windows retains GitUICommands.
    private sealed class RepositoryChangeContext
    {
        private readonly WeakReference<MainWindow> _receiver;
        private readonly GitRepository _repository;
        private readonly Window? _owner;
        private readonly CancellationToken _rootLifetime;
        private readonly ActionNotifier _notifier;
        private bool _notifyRequested;
        internal Task PublicationCompletion { get; private set; } = Task.CompletedTask;
        internal bool HasPendingNotification => _notifyRequested;

        internal RepositoryChangeContext(MainWindow receiver, GitRepository repository, Window? owner, CancellationToken rootLifetime)
        {
            _receiver = new(receiver); _repository = repository; _owner = owner; _rootLifetime = rootLifetime;
            _notifier = new(() =>
            {
                _notifyRequested = false;
                if (_receiver.TryGetTarget(out MainWindow? current))
                { PublicationCompletion = Task.WhenAll(PublicationCompletion, current.RefreshAfterRepositoryNotificationAsync(_repository, _owner, _rootLifetime)); }
            });
        }
        internal bool BelongsTo(MainWindow receiver, GitRepository repository, Window? owner)
            => _receiver.TryGetTarget(out MainWindow? original) && ReferenceEquals(original, receiver)
                && ReferenceEquals(_repository, repository) && ReferenceEquals(_owner, owner);
        internal void Lock() => _notifier.Lock();
        internal void UnLock() => _notifier.UnLock(requestNotify: false);

        internal async Task ObserveWhenIdleAsync(MainWindow source)
        {
            // Acquire synchronously at admission, before a modal Log can unlock.
            // The source's own lock holds a pending notification through its real
            // drain even if Log returns first; completed sources coalesce in Log.
            _notifier.Lock();
            _notifyRequested = true;
            _notifier.Notify();
            WindowWork? work = _receiver.TryGetTarget(out MainWindow? receiver) ? receiver.TrackWindowWork() : null;
            try
            {
                // Never track this observer on source: that would await itself.
                // An admitted effect may finish after its source window closes.
                // A refresh may already have admitted another background history reader.
                // Keep mutation/panel/child drainage, independently of that reader's EOF.
                while (!source.RepositoryMutationCompletion.IsCompleted) { await source.RepositoryMutationCompletion; }
            }
            catch (Exception error)
            {
                if (_receiver.TryGetTarget(out MainWindow? current))
                { current.AppendOutput("Repository notification: " + error.Message); }
            }
            finally
            {
                source._repositoryChangePublicationPending = false;
                try { _notifier.UnLock(requestNotify: false); }
                finally { work?.Dispose(); }
            }
        }
    }

    private async Task RefreshAfterRepositoryNotificationAsync(GitRepository repository, Window? originalOwner, CancellationToken rootLifetime)
    {
        bool IsCurrent() => WindowKind == MainWindowKind.Browse && IsVisible && !_commitInfoClosing
            && !_lifetime.IsCancellationRequested && !rootLifetime.IsCancellationRequested
            && ReferenceEquals(_repository, repository) && ReferenceEquals(Owner as Window, originalOwner)
            && (originalOwner is null || originalOwner.IsVisible);
        if (!IsCurrent()) { return; }
        // The notification observer and the receiving Browse own this wait.
        // Closing either window cancels it before a new refresh can be admitted.
        using WindowWork receipt = TrackWindowWork();
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, rootLifetime);
        try
        {
            while (IsCurrent() && !_repositoryOperationCompletion.IsCompleted)
            {
                // Only already-started RunAsync leases are awaited. Production
                // bodies/finally do not await this notification observer; a modal Log
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
                // application root's cancellation while this refresh owns CTS.
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
