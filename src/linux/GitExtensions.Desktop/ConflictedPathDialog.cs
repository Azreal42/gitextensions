using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>Routes an actual unmerged path to its file or gitlink recovery workflow.</summary>
internal static class ConflictedPathDialog
{
    internal static async Task<bool> ShowAsync(Window owner, GitRepository repository, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return false; }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        RepositoryDialog? dialog = null;
        Task? initialization = null;
        bool ownerCloseRequested = false;
        bool finished = false;
        void CloseChild()
        {
            if (finished || dialog is null) { return; }
            CloseOwned(dialog);
        }
        void OwnerClosing(object? sender, WindowClosingEventArgs args)
        {
            ownerCloseRequested = true;
            lifetime.Cancel();
            CloseChild();
        }
        void OwnerClosed(object? sender, EventArgs args) { lifetime.Cancel(); CloseChild(); }
        owner.Closing += OwnerClosing;
        owner.Closed += OwnerClosed;
        using CancellationTokenRegistration cancellation = lifetime.Token.Register(() => Dispatcher.UIThread.Post(CloseChild));
        try
        {
            GitConflictPath conflict = (await repository.GetConflictsAsync(lifetime.Token)).SingleOrDefault(item => item.Path == path)
                ?? throw new InvalidOperationException("The selected path is no longer conflicted.");
            lifetime.Token.ThrowIfCancellationRequested();
            if (!owner.IsVisible) { return false; }
            dialog = conflict.Stages.Any(stage => stage.Mode == "160000")
                ? new SubmoduleConflictDialog(repository, path)
                : new PatchConflictDialog(repository, path);
            dialog.RequestedThemeVariant = owner.ActualThemeVariant;
            dialog.ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
            dialog.Opened += (_, _) => initialization = dialog.InitializeAsync();
            await dialog.ShowDialog(owner);
            // A cancelled command may already have changed Git. Return those effects
            // before the caller observes its token and refreshes the pending operation.
            return dialog.HasRepositoryChanges;
        }
        finally
        {
            try
            {
                CloseChild();
                if (initialization is not null) { await initialization; }
            }
            finally
            {
                finished = true;
                owner.Closing -= OwnerClosing;
                owner.Closed -= OwnerClosed;
                if (ownerCloseRequested && owner.IsVisible) { Dispatcher.UIThread.Post(owner.Close); }
            }
        }
    }

    private static void CloseOwned(Window window)
    {
        foreach (Window child in window.OwnedWindows.ToArray()) { CloseOwned(child); }
        window.Close();
    }
}
