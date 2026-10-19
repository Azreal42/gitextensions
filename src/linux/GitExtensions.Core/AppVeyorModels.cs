using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux;

public enum AppVeyorBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }

/// <summary>The skipped count intentionally retains Windows' total-minus-passed compatibility formula, including failures.</summary>
public sealed record AppVeyorTestsSummary(int Total, int Passed, int Failed)
{
    public int Skipped => Total - Passed;
    public string Text => Total == 0 ? "" : Total.ToString(CultureInfo.InvariantCulture) + " tests"
        + (Failed != 0 || Skipped != 0 ? $" ( {Failed} failed, {Skipped} skipped )" : "");
}

public sealed record AppVeyorBuild
{
    public long BuildId { get; init; }
    public string Version { get; init; } = "";
    public string Id => Version; // Original BuildInfo.Id is the mutable display version, not the stable numerical identity.
    public string ProjectId { get; init; } = "";
    public string CommitHash { get; init; } = "";
    public string Branch { get; init; } = "";
    public string RawStatus { get; init; } = "";
    public AppVeyorBuildStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public long? DurationMilliseconds { get; init; }
    public string PullRequestText { get; init; } = "";
    public string PullRequestTitle { get; init; } = "";
    public Uri? PullRequestUri { get; init; }
    public Uri? PageUri { get; init; }
    public AppVeyorTestsSummary? Tests { get; init; }
    public int ProgressCounter { get; init; }
    public bool IsRunning => Status == AppVeyorBuildStatus.InProgress;
    public bool ShowInBuildReportTab => true;
    public string TestsResultText => Tests?.Text ?? "";
    public string Description => DurationText + " " + TestsResultText + (string.IsNullOrWhiteSpace(PullRequestText) ? "" : " " + PullRequestText) + " " + Version;
    public string Tooltip => (IsRunning ? "In progress" + new string('.', ProgressCounter) + new string(' ', 3 - ProgressCounter) : Status.ToString())
        + Environment.NewLine + (DurationMilliseconds.HasValue ? DurationText + Environment.NewLine : "")
        + (TestsResultText.Length > 0 ? TestsResultText + Environment.NewLine : "")
        + (PullRequestText.Length > 0 ? PullRequestText + ": " + PullRequestTitle + Environment.NewLine : "") + Version;
    private string DurationText => DurationMilliseconds.HasValue ? TimeSpan.FromMilliseconds(DurationMilliseconds.Value).ToString(@"mm\:ss", CultureInfo.InvariantCulture) : "";
    public static AppVeyorBuildStatus ParseStatus(string? status) => status switch
    { "success" => AppVeyorBuildStatus.Success, "failed" => AppVeyorBuildStatus.Failure, "cancelled" => AppVeyorBuildStatus.Stopped,
      "queued" or "running" => AppVeyorBuildStatus.InProgress, _ => AppVeyorBuildStatus.Unknown };
    public static bool IsCommitHash(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0');
    public override string ToString() => "AppVeyor build (details omitted)";
}

public sealed record AppVeyorCommitStatus(string CommitHash, AppVeyorBuildStatus Status, AppVeyorBuild Build)
{
    public IReadOnlyList<AppVeyorBuild> Builds { get; } = Array.AsReadOnly(new[] { Build });
    public string Text => Build.Description;
}

/// <summary>One build per SHA across all projects, stable descending StartedAt order as in Windows.</summary>
public sealed class AppVeyorSnapshot
{
    public IReadOnlyList<AppVeyorBuild> Builds { get; }
    public IReadOnlyDictionary<string, AppVeyorCommitStatus> Commits { get; }
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public AppVeyorSnapshot(IEnumerable<AppVeyorBuild> builds, DateTimeOffset readAt, bool truncated = false, IEnumerable<string>? diagnostics = null)
    {
        ReadAt = readAt; Truncated = truncated; Diagnostics = Array.AsReadOnly((diagnostics ?? []).Distinct(StringComparer.Ordinal).ToArray());
        Builds = Array.AsReadOnly(builds.Where(b => b.BuildId > 0 && AppVeyorBuild.IsCommitHash(b.CommitHash))
            .OrderByDescending(b => b.StartedAt ?? DateTimeOffset.MinValue).DistinctBy(b => b.CommitHash, StringComparer.OrdinalIgnoreCase).ToArray());
        Commits = new ReadOnlyDictionary<string, AppVeyorCommitStatus>(Builds.ToDictionary(b => b.CommitHash,
            b => new AppVeyorCommitStatus(b.CommitHash, b.Status, b), StringComparer.OrdinalIgnoreCase));
    }
}
