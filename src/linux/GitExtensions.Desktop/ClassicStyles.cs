using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

internal static class ClassicStyles
{
    public static Styles Create() =>
    [
        new Style(selector => selector.OfType<Window>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("Segoe UI, Noto Sans, DejaVu Sans")),
                new Setter(TemplatedControl.FontSizeProperty, 12d)
            }
        },
        new Style(selector => selector.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, 12d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(5, 2)),
                new Setter(Layoutable.MinHeightProperty, 24d),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0))
            }
        },
        new Style(selector => selector.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, 12d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 2)),
                new Setter(Layoutable.MinHeightProperty, 24d),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0))
            }
        },
        new Style(selector => selector.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, 12d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(7, 3)),
                new Setter(Layoutable.MinHeightProperty, 25d)
            }
        },
        new Style(selector => selector.OfType<MenuItem>())
        {
            Setters = { new Setter(TemplatedControl.FontSizeProperty, 12d), new Setter(TemplatedControl.PaddingProperty, new Thickness(7, 3)) }
        },
        new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 1)), new Setter(Layoutable.MinHeightProperty, 24d) }
        },
        new Style(selector => selector.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brush.Parse("#DCEBFF")), new Setter(TemplatedControl.ForegroundProperty, Brushes.Black) }
        }
    ];
}
