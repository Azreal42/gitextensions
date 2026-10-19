using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original GitHub plugin group remains the only authority; this editor changes only its explicit keys.</summary>
internal sealed class GitHubHostingPreferencesStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    internal string FilePath { get; }
    internal GitHubHostingPreferencesStore(string? directory = null) => FilePath = new PreferencesStore(directory).FilePath;
    internal IReadOnlyDictionary<string, string?> Load() => Read().Values;

    internal IReadOnlyDictionary<string, string?> SaveChanges(IReadOnlyDictionary<string, string?> baseline, IReadOnlyDictionary<string, string?> draft)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(draft);
        lock (Gates.GetOrAdd(Path.GetFullPath(FilePath), static _ => new()))
        {
            var (values, root, bytes) = Read();
            string[] changes = baseline.Keys.Union(draft.Keys).Where(key =>
                baseline.ContainsKey(key) != draft.ContainsKey(key) || baseline.GetValueOrDefault(key) != draft.GetValueOrDefault(key)).ToArray();
            if (changes.Length == 0) { return values; }
            foreach (string key in changes)
            {
                if ((values.ContainsKey(key) != baseline.ContainsKey(key) || values.GetValueOrDefault(key) != baseline.GetValueOrDefault(key))
                    && (values.ContainsKey(key) != draft.ContainsKey(key) || values.GetValueOrDefault(key) != draft.GetValueOrDefault(key)))
                { throw new InvalidOperationException("GitHub settings changed in another window. Reopen the settings before saving."); }
            }
            JsonObject plugins = root["PluginSettings"] as JsonObject ?? new();
            if (root["PluginSettings"] is null) { root["PluginSettings"] = plugins; }
            JsonObject group = plugins[GitHubHostingSettings.PluginId] as JsonObject ?? new();
            if (plugins[GitHubHostingSettings.PluginId] is null) { plugins[GitHubHostingSettings.PluginId] = group; }
            Dictionary<string, string?> merged = new(values, StringComparer.Ordinal);
            foreach (string key in changes)
            {
                if (draft.TryGetValue(key, out string? value)) { merged[key] = value; group[key] = value; }
                else { merged.Remove(key); group.Remove(key); }
            }
            GitHubHostingSettings settings = GitHubHostingSettings.FromPluginSettings(merged);
            _ = settings.CredentialsFromPluginSettings(merged);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                using (FileStream stream = new(temporary, options))
                { JsonSerializer.Serialize(stream, root, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(flushToDisk: true); }
                byte[]? current = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
                if (!(current is null && bytes is null || current is not null && bytes is not null && current.AsSpan().SequenceEqual(bytes)))
                { throw new InvalidOperationException("Preferences changed while saving GitHub settings. Reopen the settings before saving."); }
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return merged;
        }
    }

    private (IReadOnlyDictionary<string, string?> Values, JsonObject Root, byte[]? Bytes) Read()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return (new Dictionary<string, string?>(), new(), null); }
        catch (DirectoryNotFoundException) { return (new Dictionary<string, string?>(), new(), null); }
        using JsonDocument document = JsonDocument.Parse(bytes); Unique(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Preferences must be an object."); }
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("PluginSettings", out JsonElement plugins))
        {
            if (plugins.ValueKind != JsonValueKind.Object) { throw new JsonException("PluginSettings must be an object."); }
            if (plugins.TryGetProperty(GitHubHostingSettings.PluginId, out JsonElement group))
            {
                if (group.ValueKind != JsonValueKind.Object) { throw new JsonException("GitHub settings must be an object."); }
                foreach (JsonProperty pair in group.EnumerateObject())
                {
                    if (pair.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) { throw new JsonException("GitHub settings must contain strings or null."); }
                    values.Add(pair.Name, pair.Value.GetString());
                }
            }
        }
        return (values, JsonNode.Parse(bytes)!.AsObject(), bytes);
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty pair in value.EnumerateObject())
            { if (!names.Add(pair.Name)) { throw new JsonException("Preferences contain a duplicate property."); } Unique(pair.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (JsonElement item in value.EnumerateArray()) { Unique(item); } }
    }
}
