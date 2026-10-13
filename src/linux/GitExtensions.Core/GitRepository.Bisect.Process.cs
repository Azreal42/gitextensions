namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private async Task<GitResult> ExecuteBisectProcessAsync(IReadOnlyList<string> arguments, CancellationToken token,
        Action<GitProcessOutput>? outputReceived, int[]? allowedExitCodes = null)
    {
        if (outputReceived is null) { return await ExecuteAsync(arguments, token, allowedExitCodes: allowedExitCodes).ConfigureAwait(false); }
        token.ThrowIfCancellationRequested();
        GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, "--literal-pathspecs", .. arguments], token, outputReceived: outputReceived).ConfigureAwait(false);
        if (!result.Success && !(allowedExitCodes?.Contains(result.ExitCode) ?? false)) { throw new GitCommandException(arguments, result); }
        return result;
    }
}
