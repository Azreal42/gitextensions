using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record ClonePreferences(string Destination = "", bool InitializeSubmodules = true);

internal sealed class ClonePreferencesStore
{
    private readonly string _file = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "clone-preferences.json");

    internal ClonePreferences Load()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<ClonePreferences>(File.ReadAllText(_file)) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(ClonePreferences preferences)
    {
        string temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
