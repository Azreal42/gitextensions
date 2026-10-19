using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record AzureDevOpsRemote(Uri ProjectUri, string RepositoryName);
public sealed record AzureDevOpsBuildUrl(Uri ProjectUri, long BuildId);

/// <summary>Cloud and TFS URL operations corresponding to the original ProjectUrlHelper, without network or persistence.</summary>
public static class AzureDevOpsProjectUrls
{
    public static Uri ParseProjectUri(string value, bool allowVariables = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (allowVariables) { value = value.Replace("{cRepoProject}", "project", StringComparison.Ordinal).Replace("{cRepoShortName}", "repository", StringComparison.Ordinal); }
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) || value.Contains('{') || value.Contains('}')
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        { throw new ArgumentException("Enter an absolute Azure DevOps HTTP or HTTPS project URL without credentials, query, fragment or unresolved variables."); }
        string[] parts = Parts(uri);
        if (parts.Length < (uri.IdnHost == "dev.azure.com" ? 2 : 1)) { throw new ArgumentException("Enter the Azure DevOps project URL including its project path."); }
        return new(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }
    private static string[] Parts(Uri uri)
    {
        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(p => p is "." or ".." || p.Any(c => char.IsControl(c) || "/\\?#".Contains(c)))) { throw new ArgumentException("The Azure DevOps URL contains an invalid path component."); }
        return parts;
    }
    private static Uri Project(Uri origin, IEnumerable<string> parts)
    { UriBuilder builder = new(origin) { UserName = "", Password = "", Query = "", Fragment = "", Path = "/" + string.Join('/', parts.Select(Uri.EscapeDataString)) }; return ParseProjectUri(builder.Uri.AbsoluteUri); }

    public static bool TryDetectProjectFromRemoteUrl(string? remote, [NotNullWhen(true)] out AzureDevOpsRemote? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(remote) || remote != remote.Trim() || remote.Any(char.IsControl)) { return false; }
        try
        {
            Match ssh = Regex.Match(remote, @"^(?<user>[^@:/\.]+)@(?<host>ssh\.dev\.azure\.com|vs-ssh\.visualstudio\.com):v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/]+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (ssh.Success)
            {
                string host = ssh.Groups["host"].Value, org = host == "ssh.dev.azure.com" ? ssh.Groups["org"].Value : ssh.Groups["user"].Value;
                Uri origin = new(host == "ssh.dev.azure.com" ? "https://dev.azure.com/" : "https://" + org + ".visualstudio.com/");
                string repository = ssh.Groups["repo"].Value;
                result = new(Project(origin, host == "ssh.dev.azure.com" ? [org, ssh.Groups["project"].Value] : [ssh.Groups["project"].Value]), repository); return true;
            }
            if (!Uri.TryCreate(remote, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Contains(':') || uri.Query.Length > 0 || uri.Fragment.Length > 0) { return false; }
            string[] parts = Parts(uri); int marker = Array.FindIndex(parts, part => part is "_git" or "_ssh");
            if (marker < 0 || marker + 2 != parts.Length) { return false; }
            IEnumerable<string>? project = null;
            if (uri.IdnHost == "dev.azure.com" && marker == 2) { project = parts.Take(2); }
            else if (uri.IdnHost.EndsWith(".visualstudio.com", StringComparison.Ordinal) && uri.IdnHost[..^".visualstudio.com".Length].Length > 0)
            {
                if (marker == 1) { project = parts.Take(1); }
                else if (marker == 2 && parts[0] == "DefaultCollection") { project = parts.Skip(1).Take(1); }
            }
            else
            {
                int collection = Array.IndexOf(parts, "DefaultCollection");
                if (collection > 0 && marker == collection + 2) { project = parts.Take(marker); }
                else if (collection > 0 && marker == collection + 1) { project = parts.Take(marker).Append(parts[^1]); }
            }
            if (project is null) { return false; }
            result = new(Project(uri, project), parts[^1]); return true;
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or RegexMatchTimeoutException) { return false; }
    }
    public static (bool success, string? projectUrl) TryDetectProjectFromRemoteUrl(string? remote)
        => TryDetectProjectFromRemoteUrl(remote, out AzureDevOpsRemote? result) ? (true, result.ProjectUri.AbsoluteUri.TrimEnd('/')) : (false, null);
    public static (bool success, string? projectUrl) TryDetectProjectFromRemoteUrls(IEnumerable<string?> remotes)
    {
        foreach (string? remote in remotes) { (bool success, string? projectUrl) found = TryDetectProjectFromRemoteUrl(remote); if (found.success) { return found; } }
        return (false, null);
    }
    public static bool TryGetTokenManagementUri(string? projectUrl, [NotNullWhen(true)] out Uri? result)
    {
        result = null;
        try
        {
            Uri project = ParseProjectUri(projectUrl ?? ""); string[] parts = Parts(project); IEnumerable<string> parent;
            if (project.IdnHost.EndsWith(".visualstudio.com", StringComparison.Ordinal)) { parent = []; }
            else if (project.IdnHost == "dev.azure.com") { parent = parts.Take(1); }
            else { if (parts.Length < 2) { return false; } parent = parts.SkipLast(1); }
            UriBuilder builder = new(project) { Path = "/" + string.Join('/', parent.Append("_details").Append("security").Append("tokens").Select(Uri.EscapeDataString)) };
            result = builder.Uri; return true;
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { return false; }
    }
    public static (bool success, string? tokenManagementUrl) TryGetTokenManagementUrlFromProject(string? projectUrl)
        => TryGetTokenManagementUri(projectUrl, out Uri? result) ? (true, result.AbsoluteUri) : (false, null);
    public static bool TryParseBuildUrl(string? value, [NotNullWhen(true)] out AzureDevOpsBuildUrl? result)
    {
        result = null;
        if (value is null || value != value.Trim() || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) { return false; }
        try
        {
            string[] parts = Parts(uri); int marker = Array.IndexOf(parts, "_build"); if (marker < 1) { return false; }
            string? id = null;
            foreach (string item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = item.Split('=', 2);
                if (Uri.UnescapeDataString(pair[0]) != "buildId") { continue; }
                if (id is not null || pair.Length != 2) { return false; } id = Uri.UnescapeDataString(pair[1]);
            }
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n <= 0) { return false; }
            result = new(Project(uri, parts.Take(marker)), n); return true;
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { return false; }
    }
    public static (bool success, string? projectUrl, long buildId) TryParseBuildUrl(string? value)
        => TryParseBuildUrl(value, out AzureDevOpsBuildUrl? result) ? (true, result.ProjectUri.AbsoluteUri.TrimEnd('/'), result.BuildId) : (false, null, -1);
}
