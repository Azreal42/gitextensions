using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux.Desktop;

public sealed record FileHistoryPreferences
{
    public bool FollowRenames { get; init; } = true;
    public bool ExactRenames { get; init; }
    public bool FullHistory { get; init; }
    public bool SimplifyMerges { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }

    // Keep requested settings even when inactive. Both Core graph readers own
    // the single Windows policy for emitting traversal flags and rewriting parents.
    internal GitLogQuery ConfigureQuery(GitLogQuery query) => query with
    { FullHistory = FullHistory, SimplifyMerges = SimplifyMerges };
}

public sealed class FileHistoryPreferencesStore
{
    public string FilePath { get; }
    public FileHistoryPreferencesStore(string? directory = null)
        => FilePath = Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "file-history-preferences.json");

    public FileHistoryPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<FileHistoryPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(FileHistoryPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
