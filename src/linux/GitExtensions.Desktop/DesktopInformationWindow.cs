using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class DesktopInformationWindow : Window
{
    public DesktopInformationWindow(string title, string text, Func<Window, Task>? showContributors = null)
    {
        Title = title; Name = "DesktopInformationWindow";
        Width = 720; Height = 480; MinWidth = 360; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button close = new() { Content = TranslationCatalog.LoadDefault().Translate("Close"), Name = "CloseInformation", MinWidth = 75, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        close.Click += (_, _) => Close();
        TextBox content = new() { Name = "InformationText", Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8) };
        ScrollViewer.SetVerticalScrollBarVisibility(content, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Control footer = close;
        if (showContributors is not null)
        {
            Button contributors = new()
            {
                Name = "ShowContributors", Content = TranslationCatalog.LoadDefault().Translate("Contributors"), MinWidth = 90
            };
            contributors.Click += async (_, _) =>
            {
                if (!IsVisible || !contributors.IsEnabled) { return; }
                contributors.IsEnabled = false;
                try { await showContributors(this); }
                finally { if (IsVisible) { contributors.IsEnabled = true; } }
            };
            close.Margin = default;
            footer = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6, Margin = new Thickness(8), Children = { contributors, close }
            };
        }
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { footer, content } };
    }
}
