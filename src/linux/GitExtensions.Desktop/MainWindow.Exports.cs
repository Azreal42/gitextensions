namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private async Task ShowMaintenanceDialogAsync(MaintenanceView initialView = MaintenanceView.Verify)
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new MaintenanceDialog(_repository, initialView));
    }

    private async Task RecoverLostObjectsAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new RecoverLostObjectsDialog(_repository,
            autoNormalize: _preferences.AutoNormaliseBranchName, replacement: _preferences.AutoNormaliseSymbol));
    }

    private Task VerifyObjectsAsync() => RecoverLostObjectsAsync();
    private Task StatisticsAsync() => ShowMaintenanceDialogAsync(MaintenanceView.Statistics);
}
