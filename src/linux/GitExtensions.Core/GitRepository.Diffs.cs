using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux;

public sealed record GitRevisionFile(string Path, char Status, string? OriginalPath = null)
{
    public bool IsDeleted => Status == 'D';
    public int? SimilarityPercent { get; init; }
    public DiffBranchStatus DiffStatus { get; init; } = DiffBranchStatus.Unknown;
}

public sealed record GitRevisionChanges(string Revision, string? BaseRevision, IReadOnlyList<GitRevisionFile> Files);

public sealed partial class GitRepository
{
    public async Task<string> GetCommitDiffAsync(string revision, GitComparisonDiffOptions options,
        string? path = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateComparisonOptions(options);
        List<string> arguments = ["show", "--encoding=UTF-8", "--format=fuller", "--no-ext-diff", "--no-textconv"];
        AddComparisonOptions(arguments, options);
        arguments.AddRange([await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false), "--"]);
        if (path is not null) { arguments.Add(ValidatePath(path)); }
        return (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    public async Task<GitRevisionChanges> GetRevisionChangesAsync(string revision, string? baseRevision = null, CancellationToken cancellationToken = default)
    {
        string target = await ResolveCachedReadRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        string? parent = baseRevision is null
            ? (await RunCachedAsync(GitCachedReadKind.Graph, ["rev-list", "--parents", "-n", "1", target], cancellationToken).ConfigureAwait(false))
                .StandardOutput.TrimEnd('\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()
            : await ResolveCachedReadRevisionAsync(baseRevision, cancellationToken).ConfigureAwait(false);
        string[] arguments = parent is null
            ? ["diff-tree", "--root", "-r", "--no-commit-id", "--name-status", "-z", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", target, "--"]
            : ["diff", "--name-status", "-z", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", parent, target, "--"];
        IReadOnlyList<GitRevisionFile> files = GitRevisionFileParser.ParseNameStatus(
            (await RunCachedAsync(GitCachedReadKind.Diff, arguments, cancellationToken).ConfigureAwait(false)).StandardOutput);
        return new(target, parent, files);
    }

    public Task<string> GetRevisionFileDiffAsync(GitRevisionChanges revisions, GitRevisionFile file, CancellationToken cancellationToken = default)
        => GetRevisionFileDiffAsync(revisions, file, new GitComparisonDiffOptions(), cancellationToken);

    public async Task<string> GetRevisionFileDiffAsync(GitRevisionChanges revisions, GitRevisionFile file,
        GitComparisonDiffOptions options, CancellationToken cancellationToken = default)
        => await ExecuteDiffReadAsync(await BuildRevisionFileDiffArgumentsAsync(revisions, file, options, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<GitDiffReadCommand> BuildRevisionFileDiffArgumentsAsync(GitRevisionChanges revisions, GitRevisionFile file,
        GitComparisonDiffOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        ValidateComparisonOptions(options);
        string target = await ResolveCachedReadRevisionAsync(revisions.Revision, cancellationToken).ConfigureAwait(false);
        string? parent = revisions.BaseRevision is null ? null : await ResolveCachedReadRevisionAsync(revisions.BaseRevision, cancellationToken).ConfigureAwait(false);
        string[] paths = file.OriginalPath is null ? [ValidatePath(file.Path)] : [ValidatePath(file.OriginalPath), ValidatePath(file.Path)];
        List<string> arguments = parent is null
            ? ["diff-tree", "--root", "-r", "--no-commit-id", "--patch", "--no-ext-diff", "--no-textconv", "--no-color"]
            : ["diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--no-color"];
        AddComparisonOptions(arguments, options);
        if (parent is not null) { arguments.Add(parent); }
        arguments.AddRange([target, "--", .. paths]);
        // diff-tree also receives literal paths, unlike its root-only name listing above.
        return new(["--literal-pathspecs", .. arguments], CacheKind: GitCachedReadKind.Diff);
    }

    public async Task<byte[]> GetIndexFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        string[] arguments = ["cat-file", "blob", ":0:" + ValidatePath(path)];
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], cancellationToken).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, new GitResult(result.ExitCode, "", result.StandardError)); }
        return result.StandardOutput;
    }
}
