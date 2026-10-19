using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original Property Details tool window: selectable readonly text, no implicit action.</summary>
internal sealed class ExceptionDetailDialog : Window
{
    internal ExceptionDetailDialog(string property, string information, TranslationCatalog translations)
    {
        Name = "ExceptionDetailView"; Title = translations.Translate("Property Details");
        Width = 567; Height = 218; MinWidth = 320; MinHeight = 150;
        CanMinimize = CanMaximize = false; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DesktopFonts.Apply(this, DesktopFontRole.Application);
        Grid top = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(3, 4) };
        top.Children.Add(new TextBlock { Name = "propertyLabel", Text = translations.Translate("Property:"), VerticalAlignment = VerticalAlignment.Center });
        TextBox name = ReadOnly("propertyTextBox", property); Grid.SetColumn(name, 1); top.Children.Add(name);
        Border status = ClassicChrome.Border(new Border { Height = 16 }, new Thickness(0, 1, 0, 0));
        status.Name = "statusStrip"; status.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        TextBox value = ReadOnly("propertyInformationTextBox", information, multiline: true);
        Content = new Grid { RowDefinitions = new("32,*,17"), Children = { top, At(value, 1), At(status, 2) } };
        Opened += (_, _) => { value.Focus(); value.CaretIndex = value.SelectionStart = value.SelectionEnd = 0;
            ModalWindowActivation.AfterMapping(this, () => IsVisible); };
    }

    internal static TextBox ReadOnly(string name, string? value, bool multiline = false)
    {
        TextBox text = new() { Name = name, Text = value ?? "", IsReadOnly = true, AcceptsReturn = multiline,
            TextWrapping = TextWrapping.NoWrap, ClearSelectionOnLostFocus = false, IsInactiveSelectionHighlightEnabled = false,
            Padding = new Thickness(3, 1), MinHeight = multiline ? 0 : 24 };
        if (multiline)
        {
            ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Visible);
            ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Visible);
        }
        return text;
    }
    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
