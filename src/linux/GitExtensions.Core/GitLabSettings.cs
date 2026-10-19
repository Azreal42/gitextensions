using System.Globalization;

namespace GitExtensions.Linux;

/// <summary>Windows provider spelling and XML keys, without persistence or automatic API requests.</summary>
public sealed class GitLabSettings
{
    public const string ProviderName = "Gitlab";
    public const string EnableKey = "BuildServer.EnableIntegration";
    public const string ShowPageKey = "BuildServer.ShowBuildResultPage";
    public const string ProviderKey = "BuildServer.Type";
    public const string Prefix = "BuildServer.Gitlab.";
    public const string InstanceUrlKey = Prefix + "InstanceUrl";
    public const string ProjectIdKey = Prefix + "ProjectId";
    public const string ApiTokenKey = Prefix + "ApiToken";
    public const string PagesLimitKey = Prefix + "PagesLimit";
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { EnableKey, ShowPageKey, ProviderKey, InstanceUrlKey, ProjectIdKey, ApiTokenKey, PagesLimitKey });
    public bool Enabled { get; init; }
    public bool ShowBuildResultPage { get; init; }
    public string Provider { get; init; } = "";
    public string InstanceUrl { get; init; } = "";
    public long ProjectId { get; init; }
    public string ApiToken { get; init; } = "";
    public int PagesLimit { get; init; }
    public bool Active => Enabled && Provider == ProviderName;
    public Uri InstanceUri => ParseInstanceUri(InstanceUrl);

    public static GitLabSettings Read(GitDistributedSettingsSnapshot snapshot, IEnumerable<string>? remotes = null,
        GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? Get(string key) => snapshot.GetValue(key, scope);
        string instance = Get(InstanceUrlKey) ?? "";
        if (string.IsNullOrWhiteSpace(instance))
        {
            GitLabRemoteParser parser = new();
            foreach (string remote in remotes ?? [])
            { if (parser.TryParse(remote, out GitLabRemote? detected)) { instance = detected.InstanceUri.AbsoluteUri.TrimEnd('/'); break; } }
        }
        string? limit = Get(PagesLimitKey);
        GitLabSettings settings = new()
        {
            Enabled = bool.TryParse(Get(EnableKey), out bool enabled) && enabled,
            ShowBuildResultPage = bool.TryParse(Get(ShowPageKey), out bool show) && show,
            Provider = Get(ProviderKey) ?? "", InstanceUrl = instance,
            ProjectId = long.TryParse(Get(ProjectIdKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0,
            ApiToken = Get(ApiTokenKey) ?? "",
            PagesLimit = string.IsNullOrEmpty(limit) ? 0 : int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1
        };
        if (settings.ApiToken.Length > 0)
        {
            GitDistributedSettingsScope? origin = scope == GitDistributedSettingsScope.Effective ? snapshot.GetSource(ApiTokenKey) : scope;
            string? tokenInstance = origin.HasValue ? snapshot.GetValue(InstanceUrlKey, origin.Value) : null;
            if (string.IsNullOrWhiteSpace(tokenInstance) || !SameInstance(settings.InstanceUri, ParseInstanceUri(tokenInstance)))
            { throw new ArgumentException("Configure the GitLab token and instance URL together in the same settings scope; an inherited token cannot target another instance or base path."); }
        }
        return settings;
    }
    public void Validate(bool requireProject = true)
    {
        _ = InstanceUri;
        if (requireProject && ProjectId <= 0) { throw new ArgumentException("Enter a positive GitLab project ID."); }
        if (PagesLimit < 0) { throw new ArgumentException("Enter a nonnegative GitLab pages limit; zero means no configured page limit."); }
        if (ApiToken.Any(c => c < 33 || c > 126)) { throw new ArgumentException("The GitLab API token contains invalid characters."); }
    }
    public static Uri ParseInstanceUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        { throw new ArgumentException("Enter an absolute GitLab HTTP or HTTPS instance URL without credentials, query or fragment."); }
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
    public static bool SameInstance(Uri a, Uri b) => SameOrigin(a, b) && string.Equals(
        Uri.UnescapeDataString(a.AbsolutePath).TrimEnd('/'), Uri.UnescapeDataString(b.AbsolutePath).TrimEnd('/'), StringComparison.Ordinal);
    public override string ToString() => "GitLab settings (API token redacted)";
}
