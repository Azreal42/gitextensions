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
        _historyQuery = _historyQuery with { All = all, Revision = all ? null : "HEAD" };
        _historyRevision.Text = _historyQuery.Revision;
        return ReloadHistoryAsync();
    }

    private async Task FilterHistoryAsync()
    {
        if (_repository is null || _busy) { return; }
        HistoryFilterDialog dialog = new(_historyQuery with { Revision = string.IsNullOrWhiteSpace(_historyRevision.Text) ? null : _historyRevision.Text });
        GitLogQuery? query = await dialog.ShowDialog<GitLogQuery?>(this);
        if (query is null) { return; }
        _historyQuery = query;
        _historyRevision.Text = query.Revision;
        await ReloadHistoryAsync();
    }

    private Task LoadMoreHistoryAsync()
    {
        if (_busy || !_historyHasMore) { return Task.CompletedTask; }
        _historyWindow = (int)Math.Min(100000L, (long)_historyWindow + _historyQuery.Count);
        return RefreshAsync();
    }
}
