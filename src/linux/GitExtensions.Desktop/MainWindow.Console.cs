using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private EmbeddedTerminalPane? _terminalPane;

    private Control BuildConsolePanel() => _terminalPane = new(_output, OpenTerminalAsync, T);

    private async Task OpenEmbeddedConsoleAsync()
    {
        FocusDetail("ConsoleTab");
        await EnsureConsoleAsync();
    }

    private async Task EnsureConsoleAsync()
    {
        if (_terminalPane is null || _lifetime.IsCancellationRequested) { return; }
        _terminalPane.SetRepository(_repository);
        if ((_tabs.SelectedItem as TabItem)?.Name == "ConsoleTab") { await _terminalPane.EnsureStartedAsync(); }
    }
}
