namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private async Task ShowMaintenanceDialogAsync(MaintenanceView initialView = MaintenanceView.Verify)
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new MaintenanceDialog(_repository, initialView));
    }

    private Task VerifyObjectsAsync() => ShowMaintenanceDialogAsync(MaintenanceView.Verify);
    private Task StatisticsAsync() => ShowMaintenanceDialogAsync(MaintenanceView.Statistics);
}
