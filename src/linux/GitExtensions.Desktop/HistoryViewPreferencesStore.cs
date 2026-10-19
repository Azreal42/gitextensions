using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record HistoryViewPreferences
{
    public bool ShowMainToolbar { get; init; } = true;
    public bool ShowFilterToolbar { get; init; } = true;
    public bool ShowScriptsToolbar { get; init; } = true;
    public string[] HiddenToolbarItems { get; init; } = [];
    public bool ShowArtificialCommits { get; init; } = true;
    public bool ShowStashes { get; init; } = true;
    public bool ShowSessionCheckpoints { get; init; }
    public bool ShowRemoteBranches { get; init; } = true;
    public bool ShowTags { get; init; } = true;
    public bool ShowNotes { get; init; }
    public bool ShowNotesColumn { get; init; }
    public bool ShowBuildStatusIcon { get; init; } = true;
    public bool ShowBuildStatusText { get; init; }
    public bool ShowBody { get; init; } = true;
    public bool ShowAuthorDate { get; init; } = true;
    public bool RelativeDate { get; init; } = true;
    public bool ShowGraph { get; init; } = true;
    public bool ShowAvatar { get; init; } = true;
    public bool ShowAuthor { get; init; } = true;
    public bool ShowDate { get; init; } = true;
    public bool ShowId { get; init; } = true;
    public bool DrawNonRelativesGray { get; init; } = true;
    public bool ShowReflog { get; init; }
    public GitLogSort Sort { get; init; } = GitLogSort.GitDefault;
}

internal sealed class HistoryViewPreferencesStore
{
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "history-view-preferences.json");
    internal HistoryViewPreferences Load()
    {
        try
        {
            HistoryViewPreferences value = File.Exists(FilePath) ? JsonSerializer.Deserialize<HistoryViewPreferences>(File.ReadAllText(FilePath)) ?? new() : new();
            return value with { HiddenToolbarItems = value.HiddenToolbarItems ?? [], Sort = Enum.IsDefined(value.Sort) ? value.Sort : GitLogSort.GitDefault };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(HistoryViewPreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, FilePath, overwrite: true); }
        finally { File.Delete(temporary); }
    }
}
