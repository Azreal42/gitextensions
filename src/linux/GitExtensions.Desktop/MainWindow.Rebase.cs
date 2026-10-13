using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private MenuItem? _rebaseContext, _rebaseImmediate, _rebaseInteractive, _rebaseAdvanced;
    private readonly RebasePreferencesStore _rebasePreferencesStore = new();

    private MenuItem BuildRebaseMenu()
    {
        _rebaseImmediate = MenuAction("Rebase", () => OpenHistoryRebaseAsync(interactive: false, immediate: true));
        _rebaseInteractive = MenuAction("Rebase interactively", () => OpenHistoryRebaseAsync(interactive: true, immediate: true));
        _rebaseAdvanced = MenuAction("Rebase with advanced options…", RebaseAsync);
        _rebaseImmediate.Name = "HistoryRebaseImmediate";
        _rebaseInteractive.Name = "HistoryRebaseInteractive";
        _rebaseAdvanced.Name = "HistoryRebaseAdvanced";
        _rebaseContext = MenuGroup("Rebase on", _rebaseImmediate, _rebaseInteractive, _rebaseAdvanced);
        _rebaseContext.Name = "HistoryRebaseMenu";
        _rebaseContext.SubmenuOpened += (_, _) => RefreshRebaseMenu();
        RefreshRebaseMenu();
        return _rebaseContext;
    }

    private void RefreshRebaseMenu()
    {
        if (_rebaseContext is null) { return; }
        int count = SelectedComparisonCommits().Length;
        bool available = !_busy && _repository is { IsBare: false };
        _rebaseContext.IsEnabled = available && count > 0;
        _rebaseImmediate!.IsEnabled = _rebaseInteractive!.IsEnabled = available && count == 1;
        _rebaseAdvanced!.IsEnabled = available && count is 1 or 2;
    }

    private Task RebaseAsync() => OpenHistoryRebaseAsync(interactive: false);
    private Task InteractiveRebaseAsync() => OpenHistoryRebaseAsync(interactive: true);

    private async Task OpenHistoryRebaseAsync(bool interactive, bool immediate = false)
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        GitRepository repository = _repository;
        GitCommit[] selected = SelectedComparisonCommits();
        if (immediate && selected.Length != 1) { return; }
        if (selected.Length == 0 && !(await repository.GetRebaseStateAsync(_lifetime.Token)).IsActive)
        { _state.Text = T("Select a commit in History first."); return; }
        if (immediate && !await ConfirmHistoryRebaseAsync(interactive)) { return; }
        if (_repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
        // FormBrowse uses the visible grid order, rather than click order or revision dates.
        // Exactly two selections supply the excluded lower boundary and the checked-out branch.
        string? from = selected.Length == 2 ? selected[1].Hash : null;
        GitStatus? status = from is null ? null : await repository.GetStatusAsync(_lifetime.Token);
        if (_repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
        await ShowRebaseDialogAsync(selected.FirstOrDefault()?.Hash, from,
            status is null ? null : status.IsDetached ? "HEAD" : status.Branch ?? "HEAD", interactive, immediate);
    }

    private async Task RebaseOntoReferenceAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        await ShowRebaseDialogAsync(SelectedReference().Name);
    }

    private async Task<bool> ConfirmHistoryRebaseAsync(bool interactive)
    {
        if (_rebasePreferencesStore.Load().DontConfirmRebase) { return true; }
        Window owner = WorkflowOwner;
        Window prompt = new()
        {
            Name = "ConfirmHistoryRebase", Title = T("Rebase Confirmation"), Width = 520,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant
        };
        CheckBox suppress = new() { Name = "DontConfirmHistoryRebase", Content = T("Don't show again") };
        Button cancel = new() { Name = "CancelHistoryRebase", Content = T("Cancel"), IsCancel = true, IsDefault = true };
        Button accept = new() { Name = "AcceptHistoryRebase", Content = T("Rebase") };
        cancel.Click += (_, _) => prompt.Close(false); accept.Click += (_, _) => prompt.Close(true);
        prompt.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T(interactive ? "Rebase branch interactively." : "Rebase branch."), FontWeight = FontWeight.Bold },
            new TextBlock { Text = T("Are you sure you want to rebase? This action will rewrite commit history."), TextWrapping = TextWrapping.Wrap },
            suppress, ExportDialogControls.Buttons(cancel, accept)
        } };
        using CancellationTokenRegistration close = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(prompt.Close));
        bool accepted = await prompt.ShowDialog<bool>(owner);
        if (accepted && suppress.IsChecked == true)
        { _rebasePreferencesStore.Save(_rebasePreferencesStore.Load() with { DontConfirmRebase = true }); }
        return accepted;
    }

    private async Task ShowRebaseDialogAsync(string? onto = null, string? from = null, string? to = null,
        bool interactive = false, bool immediate = false, GitOperationAction? recoveryAction = null)
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        GitRepository repository = _repository;
        bool changed = false;
        try
        {
            await RunAsync("Rebase", async _ =>
            {
                CancellationToken token = Token;
                Window owner = WorkflowOwner;
                RebaseDialog dialog = new(repository, onto, from, to, interactive, ResolveWorkflowConflictsAsync, token)
                { RequestedThemeVariant = owner.ActualThemeVariant };
                dialog.Opened += async (_, _) =>
                {
                    await dialog.InitializeAsync();
                    if (dialog.LastError is not null || token.IsCancellationRequested) { return; }
                    if (recoveryAction is GitOperationAction action) { await dialog.ManageRebaseAsync(action); }
                    else if (immediate && dialog.State?.IsActive == false) { await dialog.ExecuteRebaseAsync(); }
                };
                using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
                try { token.ThrowIfCancellationRequested(); await dialog.ShowDialog(owner); token.ThrowIfCancellationRequested(); }
                finally { changed = dialog.HasRepositoryChanges; dialog.Close(); }
            }, refresh: false);
        }
        finally
        {
            if (changed && _repository == repository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
    }
}
