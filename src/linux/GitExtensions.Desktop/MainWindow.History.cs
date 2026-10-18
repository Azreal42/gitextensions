namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private int _appliedHistoryMaximum, _appliedHistoryBatchSize;
    private HistoryLoadingPolicy HistoryPolicy => new(_preferences.MaxRevisionGraphCommits);

    /// <summary>The Settings publisher updates preferences first, then awaits a refresh when this returns true.</summary>
    internal bool ApplyGeneralHistoryPolicy()
    {
        int batchSize = Math.Clamp(_preferences.HistoryLimit, 1, 5000);
        bool batchChanged = batchSize != _appliedHistoryBatchSize;
        bool changed = batchChanged || _appliedHistoryMaximum != _preferences.MaxRevisionGraphCommits;
        _appliedHistoryMaximum = _preferences.MaxRevisionGraphCommits; _appliedHistoryBatchSize = batchSize;
        if (batchChanged || _historyQuery.Count <= 0) { _historyQuery = _historyQuery with { Count = batchSize }; changed = true; }
        _historyWindow = HistoryPolicy.ClampWindow(batchChanged ? _historyQuery.Count : Math.Max(_historyWindow, 1));
        _historyHasMore &= HistoryPolicy.CanExpand(_historyWindow);
        if (_historyMore is not null) { _historyMore.IsVisible = _historyHasMore; }
        return changed;
    }

    private Task ReloadHistoryAsync()
    {
        _historyWindow = HistoryPolicy.InitialWindow(_historyQuery.Count);
        return RefreshAsync();
    }

    private Task SetHistoryScopeAsync(bool all)
    {
        return SetHistoryBranchScopeAsync(all ? HistoryBranchScope.All : HistoryBranchScope.Current);
    }

    private async Task FilterHistoryAsync()
    {
        if (_repository is null || _busy) { return; }
        HistoryFilterDialog dialog = new(_historyQuery, _acceptedBranchFilter, _historyBranchScope == HistoryBranchScope.Filtered,
            _fileHistoryPreferencesStore.Load())
        { RequestedThemeVariant = ActualThemeVariant };
        GitLogQuery? query = await dialog.ShowDialog<GitLogQuery?>(this);
        if (query is null) { return; }
        _fileHistoryPreferencesStore.Save(_fileHistoryPreferencesStore.Load() with
        { FullHistory = dialog.FullHistorySetting, SimplifyMerges = dialog.SimplifyMergesSetting });
        _historyView = _historyView with { ShowReflog = query.Reflog };
        await ApplyBranchFilterAsync(query, dialog.RememberedBranchFilter, dialog.IsBranchFilterEnabled);
    }

    private Task LoadMoreHistoryAsync()
    {
        if (_busy || !_historyHasMore) { return Task.CompletedTask; }
        int next = HistoryPolicy.NextWindow(_historyWindow, _historyQuery.Count);
        if (next == _historyWindow)
        {
            _historyHasMore = false;
            if (_historyMore is not null) { _historyMore.IsVisible = false; }
            return Task.CompletedTask;
        }
        _historyWindow = next;
        return RefreshAsync();
    }
}
