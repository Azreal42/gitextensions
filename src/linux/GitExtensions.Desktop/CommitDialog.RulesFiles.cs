using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private readonly List<Action> _updateRulesFileContexts = [];

    private void ConfigureRulesFileList(FileStatusListView view)
    {
        bool Available() => !_busy && !_commitRequested && !_repository.IsBare;
        RulesFileMenus.AttachEditors(view, Available, kind => ShowRulesDialogAsync(new RulesFileEditorDialog(_repository, kind)));
        _updateRulesFileContexts.Add(RulesFileMenus.AttachIgnoreContext(view, Available,
            (local, patterns) => ShowRulesDialogAsync(new AddToGitIgnoreDialog(_repository, local, patterns))));
    }

    private Task ShowRulesDialogAsync(RepositoryDialog dialog) => RunAsync(T("Edit ignored files"), async () =>
    {
        dialog.RequestedThemeVariant = ActualThemeVariant;
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        using CancellationTokenRegistration registration = OperationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try { await dialog.ShowDialog(this); }
        finally
        {
            if (dialog.HasRepositoryChanges) { HasRepositoryChanges = true; await RefreshDataAsync(); RepositoryChanged?.Invoke(this, EventArgs.Empty); }
        }
    });
}
