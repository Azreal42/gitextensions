using System.Globalization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Plugins;

/// <summary>Preserves the original merged, current/reference, remote, regex and age-selection rules.</summary>
public sealed class DeleteUnusedBranchesPlugin : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("DC3CA904-B9A5-4FE8-BF63-5B8EE9C2DDAC"),
        "Delete obsolete branches", "Preview branch eligibility before selecting branches to delete. Age suggests selections; it does not forbid selecting a newer eligible branch.",
        "IconDeleteUnusedBranches",
        [new("operation", "Operation", PluginParameterKind.Choice, "preview", Choices: ["preview", "delete"]),
         new("mergedInto", "Merged into", PluginParameterKind.Text, "HEAD", Required: true),
         new("olderThanDays", "Suggest branches older than (days)", PluginParameterKind.Integer, "30"),
         new("remoteBranches", "Remote branches only", PluginParameterKind.Boolean, "false"),
         new("remote", "Remote", PluginParameterKind.Text, "origin", Required: true),
         new("includeUnmerged", "Include unmerged branches", PluginParameterKind.Boolean, "false", Hint: "Deleting these branches forces local deletion."),
         new("pattern", "Branch regex filter", PluginParameterKind.Text, Hint: "Leave empty for no filter."),
         new("ignoreCase", "Ignore regex case", PluginParameterKind.Boolean, "false"),
         new("invertPattern", "Invert regex filter", PluginParameterKind.Boolean, "false"),
         new("branches", "Branches to delete", PluginParameterKind.Multiline, Hint: "Explicit selection, one name or full reference per line. Copy names from the preview.")],
        RequiresConfirmation: true, ConfirmationText: "Delete the selected branches? Remote deletion changes the remote repository; including unmerged branches permits forced local deletion.",
        ConfirmationParameter: "operation", ConfirmationValue: "delete");

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        string operation = PluginValues.Text(Descriptor, values, "operation");
        if (operation == "preview")
        {
            DeleteUnusedBranchesPreview preview = await PreviewAsync(context, values, cancellationToken).ConfigureAwait(false);
            return preview.ToPluginResult();
        }
        return (await DeleteAsync(context, values, cancellationToken: cancellationToken).ConfigureAwait(false)).ToPluginResult();
    }

    /// <summary>The same eligibility and age rules used by both the native dialog and the generic plugin API.</summary>
    public async Task<DeleteUnusedBranchesPreview> PreviewAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
    {
        string target = PluginValues.Text(Descriptor, values, "mergedInto");
        string targetHash = await context.Repository.ResolveRevisionAsync(target, cancellationToken).ConfigureAwait(false);
        bool remoteOnly = PluginValues.Boolean(Descriptor, values, "remoteBranches");
        bool includeUnmerged = PluginValues.Boolean(Descriptor, values, "includeUnmerged");
        string remote = PluginValues.Text(Descriptor, values, "remote");
        // This plugin's Windows NumericUpDown accepts 0..100000; other plugins keep their own limits.
        int days = int.TryParse(PluginValues.Text(Descriptor, values, "olderThanDays"), NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsed) && parsed <= 100000 ? parsed
            : throw new ArgumentException("olderThanDays must be an integer between 0 and 100000.", "olderThanDays");
        string pattern = PluginValues.Text(Descriptor, values, "pattern");
        // Absent means the historical generic contract: a nonempty pattern is active.
        // An explicitly disabled checkbox retains its text without applying it.
        if (values.TryGetValue("useRegex", out string? enabled))
        {
            if (!bool.TryParse(enabled, out bool useRegex)) { throw new ArgumentException("useRegex must be true or false.", "useRegex"); }
            if (!useRegex) { pattern = ""; }
        }
        bool inverted = PluginValues.Boolean(Descriptor, values, "invertPattern");
        Regex? filter = pattern.Length == 0 ? null : new(pattern,
            PluginValues.Boolean(Descriptor, values, "ignoreCase") ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(1));
        if (remoteOnly && !(await context.Repository.GetRemotesAsync(cancellationToken).ConfigureAwait(false)).Any(item => item.Name == remote))
            throw new ArgumentException($"Remote '{remote}' does not exist.", "remote");

        IReadOnlyList<GitReference> references = await context.Repository.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> merged = (await context.Repository.RunAsync(["for-each-ref", "--merged=" + targetHash, "--format=%(refname)", "refs/heads", "refs/remotes"], cancellationToken).ConfigureAwait(false))
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        string metadata = (await context.Repository.RunAsync(["for-each-ref", "--format=%(refname)%00%(committerdate:iso-strict)%00%(authorname)%00%(subject)", "refs/heads", "refs/remotes"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        Dictionary<string, string[]> details = metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\0', 4))
            .ToDictionary(fields => fields[0], StringComparer.Ordinal);
        DateTimeOffset cutoff = (context.Now ?? DateTimeOffset.UtcNow).AddDays(-days);
        List<DeleteUnusedBranchCandidate> candidates = [];
        foreach (GitReference reference in references.Where(item => item.IsBranch || item.IsRemote))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] fields = details[reference.Name];
            DateTimeOffset date = DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture);
            string? reason = reference.IsCurrent ? "Current branch."
                : reference.Name == target || reference.ShortName == target ? "Reference branch."
                : reference.SymbolicTarget is not null ? "Symbolic reference."
                : remoteOnly && !reference.IsRemote ? "Local branch excluded in remote mode."
                : !remoteOnly && reference.IsRemote ? "Remote branch excluded in local mode."
                : remoteOnly && !reference.Name.StartsWith("refs/remotes/" + remote + "/", StringComparison.Ordinal) ? "Different remote."
                : !includeUnmerged && !merged.Contains(reference.Name) ? "Not merged into the reference."
                : filter is not null && filter.IsMatch(reference.ShortName) == inverted ? "Regex filter excludes this branch."
                : null;
            candidates.Add(new(reference, date, fields[2], fields[3], reason, date < cutoff));
        }
        string rule = $"Original rule: {(includeUnmerged ? "unmerged branches included" : "only branches merged into " + target)}, current/reference and symbolic branches excluded; {(remoteOnly ? "remote " + remote + " only" : "local branches only")}. Older than {days} days suggests selection; newer eligible branches may be selected explicitly.";
        return new(rule, candidates, remote, includeUnmerged);
    }

    /// <summary>Revalidates every selected reference before the first mutation, then retains every native outcome.</summary>
    public async Task<DeleteUnusedBranchesDeletionResult> DeleteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        DeleteUnusedBranchesPreview? reviewedPreview = null, CancellationToken cancellationToken = default)
    {
        PluginValues.RequireConfirmation(context);
        DeleteUnusedBranchesPreview preview = await PreviewAsync(context, values, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> requested = PluginValues.Lines(PluginValues.Text(Descriptor, values, "branches"));
        if (requested.Count == 0) throw new ArgumentException("Select explicit branch names from the preview before deleting.", "branches");
        List<DeleteUnusedBranchCandidate> selected = [];
        foreach (string name in requested)
        {
            DeleteUnusedBranchCandidate[] matches = preview.Candidates.Where(item => item.Reference.Name == name || item.Reference.ShortName == name).ToArray();
            DeleteUnusedBranchCandidate? candidate = matches.FirstOrDefault(item => item.Eligible);
            if (candidate is null)
                throw new ArgumentException($"Cannot delete '{name}': {matches.FirstOrDefault()?.Reason ?? "not in the preview"}", "branches");
            if (reviewedPreview is not null && !reviewedPreview.Candidates.Any(item => item.Eligible
                && item.Reference.Name == candidate.Reference.Name && item.Reference.Hash == candidate.Reference.Hash))
                throw new InvalidOperationException($"Reference '{candidate.Reference.ShortName}' changed since the preview. Search branches again before deleting.");
            if (!selected.Contains(candidate)) selected.Add(candidate);
        }

        List<DeleteUnusedBranchOutcome> outcomes = [];
        bool cancelled = false;
        for (int index = 0; index < selected.Count; index++)
        {
            DeleteUnusedBranchCandidate candidate = selected[index];
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                for (; index < selected.Count; index++) outcomes.Add(new(selected[index], DeleteUnusedBranchStatus.NotAttempted, "Operation cancelled before this branch was attempted."));
                break;
            }
            GitReference reference = candidate.Reference;
            try
            {
                string remoteRef = reference.IsRemote ? "refs/heads/" + reference.Name[("refs/remotes/" + preview.Remote + "/").Length..] : "";
                string[] args = reference.IsRemote
                    ? ["push", "--force-with-lease=" + remoteRef + ":" + reference.Hash, "--", preview.Remote, ":" + remoteRef]
                    : ["branch", preview.IncludeUnmerged ? "-D" : "-d", "--", reference.ShortName];
                await context.Repository.RunAsync(args, cancellationToken).ConfigureAwait(false);
                outcomes.Add(new(candidate, DeleteUnusedBranchStatus.Deleted, "Branch deleted."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                outcomes.Add(new(candidate, DeleteUnusedBranchStatus.Interrupted, "Native Git was interrupted; the branch outcome is unknown. Refresh the references before retrying."));
            }
            catch (GitCommandException exception)
            {
                outcomes.Add(new(candidate, DeleteUnusedBranchStatus.Failed,
                    string.IsNullOrWhiteSpace(exception.Result.StandardError) ? exception.Message : exception.Result.StandardError.Trim()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                outcomes.Add(new(candidate, DeleteUnusedBranchStatus.Failed, exception.Message));
            }
        }
        return new(preview, outcomes, cancelled);
    }
}
