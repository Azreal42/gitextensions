using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux;

public enum AzureDevOpsBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }
public sealed record AzureDevOpsRepository(string Id, string Name);
public sealed record AzureDevOpsBuildDefinition(long Id, string Name);
public sealed record AzureDevOpsBuild
{
    public long BuildId { get; init; }
    public string BuildNumber { get; init; } = "";
    public long DefinitionId { get; init; }
    public string DefinitionName { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public string CommitHash { get; init; } = "";
    public string RawStatus { get; init; } = "";
    public string Result { get; init; } = "";
    public string Reason { get; init; } = "";
    public AzureDevOpsBuildStatus Status { get; init; }
    public AzureDevOpsBuildStatus BuildStatus => Status;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long? DurationMilliseconds { get; init; }
    public string DurationText { get; init; } = "";
    public string PullRequestId { get; init; } = "";
    public Uri? PageUri { get; init; }
    public Uri? PullRequestUri { get; init; }
    public bool IsRunning => RawStatus != "completed";
    public bool ShowInBuildReportTab => false;
    public string Description => DurationText + " " + BuildNumber;
    public string Tooltip => BuildNumber + " " + Symbol(IsRunning ? RawStatus : Result) + " - " + DurationText + " [" + DefinitionName + "]"
        + (PullRequestId.Length > 0 ? Environment.NewLine + "PR #" + PullRequestId : "");
    public static AzureDevOpsBuildStatus Map(string? status, string? result) => status != "completed" ? AzureDevOpsBuildStatus.InProgress : result switch
    { "failed" => AzureDevOpsBuildStatus.Failure, "canceled" => AzureDevOpsBuildStatus.Stopped, "succeeded" => AzureDevOpsBuildStatus.Success,
      "partiallySucceeded" => AzureDevOpsBuildStatus.Unstable, _ => AzureDevOpsBuildStatus.Unknown };
    private static string Symbol(string value) => value switch
    { "failed" => "❌", "canceled" or "cancelling" => "⏹️", "succeeded" => "✔", "partiallySucceeded" => "❗", "inProgress" => "▶️", "notStarted" => "⏸", "postponed" => "⏱", _ => "❓" };
    public static bool IsCommitHash(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0');
    public static string FormatDuration(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    public override string ToString() => "Azure DevOps build (details omitted)";
}
public sealed record AzureDevOpsCommitStatus(string CommitHash, AzureDevOpsBuildStatus Status, AzureDevOpsBuild Build)
{
    public IReadOnlyList<AzureDevOpsBuild> Builds { get; } = Array.AsReadOnly(new[] { Build });
    public string Text => Build.Description;
}
public sealed class AzureDevOpsSnapshot
{
    public IReadOnlyList<AzureDevOpsBuild> Builds { get; }
    public IReadOnlyDictionary<string, AzureDevOpsCommitStatus> Commits { get; }
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public AzureDevOpsSnapshot(IEnumerable<AzureDevOpsBuild> builds, DateTimeOffset readAt, bool truncated = false, IEnumerable<string>? diagnostics = null)
    {
        ReadAt = readAt; Truncated = truncated; Diagnostics = Array.AsReadOnly((diagnostics ?? []).Distinct(StringComparer.Ordinal).ToArray());
        AzureDevOpsBuild[] candidates = builds.Where(b => AzureDevOpsBuild.IsCommitHash(b.SourceVersion) && AzureDevOpsBuild.IsCommitHash(b.CommitHash)).ToArray();
        IEnumerable<AzureDevOpsBuild> running = candidates.Where(b => b.IsRunning).GroupBy(b => b.SourceVersion, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Where(b => b.StartedAt.HasValue).MinBy(b => b.StartedAt) ?? g.First());
        IEnumerable<AzureDevOpsBuild> finished = candidates.Where(b => !b.IsRunning).GroupBy(b => b.SourceVersion, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.FinishedAt).First());
        Builds = Array.AsReadOnly(running.Concat(finished).ToArray());
        // Original watcher replaces only when StartDate >= the current StartDate, after PR-head rewriting.
        Dictionary<string, AzureDevOpsCommitStatus> commits = new(StringComparer.OrdinalIgnoreCase);
        foreach (AzureDevOpsBuild build in Builds)
        {
            if (!commits.TryGetValue(build.CommitHash, out AzureDevOpsCommitStatus? current) || (build.StartedAt ?? DateTimeOffset.MinValue) >= (current.Build.StartedAt ?? DateTimeOffset.MinValue))
            { commits[build.CommitHash] = new(build.CommitHash, build.Status, build); }
        }
        Commits = new ReadOnlyDictionary<string, AzureDevOpsCommitStatus>(commits);
    }
}
