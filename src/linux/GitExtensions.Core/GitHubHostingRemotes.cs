using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public static class GitHubHostingRemotes
{
    /// <summary>Repository identity only; never returns/display-copies embedded credentials or changes any Git configuration.</summary>
    public static bool TryParse(string remoteUrl, GitHubHostingSettings settings, [NotNullWhen(true)] out GitHubRepositoryId? repository)
    {
        repository = null;
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.Any(char.IsControl)) { return false; }
        try
        {
            Uri web = GitHubHostingSettings.Endpoint(settings.WebUrl); string path;
            if (remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || remoteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out Uri? uri) || !GitHubHostingSettings.SameOrigin(web, uri)
                    || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.StartsWith(web.AbsolutePath, StringComparison.Ordinal)) { return false; }
                path = uri.AbsolutePath[web.AbsolutePath.Length..];
            }
            else if (remoteUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out Uri? uri) || !uri.IdnHost.Equals(web.IdnHost, StringComparison.OrdinalIgnoreCase)
                    || uri.UserInfo.Contains(':') || uri.Query.Length > 0 || uri.Fragment.Length > 0) { return false; }
                path = uri.AbsolutePath.TrimStart('/');
            }
            else
            {
                Match match = Regex.Match(remoteUrl, @"\A(?:[^@/:]+@)?(?<host>\[[^\]]+\]|[^:/]+):(?<path>.+)\z", RegexOptions.CultureInvariant);
                if (!match.Success || !match.Groups["host"].Value.Trim('[', ']').Equals(web.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) { return false; }
                path = match.Groups["path"].Value.TrimStart('/');
            }
            string[] parts = path.TrimEnd('/').Split('/'); if (parts.Length != 2) { return false; }
            string owner = Uri.UnescapeDataString(parts[0]), name = Uri.UnescapeDataString(parts[1]);
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) { name = name[..^4]; }
            GitHubHostingSettings.Segment(owner); GitHubHostingSettings.Segment(name); repository = new(owner, name); return true;
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { return false; }
    }
}
