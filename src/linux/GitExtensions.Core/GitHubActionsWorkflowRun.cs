using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public enum GitHubActionsBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }

/// <summary>Windows GitHubActionsWorkflowRun wire fields and BuildStatus mapping, without UI dependencies.</summary>
public sealed class GitHubActionsWorkflowRun
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("workflow_id")] public long WorkflowId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("head_sha")] public string HeadSha { get; init; } = "";
    [JsonPropertyName("head_branch")] public string? HeadBranch { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("conclusion")] public string? Conclusion { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("run_started_at")] public DateTimeOffset? RunStartedAt { get; init; }
    [JsonPropertyName("display_title")] public string? DisplayTitle { get; init; }
    [JsonPropertyName("run_number")] public int RunNumber { get; init; }
    [JsonPropertyName("run_attempt")] public int RunAttempt { get; init; }
    [JsonIgnore] public GitHubActionsBuildStatus BuildStatus => Status is "in_progress" or "queued" or "requested" or "waiting" or "pending"
        ? GitHubActionsBuildStatus.InProgress : Conclusion switch
        {
            "success" or "neutral" or "skipped" => GitHubActionsBuildStatus.Success,
            "failure" or "timed_out" => GitHubActionsBuildStatus.Failure,
            "cancelled" => GitHubActionsBuildStatus.Stopped,
            "action_required" or "stale" => GitHubActionsBuildStatus.Unstable,
            _ => GitHubActionsBuildStatus.Unknown
        };
    [JsonIgnore] public string Description => $"{Name ?? "workflow"} #{RunNumber} ({(BuildStatus == GitHubActionsBuildStatus.InProgress ? Status : Conclusion ?? Status) ?? "unknown"})";
    public static bool IsCommitHash(string? value) => value is not null && Regex.IsMatch(value, "\\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\\z", RegexOptions.CultureInvariant);
}

public sealed class GitHubActionsWorkflowRunsResponse
{
    [JsonPropertyName("total_count")] public int TotalCount { get; init; }
    [JsonRequired, JsonPropertyName("workflow_runs")] public List<GitHubActionsWorkflowRun> WorkflowRuns { get; init; } = [];
}

public sealed record GitHubActionsCommitStatus(string CommitHash, GitHubActionsBuildStatus Status,
    IReadOnlyList<GitHubActionsWorkflowRun> Runs)
{
    public string Text => string.Join("; ", Runs.Select(r => r.Description));
}

/// <summary>A complete, immutable publication. Multiple workflows on the same SHA remain distinct.</summary>
public sealed class GitHubActionsRunSnapshot
{
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<GitHubActionsWorkflowRun> Runs { get; }
    public IReadOnlyDictionary<string, GitHubActionsCommitStatus> Commits { get; }
    public GitHubActionsRunSnapshot(IEnumerable<GitHubActionsWorkflowRun> runs, DateTimeOffset readAt, bool truncated = false)
    {
        ReadAt = readAt; Truncated = truncated;
        Runs = Array.AsReadOnly(runs.Where(r => r.Id > 0 && GitHubActionsWorkflowRun.IsCommitHash(r.HeadSha))
            .GroupBy(r => r.Id).Select(g => g.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.RunAttempt).First())
            .OrderByDescending(r => r.UpdatedAt).ToArray());
        Dictionary<string, GitHubActionsCommitStatus> commits = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, GitHubActionsWorkflowRun> group in Runs.GroupBy(r => r.HeadSha, StringComparer.OrdinalIgnoreCase))
        {
            // Keep the most recent run of each workflow, rather than hiding sibling workflows by SHA.
            GitHubActionsWorkflowRun[] current = group.GroupBy(r => r.WorkflowId > 0 ? r.WorkflowId.ToString() : r.Name ?? r.Id.ToString())
                .Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ThenByDescending(r => r.RunAttempt).First()).ToArray();
            GitHubActionsBuildStatus status = current.Select(r => r.BuildStatus).OrderByDescending(Priority).First();
            commits.Add(group.Key, new(group.Key, status, Array.AsReadOnly(current)));
        }
        Commits = new ReadOnlyDictionary<string, GitHubActionsCommitStatus>(commits);
    }
    private static int Priority(GitHubActionsBuildStatus status) => status switch
    { GitHubActionsBuildStatus.Failure => 6, GitHubActionsBuildStatus.InProgress => 5, GitHubActionsBuildStatus.Stopped => 4,
        GitHubActionsBuildStatus.Unstable => 3, GitHubActionsBuildStatus.Unknown => 2, _ => 1 };
}
