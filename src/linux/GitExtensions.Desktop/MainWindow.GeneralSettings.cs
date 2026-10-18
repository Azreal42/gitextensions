using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Globalization;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private GitStatus? _generalStatus;
    private IReadOnlyList<GitStash> _generalStashes = [];
    private GitBranchTrackingStatus? _generalTracking;
    private IReadOnlyDictionary<string, GitSubmoduleBrowseStatus> _generalSubmoduleStatus = new Dictionary<string, GitSubmoduleBrowseStatus>();
    private Button? _pushToolbarButton;
    private MenuItem? _pushOverflow, _stashOverflow;
    private Grid? _stashControl;

    private async Task ReadGeneralStatusDetailsAsync(GitRepository repository, GitStatus status,
        IReadOnlyList<GitSubmodule> submodules, CancellationToken token)
    {
        _generalTracking = null; _generalSubmoduleStatus = new Dictionary<string, GitSubmoduleBrowseStatus>();
        if (_preferences.ShowAheadBehindData && !status.IsDetached && !status.IsUnborn && status.Branch is not null)
        {
            try { _generalTracking = await repository.GetBranchTrackingStatusAsync(status.Branch, token); }
            catch (GitCommandException error) { AppendOutput(error.Message); }
        }
        if (_preferences.ShowSubmoduleStatus && (_preferences.ShowGitStatusInBrowseToolbar || _preferences.ShowGitStatusForArtificialCommits)
            && !repository.IsBare && submodules.Count > 0)
        {
            try { _generalSubmoduleStatus = (await repository.GetSubmoduleBrowseStatusesAsync(token)).ToDictionary(value => value.Path, StringComparer.Ordinal); }
            catch (Exception error) when (error is GitCommandException or IOException or InvalidOperationException)
            { AppendOutput(error.Message); }
        }
    }

    private void RefreshGeneralStatusPresentation(GitStatus status, IReadOnlyList<GitStash> stashes)
    {
        _generalStatus = status; _generalStashes = stashes;
        List<string> parts = [(_repository?.IsBare == true ? T("Bare repository") + "  ·  " : "") + (status.Branch ?? T("detached"))];
        if (_preferences.ShowGitStatusInBrowseToolbar || _preferences.ShowGitStatusForArtificialCommits)
        { parts.Add(status.Changes.Count + " " + T("changes")); }
        if (_preferences.ShowAheadBehindData) { parts.Add("↑" + status.Ahead + " ↓" + status.Behind); }
        _repositorySummary = string.Join("  ·  ", parts);
        UpdateGeneralPushPresentation(); UpdateGeneralStashPresentation();
    }

    private async Task RefreshGeneralPresentationFromSettingsAsync(CancellationToken token, bool rebuildNavigation)
    {
        if (_repository is null || _generalStatus is null) { return; }
        await ReadGeneralStatusDetailsAsync(_repository, _generalStatus, _navigationSubmodules, token);
        RefreshGeneralStatusPresentation(_generalStatus, _generalStashes);
        _state.Text = _repositorySummary;
        UpdateBrowserToolbar(_generalStatus); UpdateVirtualRevisionRows(_generalStatus);
        if (rebuildNavigation) { RebuildNavigation(); }
    }

    private void UpdateGeneralPushPresentation()
    {
        if (_pushToolbarButton is null) { return; }
        GitBranchTrackingStatus? tracking = _preferences.ShowAheadBehindData ? _generalTracking : null;
        string text = tracking?.Display ?? "";
        string icon = tracking?.BehindCount.Length > 0 ? "Unstage.png" : "Push.png";
        _pushToolbarButton.Content = text.Length == 0 ? OriginalIconImage(icon) : IconLabel(text, icon);
        _pushToolbarButton.Width = text.Length == 0 ? 24 : double.NaN;
        _pushToolbarButton.MinWidth = 24;
        string tip = T("Push");
        if (tracking is not null)
        {
            List<string> lines = [tracking.RemoteRef];
            if (tracking.IsGone) { lines.Add(T("The remote branch is gone.")); }
            else
            {
                if (tracking.AheadCount.Length > 0) { lines.Add(string.Format(CultureInfo.CurrentCulture, T("{0} new commit(s) will be pushed"), tracking.AheadCount)); }
                if (tracking.BehindCount.Length > 0) { lines.Add(string.Format(CultureInfo.CurrentCulture, T("{0} commit(s) should be integrated (or will be lost if force pushed)"), tracking.BehindCount)); }
            }
            tip = string.Join(Environment.NewLine, lines);
        }
        DesktopHotkeys.BindToolTip(_pushToolbarButton, "Browse", "Push", tip);
        if (_pushOverflow is not null)
        { _pushOverflow.Header = T("Push") + (text.Length == 0 ? "" : " " + text); _pushOverflow.Icon = OriginalIconImage(icon); ToolTip.SetTip(_pushOverflow, tip); }
    }

    private void UpdateGeneralStashPresentation()
    {
        if (_stashButton is null || _stashControl is null) { return; }
        bool counted = _preferences.ShowStashCount && _repository is { IsBare: false };
        string count = "(" + _generalStashes.Count.ToString(CultureInfo.CurrentCulture) + ")";
        _stashButton.Content = counted ? IconLabel(count, "stash.png") : OriginalIconImage("stash.png");
        _stashButton.Width = counted ? double.NaN : 24; _stashButton.MinWidth = 24;
        _stashControl.Width = counted ? double.NaN : 34;
        _stashControl.ColumnDefinitions = new(counted ? "Auto,10" : "24,10");
        if (_stashOverflow is not null) { _stashOverflow.Header = T("Stash") + (counted ? " " + count : ""); }
        _browserToolbar?.Place();
    }

    private GitSubmoduleBrowseStatus? GeneralSubmoduleStatus(GitSubmodule module)
        => _preferences.ShowSubmoduleStatus && (_preferences.ShowGitStatusInBrowseToolbar || _preferences.ShowGitStatusForArtificialCommits)
            && _generalSubmoduleStatus.TryGetValue(module.Path, out GitSubmoduleBrowseStatus? status) ? status : null;

    private static string GeneralSubmoduleIcon(GitSubmoduleBrowseStatus? status)
    {
        if (status is null || status.State is GitSubmoduleBrowseState.Unknown or GitSubmoduleBrowseState.NotInitialized) { return "FolderSubmodule.png"; }
        string? icon = status.State switch
        {
            GitSubmoduleBrowseState.FastForward => "SubmoduleRevisionUp", GitSubmoduleBrowseState.Rewind => "SubmoduleRevisionDown",
            GitSubmoduleBrowseState.NewerTime => "SubmoduleRevisionSemiUp", GitSubmoduleBrowseState.OlderTime => "SubmoduleRevisionSemiDown", _ => null
        };
        return icon is not null ? icon + (status.IsDirty ? "Dirty" : "") + ".png" : status.IsDirty ? "SubmoduleDirty.png" : "FileStatusModified.png";
    }
}
