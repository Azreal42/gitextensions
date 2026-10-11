using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private async Task ShowPluginAsync(INativePlugin plugin)
    {
        if (_repository is null || _busy) { return; }
        PluginDialog dialog = new(_repository, plugin);
        await dialog.ShowDialog(this);
        // Cancellation or a hook can leave some completed mutations even when the plugin reports an error.
        await RefreshAsync();
    }
}
