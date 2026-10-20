using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

/// <summary>AppSettings defaults used by FormCommit. Signing, author and NoVerify remain session choices.</summary>
public sealed record CommitPreferences
{
    public bool ShowCommitAndPush { get; init; } = true;
    public bool ShowResetWorkTreeChanges { get; init; } = true;
    public bool ShowResetAllChanges { get; init; } = true;
    public bool CloseAfterEachCommit { get; init; } = true;
    public bool CloseWhenAllChangesCommitted { get; init; } = true;
    public bool RefreshOnFormFocus { get; init; }
    public bool SelectStagedOnMessageFocus { get; init; } = true;
    public bool ShowOnlyMyMessages { get; init; }
    public bool RememberAmendCommitState { get; init; } = true;
    public bool CommitAndPushForcedWhenAmend { get; init; }
    public bool StageInSuperprojectAfterCommit { get; init; } = true;
    public bool DontConfirmCommitIfNoBranch { get; init; }
    public bool DontConfirmAmend { get; init; }
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
            JsonObject document = JsonSerializer.SerializeToNode(preferences.Normalize())!.AsObject();
            try
            {
                if (JsonNode.Parse(File.ReadAllBytes(FilePath)) is JsonObject existing)
                {
                    // Existing workflow writers own their typed values, never a future neighbour's keys.
                    foreach ((string name, JsonNode? value) in existing)
                    { if (!document.ContainsKey(name)) { document[name] = value?.DeepClone(); } }
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (JsonException) { /* Retain explicit typed Save's historical repair contract. Settings uses strict CAS. */ }
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
