namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Read effective Windows XML CI configuration. Disabled CI does not even inspect remotes.</summary>
    public async Task<GitHubActionsSettings> ReadGitHubActionsSettingsAsync(string globalSettingsPath, CancellationToken cancellationToken = default)
    {
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(globalSettingsPath,
            await GetInfoAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        GitHubActionsSettings settings = GitHubActionsSettings.Read(snapshot);
        if (!settings.Active) { return settings; }
        IReadOnlyList<GitRemote> remotes = await GetRemotesAsync(cancellationToken).ConfigureAwait(false);
        return GitHubActionsSettings.Read(snapshot, remotes.SelectMany(r => r.PushUrls.Count > 0 ? r.PushUrls : r.FetchUrls));
    }
}
