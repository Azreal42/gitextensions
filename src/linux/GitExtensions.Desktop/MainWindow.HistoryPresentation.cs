using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private string? _configuredUserEmail, _authoredEmail;
    private Control? _virtualRevisions;
    private Button? _workingRevision, _indexRevision;
    private TextBlock? _workingRevisionState, _indexRevisionState;

    private void UpdateHistoryPresentation()
    {
        if (_refreshing || _lifetime.IsCancellationRequested) { return; }
        GitCommit[] selected = _commits.SelectedItems?.OfType<GitCommit>().ToArray() ?? [];
        // AuthorRevisionHighlighting: one selected author, keep it on multiselection,
        // and fall back to the configured user only when selection is empty.
        if (selected.Length == 1) { _authoredEmail = selected[0].AuthorEmail; }
        else if (selected.Length == 0) { _authoredEmail = _configuredUserEmail; }
        string? revision = selected.FirstOrDefault()?.Hash;
        if (!ReferenceEquals(_relationshipSource, _history) || _relationshipRevision != revision)
        {
            _relationshipSource = _history; _relationshipRevision = revision;
            _relatedHistory = RelatedHistory(revision, true);
        }
        _historyGraph.RelatedHashes = _highlightedHistoryBranch ?? (_historyView.DrawNonRelativesGray ? _relatedHistory : null);
        _historyGraph.IsVisible = _historyView.ShowGraph;
        _historyGraph.SelectedHashes = selected.Select(commit => commit.Hash).ToArray();
        _historyGraph.SelectionFocused = _commits.IsKeyboardFocusWithin;
        _historyGraph.AuthoredEmail = _historyView.HighlightAuthoredRevisions ? _authoredEmail : null;
        _historyGraph.DrawAlternateBackground = _historyView.DrawAlternateBackground;
        foreach (Control row in _commits.GetVisualDescendants().OfType<Control>().Where(control => control.Name == "RevisionHistoryRow")) { UpdateHistoryRow(row); }
        UpdateArtificialHistorySelectionAppearance();
    }

    private void UpdateHistoryRow(Control row)
    {
        if (row.Tag is not GitCommit commit) { return; }
        int index = 0;
        foreach (GitCommit entry in _historyGraph.OrderedCommits)
        { if (entry.Hash == commit.Hash) { break; } index++; }
        bool selected = _commits.SelectedItems?.OfType<GitCommit>().Any(entry => entry.Hash == commit.Hash) == true;
        RevisionHistoryRow.UpdateState(row, selected, _commits.IsKeyboardFocusWithin,
            _historyView.HighlightAuthoredRevisions ? _authoredEmail : null, index,
            _historyView.DrawAlternateBackground);
        ApplyHistoryRowOptions(row, commit);
    }

    private Control BuildVirtualRevisionRows()
    {
        ArtificialRevisionRows rows = ArtificialRevisionRows.Create(BrowseRowHeight, T);
        // Keep Browse's existing callbacks, status and navigation policy; share only the visual construction.
        for (int i = 0; i < 2; i++)
        {
            Button action = i == 0 ? rows.Working : rows.Index;
            TextBlock state = i == 0 ? rows.WorkingState : rows.IndexState;
            bool staged = i == 1;
            action.Click += async (_, _) => await ActivateArtificialRevisionAsync(staged);
            action.GotFocus += (_, _) => UpdateArtificialHistorySelectionAppearance();
            action.LostFocus += (_, _) => UpdateArtificialHistorySelectionAppearance();
            action.ActualThemeVariantChanged += (_, _) => UpdateArtificialHistorySelectionAppearance();
            action.KeyDown += async (_, args) => await ArtificialHistoryKeyDownAsync(action, staged, args);
            _actionButtons.Add(action);
            if (i == 0) { _workingRevision = action; _workingRevisionState = state; }
            else { _indexRevision = action; _indexRevisionState = state; }
        }
        return rows.View;
    }

    private async Task ActivateArtificialRevisionAsync(bool staged)
    {
        if (_busy || _repository is null || _repository.IsBare || _commitInfoClosing) { return; }
        _parentChildNavigation?.RevisionsSelectionChanged();
        if (!_applyingQuickSearchSelection) { CancelQuickSearchClipboard(); }
        _quickSearchArtificialSelection = new("", staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree);
        RefreshHistoryMenuStates();
        if (UsesArtificialDiffHistory) { await SelectArtificialHistoryRevisionAsync(staged); return; }
        if (_tabs.SelectedItem is TabItem tab && tab.Name != "ChangesTab") { _quickSearchReturnTab = tab; }
        SelectDetailsTab("ChangesTab");
        GitChange? change = _changesFilesView?.Entries.Select(entry => entry.Tag).OfType<GitChange>()
            .FirstOrDefault(entry => staged ? entry.IsStaged : entry.IsModified || entry.IsUntracked);
        _refreshing = true;
        try
        {
            _stagedDiff.IsChecked = staged;
            _changesFilesView?.SelectTags(change is null ? [] : [change]);
        }
        finally { _refreshing = false; }
        if (change is not null) { await LoadSelectedDiffAsync(); }
        else { ClearWorkingDiff(); }
    }

    private void UpdateVirtualRevisionRows(GitStatus status)
    {
        if (_virtualRevisions is not null) { _virtualRevisions.IsVisible = _historyView.ShowArtificialCommits && _repository?.IsBare != true; }
        int working = status.Changes.Count(change => change.IsModified || change.IsUntracked);
        int indexed = status.Changes.Count(change => change.IsStaged);
        foreach ((Button? button, TextBlock? state, int count) in new[] { (_workingRevision, _workingRevisionState, working), (_indexRevision, _indexRevisionState, indexed) })
        {
            if (button is null || state is null) { continue; }
            state.IsVisible = _preferences.ShowGitStatusForArtificialCommits;
            ToolTip.SetTip(button, _preferences.ShowGitStatusForArtificialCommits ? count + " " + T("changes") : null);
            state.Text = count == 0 ? "✓" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            state.Foreground = count == 0 ? Brush.Parse("#24c221") : Brushes.DarkOrange;
        }
    }
}
