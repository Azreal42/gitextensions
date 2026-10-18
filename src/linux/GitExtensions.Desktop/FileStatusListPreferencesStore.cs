using System.Collections.Concurrent;
using System.Text.Json;
using GitCommands;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

public sealed class FileStatusListPreferencesStore
{
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.Ordinal);
    private readonly SharedState _state;
    private readonly PreferencesStore _desktop;
    public string FilePath { get; }
    internal event Action<TruncatePathMethod>? PathPresentationChanged
    { add => _state.PathPresentationChanged += value; remove => _state.PathPresentationChanged -= value; }
    public event EventHandler? Changed { add => _state.Changed += value; remove => _state.Changed -= value; }

    public FileStatusListPreferencesStore(string? directory = null)
    {
        _desktop = new PreferencesStore(directory);
        FilePath = Path.Combine(Path.GetDirectoryName(_desktop.FilePath)!, "file-status-list-preferences.json");
        _state = States.GetOrAdd(FilePath, _ => new());
    }

    internal TruncatePathMethod LoadPathPresentation()
    {
        try { return _desktop.Load().TruncatePathMethod; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return TruncatePathMethod.None; }
    }

    // Reuse the existing profile-scoped channel. This is a saved Desktop value,
    // never another field in file-status-list-preferences.json or a new authority.
    internal static void PublishPathPresentation(string desktopPreferencesFilePath, TruncatePathMethod method)
    {
        if (!Enum.IsDefined(method)) { throw new ArgumentOutOfRangeException(nameof(method)); }
        string file = Path.Combine(Path.GetDirectoryName(desktopPreferencesFilePath)!, "file-status-list-preferences.json");
        if (States.TryGetValue(file, out SharedState? state)) { state.NotifyPathPresentation(method); }
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
        internal event Action<TruncatePathMethod>? PathPresentationChanged;
        internal void NotifyPathPresentation(TruncatePathMethod method) => PathPresentationChanged?.Invoke(method);
        internal void Notify(object sender) => Changed?.Invoke(sender, EventArgs.Empty);
    }
}
