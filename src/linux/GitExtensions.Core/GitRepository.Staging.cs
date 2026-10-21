namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Commit's staging policy returns the actual exit/output for its optional error report.</summary>
    public Task<GitResult> StageAsync(IEnumerable<string> paths, bool showErrorsWhenStagingFiles,
        CancellationToken cancellationToken = default)
    {
        string[] selected = ValidatePaths(paths);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            // Same literal add pipeline; the Windows option disables only safecrlf for this invocation.
            string[] policy = showErrorsWhenStagingFiles ? [] : ["-c", "core.safecrlf=false"];
            return await _runner.RunAsync(Path, [.. GlobalOptions, .. policy, "--literal-pathspecs", "add", "-A", "--", .. selected], token).ConfigureAwait(false);
        }, cancellationToken);
    }
}
