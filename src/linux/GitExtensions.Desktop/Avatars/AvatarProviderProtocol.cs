using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using GitCommands;
using GitExtensions.Linux.Plugins;
using GitUI.Avatars;

namespace GitExtensions.Linux.Desktop.Avatars;

internal static class AvatarProviderProtocol
{
    internal static string? FallbackName(AvatarFallbackType? fallback) => fallback switch
    {
        AvatarFallbackType.MonsterId => "monsterid", AvatarFallbackType.Wavatar => "wavatar",
        AvatarFallbackType.Identicon => "identicon", AvatarFallbackType.Retro => "retro",
        AvatarFallbackType.Robohash => "robohash", _ => null
    };

    internal static Uri Gravatar(string email, int pixels, AvatarFallbackType? fallback, bool forceFallback = false)
    {
        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
        return new UriBuilder("https", "www.gravatar.com") { Path = "/avatar/" + hash,
            Query = "r=g&d=" + (FallbackName(fallback) ?? "404") + (forceFallback ? "&f=y" : "")
                + "&s=" + pixels.ToString(CultureInfo.InvariantCulture) }.Uri;
    }

    internal static string? GitHubUsername(string email)
        => email == email.Trim() ? GourceAvatarProtocol.GitHubUsername(email) : null;
    internal static Uri GitHubAvatar(string email, int pixels)
    {
        string? username = GitHubUsername(email);
        string target = username is null ? "u/e?email=" + HttpUtility.UrlEncode(email) + "&s="
            : HttpUtility.UrlEncode(username) + "?s=";
        return new("https://avatars.githubusercontent.com/" + target + pixels.ToString(CultureInfo.InvariantCulture));
    }
    internal static Uri? ProfileAvatar(byte[]? bytes, int pixels)
    {
        if (bytes is null) { return null; }
        try
        {
            using JsonDocument profile = JsonDocument.Parse(bytes);
            if (profile.RootElement.TryGetProperty("avatar_url", out JsonElement item) && item.ValueKind == JsonValueKind.String
                && Uri.TryCreate(item.GetString(), UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
            {
                UriBuilder builder = new(uri);
                builder.Query = builder.Query.TrimStart('?') + (builder.Query.Length > 0 ? "&" : "") + "s=" + pixels.ToString(CultureInfo.InvariantCulture);
                return builder.Uri;
            }
        }
        catch (JsonException) { }
        return null;
    }

    internal sealed class TemplateData(AvatarRequest request)
    {
        internal AvatarRequest Request { get; } = request;
        internal readonly Lazy<string> Md5 = new(() => Hash(MD5.HashData, request.NormalizedEmail));
        internal readonly Lazy<string> Sha1 = new(() => Hash(SHA1.HashData, request.NormalizedEmail));
        internal readonly Lazy<string> Sha256 = new(() => Hash(SHA256.HashData, request.NormalizedEmail));
        private static string Hash(Func<byte[], byte[]> hash, string value) => Convert.ToHexStringLower(hash(Encoding.UTF8.GetBytes(value)));
    }
    internal static Func<TemplateData, string> CompileTemplate(string template)
        => TemplateFormatter.Create<TemplateData>(template, name => name.Trim().ToLowerInvariant() switch
        {
            "name" => data => HttpUtility.UrlEncode(data.Request.Name),
            "email" => data => HttpUtility.UrlEncode(data.Request.NormalizedEmail),
            "md5" => data => data.Md5.Value, "sha1" => data => data.Sha1.Value, "sha256" => data => data.Sha256.Value,
            "imagesize" => data => data.Request.PixelSize.ToString(CultureInfo.CurrentCulture), _ => _ => null
        });

    internal static string Initials(string email, string? name, CultureInfo culture)
    {
        char[] separators = ['.', '-', '_'];
        string? selected = !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0].TrimStart() : null;
        if (selected is null) { return "?"; }
        return FromNames(selected.Split(string.IsNullOrWhiteSpace(name) ? separators : null));
        string FromNames(string[] parts)
        {
            string[] possible = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
            if (possible.Length == 0) { return "?"; }
            string[] names = possible.Where(part => char.IsLetterOrDigit(part[0])).ToArray();
            if (names.Length == 0) { return possible.Length > 1 ? $"{possible[0][0]}{possible[1][0]}" : $"{possible[0][0]}"; }
            if (names.Length > 1) { return $"{names[0][0]}{names[^1][0]}".ToUpper(culture); }
            string single = names[0];
            if (single.Length == 1) { return single.ToUpper(culture); }
            if (char.IsUpper(single[1])) { return $"{char.ToUpper(single[0], culture)}{single[1]}"; }
            string[] split = single.Split(separators);
            if (split.Length > 1) { return FromNames(split); }
            char[] upper = single.Where(char.IsUpper).ToArray();
            return upper.Length > 1 ? $"{upper[0]}{upper[^1]}" : $"{char.ToUpper(single[0], culture)}{single[1]}";
        }
    }
}
