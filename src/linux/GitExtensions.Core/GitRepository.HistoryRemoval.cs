using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    // Values are transported in a private NUL file and environment, never interpolated into shell code.
    // Git 2.38 (our minimum) supports rm's --pathspec-from-file/--pathspec-file-nul.
    private const string HistoryRemovalIndexFilter = "\"$GITEXTENSIONS_GIT_EXECUTABLE\" --literal-pathspecs rm -r -f --cached --ignore-unmatch --pathspec-from-file=\"$GITEXTENSIONS_HISTORY_PATHS_FILE\" --pathspec-file-nul --";
    private static readonly UTF8Encoding HistoryRemovalUtf8 = new(false, true);

    /// <summary>FindLargeFiles deletion: one index-filter for the literal union, then native backup/reflog/object cleanup.
    /// Bare and linked worktrees retain Git's native behavior. Annotated tags retain Git's warning and original target.</summary>
    public async Task<GitHistoryRemovalResult> RemovePathsFromHistoryAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        string[] selected;
        try
        {
            ArgumentNullException.ThrowIfNull(paths);
            selected = ValidatePaths(paths);
            if (selected.Any(path => path == ".")) { throw new ArgumentException("The repository root cannot be removed from history.", nameof(paths)); }
            // Strict encoding rejects unrepresentable names before any native mutation.
            foreach (string path in selected) { _ = HistoryRemovalUtf8.GetByteCount(path); }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { return new([], [], false, cancellationToken.IsCancellationRequested, error.Message); }

        List<GitHistoryRemovalStepResult> steps = [];
        bool mutationStarted = false;
        string? errorMessage = null;
        try
        {
            await WithMutationAsync(async token =>
            {
                await GetInfoAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-history-remove-" + Guid.NewGuid().ToString("N"));
                if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
                else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                try
                {
                    string pathFile = System.IO.Path.Combine(directory, "paths");
                    FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                    if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                    await using (FileStream file = new(pathFile, options))
                    {
                        foreach (string path in selected)
                        {
                            await file.WriteAsync(HistoryRemovalUtf8.GetBytes(path), token).ConfigureAwait(false);
                            await file.WriteAsync(new byte[1], token).ConfigureAwait(false);
                        }
                    }
                    Dictionary<string, string?> environment = new(StringComparer.Ordinal)
                    {
                        ["GITEXTENSIONS_HISTORY_PATHS_FILE"] = pathFile,
                    };
                    token.ThrowIfCancellationRequested();
                    mutationStarted = true; // A mutation-capable command is handed to the runner; do not invent certainty after a launch failure.
                    if (!await RunStep(GitHistoryRemovalStep.RewriteHistory,
                        ["filter-branch", "--index-filter", HistoryRemovalIndexFilter, "--prune-empty", "--", "--all"], environment).ConfigureAwait(false)) { return false; }
                    if (!await RunStep(GitHistoryRemovalStep.EnumerateBackupReferences,
                        ["for-each-ref", "--format=%(refname)%00%(objectname)", "refs/original/"]).ConfigureAwait(false)) { return false; }
                    // Native refnames cannot contain LF or NUL. Validate the whole report before deleting any ref.
                    List<(string Name, string Object)> backups = [];
                    foreach (string line in steps[^1].StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] fields = line.Split('\0');
                        if (fields.Length != 2 || !fields[0].StartsWith("refs/original/", StringComparison.Ordinal)
                            || fields[0].Length == "refs/original/".Length || fields[1].Length is not (40 or 64)
                            || fields[1].Any(character => !char.IsAsciiHexDigit(character)))
                        { throw new InvalidDataException("Git returned an invalid backup reference report; backup cleanup stopped."); }
                        backups.Add((fields[0], fields[1]));
                    }
                    foreach ((string name, string expectedObject) in backups)
                    {
                        if (!await RunStep(GitHistoryRemovalStep.DeleteBackupReference,
                            ["update-ref", "-d", name, expectedObject]).ConfigureAwait(false)) { return false; }
                    }
                    if (!await RunStep(GitHistoryRemovalStep.ExpireReflogs, ["reflog", "expire", "--expire=now", "--all"]).ConfigureAwait(false)) { return false; }
                    return await RunStep(GitHistoryRemovalStep.GarbageCollect, ["gc", "--aggressive", "--prune=now"]).ConfigureAwait(false);

                    async Task<bool> RunStep(GitHistoryRemovalStep step, string[] arguments, IReadOnlyDictionary<string, string?>? commandEnvironment = null)
                    {
                        token.ThrowIfCancellationRequested();
                        GitHistoryRemovalStepResult outcome = await RunHistoryRemovalStepAsync(step, arguments, token, commandEnvironment, outputReceived).ConfigureAwait(false);
                        steps.Add(outcome);
                        return outcome.Success && !token.IsCancellationRequested;
                    }
                }
                finally { Directory.Delete(directory, recursive: true); }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { errorMessage = error.Message; }
        return new(selected, steps.ToArray(), mutationStarted, cancellationToken.IsCancellationRequested || steps.Any(step => step.Cancelled), errorMessage);
    }

    private async Task<GitHistoryRemovalStepResult> RunHistoryRemovalStepAsync(GitHistoryRemovalStep step,
        IReadOnlyList<string> arguments, CancellationToken token, IReadOnlyDictionary<string, string?>? environment,
        Action<GitProcessOutput>? outputReceived)
    {
        string[] snapshot = arguments.ToArray();
        using MemoryStream output = new(), error = new();
        try
        {
            GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. snapshot], token, environment: environment, outputReceived: item =>
            {
                (item.Stream == GitProcessOutputStream.StandardOutput ? output : error).Write(item.Bytes.Span);
                outputReceived?.Invoke(item);
            }).ConfigureAwait(false);
            return new(step, snapshot, result, false, null);
        }
        catch (Exception failure)
        {
            bool cancelled = failure is OperationCanceledException && token.IsCancellationRequested;
            byte[] outputBytes = output.ToArray(), errorBytes = error.ToArray();
            List<string> warnings = [];
            return new(step, snapshot, null, cancelled, cancelled ? null : failure.Message)
            {
                PartialStandardOutput = GitOutputText.Utf8(outputBytes, "stdout", warnings), PartialStandardError = GitOutputText.Utf8(errorBytes, "stderr", warnings),
                PartialStandardOutputBytes = outputBytes, PartialStandardErrorBytes = errorBytes,
            };
        }
    }
}
