using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

internal sealed record ResolvedDesktopFont(FontSpec Requested, FontFamily Family, double SizeInDips,
    FontWeight Weight, FontStyle Style, bool RequestedFamilyAvailable, string? EffectiveFamilyName,
    bool IsFixedPitch, string? Diagnostic);

/// <summary>A process/session snapshot. The saved profile and previews never mutate it.</summary>
internal sealed class DesktopFontSession
{
    private readonly Dictionary<DesktopFontRole, ResolvedDesktopFont> _resolved = [];
    private readonly object _gate = new();
    internal FontPreferences Preferences { get; }
    internal Exception? LoadError { get; }
    internal bool ShowEolMarkerAsGlyph => Preferences.ShowEolMarkerAsGlyph;
    internal DesktopFontSession(FontPreferences preferences, Exception? error)
    { Preferences = preferences; LoadError = error; }
    internal ResolvedDesktopFont For(DesktopFontRole role)
    {
        lock (_gate)
        {
            if (!_resolved.TryGetValue(role, out ResolvedDesktopFont? value))
            { value = DesktopFontResolver.Resolve(Preferences.For(role), role); _resolved.Add(role, value); }
            return value;
        }
    }
    internal void Apply(Control target, DesktopFontRole role) => DesktopFontResolver.Apply(target, For(role));
}

internal static class DesktopFonts
{
    private static readonly Dictionary<string, DesktopFontSession> Sessions = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    internal static void Initialize() => _ = Active;
    internal static DesktopFontSession Active
    {
        get
        {
            FontPreferencesStore store = new();
            lock (Gate)
            {
                if (!Sessions.TryGetValue(store.FilePath, out DesktopFontSession? session))
                { session = CreateSession(store); Sessions.Add(store.FilePath, session); }
                return session;
            }
        }
    }
    internal static DesktopFontSession CreateSession(FontPreferencesStore? store = null)
    {
        try { return new((store ?? new()).Load(), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return new(new(), error); }
    }
    internal static ResolvedDesktopFont For(DesktopFontRole role) => Active.For(role);
    internal static void Apply(Control target, DesktopFontRole role) => Active.Apply(target, role);
    internal static bool ShowEolMarkerAsGlyph => Active.ShowEolMarkerAsGlyph;
}
