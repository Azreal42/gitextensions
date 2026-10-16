using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class SubmoduleRemovalDialog
{
    private async Task ProcessAsync(bool sync)
    {
        if (IsBusy || _confirming || Selected is not { } selected || !(sync ? selected.CanSync : selected.CanUpdate)) { return; }
        string command = sync ? "sync" : "update --init --recursive";
        await RunAsync(sync ? "Synchronize submodule" : "Update submodule", async token =>
        {
            Exception? failure = null;
            try
            {
                LastProcess = await GitProcessDialog.ShowAsync(this, Repository.Path,
                    "git submodule " + command + " -- " + ExportDialogControls.DisplayPath(selected.Path), true,
                    (ct, output) => sync ? Repository.SyncSubmoduleAsync(selected, ct, output) : Repository.UpdateSubmoduleAsync(selected, ct, output), token);
                if (LastProcess.CommandStarted) { MarkRepositoryChanged(); }
                if ((LastProcess.Error ?? LastProcess.CompletionError) is Exception error) { throw error; }
                if (LastProcess.WasCancelled) { throw new OperationCanceledException(token); }
                if (LastProcess.Result is { Success: false } failed) { throw new GitCommandException(["submodule", sync ? "sync" : "update"], failed); }
                if (!sync && LastProcess.Success && !LastProcess.WorkflowStopRequested && !LastProcess.OwnerCloseRequested && _afterSubmoduleUpdateAsync is not null)
                { await _afterSubmoduleUpdateAsync(this, Repository, token); }
            }
            catch (Exception error) { failure = error; }
            await ReloadAfterActionAsync(selected, failure, token);
        });
        FocusModules();
    }

    private async Task AddAsync()
    {
        if (IsBusy || _confirming || !_ready || !IsVisible || Repository.IsBare) { return; }
        GitSubmoduleSnapshot? selected = Selected;
        AddSubmoduleDialog dialog = new(Repository) { RequestedThemeVariant = ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await RunAsync("Add submodule", async token =>
        {
            Exception? failure = null;
            using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            try { await dialog.ShowDialog(this); token.ThrowIfCancellationRequested(); }
            catch (Exception error) { failure = error; }
            finally { dialog.Close(); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); } }
            await ReloadAfterActionAsync(selected, failure, token, dialog.AddedSubmodulePath);
        });
        FocusModules();
    }

    private async Task PullAsync()
    {
        if (IsBusy || _confirming || !IsVisible || Selected is not { CanOpen: true } selected) { return; }
        await RunAsync("Pull", async token =>
        {
            Exception? failure = null;
            PullDialog? dialog = null;
            try
            {
                GitRepository module = await Repository.OpenSubmoduleRepositoryAsync(selected, token);
                dialog = new(module, new PullPreferencesStore().Load().DialogAction,
                    afterSubmoduleUpdateAsync: _afterSubmoduleUpdateAsync is null ? null : ct => _afterSubmoduleUpdateAsync(dialog!, module, ct),
                    resolveConflictsAsync: _resolveConflictsAsync) { RequestedThemeVariant = ActualThemeVariant };
                dialog.Opened += async (_, _) => await dialog.InitializeAsync(token);
                using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
                await dialog.ShowDialog(this); token.ThrowIfCancellationRequested();
            }
            catch (Exception error) { failure = error; }
            finally
            {
                dialog?.Close(); if (dialog?.HasRepositoryChanges == true || dialog?.Attempted == true) { MarkRepositoryChanged(); }
            }
            await ReloadAfterActionAsync(selected, failure, token);
        });
        FocusModules();
    }

    private async Task RemoveAsync()
    {
        if (IsBusy || _confirming || Selected is not { CanRemove: true, RemovalSelection: { } removal } selected) { return; }
        _confirming = true; UpdateActions();
        try
        {
            SubmoduleRemovalConfirmation confirmation = new(Translations, removal) { RequestedThemeVariant = ActualThemeVariant };
            if (!await confirmation.ShowDialog<bool>(this) || !IsVisible) { return; }
            await RunAsync(Original("RemoveSubmodule", "Remove"), async token =>
            {
                Exception? failure = null;
                // An IO failure can occur after publication and prevent a complete restore.
                // Always let Browse reread after an authorized removal attempt.
                MarkRepositoryChanged();
                try { await Repository.RemoveSubmoduleAsync(removal, token); }
                catch (Exception error) { failure = error; }
                // Removal never silently substitutes another row for the captured selection.
                await ReloadAfterActionAsync(null, failure, token);
            });
        }
        finally { _confirming = false; UpdateActions(); FocusModules(); }
    }

    private async Task ReloadAfterActionAsync(GitSubmoduleSnapshot? selected, Exception? failure, CancellationToken token, string? addedPath = null)
    {
        try { await ReloadAsync(selected, token, addedPath); }
        catch (Exception reload)
        {
            _ready = false;
            if (failure is not null) { throw new AggregateException(failure, reload); }
            throw;
        }
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private void FocusModules() { if (IsVisible && !IsBusy) { _modules.Focus(); } }
}
