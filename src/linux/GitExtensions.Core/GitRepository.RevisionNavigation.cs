namespace GitExtensions.Linux;

public enum GitGoToCommitReferenceKind { LocalBranch, Tag }

public sealed partial class GitRepository
{
    /// <summary>Independent, read-only and bounded references for Windows FormGoToCommit's two lists.</summary>
    public async Task<IReadOnlyList<GitReference>> GetGoToCommitReferencesAsync(GitGoToCommitReferenceKind kind,
        CancellationToken cancellationToken = default)
    {
        GitReferenceSorting sorting = ReferenceSorting;
        string prefix = kind switch
        {
            GitGoToCommitReferenceKind.LocalBranch => "refs/heads/",
            GitGoToCommitReferenceKind.Tag => "refs/tags/",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        GitResult result = await ExecuteAsync(["--no-optional-locks", "for-each-ref",
            .. ReferenceSortArguments(sorting, includeTags: kind == GitGoToCommitReferenceKind.Tag), "--count=1000",
            "--format=%(refname)%00%(objectname)%00%(symref)", prefix], cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            string[] fields = line.Split('\0');
            return new GitReference(fields[0], fields[1], null, false, EmptyToNull(fields[2]));
        }).ToArray();
    }
}
