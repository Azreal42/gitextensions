namespace GitExtensions.Linux.Desktop;

internal sealed record AdvancedSettingsValues
{
    public bool AlwaysShowCheckoutDialog { get; init; }
    public bool UseDefaultCheckoutChanges { get; init; }
    public bool CommitAndPushForcedWhenAmend { get; init; }
    public bool AutoNormaliseBranchName { get; init; } = true;
    public string AutoNormaliseSymbol { get; init; } = "_";
    public bool AlwaysShowAdvancedOptions { get; init; }

    internal object Get(string name) => name switch
    {
        nameof(AlwaysShowCheckoutDialog) => AlwaysShowCheckoutDialog,
        nameof(UseDefaultCheckoutChanges) => UseDefaultCheckoutChanges,
        nameof(CommitAndPushForcedWhenAmend) => CommitAndPushForcedWhenAmend,
        nameof(AutoNormaliseBranchName) => AutoNormaliseBranchName,
        nameof(AutoNormaliseSymbol) => AutoNormaliseSymbol,
        nameof(AlwaysShowAdvancedOptions) => AlwaysShowAdvancedOptions,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal AdvancedSettingsValues With(string name, object value) => name switch
    {
        nameof(AlwaysShowCheckoutDialog) => this with { AlwaysShowCheckoutDialog = (bool)value },
        nameof(UseDefaultCheckoutChanges) => this with { UseDefaultCheckoutChanges = (bool)value },
        nameof(CommitAndPushForcedWhenAmend) => this with { CommitAndPushForcedWhenAmend = (bool)value },
        nameof(AutoNormaliseBranchName) => this with { AutoNormaliseBranchName = (bool)value },
        nameof(AutoNormaliseSymbol) => this with { AutoNormaliseSymbol = (string)value },
        nameof(AlwaysShowAdvancedOptions) => this with { AlwaysShowAdvancedOptions = (bool)value },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static DesktopPreferences ApplyDesktop(DesktopPreferences current, AdvancedSettingsValues values,
        IEnumerable<AdvancedSetting> accepted)
    {
        foreach (AdvancedSetting setting in accepted.Where(setting => setting.Profile == AdvancedSettingsCatalog.DesktopProfile))
        {
            current = setting.Name switch
            {
                nameof(AutoNormaliseBranchName) => current with { AutoNormaliseBranchName = values.AutoNormaliseBranchName },
                nameof(AutoNormaliseSymbol) => current with { AutoNormaliseSymbol = values.AutoNormaliseSymbol },
                nameof(AlwaysShowAdvancedOptions) => current with { AlwaysShowAdvancedOptions = values.AlwaysShowAdvancedOptions },
                _ => current
            };
        }
        return current;
    }
}

internal sealed record AdvancedSetting(string Name, string Profile, string Control, string OriginalControl, string Caption,
    bool IsSymbol = false);

internal static class AdvancedSettingsCatalog
{
    internal const string DesktopProfile = "preferences.json";
    // Per-profile acknowledgement order. Existing authorities; no advanced.json shadow profile.
    internal static readonly IReadOnlyList<AdvancedSetting> Fields = Array.AsReadOnly<AdvancedSetting>(
    [
        new(nameof(AdvancedSettingsValues.CommitAndPushForcedWhenAmend), "commit-preferences.json", "chkCommitAndPushForcedWhenAmend", "chkCommitAndPushForcedWhenAmend", "Push forced with lease when Commit && Push action is performed with Amend option checked"),
        new(nameof(AdvancedSettingsValues.AlwaysShowCheckoutDialog), "reference-preferences.json", "chkAlwaysShowCheckoutDlg", "chkAlwaysShowCheckoutDlg", "Always show checkout dialog"),
        new(nameof(AdvancedSettingsValues.UseDefaultCheckoutChanges), "reference-preferences.json", "chkUseLocalChangesAction", "chkUseLocalChangesAction", "Use last chosen \"local changes\" action as default action.\r\nThis action will be performed without warning while checking out branch."),
        new(nameof(AdvancedSettingsValues.AutoNormaliseBranchName), DesktopProfile, "SettingsNormaliseBranches", "chkAutoNormaliseBranchName", "Auto normalise branch name"),
        new(nameof(AdvancedSettingsValues.AutoNormaliseSymbol), DesktopProfile, "SettingsNormaliseSymbol", "cboAutoNormaliseSymbol", "Symbol to use:", true),
        new(nameof(AdvancedSettingsValues.AlwaysShowAdvancedOptions), DesktopProfile, "SettingsAdvancedOptions", "chkAlwaysShowAdvOpt", "Always show advanced options")
    ]);
}
