using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux;

public enum GitLabBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }

public sealed record GitLabProject([property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path_with_namespace")] string? PathWithNamespace = null,
    [property: JsonPropertyName("web_url")] string? WebUrl = null);

/// <summary>Original Gitlab pipeline fields. Long IDs retain Windows int compatibility and larger modern IDs.</summary>
public sealed record GitLabPipeline
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("project_id")] public long ProjectId { get; init; }
    [JsonPropertyName("sha")] public string Sha { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("web_url")] public string? WebUrl { get; init; }
    [JsonPropertyName("ref")] public string? Ref { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonIgnore] public GitLabBuildStatus BuildStatus => Status switch
    {
        "running" => GitLabBuildStatus.InProgress, "success" => GitLabBuildStatus.Success,
        "failed" => GitLabBuildStatus.Failure, "canceled" => GitLabBuildStatus.Stopped, _ => GitLabBuildStatus.Unknown
    };
    [JsonIgnore] public long? Duration => CreatedAt.HasValue && UpdatedAt.HasValue ? (UpdatedAt.Value - CreatedAt.Value).Ticks : null;
    [JsonIgnore] public bool ShowInBuildReportTab => false; // Original GitlabPipeline.ToBuildInfo explicitly opts out.
    [JsonIgnore] public string Description => (Name ?? "Pipeline") + " #" + Id + " (" + Status + ")";
    public override string ToString() => "GitLab pipeline (details omitted)";
    public static bool IsCommitHash(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record GitLabCommitStatus(string CommitHash, GitLabBuildStatus Status, GitLabPipeline Pipeline)
{
    public IReadOnlyList<GitLabPipeline> Pipelines { get; } = Array.AsReadOnly(new[] { Pipeline });
    public string Text => Pipeline.Description;
}

/// <summary>Atomic bounded history. A SHA uses its latest UpdatedAt, with ID as deterministic tie breaker.</summary>
public sealed class GitLabSnapshot
{
    public IReadOnlyList<GitLabPipeline> Pipelines { get; }
    public IReadOnlyDictionary<string, GitLabCommitStatus> Commits { get; }
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public GitLabSnapshot(IEnumerable<GitLabPipeline> pipelines, DateTimeOffset readAt, bool truncated = false, IEnumerable<string>? diagnostics = null)
    {
        ReadAt = readAt; Truncated = truncated; Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        Pipelines = Array.AsReadOnly(pipelines.Where(p => p.Id > 0 && GitLabPipeline.IsCommitHash(p.Sha)).GroupBy(p => p.Id)
            .Select(g => g.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.CreatedAt).First())
            .OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id).ToArray());
        Dictionary<string, GitLabCommitStatus> commits = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, GitLabPipeline> group in Pipelines.GroupBy(p => p.Sha, StringComparer.OrdinalIgnoreCase))
        {
            GitLabPipeline latest = group.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id).First();
            commits[group.Key] = new(group.Key, latest.BuildStatus, latest);
        }
        Commits = new ReadOnlyDictionary<string, GitLabCommitStatus>(commits);
    }
}
