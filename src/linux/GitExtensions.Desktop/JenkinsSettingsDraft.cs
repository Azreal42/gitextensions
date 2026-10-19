namespace GitExtensions.Linux.Desktop;

internal sealed partial class GitHubActionsSettingsSession
{
    // The folder suggestion is a Windows settings-page default, not a runtime project or a remote-derived job.
    internal JenkinsSettings Jenkins(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective) => new()
    {
        Enabled = GitHubActionsSettings.Boolean(Read(scope, JenkinsSettings.EnableKey)) == true,
        ShowBuildResultPage = GitHubActionsSettings.Boolean(Read(scope, JenkinsSettings.ShowPageKey)) == true,
        Provider = Read(scope, JenkinsSettings.ProviderKey) ?? "",
        ServerUrl = Read(scope, JenkinsSettings.ServerUrlKey) ?? "",
        ProjectName = ExpandBuildServerVariables(Read(scope, JenkinsSettings.ProjectNameKey) ?? ""),
        IgnoreBuildBranch = Read(scope, JenkinsSettings.IgnoreBuildBranchKey) ?? ""
    };

    internal void PrepareJenkinsProject(GitDistributedSettingsScope scope, string displayedDefault, bool projectEdited, bool activated)
    {
        if (!Loaded || scope == GitDistributedSettingsScope.Effective || _workspace.Snapshot?.IsAvailable(scope) != true
            || Read(scope, JenkinsSettings.ProviderKey) != JenkinsSettings.ProviderName || projectEdited
            || Read(scope, JenkinsSettings.ProjectNameKey) is not null || displayedDefault.Length == 0) { return; }
        bool edited = _intentions.TryGetValue(scope, out Dictionary<string, string?>? changes)
            && changes.Keys.Any(key => key.StartsWith(JenkinsSettings.Prefix, StringComparison.Ordinal));
        if (activated || edited) { Set(scope, JenkinsSettings.ProjectNameKey, displayedDefault); }
    }

    internal void ValidateJenkinsEdits()
    {
        foreach ((GitDistributedSettingsScope scope, Dictionary<string, string?> changes) in _intentions)
        {
            if (changes.ContainsKey(JenkinsSettings.IgnoreBuildBranchKey))
            { _ = Jenkins(scope).CreateIgnoreBuildBranchRegex(); }
        }
    }
}
