using Avalonia.Media;

namespace GitExtensions.Desktop.Controls;

internal static class FileStatusListColors
{
    // Portable ColorHelper.EnsureContrast (GitExtUtils/GitUI/Theming/ColorHelper.cs).
    // The original adjusts channels by ten percent and keeps the first readable result.
    internal static Color Contrasting(Color foreground, Color background)
    {
        if (Contrast(foreground, background) >= 4.5) { return foreground; }
        bool lighter = Luminance(foreground) >= Luminance(background);
        Color first = Adjust(lighter);
        if (Contrast(first, background) >= 4.5) { return first; }
        Color second = Adjust(!lighter);
        return Contrast(first, background) >= Contrast(second, background) ? first : second;

        Color Adjust(bool increase)
        {
            Color current = foreground;
            while (Contrast(current, background) < 4.5 && current != (increase ? Colors.White : Colors.Black))
            {
                byte Channel(byte channel) => (byte)(increase ? Math.Min(255, channel + Math.Ceiling((255 - channel) * .1)) : channel - Math.Ceiling(channel * .1));
                current = Color.FromRgb(Channel(current.R), Channel(current.G), Channel(current.B));
            }
            return current;
        }
    }

    private static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
