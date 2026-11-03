using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

internal static class DesktopFontResolver
{
    // Names order fallback candidates only; every fixed-width face is checked through its actual metrics.
    internal static IReadOnlyList<FontFamily> Families(DesktopFontRole role, bool bold = false, bool italic = false)
        => FontManager.Current.SystemFonts.Where(family => !family.Name.StartsWith('@'))
            .DistinctBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
            .Where(family => role != DesktopFontRole.Code || Face(family, bold, italic) is { Fixed: true, Matches: true })
            .OrderBy(family => family.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static ResolvedDesktopFont Resolve(FontSpec requested, DesktopFontRole role)
    {
        requested.Validate();
        FontFamily? installed = FontManager.Current.SystemFonts.FirstOrDefault(family => string.Equals(family.Name, requested.Family, StringComparison.OrdinalIgnoreCase));
        bool available = installed is not null;
        FontFamily family = installed ?? Fallback(role, requested.Bold, requested.Italic);
        var face = Face(family, requested.Bold, requested.Italic);
        string? diagnostic = !available ? "The requested font family is unavailable; a fallback is displayed." : null;
        if (!face.Found || !face.Matches || (role == DesktopFontRole.Code && !face.Fixed))
        {
            diagnostic = role == DesktopFontRole.Code
                ? "The requested code face is unavailable or is not fixed pitch."
                : "The requested font face is unavailable; a fallback is displayed.";
            family = Fallback(role, requested.Bold, requested.Italic); face = Face(family, requested.Bold, requested.Italic);
            if (role == DesktopFontRole.Code && face.Fixed) { diagnostic += " A fixed-pitch fallback is displayed."; }
        }
        double size = requested.SizeInDips;
        if (!CanRender(family, size, requested.Bold, requested.Italic, out string? error))
        {
            diagnostic = "The requested font size cannot be rendered. " + error;
            size = new FontPreferences().For(role).SizeInDips;
            family = Fallback(role, requested.Bold, requested.Italic); face = Face(family, requested.Bold, requested.Italic);
        }
        if (role == DesktopFontRole.Code && !face.Fixed)
        { diagnostic = (diagnostic is null ? "" : diagnostic + " ") + "No fixed-pitch font face is installed."; }
        return new(requested, family, size, requested.Bold ? FontWeight.Bold : FontWeight.Normal,
            requested.Italic ? FontStyle.Italic : FontStyle.Normal, available, face.Name, face.Fixed, diagnostic);
    }

    internal static bool CanRender(FontFamily family, double size, bool bold, bool italic, out string? error)
    {
        try
        {
            // The native font-size API takes a float. Reject only its actual non-finite range, not a UI maximum.
            if (!double.IsFinite(size) || size <= 0 || !float.IsFinite((float)size))
            { throw new ArgumentException("The size exceeds the renderer's finite numeric range."); }
            FormattedText sample = new("Aa0 Wm", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(family, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, Brushes.Black);
            if (!double.IsFinite(sample.Width) || !double.IsFinite(sample.Height) || sample.Height <= 0)
            { throw new ArgumentException("The renderer did not produce finite font metrics."); }
            error = null; return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        { error = exception.Message; return false; }
    }

    internal static void Apply(Control target, ResolvedDesktopFont font)
    {
        switch (target)
        {
            case TextBlock text:
                text.FontFamily = font.Family; text.FontSize = font.SizeInDips; text.FontWeight = font.Weight; text.FontStyle = font.Style; break;
            case TemplatedControl control:
                control.FontFamily = font.Family; control.FontSize = font.SizeInDips; control.FontWeight = font.Weight; control.FontStyle = font.Style; break;
            default: throw new ArgumentException("This control does not expose font properties.", nameof(target));
        }
    }

    private static FontFamily Fallback(DesktopFontRole role, bool bold, bool italic)
    {
        FontFamily[] fonts = FontManager.Current.SystemFonts.Where(font => !font.Name.StartsWith('@')).ToArray();
        bool fixedPitch = role is DesktopFontRole.Code or DesktopFontRole.Monospace;
        string[] preferred = fixedPitch
            ? ["Consolas", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Menlo", "Monaco", "Courier New"]
            : ["Segoe UI", "Noto Sans", "DejaVu Sans", "Helvetica Neue", "Arial"];
        foreach (string name in preferred)
        {
            FontFamily? candidate = fonts.FirstOrDefault(font => string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null && Face(candidate, bold, italic) is { Found: true, Matches: true } face && (!fixedPitch || face.Fixed)) { return candidate; }
        }
        return fonts.FirstOrDefault(font => Face(font, bold, italic) is { Found: true, Matches: true } face && (!fixedPitch || face.Fixed)) ?? FontFamily.Default;
    }

    private static (bool Found, bool Fixed, string? Name, bool Matches) Face(FontFamily family, bool bold, bool italic)
    {
        FontStyle style = italic ? FontStyle.Italic : FontStyle.Normal;
        FontWeight weight = bold ? FontWeight.Bold : FontWeight.Normal;
        // Query this installed collection directly: the manager alone may silently return its default family.
        // A collection-resolved alias is attested by that actual public lookup, not guessed from its spelling.
        bool requested = FontManager.Current.SystemFonts.TryGetGlyphTypeface(family.Name, style, weight, FontStretch.Normal, out var requestedGlyph);
        bool rendered = FontManager.Current.TryGetGlyphTypeface(new Typeface(family, style, weight), out var glyph);
        bool matches = requested && rendered && string.Equals(requestedGlyph!.FamilyName, glyph!.FamilyName, StringComparison.OrdinalIgnoreCase);
        return (requested && rendered, rendered && glyph!.Metrics.IsFixedPitch, rendered ? glyph!.FamilyName : null, matches);
    }
}
