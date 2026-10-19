using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed class JenkinsSettings
{
    public const string ProviderName = "Jenkins";
    public const string Prefix = "BuildServer.Jenkins.";
    public const string EnableKey = "BuildServer.EnableIntegration";
    public const string ShowPageKey = "BuildServer.ShowBuildResultPage";
    public const string ProviderKey = "BuildServer.Type";
    public const string ServerUrlKey = Prefix + "BuildServerUrl";
    public const string ProjectNameKey = Prefix + "ProjectName";
    public const string IgnoreBuildBranchKey = Prefix + "IgnoreBuildBranch";
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { EnableKey, ShowPageKey, ProviderKey, ServerUrlKey, ProjectNameKey, IgnoreBuildBranchKey });
    public bool Enabled { get; init; }
    public bool ShowBuildResultPage { get; init; }
    public string Provider { get; init; } = "";
    public string ServerUrl { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string IgnoreBuildBranch { get; init; } = "";
    public bool Active => Enabled && Provider == ProviderName;
    public Uri ServerUri => ParseServerUri(ServerUrl);
    public string UniqueKey => ServerUri.Host; // Original display key; persistence must use the full endpoint separately.

    public static JenkinsSettings Read(GitDistributedSettingsSnapshot snapshot, Func<string, string>? expandVariables = null,
        GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? Get(string key) => snapshot.GetValue(key, scope);
        string project = Get(ProjectNameKey) ?? "";
        return new()
        {
            Enabled = bool.TryParse(Get(EnableKey), out bool enabled) && enabled,
            ShowBuildResultPage = bool.TryParse(Get(ShowPageKey), out bool show) && show,
            Provider = Get(ProviderKey) ?? "", ServerUrl = Get(ServerUrlKey) ?? "",
            ProjectName = expandVariables is null ? project : expandVariables(project), IgnoreBuildBranch = Get(IgnoreBuildBranchKey) ?? ""
        };
    }
    public void Validate(bool requireProjects = true)
    {
        _ = ServerUri;
        IReadOnlyList<JenkinsProject> projects = JenkinsProjects.Resolve(ProjectName, ServerUri, allowVariables: true);
        if (requireProjects && projects.Count == 0) { throw new ArgumentException("Configure at least one Jenkins project."); }
        _ = CreateIgnoreBuildBranchRegex();
    }
    public Regex? CreateIgnoreBuildBranchRegex()
    {
        if (string.IsNullOrWhiteSpace(IgnoreBuildBranch)) { return null; }
        try { return new(IgnoreBuildBranch, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException) { throw new ArgumentException("The Jenkins ignore-branch regular expression is not valid."); }
    }
    public static Uri ParseServerUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\')) { throw InvalidServer(); }
        string address = value;
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            // Original default is http:8080. Preserve a user's explicit port and optional on-premises base path.
            int slash = value.IndexOf('/'); string authority = slash < 0 ? value : value[..slash];
            bool port = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
            address = "http://" + authority + (port ? "" : ":8080") + (slash < 0 ? "" : value[slash..]);
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !JenkinsProjects.SafePath(uri.AbsolutePath)) { throw InvalidServer(); }
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    private static ArgumentException InvalidServer() => new("Enter a Jenkins HTTP or HTTPS server URL without credentials, query or fragment.");
    public override string ToString() => "Jenkins settings (credentials supplied separately)";
}
