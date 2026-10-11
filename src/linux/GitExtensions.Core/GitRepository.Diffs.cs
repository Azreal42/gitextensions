namespace GitExtensions.Linux;

public sealed record GitRevisionFile(string Path, char Status, string? OriginalPath = null)
{
    public bool IsDeleted => Status == 'D';
}

public sealed record GitRevisionChanges(string Revision, string? BaseRevision, IReadOnlyList<GitRevisionFile> Files);

public sealed partial class GitRepository
{
    public async Task<GitRevisionChanges> GetRevisionChangesAsync(string revision, string? baseRevision = null, CancellationToken cancellationToken = default)
    {
        string target = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        string? parent = baseRevision is null
            ? (await ExecuteAsync(["rev-list", "--parents", "-n", "1", target], cancellationToken).ConfigureAwait(false))
                .StandardOutput.TrimEnd('\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()
            : await ResolveRevisionAsync(baseRevision, cancellationToken).ConfigureAwait(false);
        string[] arguments = parent is null
            ? ["diff-tree", "--root", "-r", "--no-commit-id", "--name-status", "-z", "--find-renames", "--no-ext-diff", "--no-textconv", target, "--"]
            : ["diff", "--name-status", "-z", "--find-renames", "--no-ext-diff", "--no-textconv", parent, target, "--"];
        string[] fields = (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\0');
        List<GitRevisionFile> files = [];
        for (int i = 0; i < fields.Length && fields[i].Length > 0;)
        {
            char status = fields[i++][0];
            if (i >= fields.Length) { throw new FormatException("Incomplete Git file-change record."); }
            string path = fields[i++];
            if (status is 'R' or 'C')
            {
                if (i >= fields.Length) { throw new FormatException("Incomplete Git rename record."); }
                files.Add(new(fields[i++], status, path));
            }
            else { files.Add(new(path, status)); }
        }
        return new(target, parent, files);
    }

    public async Task<string> GetRevisionFileDiffAsync(GitRevisionChanges revisions, GitRevisionFile file, CancellationToken cancellationToken = default)
    {
        string target = await ResolveRevisionAsync(revisions.Revision, cancellationToken).ConfigureAwait(false);
        string? parent = revisions.BaseRevision is null ? null : await ResolveRevisionAsync(revisions.BaseRevision, cancellationToken).ConfigureAwait(false);
        string[] paths = file.OriginalPath is null ? [ValidatePath(file.Path)] : [ValidatePath(file.OriginalPath), ValidatePath(file.Path)];
        string[] arguments = parent is null
            ? ["diff-tree", "--root", "-r", "--no-commit-id", "--patch", "--no-ext-diff", "--no-textconv", target, "--", .. paths]
            : ["diff", "--find-renames", "--no-ext-diff", "--no-textconv", parent, target, "--", .. paths];
        // diff-tree also receives literal paths, unlike its root-only name listing above.
        return (await ExecuteAsync(["--literal-pathspecs", .. arguments], cancellationToken).ConfigureAwait(false)).StandardOutput;
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
