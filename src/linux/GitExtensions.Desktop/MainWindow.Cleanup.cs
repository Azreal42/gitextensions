namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Task CleanupRepositoryAsync()
        => _busy || _repository is not { IsBare: false } repository ? Task.CompletedTask
            : ShowRepositoryDialogAsync(new CleanupRepositoryDialog(repository));
}
