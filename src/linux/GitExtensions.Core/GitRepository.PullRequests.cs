namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Fetches one PR branch without force, prune, tags or checkout. An existing remote is never redirected.</summary>
    public Task<GitResult> FetchPullRequestBranchAsync(string sourceUrl, string headRef, string localBranch, string? addRemoteName = null,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        ValidateValue(sourceUrl); ValidateValue(headRef); ValidateValue(localBranch);
        if (addRemoteName is not null) { ValidateValue(addRemoteName); }
        return WithRemoteSettingsAsync(async (state, token) =>
        {
            await ExecuteAsync(["check-ref-format", "refs/heads/" + headRef], token).ConfigureAwait(false);
            await ExecuteAsync(["check-ref-format", "refs/heads/" + localBranch], token).ConfigureAwait(false);
            string source = sourceUrl;
            if (addRemoteName is not null)
            {
                await ValidateRemoteSettingsNameAsync(addRemoteName, token).ConfigureAwait(false);
                GitRemoteSettingsSnapshot? existing = (await ReadRemoteSettingsAsync(state, token).ConfigureAwait(false)).FirstOrDefault(r => r.Name == addRemoteName);
                if (existing is null)
                {
                    await CheckRemoteSettingsUnchangedAsync(state, token).ConfigureAwait(false);
                    await ExecuteAsync(["remote", "add", "--", addRemoteName, sourceUrl], token).ConfigureAwait(false);
                }
                else if (existing.Disabled || existing.FetchUrls.Count != 1 || existing.FetchUrls[0] != sourceUrl)
                { throw new InvalidOperationException("A remote with this name already exists with a different URL. Choose another remote name in Manage remotes."); }
                source = addRemoteName;
            }
            string[] arguments = ["fetch", "--no-tags", "--progress", "--", source, "refs/heads/" + headRef + ":refs/heads/" + localBranch];
            string[] network = await GetNetworkOptionsAsync(token).ConfigureAwait(false);
            GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. network, .. arguments], token, outputReceived: outputReceived).ConfigureAwait(false);
            if (!result.Success) { throw new GitCommandException(arguments, result); }
            return result;
        }, cancellationToken);
    }
}
