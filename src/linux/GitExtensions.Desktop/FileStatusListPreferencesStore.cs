using System.Collections.Concurrent;
using System.Text.Json;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

public sealed class FileStatusListPreferencesStore
{
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.Ordinal);
    private readonly SharedState _state;
    public string FilePath { get; }
    public event EventHandler? Changed { add => _state.Changed += value; remove => _state.Changed -= value; }

    public FileStatusListPreferencesStore(string? directory = null)
    {
        FilePath = Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "file-status-list-preferences.json");
        _state = States.GetOrAdd(FilePath, _ => new());
    }

    public FileStatusListOptions Load()
    {
        try
        {
            FileStatusListOptions value = File.Exists(FilePath) ? JsonSerializer.Deserialize<FileStatusListOptions>(File.ReadAllText(FilePath)) ?? new() : new();
            return Enum.IsDefined(value.SortMode) ? value with { HiddenToolbarItems = value.HiddenToolbarItems ?? [] } : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public bool Save(FileStatusListOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_state)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(value));
                File.Move(temporary, FilePath, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
        _state.Notify(this);
        return true;
    }

    private sealed class SharedState
    {
        internal event EventHandler? Changed;
        internal void Notify(object sender) => Changed?.Invoke(sender, EventArgs.Empty);
    }
}
