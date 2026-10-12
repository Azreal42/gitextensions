namespace GitExtensions.Linux;

// Portable RGB parts of GitExtUtils/GitUI/Theming/ColorHelper.cs and HslColor.cs.
public static class GitDiffColorMath
{
    public static GitDiffColor Dim(GitDiffColor color, GitDiffColor background)
        => new(Blend(color.R, background.R), Blend(color.G, background.G), Blend(color.B, background.B));
    private static byte Blend(byte color, byte background)
    {
        double linear = (Linear(color) + Linear(background)) * 0.5;
        double value = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(value * 255, 0, 255));
    }
    private static double Linear(byte color)
    {
        double value = color / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
    private static double Luminance(GitDiffColor color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    private static double Contrast(GitDiffColor first, GitDiffColor second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    internal static bool IsDark(GitDiffColorPalette palette) => Luminance(palette.EditorBackground) < Luminance(palette.EditorForeground);
    public static GitDiffColor TextColor(GitDiffColor background, GitDiffColor foreground)
    {
        if (Contrast(background, foreground) >= 4.5) { return foreground; }
        bool reduce = Luminance(foreground) < Luminance(background);
        GitDiffColor first = Adjust(reduce);
        if (Contrast(background, first) >= 4.5) { return first; }
        GitDiffColor second = Adjust(!reduce);
        return Contrast(background, first) >= Contrast(background, second) ? first : second;
        GitDiffColor Adjust(bool darker)
        {
            int r = foreground.R, g = foreground.G, b = foreground.B;
            GitDiffColor result = foreground;
            while (Contrast(background, result) < 4.5 && (darker ? r > 0 || g > 0 || b > 0 : r < 255 || g < 255 || b < 255))
            {
                r = Channel(r); g = Channel(g); b = Channel(b); result = new((byte)r, (byte)g, (byte)b);
            }
            return result;
            int Channel(int value) => darker ? value - (int)Math.Ceiling(value * 0.1) : Math.Min(255, value + (int)Math.Ceiling((255 - value) * 0.1));
        }
    }
    public static GitDiffColor Brighten(GitDiffColor color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min; s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        l = Math.Clamp(l + 0.1, 0, 1);
        if (s == 0) { byte gray = Byte(l); return new(gray, gray, gray); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        return new(Byte(Hue(h + 1 / 3d)), Byte(Hue(h)), Byte(Hue(h - 1 / 3d)));
        double Hue(double t)
        {
            if (t < 0) { t++; } if (t > 1) { t--; }
            return t < 1 / 6d ? p + (q - p) * 6 * t : t < 1 / 2d ? q : t < 2 / 3d ? p + (q - p) * (2 / 3d - t) * 6 : p;
        }
        static byte Byte(double value) => (byte)Math.Min(255, (int)Math.Floor(value * 256));
    }
}
