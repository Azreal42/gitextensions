using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record ConflictConfirmationPreferences(bool DontConfirmResolveConflicts = false,
    bool DontConfirmCommitAfterConflictsResolved = false);

internal sealed class ConflictConfirmationPreferencesStore
{
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "conflict-confirmation-preferences.json");
    internal ConflictConfirmationPreferences Load()
    {
        try { return JsonSerializer.Deserialize<ConflictConfirmationPreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
}
