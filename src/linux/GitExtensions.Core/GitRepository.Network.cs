namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    // Ordinary fetches preserve fetch.prune and remote tagopt. Explicit choices
    // only add their original flags; they never rewrite the user's Git config.
    public Task<GitResult> FetchConfiguredRemoteAsync(CancellationToken cancellationToken = default)
        => FetchNetworkAsync(null, null, new(), false, cancellationToken);

    public Task<GitResult> FetchRemoteAsync(string remote, bool prune = false, CancellationToken cancellationToken = default)
        => FetchRemoteAsync(remote, new GitFetchOptions(Prune: prune), cancellationToken);

    public Task<GitResult> FetchRemoteAsync(string remote, GitFetchOptions options, CancellationToken cancellationToken = default)
        => FetchNetworkAsync(ValidateValue(remote), null, options, false, cancellationToken);

    public Task<GitResult> FetchAllAsync(CancellationToken cancellationToken = default)
        => FetchAllAsync(new GitFetchOptions(), cancellationToken);

    public Task<GitResult> FetchAllAsync(GitFetchOptions options, CancellationToken cancellationToken = default)
        => FetchNetworkAsync(null, null, options, true, cancellationToken);

    public Task<GitResult> FetchRemoteBranchAsync(string remote, string branch, bool prune = false, CancellationToken cancellationToken = default)
        => FetchRemoteBranchAsync(remote, branch, new GitFetchOptions(Prune: prune), cancellationToken);

    public Task<GitResult> FetchRemoteBranchAsync(string remote, string branch, GitFetchOptions options, CancellationToken cancellationToken = default)
        => FetchNetworkAsync(ValidateValue(remote), ValidateValue(branch), options, false, cancellationToken);

    public Task<GitResult> FetchPruneAllAsync(CancellationToken cancellationToken = default)
        => FetchAllAsync(new GitFetchOptions(Prune: true), cancellationToken);

    public Task<GitResult> PullMergeAsync(string? remote = null, string? branch = null, CancellationToken cancellationToken = default)
        => PullLegacyAsync(GitPullMode.Merge, remote, branch, cancellationToken);

    public Task<GitResult> PullRebaseAsync(string? remote = null, string? branch = null, CancellationToken cancellationToken = default)
        => PullLegacyAsync(GitPullMode.Rebase, remote, branch, cancellationToken);

    public async Task<bool> IsShallowRepositoryAsync(CancellationToken cancellationToken = default)
        => (await ExecuteAsync(["rev-parse", "--is-shallow-repository"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim() == "true";

    public async Task ValidateFetchOptionsAsync(GitFetchOptions options, CancellationToken cancellationToken = default)
    {
        ValidateFetchChoices(options);
        await ValidateUnshallowAsync(options.Unshallow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks options without changing refs, the index or any local changes.</summary>
    public async Task ValidatePullOptionsAsync(GitPullMode mode, string? remote, string? branch,
        GitPullOptions options, CancellationToken cancellationToken = default)
    {
        ValidatePullArguments(mode, remote, branch, options);
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        await ValidateUnshallowAsync(options.Unshallow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Save tracked local changes if requested, then pull under the mutation lock.
    /// Reapplication is deliberately left to the host's original Yes/No/remember prompt.</summary>
    public Task<GitPullExecutionResult> PullWithOptionsAsync(GitPullMode mode, string? remote, string? branch,
        GitPullOptions options, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ValidatePullArguments(mode, remote, branch, options);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            await ValidateUnshallowAsync(options.Unshallow, token).ConfigureAwait(false);
            using RebaseLease? rebaseLease = mode == GitPullMode.Rebase
                ? await AcquireRebaseLeaseAsync(token).ConfigureAwait(false) : null;
            if (rebaseLease is not null)
            {
                if (HasRebaseBackend(rebaseLease))
                { throw new InvalidOperationException("A rebase is already in progress. Continue, skip or abort it before pulling again."); }
                await CleanupObsoleteRebaseJournalsAsync(rebaseLease).ConfigureAwait(false);
            }
            if (mode == GitPullMode.Merge && (await GetConfigAsync("^pull\\.ff$", cancellationToken: token).ConfigureAwait(false)).LastOrDefault().Value == "only")
            { arguments.Insert(2, "--ff-only"); }
            string[] global = await GetNetworkOptionsAsync(token).ConfigureAwait(false);
            GitStash? saved = null;
            GitResult? stashResult = null;
            if (options.AutoStash)
            {
                // FormPull.GitStatus(UntrackedFilesMode.No, IgnoreSubmodulesMode.All):
                // untracked-only files and dirty submodule checkouts do not trigger a stash.
                GitResult status = await ExecuteAsync(["status", "--porcelain=v1", "-z", "--untracked-files=no", "--ignore-submodules=all"], token).ConfigureAwait(false);
                if (status.StandardOutput.Length > 0)
                {
                    GitStash? previous = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault();
                    stashResult = await ExecuteAsync(options.IncludeUntrackedInAutoStash
                        ? ["stash", "push", "--include-untracked"] : ["stash", "push"], token).ConfigureAwait(false);
                    GitStash? current = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault();
                    if (current?.Hash != previous?.Hash) { saved = current; }
                }
            }
            GitResult result;
            try { result = await _runner.RunAsync(Path, [.. GlobalOptions, .. global, .. arguments], token).ConfigureAwait(false); }
            finally { if (rebaseLease is not null) { await CleanupObsoleteRebaseJournalsAsync(rebaseLease).ConfigureAwait(false); } }
            // Git errors are returned with the saved stash so the host can keep it
            // and expose conflict recovery. Cancellation still propagates normally.
            return new GitPullExecutionResult(result, saved, arguments.ToArray()) { StashResult = stashResult };
        }, cancellationToken);
    }

    private async Task<GitResult> PullLegacyAsync(GitPullMode mode, string? remote, string? branch, CancellationToken token)
    {
        GitPullExecutionResult result = await PullWithOptionsAsync(mode, remote, branch, new(), token).ConfigureAwait(false);
        if (!result.Result.Success) { throw new GitCommandException(result.Arguments, result.Result); }
        return result.Result;
    }

    private Task<GitResult> FetchNetworkAsync(string? remote, string? branch, GitFetchOptions options, bool all, CancellationToken token)
    {
        ValidateFetchChoices(options);
        List<string> arguments = ["fetch", "--progress"];
        if (all) { arguments.Add("--all"); }
        AddFetchOptions(arguments, options.Tags, options.Unshallow);
        if (options.Prune || options.PruneTags) { arguments.AddRange(["--prune", "--force"]); }
        if (options.PruneTags) { arguments.Add("--prune-tags"); }
        if (remote is not null)
        {
            arguments.AddRange(["--", remote]);
            if (branch is not null) { arguments.Add(branch); }
        }
        return WithMutationAsync(async cancellation =>
        {
            await ValidateUnshallowAsync(options.Unshallow, cancellation).ConfigureAwait(false);
            string[] global = await GetNetworkOptionsAsync(cancellation).ConfigureAwait(false);
            GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. global, .. arguments], cancellation).ConfigureAwait(false);
            if (!result.Success) { throw new GitCommandException(arguments, result); }
            return result;
        }, token);
    }

    private static List<string> ValidatePullArguments(GitPullMode mode, string? remote, string? branch, GitPullOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (mode is not (GitPullMode.Merge or GitPullMode.Rebase)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        ValidateTagChoice(options.Tags);
        if (remote is null && branch is not null) { throw new ArgumentException("A branch requires a remote.", nameof(branch)); }
        if (branch?.Contains('*') == true) { throw new ArgumentException("Wildcards are only allowed when fetching.", nameof(branch)); }
        List<string> arguments = ["pull", mode == GitPullMode.Rebase ? "--rebase" : "--no-rebase", "--progress"];
        AddFetchOptions(arguments, options.Tags, options.Unshallow);
        if (remote is not null)
        {
            arguments.AddRange(["--", ValidateValue(remote)]);
            if (branch is not null) { arguments.Add(ValidateValue(branch)); }
        }
        return arguments;
    }

    private static void ValidateTagChoice(GitFetchTags tags)
    {
        if (tags is not (GitFetchTags.FollowTagOpt or GitFetchTags.None or GitFetchTags.All))
        { throw new ArgumentOutOfRangeException(nameof(tags)); }
    }

    private static void ValidateFetchChoices(GitFetchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateTagChoice(options.Tags);
    }

    private static void AddFetchOptions(List<string> arguments, GitFetchTags tags, bool unshallow)
    {
        if (tags == GitFetchTags.All) { arguments.Add("--tags"); }
        else if (tags == GitFetchTags.None) { arguments.Add("--no-tags"); }
        if (unshallow) { arguments.Add("--unshallow"); }
    }

    private async Task ValidateUnshallowAsync(bool requested, CancellationToken token)
    {
        if (requested && !await IsShallowRepositoryAsync(token).ConfigureAwait(false))
        { throw new InvalidOperationException("Download full history is only available for a shallow repository."); }
    }

    private async Task<string[]> GetNetworkOptionsAsync(CancellationToken token)
    {
        IReadOnlyList<KeyValuePair<string, string>> settings = await GetConfigAsync("^(fetch\\.parallel|submodule\\.fetchjobs)$", cancellationToken: token).ConfigureAwait(false);
        List<string> options = [];
        foreach (string key in new[] { "fetch.parallel", "submodule.fetchjobs" })
        {
            if (string.IsNullOrWhiteSpace(settings.LastOrDefault(value => value.Key == key).Value))
            { options.AddRange(["-c", key + "=0"]); }
        }
        return options.ToArray();
    }
}
