using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private GitCompareRevision? _selectedArtificialHistoryRevision;

    private void ClearArtificialHistorySelection()
    {
        _selectedArtificialHistoryRevision = null;
        UpdateArtificialHistorySelectionAppearance();
    }

    private void UpdateArtificialHistorySelectionAppearance()
    {
        foreach ((Button? button, GitCompareRevisionKind kind) in new[]
            { (_workingRevision, GitCompareRevisionKind.WorkingTree), (_indexRevision, GitCompareRevisionKind.Index) })
        {
            if (button is null) { continue; }
            bool selected = UsesArtificialDiffHistory && _selectedArtificialHistoryRevision?.Kind == kind;
            if (selected)
            {
                button.Background = RevisionHistoryRow.BackgroundFor(null, true, button.IsKeyboardFocusWithin, null, 0, false, ActualThemeVariant == ThemeVariant.Dark);
                if (button.Content is TextBlock text)
                { text.Foreground = ActualThemeVariant == ThemeVariant.Dark ? Brush.Parse("#dedede") : button.IsKeyboardFocusWithin ? Brushes.White : Brushes.Black; }
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                if (button.Content is TextBlock text) { text.ClearValue(TextBlock.ForegroundProperty); }
            }
        }
    }

    private Task ReadArtificialHistoryRevisionAsync(GitCompareRevision source)
        => ReadBrowserAsync(_revisionRead, "Artificial revision diff", (repository, token) => LoadArtificialHistoryRevisionAsync(repository, source, token));

    private async Task SelectArtificialHistoryRevisionAsync(bool staged)
    {
        if (!UsesArtificialDiffHistory || _busy || _repository is not { IsBare: false } || _lifetime.IsCancellationRequested) { return; }
        _parentChildNavigation?.RevisionsSelectionChanged();
        GitCompareRevision source = new("", staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree);
        CancelRevisionReads();
        _browseInitialComparison = null;
        _refreshing = true;
        try
        {
            _selectedArtificialHistoryRevision = null;
            _commits.SelectedItems?.Clear(); _commits.SelectedItem = null;
            SelectDetailsTab("DiffTab");
            _selectedArtificialHistoryRevision = source;
        }
        finally { _refreshing = false; }
        ClearCommitInformation(); _gpgInfo.Text = "";
        RefreshRebaseMenu(); RefreshRevisionContextMenu(); UpdateHistoryPresentation();
        await ReadArtificialHistoryRevisionAsync(source);
    }

    private async Task LoadArtificialHistoryRevisionAsync(GitRepository repository, GitCompareRevision source, CancellationToken token)
    {
        if (source.Kind is not (GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree))
        { throw new ArgumentException("An artificial history revision must be the index or working directory.", nameof(source)); }
        string identity = RevisionSelectionIdentity;
        object? previous = CaptureRevisionFileSelection();
        GitComparison comparison = await repository.GetArtificialRevisionComparisonAsync(source, token);
        await ShowArtificialHistoryComparisonAsync(repository, comparison, identity, previous, GitRevisionDiffGroupKind.Parent, token);
    }

    private async Task LoadArtificialHistoryComparisonAsync(GitRepository repository, GitCompareRevision first,
        GitCompareRevision selected, CancellationToken token)
    {
        string identity = RevisionSelectionIdentity;
        object? previous = CaptureRevisionFileSelection();
        GitComparison comparison = await repository.GetComparisonAsync(first, selected, token);
        await ShowArtificialHistoryComparisonAsync(repository, comparison, identity, previous, GitRevisionDiffGroupKind.Comparison, token);
    }

    private async Task ShowArtificialHistoryComparisonAsync(GitRepository repository, GitComparison comparison,
        string identity, object? previous, GitRevisionDiffGroupKind kind, CancellationToken token)
    {
        GitRevisionDiffGroup group = new(kind,
            comparison.Compare.Kind == GitCompareRevisionKind.Commit ? comparison.Compare.Revision : comparison.Compare.Kind.ToString(),
            comparison.Base.Kind == GitCompareRevisionKind.Commit ? comparison.Base.Revision : comparison.Base.Kind.ToString(), comparison.Files);
        Dictionary<GitRevisionDiffGroup, GitComparison> comparisons = new(ReferenceEqualityComparer.Instance) { [group] = comparison };
        var metadata = await ReadFileMenuMetadataAsync(repository, [group], token, comparisons);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(repository, _repository) || identity != RevisionSelectionIdentity) { return; }
        _revisionComparisons = comparisons; _fileMenuMetadata = metadata;
        TreeViewItem? selected = SetRevisionFileGroups([group], previous);
        _loadedDiffSelection = identity;
        if (selected is null) { ClearRevisionPreview(); }
        else if (_busy) { await ShowRevisionDiffItemAsync(repository, selected, token); }
        else { await ReadRevisionDiffAsync(selected, token); }
    }

    private async Task ArtificialHistoryKeyDownAsync(Button button, bool staged, KeyEventArgs args)
    {
        if (!UsesArtificialDiffHistory || _busy || !button.IsKeyboardFocusWithin || args.Handled || args.KeyModifiers != KeyModifiers.None || args.Key is not (Key.Up or Key.Down)) { return; }
        args.Handled = true;
        if (args.Key == Key.Up)
        {
            if (staged)
            {
                Task selection = SelectArtificialHistoryRevisionAsync(false);
                _workingRevision?.Focus(); await selection;
            }
            return;
        }
        if (!staged)
        {
            Task selection = SelectArtificialHistoryRevisionAsync(true);
            _indexRevision?.Focus(); await selection; return;
        }
        if (_commits.Items.OfType<GitCommit>().FirstOrDefault() is not { } commit) { return; }
        _commits.SelectedItem = commit;
        _commits.UpdateLayout();
        if (_commits.ContainerFromIndex(_commits.SelectedIndex) is Control row) { row.Focus(); row.BringIntoView(); }
        else { _commits.Focus(); }
    }
}
