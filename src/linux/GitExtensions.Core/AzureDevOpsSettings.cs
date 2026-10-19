using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

/// <summary>Original Windows provider identity and XML keys. No credential discovery or persistence.</summary>
public sealed class AzureDevOpsSettings
{
    public const string ProviderName = "Azure DevOps and Team Foundation Server (since TFS2015)";
    public const string Prefix = "BuildServer." + ProviderName + ".";
    public const string EnableKey = "BuildServer.EnableIntegration";
    public const string ShowPageKey = "BuildServer.ShowBuildResultPage";
    public const string ProviderKey = "BuildServer.Type";
    public const string ProjectUrlKey = Prefix + "ProjectUrl";
    public const string BuildDefinitionFilterKey = Prefix + "BuildDefinitionNameFilter";
    public const string ApiTokenKey = Prefix + "RestApiToken";
    public const string RepositoryNameKey = Prefix + "RepositoryName";
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { EnableKey, ShowPageKey, ProviderKey, ProjectUrlKey, BuildDefinitionFilterKey, ApiTokenKey, RepositoryNameKey });
    public bool Enabled { get; init; }
    public bool ShowBuildResultPage { get; init; }
    public string Provider { get; init; } = "";
    public string ProjectUrl { get; init; } = "";
    public string BuildDefinitionFilter { get; init; } = "";
    public string ApiToken { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public bool Active => Enabled && Provider == ProviderName;
    public Uri ProjectUri => AzureDevOpsProjectUrls.ParseProjectUri(ProjectUrl);

    public static AzureDevOpsSettings Read(GitDistributedSettingsSnapshot snapshot, IEnumerable<string>? remotes = null,
        Func<string, string>? expandVariables = null, GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? Get(string key) => snapshot.GetValue(key, scope);
        string project = Get(ProjectUrlKey) ?? "", repository = Get(RepositoryNameKey) ?? "";
        foreach (string remote in remotes ?? [])
        {
            if (!AzureDevOpsProjectUrls.TryDetectProjectFromRemoteUrl(remote, out AzureDevOpsRemote? detected)) { continue; }
            if (string.IsNullOrWhiteSpace(project)) { project = detected.ProjectUri.AbsoluteUri.TrimEnd('/'); }
            if (string.IsNullOrWhiteSpace(repository)) { repository = detected.RepositoryName; }
            break;
        }
        AzureDevOpsSettings result = new()
        {
            Enabled = bool.TryParse(Get(EnableKey), out bool enabled) && enabled,
            ShowBuildResultPage = bool.TryParse(Get(ShowPageKey), out bool show) && show,
            Provider = Get(ProviderKey) ?? "", ProjectUrl = expandVariables is null ? project : expandVariables(project),
            BuildDefinitionFilter = Get(BuildDefinitionFilterKey) ?? "", ApiToken = Get(ApiTokenKey) ?? "", RepositoryName = repository
        };
        if (result.ApiToken.Length > 0)
        {
            GitDistributedSettingsScope? origin = scope == GitDistributedSettingsScope.Effective ? snapshot.GetSource(ApiTokenKey) : scope;
            string? bound = origin.HasValue ? snapshot.GetValue(ProjectUrlKey, origin.Value) : null;
            if (string.IsNullOrWhiteSpace(bound)) { throw TokenBindingError(); }
            if (expandVariables is not null) { bound = expandVariables(bound); }
            if (!SameProject(AzureDevOpsProjectUrls.ParseProjectUri(bound, allowVariables: expandVariables is null),
                AzureDevOpsProjectUrls.ParseProjectUri(result.ProjectUrl, allowVariables: expandVariables is null))) { throw TokenBindingError(); }
        }
        return result;
    }
    private static ArgumentException TokenBindingError() => new("Configure the Azure DevOps PAT and project URL together in the same settings scope; an inherited PAT cannot target another project, origin or base path.");
    public void Validate()
    {
        _ = AzureDevOpsProjectUrls.ParseProjectUri(ProjectUrl, allowVariables: true);
        _ = CreateBuildDefinitionRegex();
        if (ApiToken.Any(c => c < 33 || c > 126)) { throw new ArgumentException("The Azure DevOps PAT contains invalid characters."); }
        if (RepositoryName.Any(char.IsControl)) { throw new ArgumentException("The Azure DevOps repository name contains invalid characters."); }
    }
    public Regex CreateBuildDefinitionRegex()
    {
        try { return new(BuildDefinitionFilter, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException) { throw new ArgumentException("The Azure DevOps build definition regular expression is not valid."); }
    }
    public static bool SameProject(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port
        && string.Equals(Uri.UnescapeDataString(a.AbsolutePath).TrimEnd('/'), Uri.UnescapeDataString(b.AbsolutePath).TrimEnd('/'), StringComparison.Ordinal);
    public override string ToString() => "Azure DevOps settings (PAT redacted)";
}
