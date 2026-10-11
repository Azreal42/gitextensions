using Avalonia.Controls;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

internal static class ClassicTextContrast
{
    // ColorHelper.AdaptForeColor in the original UI uses this WCAG threshold
    // and changes each channel by 10% until the foreground is legible.
    internal static Color AdaptForeground(Control control, Color foreground)
    {
        Color background = control.TryFindResource("ClassicWindowBrush", control.ActualThemeVariant, out object? value) && value is ISolidColorBrush brush ? brush.Color : Colors.White;
        if (Contrast(background, foreground) >= 4.5) { return foreground; }
        bool lighter = Luminance(foreground) >= Luminance(background);
        Color first = Adjust(lighter);
        if (Contrast(background, first) >= 4.5) { return first; }
        Color second = Adjust(!lighter);
        return Contrast(background, first) >= Contrast(background, second) ? first : second;

        Color Adjust(bool increase)
        {
            Color current = foreground;
            while (Contrast(background, current) < 4.5 && current != (increase ? Colors.White : Colors.Black))
            {
                byte Channel(byte channel) => (byte)(increase ? Math.Min(255, channel + Math.Ceiling((255 - channel) * .1)) : channel - Math.Ceiling(channel * .1));
                current = Color.FromRgb(Channel(current.R), Channel(current.G), Channel(current.B));
            }
            return current;
        }
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double normalized = channel / 255d;
            return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
