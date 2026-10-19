using GitCommands.Remotes;

namespace GitExtensions.Linux;

/// <summary>The original Windows BuildServer group and GitHub Actions provider keys.</summary>
public sealed class GitHubActionsSettings
{
    public const string ProviderName = "GitHub Actions";
    public const string EnableKey = "BuildServer.EnableIntegration";
    public const string ShowPageKey = "BuildServer.ShowBuildResultPage";
    public const string ProviderKey = "BuildServer.Type";
    public const string Prefix = "BuildServer.GitHub Actions.";
    public const string ApiUrlKey = Prefix + "GitHubActionsApiUrl";
    public const string OwnerKey = Prefix + "GitHubActionsOwner";
    public const string RepositoryKey = Prefix + "GitHubActionsRepository";
    public const string TokenKey = Prefix + "GitHubActionsApiToken";
    public const string DefaultApiUrl = "https://api.github.com";
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { EnableKey, ShowPageKey, ProviderKey, ApiUrlKey, OwnerKey, RepositoryKey, TokenKey });

    public bool Enabled { get; init; }
    public bool ShowBuildResultPage { get; init; }
    public string Provider { get; init; } = "";
    public string ApiUrl { get; init; } = DefaultApiUrl;
    public string Owner { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Token { get; init; } = "";
    public bool Active => Enabled && Provider == ProviderName;

    public static GitHubActionsSettings Read(GitDistributedSettingsSnapshot snapshot, IEnumerable<string>? remotes = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? owner = snapshot.GetValue(OwnerKey), repository = snapshot.GetValue(RepositoryKey);
        // Detection fills missing values only, exactly as the Windows detector. It never writes or enables CI.
        GitHubRemoteParser parser = new();
        foreach (string remote in remotes ?? [])
        {
            if (!parser.TryExtractGitHubDataFromRemoteUrl(remote, out string? detectedOwner, out string? detectedRepository)) { continue; }
            if (string.IsNullOrWhiteSpace(owner)) { owner = detectedOwner; }
            if (string.IsNullOrWhiteSpace(repository)) { repository = detectedRepository; }
            break;
        }
        GitHubActionsSettings result = new()
        {
            Enabled = Boolean(snapshot.GetValue(EnableKey)) == true,
            ShowBuildResultPage = Boolean(snapshot.GetValue(ShowPageKey)) == true,
            Provider = snapshot.GetValue(ProviderKey) ?? "", ApiUrl = snapshot.GetValue(ApiUrlKey) ?? DefaultApiUrl,
            Owner = owner ?? "", Repository = repository ?? "", Token = snapshot.GetValue(TokenKey) ?? ""
        };
        if (result.Active && result.Token.Length > 0 && snapshot.GetSource(TokenKey) is { } tokenScope)
        {
            // A repository override must never redirect an inherited global token to a different origin.
            string tokenEndpoint = snapshot.GetValue(ApiUrlKey, tokenScope) ?? DefaultApiUrl;
            if (!SameOrigin(ApiUri(result.ApiUrl), ApiUri(tokenEndpoint)))
            { throw new ArgumentException("The token and API endpoint belong to different settings origins. Configure them together in the selected scope."); }
        }
        return result;
    }

    public void Validate()
    {
        Uri endpoint = ApiUri(ApiUrl);
        ValidateSegment(Owner, "Enter a GitHub repository owner.");
        ValidateSegment(Repository, "Enter a GitHub repository name.");
        if (Token.Length == 0) { return; }
        if (endpoint.Scheme != Uri.UriSchemeHttps) { throw new ArgumentException("An API token requires HTTPS."); }
        if (Token.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c))))
        { throw new ArgumentException("The API token contains invalid characters."); }
    }

    public static Uri ApiUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { throw new ArgumentException("Enter an HTTP or HTTPS API endpoint without credentials, query or fragment."); }
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    public static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;
    public static bool? Boolean(string? value) => bool.TryParse(value, out bool result) ? result : null;
    private static void ValidateSegment(string value, string error)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "/\\?#".Contains(c)))
        { throw new ArgumentException(error); }
    }
    public override string ToString() => "GitHub Actions settings (token redacted)";
}
