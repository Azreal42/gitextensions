using System.Diagnostics;
using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>Shares the original Browse/default and dedicated-dialog choice across file-menu hosts.</summary>
internal static class FileHistoryWorkflow
{
    internal static async Task OpenAsync(Window owner, GitRepository repository, string path, bool directory,
        GitCompareRevision selected, CancellationToken token, Func<ProcessStartInfo, Process?>? launch = null,
        bool initialBlame = false, GitCommandLogProcessContext? journalContext = null)
    {
        token.ThrowIfCancellationRequested();
        journalContext ??= GitCommandLog.CaptureProcessContext();
        if (directory && path.Length == 0) { path = "."; }
        if (new PreferencesStore().Load().UseBrowseForFileHistory)
        {
            BrowseRequest request = new(repository.Path,
                new BrowsePathFilter(path, directory ? BrowsePathKind.Directory : BrowsePathKind.File), selected);
            ProcessStartInfo info = BrowseApplicationLauncher.CreateStartInfo(request);
            token.ThrowIfCancellationRequested();
            if (!LoggedExternalProcess.Start(info, GitCommandLogProcessKind.Browse, launch, journalContext, cancellationToken: token).HasProcess)
            { throw new IOException("The Git Extensions file history browser could not be started."); }
            // Browse is an independent application. Closing this owner must not cancel its history.
            return;
        }

        FileHistoryDialog dialog = new(repository, path, selected, initialBlame)
        { RequestedThemeVariant = owner.ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await dialog.ShowDialog(owner);
        token.ThrowIfCancellationRequested();
    }
}
