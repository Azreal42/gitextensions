using System.Globalization;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Checks object integrity without creating lost-found files or pruning objects. A failed check remains a readable report.</summary>
    public Task<GitFsckResult> VerifyObjectsAsync(GitFsckOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        List<string> arguments = ["fsck", options.Full ? "--full" : "--no-full"];
        if (options.IncludeUnreachable) { arguments.Add("--unreachable"); }
        if (options.NoReflogs) { arguments.Add("--no-reflogs"); }
        if (options.Strict) { arguments.Add("--strict"); }
        return WithMutationAsync(async token =>
        {
            await GetInfoAsync(token).ConfigureAwait(false);
            GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. arguments], token).ConfigureAwait(false);
            List<GitFsckIssue> issues = [];
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 3 && fields[0] is "dangling" or "unreachable")
                {
                    issues.Add(new GitFsckIssue(fields[0] == "dangling" ? GitFsckIssueKind.Dangling : GitFsckIssueKind.Unreachable, fields[1], fields[2]));
                }
            }

            return new GitFsckResult(result.ExitCode, result.StandardOutput, result.StandardError, issues);
        }, cancellationToken);
    }

    public Task<GitRepositoryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await GetInfoAsync(token).ConfigureAwait(false);
            GitResult result = await ExecuteAsync(["count-objects", "-v"], token).ConfigureAwait(false);
            Dictionary<string, long> values = new(StringComparer.Ordinal);
            List<string> alternates = [];
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf(": ", StringComparison.Ordinal);
                if (separator < 0) { continue; }
                string key = line[..separator];
                string value = line[(separator + 2)..];
                if (key == "alternate") { alternates.Add(GitQuotedPath.Decode(value)); }
                else if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number)) { values.Add(key, number); }
            }

            return new GitRepositoryStatistics(Number("count"), checked(Number("size") * 1024), Number("in-pack"), Number("packs"), checked(Number("size-pack") * 1024),
                Number("prune-packable"), Number("garbage"), checked(Number("size-garbage") * 1024), alternates, result.StandardError);

            long Number(string key) => values.TryGetValue(key, out long value) ? value : throw new InvalidDataException("Git omitted the repository statistic: " + key);
        }, cancellationToken);

    /// <summary>Runs normal Git garbage collection, retaining Git's configured expiry safeguards.</summary>
    public Task<GitResult> GarbageCollectAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            await GetInfoAsync(token).ConfigureAwait(false);
            return await ExecuteAsync(["gc"], token).ConfigureAwait(false);
        }, cancellationToken);
}
