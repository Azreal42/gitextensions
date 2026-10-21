using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

internal static class BackgroundFetchSettingsCatalog
{
    internal const string Warning = "WARNING: be careful when force push with lease having the periodic background fetch enabled but chose not to auto-refresh after each fetch.\r\n\r\nYou could lose new commits pushed by others to the remote branch.\r\n\r\nBe sure to refresh the revision grid before doing a force push with lease.";
    internal static PluginScopedSettingsDefinition Definition { get; } = new(
        BackgroundFetchService.Descriptor.Id, "Periodic background fetch", "BackgroundFetchPlugin", "BackgroundFetch",
        [new("_gitCommand", "Arguments of git command to run", "Arguments of git command to run", "arguments", PluginScopedSettingKind.Text, "fetch --all"),
         new("_fetchInterval", "Fetch every (seconds) - set to 0 to disable", "Fetch every (seconds) - set to 0 to disable", "interval", PluginScopedSettingKind.Integer, "0"),
         new("_autoRefresh", "Refresh view after fetch", "Refresh view after fetch", "autoRefresh", PluginScopedSettingKind.Boolean, "false"),
         new("_fetchAllSubmodules", "Fetch all submodules", "Fetch all submodules", "submodules", PluginScopedSettingKind.Boolean, "false"),
         new("_fetchImmediatelyOnRepoOpening", "Fetch immediately on repository opening", "Fetch immediately on repository opening", "immediate", PluginScopedSettingKind.Boolean, "false")],
        new("_warningForceWithLease", Warning, 500));
    internal static PluginScopedSetting Find(string control) => Definition.Find(control);
}

internal sealed class BackgroundFetchSettingsSession : PluginScopedSettingsSession
{
    internal BackgroundFetchSettingsSession(GitRepository? repository, IReadOnlyDictionary<string, string?>? legacyValues = null,
        DistributedSettingsWorkspace? workspace = null) : base(BackgroundFetchSettingsCatalog.Definition, repository, legacyValues, workspace) { }
    internal BackgroundFetchOptions EffectiveOptions() => BackgroundFetchOptions.FromValues(EffectiveValues());
    protected override string? ValidateValue(PluginScopedSetting setting, string value)
    {
        if (setting.RuntimeKey != "arguments") { return null; }
        try { _ = new BackgroundFetchOptions(Arguments: value).GetCommandArguments(); return null; }
        catch (ArgumentException) { return "The background Git command arguments are invalid."; }
    }
}
