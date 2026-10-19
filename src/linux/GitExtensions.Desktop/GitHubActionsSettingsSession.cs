namespace GitExtensions.Linux.Desktop;

/// <summary>All CI provider pages share one draft and the Settings window's acknowledged XML baselines.</summary>
internal sealed partial class GitHubActionsSettingsSession
{
    private static readonly HashSet<string> KnownKeys = GitHubActionsSettings.Keys.Concat(TeamCitySettings.Keys)
        .Concat(GitLabSettings.Keys).Concat(AppVeyorSettings.Keys).Concat(AzureDevOpsSettings.Keys).Concat(JenkinsSettings.Keys).ToHashSet(StringComparer.Ordinal);
    private readonly GitRepository? _repository;
    private readonly DistributedSettingsWorkspace _workspace;
    private readonly Dictionary<GitDistributedSettingsScope, Dictionary<string, string?>> _intentions = [];
    private IReadOnlyList<string> _remoteUrls = [];
    internal IReadOnlyList<string> AzureDevOpsRemoteUrls { get; private set; } = [];
    private BuildServerRepositoryVariables? _buildServerVariables;
    internal string ExpandBuildServerVariables(string value) => _buildServerVariables?.Expand(value) ?? value;
    internal bool Loaded => _workspace.Loaded;
    internal bool HasChanges => _intentions.Values.Any(v => v.Count > 0) || HasCredentialChanges;
    internal Exception? LoadError => _workspace.LoadError;
    internal string? DetectionError { get; private set; }
    internal bool HasRepository => _repository is not null;
    internal bool HasDistributed => _workspace.Snapshot?.Distributed is not null;
    internal GitDistributedSettingsScope InitialScope => HasRepository ? GitDistributedSettingsScope.Effective : GitDistributedSettingsScope.Global;
    internal GitHubActionsSettingsSession(GitRepository? repository, DistributedSettingsWorkspace? workspace = null)
    { _repository = repository; _workspace = workspace ?? new(repository); }

    internal async Task InitializeAsync(CancellationToken token)
    {
        await _workspace.InitializeAsync(token);
        if (!Loaded || _repository is null) { return; }
        try
        {
            IReadOnlyList<GitRemote> remotes = await _repository.GetRemotesAsync(token);
            _remoteUrls = remotes.SelectMany(r => r.PushUrls.Count > 0 ? r.PushUrls : r.FetchUrls).ToArray();
            AzureDevOpsRemoteUrls = remotes.SelectMany(r => r.FetchUrls).ToArray();
            _buildServerVariables = await _repository.GetBuildServerRepositoryVariablesAsync(token);
        }
        catch (Exception error) when (error is IOException or GitCommandException or InvalidOperationException)
        { DetectionError = "Remote detection is unavailable. Enter the owner and repository explicitly."; }
    }
    internal IReadOnlyList<string> RemoteUrls => _remoteUrls;
    internal string DefaultProjectName => _repository is null ? "" : Path.GetFileName(_repository.Path.TrimEnd(Path.DirectorySeparatorChar));
    internal TeamCitySettings TeamCity(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective) => new()
    {
        Enabled = GitHubActionsSettings.Boolean(Read(scope, TeamCitySettings.EnableKey)) == true,
        ShowBuildResultPage = GitHubActionsSettings.Boolean(Read(scope, TeamCitySettings.ShowPageKey)) == true,
        Provider = Read(scope, TeamCitySettings.ProviderKey) ?? "", ServerUrl = Read(scope, TeamCitySettings.ServerUrlKey) ?? "",
        ProjectName = Read(scope, TeamCitySettings.ProjectNameKey) ?? DefaultProjectName,
        BuildIdFilter = Read(scope, TeamCitySettings.BuildIdFilterKey) ?? "",
        LogAsGuest = GitHubActionsSettings.Boolean(Read(scope, TeamCitySettings.LogAsGuestKey)) == true
    };
    internal string? ScopePath(GitDistributedSettingsScope scope) => scope == GitDistributedSettingsScope.Effective
        ? null : _workspace.Snapshot?.IsAvailable(scope) == true ? _workspace.Snapshot.GetSnapshot(scope).Path : null;
    internal string? Read(GitDistributedSettingsScope scope, string key)
    {
        if (!KnownKeys.Contains(key)) { throw new ArgumentException("Unknown CI key.", nameof(key)); }
        if (scope == GitDistributedSettingsScope.Effective)
        { return Read(GitDistributedSettingsScope.Local, key) ?? Read(GitDistributedSettingsScope.Distributed, key) ?? Read(GitDistributedSettingsScope.Global, key); }
        if (_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes) && changes.TryGetValue(key, out string? value)) { return value; }
        GitDistributedSettingsSnapshot? snapshot = _workspace.Snapshot;
        return snapshot?.IsAvailable(scope) == true ? snapshot.GetValue(key, scope) : null;
    }
    internal void Set(GitDistributedSettingsScope scope, string key, string? value)
    {
        if (scope == GitDistributedSettingsScope.Effective) { throw new InvalidOperationException("Effective settings are read-only."); }
        if (!KnownKeys.Contains(key)) { throw new ArgumentException("Unknown CI key.", nameof(key)); }
        GitXmlSettingsSnapshot file = (_workspace.Snapshot ?? throw new InvalidOperationException("CI settings have not been loaded.")).GetSnapshot(scope);
        if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes)) { _intentions.Add(scope, changes = new(StringComparer.Ordinal)); }
        if (value is null ? !file.Values.ContainsKey(key) : file.Values.GetValueOrDefault(key) == value) { changes.Remove(key); }
        else { changes[key] = value; }
        BindGitLabDraft(scope, key, value);
        BindAzureDevOpsDraft(scope, key, value);
        if (key == GitHubActionsSettings.TokenKey && !string.IsNullOrEmpty(value))
        {
            // Store the endpoint with an explicitly entered token so a later inherited URL cannot retarget it.
            string endpoint = Read(scope, GitHubActionsSettings.ApiUrlKey) ?? Read(GitDistributedSettingsScope.Effective, GitHubActionsSettings.ApiUrlKey) ?? GitHubActionsSettings.DefaultApiUrl;
            Set(scope, GitHubActionsSettings.ApiUrlKey, endpoint);
        }
    }
    internal (string Owner, string Repository) DetectedRepository
    {
        get
        {
            GitCommands.Remotes.GitHubRemoteParser parser = new();
            foreach (string remote in _remoteUrls)
            { if (parser.TryExtractGitHubDataFromRemoteUrl(remote, out string? owner, out string? repository)) { return (owner, repository); } }
            return ("", "");
        }
    }
    internal async Task SaveFilesAsync(CancellationToken token)
    {
        if (!HasChanges) { return; }
        if (!Loaded) { throw new InvalidOperationException("CI settings could not be loaded."); }
        string? Raw(string key) => Read(GitDistributedSettingsScope.Effective, key);
        (string owner, string repo) = DetectedRepository;
        GitHubActionsSettings effective = new()
        {
            Enabled = GitHubActionsSettings.Boolean(Raw(GitHubActionsSettings.EnableKey)) == true,
            ShowBuildResultPage = GitHubActionsSettings.Boolean(Raw(GitHubActionsSettings.ShowPageKey)) == true,
            Provider = Raw(GitHubActionsSettings.ProviderKey) ?? "", ApiUrl = Raw(GitHubActionsSettings.ApiUrlKey) ?? GitHubActionsSettings.DefaultApiUrl,
            Owner = string.IsNullOrWhiteSpace(Raw(GitHubActionsSettings.OwnerKey)) ? owner : Raw(GitHubActionsSettings.OwnerKey)!,
            Repository = string.IsNullOrWhiteSpace(Raw(GitHubActionsSettings.RepositoryKey)) ? repo : Raw(GitHubActionsSettings.RepositoryKey)!,
            Token = Raw(GitHubActionsSettings.TokenKey) ?? ""
        };
        if (_intentions.Values.Any(v => v.Count > 0) && effective.Active) { effective.Validate(); }
        GitLabSettings gitLab = GitLab();
        if (gitLab.Active) { gitLab.Validate(); }
        AppVeyorSettings appVeyor = AppVeyor();
        if (appVeyor.Active) { appVeyor.Validate(); }
        AzureDevOpsSettings azureDevOps = AzureDevOps();
        if (azureDevOps.Active) { azureDevOps.Validate(); }
        ValidateAzureDevOpsEdits();
        JenkinsSettings jenkins = Jenkins();
        if (jenkins.Active) { jenkins.Validate(); }
        ValidateJenkinsEdits();
        TeamCitySettings teamCity = TeamCity();
        if (teamCity.Active) { teamCity.Validate(); }
        // The original settings page rejects malformed regex even before integration is enabled.
        if (_intentions.Values.Any(changes => changes.ContainsKey(TeamCitySettings.BuildIdFilterKey))) { _ = teamCity.CreateBuildIdRegex(); }
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        {
            if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes) || changes.Count == 0) { continue; }
            // Read the current workspace on each destination; earlier page/file acknowledgements are real CAS baselines.
            GitDistributedSettingsSnapshot before = _workspace.Snapshot!;
            GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.SaveChangesAsync(before.GetSnapshot(scope), new Dictionary<string, string?>(changes), token);
            _workspace.AcceptSaved(before.WithSaved(scope, saved)); changes.Clear();
            GitHubActionsSettingsPublication.Publish(saved.Path);
        }
        SaveCredentials();
    }
}

internal static class GitHubActionsSettingsPublication
{
    internal static event Action<string>? Saved;
    internal static void Publish(string path) => Saved?.Invoke(path);
}
