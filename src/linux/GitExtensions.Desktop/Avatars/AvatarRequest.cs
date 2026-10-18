using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop.Avatars;

internal sealed record AvatarRenderIdentity(string FontFamily, string CultureName, FontWeight Weight, FontStyle Style)
{
    internal static AvatarRenderIdentity Capture(DesktopFontSession? fonts = null, CultureInfo? culture = null)
    {
        ResolvedDesktopFont font = (fonts ?? DesktopFonts.Active).For(DesktopFontRole.Application);
        return new(font.Family.ToString(), (culture ?? CultureInfo.CurrentCulture).Name, font.Weight, font.Style);
    }
}

internal sealed record AvatarRequest(string Email, string? Name, int PixelSize, AvatarRenderIdentity RenderIdentity)
{
    internal string NormalizedEmail => Email.Trim().ToLowerInvariant();
    internal AvatarRequest Normalize()
    {
        ArgumentNullException.ThrowIfNull(Email);
        ArgumentNullException.ThrowIfNull(RenderIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(RenderIdentity.FontFamily);
        _ = CultureInfo.GetCultureInfo(RenderIdentity.CultureName);
        return this with { PixelSize = PixelSize < 1 ? 64 : Math.Min(PixelSize, 512) };
    }

    internal string CacheKey(AvatarPreferences preferences)
        => Hash(JsonSerializer.Serialize(new[] { "shared-avatar-v1", PolicyKey(preferences), Email,
            NormalizedEmail, Name, PixelSize.ToString(CultureInfo.InvariantCulture), RenderIdentity.FontFamily,
            RenderIdentity.CultureName, RenderIdentity.Weight.ToString(), RenderIdentity.Style.ToString() }));

    internal static string PolicyKey(AvatarPreferences preferences)
        => Hash(JsonSerializer.Serialize(new[] { ((int)preferences.Provider).ToString(CultureInfo.InvariantCulture),
            ((int)preferences.Fallback).ToString(CultureInfo.InvariantCulture), preferences.CustomTemplate ?? "" }));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
