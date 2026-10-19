using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

internal sealed record ConsoleStylePreferences(string Terminal = "Automatic", string Style = "Default", FontSpec? Font = null)
{
    internal static readonly string[] Terminals = ["Automatic", "gnome-terminal", "konsole", "xterm", "x-terminal-emulator"];
    internal static readonly string[] Styles = ["Default", "Light", "Dark"];
    internal void ValidateChanges(ConsoleStylePreferences baseline)
    {
        if (Terminal != baseline.Terminal && !Terminals.Contains(Terminal, StringComparer.Ordinal))
        { throw new ArgumentException("Choose an available Linux terminal."); }
        if (Style != baseline.Style && !Styles.Contains(Style, StringComparer.Ordinal))
        { throw new ArgumentException("Choose Default, Light or Dark console style."); }
        Font?.Validate();
    }
}

/// <summary>One XDG authority. Merge only explicit fields; preserve unknown JSON and never repair corrupt input.</summary>
internal sealed class ConsoleStylePreferencesStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    internal string FilePath { get; }
    internal ConsoleStylePreferencesStore(string? directory = null)
        => FilePath = Path.Combine(directory is null ? Path.GetDirectoryName(new PreferencesStore().FilePath)! : Path.Combine(directory, "gitextensions"), "console-style.json");
    internal ConsoleStylePreferences Load() => Read().Value;

    internal ConsoleStylePreferences SaveChanges(ConsoleStylePreferences baseline, ConsoleStylePreferences draft)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(draft); draft.ValidateChanges(baseline);
        lock (Gates.GetOrAdd(Path.GetFullPath(FilePath), static _ => new()))
        {
            (ConsoleStylePreferences latest, JsonObject json) = Read();
            ConsoleStylePreferences merged = latest; bool changed = false;
            if (draft.Terminal != baseline.Terminal) { merged = merged with { Terminal = draft.Terminal }; json["Terminal"] = draft.Terminal; changed = true; }
            if (draft.Style != baseline.Style) { merged = merged with { Style = draft.Style }; json["Style"] = draft.Style; changed = true; }
            if (draft.Font != baseline.Font)
            {
                merged = merged with { Font = draft.Font }; changed = true;
                if (draft.Font is null) { json["Font"] = null; }
                else
                {
                    JsonObject font = json["Font"] as JsonObject ?? new();
                    font["Family"] = draft.Font.Family; font["SizeInPoints"] = draft.Font.SizeInPoints;
                    font["Bold"] = draft.Font.Bold; font["Italic"] = draft.Font.Italic;
                    if (json["Font"] is null) { json["Font"] = font; }
                }
            }
            if (!changed) { return latest; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, json, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(flushToDisk: true); }
                if (!OperatingSystem.IsWindows() && File.Exists(FilePath)) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(FilePath)); }
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return merged;
        }
    }

    private (ConsoleStylePreferences Value, JsonObject Json) Read()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return (new(), new()); }
        catch (DirectoryNotFoundException) { return (new(), new()); }
        using JsonDocument parsed = JsonDocument.Parse(bytes); Unique(parsed.RootElement);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Console style preferences must be an object."); }
        JsonObject json = JsonNode.Parse(bytes)!.AsObject();
        string terminal = String("Terminal", "Automatic"), style = String("Style", "Default");
        FontSpec? font = null;
        if (json["Font"] is JsonNode node)
        {
            try { font = node.Deserialize<FontSpec>() ?? throw new JsonException("Invalid console font."); font.Validate(); }
            catch (ArgumentException error) { throw new JsonException("Invalid console font.", error); }
        }
        return (new(terminal, style, font), json);
        string String(string key, string fallback)
        {
            if (!parsed.RootElement.TryGetProperty(key, out JsonElement value)) { return fallback; }
            return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text : throw new JsonException(key + " must be a non-empty string.");
        }
    }
    private static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in node.EnumerateObject())
            { if (!keys.Add(property.Name)) { throw new JsonException("Duplicate console preference: " + property.Name); } Unique(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) { foreach (JsonElement value in node.EnumerateArray()) { Unique(value); } }
    }
}
