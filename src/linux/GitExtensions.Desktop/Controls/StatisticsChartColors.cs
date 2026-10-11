using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace GitExtensions.Desktop.Controls;

/// <summary>Portable RGB/HSL translation of upstream ColorHelper.AdaptBackColor, with its original CSS color pairs.</summary>
internal static class StatisticsChartColors
{
    private static readonly (string Back, string Fore)[] Pairs =
        [("Window", "WindowText"), ("Control", "ControlText"), ("Info", "InfoText"),
         ("Highlight", "HighlightText"), ("MenuHighlight", "HighlightText")];
    private static readonly Dictionary<string, Color> Light = Read("invariant.css");
    private static readonly Dictionary<string, Color> Dark = Read("dark.css");

    internal static Color Adapt(Color original, Control control)
    {
        if (control.ActualThemeVariant != ThemeVariant.Dark) return original;
        var originalHsl = Hsl(original);
        double originalPercepted = GammaTransform(originalHsl.L, Gamma(original));
        var pair = Pairs.MinBy(pair =>
        {
            var sample = Hsl(Light[pair.Back]);
            return Math.Abs(originalPercepted - GammaTransform(sample.L, Gamma(Light[pair.Back]))) + 0.25 * Math.Abs(originalHsl.S - sample.S);
        });
        double Percepted(Color color) => GammaTransform(Hsl(color).L, Gamma(color));
        double transformed = Transform(originalPercepted, Percepted(Light[pair.Back]), Percepted(Light[pair.Fore]),
            Percepted(Dark[pair.Back]), Percepted(Dark[pair.Fore]));
        double actual = GammaTransform(transformed, 1 / Gamma(original));
        return FromHsl(original.A, originalHsl.H, originalHsl.S, actual);
    }

    internal static Color CorrectLightness(Color color, double correction)
    {
        byte Channel(byte value) => (byte)(correction < 0 ? value * (1 + correction) : (255 - value) * correction + value);
        return Color.FromArgb(color.A, Channel(color.R), Channel(color.G), Channel(color.B));
    }

    internal static IBrush Brush(Control control, string resource, string fallback)
        => control.TryFindResource("Classic" + resource + "Brush", control.ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Avalonia.Media.Brush.Parse(fallback);

    private static Dictionary<string, Color> Read(string file)
    {
        using StreamReader reader = new(AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Themes/" + file)));
        string css = reader.ReadToEnd();
        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(css, @"^\.(?<name>\w+)\s*\{\s*color:\s*(?<color>#[a-fA-F0-9]{6});\s*\}", RegexOptions.Multiline | RegexOptions.CultureInvariant))
            colors[match.Groups["name"].Value] = Color.Parse(match.Groups["color"].Value);
        foreach (Match match in Regex.Matches(css, @"^(?<name>\w+): ff(?<color>[a-fA-F0-9]{6})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
            colors[match.Groups["name"].Value] = Color.Parse("#" + match.Groups["color"].Value);
        return colors;
    }

    private static (double H, double S, double L) Hsl(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2;
        if (max == min) return (0, 0, lightness);
        double delta = max - min;
        double saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue = max == r ? (g - b) / delta + (g < b ? 6 : 0) : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue / 6, saturation, lightness);
    }
    private static double Gamma(Color color)
        => color.R == color.G && color.G == color.B ? 1 : (color.R + color.G + color.B) / (color.R * 0.8 + color.G * 1.75 + color.B * 0.45);
    private static double GammaTransform(double value, double gamma)
        => value < gamma / (gamma + 1) ? value / gamma : 1 + gamma * (value - 1);
    private static double Transform(double original, double exampleOriginal, double oppositeOriginal, double example, double opposite)
    {
        if (oppositeOriginal < exampleOriginal && exampleOriginal < original)
        {
            oppositeOriginal = 1; opposite = opposite < example ? 1 : 0;
        }
        else if (original < exampleOriginal && exampleOriginal < oppositeOriginal)
        {
            oppositeOriginal = 0; opposite = example < opposite ? 0 : 1;
        }
        double deltaOriginal = exampleOriginal - oppositeOriginal;
        if (Math.Abs(deltaOriginal) < 0.01)
        {
            oppositeOriginal = Math.Clamp(oppositeOriginal - 0.2, 0, 1);
            exampleOriginal = Math.Clamp(exampleOriginal + 0.2, 0, 1);
            deltaOriginal = exampleOriginal - oppositeOriginal;
        }
        return Math.Clamp(opposite + (original - oppositeOriginal) / deltaOriginal * (example - opposite), 0, 1);
    }
    private static Color FromHsl(byte alpha, double hue, double saturation, double lightness)
    {
        double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        double p = 2 * lightness - q;
        double Channel(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1d / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
        }
        byte Convert(double value) => (byte)Math.Min(255, Math.Floor(Math.Clamp(value, 0, 1) * 256));
        return saturation == 0 ? Color.FromArgb(alpha, Convert(lightness), Convert(lightness), Convert(lightness))
            : Color.FromArgb(alpha, Convert(Channel(hue + 1d / 3)), Convert(Channel(hue)), Convert(Channel(hue - 1d / 3)));
    }
}
