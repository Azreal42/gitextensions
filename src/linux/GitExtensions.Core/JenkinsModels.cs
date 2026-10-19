using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux;

public enum JenkinsBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }
public sealed record JenkinsTestResults(long Total, long Failed, long Skipped);
public sealed record JenkinsBuild
{
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public long Number { get; init; }
    public string BuildId => ProjectId + "#" + Number.ToString(CultureInfo.InvariantCulture);
    public string Id => BuildId;
    public string BuildNumber => Number.ToString(CultureInfo.InvariantCulture);
    public string RawResult { get; init; } = "";
    public bool IsRunning { get; init; }
    public JenkinsBuildStatus Status { get; init; }
    public JenkinsBuildStatus BuildStatus => Status;
    public IReadOnlyList<string> CommitHashes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CommitHashList => CommitHashes;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt => DurationMilliseconds is long ms ? StartedAt.AddMilliseconds(ms) : StartedAt;
    public long? DurationMilliseconds { get; init; }
    public long? Duration => DurationMilliseconds;
    public JenkinsTestResults? TestResults { get; init; }
    public Uri? PageUri { get; init; }
    public string? Tooltip { get; init; }
    public bool ShowInBuildReportTab => true;
    public string Description => "#" + BuildNumber + " " + FormatDuration(DurationMilliseconds) + " "
        + (TestResults is { Total: > 0 } tests ? $"{tests.Total} tests ({tests.Failed} failed, {tests.Skipped} skipped)" : "") + " " + Status;
    public static JenkinsBuildStatus Map(string? result, bool running) => running ? JenkinsBuildStatus.InProgress : result switch
    { "SUCCESS" => JenkinsBuildStatus.Success, "FAILURE" => JenkinsBuildStatus.Failure, "UNSTABLE" => JenkinsBuildStatus.Unstable, "ABORTED" => JenkinsBuildStatus.Stopped, _ => JenkinsBuildStatus.Unknown };
    private static string FormatDuration(long? milliseconds) => milliseconds.HasValue ? TimeSpan.FromMilliseconds(milliseconds.Value).ToString(@"mm\:ss", CultureInfo.InvariantCulture) : "";
    public static bool IsCommitHash(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0');
    public override string ToString() => "Jenkins build (details omitted)";
}
public sealed record JenkinsCommitStatus(string CommitHash, JenkinsBuildStatus Status, JenkinsBuild Build)
{
    public IReadOnlyList<JenkinsBuild> Builds { get; } = Array.AsReadOnly(new[] { Build });
}
public sealed class JenkinsSnapshot
{
    public IReadOnlyList<JenkinsBuild> Builds { get; }
    public IReadOnlyDictionary<string, JenkinsCommitStatus> Commits { get; }
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public JenkinsSnapshot(IEnumerable<JenkinsBuild> builds, DateTimeOffset readAt, bool truncated = false, IEnumerable<string>? diagnostics = null,
        IReadOnlyDictionary<string, JenkinsCommitStatus>? previous = null, IEnumerable<JenkinsBuild>? emissions = null)
    {
        JenkinsBuild[] observed = builds.ToArray();
        Builds = Array.AsReadOnly(observed.GroupBy(b => b.BuildId).Select(group => group.FirstOrDefault(b => Completed(b.Status))
            ?? group.FirstOrDefault(b => b.Status == JenkinsBuildStatus.InProgress) ?? group.First()).ToArray()); ReadAt = readAt; Truncated = truncated;
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).Distinct(StringComparer.Ordinal).ToArray());
        Dictionary<string, JenkinsBuildStatus> pass = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> present = observed.SelectMany(b => b.CommitHashes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JenkinsCommitStatus> commits = previous?.Where(p => present.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        // Only full reads emitted during this pass participate in its Windows priority admission.
        // Unchanged project history still supplies Builds/presence and preserves the watcher result.
        // A full read can run before a cached project but fall outside the configured-order history bound.
        HashSet<string> admitted = observed.Select(build => build.BuildId).ToHashSet(StringComparer.Ordinal);
        foreach (JenkinsBuild build in (emissions ?? observed).Where(build => admitted.Contains(build.BuildId)))
        {
            bool better = false;
            foreach (string sha in build.CommitHashes.Where(JenkinsBuild.IsCommitHash))
            {
                if (!pass.TryGetValue(sha, out JenkinsBuildStatus old) || !Completed(old) && (Completed(build.Status) || old != JenkinsBuildStatus.InProgress && build.Status == JenkinsBuildStatus.InProgress))
                { pass[sha] = build.Status; better = true; }
            }
            if (!better) { continue; }
            // Original watcher compares start time after per-pass priority admission; ties replace.
            foreach (string sha in build.CommitHashes.Where(JenkinsBuild.IsCommitHash))
            { if (!commits.TryGetValue(sha, out JenkinsCommitStatus? old) || build.StartedAt >= old.Build.StartedAt) { commits[sha] = new(sha, build.Status, build); } }
        }
        Commits = new ReadOnlyDictionary<string, JenkinsCommitStatus>(commits);
    }
    private static bool Completed(JenkinsBuildStatus status) => status is JenkinsBuildStatus.Success or JenkinsBuildStatus.Failure or JenkinsBuildStatus.Unstable;
}
