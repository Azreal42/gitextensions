using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public static class RevertSequence
{
    /// <summary>Original ascending grid order; Cancel skips this dialog, while pending conflicts stop the next mutation.</summary>
    public static async Task<bool> ShowAsync(Window owner, GitRepository repository, IReadOnlyList<string> revisionsOldestFirst,
        CancellationToken cancellationToken = default,
        Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(repository); ArgumentNullException.ThrowIfNull(revisionsOldestFirst);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OwnerClosed(object? sender, EventArgs args) => lifetime.Cancel();
        void OwnerClosing(object? sender, WindowClosingEventArgs args) => lifetime.Cancel();
        owner.Closed += OwnerClosed; owner.Closing += OwnerClosing;
        bool changed = false;
        try
        {
            IReadOnlyList<GitRevertRevision> revisions = await repository.PrepareRevertSelectionAsync(revisionsOldestFirst, lifetime.Token);
            foreach (GitRevertRevision revision in revisions)
            {
                lifetime.Token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { break; }
                RevertDialog dialog = new(repository, revision.Commit.Hash, lifetime.Token) { RequestedThemeVariant = owner.ActualThemeVariant };
                await dialog.InitializeAsync(); lifetime.Token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { break; }
                if (dialog.LastError is not null) { throw dialog.LastException ?? new InvalidOperationException(dialog.LastError); }
                using CancellationTokenRegistration cancellation = lifetime.Token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(false); } }));
                await dialog.ShowDialog<bool>(owner); changed |= dialog.HasRepositoryChanges;
                lifetime.Token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { break; }
                if ((await repository.GetStatusAsync(lifetime.Token)).Changes.Any(change => change.IsConflict))
                {
                    if (resolveConflictsAsync is null) { break; }
                    await resolveConflictsAsync(owner, repository, dialog.AutoCommit, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { break; }
                    if ((await repository.GetStatusAsync(lifetime.Token)).Changes.Any(change => change.IsConflict)) { break; }
                }
            }
            return changed;
        }
        finally { owner.Closed -= OwnerClosed; owner.Closing -= OwnerClosing; }
    }
}
