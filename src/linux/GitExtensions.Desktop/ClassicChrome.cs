using Avalonia;
using Avalonia.Controls;
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
}
