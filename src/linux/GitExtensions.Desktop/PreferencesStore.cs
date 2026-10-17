using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed record DesktopPreferences
{
    public List<string> RecentRepositories { get; init; } = [];
    public string Theme { get; init; } = "Light";
    public int HistoryLimit { get; init; } = 300;
    public bool AutoNormaliseBranchName { get; init; } = true;
    public string AutoNormaliseSymbol { get; init; } = "_";
    public bool AlwaysShowAdvancedOptions { get; init; }
    public GitReferenceSorting ReferenceSorting { get; init; } = new();
    public string PrioritizedBranchNames { get; init; } = GitReferencePriorities.DefaultBranchPatterns;
    public string PrioritizedRemoteNames { get; init; } = GitReferencePriorities.DefaultRemotePatterns;
    public Dictionary<string, Dictionary<string, string?>> PluginSettings { get; init; } = [];
}

public sealed class PreferencesStore
{
    public string FilePath { get; }

    public PreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(root, "gitextensions", "preferences.json");
    }

    public DesktopPreferences Load()
    {
        if (!File.Exists(FilePath))
        {
            return new DesktopPreferences();
        }

        DesktopPreferences value = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(FilePath)) ?? new();
        GitReferenceSorting sorting = value.ReferenceSorting ?? new();
        return value with
        {
            ReferenceSorting = new(Enum.IsDefined(sorting.By) ? sorting.By : GitRefsSortBy.GitDefault,
                Enum.IsDefined(sorting.Order) ? sorting.Order : GitRefsSortOrder.Descending),
            PrioritizedBranchNames = value.PrioritizedBranchNames ?? GitReferencePriorities.DefaultBranchPatterns,
            PrioritizedRemoteNames = value.PrioritizedRemoteNames ?? GitReferencePriorities.DefaultRemotePatterns
        };
    }

    public void Save(DesktopPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
