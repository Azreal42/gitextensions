using System.Text.Json;
using System.Text.Json.Nodes;

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
    public bool DrawAlternateBackground { get; init; } = true;
    public bool DrawNonRelativesTextGray { get; init; }
    public bool HighlightAuthoredRevisions { get; init; } = true;
    public bool FillRefLabels { get; init; }
    public bool ShowReflog { get; init; }
    public GitLogSort Sort { get; init; } = GitLogSort.GitDefault;
}

internal sealed class HistoryViewPreferencesStore
{
    internal string FilePath { get; }
    internal HistoryViewPreferencesStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "history-view-preferences.json");
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
    internal ColorsSettingsValues ReadColors()
    {
        var current = ReadDocument();
        return ColorsSettingsValues.From(Decode(current.Document));
    }

    /// <summary>Colors edits only explicit controls, against their saved baseline rather than a temporary View.</summary>
    internal bool SaveColors(ColorsSettingsValues savedBaseline, ColorsSettingsValues draft,
        IReadOnlyList<ColorsSetting> edited, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(savedBaseline); ArgumentNullException.ThrowIfNull(draft);
        token.ThrowIfCancellationRequested(); if (edited.Count == 0) { return false; }
        var snapshot = ReadDocument(); ColorsSettingsValues current = ColorsSettingsValues.From(Decode(snapshot.Document));
        bool written = false;
        foreach (ColorsSetting choice in edited)
        {
            bool wanted = draft.Get(choice.Name), actual = current.Get(choice.Name);
            if (actual != savedBaseline.Get(choice.Name) && actual != wanted)
            { throw new IOException("History view preferences changed after Settings opened."); }
            if (actual == wanted) { continue; }
            snapshot.Document[choice.Name] = JsonValue.Create(wanted); written = true;
        }
        if (written) { Publish(snapshot.Existed, snapshot.Bytes, snapshot.Document, token); }
        return written;
    }

    /// <summary>An explicit Save View saves other View fields, but never republishes stale Colors flags.</summary>
    internal HistoryViewPreferences SaveCurrentViewDefaults(HistoryViewPreferences active, HistoryGraphGrayIntent? graphGray,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(active); token.ThrowIfCancellationRequested();
        var snapshot = ReadDocument(); HistoryViewPreferences current = Decode(snapshot.Document);
        if (graphGray is not null && (graphGray.SavedBaseline is null
            || current.DrawNonRelativesGray != graphGray.SavedBaseline && current.DrawNonRelativesGray != graphGray.Value))
        { throw new IOException("The graph gray default changed after the View choice. Reopen Settings before saving it."); }
        JsonObject previous = (JsonObject)snapshot.Document.DeepClone();
        JsonObject desired = JsonSerializer.SerializeToNode(active)!.AsObject();
        foreach ((string name, JsonNode? value) in desired)
        {
            if (!ColorsSettingsCatalog.Fields.Any(choice => choice.Name == name))
            { snapshot.Document[name] = value?.DeepClone(); }
        }
        if (graphGray is not null) { snapshot.Document[nameof(HistoryViewPreferences.DrawNonRelativesGray)] = JsonValue.Create(graphGray.Value); }
        if (!JsonNode.DeepEquals(previous, snapshot.Document)) { Publish(snapshot.Existed, snapshot.Bytes, snapshot.Document, token); }
        return Decode(snapshot.Document);
    }

    internal HistoryViewPreferences ReadAppearanceBaseline() => Decode(ReadDocument().Document);

    /// <summary>Only Appearance's three explicit View intentions may change; retain every other JSON node.</summary>
    internal bool SaveAppearance(HistoryViewPreferences savedBaseline, HistoryViewPreferences draft,
        IReadOnlyList<string> edited, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(savedBaseline); ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(edited); token.ThrowIfCancellationRequested();
        if (edited.Count == 0) { return false; }
        // Validate every requested field before any IO or mutation. This is not a general typed Save replacement.
        foreach (string name in edited) { _ = AppearanceValue(draft, name); }
        var snapshot = ReadDocument(); HistoryViewPreferences current = Decode(snapshot.Document);
        bool written = false;
        foreach (string name in edited)
        {
            object wanted = AppearanceValue(draft, name), actual = AppearanceValue(current, name);
            if (!Equals(actual, AppearanceValue(savedBaseline, name)) && !Equals(actual, wanted))
            { throw new IOException("History view preferences changed after Appearance opened."); }
            if (Equals(actual, wanted)) { continue; }
            snapshot.Document[name] = name == nameof(HistoryViewPreferences.Sort)
                ? JsonValue.Create((int)draft.Sort) : JsonValue.Create((bool)wanted);
            written = true;
        }
        if (written) { Publish(snapshot.Existed, snapshot.Bytes, snapshot.Document, token); }
        return written;
    }

    private static object AppearanceValue(HistoryViewPreferences value, string name) => name switch
    {
        nameof(HistoryViewPreferences.RelativeDate) => value.RelativeDate,
        nameof(HistoryViewPreferences.ShowAvatar) => value.ShowAvatar,
        nameof(HistoryViewPreferences.Sort) when Enum.IsDefined(value.Sort) => value.Sort,
        _ => throw new ArgumentException("Only RelativeDate, ShowAvatar and a valid Sort are Appearance intentions.", nameof(name))
    };

    private static HistoryViewPreferences Decode(JsonObject document)
        => document.Deserialize<HistoryViewPreferences>() ?? throw new JsonException("Invalid history view preferences.");

    private void Publish(bool existed, byte[] before, JsonObject document, CancellationToken token)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var comparison = ReadDocument();
            if (comparison.Existed != existed || !comparison.Bytes.AsSpan().SequenceEqual(before))
            { throw new IOException("History view preferences changed while saving."); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }

    private (bool Existed, byte[] Bytes, JsonObject Document) ReadDocument()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return (false, [], new()); }
        catch (DirectoryNotFoundException) { return (false, [], new()); }
        return (true, bytes, JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("History view preferences must be an object."));
    }
}
