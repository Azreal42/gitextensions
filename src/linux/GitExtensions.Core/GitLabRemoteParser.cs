using System.Diagnostics.CodeAnalysis;

namespace GitExtensions.Linux;

public sealed record GitLabRemote(Uri InstanceUri, string Namespace, string Name);

/// <summary>Pure anchored parsing. SSH web scheme/port/basepath require explicit instance settings when nonstandard.</summary>
public sealed class GitLabRemoteParser
{
    public bool IsValidRemoteUrl(string? remoteUrl) => TryParse(remoteUrl, out _);
    public bool TryExtractGitlabDataFromRemoteUrl(string? remoteUrl, [NotNullWhen(true)] out string? host,
        [NotNullWhen(true)] out string? owner, [NotNullWhen(true)] out string? repository)
    {
        host = owner = repository = null;
        if (!TryParse(remoteUrl, out GitLabRemote? value)) { return false; }
        host = value.InstanceUri.Authority; owner = value.Namespace; repository = value.Name; return true;
    }
    public bool TryParse(string? remoteUrl, [NotNullWhen(true)] out GitLabRemote? data, Uri? configuredInstance = null)
    {
        data = null;
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.Any(char.IsControl) || remoteUrl != remoteUrl.Trim()) { return false; }
        Uri? uri; bool web; string path;
        if (remoteUrl.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https" or "ssh" or "git")
                || uri.Query.Length > 0 || uri.Fragment.Length > 0) { return false; }
            web = uri.Scheme is "http" or "https";
            if (web && uri.UserInfo.Length > 0 || !web && uri.UserInfo.Contains(':')) { return false; }
            path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        }
        else
        {
            // scp form: user@host:group/subgroup/project.git (including bracketed IPv6).
            int at = remoteUrl.IndexOf('@'); if (at <= 0 || remoteUrl.IndexOf('@', at + 1) >= 0) { return false; }
            string rest = remoteUrl[(at + 1)..];
            int colon = rest.StartsWith('[') ? rest.IndexOf("]:", StringComparison.Ordinal) + 1 : rest.IndexOf(':');
            if (colon <= 0 || colon >= rest.Length - 1 || !Uri.TryCreate("ssh://" + rest[..colon] + "/", UriKind.Absolute, out uri)) { return false; }
            if (remoteUrl[..at].Any(c => char.IsWhiteSpace(c) || ":/\\".Contains(c))) { return false; }
            path = rest[(colon + 1)..].Trim('/'); web = false;
        }
        if (uri.Host.Length == 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || path.Contains('\\')) { return false; }
        Uri instance;
        if (configuredInstance is not null)
        {
            try { instance = GitLabSettings.ParseInstanceUri(configuredInstance.AbsoluteUri); }
            catch (ArgumentException) { return false; }
            if (uri.IdnHost != instance.IdnHost || web && !GitLabSettings.SameOrigin(uri, instance)) { return false; }
            if (web)
            {
                string prefix = Uri.UnescapeDataString(instance.AbsolutePath).Trim('/');
                if (prefix.Length > 0)
                {
                    if (!path.StartsWith(prefix + "/", StringComparison.Ordinal)) { return false; }
                    path = path[(prefix.Length + 1)..];
                }
            }
        }
        else
        {
            // A custom SSH port is not a web port. Windows also uses HTTPS for git/scp detection.
            UriBuilder builder = new(web ? uri.Scheme : "https", uri.Host, web && !uri.IsDefaultPort ? uri.Port : -1);
            instance = builder.Uri;
        }
        string[] parts = path.Split('/');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "?#".Contains(c)))) { return false; }
        string name = parts[^1]; if (name.EndsWith(".git", StringComparison.Ordinal)) { name = name[..^4]; }
        if (name.Length == 0 || name is "." or "..") { return false; }
        data = new(instance, string.Join('/', parts[..^1]), name); return true;
    }
}
