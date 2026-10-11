using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux.Desktop;

public sealed record BlamePreferences
{
    public bool IgnoreWhitespace { get; init; } = true;
    public bool DetectMoves { get; init; }
    public bool DetectCopies { get; init; }
    public bool DisplayAuthorFirst { get; init; }
    public bool ShowAuthorAvatar { get; init; } = true;
    public bool ShowAuthor { get; init; } = true;
    public bool ShowAuthorDate { get; init; } = true;
    public bool ShowAuthorTime { get; init; } = true;
    public bool ShowLineNumbers { get; init; }
    public bool ShowOriginalFilePath { get; init; } = true;
    public bool UseHistogramDiffAlgorithm { get; init; }
    [JsonIgnore]
    public GitFileBlameOptions GitOptions => new(IgnoreWhitespace, DetectMoves, DetectCopies);
    public BlamePreferences Normalize() => !ShowAuthor && !ShowAuthorDate ? this with { ShowAuthorDate = true } : this;
}

public sealed class BlamePreferencesStore
{
    public string FilePath { get; }
    public BlamePreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(root, "gitextensions", "blame-preferences.json");
    }
    public BlamePreferences Load()
    {
        try { return (File.Exists(FilePath) ? JsonSerializer.Deserialize<BlamePreferences>(File.ReadAllText(FilePath)) ?? new() : new()).Normalize(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(BlamePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
