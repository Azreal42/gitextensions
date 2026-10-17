using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

/// <summary>Text metrics for compact hosts. Icon and drawing dimensions are intentionally separate.</summary>
internal static class DesktopFontMetrics
{
    internal static Typeface Typeface(ResolvedDesktopFont font, bool emphasis = false)
        => new(font.Family, font.Style, emphasis ? FontWeight.Bold : font.Weight);

    private sealed record Metrics(double Height, double ShortHashWidth);
    private static readonly ConditionalWeakTable<ResolvedDesktopFont, Metrics> Cache = new();
    private static Metrics Measure(ResolvedDesktopFont font) => Cache.GetValue(font, static value => new(
        new FormattedText("By", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface(value), value.SizeInDips, Brushes.Black).Height,
        new FormattedText("999999999", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface(value), value.SizeInDips, Brushes.Black).WidthIncludingTrailingWhitespace));
    internal static double TextHeight(ResolvedDesktopFont font) => Measure(font).Height;
    internal static double HashWidth(ResolvedDesktopFont font) => Math.Max(75, Math.Ceiling(Measure(font).ShortHashWidth) + 8);

    internal static double Height(ResolvedDesktopFont font, double minimum, double padding)
        => Math.Max(minimum, Math.Ceiling(TextHeight(font) + padding));

    // Compact hosts have no vertical text padding. Preserve their established
    // density while the real line fits; extra breathing room is needed only
    // when a larger line requires an expanded row.
    internal static double CompactHeight(ResolvedDesktopFont font, double minimum, double extra)
    {
        double textHeight = TextHeight(font);
        return Math.Ceiling(textHeight) <= minimum ? minimum : Math.Ceiling(textHeight + extra);
    }

    internal static double TreeRowHeight => CompactHeight(DesktopFonts.For(DesktopFontRole.Application), 18, 2);
    internal static double ToolbarHeight => Height(DesktopFonts.For(DesktopFontRole.Application), 24, 6);
    internal static double HistoryContentHeight(DesktopFontSession fonts)
        => Math.Max(CompactHeight(fonts.For(DesktopFontRole.Application), 18, 2), CompactHeight(fonts.For(DesktopFontRole.Monospace), 18, 2));
    internal static double HistoryRowHeight(DesktopFontSession fonts) => HistoryContentHeight(fonts) + 2;
    internal static double GraphRowHeight(DesktopFontSession fonts) => Math.Max(24, HistoryRowHeight(fonts));
    internal static double WorktreeRowHeight(DesktopFontSession fonts)
        => Math.Max(Height(fonts.For(DesktopFontRole.Application), 22, 5), Height(fonts.For(DesktopFontRole.Monospace), 22, 5));
}
