namespace GitExtensions.Linux.Plugins;

/// <summary>Ports CreateLocalBranchesForm's tracking-branch operation without its WinForms dialog.</summary>
public sealed class CreateLocalBranchesPlugin : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("BE7BEE10-21B5-489F-9664-957945C203DC"),
        "Create local tracking branches", "Create local tracking branches for the selected remote without checking them out.",
        "IconCreateLocalBranches",
        [new("remote", "Remote", PluginParameterKind.Text, "origin", Required: true),
         new("branches", "Remote branches", PluginParameterKind.Multiline, Hint: "One remote branch per line; leave empty to create all. Existing local branches are preserved.")]);

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        string remote = PluginValues.Text(Descriptor, values, "remote");
        if (!(await context.Repository.GetRemotesAsync(cancellationToken).ConfigureAwait(false)).Any(item => item.Name == remote))
        {
            throw new ArgumentException($"Remote '{remote}' does not exist.", "remote");
        }
        IReadOnlyList<GitReference> references = await context.Repository.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        string prefix = "refs/remotes/" + remote + "/";
        GitReference[] candidates = references.Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal) && item.SymbolicTarget is null).ToArray();
        IReadOnlyList<string> requested = PluginValues.Lines(PluginValues.Text(Descriptor, values, "branches"));
        if (requested.Count > 0)
        {
            List<GitReference> selected = [];
            foreach (string name in requested)
            {
                GitReference? found = candidates.FirstOrDefault(item => item.Name == name || item.ShortName == name || item.Name[prefix.Length..] == name);
                if (found is null)
                {
                    throw new ArgumentException($"'{name}' is not a branch from remote '{remote}'.", "branches");
                }
                if (!selected.Contains(found)) selected.Add(found);
            }
            candidates = selected.ToArray();
        }
        HashSet<string> existing = references.Where(item => item.IsBranch).Select(item => item.ShortName).ToHashSet(StringComparer.Ordinal);
        List<PluginRow> rows = [];
        int created = 0, failures = 0;
        foreach (GitReference reference in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string branch = reference.Name[prefix.Length..];
            if (existing.Contains(branch))
            {
                rows.Add(new(reference.Name, [branch, reference.ShortName, "Existing", "Local branch preserved."]));
                continue;
            }
            try
            {
                await context.Repository.RunAsync(["branch", "--track", "--", branch, reference.Name], cancellationToken).ConfigureAwait(false);
                existing.Add(branch);
                created++;
                rows.Add(new(reference.Name, [branch, reference.ShortName, "Created", "Tracking branch created."]));
            }
            catch (GitCommandException exception)
            {
                failures++;
                rows.Add(new(reference.Name, [branch, reference.ShortName, "Failed", exception.Result.StandardError.Trim()]));
            }
        }
        return new($"{created} tracking branch(es) created; {failures} failed.",
            new(["Branch", "Remote branch", "Status", "Message"], rows), RepositoryChanged: created > 0, Success: failures == 0);
    }
}
