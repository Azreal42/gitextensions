using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

/// <summary>Explicit saved defaults only. Saving never replaces DesktopFonts' active session.</summary>
internal sealed class FontPreferencesStore
{
    internal string FilePath { get; }
    internal FontPreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(root, "gitextensions", "fonts.json");
    }
    internal FontPreferences Load() => Read().Preferences;

    internal FontPreferences SaveChanges(FontPreferences baseline, FontPreferences draft)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(draft);
        baseline.Validate(); draft.Validate();
        (FontPreferences latest, JsonObject document) = Read();
        FontPreferences merged = latest; bool changed = false;
        foreach (DesktopFontRole role in Enum.GetValues<DesktopFontRole>())
        {
            if (draft.For(role) == baseline.For(role)) { continue; }
            merged = merged.With(role, draft.For(role)); changed = true;
            string key = role.ToString(); JsonObject node = document[key] as JsonObject ?? new();
            node[nameof(FontSpec.Family)] = draft.For(role).Family;
            node[nameof(FontSpec.SizeInPoints)] = draft.For(role).SizeInPoints;
            node[nameof(FontSpec.Bold)] = draft.For(role).Bold; node[nameof(FontSpec.Italic)] = draft.For(role).Italic;
            if (document[key] is null) { document[key] = node; }
        }
        if (draft.ShowEolMarkerAsGlyph != baseline.ShowEolMarkerAsGlyph)
        {
            merged = merged with { ShowEolMarkerAsGlyph = draft.ShowEolMarkerAsGlyph };
            document[nameof(FontPreferences.ShowEolMarkerAsGlyph)] = draft.ShowEolMarkerAsGlyph; changed = true;
        }
        if (!changed) { return latest; }
        merged.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (!OperatingSystem.IsWindows() && File.Exists(FilePath)) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(FilePath)); }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return merged;
    }

    private (FontPreferences Preferences, JsonObject Document) Read()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return (new(), new()); }
        catch (DirectoryNotFoundException) { return (new(), new()); }
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        RequireUniqueObjects(parsed.RootElement);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Font preferences must be a JSON object."); }
        JsonElement root = parsed.RootElement; FontPreferences defaults = new(); FontPreferences value = defaults;
        foreach (DesktopFontRole role in Enum.GetValues<DesktopFontRole>())
        {
            if (!root.TryGetProperty(role.ToString(), out JsonElement font)) { continue; }
            if (font.ValueKind != JsonValueKind.Object) { throw new JsonException(role + " must be a font object."); }
            FontSpec fallback = defaults.For(role);
            string family = font.TryGetProperty(nameof(FontSpec.Family), out JsonElement f) ? String(f, role + ".Family") : fallback.Family;
            double size = font.TryGetProperty(nameof(FontSpec.SizeInPoints), out JsonElement s) ? Number(s, role + ".SizeInPoints") : fallback.SizeInPoints;
            bool bold = font.TryGetProperty(nameof(FontSpec.Bold), out JsonElement b) ? Boolean(b, role + ".Bold") : fallback.Bold;
            bool italic = font.TryGetProperty(nameof(FontSpec.Italic), out JsonElement i) ? Boolean(i, role + ".Italic") : fallback.Italic;
            value = value.With(role, new(family, size, bold, italic));
        }
        if (root.TryGetProperty(nameof(FontPreferences.ShowEolMarkerAsGlyph), out JsonElement glyph))
        { value = value with { ShowEolMarkerAsGlyph = Boolean(glyph, nameof(FontPreferences.ShowEolMarkerAsGlyph)) }; }
        try { value.Validate(); }
        catch (ArgumentException error) { throw new JsonException("Invalid font preferences: " + error.Message, error); }
        return (value, (JsonNode.Parse(bytes) as JsonObject)!);
    }
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.String && value.GetString() is string text
        ? text : throw new JsonException(name + " must be a string.");
    private static double Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
        ? number : throw new JsonException(name + " must be a number.");
    private static bool Boolean(JsonElement value, string name) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new JsonException(name + " must be a boolean.");
    private static void RequireUniqueObjects(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) { throw new JsonException("Duplicate font preference property: " + property.Name); } RequireUniqueObjects(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (JsonElement item in value.EnumerateArray()) { RequireUniqueObjects(item); } }
    }
}
