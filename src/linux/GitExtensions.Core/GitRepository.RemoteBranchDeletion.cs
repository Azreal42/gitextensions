using System.ComponentModel;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<IReadOnlyList<GitRemoteBranchDeletionPreview>> GetRemoteBranchDeletionPreviewAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(ReadRemoteBranchDeletionPreviewAsync, cancellationToken);

    /// <summary>Read the current local tracking candidates, including after Git has removed the remote-tracking refs.
    /// The confirmed remote/branch identities are retained, but cached local names are never reused.</summary>
    public Task<IReadOnlyList<string>> GetRemoteBranchTrackingLocalBranchesAsync(IReadOnlyList<GitRemoteBranchDeletionPreview> branches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branches);
        cancellationToken.ThrowIfCancellationRequested();
        (string Remote, string DestinationRef)[] targets = branches.Select(branch =>
        {
            ArgumentNullException.ThrowIfNull(branch);
            ValidateValue(branch.Remote); ValidateValue(branch.BranchName);
            return (branch.Remote, branch.DestinationRef);
        }).Distinct().ToArray();
        if (targets.Length == 0) { return Task.FromResult<IReadOnlyList<string>>([]); }
        return WithMutationAsync<IReadOnlyList<string>>(async token =>
        {
            IReadOnlyList<GitReference> references = await GetReferencesAsync(token).ConfigureAwait(false);
            IReadOnlyList<KeyValuePair<string, string>> configuration = await GetConfigAsync("^branch\\..*\\.(remote|merge)$", cancellationToken: token).ConfigureAwait(false);
            Dictionary<(string Remote, string DestinationRef), string[]> tracking = RemoteBranchTrackingLocalBranches(references, configuration);
            return targets.SelectMany(target => tracking.GetValueOrDefault(target) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<GitRemoteBranchDeletionPreview>> ReadRemoteBranchDeletionPreviewAsync(CancellationToken token)
    {
        IReadOnlyList<GitReference> references = await GetReferencesAsync(token).ConfigureAwait(false);
        GitRemote[] remotes = (await GetRemotesAsync(token).ConfigureAwait(false)).OrderByDescending(remote => remote.Name.Length).ToArray();
        IReadOnlyList<KeyValuePair<string, string>> configuration = await GetConfigAsync("^(branch\\..*\\.(remote|merge)|remote\\..*\\.fetch)$", cancellationToken: token).ConfigureAwait(false);
        Dictionary<(string Remote, string DestinationRef), string[]> localTracking = RemoteBranchTrackingLocalBranches(references, configuration);
        HashSet<string> merged = new(StringComparer.Ordinal);
        if (await HasHeadAsync(token).ConfigureAwait(false))
        {
            GitResult result = await ExecuteAsync(["for-each-ref", "--merged=HEAD", "--format=%(refname)", "refs/remotes/"], token).ConfigureAwait(false);
            merged.UnionWith(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        List<GitRemoteBranchDeletionPreview> branches = [];
        Dictionary<string, string[]> resolvedPushUrls = new(StringComparer.Ordinal);
        foreach (GitReference reference in references.Where(reference => reference.IsRemote && reference.SymbolicTarget is null))
        {
            GitRemote? remote = remotes.FirstOrDefault(remote => reference.Name.StartsWith("refs/remotes/" + remote.Name + "/", StringComparison.Ordinal));
            if (remote is null) { continue; }
            string[] fetchMappings = configuration.Where(item => item.Key == "remote." + remote.Name + ".fetch").Select(item => item.Value).ToArray();
            string[] destinations = fetchMappings.Select(mapping => RemoteBranchSource(mapping, reference.Name)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            // Stale, excluded, non-branch, or ambiguous mappings must never guess a server branch.
            if (destinations.Length != 1 || !destinations[0].StartsWith("refs/heads/", StringComparison.Ordinal)
                || fetchMappings.Any(mapping => mapping.StartsWith('^') && ReferencePatternMatches(mapping[1..], destinations[0]))) { continue; }
            string destination = destinations[0], branch = destination["refs/heads/".Length..];
            string[] tracking = localTracking.GetValueOrDefault((remote.Name, destination)) ?? [];
            if (!resolvedPushUrls.TryGetValue(remote.Name, out string[]? pushUrls))
            {
                // Git pushes to all fallback URLs, not just the first fetch URL. Let Git also
                // apply insteadOf/pushInsteadOf so confirmation covers the actual destinations.
                GitResult result = await ExecuteAsync(["remote", "get-url", "--push", "--all", "--", remote.Name], token).ConfigureAwait(false);
                pushUrls = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                resolvedPushUrls.Add(remote.Name, pushUrls);
            }
            branches.Add(new(reference, remote.Name, branch, merged.Contains(reference.Name), tracking) { ExpectedPushUrls = pushUrls });
        }
        return branches.OrderBy(branch => branch.Reference.Name, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<(string Remote, string DestinationRef), string[]> RemoteBranchTrackingLocalBranches(
        IReadOnlyList<GitReference> references, IReadOnlyList<KeyValuePair<string, string>> configuration)
    {
        Dictionary<string, string> remotes = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> merges = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> setting in configuration.Where(setting => setting.Key.StartsWith("branch.", StringComparison.Ordinal)))
        {
            if (setting.Key.EndsWith(".remote", StringComparison.Ordinal)) { remotes[setting.Key[7..^7]] = setting.Value; }
            else if (setting.Key.EndsWith(".merge", StringComparison.Ordinal))
            {
                string branch = setting.Key[7..^6];
                if (!merges.TryGetValue(branch, out HashSet<string>? destinations)) { merges[branch] = destinations = new(StringComparer.Ordinal); }
                destinations.Add(setting.Value);
            }
        }
        Dictionary<(string Remote, string DestinationRef), List<string>> tracking = [];
        foreach (GitReference local in references.Where(local => local.IsBranch && local.SymbolicTarget is null))
        {
            if (!remotes.TryGetValue(local.ShortName, out string? remote) || !merges.TryGetValue(local.ShortName, out HashSet<string>? destinations)) { continue; }
            foreach (string destination in destinations)
            {
                (string, string) target = (remote, destination);
                if (!tracking.TryGetValue(target, out List<string>? locals)) { tracking[target] = locals = []; }
                locals.Add(local.ShortName);
            }
        }
        return tracking.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Validate the entire selection, then delete each remote atomically under the legacy single mutation scope.</summary>
    public Task<GitRemoteBranchDeletionResult> DeleteRemoteBranchesAsync(GitDeleteRemoteBranchesOptions options,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        GitRemoteBranchDeletionPreview[] requested = RemoteDeletionRequests(options, cancellationToken);
        return WithMutationAsync(async token =>
        {
            IReadOnlyList<GitRemoteBranchDeletionPreview> selected = await ValidateRemoteDeletionTargetsAsync(requested, options.AllowUnmerged, token).ConfigureAwait(false);
            GitRemoteBranchDeletionBatchResult[] batches = selected.GroupBy(branch => branch.Remote, StringComparer.Ordinal)
                .Select(group => new GitRemoteBranchDeletionBatchResult(group.Key, group.ToArray(), GitRemoteBranchDeletionBatchStatus.NotAttempted)).ToArray();
            token.ThrowIfCancellationRequested();
            for (int index = 0; index < batches.Length; index++)
            {
                if (token.IsCancellationRequested) { break; }
                batches[index] = await RunRemoteDeletionBatchAsync(batches[index], token, outputReceived).ConfigureAwait(false);
                if (batches[index].Status == GitRemoteBranchDeletionBatchStatus.Cancelled) { break; }
            }
            return new GitRemoteBranchDeletionResult(batches, token.IsCancellationRequested);
        }, cancellationToken);
    }

    /// <summary>Freeze and validate ALL selected targets before the first remote is mutated.</summary>
    public Task<GitRemoteBranchDeletionPlan> PrepareRemoteBranchDeletionAsync(GitDeleteRemoteBranchesOptions options, CancellationToken cancellationToken = default)
    {
        GitRemoteBranchDeletionPreview[] requested = RemoteDeletionRequests(options, cancellationToken);
        return WithMutationAsync(async token => new GitRemoteBranchDeletionPlan(this, options.AllowUnmerged,
            await ValidateRemoteDeletionTargetsAsync(requested, options.AllowUnmerged, token).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Revalidate one planned remote's exact fetched hashes and push URLs immediately before its atomic transaction.</summary>
    public Task<GitRemoteBranchDeletionBatchResult> DeleteRemoteBranchBatchAsync(GitRemoteBranchDeletionPlan plan, string remote,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
        => WithMutationAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (!ReferenceEquals(plan.Repository, this)) { throw new ArgumentException("The deletion plan belongs to another repository.", nameof(plan)); }
            GitRemoteBranchDeletionPreview[] requested = plan.Branches.Where(branch => branch.Remote == remote).ToArray();
            if (requested.Length == 0) { throw new ArgumentException("The remote does not belong to this deletion plan.", nameof(remote)); }
            IReadOnlyList<GitRemoteBranchDeletionPreview> selected = await ValidateRemoteDeletionTargetsAsync(requested, plan.AllowUnmerged, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await RunRemoteDeletionBatchAsync(new(remote, selected, GitRemoteBranchDeletionBatchStatus.NotAttempted), token, outputReceived).ConfigureAwait(false);
        }, cancellationToken);

    private static GitRemoteBranchDeletionPreview[] RemoteDeletionRequests(GitDeleteRemoteBranchesOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Branches);
        cancellationToken.ThrowIfCancellationRequested();
        GitRemoteBranchDeletionPreview[] requested = options.Branches.ToArray();
        if (requested.Length == 0) { throw new ArgumentException("Select at least one remote branch.", nameof(options)); }
        if (requested.Any(branch => branch is null || branch.Reference is null)) { throw new ArgumentException("Select existing remote branches.", nameof(options)); }
        if (requested.Select(branch => branch.Reference.Name).Distinct(StringComparer.Ordinal).Count() != requested.Length)
        { throw new ArgumentException("A remote branch may only be selected once.", nameof(options)); }

        return requested;
    }

    private async Task<IReadOnlyList<GitRemoteBranchDeletionPreview>> ValidateRemoteDeletionTargetsAsync(
        GitRemoteBranchDeletionPreview[] requested, bool allowUnmerged, CancellationToken token)
    {
        IReadOnlyList<GitRemoteBranchDeletionPreview> current = await ReadRemoteBranchDeletionPreviewAsync(token).ConfigureAwait(false);
        List<GitRemoteBranchDeletionPreview> selected = [];
        foreach (GitRemoteBranchDeletionPreview branch in requested)
        {
            GitRemoteBranchDeletionPreview actual = current.SingleOrDefault(candidate => candidate.Reference.Name == branch.Reference.Name)
                ?? throw new ArgumentException("The selected remote branch no longer exists: " + branch.Reference.Name, nameof(requested));
            if (actual.Remote != branch.Remote || actual.BranchName != branch.BranchName || actual.ExpectedHash != branch.ExpectedHash)
            { throw new InvalidOperationException("The selected remote branch changed after confirmation; refresh the list: " + branch.Reference.Name); }
            if (!allowUnmerged && !actual.IsMerged)
            { throw new InvalidOperationException("The remote branch has not been merged into HEAD: " + actual.Reference.ShortName); }
            await ValidateReferenceNameAsync(actual.BranchName, branch: true, token).ConfigureAwait(false);
            if (!actual.ExpectedPushUrls.SequenceEqual(branch.ExpectedPushUrls, StringComparer.Ordinal))
            { throw new InvalidOperationException("The push destination changed after confirmation; refresh the list: " + actual.Remote); }
            if (actual.ExpectedPushUrls.Count != 1)
            { throw new InvalidOperationException("Remote branch deletion requires one push URL per remote: " + actual.Remote); }
            selected.Add(actual);
        }
        if (selected.GroupBy(branch => (branch.Remote, branch.BranchName)).Any(group => group.Select(branch => branch.ExpectedHash).Distinct(StringComparer.Ordinal).Count() > 1))
        { throw new InvalidOperationException("Fetched aliases disagree on the remote branch hash; refresh the list before deleting."); }

        return selected;
    }

    private async Task<GitRemoteBranchDeletionBatchResult> RunRemoteDeletionBatchAsync(GitRemoteBranchDeletionBatchResult batch,
        CancellationToken token, Action<GitProcessOutput>? outputReceived)
    {
        List<string> arguments = ["push", "--progress", "--porcelain", "--atomic", "--no-follow-tags"];
        GitRemoteBranchDeletionPreview[] targets = batch.Branches.DistinctBy(branch => branch.BranchName, StringComparer.Ordinal).ToArray();
        arguments.AddRange(targets.Select(branch => "--force-with-lease=" + branch.DestinationRef + ":" + branch.ExpectedHash));
        arguments.AddRange(["--", batch.Remote]);
        arguments.AddRange(targets.Select(branch => ":" + branch.DestinationRef));
        using MemoryStream output = new(), error = new();
        try
        {
            GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. arguments], token, outputReceived: item =>
            {
                (item.Stream == GitProcessOutputStream.StandardOutput ? output : error).Write(item.Bytes.Span);
                outputReceived?.Invoke(item);
            }).ConfigureAwait(false);
            return batch with
            {
                Status = result.Success ? GitRemoteBranchDeletionBatchStatus.Succeeded : GitRemoteBranchDeletionBatchStatus.Failed,
                Result = result,
                ErrorMessage = result.Success ? null : result.StandardError,
            };
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or Win32Exception or UnauthorizedAccessException)
        {
            bool cancelled = failure is OperationCanceledException && token.IsCancellationRequested;
            List<string> warnings = [];
            return batch with
            {
                Status = cancelled ? GitRemoteBranchDeletionBatchStatus.Cancelled : GitRemoteBranchDeletionBatchStatus.Failed,
                ErrorMessage = cancelled ? "Deletion was cancelled; refresh remote references to check the remote outcome." : failure.Message,
                PartialStandardOutput = GitOutputText.Utf8(output.ToArray(), "stdout", warnings),
                PartialStandardError = GitOutputText.Utf8(error.ToArray(), "stderr", warnings),
            };

        }
    }

    private static string? RemoteBranchSource(string refspec, string destination)
    {
        if (refspec.StartsWith('+')) { refspec = refspec[1..]; }
        if (refspec.StartsWith('^')) { return null; }
        int colon = refspec.IndexOf(':');
        if (colon < 0) { return null; }
        string source = refspec[..colon], target = refspec[(colon + 1)..];
        if (!source.StartsWith("refs/", StringComparison.Ordinal)) { return null; }
        int star = target.IndexOf('*');
        if (star < 0) { return target == destination && !source.Contains('*') ? source : null; }
        if (!destination.StartsWith(target[..star], StringComparison.Ordinal) || !destination.EndsWith(target[(star + 1)..], StringComparison.Ordinal)
            || destination.Length < target.Length - 1 || !source.Contains('*')) { return null; }
        return source.Replace("*", destination.Substring(star, destination.Length - target.Length + 1), StringComparison.Ordinal);
    }

    private static bool ReferencePatternMatches(string pattern, string reference)
    {
        int star = pattern.IndexOf('*');
        return star < 0 ? pattern == reference : reference.Length >= pattern.Length - 1
            && reference.StartsWith(pattern[..star], StringComparison.Ordinal) && reference.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal);
    }
}
