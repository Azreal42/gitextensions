using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

/// <summary>Merge only edited fields/slots; preserve unknown JSON. Conflicting edits are reported, never silently lost.</summary>
public sealed class CommitMessagePreferencesStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    public string FilePath { get; }
    public static event Action<string>? Saved;
    public CommitMessagePreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.GetFullPath(Path.Combine(root, "gitextensions", "commit-message-preferences.json"));
    }
    public CommitMessagePreferences Load() => Read().Preferences;

    public CommitMessagePreferences SaveChanges(CommitMessagePreferences baseline, CommitMessagePreferences draft)
    {
        baseline = baseline.Snapshot(); draft = draft.Snapshot();
        lock (Gates.GetOrAdd(FilePath, _ => new()))
        {
            var current = Read(); CommitMessagePreferences latest = current.Preferences;
            if (draft.SameAs(baseline)) { return latest; }
            JsonObject document = current.Document;
            T Merge<T>(string key, T before, T desired, T actual)
            {
                if (EqualityComparer<T>.Default.Equals(before, desired)) { return actual; }
                if (!EqualityComparer<T>.Default.Equals(actual, before) && !EqualityComparer<T>.Default.Equals(actual, desired))
                { throw new InvalidOperationException("Commit message settings changed in another window: " + key); }
                document[key] = JsonSerializer.SerializeToNode(desired); return desired;
            }
            CommitMessagePreferences merged = latest with
            {
                MaxFirstLineLength = Merge(nameof(draft.MaxFirstLineLength), baseline.MaxFirstLineLength, draft.MaxFirstLineLength, latest.MaxFirstLineLength),
                MaxLineLength = Merge(nameof(draft.MaxLineLength), baseline.MaxLineLength, draft.MaxLineLength, latest.MaxLineLength),
                AutoWrap = Merge(nameof(draft.AutoWrap), baseline.AutoWrap, draft.AutoWrap, latest.AutoWrap),
                ValidationRegex = Merge(nameof(draft.ValidationRegex), baseline.ValidationRegex, draft.ValidationRegex, latest.ValidationRegex),
                IndentAfterFirstLine = Merge(nameof(draft.IndentAfterFirstLine), baseline.IndentAfterFirstLine, draft.IndentAfterFirstLine, latest.IndentAfterFirstLine),
                SecondLineMustBeEmpty = Merge(nameof(draft.SecondLineMustBeEmpty), baseline.SecondLineMustBeEmpty, draft.SecondLineMustBeEmpty, latest.SecondLineMustBeEmpty)
            };
            JsonArray slots = document[nameof(draft.Templates)] as JsonArray ?? new();
            List<CommitMessageTemplate> templates = latest.Templates.ToList();
            for (int i = 0; i < Math.Max(baseline.Templates.Count, draft.Templates.Count); i++)
            {
                CommitMessageTemplate before = i < baseline.Templates.Count ? baseline.Templates[i] : new();
                CommitMessageTemplate desired = i < draft.Templates.Count ? draft.Templates[i] : before;
                if (before == desired) { continue; }
                while (templates.Count <= i) { templates.Add(new()); }
                while (slots.Count <= i) { slots.Add(new JsonObject()); }
                JsonObject slot = (JsonObject)slots[i]!;
                T Field<T>(string key, T oldValue, T newValue, T actual)
                {
                    if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) { return actual; }
                    if (!EqualityComparer<T>.Default.Equals(oldValue, actual) && !EqualityComparer<T>.Default.Equals(newValue, actual))
                    { throw new InvalidOperationException($"Commit template {i + 1} changed in another window: {key}"); }
                    slot[key] = JsonSerializer.SerializeToNode(newValue); return newValue;
                }
                CommitMessageTemplate value = templates[i];
                templates[i] = new(Field(nameof(value.Name), before.Name, desired.Name, value.Name),
                    Field(nameof(value.Text), before.Text, desired.Text, value.Text), Field(nameof(value.IsRegex), before.IsRegex, desired.IsRegex, value.IsRegex));
            }
            if (document[nameof(draft.Templates)] is null) { document[nameof(draft.Templates)] = slots; }
            merged = merged with { Templates = Array.AsReadOnly(templates.ToArray()) }; merged.Validate();
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                    stream.Write(data); stream.Flush(flushToDisk: true);
                }
                byte[]? observed = ReadBytes();
                if ((observed is null) != (current.Bytes is null) || observed is not null && !observed.AsSpan().SequenceEqual(current.Bytes))
                { throw new InvalidOperationException("Commit message settings changed before publication. Reopen the settings."); }
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
            Saved?.Invoke(FilePath);
            return merged;
        }
    }

    private byte[]? ReadBytes()
    {
        try { return File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private (CommitMessagePreferences Preferences, JsonObject Document, byte[]? Bytes) Read()
    {
        byte[]? bytes = ReadBytes(); if (bytes is null) { return (new(), new(), null); }
        using JsonDocument parsed = JsonDocument.Parse(bytes); Unique(parsed.RootElement);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Commit message settings must be a JSON object."); }
        JsonElement root = parsed.RootElement;
        string Text(JsonElement obj, string key, string fallback) => obj.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("Invalid string: " + key) : fallback;
        bool Flag(JsonElement obj, string key, bool fallback) => obj.TryGetProperty(key, out var value)
            ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new JsonException("Invalid boolean: " + key) : fallback;
        int Number(string key) => root.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= 0 ? number : throw new JsonException("Invalid nonnegative limit: " + key) : 0;
        List<CommitMessageTemplate> templates = [];
        if (root.TryGetProperty("Templates", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array) { throw new JsonException("Templates must be an array."); }
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { throw new JsonException("A commit template must be an object."); }
                templates.Add(new(Text(item, "Name", ""), Text(item, "Text", ""), Flag(item, "IsRegex", false)));
            }
        }
        var preferences = new CommitMessagePreferences
        {
            Templates = templates, MaxFirstLineLength = Number("MaxFirstLineLength"), MaxLineLength = Number("MaxLineLength"),
            AutoWrap = Flag(root, "AutoWrap", true), ValidationRegex = Text(root, "ValidationRegex", ""),
            IndentAfterFirstLine = Flag(root, "IndentAfterFirstLine", true), SecondLineMustBeEmpty = Flag(root, "SecondLineMustBeEmpty", false)
        };
        return (preferences.Snapshot(), (JsonObject)JsonNode.Parse(bytes)!, bytes);
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) { throw new JsonException("Duplicate preference property: " + property.Name); } Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (var child in value.EnumerateArray()) { Unique(child); } }
    }
}
