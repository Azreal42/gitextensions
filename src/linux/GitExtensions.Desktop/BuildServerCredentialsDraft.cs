using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

internal sealed partial class GitHubActionsSettingsSession
{
    private sealed record CredentialsDraft(BuildServerCredentialsSnapshot Baseline, BuildServerCredentials? Draft);
    private readonly Dictionary<(string Provider, string Endpoint), CredentialsDraft> _credentials = [];
    internal BuildServerCredentialsStore CredentialsStore { get; set; } = new();
    private bool HasCredentialChanges => _credentials.Values.Any(value => value.Draft is not null);
    internal BuildServerCredentialsSnapshot ReadCredentials(Uri endpoint) => ReadCredentials(TeamCitySettings.ProviderName, endpoint);
    internal BuildServerCredentialsSnapshot ReadCredentials(string provider, Uri endpoint)
    {
        var key = (provider, endpoint.AbsoluteUri);
        if (!_credentials.TryGetValue(key, out CredentialsDraft? value))
        {
            value = new(CredentialsStore.Load(provider, endpoint), null); _credentials.Add(key, value);
        }
        return value.Baseline;
    }
    internal BuildServerCredentials? CredentialsFor(TeamCitySettings settings) => CredentialsFor(TeamCitySettings.ProviderName, settings.ServerUri);
    internal BuildServerCredentials? CredentialsFor(JenkinsSettings settings) => CredentialsFor(JenkinsSettings.ProviderName, settings.ServerUri);
    internal BuildServerCredentials? CredentialsFor(string provider, Uri endpoint)
    {
        BuildServerCredentialsSnapshot baseline = ReadCredentials(provider, endpoint);
        CredentialsDraft value = _credentials[(provider, endpoint.AbsoluteUri)];
        return value.Draft is not null ? CloneCredentials(value.Draft) : baseline.Exists ? baseline.Credentials : null;
    }
    internal BuildServerCredentials CredentialDraft(Uri endpoint) => CredentialDraft(TeamCitySettings.ProviderName, endpoint);
    internal BuildServerCredentials CredentialDraft(string provider, Uri endpoint)
    {
        BuildServerCredentialsSnapshot baseline = ReadCredentials(provider, endpoint);
        return _credentials[(provider, endpoint.AbsoluteUri)].Draft is { } draft ? CloneCredentials(draft) : baseline.ToDraft();
    }
    internal void StageCredentials(Uri endpoint, BuildServerCredentials credentials) => StageCredentials(TeamCitySettings.ProviderName, endpoint, credentials);
    internal void StageCredentials(string provider, Uri endpoint, BuildServerCredentials credentials)
    {
        _ = ReadCredentials(provider, endpoint);
        var key = (provider, endpoint.AbsoluteUri);
        CredentialsDraft value = _credentials[key];
        BuildServerCredentials baseline = value.Baseline.Credentials;
        bool equal = value.Baseline.Exists && baseline.BuildServerCredentialsType == credentials.BuildServerCredentialsType
            && baseline.Username == credentials.Username && baseline.Password == credentials.Password && baseline.BearerToken == credentials.BearerToken;
        _credentials[key] = value with { Draft = equal ? null : CloneCredentials(credentials) };
    }
    private void SaveCredentials()
    {
        foreach (var (key, value) in _credentials.ToArray())
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
