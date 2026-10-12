namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    // QuickFetch deliberately lets Git choose the current branch's remote.
    // Ordinary fetches preserve fetch.prune/tagopt configured by the user.
    public Task<GitResult> FetchConfiguredRemoteAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["fetch"], cancellationToken);

    public Task<GitResult> FetchRemoteAsync(string remote, bool prune = false, CancellationToken cancellationToken = default)
        => MutateAsync(prune ? ["fetch", "--prune", "--force", "--", ValidateValue(remote)]
            : ["fetch", "--", ValidateValue(remote)], cancellationToken);

    public Task<GitResult> FetchAllAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["fetch", "--all"], cancellationToken);

    public Task<GitResult> FetchRemoteBranchAsync(string remote, string branch, bool prune = false, CancellationToken cancellationToken = default)
        => MutateAsync(prune ? ["fetch", "--prune", "--force", "--", ValidateValue(remote), ValidateValue(branch)]
            : ["fetch", "--", ValidateValue(remote), ValidateValue(branch)], cancellationToken);

    public Task<GitResult> FetchPruneAllAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["fetch", "--all", "--prune", "--force"], cancellationToken);

    public Task<GitResult> PullMergeAsync(string? remote = null, string? branch = null, CancellationToken cancellationToken = default)
        => PullModeAsync("--no-rebase", remote, branch, cancellationToken);

    public Task<GitResult> PullRebaseAsync(string? remote = null, string? branch = null, CancellationToken cancellationToken = default)
        => PullModeAsync("--rebase", remote, branch, cancellationToken);

    private async Task<GitResult> PullModeAsync(string mode, string? remote, string? branch, CancellationToken cancellationToken)
    {
        // Select the visible merge/rebase mode explicitly. Git interprets its
        // other configured options and their precedence, without rewriting them.
        List<string> arguments = ["pull", mode];
        if (remote is not null)
        {
            arguments.AddRange(["--", ValidateValue(remote)]);
            if (branch is not null) { arguments.Add(ValidateValue(branch)); }
        }
        else if (branch is not null) { throw new ArgumentException("A branch requires a remote.", nameof(branch)); }
        // Git treats either explicit --[no-]rebase as an override of configured
        // ff-only. Merge in the original menu was a plain pull: preserve that
        // configured restriction when selecting merge explicitly. Rebase keeps
        // the native --rebase precedence used by the original action.
        if (mode == "--no-rebase" && (await GetConfigAsync("^pull\\.ff$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value == "only")
        { arguments.Insert(2, "--ff-only"); }
        return await MutateAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
