using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux;

/// <summary>Copies the original mutable credential model at an ownership boundary. Never prints secrets.</summary>
public sealed class GitHubHostingCredentials
{
    public BuildServerCredentialsType Type { get; }
    public string? Username { get; }
    public string? Password { get; }
    public string? BearerToken { get; }
    private GitHubHostingCredentials(IBuildServerCredentials? value)
    { Type = value?.BuildServerCredentialsType ?? BuildServerCredentialsType.Guest; Username = value?.Username; Password = value?.Password; BearerToken = value?.BearerToken; }
    public static GitHubHostingCredentials From(IBuildServerCredentials? value) => new(value);
    public BuildServerCredentials ToDraft() => new()
    { BuildServerCredentialsType = Type, Username = Username, Password = Password, BearerToken = BearerToken };
    public static BuildServerCredentials CloneDraft(IBuildServerCredentials? value) => From(value).ToDraft();
    public void Validate(GitHubHostingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        if (!Enum.IsDefined(Type)) { throw new ArgumentException("Choose a supported credential type."); }
        if (Type == BuildServerCredentialsType.Guest) { return; }
        if (GitHubHostingSettings.Endpoint(settings.ApiUrl).Scheme != Uri.UriSchemeHttps) { throw new ArgumentException("Hosting credentials require an HTTPS API endpoint."); }
        if (Type == BuildServerCredentialsType.BearerToken && (string.IsNullOrEmpty(BearerToken)
            || BearerToken.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c)))))
        { throw new ArgumentException("Enter a valid bearer token."); }
        if (Type == BuildServerCredentialsType.UsernameAndPassword && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)
            || Username.Contains(':') || Username.Any(char.IsControl) || Password.Any(char.IsControl)))
        { throw new ArgumentException("Enter a username and password without control characters."); }
    }
    public override string ToString() => "GitHub credentials (redacted)";
}
