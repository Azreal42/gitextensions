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
            PullPreferences value = File.Exists(FilePath) ? JsonSerializer.Deserialize<PullPreferences>(File.ReadAllText(FilePath), Json) ?? new() : new();
            return value with
            {
                DefaultAction = IsToolbarAction(value.DefaultAction) ? value.DefaultAction : GitPullAction.Merge,
                DialogAction = value.DialogAction is GitPullAction.Merge or GitPullAction.Rebase or GitPullAction.Fetch ? value.DialogAction : GitPullAction.Merge
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal static bool IsToolbarAction(GitPullAction action) => action is >= GitPullAction.None and <= GitPullAction.FetchPruneAll;

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
