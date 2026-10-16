using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record SubmoduleDialogPreferences(double SplitterDistance = 222);

internal sealed class SubmoduleDialogPreferencesStore(string? filePath = null)
{
    internal string FilePath { get; } = filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "submodule-dialog-preferences.json");

    internal SubmoduleDialogPreferences Load()
    {
        try
        {
            SubmoduleDialogPreferences value = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SubmoduleDialogPreferences>(File.ReadAllText(FilePath)) ?? new() : new();
            return double.IsFinite(value.SplitterDistance) && value.SplitterDistance >= 120 ? value : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(SubmoduleDialogPreferences value)
    {
        if (!double.IsFinite(value.SplitterDistance) || value.SplitterDistance < 120) { return; }
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
