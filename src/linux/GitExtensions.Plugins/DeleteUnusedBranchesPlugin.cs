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
        if (operation == "delete") PluginValues.RequireConfirmation(context);
        string target = PluginValues.Text(Descriptor, values, "mergedInto");
        string targetHash = await context.Repository.ResolveRevisionAsync(target, cancellationToken).ConfigureAwait(false);
        bool remoteOnly = PluginValues.Boolean(Descriptor, values, "remoteBranches");
        bool includeUnmerged = PluginValues.Boolean(Descriptor, values, "includeUnmerged");
        string remote = PluginValues.Text(Descriptor, values, "remote");
        int days = PluginValues.Integer(Descriptor, values, "olderThanDays");
        string pattern = PluginValues.Text(Descriptor, values, "pattern");
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
        List<Candidate> candidates = [];
        foreach (GitReference reference in references.Where(item => item.IsBranch || item.IsRemote))
        {
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
        List<PluginRow> rows = candidates.Select(item => item.Row()).ToList();
        string rule = $"Original rule: {(includeUnmerged ? "unmerged branches included" : "only branches merged into " + target)}, current/reference and symbolic branches excluded; {(remoteOnly ? "remote " + remote + " only" : "local branches only")}. Older than {days} days suggests selection; newer eligible branches may be selected explicitly.";
        if (operation == "preview") return new(rule + "\nPreview only; no branches were changed.", Table(rows));

        IReadOnlyList<string> requested = PluginValues.Lines(PluginValues.Text(Descriptor, values, "branches"));
        if (requested.Count == 0) throw new ArgumentException("Select explicit branch names from the preview before deleting.", "branches");
        List<Candidate> selected = [];
        foreach (string name in requested)
        {
            Candidate[] matches = candidates.Where(item => item.Reference.Name == name || item.Reference.ShortName == name).ToArray();
            Candidate? candidate = matches.FirstOrDefault(item => item.Reason is null);
            if (candidate is null)
                throw new ArgumentException($"Cannot delete '{name}': {matches.FirstOrDefault()?.Reason ?? "not in the preview"}", "branches");
            if (!selected.Contains(candidate)) selected.Add(candidate);
        }
        // Validate the entire selection before the first mutation, rather than partly applying an invalid request.
        int deleted = 0, failures = 0;
        foreach (Candidate candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitReference reference = candidate.Reference;
            try
            {
                string remoteRef = reference.IsRemote ? "refs/heads/" + reference.Name[("refs/remotes/" + remote + "/").Length..] : "";
                string[] args = reference.IsRemote
                    ? ["push", "--force-with-lease=" + remoteRef + ":" + reference.Hash, "--", remote, ":" + remoteRef]
                    : ["branch", includeUnmerged ? "-D" : "-d", "--", reference.ShortName];
                await context.Repository.RunAsync(args, cancellationToken).ConfigureAwait(false);
                deleted++;
                rows[candidates.IndexOf(candidate)] = candidate.Row("Deleted", "Branch deleted.");
            }
            catch (GitCommandException exception)
            {
                failures++;
                rows[candidates.IndexOf(candidate)] = candidate.Row("Failed", exception.Result.StandardError.Trim());
            }
        }
        return new(rule + $"\n{deleted} branch(es) deleted; {failures} failed.", Table(rows), RepositoryChanged: deleted > 0, Success: failures == 0);
    }

    private static PluginTable Table(IReadOnlyList<PluginRow> rows)
        => new(["Branch", "Scope", "Tip date", "Author", "Subject", "Status", "Reason", "Tip"], rows);

    private sealed record Candidate(GitReference Reference, DateTimeOffset Date, string Author, string Subject, string? Reason, bool Suggested)
    {
        internal PluginRow Row(string? status = null, string? reason = null)
            => new(Reference.Name, [Reference.ShortName, Reference.IsRemote ? "Remote" : "Local", Date.ToString("O", CultureInfo.InvariantCulture),
                Author, Subject, status ?? (Reason is not null ? "Excluded" : Suggested ? "Suggested" : "Eligible"),
                reason ?? Reason ?? (Suggested ? "Older than the configured age." : "Eligible; not preselected by age."), Reference.Hash]);
    }
}
