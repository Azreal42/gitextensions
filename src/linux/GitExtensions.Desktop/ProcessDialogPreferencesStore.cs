using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed record ProcessDialogPreferences(bool CloseProcessDialog = false);

public sealed class ProcessDialogPreferencesStore
{
    public string FilePath { get; }
    public ProcessDialogPreferencesStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "process-dialog-preferences.json");
    public ProcessDialogPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ProcessDialogPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(ProcessDialogPreferences preferences)
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
