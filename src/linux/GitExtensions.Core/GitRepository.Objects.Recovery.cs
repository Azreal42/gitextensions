using System.Globalization;
using System.ComponentModel;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Deletes old recovery tags, then creates lightweight tags on the selected exact objects in display order.</summary>
    public Task<GitObjectRecoveryResult> RecoverObjectsAsync(IEnumerable<string> selectedOids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedOids);
        string[] selected = selectedOids.Select(ValidateObjectId).ToArray();
        if (selected.Length == 0) { throw new ArgumentException("Select objects to restore.", nameof(selectedOids)); }
        return WithMutationAsync(token => RecoverObjectsCoreAsync(selected, token), cancellationToken);
    }

    public Task<GitObjectRecoveryResult> DeleteLostFoundTagsAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(token => RecoverObjectsCoreAsync(null, token), cancellationToken);

    public Task<GitObjectRecoveryReport> SaveLostFoundAsync(GitObjectRecoveryOptions? options = null, CancellationToken cancellationToken = default)
        => WithMutationAsync(token => ScanLostObjectsAsync(options ?? new(), saveLostFound: true, token), cancellationToken);

    /// <summary>Runs the original plain Git prune. Confirmation belongs to the host; no expiry/config override is invented.</summary>
    public Task<GitResult> PruneLostObjectsAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await GetInfoAsync(token).ConfigureAwait(false);
            return await ExecuteAsync(["prune"], token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<GitObjectRecoveryResult> RecoverObjectsCoreAsync(string[]? selected, CancellationToken token)
    {
        await GetInfoAsync(token).ConfigureAwait(false);
        Dictionary<string, string> before = await RecoveryTagsAsync(token).ConfigureAwait(false);
        List<GitObjectRecoveryError> errors = [];
        Dictionary<string, string> requested = new(StringComparer.Ordinal);
        HashSet<string> deletedConfirmed = new(StringComparer.Ordinal), createdConfirmed = new(StringComparer.Ordinal);
        bool cancelled = false;
        try
        {
            foreach ((string name, _) in before)
            {
                token.ThrowIfCancellationRequested();
                GitResult deleted = await _runner.RunAsync(Path, [.. GlobalOptions, "tag", "--delete", "--", name], token).ConfigureAwait(false);
                if (!deleted.Success) { errors.Add(new(null, "Delete " + name, deleted.StandardError)); }
                else { deletedConfirmed.Add(name); }
            }
            if (selected is not null)
            {
                for (int index = 0; index < selected.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string oid = selected[index];
                    try
                    {
                        GitObjectData data = await ReadObjectAsync(oid, token).ConfigureAwait(false);
                        string name = LostFoundTagPrefix + (index + 1).ToString(CultureInfo.InvariantCulture);
                        if (data.Type == GitLostObjectType.Tag)
                        {
                            GitLostObject metadata = FillLostObject(new(oid, data.Type, data.RawType, ""), data);
                            if (string.IsNullOrEmpty(metadata.TagName)) { throw new InvalidDataException("The annotated tag has no readable tag name: " + oid); }
                            name = LostFoundTagPrefix + metadata.TagName;
                        }
                        GitResult valid = await _runner.RunAsync(Path, [.. GlobalOptions, "check-ref-format", "refs/tags/" + name], token).ConfigureAwait(false);
                        if (!valid.Success) { errors.Add(new(oid, "Create " + name, "The recovery tag name is not a valid Git reference.")); continue; }
                        // Explicit lightweight semantics even with tag.gpgSign=true. The object is never resolved through ^{commit}.
                        requested.TryAdd(name, oid);
                        GitResult created = await _runner.RunAsync(Path, [.. GlobalOptions, "tag", "--no-sign", "--", name, oid], token).ConfigureAwait(false);
                        if (!created.Success) { errors.Add(new(oid, "Create " + name, created.StandardError)); }
                        else { requested[name] = oid; createdConfirmed.Add(name); }
                    }
                    catch (Exception error) when (IsObjectReadError(error)) { errors.Add(new(oid, "Recover", error.Message)); }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        { errors.Add(new(null, "Recover batch", error.Message)); }

        // A cancelled process may already have committed a ref. Observe the actual outcome instead of counting attempts.
        using CancellationTokenSource observation = new(TimeSpan.FromSeconds(5));
        Dictionary<string, string> after;
        try { after = await RecoveryTagsAsync(observation.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is GitCommandException or OperationCanceledException or IOException or Win32Exception or UnauthorizedAccessException)
        {
            errors.Add(new(null, "Observe effects", error.Message));
            return new([], deletedConfirmed.ToArray(), errors, cancelled) { EffectsVerified = false };
        }
        List<string> removed = before.Keys.Where(name => deletedConfirmed.Contains(name) || !after.ContainsKey(name) || after[name] != before[name]).ToList();
        foreach ((string name, string oid) in requested)
        {
            if (after.TryGetValue(name, out string? observed) && observed != oid)
            { errors.Add(new(oid, "Observe " + name, "The recovery ref changed to a different object: " + observed)); }
            else if (!after.ContainsKey(name)) { errors.Add(new(oid, "Observe " + name, "The created recovery ref is no longer present.")); }
        }
        List<GitRecoveredObjectTag> createdTags = requested.Keys.Where(name => after.TryGetValue(name, out string? observed) && observed == requested[name]
            && (createdConfirmed.Contains(name) || deletedConfirmed.Contains(name) || !before.ContainsKey(name)))
            .Select(name => new GitRecoveredObjectTag(name, after[name])).ToList();
        return new(createdTags, removed, errors, cancelled)
        {
            DivergentTags = requested.Where(pair => after.TryGetValue(pair.Key, out string? oid) && oid != pair.Value
                && (!before.TryGetValue(pair.Key, out string? original) || original != oid))
                .Select(pair => new GitRecoveredObjectTag(pair.Key, after[pair.Key])).ToArray(),
        };
    }

    private async Task<Dictionary<string, string>> RecoveryTagsAsync(CancellationToken token)
    {
        GitResult refs = await ExecuteAsync(["for-each-ref", "--format=%(refname)%00%(objectname)", "refs/tags/"], token).ConfigureAwait(false);
        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        foreach (string line in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\0');
            string name = fields[0]["refs/tags/".Length..];
            if (name.StartsWith(LostFoundTagPrefix, StringComparison.Ordinal)) { tags.Add(name, fields[1]); }
        }
        return tags;
    }
}
