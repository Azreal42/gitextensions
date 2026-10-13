using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record RebasePreferences(bool AutoStash = false, bool DontConfirmRebase = false);

/// <summary>FormRebase remembers only Git Extensions' Auto stash choice, independently of Git configuration.</summary>
internal sealed class RebasePreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "rebase-preferences.json");

    internal RebasePreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<RebasePreferences>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(RebasePreferences preferences)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Json));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
