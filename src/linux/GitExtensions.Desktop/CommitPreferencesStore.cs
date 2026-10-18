using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

/// <summary>AppSettings defaults used by FormCommit. Signing, author and NoVerify remain session choices.</summary>
public sealed record CommitPreferences
{
    public bool CloseAfterEachCommit { get; init; } = true;
    public bool CloseWhenAllChangesCommitted { get; init; } = true;
    public bool RefreshOnFormFocus { get; init; }
    public bool SelectStagedOnMessageFocus { get; init; } = true;
    public bool ShowOnlyMyMessages { get; init; }
    public bool RememberAmendCommitState { get; init; } = true;
    public bool CommitAndPushForcedWhenAmend { get; init; }
    public bool StageInSuperprojectAfterCommit { get; init; } = true;
    public bool DontConfirmCommitIfNoBranch { get; init; }
    public bool EnsureCommitMessageSecondLineEmpty { get; init; } = true;
    public int NumberOfPreviousMessages { get; init; } = 6;
    public string LastCommitMessage { get; init; } = "";
    public CommitPreferences Normalize() => this with { NumberOfPreviousMessages = Math.Clamp(NumberOfPreviousMessages, 1, 1000), LastCommitMessage = LastCommitMessage ?? "" };
}

public sealed class CommitPreferencesStore
{
    public string FilePath { get; }
    public CommitPreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(root, "gitextensions", "commit-preferences.json");
    }
    public CommitPreferences Load()
    {
        try { return Read(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    // A fresh default must not silently enable parent staging after a profile read failure.
    internal CommitPreferences LoadForSuperprojectStaging() => Read();
    private CommitPreferences Read()
    {
        string json;
        try { json = File.ReadAllText(FilePath); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        return (JsonSerializer.Deserialize<CommitPreferences>(json) ?? throw new JsonException("Commit preferences must contain an object.")).Normalize();
    }
    public void Save(CommitPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
