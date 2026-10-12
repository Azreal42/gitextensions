using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record CherryPickPreferences(bool CommitAutomaticallyAfterCherryPick = false, bool AddCommitReferenceToCherryPick = false);

internal sealed class CherryPickPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "cherry-pick-preferences.json");
    internal CherryPickPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<CherryPickPreferences>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(CherryPickPreferences value)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
