using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux;

public sealed record TeamCityProject(string Id, string Name, string? ParentProjectId, bool Archived = false);
public sealed record TeamCityBuildType(string Id, string Name, string ProjectId)
{
    public string DisplayName => Name + " (" + Id + ")";
}
public sealed record TeamCityBuildSelection(string ProjectId, string BuildTypeId);

public sealed class TeamCityPage<T>
{
    public IReadOnlyList<T> Items { get; }
    public bool Truncated { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public TeamCityPage(IEnumerable<T> items, bool truncated = false, IEnumerable<string>? diagnostics = null)
    { Items = Array.AsReadOnly(items.ToArray()); Truncated = truncated; Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray()); }
}

public enum TeamCityBuildStatus { Unknown, InProgress, Success, Failure, Stopped, Unstable }

public sealed record TeamCityBuild(string Id, string BuildTypeId, string Number, string State, string RawStatus,
    bool Running, bool Cancelled, DateTimeOffset? StartDate, DateTimeOffset? FinishDate, string Description,
    string? WebUrl, IReadOnlyList<string> CommitHashes)
{
    public TeamCityBuildStatus Status => Cancelled ? TeamCityBuildStatus.Stopped
        : Running || State is "running" or "queued" ? TeamCityBuildStatus.InProgress
        : State != "finished" ? TeamCityBuildStatus.Unknown
        : RawStatus switch { "SUCCESS" => TeamCityBuildStatus.Success, "FAILURE" => TeamCityBuildStatus.Failure, _ => TeamCityBuildStatus.Unknown };
    public override string ToString() => "TeamCity build (details omitted)";
}

public sealed record TeamCityCommitStatus(string CommitHash, TeamCityBuildStatus Status, IReadOnlyList<TeamCityBuild> Builds)
{
    public string Text => string.Join("; ", Builds.Select(b => b.Description));
}

/// <summary>A bounded immutable publication; Truncated never implies a complete build history.</summary>
public sealed class TeamCityBuildSnapshot
{
    public DateTimeOffset ReadAt { get; }
    public bool Truncated { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<TeamCityBuild> Builds { get; }
    public IReadOnlyDictionary<string, TeamCityCommitStatus> Commits { get; }
    public TeamCityBuildSnapshot(IEnumerable<TeamCityBuild> builds, DateTimeOffset readAt, bool truncated = false, IEnumerable<string>? diagnostics = null)
    {
        ReadAt = readAt; Truncated = truncated; Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        Builds = Array.AsReadOnly(builds.GroupBy(b => b.Id, StringComparer.Ordinal).Select(g => g.Last())
            .Select(b => b with { CommitHashes = Array.AsReadOnly(b.CommitHashes.Where(IsCommitHash).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) })
            .OrderByDescending(b => NumericId(b.Id)).ToArray());
        Dictionary<string, TeamCityCommitStatus> commits = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, (string Sha, TeamCityBuild Build)> group in Builds.SelectMany(b => b.CommitHashes.Select(s => (Sha: s, Build: b))).GroupBy(x => x.Sha, StringComparer.OrdinalIgnoreCase))
        {
            TeamCityBuild[] latest = group.Select(x => x.Build).GroupBy(b => b.BuildTypeId, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(b => NumericId(b.Id)).First()).ToArray();
            commits[group.Key] = new(group.Key, latest.Select(b => b.Status).OrderByDescending(Priority).First(), Array.AsReadOnly(latest));
        }
        Commits = new ReadOnlyDictionary<string, TeamCityCommitStatus>(commits);
    }
    public static bool IsCommitHash(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);
    private static ulong NumericId(string id) => ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n) ? n : 0;
    private static int Priority(TeamCityBuildStatus s) => s switch
    { TeamCityBuildStatus.Failure => 6, TeamCityBuildStatus.InProgress => 5, TeamCityBuildStatus.Stopped => 4, TeamCityBuildStatus.Unstable => 3, TeamCityBuildStatus.Unknown => 2, _ => 1 };
}
