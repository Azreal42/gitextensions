namespace GitExtensions.Linux;

public sealed record GitStashSaveOptions(string? Message = null, bool IncludeUntracked = false,
    bool KeepIndex = false, IReadOnlyList<string>? Paths = null, bool StagedOnly = false);
public sealed record GitStashChanges(GitRevisionChanges Tracked, GitRevisionChanges? Untracked, string Message);

public sealed partial class GitRepository
{
    public async Task<bool> SupportsStashStagedAsync(CancellationToken cancellationToken = default)
        => await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false) >= new Version(2, 35);

    public Task<GitResult> StashAsync(GitStashSaveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string[] paths = options.Paths?.Select(ValidatePath).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (options.Paths is not null && paths.Length == 0) { throw new ArgumentException("Select files before stashing selected changes.", nameof(options)); }
        if (options.StagedOnly && (options.IncludeUntracked || options.KeepIndex || paths.Length != 0))
        { throw new ArgumentException("Stash staged cannot be combined with keep index, untracked files or selected paths.", nameof(options)); }
        List<string> arguments = ["stash", "push"];
        if (options.IncludeUntracked) { arguments.Add("--include-untracked"); }
        if (options.KeepIndex) { arguments.Add("--keep-index"); }
        if (options.StagedOnly) { arguments.Add("--staged"); }
        if (!string.IsNullOrWhiteSpace(options.Message)) { arguments.AddRange(["--message", options.Message.Trim()]); }
        // Do not enable global literal-pathspecs: stash's untracked cleanup uses
        // internal magic pathspecs. Only the user's selected paths are literal.
        if (paths.Length > 0) { arguments.AddRange(["--", ..paths.Select(path => ":(literal)" + path)]); }
        return MutateAsync(arguments, cancellationToken);
    }

    public async Task<GitStashChanges> GetStashChangesAsync(GitStash stash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stash);
        GitCommit commit = (await GetLogAsync(1, stash.Hash, cancellationToken, all: false).ConfigureAwait(false)).Single();
        if (commit.Parents.Count < 2) { throw new ArgumentException("Select a stash commit.", nameof(stash)); }
        GitRevisionChanges tracked = await GetRevisionChangesAsync(commit.Hash, commit.Parents[0], cancellationToken).ConfigureAwait(false);
        GitRevisionChanges? untracked = commit.Parents.Count > 2
            ? await GetRevisionChangesAsync(commit.Parents[2], cancellationToken: cancellationToken).ConfigureAwait(false) : null;
        string message = (await ExecuteAsync(["show", "--no-patch", "--encoding=UTF-8", "--format=%B", commit.Hash, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\n');
        return new(tracked, untracked, message);
    }

    public Task<GitResult> ApplyStashAsync(GitStash stash, bool pop = false, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await CheckStashSelectionAsync(stash, token).ConfigureAwait(false);
            // Windows apply/pop do not pass --index. Keep index only affects save.
            return await ExecuteAsync(["stash", pop ? "pop" : "apply", pop ? stash.Reference : stash.Hash], token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitResult> PopLatestStashAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            GitStash? stash = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault();
            if (stash is null) { throw new InvalidOperationException("There are no stashes."); }
            return await ExecuteAsync(["stash", "pop", stash.Reference], token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitResult> DropStashAsync(GitStash stash, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await CheckStashSelectionAsync(stash, token).ConfigureAwait(false);
            return await ExecuteAsync(["stash", "drop", stash.Reference], token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task CheckStashSelectionAsync(GitStash stash, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stash);
        GitStash? current = (await GetStashesAsync(token).ConfigureAwait(false)).FirstOrDefault(entry => entry.Reference == stash.Reference);
        if (current?.Hash != stash.Hash) { throw new InvalidOperationException("The stash list changed. Refresh and select the stash again."); }
    }
}
