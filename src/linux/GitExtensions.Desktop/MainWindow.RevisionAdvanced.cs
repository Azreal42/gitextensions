using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private MenuItem? _revisionAdvancedMenu;
    private MenuItem[] _revisionAdvancedActions = [];

    private MenuItem BuildRevisionAdvancedMenu()
    {
        _revisionAdvancedActions =
        [
            MenuAction("Edit commit", () => EditRevisionCommitAsync(GitRebaseAction.Edit)),
            MenuAction("Reword commit", () => EditRevisionCommitAsync(GitRebaseAction.Reword)),
            MenuAction("Create a fixup commit...", () => OpenTargetedCommitAsync(TargetedCommitKind.Fixup)),
            MenuAction("Create a squash commit...", () => OpenTargetedCommitAsync(TargetedCommitKind.Squash)),
            MenuAction("Create an amend commit...", () => OpenTargetedCommitAsync(TargetedCommitKind.Amend))
        ];
        string[] names = ["HistoryEditCommit", "HistoryRewordCommit", "HistoryFixupCommit", "HistorySquashCommit", "HistoryAmendCommit"];
        for (int index = 0; index < _revisionAdvancedActions.Length; index++)
        { _revisionAdvancedActions[index].Name = names[index]; _revisionAdvancedActions[index].Tag = true; }
        MenuItem help = MenuAction("Get help on how to use these features", () =>
        {
            LoggedExternalProcess.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                ArgumentList = { "https://git-extensions-documentation.readthedocs.io/en/latest/modify_history.html#using-autosquash-rebase-feature" }
            }, GitCommandLogProcessKind.OpenResource);
            return Task.CompletedTask;
        });
        help.Name = "HistoryAdvancedHelp";
        _revisionAdvancedMenu = new MenuItem
        {
            Name = "HistoryAdvancedMenu", Header = T("Advanced"), Icon = OriginalIconImage("Advanced.png"),
            ItemsSource = _revisionAdvancedActions.Append(help).ToArray()
        };
        _revisionAdvancedMenu.SubmenuOpened += (_, _) => RefreshRevisionAdvancedMenu();
        RefreshRevisionAdvancedMenu();
        return _revisionAdvancedMenu;
    }

    private void RefreshRevisionAdvancedMenu()
    {
        bool available = !_busy && _repository is { IsBare: false } && PrimaryHistoryItem is GitCommit;
        if (_revisionAdvancedMenu is not null) { _revisionAdvancedMenu.IsEnabled = available; }
        foreach (MenuItem item in _revisionAdvancedActions) { item.IsEnabled = available; }
    }

    private async Task OpenTargetedCommitAsync(TargetedCommitKind kind)
    {
        if (_busy || _repository is not { IsBare: false } repository || PrimaryHistoryItem is not GitCommit selected) { return; }
        Window owner = WorkflowOwner;
        await using var historyMutation = BeginHistoryMutationScope();
        CommitDialog dialog = new(repository,
            afterSubmoduleUpdateAsync: (window, token) => AfterSubmoduleUpdateAsync(repository, window, token),
            initialKind: kind, targetCommit: selected) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancel = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try
        {
            await RunHistoryMutationAsync("Open commit dialog", _ => dialog.InitializeAsync(), refresh: false);
            if (_repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
            MarkStandaloneMayHaveRepositoryChanges();
            await dialog.ShowDialog(owner);
        }
        finally
        {
            dialog.Close(); if (dialog.HasRepositoryChanges) { AcceptStandaloneRepositoryMutationAttempt(repository); }
            if (dialog.HasRepositoryChanges && _repository == repository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
    }

    private async Task EditRevisionCommitAsync(GitRebaseAction action)
    {
        if (_busy || _repository is not { IsBare: false } repository || PrimaryHistoryItem is not GitCommit selected) { return; }
        if (!await ConfirmHistoryRebaseAsync(interactive: true)) { return; }
        if (_repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
        await using var historyMutation = BeginHistoryMutationScope();
        try
        {
            await RunHistoryMutationAsync(action == GitRebaseAction.Edit ? "Edit commit" : "Reword commit", async _ =>
            {
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the rebase editor application.");
                MarkStandaloneMayHaveRepositoryChanges();
                GitResult result = await repository.RebaseCommitAsync(selected.Hash, action, executable, Token);
                AppendOutput(result.StandardOutput + result.StandardError);
            }, refresh: false);
        }
        finally
        {
            if (_repository == repository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
        // Edit stops after the selected commit; conflicts or a cancelled message editor
        // also leave a native session. Continue/Commit/Abort use the existing recovery UI.
        if (_repository == repository && !_lifetime.IsCancellationRequested
            && (await repository.GetRebaseStateAsync(_lifetime.Token)).IsActive)
        { await ShowRebaseDialogAsync(); }
    }
}
