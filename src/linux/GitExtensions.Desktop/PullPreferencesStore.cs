using System.Text.Json;
using System.Text.Json.Serialization;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

internal sealed record PullPreferences(GitPullAction DefaultAction = GitPullAction.Merge,
    GitPullAction DialogAction = GitPullAction.Merge, bool DontConfirmFetchAndPruneAll = false,
    bool AutoStash = false, bool? AutoPopStashAfterPull = null, bool IncludeUntrackedFilesInAutoStash = false);

internal sealed class PullPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "pull-preferences.json");

    internal PullPreferences Load()
    {
        try
        {
            PullPreferences value = new();
            bool hasIncludeUntracked = false;
            if (File.Exists(FilePath))
            {
                using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(FilePath));
                value = profile.RootElement.Deserialize<PullPreferences>(Json) ?? new();
                hasIncludeUntracked = profile.RootElement.ValueKind == JsonValueKind.Object
                    && profile.RootElement.TryGetProperty(nameof(PullPreferences.IncludeUntrackedFilesInAutoStash), out _);
            }
            // This used to be a checkout-only choice. Honor only an explicitly
            // saved legacy value; an untouched profile now uses Windows' false.
            if (!hasIncludeUntracked)
            { value = value with { IncludeUntrackedFilesInAutoStash = ReadLegacyIncludeUntracked() }; }
            return value with
            {
                DefaultAction = IsToolbarAction(value.DefaultAction) ? value.DefaultAction : GitPullAction.Merge,
                DialogAction = value.DialogAction is GitPullAction.Merge or GitPullAction.Rebase or GitPullAction.Fetch ? value.DialogAction : GitPullAction.Merge
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal static bool IsToolbarAction(GitPullAction action) => action is >= GitPullAction.None and <= GitPullAction.FetchPruneAll;

    private static bool ReadLegacyIncludeUntracked()
    {
        string legacy = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "reference-preferences.json");
        try
        {
            if (!File.Exists(legacy)) { return false; }
            using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(legacy));
            return profile.RootElement.ValueKind == JsonValueKind.Object
                && profile.RootElement.TryGetProperty(nameof(ReferencePreferences.IncludeUntracked), out JsonElement value)
                && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal void Save(PullPreferences value)
    {
        if (!IsToolbarAction(value.DefaultAction) || value.DialogAction is not (GitPullAction.Merge or GitPullAction.Rebase or GitPullAction.Fetch))
        { throw new ArgumentOutOfRangeException(nameof(value)); }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, FilePath, overwrite: true); }
        finally { File.Delete(temporary); }
    }
}
