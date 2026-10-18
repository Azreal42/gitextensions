using System.Text.Json;
using System.Collections.Concurrent;

namespace GitExtensions.Linux.Desktop;

internal sealed record MergePreferences(bool NoCommit = false, bool NoFastForward = false, bool AddLog = false, int LogCount = 20);
internal sealed class MergePreferencesStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private object Gate => Gates.GetOrAdd(Path.GetFullPath(FilePath), static _ => new());
    private sealed record Settings(bool NoCommit, Dictionary<string, MergePreferences> Repositories, HashSet<string>? LogPreferencesMigrated = null);
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
        lock (Gate)
        {
            Settings settings = Read();
            Dictionary<string, MergePreferences> repositories = settings.Repositories ?? [];
            repositories[repository] = value with { LogCount = Math.Clamp(value.LogCount, 0, 100) };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(value.NoCommit, repositories, settings.LogPreferencesMigrated))); File.Move(temporary, FilePath, true); }
            finally { File.Delete(temporary); }

        }
    }
    internal MergePreferences? LegacyLog(string repository)
    {
        Settings settings = ReadStrict();
        return settings.LogPreferencesMigrated?.Contains(repository) == true ? null : settings.Repositories?.GetValueOrDefault(repository);
    }
    internal void MarkLogMigrated(string repository)
    {
        lock (Gate)
        {
            Settings settings = ReadStrict();
            HashSet<string> migrated = settings.LogPreferencesMigrated ?? new(StringComparer.Ordinal);
            if (migrated.Add(repository)) { WriteStrict(settings with { LogPreferencesMigrated = migrated }); }

        }
    }
    internal void SaveWorkflow(string repository, bool noCommit, bool noFastForward)
    {
        lock (Gate)
        {
            Settings settings = ReadStrict();
            Dictionary<string, MergePreferences> repositories = settings.Repositories ?? [];
            bool existed = repositories.TryGetValue(repository, out MergePreferences? previous);
            repositories[repository] = (previous ?? new()) with { NoCommit = noCommit, NoFastForward = noFastForward };
            HashSet<string> migrated = settings.LogPreferencesMigrated ?? new(StringComparer.Ordinal);
            // New workflow entries are not historical AddLog/LogCount import candidates.
            if (!existed) { migrated.Add(repository); }
            WriteStrict(settings with { NoCommit = noCommit, Repositories = repositories, LogPreferencesMigrated = migrated });

        }
    }
    private Settings ReadStrict()
    {
        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (FileNotFoundException) { return new(false, []); }
        catch (DirectoryNotFoundException) { return new(false, []); }
        return JsonSerializer.Deserialize<Settings>(text) ?? throw new JsonException("Invalid merge preferences profile: " + FilePath);
    }
    private void WriteStrict(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, FilePath, true); }
        finally { File.Delete(temporary); }
    }

}
