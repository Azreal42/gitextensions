using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private MenuItem BuildViewPatchMenu()
    {
        MenuItem item = OriginalAction("FormBrowse", "patchToolStripMenuItem",
            "View patc&h file...", "PatchView.png", ShowViewPatchAsync);
        _actionMenus.Add(item);
        return item;
    }

    private async Task ShowViewPatchAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) { return; }
        await RunAsync("View patch file", async repository =>
        {
            string encoding = repository is null ? "utf-8" : await repository.GetFileEncodingNameAsync(Token);
            using ViewPatchDialog dialog = new(encoding, _translations);
            CancellationToken token = Token;
            using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            token.ThrowIfCancellationRequested();
            try { await dialog.ShowDialog(WorkflowOwner); }
            finally { dialog.Close(); await dialog.CloseCompletion; }
            token.ThrowIfCancellationRequested();
        }, refresh: false, requireRepository: false);
    }
}
