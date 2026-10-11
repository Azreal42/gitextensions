using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Plugins;

/// <summary>Portable counterparts of GitUI.Avatars' GitHub/Gravatar requests and initials selection.</summary>
public static partial class GourceAvatarProtocol
{
    [GeneratedRegex(@"^(\d+\+)?(?<username>[^@]+)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex NoReplyEmail();

    public static string? GitHubUsername(string email)
    {
        Match match = NoReplyEmail().Match(email.Trim());
        return match.Success ? match.Groups["username"].Value : null;
    }

    public static Uri GitHubAvatar(string email, int size)
    {
        string? username = GitHubUsername(email);
        return new(username is null
            ? $"https://avatars.githubusercontent.com/u/e?email={Uri.EscapeDataString(email.Trim())}&s={Size(size)}"
            : $"https://avatars.githubusercontent.com/{Uri.EscapeDataString(username)}?s={Size(size)}");
    }

    public static Uri GitHubProfile(string username) => new("https://api.github.com/users/" + Uri.EscapeDataString(username));

    public static Uri Gravatar(string email, int size)
    {
        // Same normalized email and r=g/d=404 semantics as the original provider.
        // Current Gravatar requests document SHA256 rather than the original MD5.
        // https://docs.gravatar.com/rest/hash/ and https://docs.gravatar.com/sdk/images/
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
        return new($"https://www.gravatar.com/avatar/{hash}?r=g&d=404&s={Size(size)}");
    }

    public static string CacheKey(string email, string name, int size)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            (string.IsNullOrWhiteSpace(email) ? name : email.Trim().ToLowerInvariant()) + "\0" + Size(size))));

    private static string Size(int size) => size is >= 1 and <= 2048
        ? size.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(size));

    public static int InitialsColorIndex(string email, int paletteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paletteLength);
        int hash = 23;
        unchecked { foreach (char character in email) hash = hash * 31 + character; }
        return (int)(Math.Abs((long)hash) % paletteLength);
    }

    public static string Initials(string email, string? name)
    {
        char[] separators = ['.', '-', '_'];
        string? selected = !string.IsNullOrWhiteSpace(name) ? name.Trim()
            : !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0].TrimStart() : null;
        if (selected is null) return "?";
        return FromNames(selected.Split(string.IsNullOrWhiteSpace(name) ? separators : null));

        string FromNames(string[] parts)
        {
            string[] possible = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
            if (possible.Length == 0) return "?";
            string[] names = possible.Where(part => char.IsLetterOrDigit(part[0])).ToArray();
            if (names.Length == 0) return possible.Length > 1 ? $"{possible[0][0]}{possible[1][0]}" : $"{possible[0][0]}";
            if (names.Length > 1) return $"{names[0][0]}{names[^1][0]}".ToUpper(CultureInfo.CurrentCulture);
            string single = names[0];
            if (single.Length == 1) return single.ToUpper(CultureInfo.CurrentCulture);
            if (char.IsUpper(single[1])) return $"{char.ToUpper(single[0], CultureInfo.CurrentCulture)}{single[1]}";
            string[] split = single.Split(separators);
            if (split.Length > 1) return FromNames(split);
            char[] upper = single.Where(char.IsUpper).ToArray();
            return upper.Length > 1 ? $"{upper[0]}{upper[^1]}" : $"{char.ToUpper(single[0], CultureInfo.CurrentCulture)}{single[1]}";
        }
    }
}
