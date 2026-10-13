using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed record ResetBranchPreferences(bool CheckoutOtherBranchAfterReset = true);

public sealed class ResetBranchPreferencesStore
{
    public string FilePath { get; }
    public ResetBranchPreferencesStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "reset-branch-preferences.json");
    public ResetBranchPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ResetBranchPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(ResetBranchPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(temporary, JsonSerializer.Serialize(preferences)); File.Move(temporary, FilePath, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
