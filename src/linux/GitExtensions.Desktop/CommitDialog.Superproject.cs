using Avalonia.Controls;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private readonly CheckBox _stageInSuperproject = new()
    {
        Name = "StageInSuperproject", IsVisible = false,
        Height = 24, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Left
    };
    private GitSuperprojectSnapshot? _superproject;
    private bool _savingStagePreference;

    private void ConfigureSuperprojectStage()
    {
        _stageInSuperproject.Content = C("StageInSuperproject", "Stage in Superproject");
        _stageInSuperproject.Height = _stageInSuperproject.MinHeight = DesktopFontMetrics.ToolbarHeight;
        ToolTip.SetTip(_stageInSuperproject, _translations.Translate("FormCommit", "StageInSuperproject", "fileTooltip",
            "Stage current submodule in superproject after commit"));
        _stageInSuperproject.IsChecked = _preferences.StageInSuperprojectAfterCommit;
        _stageInSuperproject.IsCheckedChanged += (_, _) =>
        {
            if (!_stageInSuperproject.IsVisible || _savingStagePreference) { return; }
            CommitPreferences previous = _preferencesStore.Load();
            try
            {
                // This is an immediate application preference, not a Git setting or a commit transaction.
                _preferences = previous with { StageInSuperprojectAfterCommit = _stageInSuperproject.IsChecked == true };
                _preferencesStore.Save(_preferences);
            }
            catch (Exception error)
            {
                // The checkbox must not advertise an opt-out that could not be saved.
                _preferences = previous;
                _savingStagePreference = true;
                try { _stageInSuperproject.IsChecked = previous.StageInSuperprojectAfterCommit; }
                finally { _savingStagePreference = false; }
                ShowFailure(C("StageInSuperproject", "Stage in Superproject"), error);
            }
        };
        _interactive.Add(_stageInSuperproject);
    }

    private async Task RefreshSuperprojectAsync()
    {
        GitSuperprojectSnapshot? snapshot = await _repository.GetSuperprojectSnapshotAsync(OperationToken);
        OperationToken.ThrowIfCancellationRequested();
        _superproject = snapshot;
        _stageInSuperproject.IsVisible = snapshot is not null;
    }

    private async Task StageCommittedSubmoduleAsync(GitSuperprojectSnapshot? expected)
    {
        // A failed/cancelled push and a push-only workflow never stage the parent.
        if (!PushCompleted || expected is null || !_preferencesStore.LoadForSuperprojectStaging().StageInSuperprojectAfterCommit) { return; }
        OperationToken.ThrowIfCancellationRequested();
        GitSuperprojectStageResult staged = await _repository.StageInSuperprojectAsync(expected, OperationToken);
        if (!staged.Result.Success)
        {
            throw new GitCommandException(["update-index", "--add", "--", expected.SubmodulePath], staged.Result);
        }
        if (!staged.Verified) { throw new InvalidOperationException(staged.Error ?? "The submodule could not be verified in the superproject index."); }
        RepositoryChanged?.Invoke(this, EventArgs.Empty);
    }
}
