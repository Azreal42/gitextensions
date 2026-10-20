using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux.Desktop;

public sealed record BlamePreferences
{
    public bool IgnoreWhitespace { get; init; } = true;
    public bool DetectMoves { get; init; }
    public bool DetectCopies { get; init; }
    public bool DisplayAuthorFirst { get; init; }
    public bool ShowAuthorAvatar { get; init; } = true;
    public bool ShowAuthor { get; init; } = true;
    public bool ShowAuthorDate { get; init; } = true;
    public bool ShowAuthorTime { get; init; } = true;
    public bool ShowLineNumbers { get; init; }
    public bool ShowOriginalFilePath { get; init; } = true;
    public bool UseHistogramDiffAlgorithm { get; init; }
    [JsonIgnore]
    public GitFileBlameOptions GitOptions => new(IgnoreWhitespace, DetectMoves, DetectCopies);
    public BlamePreferences Normalize() => !ShowAuthor && !ShowAuthorDate ? this with { ShowAuthorDate = true } : this;
}

internal sealed record BlameSettingsSaveReceipt(BlamePreferences Values, IReadOnlyList<string> Fields, bool Written);

public sealed class BlamePreferencesStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);
    public string FilePath { get; }
    public BlamePreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.GetFullPath(Path.Combine(root, "gitextensions", "blame-preferences.json"));
    }
    public BlamePreferences Load()
    {
        try { return ReadSettings(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    // Existing callers of full Save retain their author/date normalization contract.
    // Settings and individual menu/General intentions use the strict field merge below.
    public void Save(BlamePreferences preferences)
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

    internal BlamePreferences ReadSettings() => Read().Values;

    internal BlameSettingsSaveReceipt SaveChanges(BlamePreferences baseline, BlamePreferences draft,
        IReadOnlyList<string> fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(fields);
        cancellationToken.ThrowIfCancellationRequested();
        string[] edited = fields.Distinct(StringComparer.Ordinal).Where(name =>
            BlameViewerSettingsCatalog.Get(baseline, name) != BlameViewerSettingsCatalog.Get(draft, name)).ToArray();
        if (edited.Length == 0) { return new(baseline, [], false); }
        lock (Locks.GetOrAdd(FilePath, _ => new()))
        {
            Snapshot current = Read(); BlamePreferences values = current.Values; bool written = false;
            foreach (string name in edited)
            {
                bool actual = BlameViewerSettingsCatalog.Get(values, name), wanted = BlameViewerSettingsCatalog.Get(draft, name);
                if (actual != BlameViewerSettingsCatalog.Get(baseline, name) && actual != wanted)
                { throw new IOException("Blame preferences changed after this choice was opened."); }
                if (actual == wanted) { continue; }
                current.Document[name] = JsonValue.Create(wanted); values = BlameViewerSettingsCatalog.With(values, name, wanted); written = true;
            }
            if (written) { Publish(current, cancellationToken); }
            return new(values, Array.AsReadOnly(edited), written);
        }
    }

    private sealed record Snapshot(bool Existed, byte[] Bytes, JsonObject Document, BlamePreferences Values);
    private Snapshot Read()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return new(false, [], new(), new()); }
        catch (DirectoryNotFoundException) { return new(false, [], new(), new()); }
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Blame preferences must be an object."); }
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
        { if (!names.Add(property.Name)) { throw new JsonException("Duplicate blame preference: " + property.Name); } }
        JsonObject document = JsonNode.Parse(bytes)!.AsObject(); BlamePreferences values = new();
        foreach (string name in BlameViewerSettingsCatalog.AllPreferenceNames)
        {
            if (!document.TryGetPropertyValue(name, out JsonNode? node)) { continue; }
            if (node is not JsonValue scalar || !scalar.TryGetValue(out bool boolean))
            { throw new JsonException("Invalid blame preference: " + name); }
            values = BlameViewerSettingsCatalog.With(values, name, boolean);
        }
        // Do not infer another true field when the Windows page explicitly saved both author/date false.
        return new(true, bytes, document, values);
    }

    private void Publish(Snapshot expected, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(expected.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); token.ThrowIfCancellationRequested();
            File.WriteAllBytes(temporary, bytes);
            if (!OperatingSystem.IsWindows() && expected.Existed) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(FilePath)); }
            Snapshot latest = Read();
            if (latest.Existed != expected.Existed || !latest.Bytes.AsSpan().SequenceEqual(expected.Bytes))
            { throw new IOException("Blame preferences changed while saving. Reopen Settings before retrying."); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
