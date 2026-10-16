using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed record BrowseCommandPreferences(bool DontConfirmUndoLastCommit = false, bool DontConfirmSwitchWorktree = false);

public sealed class BrowseCommandPreferencesStore
{
    public string FilePath { get; }
    public BrowseCommandPreferencesStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "browse-command-preferences.json");
    public BrowseCommandPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<BrowseCommandPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(BrowseCommandPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences)); File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
