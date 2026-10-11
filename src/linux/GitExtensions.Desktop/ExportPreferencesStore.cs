using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record ExportPreferences(string? LastFormatPatchDirectory = null, bool ApplyIgnoreWhitespace = false, bool ApplySignOff = false);

internal sealed class ExportPreferencesStore
{
    private readonly string _file = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "export-preferences.json");

    internal ExportPreferences Load()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<ExportPreferences>(File.ReadAllText(_file)) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(ExportPreferences preferences)
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
