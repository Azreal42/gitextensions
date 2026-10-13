using System.Text;

namespace GitExtensions.Linux;

public sealed record GitMergeOptions(string Revision, bool AllowFastForward = true, bool Squash = false,
    bool NoCommit = false, string? Strategy = null, bool AllowUnrelatedHistories = false,
    string? Message = null, int? LogCount = null);

public sealed record GitMergeResult(GitResult Result, IReadOnlyList<string> ConflictedPaths,
    string Revision, bool HeadChanged, bool CanContinue);

public sealed partial class GitRepository
{
    /// <summary>Merge with the explicit flags offered by the original merge window.</summary>
    public Task<GitMergeResult> MergeAsync(GitMergeOptions options, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Squash && !options.AllowFastForward) { throw new ArgumentException("Squash cannot be combined with no fast forward.", nameof(options)); }
            if (options.LogCount is < 0) { throw new ArgumentOutOfRangeException(nameof(options)); }
            if (options.Strategy is string strategy && (string.IsNullOrWhiteSpace(strategy) || strategy.IndexOfAny(['\0', '\r', '\n']) >= 0))
            { throw new ArgumentException("Provide a single merge strategy name.", nameof(options)); }
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            string revision = await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false);
            string? before = (await GetStatusAsync(token).ConfigureAwait(false)).Head;
            byte[]? message = options.Message is null ? null : (await GetCommitMessageEncodingAsync(token).ConfigureAwait(false)).GetBytes(options.Message);
            List<string> arguments = ["merge", options.AllowFastForward ? "--ff" : "--no-ff", "--no-edit"];
            if (options.Squash) { arguments.Add("--squash"); }
            if (options.NoCommit) { arguments.Add("--no-commit"); }
            if (options.Strategy is not null) { arguments.Add("--strategy=" + options.Strategy); }
            if (options.AllowUnrelatedHistories) { arguments.Add("--allow-unrelated-histories"); }
            if (options.LogCount is > 0) { arguments.Add("--log=" + options.LogCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            string? messagePath = null;
            if (message is not null)
            {
                // Unlike git commit, git merge -F does not interpret '-' as stdin.
                messagePath = System.IO.Path.Combine(info.GitDirectory, "gitextensions-merge-message." + Guid.NewGuid().ToString("N"));
                FileStreamOptions fileOptions = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) { fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                bool created = false;
                try
                {
                    await using FileStream file = new(messagePath, fileOptions);
                    created = true;
                    await file.WriteAsync(message, token).ConfigureAwait(false);
                    await file.FlushAsync(token).ConfigureAwait(false);
                }
                catch { if (created) { File.Delete(messagePath); } throw; }
                arguments.AddRange(["-F", messagePath]);
            }
            // Preserve the selected name: Git uses it to generate branch/tag merge messages.
            arguments.AddRange(["--", options.Revision]);
            GitBinaryResult binary;
            try
            {
                binary = await _runner.RunBinaryAsync(Path,
                    [.. GlobalOptions, "-c", "i18n.logOutputEncoding=UTF-8", .. arguments], token).ConfigureAwait(false);
            }
            finally { if (messagePath is not null) { File.Delete(messagePath); } }
            GitResult result = new(binary.ExitCode, Encoding.UTF8.GetString(binary.StandardOutput), binary.StandardError);
            GitStatus after = await GetStatusAsync(token).ConfigureAwait(false);
            string[] conflicts = after.Changes.Where(change => change.IsConflict).Select(change => change.Path).ToArray();
            bool pending = File.Exists(System.IO.Path.Combine(info.GitDirectory, "MERGE_HEAD"));
            string output = result.StandardOutput + result.StandardError;
            bool rerereContinuation = pending && output.Contains("using previous resolution", StringComparison.Ordinal)
                && !output.TrimEnd().EndsWith("Aborted", StringComparison.Ordinal);
            if (!result.Success && conflicts.Length == 0 && !rerereContinuation) { throw new GitCommandException(arguments, result); }
            return new GitMergeResult(result, conflicts, revision, before != after.Head, pending || conflicts.Length > 0);
        }, cancellationToken);
}
