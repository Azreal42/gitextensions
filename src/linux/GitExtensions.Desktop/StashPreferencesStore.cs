using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record StashPreferences(bool KeepIndex = false, bool IncludeUntracked = false, bool DontConfirmDrop = false);

internal sealed class StashPreferencesStore
{
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "stash-preferences.json");
    internal StashPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<StashPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(StashPreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, FilePath, overwrite: true); }
        finally { File.Delete(temporary); }
    }
}
