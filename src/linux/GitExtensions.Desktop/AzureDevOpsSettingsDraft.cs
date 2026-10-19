namespace GitExtensions.Linux.Desktop;

internal sealed partial class GitHubActionsSettingsSession
{
    private sealed class AzureTokenBinding(string token, string project)
    {
        internal string Token { get; } = token;
        internal string Project { get; } = project;
    }
    private readonly Dictionary<GitDistributedSettingsScope, AzureTokenBinding> _azureTokenBindings = [];

    internal AzureDevOpsSettings AzureDevOps(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        AzureDevOpsSettings settings = new()
        {
            Enabled = GitHubActionsSettings.Boolean(Read(scope, AzureDevOpsSettings.EnableKey)) == true,
            ShowBuildResultPage = GitHubActionsSettings.Boolean(Read(scope, AzureDevOpsSettings.ShowPageKey)) == true,
            Provider = Read(scope, AzureDevOpsSettings.ProviderKey) ?? "",
            ProjectUrl = string.IsNullOrWhiteSpace(Read(scope, AzureDevOpsSettings.ProjectUrlKey)) ? DetectedAzureDevOpsProject() : Read(scope, AzureDevOpsSettings.ProjectUrlKey)!,
            BuildDefinitionFilter = Read(scope, AzureDevOpsSettings.BuildDefinitionFilterKey) ?? "",
            ApiToken = Read(scope, AzureDevOpsSettings.ApiTokenKey) ?? "",
            RepositoryName = Read(scope, AzureDevOpsSettings.RepositoryNameKey) ?? ""
        };
        if (settings.Active && settings.ApiToken.Length > 0
            && !CanUseAzureDevOpsToken(scope, settings.ApiToken, settings.ProjectUrl))
        { throw new ArgumentException("Enter the Azure DevOps PAT again for this project URL. A saved PAT cannot be reused for another project, origin or base path."); }
        return settings;
    }

    internal string DetectedAzureDevOpsProject()
    {
        foreach (string remote in AzureDevOpsRemoteUrls)
        {
            if (AzureDevOpsProjectUrls.TryDetectProjectFromRemoteUrl(remote, out AzureDevOpsRemote? detected))
            { return detected.ProjectUri.AbsoluteUri.TrimEnd('/'); }
        }
        return "";
    }

    private void EnsureAzureDevOpsTokenBindings()
    {
        if (_workspace.Snapshot is not { } snapshot) { return; }
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        {
            if (!snapshot.IsAvailable(scope) || _azureTokenBindings.ContainsKey(scope)) { continue; }
            // The acknowledged XML, rather than the edited project URL, owns an existing PAT.
            _azureTokenBindings.Add(scope, new(snapshot.GetValue(AzureDevOpsSettings.ApiTokenKey, scope) ?? "",
                snapshot.GetValue(AzureDevOpsSettings.ProjectUrlKey, scope) ?? ""));
        }
    }

    internal bool CanUseAzureDevOpsToken(GitDistributedSettingsScope scope, string token, string projectUrl)
    {
        EnsureAzureDevOpsTokenBindings();
        GitDistributedSettingsScope origin = scope;
        if (scope == GitDistributedSettingsScope.Effective)
        {
            GitDistributedSettingsScope? found = null;
            foreach (GitDistributedSettingsScope candidate in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
            { if (Read(candidate, AzureDevOpsSettings.ApiTokenKey) is not null) { found = candidate; break; } }
            if (!found.HasValue) { return false; }
            origin = found.Value;
        }
        if (!_azureTokenBindings.TryGetValue(origin, out AzureTokenBinding? binding) || binding.Token != token
            || string.IsNullOrWhiteSpace(Read(origin, AzureDevOpsSettings.ProjectUrlKey))) { return false; }
        try
        {
            // Syntax validation may accept templates, but an authorization decision uses only real repository substitutions.
            Uri project = AzureDevOpsProjectUrls.ParseProjectUri(ExpandBuildServerVariables(projectUrl));
            Uri bound = AzureDevOpsProjectUrls.ParseProjectUri(ExpandBuildServerVariables(binding.Project));
            Uri current = AzureDevOpsProjectUrls.ParseProjectUri(ExpandBuildServerVariables(Read(origin, AzureDevOpsSettings.ProjectUrlKey)!));
            return AzureDevOpsSettings.SameProject(bound, project) && AzureDevOpsSettings.SameProject(current, project);
        }
        catch (ArgumentException) { return false; }
    }

    // Called by Set, after the intention has been recorded. Only actual PAT edits can bind/rebind it.
    private void BindAzureDevOpsDraft(GitDistributedSettingsScope scope, string key, string? value)
    {
        if (key != AzureDevOpsSettings.ApiTokenKey) { return; }
        EnsureAzureDevOpsTokenBindings();
        string endpoint = Read(scope, AzureDevOpsSettings.ProjectUrlKey) ?? DetectedAzureDevOpsProject();
        BindAzureDevOpsTokenToDisplayedProject(scope, value, endpoint);
    }

    internal void BindAzureDevOpsTokenToDisplayedProject(GitDistributedSettingsScope scope, string? value, string endpoint)
    {
        EnsureAzureDevOpsTokenBindings();
        _azureTokenBindings[scope] = new(value ?? "", endpoint);
        if (!string.IsNullOrEmpty(value)) { Set(scope, AzureDevOpsSettings.ProjectUrlKey, endpoint.Length == 0 ? null : endpoint); }
    }

    internal void PrepareAzureDevOpsProject(GitDistributedSettingsScope scope, string displayedDefault, bool projectEdited, bool activated)
    {
        if (!Loaded || scope == GitDistributedSettingsScope.Effective || _workspace.Snapshot?.IsAvailable(scope) != true
            || Read(scope, AzureDevOpsSettings.ProviderKey) != AzureDevOpsSettings.ProviderName || projectEdited
            || !string.IsNullOrWhiteSpace(Read(scope, AzureDevOpsSettings.ProjectUrlKey)) || displayedDefault.Length == 0) { return; }
        bool edited = _intentions.TryGetValue(scope, out Dictionary<string, string?>? changes)
            && changes.Keys.Any(key => key.StartsWith(AzureDevOpsSettings.Prefix, StringComparison.Ordinal));
        if (activated || edited) { Set(scope, AzureDevOpsSettings.ProjectUrlKey, displayedDefault); }
    }

    internal void ValidateAzureDevOpsEdits()
    {
        foreach ((GitDistributedSettingsScope scope, Dictionary<string, string?> changes) in _intentions)
        {
            if (changes.ContainsKey(AzureDevOpsSettings.BuildDefinitionFilterKey))
            { _ = new AzureDevOpsSettings { BuildDefinitionFilter = Read(scope, AzureDevOpsSettings.BuildDefinitionFilterKey) ?? "" }.CreateBuildDefinitionRegex(); }
            bool edited = changes.Keys.Any(key => key.StartsWith(AzureDevOpsSettings.Prefix, StringComparison.Ordinal))
                || changes.GetValueOrDefault(AzureDevOpsSettings.ProviderKey) == AzureDevOpsSettings.ProviderName;
            string token = Read(scope, AzureDevOpsSettings.ApiTokenKey) ?? "";
            if (edited && token.Length > 0 && !CanUseAzureDevOpsToken(scope, token,
                Read(scope, AzureDevOpsSettings.ProjectUrlKey) ?? DetectedAzureDevOpsProject()))
            { throw new ArgumentException("Enter the Azure DevOps PAT again for this project URL. A saved PAT cannot be reused for another project, origin or base path."); }
        }
    }
}
