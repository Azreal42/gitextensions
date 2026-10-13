using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record MergePreferences(bool NoCommit = false, bool NoFastForward = false, bool AddLog = false, int LogCount = 20);
internal sealed class MergePreferencesStore
{
    private sealed record Settings(bool NoCommit, Dictionary<string, MergePreferences> Repositories);
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "merge-preferences.json");
    private Settings Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(false, []) : new(false, []); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(false, []); }
    }
    internal MergePreferences Load(string repository)
    {
        Settings settings = Read();
        MergePreferences value = settings.Repositories?.GetValueOrDefault(repository) ?? new();
        return value with { NoCommit = settings.NoCommit, LogCount = Math.Clamp(value.LogCount, 0, 100) };
    }
    internal void Save(string repository, MergePreferences value)
    {
        Settings settings = Read();
        Dictionary<string, MergePreferences> repositories = settings.Repositories ?? [];
        repositories[repository] = value with { LogCount = Math.Clamp(value.LogCount, 0, 100) };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(value.NoCommit, repositories))); File.Move(temporary, FilePath, true); }
        finally { File.Delete(temporary); }
    }
}
