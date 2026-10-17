namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private IndexFlagFileList? _indexFlagFiles;

    private void ConfigureIndexFlagFiles()
    {
        _indexFlagFiles = new(_unstagedView,
            () => !_busy && !_commitRequested && !_lifetime.IsCancellationRequested && IsVisible && !_repository.IsBare,
            RefreshAsync, (paths, skip, enabled) => RunAsync(T("Update index flags"), async () =>
            {
                // Refresh even after a partial/native failure; retain its original
                // diagnostic if the follow-up read fails too.
                Exception? original = null;
                try
                {
                    if (skip) { await _repository.SetSkipWorktreeAsync(paths, enabled, OperationToken); }
                    else { await _repository.SetAssumeUnchangedAsync(paths, enabled, OperationToken); }
                }
                catch (Exception error) { original = error; throw; }
                finally
                {
                    HasRepositoryChanges = true;
                    try { if (!OperationToken.IsCancellationRequested) { await RefreshDataAsync(); } }
                    catch (Exception error) when (original is not null) { original.Data["Index flag refresh error"] = error.Message; }
                    RepositoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }));
    }
}
