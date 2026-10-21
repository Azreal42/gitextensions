using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private static string HistoryRevisionKey(GitCompareRevision revision) => revision.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => ArtificialHistoryRevision.WorkingTreeKey,
        GitCompareRevisionKind.Index => ArtificialHistoryRevision.IndexKey,
        _ => revision.Revision
    };

    private void SetProgressiveSelectionTarget(BrowseHistoryLoad load)
    {
        bool changedHead = !load.ChangedRepository && load.Bookmark.Head is not null && load.Bookmark.Head != load.Status.Head;
        load.PendingSelection = load.Requested is not null
            ? new[] { load.Requested, load.First }.OfType<GitCompareRevision>().Select(HistoryRevisionKey).Distinct(StringComparer.Ordinal).ToArray()
            : changedHead && !load.PreserveSelection && load.Status.Head is not null ? [load.Status.Head]
            : !load.ChangedRepository && load.Bookmark.Selection.Length > 0
                ? load.Bookmark.Recency.Reverse().Where(load.Bookmark.Selection.Contains).Concat(load.Bookmark.Selection).Distinct(StringComparer.Ordinal).ToArray()
                : load.Status.Head is not null ? [load.Status.Head] : [];
        load.SelectionPending = load.PendingSelection.Length > 0;
    }

    private Task PublishProgressiveHistory(BrowseHistoryLoad load, IReadOnlyList<GitCommit> batch, bool final, bool rebuild = false)
    {
        if (!CanPublishHistory(load)) { return Task.CompletedTask; }
        bool first = !load.Published;
        HistoryBookmark bookmark = CaptureHistoryBookmark();
        bool focused = _commits.IsKeyboardFocusWithin;
        string? caret = (FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(row => row.FindAncestorOfType<ListBox>() == _commits)?.DataContext is { } focusedItem
                ? HistoryRevisionItems.Key(focusedItem) : null;
        string before = RevisionSelectionIdentity;
        bool refreshing = _refreshing; _refreshing = true;
        try
        {
            if (first)
            {
                PublishHistoryHost(load);
                load.Published = true;
                _historySnapshotAncestors = null; _highlightedHistoryBranch = null;
            }
            if (first || rebuild)
            {
                _historyGraph.BeginIncrementalData(load.Status.Head, load.Query.FirstParent,
                    _historyView.ShowArtificialCommits && !load.Repository.IsBare, _historyRows);
                _historyGraph.References = HistoryViewReferences(load.References);
                if (rebuild) { _historyGraph.AppendCommits(load.Received); }
            }
            if (batch.Count > 0)
            {
                GitCommit[] normalized = _historyRows.ResolveSnapshotItems(batch.DistinctBy(commit => commit.Hash, StringComparer.Ordinal).Cast<object>().ToArray())
                    .Cast<GitCommit>().Where(commit => load.ReceivedKeys.Add(commit.Hash)).ToArray();
                load.Received.AddRange(normalized);
                _historyGraph.AppendCommits(normalized);
            }
            if (final) { _historyGraph.CompleteCommits(load.Ancestors); }
            _history = _historyGraph.OrderedCommits.ToArray();
            if (first || rebuild) { _historyRows.PublishSnapshot(_historyGraph.OrderedItems); }
            else { _historyRows.PublishOrder(_historyGraph.OrderedItems); }
            if (!ReferenceEquals(_commits.ItemsSource, _historyRows)) { _commits.ItemsSource = _historyRows; }
            _browseFileHistory = load.FileContext is { } context ? new(_history, context.Paths, context.PathByCommit) : null;
            _historyWindow = _history.Count; _historyHasMore = false;
            if (_historyMore is not null) { _historyMore.IsVisible = false; }
            RestoreProgressiveSelection(load, bookmark, first, final);
            _relationshipSource = null;
            _historyStatus = load.Status;
            if (final) { _historySnapshotAncestors = load.Ancestors?.ToArray(); }
            if (focused && caret is not null && HistoryItem(caret) is { } surviving)
            {
                _commits.UpdateLayout();
                if (_commits.ContainerFromItem(surviving) is Control row) { row.Focus(); }
            }
        }
        finally { _refreshing = refreshing; }
        _commitInfo.UpdateGraph(load.Repository, _history);
        UpdateHistoryPresentation(); RefreshHistoryMenuStates();
        // EOF finalizes graph geometry; an unchanged selection retains its owned panel read/result.
        if (first || before != RevisionSelectionIdentity)
        { load.PendingPanels = true; }
        if (first)
        {
            load.Ready.TrySetResult();
            load.Inventory = LoadProgressiveInventoryAsync(load);
        }
        load.Pulse();
        RestoreRetainedHistoryFailure(load);
        return !_busy ? ResumeProgressivePanelsAsync(load) : Task.CompletedTask;
    }

    private void RestoreProgressiveSelection(BrowseHistoryLoad load, HistoryBookmark bookmark, bool first, bool final)
    {
        bool pending = load.SelectionPending && load.Intent == _historyIntentVersion;
        string[] keys = pending ? load.PendingSelection : bookmark.Recency.Reverse().Where(bookmark.Selection.Contains)
            .Concat(bookmark.Selection).Distinct(StringComparer.Ordinal).ToArray();
        object[] selected = keys.Select(HistoryItem).OfType<object>().ToArray();
        bool allTargets = pending && selected.Length == keys.Length;
        bool explicitMissing = load.Requested is not null && pending && !allTargets;
        if (selected.Length == 0 && (!explicitMissing || !final))
        {
            object? fallback = load.Status.Head is not null ? HistoryItem(load.Status.Head) : null;
            if (final) { fallback ??= load.Ancestors?.Select(HistoryItem).OfType<GitCommit>().FirstOrDefault(); }
            fallback ??= _historyRows.OfType<GitCommit>().FirstOrDefault() ?? _historyRows.FirstOrDefault();
            selected = fallback is null ? [] : [fallback];
        }
        string[] actual = (_commits.SelectedItems?.Cast<object>() ?? []).Select(HistoryRevisionItems.Key).ToArray();
        if (!actual.SequenceEqual(selected.Select(HistoryRevisionItems.Key)))
        { _commits.SelectedItems = new AvaloniaList<object?>(selected.Cast<object?>()); }
        _historySelectionOrder.Clear();
        _historySelectionOrder.AddRange(selected.Reverse().Select(HistoryRevisionItems.Key));
        _quickSearchLastCommit = _historySelectionOrder.LastOrDefault();
        if (_parentChildLatestCommit is null || HistoryItem(_parentChildLatestCommit) is null || first && pending)
        { _parentChildLatestCommit = _quickSearchLastCommit; }
        UpdateTypedHistorySelection();
        if (pending && load.First is not null && load.Requested is not null && allTargets)
        { _browseInitialComparison = new(load.First, load.Requested); }
        else if (load.Comparison is { } pair && load.Intent == _historyIntentVersion
            && HistoryItem(pair.First) is not null && HistoryItem(pair.Selected) is not null)
        { _browseInitialComparison = pair; }
        string? anchor = pending && load.First is not null && allTargets ? HistoryRevisionKey(load.First)
            : bookmark.Anchor ?? (first ? load.Bookmark.Anchor : null);
        if (anchor is not null && _historyRows.IndexOfKey(anchor) is int anchorIndex && anchorIndex >= 0)
        { _commits.Selection.AnchorIndex = anchorIndex; }
        else if (selected.Length > 0) { _commits.Selection.AnchorIndex = _commits.Items.IndexOf(selected[^1]); }
        else { _commits.Selection.AnchorIndex = -1; }
        _commits.UpdateLayout();
        bool newTarget = pending && allTargets;
        if (!newTarget && _commits.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
        {
            int top = bookmark.Top is null ? -1 : _historyRows.IndexOfKey(bookmark.Top);
            scroll.Offset = new(bookmark.Offset.X, Math.Max(0, bookmark.Offset.Y + (top >= 0 ? (top - bookmark.TopIndex) * BrowseRowHeight : 0)));
        }
        else if (newTarget && PrimaryHistoryItem is { } target) { _commits.ScrollIntoView(target); }
        if (allTargets || final) { load.SelectionPending = false; }
        if (final && explicitMissing)
        {
            string hash = load.Requested!.Revision;
            _diff.Text = T("The revision is not visible with the current history filters.") + " " + hash;
            _state.Text = _diff.Text;
        }
    }

    private void PublishHistoryHost(BrowseHistoryLoad load)
    {
        if (load.Opening)
        {
            _repository = load.Repository;
            if (load.ChangedRepository)
            {
                _comparisonBase = null; _navigationSelectionKey = null; _authoredEmail = null;
                _quickSearchArtificialSelection = null; _quickSearchLastCommit = null; _browseInitialComparison = null;
                _historyMetadata = new(StringComparer.Ordinal);
            }
            _navigation.SelectedItem = null; _navigationRevisionFilter = null;
            _repositoryPath.Text = load.Repository.Path;
            Title = IsRevisionLog ? T("Diff") : $"Git Extensions — {load.Repository.Path}"
                + (load.Query.Path is null ? "" : " (" + load.Query.Path + ")");
            if (load.Repository.IsBare) { SelectDetailsTab("DiffTab"); }
            SetDashboardVisible(false);
        }
        if (load.MissingNavigationReference)
        {
            if (_historyRevision.Text == _navigationRevisionFilter) { SetBranchFilterDraft(null); }
            _navigationRevisionFilter = null; _acceptedBranchFilter = null; _historyBranchScope = HistoryBranchScope.All;
        }
        if (_fileHistoryTrackedFilter != load.Query.Path)
        { _explicitFileHistoryDiffPath = null; _explicitFileHistoryTreePath = null; _fileHistoryTrackedFilter = load.Query.Path; }
        _historyQuery = load.Query;
        UpdateBranchFilterReferences(load.References, load.Opening);
        _references.ItemsSource = load.References;
        _references.SelectedItem = load.References.FirstOrDefault(reference => reference.Name == load.ReferenceName);
        _referenceSelectionOverride = null;
        RefreshGeneralStatusPresentation(load.Status, load.ChangedRepository ? [] : _generalStashes);
        UpdateBrowserToolbar(load.Status); UpdateVirtualRevisionRows(load.Status);
        SetWorkingFiles(load.Status.Changes, load.WorkingPath, preserveSelection: !load.ChangedRepository);
        UpdateBrowserNavigation(load.References, load.ChangedRepository ? [] : _navigationStashes,
            load.ChangedRepository ? [] : _navigationSubmodules, load.ChangedRepository ? [] : _navigationWorktrees);
        _browseSelectionApplied = true;
        _parentChildNavigation?.Clear(); _parentChildFileFocus = null;
        HideQuickSearch(); BeginRepositoryBranchRefresh(load.Repository);
    }

    private async Task ResumeProgressivePanelsAsync(BrowseHistoryLoad load)
    {
        if (!CanPublishHistory(load) || !load.PendingPanels || _busy) { return; }
        load.PendingPanels = false;
        try
        {
            _historySelectionWork = PublishHistorySelectionChange();
            await _historySelectionWork;
            if (!CanPublishHistory(load)) { return; }
            if ((_tabs.SelectedItem as TabItem)?.Name == "ChangesTab") { await LoadSelectedDiffAsync(); }
            if (!CanPublishHistory(load)) { return; }
            if (PrimaryHistoryItem is null)
            {
                ClearRevisionDiff(); ClearRevisionPreview(); ClearCommitInformation(); _gpgInfo.Text = "";
                CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
                _fileTreeSource = null; _fileTreeRevision = null; _fileTreeRepository = null;
                _allFileTreePaths = []; _fileTreeEntries = new Dictionary<string, GitComparisonTreeEntry>();
                UpdateFileTreeEntries([]); ShowSelectedTreeFolder();
                _diff.Text = !load.EndOfHistory ? T("Loading data")
                    : load.Requested is { Kind: GitCompareRevisionKind.Commit } requested
                        ? T("The revision is not visible with the current history filters.") + " " + requested.Revision
                        : load.Status.IsUnborn ? "This repository has no commits yet." : T("No commits match the current history filters.");
            }
        }
        finally { RestoreRetainedHistoryFailure(load); }
    }

    private async Task RepublishLiveArtificialVisibilityAsync(BrowseHistoryLoad load)
    {
        if (!CanPublishHistory(load) || !load.Published) { return; }
        using WindowWork work = TrackWindowWork();
        OperationFocus? focus = CaptureOperationFocus();
        // A view choice changes membership, so it supersedes any deferred initial selection.
        RecordHistorySelectionIntent();
        await PublishProgressiveHistory(load, [], final: load.EndOfHistory, rebuild: true);
        if (focus?.HistoryKey is string key && HistoryItem(key) is null)
        { focus = focus with { Input = _commits, HistoryKey = PrimaryHistoryItem is { } current ? HistoryRevisionItems.Key(current) : null }; }
        if (PrimaryHistoryItem is null && (focus is null || focus.Input is MenuItem))
        { focus = new OperationFocus(_repository, _commits, HistoryKey: null); }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreOperationFocus(focus), Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
