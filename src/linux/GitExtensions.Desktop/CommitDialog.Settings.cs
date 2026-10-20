namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private bool _commitSettingsSubscribed;
    private void InitializeCommitDialogSettings()
    {
        ApplyCommitActionVisibility();
        Opened += (_, _) =>
        { if (!_commitSettingsSubscribed) { CommitDialogSettingsPublication.Saved += CommitSettingsSaved; _commitSettingsSubscribed = true; } };
        Closed += (_, _) =>
        { if (_commitSettingsSubscribed) { CommitDialogSettingsPublication.Saved -= CommitSettingsSaved; _commitSettingsSubscribed = false; } };
    }
    private void CommitSettingsSaved(CommitDialogSettingsSaveReceipt receipt)
    {
        if (_lifetime.IsCancellationRequested || _closeWhenIdle || _spellingClosing || !IsVisible
            || !string.Equals(Path.GetFullPath(receipt.Path), Path.GetFullPath(_preferencesStore.FilePath), StringComparison.Ordinal)) { return; }
        _preferences = CommitDialogSettingsValues.ApplyCommit(_preferences, receipt.Values, receipt.Fields);
        ApplyCommitActionVisibility();
    }
    private void ApplyCommitActionVisibility()
    {
        _push.IsVisible = _preferences.ShowCommitAndPush;
        foreach (Avalonia.Controls.Button button in _interactive.OfType<Avalonia.Controls.Button>())
        {
            if (button.Name == "ResetUnstaged") { button.IsVisible = _preferences.ShowResetWorkTreeChanges; }
            else if (button.Name == "ResetAll") { button.IsVisible = _preferences.ShowResetAllChanges; }
        }
    }
}
