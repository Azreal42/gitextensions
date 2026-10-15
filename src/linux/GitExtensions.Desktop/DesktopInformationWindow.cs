using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class DesktopInformationWindow : Window
{
    public DesktopInformationWindow(string title, string text)
    {
        Title = title; Name = "DesktopInformationWindow";
        Width = 720; Height = 480; MinWidth = 360; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button close = new() { Content = TranslationCatalog.LoadDefault().Translate("Close"), Name = "CloseInformation", MinWidth = 75, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        close.Click += (_, _) => Close();
        TextBox content = new() { Name = "InformationText", Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8) };
        ScrollViewer.SetVerticalScrollBarVisibility(content, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        DockPanel.SetDock(close, Dock.Bottom);
        Content = new DockPanel { Children = { close, content } };
    }
}
