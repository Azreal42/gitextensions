namespace GitExtensions.Linux.Desktop;

internal sealed partial class GitHubActionsSettingsSession
{
    /// <summary>The runtime's empty project means account discovery. The folder suggestion belongs only to the page.</summary>
    internal AppVeyorSettings AppVeyor(GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective) => new()
    {
        Enabled = GitHubActionsSettings.Boolean(Read(scope, AppVeyorSettings.EnableKey)) == true,
        ShowBuildResultPage = GitHubActionsSettings.Boolean(Read(scope, AppVeyorSettings.ShowPageKey)) == true,
        Provider = Read(scope, AppVeyorSettings.ProviderKey) ?? "",
        AccountName = Read(scope, AppVeyorSettings.AccountNameKey) ?? "",
        AccountToken = Read(scope, AppVeyorSettings.AccountTokenKey) ?? "",
        ProjectName = Read(scope, AppVeyorSettings.ProjectNameKey) ?? "",
        LoadTestsResults = GitHubActionsSettings.Boolean(Read(scope, AppVeyorSettings.LoadTestsResultsKey)) == true
    };

    /// <summary>Only an explicit save of a real AppVeyor edit/activation accepts the visible Windows folder suggestion.</summary>
    internal void PrepareAppVeyorProject(GitDistributedSettingsScope scope, string displayedDefault,
        bool projectEdited, bool activated)
    {
        if (!Loaded || scope == GitDistributedSettingsScope.Effective || _workspace.Snapshot?.IsAvailable(scope) != true
            || Read(scope, AppVeyorSettings.ProviderKey) != AppVeyorSettings.ProviderName || projectEdited
            || Read(scope, AppVeyorSettings.ProjectNameKey) is not null || displayedDefault.Length == 0) { return; }
        bool edited = _intentions.TryGetValue(scope, out Dictionary<string, string?>? changes)
            && changes.Keys.Any(key => key.StartsWith(AppVeyorSettings.Prefix, StringComparison.Ordinal));
        if (activated || edited) { Set(scope, AppVeyorSettings.ProjectNameKey, displayedDefault); }
    }
}
