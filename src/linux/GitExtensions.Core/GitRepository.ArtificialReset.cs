namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>
    /// Resets the whole working tree from the index, or the index and working tree from
    /// implicit HEAD, as the artificial revision menu does. Only Index and WorkingTree
    /// are valid kinds. Optional cleanup removes nonignored untracked files/directories
    /// without an explicit submodule recursion or an extra force flag.
    /// </summary>
    /// <returns>The last completed native command's result. A failed reset prevents cleanup.</returns>
    /// <remarks>
    /// Both commands share one mutation/execution lease. This is not a transaction:
    /// a failure or cancellation may follow actual reset or cleanup effects. Callers must
    /// retain that possibility and refresh the repository after the operation drains.
    /// HEAD is intentionally not resolved first, including in an unborn repository.
    /// </remarks>
    public Task<GitResult> ResetArtificialRevisionAsync(GitCompareRevisionKind kind, bool deleteNewFiles,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (GitCompareRevisionKind.WorkingTree or GitCompareRevisionKind.Index))
        { throw new ArgumentOutOfRangeException(nameof(kind), kind, "Choose the working tree or index artificial revision."); }
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            GitResult result = await ExecuteAsync(kind == GitCompareRevisionKind.WorkingTree
                ? ["checkout", "--", "."] : ["reset", "--hard", "--quiet", "--"], token).ConfigureAwait(false);
            if (deleteNewFiles)
            {
                token.ThrowIfCancellationRequested();
                result = await ExecuteAsync(["clean", "-d", "-f", "--"], token).ConfigureAwait(false);
            }
            return result;
        }, cancellationToken);
    }
}
