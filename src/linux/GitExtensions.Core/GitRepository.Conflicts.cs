namespace GitExtensions.Linux;

public sealed record GitConflictStage(int Stage, string Mode, string Hash);
public sealed record GitConflictPath(string Path, IReadOnlyList<GitConflictStage> Stages);
public enum GitConflictResolution { Base, Ours, Theirs, Delete, Manual }

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<GitConflictPath>> GetConflictsAsync(CancellationToken cancellationToken = default)
    {
        GitResult result = await ExecuteAsync(["ls-files", "--unmerged", "-z"], cancellationToken).ConfigureAwait(false);
        Dictionary<string, List<GitConflictStage>> paths = new(StringComparer.Ordinal);
        foreach (string entry in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = entry.IndexOf('\t');
            string[] fields = entry[..tab].Split(' ');
            string path = entry[(tab + 1)..];
            if (!paths.TryGetValue(path, out List<GitConflictStage>? stages)) { paths[path] = stages = []; }
            stages.Add(new GitConflictStage(int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture), fields[0], fields[1]));
        }
        return paths.Select(pair => new GitConflictPath(pair.Key, pair.Value)).ToArray();
    }

    public Task<GitResult> ResolveConflictAsync(string path, GitConflictResolution resolution, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            if (!Enum.IsDefined(resolution)) { throw new ArgumentOutOfRangeException(nameof(resolution)); }
            path = ValidatePath(path);
            GitConflictPath conflict = (await GetConflictsAsync(token).ConfigureAwait(false)).SingleOrDefault(item => item.Path == path)
                ?? throw new InvalidOperationException("The selected path is no longer conflicted.");
            if (resolution == GitConflictResolution.Delete)
            { return await ExecuteAsync(["rm", "--force", "--", path], token).ConfigureAwait(false); }
            if (conflict.Stages.Any(stage => stage.Mode == "160000"))
            { throw new NotSupportedException("Use the submodule conflict assistant to choose, confirm and stage a real child commit. Use Delete explicitly to resolve by deleting this gitlink."); }
            if (resolution != GitConflictResolution.Manual)
            {
                int stage = resolution switch { GitConflictResolution.Base => 1, GitConflictResolution.Ours => 2, _ => 3 };
                GitConflictStage source = conflict.Stages.SingleOrDefault(item => item.Stage == stage)
                    ?? throw new InvalidOperationException("This version is a deletion. Use Delete explicitly to resolve by deleting the file.");
                // checkout-index consumes an exact index pathname, rather than a revision:path expression.
                await ExecuteAsync(["checkout-index", "--force", "--stage=" + stage, "--", path], token).ConfigureAwait(false);
            }
            return await ExecuteAsync(["add", "--", path], token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitResult> RunMergeToolAsync(string path, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            path = ValidatePath(path);
            GitConflictPath conflict = (await GetConflictsAsync(token).ConfigureAwait(false)).SingleOrDefault(item => item.Path == path)
                ?? throw new InvalidOperationException("The selected path is no longer conflicted.");
            if (conflict.Stages.Any(stage => stage.Mode == "160000"))
            { throw new NotSupportedException("Use the submodule conflict assistant to resolve a gitlink; a file merge tool cannot choose its child commit safely."); }
            // The native shell helper consumes line-delimited diff output. Decline
            // quoted/control-character names before it can address a different file.
            GitResult names = await ExecuteAsync(["diff", "--name-only", "--diff-filter=U", "--", path], token).ConfigureAwait(false);
            if (names.StandardOutput != path + "\n")
            { throw new NotSupportedException("Git's merge-tool helper cannot address this pathname. Use the stage choices or edit the file and mark it resolved."); }
            Version version = await GetToolConfigurationGitVersionAsync(token).ConfigureAwait(false);
            // Windows uses the GUI selector only where Git supports it; older Git keeps merge.tool.
            return await ExecuteAsync(["mergetool", .. (version >= new Version(2, 20) ? new[] { "--gui" } : []), "--no-prompt", "--", path], token,
                environment: new Dictionary<string, string?> { ["GIT_LITERAL_PATHSPECS"] = "1" }).ConfigureAwait(false);
        }, cancellationToken);
}
