namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<GitCombinedDiff> GetCombinedDiffAsync(string revision, IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
    {
        string target = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> parents = await GetActualDiffParentsAsync(target, cancellationToken).ConfigureAwait(false);
        string[] literalPaths = paths is null || paths.Count == 0 ? [] : ValidatePaths(paths);
        if (parents.Count < 2) { return new(target, parents, []); }
        // Match GitModule.GetCombinedDiffFileList: --cc includes only paths changed against every parent.
        string output = (await ExecuteAsync(["--literal-pathspecs", "diff-tree", "--name-only", "-z", "--cc", "--no-commit-id",
            "--no-ext-diff", "--no-textconv", target, "--", .. literalPaths], cancellationToken).ConfigureAwait(false)).StandardOutput;
        if (output.Length > 0 && output[^1] != '\0') { throw new FormatException("Incomplete NUL-terminated combined-diff file list."); }
        return new(target, parents, output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => new GitRevisionFile(path, 'M')).ToArray());
    }

    public async Task<string> GetCombinedFileDiffAsync(GitCombinedDiff diff, GitRevisionFile file,
        GitCombinedDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(file);
        options ??= new();
        GitComparisonDiffOptions display = options.DiffOptions ?? new();
        ValidateComparisonOptions(display);
        string target = await ResolveRevisionAsync(diff.Revision, cancellationToken).ConfigureAwait(false);
        if ((await GetActualDiffParentsAsync(target, cancellationToken).ConfigureAwait(false)).Count < 2)
        { throw new ArgumentException("Combined diff requires a merge commit.", nameof(diff)); }
        string path = ValidatePath(file.Path);
        List<string> arguments = ["--literal-pathspecs", "diff-tree"];
        arguments.AddRange(options.OmitUninterestingDiff ? ["--cc"] : ["-c", "-p"]);
        arguments.AddRange(["--no-commit-id", "--no-ext-diff", "--no-textconv", options.UseGitColoring ? "--color=always" : "--no-color"]);
        if (options.UseHistogram) { arguments.Add("--histogram"); }
        AddComparisonOptions(arguments, display);
        arguments.AddRange([target, "--", path]);
        return (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    /// <summary>Upstream FileStatusDiffCalculator groups in caller order: B,A; B,BASE,A; or B,baseB,A,baseA.</summary>
    public async Task<IReadOnlyList<GitRevisionDiffGroup>> GetRevisionDiffGroupsAsync(IReadOnlyList<string> revisions,
        bool showDiffForAllParents = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        cancellationToken.ThrowIfCancellationRequested();
        if (revisions.Count == 0) { return []; }
        List<GitRevisionDiffGroup> groups = [];
        string target = await ResolveRevisionAsync(revisions[0], cancellationToken).ConfigureAwait(false);
        if (revisions.Count == 1)
        {
            IReadOnlyList<string> parents = await GetActualDiffParentsAsync(target, cancellationToken).ConfigureAwait(false);
            IEnumerable<string?> selectedParents = parents.Count == 0 ? [null] : parents.Take(showDiffForAllParents ? parents.Count : 1).Cast<string?>();
            foreach (string? parent in selectedParents)
            {
                GitRevisionChanges changes = await GetRevisionChangesAsync(target, parent, cancellationToken).ConfigureAwait(false);
                groups.Add(new(GitRevisionDiffGroupKind.Parent, target, changes.BaseRevision, changes.Files));
            }
            if (showDiffForAllParents && parents.Count > 1)
            {
                GitCombinedDiff combined = await GetCombinedDiffAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (combined.Files.Count > 0) { groups.Add(new(GitRevisionDiffGroupKind.Combined, target, null, combined.Files, CombinedDiff: combined)); }
            }
            return groups;
        }

        string first = await ResolveRevisionAsync(revisions[showDiffForAllParents && revisions.Count == 4 ? 2 : revisions.Count - 1], cancellationToken).ConfigureAwait(false);
        GitRevisionChanges comparison = await GetRevisionChangesAsync(target, first, cancellationToken).ConfigureAwait(false);
        groups.Add(new(GitRevisionDiffGroupKind.Comparison, target, first, comparison.Files));
        if (!showDiffForAllParents || revisions.Count > 4) { return groups; }
        GitRangeDiffComparison? range = await GetRangeDiffComparisonAsync(revisions, cancellationToken).ConfigureAwait(false);
        if (range is null)
        {
            foreach (string selected in revisions.Skip(1))
            {
                string revision = await ResolveRevisionAsync(selected, cancellationToken).ConfigureAwait(false);
                if (revision == first || revision == target) { continue; }
                GitRevisionChanges additional = await GetRevisionChangesAsync(target, revision, cancellationToken).ConfigureAwait(false);
                groups.Add(new(GitRevisionDiffGroupKind.Comparison, target, revision, additional.Files));
            }
            return groups;
        }

        if (await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false) >= new Version(2, 19))
        { groups.Add(new(GitRevisionDiffGroupKind.RangeDiff, target, first, [], RangeDiff: range)); }
        GitRevisionChanges baseToSecond = await GetRevisionChangesAsync(target, range.CommonAncestor, cancellationToken).ConfigureAwait(false);
        GitRevisionChanges baseToFirst = await GetRevisionChangesAsync(first, range.CommonAncestor, cancellationToken).ConfigureAwait(false);
        groups.Add(new(GitRevisionDiffGroupKind.BaseToSecond, target, range.CommonAncestor, baseToSecond.Files));
        groups.Add(new(GitRevisionDiffGroupKind.BaseToFirst, first, range.CommonAncestor, baseToFirst.Files));
        return groups;
    }

    private async Task<IReadOnlyList<string>> GetActualDiffParentsAsync(string revision, CancellationToken cancellationToken)
        => (await ExecuteAsync(["rev-list", "--parents", "-n", "1", revision, "--"], cancellationToken).ConfigureAwait(false))
            .StandardOutput.TrimEnd('\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
}
