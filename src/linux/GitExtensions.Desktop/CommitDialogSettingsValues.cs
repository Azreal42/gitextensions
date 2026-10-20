namespace GitExtensions.Linux.Desktop;

internal sealed record CommitDialogSettingsValues
{
    public bool ProvideAutocompletion { get; init; } = true;
    public bool EnsureCommitMessageSecondLineEmpty { get; init; } = true;
    public int NumberOfPreviousMessages { get; init; } = 6;
    public bool RememberAmendCommitState { get; init; } = true;
    public bool ShowCommitAndPush { get; init; } = true;
    public bool ShowResetWorkTreeChanges { get; init; } = true;
    public bool ShowResetAllChanges { get; init; } = true;

    internal object Get(string name) => name switch
    {
        nameof(ProvideAutocompletion) => ProvideAutocompletion,
        nameof(EnsureCommitMessageSecondLineEmpty) => EnsureCommitMessageSecondLineEmpty,
        nameof(NumberOfPreviousMessages) => NumberOfPreviousMessages,
        nameof(RememberAmendCommitState) => RememberAmendCommitState,
        nameof(ShowCommitAndPush) => ShowCommitAndPush,
        nameof(ShowResetWorkTreeChanges) => ShowResetWorkTreeChanges,
        nameof(ShowResetAllChanges) => ShowResetAllChanges,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal CommitDialogSettingsValues With(string name, object value) => name switch
    {
        nameof(ProvideAutocompletion) => this with { ProvideAutocompletion = (bool)value },
        nameof(EnsureCommitMessageSecondLineEmpty) => this with { EnsureCommitMessageSecondLineEmpty = (bool)value },
        nameof(NumberOfPreviousMessages) => this with { NumberOfPreviousMessages = (int)value },
        nameof(RememberAmendCommitState) => this with { RememberAmendCommitState = (bool)value },
        nameof(ShowCommitAndPush) => this with { ShowCommitAndPush = (bool)value },
        nameof(ShowResetWorkTreeChanges) => this with { ShowResetWorkTreeChanges = (bool)value },
        nameof(ShowResetAllChanges) => this with { ShowResetAllChanges = (bool)value },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static CommitPreferences ApplyCommit(CommitPreferences current, CommitDialogSettingsValues values,
        IEnumerable<CommitDialogSetting> settings)
    {
        foreach (CommitDialogSetting setting in settings.Where(choice => choice.Profile == CommitDialogSettingsCatalog.CommitProfile))
        {
            current = setting.Name switch
            {
                nameof(EnsureCommitMessageSecondLineEmpty) => current with { EnsureCommitMessageSecondLineEmpty = values.EnsureCommitMessageSecondLineEmpty },
                nameof(NumberOfPreviousMessages) => current with { NumberOfPreviousMessages = values.NumberOfPreviousMessages },
                nameof(RememberAmendCommitState) => current with { RememberAmendCommitState = values.RememberAmendCommitState },
                nameof(ShowCommitAndPush) => current with { ShowCommitAndPush = values.ShowCommitAndPush },
                nameof(ShowResetWorkTreeChanges) => current with { ShowResetWorkTreeChanges = values.ShowResetWorkTreeChanges },
                nameof(ShowResetAllChanges) => current with { ShowResetAllChanges = values.ShowResetAllChanges },
                _ => throw new ArgumentOutOfRangeException(nameof(settings))
            };
        }
        return current;
    }
}

internal sealed record CommitDialogSetting(string Name, string Profile, string Control, string Caption, bool IsNumber = false);

internal static class CommitDialogSettingsCatalog
{
    internal const string CommitProfile = "commit-preferences.json", DesktopProfile = "preferences.json";
    // Commit is acknowledged before Desktop; a later file failure cannot replay it.
    internal static IReadOnlyList<CommitDialogSetting> Fields { get; } = Array.AsReadOnly<CommitDialogSetting>(
    [
        new(nameof(CommitDialogSettingsValues.EnsureCommitMessageSecondLineEmpty), CommitProfile, "chkEnsureCommitMessageSecondLineEmpty", "Ensure the second line of commit message is empty"),
        new(nameof(CommitDialogSettingsValues.NumberOfPreviousMessages), CommitProfile, "_NO_TRANSLATE_CommitDialogNumberOfPreviousMessages", "Number of previous messages in commit dialog", true),
        new(nameof(CommitDialogSettingsValues.RememberAmendCommitState), CommitProfile, "cbRememberAmendCommitState", "Remember 'Amend commit' checkbox on commit form close"),
        new(nameof(CommitDialogSettingsValues.ShowCommitAndPush), CommitProfile, "chkShowCommitAndPush", "Commit && Push"),
        new(nameof(CommitDialogSettingsValues.ShowResetWorkTreeChanges), CommitProfile, "chkShowResetWorkTreeChanges", "Reset Unstaged Changes"),
        new(nameof(CommitDialogSettingsValues.ShowResetAllChanges), CommitProfile, "chkShowResetAllChanges", "Reset All Changes"),
        new(nameof(CommitDialogSettingsValues.ProvideAutocompletion), DesktopProfile, "chkAutocomplete", "Provide auto-completion in commit dialog")
    ]);
}
