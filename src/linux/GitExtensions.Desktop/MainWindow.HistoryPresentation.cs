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
        _historyGraph.RelatedHashes = _highlightedHistoryBranch;
        _historyGraph.DrawNonRelativesGray = _historyView.DrawNonRelativesGray;
        _historyGraph.IsVisible = _historyView.ShowGraph;
        _historyGraph.SelectedHashes = (_commits.SelectedItems?.Cast<object>() ?? []).Select(HistoryRevisionItems.Key).ToArray();
        _historyGraph.SelectionFocused = _commits.IsKeyboardFocusWithin;
        _historyGraph.AuthoredEmail = _historyView.HighlightAuthoredRevisions ? _authoredEmail : null;
        _historyGraph.DrawAlternateBackground = _historyView.DrawAlternateBackground;
        foreach (Control row in _commits.GetVisualDescendants().OfType<Control>().Where(control => control.Name is "RevisionHistoryRow" or "ArtificialRevisionHistoryRow")) { UpdateHistoryRow(row); }
        if (_tabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Name == "GpgTab") is { } gpg)
        { gpg.IsVisible = PrimaryHistoryItem is GitCommit; }
    }

    private void UpdateHistoryRow(Control row)
    {
        if (row.Tag is ArtificialHistoryRevision artificial)
        {
            if (row is Grid grid) { RevisionHistoryColumns.From(_historyView).Apply(grid.ColumnDefinitions); }
            bool artificialSelected = _commits.SelectedItems?.Contains(artificial) == true;
            row.SetValue(Avalonia.Controls.Panel.BackgroundProperty, RevisionHistoryRow.BackgroundFor(null, artificialSelected, _commits.IsKeyboardFocusWithin,
                null, _historyRows.IndexOfKey(artificial.GraphId), _historyView.DrawAlternateBackground, ActualThemeVariant == ThemeVariant.Dark));
            ArtificialRevisionRows.ApplyStatus(row, artificial.Kind, _historyStatus, _preferences.ShowGitStatusForArtificialCommits, T);
            return;
        }
        if (row.Tag is not GitCommit commit) { return; }
        int index = _historyRows.IndexOfHash(commit.Hash);
        bool selected = _commits.SelectedItems?.OfType<GitCommit>().Any(entry => entry.Hash == commit.Hash) == true;
        RevisionHistoryRow.UpdateState(row, selected, _commits.IsKeyboardFocusWithin,
            _historyView.HighlightAuthoredRevisions ? _authoredEmail : null, index,
            _historyView.DrawAlternateBackground);
        ApplyHistoryRowOptions(row, commit);
    }

    private Task ActivateArtificialRevisionAsync(bool staged)
    {
        if (_busy || _repository?.IsBare != false || _commitInfoClosing) { return Task.CompletedTask; }
        object? item = HistoryItem(new GitCompareRevision("", staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree));
        return item is null ? Task.CompletedTask : SelectHistoryItemAsync(item);
    }

    private void UpdateVirtualRevisionRows(GitStatus status)
    {
        _historyStatus = status;
        foreach (Control row in _commits.GetVisualDescendants().OfType<Control>().Where(control => control.Name == "ArtificialRevisionHistoryRow"))
        { UpdateHistoryRow(row); }
    }
}
