namespace GitExtensions.Linux.Desktop;

internal enum DesktopFontRole { Code, Application, Commit, Monospace }

/// <summary>Requested family identity and point size, independent of the installed Linux fallback.</summary>
internal sealed record FontSpec(string Family, double SizeInPoints, bool Bold = false, bool Italic = false)
{
    internal double SizeInDips => SizeInPoints * (96d / 72d);
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Family) || Family.Contains('\0') || Family.StartsWith('@'))
        { throw new ArgumentException("Choose a non-vertical font family.", nameof(Family)); }
        if (!double.IsFinite(SizeInPoints) || SizeInPoints <= 0 || !double.IsFinite(SizeInDips) || SizeInDips <= 0)
        { throw new ArgumentException("Enter a positive, finite font size in points that can be converted to display units.", nameof(SizeInPoints)); }
    }
}

internal sealed record FontPreferences
{
    public FontSpec Code { get; init; } = new("Consolas", 10);
    public FontSpec Application { get; init; } = new("Segoe UI", 9);
    public FontSpec Commit { get; init; } = new("Segoe UI", 9);
    public FontSpec Monospace { get; init; } = new("Consolas", 9);
    public bool ShowEolMarkerAsGlyph { get; init; }
    internal FontSpec For(DesktopFontRole role) => role switch
    {
        DesktopFontRole.Code => Code, DesktopFontRole.Application => Application,
        DesktopFontRole.Commit => Commit, DesktopFontRole.Monospace => Monospace,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    internal FontPreferences With(DesktopFontRole role, FontSpec value) => role switch
    {
        DesktopFontRole.Code => this with { Code = value }, DesktopFontRole.Application => this with { Application = value },
        DesktopFontRole.Commit => this with { Commit = value }, DesktopFontRole.Monospace => this with { Monospace = value },
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    internal void Validate()
    {
        foreach (DesktopFontRole role in Enum.GetValues<DesktopFontRole>())
        { (For(role) ?? throw new ArgumentException("Every font role requires a font specification.")).Validate(); }
    }
}
