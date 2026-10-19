using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

/// <summary>Original Windows XML keys. Credentials are supplied separately, never read from XML or the OS.</summary>
public sealed class TeamCitySettings
{
    public const string ProviderName = "TeamCity";
    public const string EnableKey = "BuildServer.EnableIntegration";
    public const string ShowPageKey = "BuildServer.ShowBuildResultPage";
    public const string ProviderKey = "BuildServer.Type";
    public const string Prefix = "BuildServer.TeamCity.";
    public const string ServerUrlKey = Prefix + "BuildServerUrl";
    public const string ProjectNameKey = Prefix + "ProjectName";
    public const string BuildIdFilterKey = Prefix + "BuildIdFilter";
    public const string LogAsGuestKey = Prefix + "LogAsGuest";
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { EnableKey, ShowPageKey, ProviderKey, ServerUrlKey, ProjectNameKey, BuildIdFilterKey, LogAsGuestKey });
    public bool Enabled { get; init; }
    public bool ShowBuildResultPage { get; init; }
    public string Provider { get; init; } = "";
    public string ServerUrl { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string BuildIdFilter { get; init; } = "";
    public bool LogAsGuest { get; init; }
    public bool Active => Enabled && Provider == ProviderName;
    public Uri ServerUri => ParseServerUri(ServerUrl);
    public string UniqueKey => ServerUri.Host; // Windows adapter's credential chooser key.
    public IReadOnlyList<string> ProjectIds => Array.AsReadOnly(ProjectName.Split('|', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray());

    public static TeamCitySettings Read(GitDistributedSettingsSnapshot snapshot, string? defaultProjectName = null, Func<string, string>? expandVariables = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string project = snapshot.GetValue(ProjectNameKey) ?? defaultProjectName ?? "";
        return new()
        {
            Enabled = bool.TryParse(snapshot.GetValue(EnableKey), out bool enabled) && enabled,
            ShowBuildResultPage = bool.TryParse(snapshot.GetValue(ShowPageKey), out bool page) && page,
            Provider = snapshot.GetValue(ProviderKey) ?? "", ServerUrl = snapshot.GetValue(ServerUrlKey) ?? "",
            ProjectName = expandVariables is null ? project : expandVariables(project),
            BuildIdFilter = snapshot.GetValue(BuildIdFilterKey) ?? "",
            LogAsGuest = bool.TryParse(snapshot.GetValue(LogAsGuestKey), out bool guest) && guest
        };
    }

    public void Validate(bool requireProjects = true)
    {
        _ = ServerUri;
        if (requireProjects && ProjectIds.Count == 0) { throw new ArgumentException("Select at least one TeamCity project."); }
        foreach (string project in ProjectIds) { ValidateId(project); }
        _ = CreateBuildIdRegex();
    }
    public Regex CreateBuildIdRegex()
    {
        try { return new(BuildIdFilter, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException) { throw new ArgumentException("The TeamCity build ID filter is not a valid regular expression."); }
    }
    public static Uri ParseServerUri(string value)
    {
        string address = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || value.Any(char.IsControl))
        { throw new ArgumentException("Enter a TeamCity HTTP or HTTPS server URL without credentials, query or fragment."); }
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
    internal static void ValidateId(string id)
    {
        // Windows also accepts opaque project locators (for example name:My Project).
        // They are escaped as one path component, never interpolated into a URL/query.
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(c => char.IsControl(c) || "/\\?#".Contains(c)))
        { throw new ArgumentException("The TeamCity project or build ID is invalid."); }
    }
    public override string ToString() => "TeamCity settings (credentials supplied separately)";
}
