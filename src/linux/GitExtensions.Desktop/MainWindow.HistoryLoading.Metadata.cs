using System.Globalization;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private async Task LoadProgressiveInventoryAsync(BrowseHistoryLoad load)
    {
        CancellationToken token = load.Cancellation.Token;
        GitRepository repository = load.Repository;
        IReadOnlyList<GitRemote> remotes = await repository.GetRemotesAsync(token);
        IReadOnlyList<GitStash> stashes = await repository.GetStashesAsync(token);
        bool stagedStash = _supportsStashStaged ?? await repository.SupportsStashStagedAsync(token);
        IReadOnlyList<GitWorktree> worktrees = await repository.GetWorktreesAsync(token);
        IReadOnlyList<GitSubmodule> submodules = await repository.GetSubmodulesAsync(token);
        IReadOnlyList<KeyValuePair<string, string>> configuration = await repository.GetConfigAsync(cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!CanPublishHistory(load)) { return; }
        var general = await ReadProgressiveGeneralStatusAsync(load, submodules, token);
        if (_fileMenuAdditionalOperations is not null) { await _fileMenuAdditionalOperations.RefreshAsync(repository, token); }
        token.ThrowIfCancellationRequested();
        if (!CanPublishHistory(load)) { return; }
        bool refreshing = _refreshing; _refreshing = true;
        try
        {
            _generalTracking = general.Tracking; _generalSubmoduleStatus = general.Submodules;
            _supportsStashStaged = stagedStash;
            _remotes.ItemsSource = remotes; _stashes.ItemsSource = stashes;
            _worktrees.ItemsSource = worktrees; _submodules.ItemsSource = submodules;
            _configuredUserEmail = configuration.LastOrDefault(entry => entry.Key == "user.email").Value;
            _configuredUserName = configuration.LastOrDefault(entry => entry.Key == "user.name").Value;
            _configuration.ItemsSource = configuration.Select(entry => $"{entry.Key} = {entry.Value}").ToList();
            RefreshGeneralStatusPresentation(load.Status, stashes);
            UpdateBrowserToolbar(load.Status); UpdateVirtualRevisionRows(load.Status);
            UpdateBrowserNavigation(load.References, stashes, submodules, worktrees);
        }
        finally { _refreshing = refreshing; }
        UpdateHistoryPresentation();
        if (LastException is null && !_busy && load.RetainedFailure is null) { _state.Text = _repositorySummary; }
        RestoreRetainedHistoryFailure(load);
    }

    private async Task<(GitBranchTrackingStatus? Tracking, Dictionary<string, GitSubmoduleBrowseStatus> Submodules)> ReadProgressiveGeneralStatusAsync(
        BrowseHistoryLoad load, IReadOnlyList<GitSubmodule> submodules, CancellationToken token)
    {
        GitBranchTrackingStatus? tracking = null;
        Dictionary<string, GitSubmoduleBrowseStatus> statuses = new(StringComparer.Ordinal);
        GitStatus status = load.Status;
        if (_preferences.ShowAheadBehindData && !status.IsDetached && !status.IsUnborn && status.Branch is not null)
        {
            try { tracking = await load.Repository.GetBranchTrackingStatusAsync(status.Branch, token); }
            catch (GitCommandException error) { if (CanPublishHistory(load)) { AppendOutput(error.Message); } }
        }
        if (_preferences.ShowSubmoduleStatus && (_preferences.ShowGitStatusInBrowseToolbar || _preferences.ShowGitStatusForArtificialCommits)
            && !load.Repository.IsBare && submodules.Count > 0)
        {
            try { statuses = (await load.Repository.GetSubmoduleBrowseStatusesAsync(token)).ToDictionary(value => value.Path, StringComparer.Ordinal); }
            catch (Exception error) when (error is GitCommandException or IOException or InvalidOperationException)
            { if (CanPublishHistory(load)) { AppendOutput(error.Message); } }
        }
        return (tracking, statuses);
    }

    private async Task LoadProgressiveMetadataAsync(BrowseHistoryLoad load)
    {
        if (!CanPublishHistory(load)) { return; }
        Dictionary<string, (DateTimeOffset Date, string Notes)> metadata = new(StringComparer.Ordinal);
        HistoryViewPreferences view = load.View;
        if ((!view.ShowAuthorDate || view.ShowNotes || view.ShowNotesColumn) && load.Received.Count > 0)
        {
            foreach (GitCommit[] batch in load.Received.Chunk(200))
            {
                string format = view.ShowNotes || view.ShowNotesColumn ? "%H%x00%cI%x00%N" : "%H%x00%cI%x00";
                GitResult result = await load.Repository.RunAsync(new[] { "show", "--no-patch", "--no-ext-diff", "--no-textconv", "--encoding=UTF-8", "-z", "--format=" + format }
                    .Concat(batch.Select(commit => commit.Hash)).Append("--").ToArray(), load.Cancellation.Token);
                if (!result.Success) { throw new GitCommandException(["show"], result); }
                string[] fields = result.StandardOutput.Split('\0');
                if (fields.Length != batch.Length * 3 + 1) { throw new FormatException("Invalid commit metadata or binary Git note."); }
                for (int index = 0; index < batch.Length; index++)
                {
                    if (fields[index * 3] != batch[index].Hash) { throw new FormatException("Commit metadata did not match its requested revision."); }
                    metadata.Add(fields[index * 3], (DateTimeOffset.Parse(fields[index * 3 + 1], CultureInfo.InvariantCulture), fields[index * 3 + 2]));
                }
            }
        }
        load.Cancellation.Token.ThrowIfCancellationRequested();
        if (!CanPublishHistory(load)) { return; }
        _historyMetadata = metadata; UpdateHistoryPresentation();
    }
}
