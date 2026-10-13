using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace GitExtensions.Linux.Desktop.Controls;

internal static class OriginalCommandHelpBitmap
{
    // Portable LightnessCorrection: perceived HSL, near-black saturation,
    // theme WindowText/Window endpoints and the original floor(channel * 256).
    internal static Bitmap Adapt(Bitmap original, Color text, Color background)
    {
        double foreground = Hsl(text).L, window = Hsl(background).L;
        WriteableBitmap adapted = new(original.PixelSize, original.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using ILockedFramebuffer framebuffer = adapted.Lock(); original.CopyPixels(framebuffer, AlphaFormat.Unpremul);
            byte[] bytes = new byte[framebuffer.RowBytes * framebuffer.Size.Height]; Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);
            for (int y = 0; y < framebuffer.Size.Height; y++)
            {
                for (int x = 0; x < framebuffer.Size.Width; x++)
                {
                    int offset = y * framebuffer.RowBytes + x * 4;
                    Color rgb = Color.FromRgb(bytes[offset + 2], bytes[offset + 1], bytes[offset]);
                    (double hue, double saturation, double lightness) = Hsl(rgb);
                    double gamma = rgb.R == rgb.G && rgb.G == rgb.B ? 1 : (rgb.R + rgb.G + rgb.B) / (rgb.R * 0.8 + rgb.G * 1.75 + rgb.B * 0.45);
                    double perceived = TransformGamma(lightness, gamma);
                    double s = perceived > 0.1 ? saturation : saturation * perceived / 0.1;
                    double l = TransformGamma(Math.Clamp(foreground + perceived * (window - foreground), 0, 1), 1 / gamma);
                    Color result = Rgb(hue, s, Math.Clamp(l, 0, 1));
                    bytes[offset] = result.B; bytes[offset + 1] = result.G; bytes[offset + 2] = result.R;
                }
            }
            Marshal.Copy(bytes, 0, framebuffer.Address, bytes.Length); return adapted;
        }
        catch { adapted.Dispose(); throw; }
    }
    private static double TransformGamma(double value, double gamma) => value < gamma / (gamma + 1) ? value / gamma : 1 + gamma * (value - 1);
    private static (double H, double S, double L) Hsl(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        if (max == min) { return (0, 0, l); }
        double d = max - min, s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }
    private static Color Rgb(double h, double s, double l)
    {
        if (s == 0) { byte gray = Byte(l); return Color.FromRgb(gray, gray, gray); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        return Color.FromRgb(Byte(Hue(h + 1 / 3d)), Byte(Hue(h)), Byte(Hue(h - 1 / 3d)));
        double Hue(double t)
        {
            if (t < 0) { t++; } if (t > 1) { t--; }
            return t < 1 / 6d ? p + (q - p) * 6 * t : t < 1 / 2d ? q : t < 2 / 3d ? p + (q - p) * (2 / 3d - t) * 6 : p;
        }
        static byte Byte(double value) => (byte)Math.Clamp(Math.Floor(value * 256), 0, 255);
    }
}
