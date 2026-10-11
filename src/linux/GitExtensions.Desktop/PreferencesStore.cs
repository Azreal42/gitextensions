using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed record DesktopPreferences
{
    public List<string> RecentRepositories { get; init; } = [];
    public string Theme { get; init; } = "Light";
    public int HistoryLimit { get; init; } = 300;
    public bool AutoNormaliseBranchName { get; init; } = true;
    public string AutoNormaliseSymbol { get; init; } = "_";
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

        return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(FilePath)) ?? new DesktopPreferences();
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
