using System.Collections.Concurrent;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public enum DiffViewerAppearance { Patch, GitWordDiff, Difftastic }

public sealed record DiffViewerPreferences(bool UseGitColoring = true, bool ReverseGitColoring = true,
    DiffViewerAppearance Appearance = DiffViewerAppearance.Patch, bool RememberAppearance = false);

/// <summary>Original diff appearance defaults, with one shared runtime appearance per configuration path.</summary>
public sealed class DiffViewerPreferencesStore
{
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.Ordinal);
    private readonly SharedState _state;
    public string FilePath { get; }
    public event EventHandler? Changed { add => _state.Changed += value; remove => _state.Changed -= value; }

    public DiffViewerPreferencesStore(string? directory = null)
    {
        FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "diff-viewer-preferences.json"));
        _state = States.GetOrAdd(FilePath, _ => new());
    }

    public DiffViewerPreferences Load()
    {
        lock (_state)
        {
            (DiffViewerPreferences persisted, bool valid) = Read();
            if (_state.Current is null || _state.Persisted != persisted || !valid)
            {
                _state.Current = persisted.RememberAppearance ? persisted : persisted with { Appearance = DiffViewerAppearance.Patch };
                _state.Persisted = persisted;
            }
            return _state.Current;
        }
    }

    public DiffViewerPreferences BeginViewerSession()
    {
        DiffViewerPreferences value;
        bool changed;
        lock (_state)
        {
            DiffViewerPreferences current = Load();
            value = current.RememberAppearance ? current : current with { Appearance = _state.Persisted!.Appearance };
            changed = value != current; _state.Current = value;
        }
        if (changed) { _state.Notify(this); }
        return value;
    }

    public bool Save(DiffViewerPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Appearance)) { throw new ArgumentOutOfRangeException(nameof(value)); }
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool changed;
        try
        {
            lock (_state)
            {
                DiffViewerPreferences persisted = value.RememberAppearance ? value : value with { Appearance = DiffViewerAppearance.Patch };
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(persisted));
                File.Move(temporary, FilePath, overwrite: true);
                changed = _state.Current != value;
                _state.Current = value; _state.Persisted = persisted;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
        if (changed) { _state.Notify(this); }
        return true;
    }

    private (DiffViewerPreferences Preferences, bool Valid) Read()
    {
        try
        {
            if (!File.Exists(FilePath)) { return (new(), true); }
            DiffViewerPreferences? value = JsonSerializer.Deserialize<DiffViewerPreferences>(File.ReadAllText(FilePath));
            if (value is null) { return (new(), false); }
            return Enum.IsDefined(value.Appearance) ? (value, true) : (value with { Appearance = DiffViewerAppearance.Patch }, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return (new(), false); }
    }

    private sealed class SharedState
    {
        internal DiffViewerPreferences? Current, Persisted;
        internal event EventHandler? Changed;
        internal void Notify(object sender) => Changed?.Invoke(sender, EventArgs.Empty);
    }
}
