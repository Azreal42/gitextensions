using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private EmbeddedTerminalPane? _terminalPane;

    private Control BuildConsolePanel()
    {
        // Like the dock-filled Windows console, give the terminal the entire tab surface.
        _tabs.Styles.Add(new Style(selector => selector.OfType<TabControl>().Class("console-active"))
        { Setters = { new Setter(TabControl.PaddingProperty, new Thickness(0)) } });
        return _terminalPane = new(_output, OpenTerminalAsync, T);
    }

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
