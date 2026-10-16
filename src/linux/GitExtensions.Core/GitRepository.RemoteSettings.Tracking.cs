namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<IReadOnlyList<GitBranchTrackingSnapshot>> GetBranchTrackingSettingsAsync(CancellationToken cancellationToken = default)
        => WithRemoteSettingsAsync<IReadOnlyList<GitBranchTrackingSnapshot>>(async (state, token) =>
        {
            IReadOnlyList<GitReference> references = await GetReferencesAsync(token).ConfigureAwait(false);
            await CheckRemoteSettingsUnchangedAsync(state, token).ConfigureAwait(false);
            return Array.AsReadOnly(references.Where(r => r.IsBranch).OrderBy(r => r.ShortName, StringComparer.Ordinal)
                .Select(r => new GitBranchTrackingSnapshot(state, r.ShortName, r.Hash)).ToArray());
        }, cancellationToken);

    public Task<GitBranchTrackingSnapshot> SetBranchTrackingRemoteAsync(GitBranchTrackingSnapshot expected, string? remote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        remote = string.IsNullOrEmpty(remote) ? null : remote;
        ValidateRemoteSettingsText(remote, "tracking remote");
        return WithRemoteSettingsAsync(async (state, token) =>
        {
            await CheckBranchTrackingSnapshotAsync(expected, state, token).ConfigureAwait(false);
            if (remote is not null && remote != ".") { await ValidateRemoteSettingsNameAsync(remote, token).ConfigureAwait(false); }
            string key = "branch." + expected.LocalBranch + ".remote";
            List<RemoteConfigEdit> edits = [RemoteTrackingScalar(state, key, remote)];
            if (remote is not null && string.IsNullOrEmpty(expected.MergeRef))
            {
                string mergeKey = "branch." + expected.LocalBranch + ".merge";
                RequireLocalTrackingMerge(state, mergeKey);
                edits.Add(new(mergeKey, ["refs/heads/" + expected.LocalBranch]));
            }
            return await SaveBranchTrackingAsync(expected, state, edits, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitBranchTrackingSnapshot> SetBranchTrackingMergeAsync(GitBranchTrackingSnapshot expected, string? mergeBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string? value = string.IsNullOrWhiteSpace(mergeBranch) ? null : mergeBranch.Trim();
        ValidateRemoteSettingsText(value, "tracking branch");
        if (value is not null && !value.StartsWith("refs/", StringComparison.Ordinal)) { value = "refs/heads/" + value; }
        return WithRemoteSettingsAsync(async (state, token) =>
        {
            await CheckBranchTrackingSnapshotAsync(expected, state, token).ConfigureAwait(false);
            if (value is not null) { await ExecuteAsync(["check-ref-format", value], token).ConfigureAwait(false); }
            string key = "branch." + expected.LocalBranch + ".merge";
            if (expected.MergeRefs.Count <= 1 && expected.MergeRef == value) { return expected; }
            RequireLocalTrackingMerge(state, key);
            return await SaveBranchTrackingAsync(expected, state, [new(key, value is null ? [] : [value])], token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task CheckBranchTrackingSnapshotAsync(GitBranchTrackingSnapshot expected, RemoteSettingsState actual, CancellationToken token)
    {
        CheckRemoteSettingsState(expected.State, actual);
        GitReference? branch = (await GetReferencesAsync(token).ConfigureAwait(false)).SingleOrDefault(r => r.IsBranch && r.ShortName == expected.LocalBranch);
        if (branch is null || branch.Hash != expected.Hash)
        { throw new GitRemoteSettingsException("The selected local branch changed or no longer exists. Reload its tracking settings.", false, expected.LocalBranch); }
    }
    private static RemoteConfigEdit RemoteTrackingScalar(RemoteSettingsState state, string key, string? value)
    {
        bool inherited = state.Effective.Any(s => s.Key == key && (s.Scope != "local" || s.Origin != state.Config.Path));
        if (value is null && inherited)
        { throw new GitRemoteSettingsException("The tracking remote is inherited from another configuration file and cannot be unset locally. An empty remote is not a safe Git override; edit the source configuration or choose a valid remote instead.", false, null); }
        return new(key, value is not null ? [value] : []);
    }
    private static void RequireLocalTrackingMerge(RemoteSettingsState state, string key)
    {
        if (state.Effective.Any(s => s.Key == key && (s.Scope != "local" || s.Origin != state.Config.Path)))
        { throw new GitRemoteSettingsException("The merge destination is inherited from another configuration file. Git treats branch.merge as a list, so a local value cannot replace or clear that inherited destination. Edit the source configuration instead.", false, null); }
    }
    private async Task<GitBranchTrackingSnapshot> SaveBranchTrackingAsync(GitBranchTrackingSnapshot expected, RemoteSettingsState state,
        IReadOnlyList<RemoteConfigEdit> edits, CancellationToken token)
    {
        byte[] bytes = await PrepareRemoteSettingsConfigAsync(state, edits, token).ConfigureAwait(false);
        await CheckBranchTrackingSnapshotAsync(expected, await ReadRemoteSettingsStateAsync(state.CommonDirectory, token).ConfigureAwait(false), token).ConfigureAwait(false);
        bool changed = false;
        try
        {
            await PublishRemoteSettingsConfigAsync(state, bytes, token).ConfigureAwait(false);
            changed = !bytes.AsSpan().SequenceEqual(state.Config.Bytes);
            RemoteSettingsState saved = await ReadRemoteSettingsStateAsync(state.CommonDirectory, CancellationToken.None).ConfigureAwait(false);
            return new(saved, expected.LocalBranch, expected.Hash);
        }
        catch (Exception exception) when (changed)
        { throw new GitRemoteSettingsException("Tracking configuration changed, but its updated state could not be read. Reload the repository. " + exception.Message, true, expected.LocalBranch, exception); }
    }
}
