using System.Text.Json;
using System.Text.Json.Serialization;
using GitCommands;

namespace GitExtensions.Linux.Desktop;

internal sealed record BrowserLayoutPreferences(CommitInfoPosition CommitInfoPosition = CommitInfoPosition.BelowList);

internal sealed class BrowserLayoutPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "browser-layout-preferences.json");

    internal BrowserLayoutPreferences Load()
    {
        try
        {
            BrowserLayoutPreferences value = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<BrowserLayoutPreferences>(File.ReadAllText(FilePath), Json) ?? new()
                : new();
            return Enum.IsDefined(value.CommitInfoPosition) ? value : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(BrowserLayoutPreferences value)
    {
        if (!Enum.IsDefined(value.CommitInfoPosition)) { throw new ArgumentOutOfRangeException(nameof(value)); }
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
