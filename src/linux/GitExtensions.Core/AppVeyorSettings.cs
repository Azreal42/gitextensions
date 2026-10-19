namespace GitExtensions.Linux;

/// <summary>Original AppVeyor XML settings. The service endpoint is fixed, never inherited from another provider.</summary>
public sealed class AppVeyorSettings
{
    public const string ProviderName = "AppVeyor";
    public const string EnableKey = "BuildServer.EnableIntegration";
    public const string ShowPageKey = "BuildServer.ShowBuildResultPage";
    public const string ProviderKey = "BuildServer.Type";
    public const string Prefix = "BuildServer.AppVeyor.";
    public const string AccountNameKey = Prefix + "AppVeyorAccountName";
    public const string AccountTokenKey = Prefix + "AppVeyorAccountToken";
    public const string ProjectNameKey = Prefix + "AppVeyorProjectName";
    public const string LoadTestsResultsKey = Prefix + "AppVeyorLoadTestsResults";
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { EnableKey, ShowPageKey, ProviderKey, AccountNameKey, AccountTokenKey, ProjectNameKey, LoadTestsResultsKey });
    public bool Enabled { get; init; }
    public bool ShowBuildResultPage { get; init; }
    public string Provider { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string AccountToken { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public bool LoadTestsResults { get; init; }
    public bool Active => Enabled && Provider == ProviderName;

    public static AppVeyorSettings Read(GitDistributedSettingsSnapshot snapshot, string? defaultProjectName = null,
        Func<string, string>? expandVariables = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string project = snapshot.GetValue(ProjectNameKey) ?? defaultProjectName ?? "";
        return new()
        {
            Enabled = bool.TryParse(snapshot.GetValue(EnableKey), out bool enabled) && enabled,
            ShowBuildResultPage = bool.TryParse(snapshot.GetValue(ShowPageKey), out bool show) && show,
            Provider = snapshot.GetValue(ProviderKey) ?? "", AccountName = snapshot.GetValue(AccountNameKey) ?? "",
            AccountToken = snapshot.GetValue(AccountTokenKey) ?? "", ProjectName = expandVariables is null ? project : expandVariables(project),
            LoadTestsResults = bool.TryParse(snapshot.GetValue(LoadTestsResultsKey), out bool load) && load
        };
    }

    public void Validate()
    {
        if (AccountName.Length > 0) { AppVeyorProjects.ValidatePart(AccountName); }
        if (AccountToken.Any(c => c < 33 || c > 126)) { throw new ArgumentException("The AppVeyor API token contains invalid characters."); }
        // Drafts may retain the two original variables; only the runtime resolves them from the actual repository.
        string draft = ProjectName.Replace("{cRepoProject}", "project", StringComparison.Ordinal)
            .Replace("{cRepoShortName}", "repository", StringComparison.Ordinal);
        _ = AppVeyorProjects.Resolve(draft, AccountName);
        // Explicit account/project names remain usable anonymously; empty configuration simply has nothing to query.
    }
    public override string ToString() => "AppVeyor settings (API token redacted)";
}
