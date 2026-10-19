using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

internal sealed partial class GitHubActionsSettingsSession
{
    private sealed record CredentialsDraft(BuildServerCredentialsSnapshot Baseline, BuildServerCredentials? Draft);
    private readonly Dictionary<string, CredentialsDraft> _credentials = new(StringComparer.Ordinal);
    internal BuildServerCredentialsStore CredentialsStore { get; set; } = new();
    private bool HasCredentialChanges => _credentials.Values.Any(value => value.Draft is not null);
    internal BuildServerCredentialsSnapshot ReadCredentials(Uri endpoint)
    {
        string key = endpoint.AbsoluteUri;
        if (!_credentials.TryGetValue(key, out CredentialsDraft? value))
        {
            value = new(CredentialsStore.Load(TeamCitySettings.ProviderName, endpoint), null); _credentials.Add(key, value);
        }
        return value.Baseline;
    }
    internal BuildServerCredentials? CredentialsFor(TeamCitySettings settings)
    {
        BuildServerCredentialsSnapshot baseline = ReadCredentials(settings.ServerUri);
        CredentialsDraft value = _credentials[settings.ServerUri.AbsoluteUri];
        return value.Draft is not null ? CloneCredentials(value.Draft) : baseline.Exists ? baseline.Credentials : null;
    }
    internal BuildServerCredentials CredentialDraft(Uri endpoint)
    {
        BuildServerCredentialsSnapshot baseline = ReadCredentials(endpoint);
        return _credentials[endpoint.AbsoluteUri].Draft is { } draft ? CloneCredentials(draft) : baseline.ToDraft();
    }
    internal void StageCredentials(Uri endpoint, BuildServerCredentials credentials)
    {
        _ = ReadCredentials(endpoint);
        CredentialsDraft value = _credentials[endpoint.AbsoluteUri];
        BuildServerCredentials baseline = value.Baseline.Credentials;
        bool equal = value.Baseline.Exists && baseline.BuildServerCredentialsType == credentials.BuildServerCredentialsType
            && baseline.Username == credentials.Username && baseline.Password == credentials.Password && baseline.BearerToken == credentials.BearerToken;
        _credentials[endpoint.AbsoluteUri] = value with { Draft = equal ? null : CloneCredentials(credentials) };
    }
    private void SaveCredentials()
    {
        foreach ((string key, CredentialsDraft value) in _credentials.ToArray())
        {
            if (value.Draft is null) { continue; }
            BuildServerCredentialsSnapshot saved = CredentialsStore.SaveChanges(value.Baseline, value.Draft);
            _credentials[key] = new(saved, null);
            GitHubActionsSettingsPublication.Publish(DetailedRepositorySettings.GlobalPath);
        }
    }
    private static BuildServerCredentials CloneCredentials(IBuildServerCredentials value) => new()
    { BuildServerCredentialsType = value.BuildServerCredentialsType, Username = value.Username, Password = value.Password, BearerToken = value.BearerToken };
}
