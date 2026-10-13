using System.Text.Json;

namespace GitExtensions.Linux.Desktop.Controls;

internal sealed class OriginalCommandHelpPreferencesStore
{
    private readonly string _file = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "command-help-preferences.json");
    internal bool Load(string id) => id.Length == 0 || !Read().TryGetValue(id, out bool expanded) || expanded;
    private Dictionary<string, bool> Read() => File.Exists(_file)
        ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_file)) ?? new(StringComparer.Ordinal) : new(StringComparer.Ordinal);
    internal void Save(string id, bool expanded)
    {
        Dictionary<string, bool> values = Read(); values[id] = expanded;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(values)); File.Move(temporary, _file, overwrite: true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
