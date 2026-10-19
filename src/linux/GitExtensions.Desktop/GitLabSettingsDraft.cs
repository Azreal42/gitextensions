using System.Globalization;

namespace GitExtensions.Linux.Desktop;

internal sealed partial class GitHubActionsSettingsSession
{
    internal GitLabSettings GitLab(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        string? rawUrl = Read(scope, GitLabSettings.InstanceUrlKey);
        GitLabSettings result = new()
        {
            Enabled = GitHubActionsSettings.Boolean(Read(scope, GitLabSettings.EnableKey)) == true,
            ShowBuildResultPage = GitHubActionsSettings.Boolean(Read(scope, GitLabSettings.ShowPageKey)) == true,
            Provider = Read(scope, GitLabSettings.ProviderKey) ?? "",
            InstanceUrl = rawUrl ?? DetectedGitLabInstance(),
            ProjectId = long.TryParse(Read(scope, GitLabSettings.ProjectIdKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out long project) ? project : 0,
            ApiToken = Read(scope, GitLabSettings.ApiTokenKey) ?? "",
            PagesLimit = string.IsNullOrEmpty(Read(scope, GitLabSettings.PagesLimitKey)) ? 0 : int.TryParse(Read(scope, GitLabSettings.PagesLimitKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages) ? pages : -1
        };
        if (result.Active && result.ApiToken.Length > 0 && scope == GitDistributedSettingsScope.Effective)
        {
            foreach (GitDistributedSettingsScope origin in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
            {
                if (Read(origin, GitLabSettings.ApiTokenKey) is null) { continue; }
                Uri tokenInstance = GitLabSettings.ParseInstanceUri(Read(origin, GitLabSettings.InstanceUrlKey) ?? DetectedGitLabInstance());
                if (!GitLabSettings.SameInstance(tokenInstance, result.InstanceUri))
                { throw new ArgumentException("The GitLab token and instance belong to different settings origins. Configure them together in the selected scope."); }
                break;
            }
        }
        return result;
    }
    private string DetectedGitLabInstance()
    {
        foreach (string remote in _remoteUrls)
        { if (new GitLabRemoteParser().TryParse(remote, out GitLabRemote? value)) { return value.InstanceUri.AbsoluteUri.TrimEnd('/'); } }
        return "";
    }
    private void BindGitLabDraft(GitDistributedSettingsScope scope, string key, string? value)
    {
        if (key == GitLabSettings.ApiTokenKey && !string.IsNullOrEmpty(value))
        {
            string endpoint = Read(scope, GitLabSettings.InstanceUrlKey) ?? Read(GitDistributedSettingsScope.Effective, GitLabSettings.InstanceUrlKey) ?? DetectedGitLabInstance();
            Set(scope, GitLabSettings.InstanceUrlKey, endpoint);
        }
        // The Windows page always saves PagesLimit=0; retain untouched files, but use its value for actual page edits.
        if (key != GitLabSettings.PagesLimitKey && (key.StartsWith(GitLabSettings.Prefix, StringComparison.Ordinal)
            || key == GitLabSettings.ProviderKey && value == GitLabSettings.ProviderName))
        { Set(scope, GitLabSettings.PagesLimitKey, "0"); }
    }
}
