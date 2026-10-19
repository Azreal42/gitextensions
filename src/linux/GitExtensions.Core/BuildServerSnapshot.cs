using System.Collections.ObjectModel;

namespace GitExtensions.Linux;

/// <summary>Presentation shared by CI providers; provider DTOs retain their original wire semantics.</summary>
public enum BuildServerBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }

public sealed record BuildServerRun(string Id, string DefinitionId, string Description, BuildServerBuildStatus Status,
    IReadOnlyList<string> CommitHashes, DateTimeOffset? StartedAt, DateTimeOffset? UpdatedAt, Uri? PageUri,
    string? Tooltip = null, Uri? PullRequestUri = null, bool ShowInBuildReportTab = true, long? DurationMilliseconds = null);

public sealed record BuildServerCommitStatus(string CommitHash, BuildServerBuildStatus Status, IReadOnlyList<BuildServerRun> Runs)
{
    public string Text => string.Join("; ", Runs.Select(run => run.Description));
    public string Tooltip => Runs.Any(run => run.Tooltip is not null)
        ? string.Join(Environment.NewLine + Environment.NewLine, Runs.Select(run => run.Tooltip ?? run.Description)) : Text;
    public static BuildServerCommitStatus FromGitHubActions(GitHubActionsCommitStatus status, Func<GitHubActionsWorkflowRun, Uri?>? page = null)
        => new(status.CommitHash, (BuildServerBuildStatus)status.Status,
            Array.AsReadOnly(status.Runs.Select(run => FromGitHubActionsRun(run, page?.Invoke(run))).ToArray()));
    internal static BuildServerRun FromGitHubActionsRun(GitHubActionsWorkflowRun run, Uri? page)
        => new(run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), run.WorkflowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            run.Description, (BuildServerBuildStatus)run.BuildStatus, Array.AsReadOnly(new[] { run.HeadSha }),
            run.RunStartedAt ?? run.CreatedAt, run.UpdatedAt, page, ShowInBuildReportTab: false);
}

public sealed class BuildServerSnapshot
{
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<BuildServerRun> Runs { get; }
    public IReadOnlyDictionary<string, BuildServerCommitStatus> Commits { get; }
    private BuildServerSnapshot(IEnumerable<BuildServerRun> runs, IEnumerable<BuildServerCommitStatus> commits, DateTimeOffset readAt, bool truncated)
    {
        ReadAt = readAt; Truncated = truncated;
        Runs = Array.AsReadOnly(runs.Select(run => run with { CommitHashes = Array.AsReadOnly(run.CommitHashes.ToArray()) }).ToArray());
        Commits = new ReadOnlyDictionary<string, BuildServerCommitStatus>(commits.ToDictionary(value => value.CommitHash,
            value => value with { Runs = Array.AsReadOnly(value.Runs.ToArray()) }, StringComparer.OrdinalIgnoreCase));
    }
    public static BuildServerSnapshot FromGitHubActions(GitHubActionsRunSnapshot snapshot, Func<GitHubActionsWorkflowRun, Uri?> page)
        => new(snapshot.Runs.Select(run => BuildServerCommitStatus.FromGitHubActionsRun(run, page(run))),
            snapshot.Commits.Values.Select(status => BuildServerCommitStatus.FromGitHubActions(status, page)), snapshot.ReadAt, snapshot.Truncated);
    public static BuildServerSnapshot FromTeamCity(TeamCityBuildSnapshot snapshot, Func<TeamCityBuild, Uri?> page)
    {
        BuildServerRun Run(TeamCityBuild build) => new(build.Id, build.BuildTypeId, build.Description, (BuildServerBuildStatus)build.Status,
            Array.AsReadOnly(build.CommitHashes.ToArray()), build.StartDate, build.FinishDate, page(build));
        return new(snapshot.Builds.Select(Run), snapshot.Commits.Values.Select(status => new BuildServerCommitStatus(status.CommitHash,
            (BuildServerBuildStatus)status.Status, Array.AsReadOnly(status.Builds.Select(Run).ToArray()))), snapshot.ReadAt, snapshot.Truncated);
    }
    public static BuildServerSnapshot FromGitLab(GitLabSnapshot snapshot, Func<GitLabPipeline, Uri?> page)
    {
        BuildServerRun Run(GitLabPipeline pipeline) => new(pipeline.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pipeline.ProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture), pipeline.Description,
            (BuildServerBuildStatus)pipeline.BuildStatus, Array.AsReadOnly(new[] { pipeline.Sha }), pipeline.CreatedAt, pipeline.UpdatedAt, page(pipeline), ShowInBuildReportTab: pipeline.ShowInBuildReportTab);
        return new(snapshot.Pipelines.Select(Run), snapshot.Commits.Values.Select(status => new BuildServerCommitStatus(status.CommitHash,
            (BuildServerBuildStatus)status.Status, Array.AsReadOnly(status.Pipelines.Select(Run).ToArray()))), snapshot.ReadAt, snapshot.Truncated);
    }
    public static BuildServerSnapshot FromAppVeyor(AppVeyorSnapshot snapshot, Func<AppVeyorBuild, Uri?> page)
    {
        BuildServerRun Run(AppVeyorBuild build) => new(build.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.ProjectId, build.Description, (BuildServerBuildStatus)build.Status, Array.AsReadOnly(new[] { build.CommitHash }),
            build.StartedAt, build.UpdatedAt, page(build), build.Tooltip, build.PullRequestUri,
            build.ShowInBuildReportTab, build.DurationMilliseconds);
        return new(snapshot.Builds.Select(Run), snapshot.Commits.Values.Select(status => new BuildServerCommitStatus(status.CommitHash,
            (BuildServerBuildStatus)status.Status, Array.AsReadOnly(status.Builds.Select(Run).ToArray()))), snapshot.ReadAt, snapshot.Truncated);
    }

}
