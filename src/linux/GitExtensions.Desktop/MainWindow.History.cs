namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Task ReloadHistoryAsync()
    {
        _historyWindow = _historyQuery.Count;
        return RefreshAsync();
    }

    private Task SetHistoryScopeAsync(bool all)
    {
        return SetHistoryBranchScopeAsync(all ? HistoryBranchScope.All : HistoryBranchScope.Current);
    }

    private async Task FilterHistoryAsync()
    {
        if (_repository is null || _busy) { return; }
        HistoryFilterDialog dialog = new(_historyQuery, _acceptedBranchFilter, _historyBranchScope == HistoryBranchScope.Filtered)
        { RequestedThemeVariant = ActualThemeVariant };
        GitLogQuery? query = await dialog.ShowDialog<GitLogQuery?>(this);
        if (query is null) { return; }
        await ApplyBranchFilterAsync(query, dialog.RememberedBranchFilter, dialog.IsBranchFilterEnabled);
    }

    private Task LoadMoreHistoryAsync()
    {
        if (_busy || !_historyHasMore) { return Task.CompletedTask; }
        _historyWindow = (int)Math.Min(100000L, (long)_historyWindow + _historyQuery.Count);
        return RefreshAsync();
    }
}
