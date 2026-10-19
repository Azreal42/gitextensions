using System.Collections.ObjectModel;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux;

/// <summary>Pure codec over the existing XDG Preferences.PluginSettings GitHub plugin group. No file or CI-token access.</summary>
public sealed record GitHubHostingSettings(string WebUrl = "https://github.com", string ApiUrl = "https://api.github.com")
{
    public const string PluginId = "2ec3e1f0-ef37-413f-bea5-b8fe1f9c505c";
    public const string TokenKey = "OAuth Token";
    public const string HostnameKey = "GitHub (Enterprise) hostname";
    public const string WebUrlKey = "Web URL";
    public const string ApiUrlKey = "API URL";
    public const string CredentialsTypeKey = "Credentials Type";
    public const string UsernameKey = "Username";
    public const string PasswordKey = "Password";
    public const string CredentialsOriginKey = "Credentials API Origin";

    public void Validate() { Endpoint(WebUrl); Endpoint(ApiUrl); }
    public bool TryParseRemote(string remoteUrl, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GitHubRepositoryId? repository)
        => GitHubHostingRemotes.TryParse(remoteUrl, this, out repository);
    internal static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { throw new ArgumentException("Enter an explicit HTTP or HTTPS hosting endpoint without credentials, query or fragment."); }
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
    internal static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "/\\?#".Contains(c)))
        { throw new ArgumentException("Enter a valid GitHub owner or repository name."); }
        return Uri.EscapeDataString(value);
    }
    public Uri? SafeWebUri(string? value)
    {
        Uri root = Endpoint(WebUrl);
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && SameOrigin(root, uri) && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal) ? uri : null;
    }
    public Uri PullRequestUri(GitHubRepositoryId repository, int number)
    {
        ArgumentNullException.ThrowIfNull(repository); if (number <= 0) { throw new ArgumentOutOfRangeException(nameof(number)); }
        return new(Endpoint(WebUrl), Segment(repository.Owner) + "/" + Segment(repository.Name) + "/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public static GitHubHostingSettings FromPluginSettings(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string host = values.GetValueOrDefault(HostnameKey)?.Trim() ?? "github.com";
        string web = values.GetValueOrDefault(WebUrlKey) ?? "https://" + host;
        string api = values.GetValueOrDefault(ApiUrlKey) ?? (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com" : throw new ArgumentException("Enter the GitHub Enterprise API URL explicitly, for example https://host/api/v3/; it is not inferred from the web hostname."));
        GitHubHostingSettings result = new(web, api); result.Validate(); return result;
    }
    private static BuildServerCredentials RawCredentials(IReadOnlyDictionary<string, string?> values)
    {
        string? type = values.GetValueOrDefault(CredentialsTypeKey);
        BuildServerCredentialsType mode = type is null ? string.IsNullOrEmpty(values.GetValueOrDefault(TokenKey)) ? BuildServerCredentialsType.Guest : BuildServerCredentialsType.BearerToken
            : Enum.TryParse(type, ignoreCase: false, out BuildServerCredentialsType parsed) && Enum.IsDefined(parsed) ? parsed
            : throw new ArgumentException("Choose Guest, Username and password or Bearer token credentials.");
        return new() { BuildServerCredentialsType = mode, Username = values.GetValueOrDefault(UsernameKey), Password = values.GetValueOrDefault(PasswordKey), BearerToken = values.GetValueOrDefault(TokenKey) };
    }
    public GitHubHostingCredentials CredentialsFromPluginSettings(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values); BuildServerCredentials draft = RawCredentials(values);
        if (draft.BuildServerCredentialsType != BuildServerCredentialsType.Guest)
        {
            // A legacy original OAuth Token belongs to api.github.com, never to an arbitrary new Enterprise URL.
            Uri origin = Endpoint(values.GetValueOrDefault(CredentialsOriginKey) ?? "https://api.github.com");
            if (!SameOrigin(origin, Endpoint(ApiUrl))) { throw new ArgumentException("These hosting credentials belong to a different API origin. Enter credentials explicitly for the configured API endpoint."); }
        }
        GitHubHostingCredentials result = GitHubHostingCredentials.From(draft); result.Validate(this); return result;
    }
    public IReadOnlyDictionary<string, string?> WithCredentials(IReadOnlyDictionary<string, string?> values, IBuildServerCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(credentials); Validate();
        GitHubHostingCredentials snapshot = GitHubHostingCredentials.From(credentials);
        snapshot.Validate(this);
        Dictionary<string, string?> copy = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        copy[CredentialsTypeKey] = snapshot.Type.ToString(); copy[UsernameKey] = snapshot.Username;
        copy[PasswordKey] = snapshot.Password; copy[TokenKey] = snapshot.BearerToken;
        copy[CredentialsOriginKey] = Endpoint(ApiUrl).GetLeftPart(UriPartial.Authority);
        copy[WebUrlKey] = WebUrl; copy[ApiUrlKey] = ApiUrl;
        return new ReadOnlyDictionary<string, string?>(copy);
    }
    public IReadOnlyDictionary<string, string?> WithPersonalAccessToken(IReadOnlyDictionary<string, string?> values, string token)
    {
        ArgumentNullException.ThrowIfNull(token); BuildServerCredentials draft = RawCredentials(values);
        draft.BearerToken = token; draft.BuildServerCredentialsType = token.Length == 0 ? BuildServerCredentialsType.Guest : BuildServerCredentialsType.BearerToken;
        return WithCredentials(values, draft);
    }
    public override string ToString() => "GitHub hosting settings";
}
