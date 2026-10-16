using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace GitExtensions.Linux.Desktop;

internal static class ClassicChrome
{
    internal static Border Border(Control child, Thickness thickness, Thickness padding = default)
    {
        Border border = new() { Child = child, BorderThickness = thickness, Padding = padding };
        border.Bind(Avalonia.Controls.Border.BorderBrushProperty, new DynamicResourceExtension("ClassicBorderBrush"));
        return border;
    }

    internal static Grid Group(string name, string caption, Control body, Thickness? padding = null)
    {
        Grid group = new() { Name = name };
        Border frame = Border(body, new Thickness(1), padding ?? new Thickness(6, 9, 6, 6));
        frame.Name = name + "Frame";
        frame.Margin = new Thickness(0, 7, 0, 0);
        group.Children.Add(frame);
        Border legend = new()
        {
            Name = name + "Caption", Child = new TextBlock { Text = caption }, Padding = new Thickness(2, 0),
            Margin = new Thickness(7, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        legend.Bind(Avalonia.Controls.Border.BackgroundProperty, new DynamicResourceExtension("ClassicWindowBrush"));
        group.Children.Add(legend);
        return group;
    }
}
