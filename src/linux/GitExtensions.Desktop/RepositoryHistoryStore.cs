using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux.Desktop;

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryHistoryAnchor>))]
public enum RepositoryHistoryAnchor { None, Top, Recent }

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryPathShorteningStrategy>))]
public enum RepositoryPathShorteningStrategy { None, MostSignDir, MiddleDots }

public sealed record RepositoryHistoryEntry(string Path, string? Category = null, bool Pinned = false)
{
    public RepositoryHistoryAnchor Anchor { get; init; }

    // Older native history files recorded a persistent recent entry as a boolean pin.
    [JsonIgnore]
    public RepositoryHistoryAnchor EffectiveAnchor => Anchor is RepositoryHistoryAnchor.Top or RepositoryHistoryAnchor.Recent
        ? Anchor : Pinned ? RepositoryHistoryAnchor.Recent : RepositoryHistoryAnchor.None;

    public RepositoryHistoryEntry WithAnchor(RepositoryHistoryAnchor anchor)
        => this with { Anchor = anchor, Pinned = anchor != RepositoryHistoryAnchor.None };
}

public sealed record RepositoryHistoryState
{
    private string? _lastVisitedRepositoryPath;
    private bool _lastVisitRecorded;
    public string? LastVisitedRepositoryPath
    {
        get => _lastVisitedRepositoryPath;
        init { _lastVisitedRepositoryPath = value; _lastVisitRecorded = true; }
    }
    // A missing legacy field can fall back once. An explicitly empty/null or
    // invalid recorded last visit must not select another repository instead.
    internal string? StartupRepositoryPath => _lastVisitRecorded ? LastVisitedRepositoryPath : Recent.FirstOrDefault()?.Path;
    public List<RepositoryHistoryEntry> Recent { get; init; } = [];
    public List<RepositoryHistoryEntry> Favorites { get; init; } = [];
    public int HistorySize { get; init; } = 30;
    public bool SortRecent { get; init; }
    public bool SortFavorites { get; init; }
    public int MaxTopRepositories { get; init; }
    public bool HideTopRepositoriesFromRecentList { get; init; }
    public bool SortTopRepos { get; init; }
    public int RecentReposComboMinWidth { get; init; }
    public RepositoryPathShorteningStrategy ShorteningStrategy { get; init; }

    public RepositoryHistoryState Visit(string path)
    {
        RepositoryHistoryEntry entry = Recent.FirstOrDefault(item => item.Path == path) ?? new(path);
        List<RepositoryHistoryEntry> recent = [entry, .. Recent.Where(item => item.Path != path)];
        return (this with { Recent = recent, LastVisitedRepositoryPath = path }).TrimRecent();
    }

    public RepositoryHistoryState Anchor(string path, RepositoryHistoryAnchor anchor)
        => this with { Recent = Recent.Select(item => item.Path == path ? item.WithAnchor(anchor) : item).ToList() };

    public RepositoryHistoryState TrimRecent()
    {
        // Upstream AdjustHistorySize counts anchors towards capacity, but never evicts them.
        int remaining = Math.Max(0, Math.Clamp(HistorySize, 1, 5000) - Recent.Count(item => item.EffectiveAnchor != RepositoryHistoryAnchor.None));
        List<RepositoryHistoryEntry> retained = [];
        foreach (RepositoryHistoryEntry entry in Recent)
        {
            if (entry.EffectiveAnchor != RepositoryHistoryAnchor.None) { retained.Add(entry); }
            else if (remaining > 0) { retained.Add(entry); remaining--; }
        }
        return this with { Recent = retained };
    }

    public RepositoryHistoryState Favorite(string path, bool enabled)
        => this with { Favorites = enabled && !Favorites.Any(item => item.Path == path) ? [new(path), .. Favorites] : enabled ? Favorites : Favorites.Where(item => item.Path != path).ToList() };

    public RepositoryHistoryState AssignCategory(string path, string? category)
        => this with { Favorites = Favorites.Select(item => item.Path == path ? item with { Category = string.IsNullOrWhiteSpace(category) ? null : category } : item).ToList() };

    public RepositoryHistoryState Remove(string path, bool favorite)
        => favorite ? this with { Favorites = Favorites.Where(item => item.Path != path).ToList() } : this with { Recent = Recent.Where(item => item.Path != path).ToList() };

    public RepositoryHistoryState RenameCategory(string category, string? replacement)
        => this with { Favorites = Favorites.Select(item => item.Category == category ? item with { Category = replacement } : item).ToList() };

    public RepositoryHistoryState Normalize()
    {
        static List<RepositoryHistoryEntry> Clean(IEnumerable<RepositoryHistoryEntry>? entries)
            => (entries ?? []).Where(item => item is not null && !string.IsNullOrEmpty(item.Path) && Path.IsPathFullyQualified(item.Path))
                .GroupBy(item => item.Path, StringComparer.Ordinal).Select(group =>
                {
                    RepositoryHistoryEntry first = group.First();
                    RepositoryHistoryEntry? anchored = group.FirstOrDefault(item => item.EffectiveAnchor != RepositoryHistoryAnchor.None);
                    return anchored is not null && first.EffectiveAnchor == RepositoryHistoryAnchor.None ? first.WithAnchor(anchored.EffectiveAnchor) : first;
                }).ToList();
        RepositoryHistoryState normalized = this with
        {
            Recent = Clean(Recent), Favorites = Clean(Favorites), HistorySize = Math.Clamp(HistorySize, 1, 5000),
            MaxTopRepositories = Math.Clamp(MaxTopRepositories, 0, 1_000_000),
            RecentReposComboMinWidth = RecentReposComboMinWidth <= 0 ? 0 : Math.Clamp(RecentReposComboMinWidth, 30, 800),
            ShorteningStrategy = Enum.IsDefined(ShorteningStrategy) ? ShorteningStrategy : RepositoryPathShorteningStrategy.None
        };
        if (LastVisitedRepositoryPath is not null && (string.IsNullOrEmpty(LastVisitedRepositoryPath)
            || !Path.IsPathFullyQualified(LastVisitedRepositoryPath) || LastVisitedRepositoryPath.Contains('\0')))
        { normalized = normalized with { LastVisitedRepositoryPath = null }; }
        return normalized.TrimRecent();
    }

    internal RepositoryHistoryState RecordLegacyLastVisit()
        => _lastVisitRecorded ? this : this with { LastVisitedRepositoryPath = Recent.FirstOrDefault()?.Path };
}

public sealed class RepositoryHistoryStore
{
    public string FilePath { get; }

    public RepositoryHistoryStore(string? directory = null)
    {
        string preferencesPath = new PreferencesStore(directory).FilePath;
        FilePath = Path.Combine(Path.GetDirectoryName(preferencesPath)!, "repository-history.json");
    }

    public RepositoryHistoryState Load(IEnumerable<string>? legacyRecent = null)
    {
        if (File.Exists(FilePath)) { return (JsonSerializer.Deserialize<RepositoryHistoryState>(File.ReadAllText(FilePath)) ?? new()).Normalize(); }
        return new RepositoryHistoryState { Recent = (legacyRecent ?? []).Select(path => new RepositoryHistoryEntry(path)).ToList() }.Normalize();
    }

    public void Save(RepositoryHistoryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state.Normalize().RecordLegacyLastVisit(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
