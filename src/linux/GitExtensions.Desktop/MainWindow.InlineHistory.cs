using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly IncrementalRevisionRows _historyRows = new();
    private GitStatus? _historyStatus;
    private IReadOnlyList<string>? _historySnapshotAncestors;
    private Task _historySelectionWork = Task.CompletedTask;
    private sealed record HistoryBookmark(string[] Selection, string[] Recency, string? Anchor,
        string? Top, int TopIndex, Vector Offset, string? Head);

    private object? HistoryItem(string key) => _historyRows.FirstOrDefault(item => HistoryRevisionItems.Key(item) == key);
    private object? HistoryItem(GitCompareRevision revision) => _historyRows.FirstOrDefault(item => HistoryRevisionItems.Revision(item) == revision);
    private object[] SelectedHistoryItems()
    {
        Dictionary<string, object> selected = (_commits.SelectedItems?.Cast<object>() ?? [])
            .ToDictionary(HistoryRevisionItems.Key, StringComparer.Ordinal);
        return _historySelectionOrder.AsEnumerable().Reverse().Where(selected.ContainsKey).Select(key => selected[key])
            .Concat(selected.Values.Where(item => !_historySelectionOrder.Contains(HistoryRevisionItems.Key(item), StringComparer.Ordinal))).ToArray();
    }
    private GitCompareRevision[] SelectedHistoryRevisions() => SelectedHistoryItems().Select(HistoryRevisionItems.Revision).ToArray();
    private object? PrimaryHistoryItem => SelectedHistoryItems().FirstOrDefault();

    private HistoryBookmark CaptureHistoryBookmark()
    {
        ScrollViewer? scroll = _commits.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        Vector offset = scroll?.Offset ?? default; int top = (int)(offset.Y / BrowseRowHeight);
        int anchor = _commits.Selection.AnchorIndex;
        return new((_commits.SelectedItems?.Cast<object>() ?? []).Select(HistoryRevisionItems.Key).ToArray(),
            _historySelectionOrder.ToArray(), anchor >= 0 && anchor < _commits.ItemCount ? HistoryRevisionItems.Key(_commits.Items[anchor]!) : null,
            top >= 0 && top < _commits.ItemCount ? HistoryRevisionItems.Key(_commits.Items[top]!) : null, top, offset, _historyGraph.CurrentHead);
    }

    private async Task<IReadOnlyList<string>> ReadHistoryAncestorsAsync(GitRepository repository, string revision)
    {
        try { return await repository.ReadRevisionAncestryAsync(revision, Token); }
        catch (GitCommandException) { return []; } // Native Windows EOF fallback positions/selects the first visible row.
    }

    private void PublishHistorySnapshot(IReadOnlyList<GitCommit> history, GitStatus status, IReadOnlyList<GitReference> references,
        GitLogQuery query, IReadOnlyList<string>? ancestors, IReadOnlyList<string>? selectionAncestors, HistoryBookmark bookmark, bool changedRepository,
        GitCompareRevision? requested, GitCompareRevision? first, BrowseInitialComparison? retainedComparison)
    {
        if (changedRepository) { _historyRows.PublishSnapshot([]); }
        _history = _historyRows.ResolveSnapshotItems(history.DistinctBy(commit => commit.Hash, StringComparer.Ordinal).Cast<object>().ToArray()).Cast<GitCommit>().ToArray();
        _historyGraph.BeginIncrementalData(status.Head, query.FirstParent,
            _historyView.ShowArtificialCommits && !(_repository?.IsBare ?? true), _historyRows);
        _historyGraph.AppendCommits(_history); _historyGraph.CompleteCommits(ancestors);
        _historyGraph.References = HistoryViewReferences(references); _highlightedHistoryBranch = null;
        _historyRows.PublishSnapshot(_historyGraph.OrderedItems);
        if (!ReferenceEquals(_commits.ItemsSource, _historyRows)) { _commits.ItemsSource = _historyRows; }
        string Key(GitCompareRevision source) => source.Kind switch
        { GitCompareRevisionKind.WorkingTree => ArtificialHistoryRevision.WorkingTreeKey,
          GitCompareRevisionKind.Index => ArtificialHistoryRevision.IndexKey, _ => source.Revision };
        bool headChanged = !changedRepository && bookmark.Head is not null && bookmark.Head != status.Head;
        bool preserveOperationSelection = !changedRepository && _operationHistoryFocus is { PreserveHistorySelection: true } origin
            && ReferenceEquals(origin.Repository, _repository);
        bool followChangedHead = headChanged && !preserveOperationSelection;
        IEnumerable<string> wanted = requested is not null ? new[] { requested, first }.OfType<GitCompareRevision>().Select(Key)
            : followChangedHead && status.Head is not null ? [status.Head] : changedRepository ? [] : bookmark.Selection;
        object[] selected = wanted.Distinct(StringComparer.Ordinal).Select(HistoryItem).OfType<object>().ToArray();
        if (selected.Length == 0 && requested is null)
        {
            object? fallback = status.Head is not null ? HistoryItem(status.Head) : null;
            fallback ??= selectionAncestors?.Select(HistoryItem).OfType<GitCommit>().FirstOrDefault();
            fallback ??= _historyRows.OfType<GitCommit>().FirstOrDefault() ?? _historyRows.FirstOrDefault();
            selected = fallback is null ? [] : [fallback];
        }
        _commits.SelectedItems = new AvaloniaList<object?>(selected.Cast<object?>());
        HashSet<string> selectedKeys = selected.Select(HistoryRevisionItems.Key).ToHashSet(StringComparer.Ordinal);
        _historySelectionOrder.Clear();
        if (requested is null && !followChangedHead && !changedRepository)
        { _historySelectionOrder.AddRange(bookmark.Recency.Where(selectedKeys.Contains)); }
        foreach (object item in selected.Reverse())
        { string key = HistoryRevisionItems.Key(item); if (!_historySelectionOrder.Contains(key)) { _historySelectionOrder.Add(key); } }
        _quickSearchLastCommit = _historySelectionOrder.LastOrDefault();
        if (followChangedHead || changedRepository || _parentChildLatestCommit is null || HistoryItem(_parentChildLatestCommit) is null)
        { _parentChildLatestCommit = _quickSearchLastCommit; }
        UpdateTypedHistorySelection();
        if (requested is not null && first is not null && HistoryItem(requested) is not null && HistoryItem(first) is not null)
        { _browseInitialComparison = new(first, requested); }
        else if (retainedComparison is not null && requested is null && !followChangedHead
            && HistoryItem(retainedComparison.First) is not null && HistoryItem(retainedComparison.Selected) is not null)
        { _browseInitialComparison = retainedComparison; }
        if (requested is not null && first is not null && HistoryItem(first) is { } firstItem)
        { _commits.Selection.AnchorIndex = _commits.Items.IndexOf(firstItem); }
        else if (!changedRepository && !followChangedHead && bookmark.Anchor is not null && _historyRows.IndexOfKey(bookmark.Anchor) is int anchor && anchor >= 0)
        { _commits.Selection.AnchorIndex = anchor; }
        _commits.UpdateLayout();
        if (!changedRepository && !followChangedHead && _commits.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
        {
            int top = bookmark.Top is null ? -1 : _historyRows.IndexOfKey(bookmark.Top);
            scroll.Offset = new(bookmark.Offset.X, Math.Max(0, bookmark.Offset.Y + (top >= 0 ? (top - bookmark.TopIndex) * BrowseRowHeight : 0)));
        }
        else if (PrimaryHistoryItem is { } item) { _commits.ScrollIntoView(item); }
        _historyStatus = status;
        _historySnapshotAncestors = ancestors?.ToArray();
    }

    private void UpdateTypedHistorySelection()
    {
        GitCompareRevision[] selected = SelectedHistoryRevisions();
        GitCompareRevision? primary = selected.FirstOrDefault();
        _selectedArtificialHistoryRevision = primary?.Kind is GitCompareRevisionKind.WorkingTree or GitCompareRevisionKind.Index ? primary : null;
        _quickSearchArtificialSelection = _selectedArtificialHistoryRevision;
        _browseInitialComparison = selected.Length > 1 && selected.Any(source => source.Kind != GitCompareRevisionKind.Commit)
            ? new(selected[^1], selected[0]) : null;
    }

    private Task HandleHistorySelectionChanged(SelectionChangedEventArgs args)
    {
        if (_refreshing) { return Task.CompletedTask; }
        RecordHistorySelectionIntent();
        ObserveQuickSearchSelection(args);
        return PublishHistorySelectionChange();
    }

    private Task PublishHistorySelectionChange()
    {
        _parentChildNavigation?.RevisionsSelectionChanged();
        UpdateTypedHistorySelection(); CancelRevisionReads(); RememberRevisionNavigation();
        RefreshRebaseMenu(); RefreshRevisionContextMenu(); OnGitHubActionsSelectionChanged(); UpdateHistoryPresentation();
        if (PrimaryHistoryItem is GitCommit commit) { UpdateCommitInfo(commit); _gpgInfo.Text = null; }
        else { ClearCommitInformation(); _gpgInfo.Text = ""; }
        if (_selectingRevisionForContextMenu) { _contextRevisionDiffPending = true; return Task.CompletedTask; }
        if (PrimaryHistoryItem is GitCommit selected) { CancelFileGrepForCommit(selected.Hash); }
        return LoadVisibleRevisionPanelAsync();
    }

    private async Task SelectHistoryItemAsync(object item)
    {
        RecordHistorySelectionIntent();
        bool same = _commits.SelectedItems is { Count: 1 } && ReferenceEquals(_commits.SelectedItem, item);
        if (!same)
        {
            _applyingQuickSearchSelection = true;
            try
            {
                _commits.Selection.BeginBatchUpdate();
                try { _commits.Selection.Clear(); _commits.Selection.Select(_commits.Items.IndexOf(item)); }
                finally { _commits.Selection.EndBatchUpdate(); }
            }
            finally { _applyingQuickSearchSelection = false; }
        }
        string key = HistoryRevisionItems.Key(item); _quickSearchLastCommit = key; _parentChildLatestCommit = key;
        _commits.ScrollIntoView(item); _commits.UpdateLayout();
        _applyingQuickSearchSelection = true;
        try { if (_commits.ContainerFromItem(item) is Control row) { row.Focus(); } else { _commits.Focus(); } }
        finally { _applyingQuickSearchSelection = false; }
        if (!same) { await _historySelectionWork; }
    }
    private void PreviewHistoryRange(PointerPressedEventArgs args)
    {
        if (args.Handled || _busy || !args.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || !args.GetCurrentPoint(_commits).Properties.IsLeftButtonPressed) { return; }
        ListBoxItem? row = (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not { } target) { return; }
        int end = _commits.Items.IndexOf(target), anchor = _commits.Selection.AnchorIndex;
        if (end < 0 || anchor < 0 || anchor >= _commits.ItemCount || !_commits.Selection.IsSelected(anchor)) { return; }
        // WinForms keeps an already selected clicked row, removes the other
        // selections, then admits the inclusive visual range from top to bottom.
        // Avalonia reports only new members; that alone loses the native order.
        List<object> admitted = [];
        if (_commits.SelectedItems?.Contains(target) == true) { admitted.Add(target); }
        for (int index = Math.Min(anchor, end); index <= Math.Max(anchor, end); index++)
        { if (!admitted.Contains(_commits.Items[index]!)) { admitted.Add(_commits.Items[index]!); } }
        bool refreshing = _refreshing; _refreshing = true;
        try
        {
            _commits.SelectedItems = new AvaloniaList<object?>(admitted.AsEnumerable().Reverse().Cast<object?>());
            _commits.Selection.AnchorIndex = anchor;
        }
        finally { _refreshing = refreshing; }
        _historySelectionOrder.Clear(); _historySelectionOrder.AddRange(admitted.Select(HistoryRevisionItems.Key));
        _quickSearchLastCommit = _historySelectionOrder.LastOrDefault();
        _historySelectionWork = PublishHistorySelectionChange();
        _commits.ScrollIntoView(target); _commits.UpdateLayout(); row.Focus(); args.Handled = true;
    }

    private void PreviewArtificialAmend(PointerPressedEventArgs args)
    {
        if (args.Handled || _busy || !args.KeyModifiers.HasFlag(KeyModifiers.Control)
            || !args.GetCurrentPoint(_commits).Properties.IsLeftButtonPressed || _commits.SelectedItems?.Count != 1) { return; }
        ListBoxItem? row = (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not ArtificialHistoryRevision artificial || !_commits.SelectedItems.Contains(artificial)) { return; }
        string? parent = _history.FirstOrDefault(commit => commit.Hash == _historyGraph.CurrentHead)?.Parents.FirstOrDefault();
        if (parent is null || HistoryItem(parent) is not GitCommit previous) { return; }
        bool refreshing = _refreshing; _refreshing = true;
        try
        {
            _commits.Selection.BeginBatchUpdate();
            try
            {
                _commits.Selection.Clear(); _commits.Selection.Select(_commits.Items.IndexOf(previous));
                _commits.Selection.Select(_commits.Items.IndexOf(artificial));
            }
            finally { _commits.Selection.EndBatchUpdate(); }
        }
        finally { _refreshing = refreshing; }
        _historySelectionOrder.Clear(); _historySelectionOrder.Add(previous.Hash); _historySelectionOrder.Add(artificial.GraphId);
        _historySelectionWork = PublishHistorySelectionChange();
        _commits.Selection.AnchorIndex = _commits.Items.IndexOf(artificial); row.Focus(); args.Handled = true;
    }

    private void InitializeHistoryKeyboard()
    {
        _commits.AutoScrollToSelectedItem = false;
        _commits.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!_refreshing && !_busy && args.GetCurrentPoint(_commits).Properties.PointerUpdateKind is
                PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed) { RecordHistorySelectionIntent(); }
            PreviewHistoryRange(args); PreviewArtificialAmend(args);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _commits.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Handled || !_commits.IsKeyboardFocusWithin || _busy || _commits.ItemCount == 0) { return; }
            if (args.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Space)
            { RecordHistorySelectionIntent(); }
            if (args.Key == Key.A && args.KeyModifiers == KeyModifiers.Control) { args.Handled = true; return; }
            bool control = args.KeyModifiers.HasFlag(KeyModifiers.Control), shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (args.KeyModifiers.HasFlag(KeyModifiers.Alt)) { return; }
            int current = _commits.Selection.AnchorIndex;
            if (FocusManager?.GetFocusedElement() is Control focused
                && focused.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is { } item)
            { current = _commits.Items.IndexOf(item); }
            if (current < 0) { current = Math.Max(0, _commits.SelectedIndex); }
            int page = Math.Max(1, (int)((_commits.Scroll?.Viewport.Height ?? _commits.Bounds.Height) / BrowseRowHeight));
            int? next = args.Key switch
            {
                Key.Home => 0, Key.End => _commits.ItemCount - 1,
                Key.Up when control => 0, Key.Down when control => _commits.ItemCount - 1,
                Key.PageUp when !control => Math.Max(0, current - page),
                Key.PageDown when !control => Math.Min(_commits.ItemCount - 1, current + page), _ => null
            };
            if (next is not int index) { return; }
            int anchor = _commits.Selection.AnchorIndex;
            _commits.Selection.BeginBatchUpdate();
            try
            {
                _commits.Selection.Clear();
                if (shift && anchor >= 0) { _commits.Selection.SelectRange(anchor, index); }
                else { _commits.Selection.Select(index); }
            }
            finally { _commits.Selection.EndBatchUpdate(); }
            _commits.Selection.AnchorIndex = shift && anchor >= 0 ? anchor : index;
            _commits.ScrollIntoView(_commits.Items[index]!); _commits.UpdateLayout();
            if (_commits.ContainerFromIndex(index) is Control row) { row.Focus(NavigationMethod.Directional); }
            else { _commits.Focus(NavigationMethod.Directional); }
            args.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

}
