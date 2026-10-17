namespace GitExtensions.Linux;

public enum GitInteractiveFileAction { AddPatch, ResetPatch }

public sealed partial class GitRepository
{
    public string GitExecutable => _runner.Executable;

    /// <summary>The caller owns the interactive console, while Core owns validation and the mutation lock.</summary>
    public Task<GitResult> RunInteractiveFileActionAsync(GitInteractiveFileAction action, IReadOnlyList<string> paths,
        Func<IReadOnlyList<string>, CancellationToken, Task<GitResult>> launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!Enum.IsDefined(action)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        string[] selected = ValidatePaths(paths);
        IReadOnlyList<string> arguments = Array.AsReadOnly<string>(["--literal-pathspecs",
            action == GitInteractiveFileAction.AddPatch ? "add" : "checkout", "--patch", "--", .. selected]);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            foreach (string path in selected) { ValidateWorktreeFileLocation(path); }
            token.ThrowIfCancellationRequested();
            return await launch(arguments, token).ConfigureAwait(false);
        }, cancellationToken);
    }
}
