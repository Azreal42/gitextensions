using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

internal sealed record ConfirmationsSaveReceipt(string Profile, ConfirmationsSettingsValues Values,
    IReadOnlyList<ConfirmationSetting> Fields, bool Written);

/// <summary>Global intentions over the existing workflow profiles, acknowledged one file at a time.</summary>
internal sealed class ConfirmationsSettingsSession
{
    private readonly string _directory;
    private readonly Dictionary<string, Exception> _errors = new(StringComparer.Ordinal);
    internal ConfirmationsSettingsValues Initial { get; private set; } = new();
    internal IReadOnlyDictionary<string, Exception> LoadErrors => _errors;

    internal ConfirmationsSettingsSession(string? profileDirectory = null)
    {
        _directory = profileDirectory ?? Path.GetDirectoryName(new PreferencesStore().FilePath)!;
        foreach (IGrouping<string, ConfirmationSetting> profile in ConfirmationSettingsCatalog.Fields.GroupBy(field => field.Profile))
        {
            try { Initial = ReadValues(Read(Path.Combine(_directory, profile.Key)).Document, profile, Initial); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { _errors[profile.Key] = error; }
        }
    }

    internal ConfirmationsSettingsValues Save(ConfirmationsSettingsValues draft,
        Action<ConfirmationsSaveReceipt>? accepted = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        foreach (IGrouping<string, ConfirmationSetting> profile in ConfirmationSettingsCatalog.Fields.GroupBy(field => field.Profile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmationSetting[] edited = profile.Where(field => Initial.Get(field.Name) != draft.Get(field.Name)).ToArray();
            if (edited.Length == 0) { continue; }
            string path = Path.Combine(_directory, profile.Key);
            Snapshot current = Read(path);
            ConfirmationsSettingsValues values = ReadValues(current.Document, profile, Initial);
            // Compare only this page's intentions. General may legitimately write other
            // Pull/Submodule keys (or explicit defaults) since both pages were opened.
            foreach (ConfirmationSetting field in edited)
            {
                bool? latest = values.Get(field.Name), wanted = draft.Get(field.Name);
                if (latest != Initial.Get(field.Name) && latest != wanted)
                { throw new IOException("Confirmation preferences changed after Settings opened: " + profile.Key); }
            }
            bool written = false;
            foreach (ConfirmationSetting field in edited)
            {
                bool? wanted = draft.Get(field.Name);
                if (values.Get(field.Name) == wanted) { continue; }
                current.Document[field.Key] = wanted is bool boolean ? JsonValue.Create(boolean) : null;
                values = values.With(field.Name, wanted); written = true;
            }
            if (written) { Publish(path, current, cancellationToken); }
            // A completed file is durable even if cancellation or a later profile fails.
            Initial = ReadValues(current.Document, profile, Initial);
            _errors.Remove(profile.Key);
            accepted?.Invoke(new(profile.Key, Initial, Array.AsReadOnly(profile.ToArray()), written));
        }
        return Initial;
    }

    private sealed record Snapshot(bool Existed, byte[] Bytes, JsonObject Document);
    private static Snapshot Read(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return new(false, [], new()); }
        catch (DirectoryNotFoundException) { return new(false, [], new()); }
        JsonObject document = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Confirmation preferences must be an object.");
        return new(true, bytes, document);
    }
    private static ConfirmationsSettingsValues ReadValues(JsonObject document, IEnumerable<ConfirmationSetting> fields, ConfirmationsSettingsValues values)
    {
        foreach (ConfirmationSetting field in fields)
        {
            bool? value = field.Nullable ? null : false;
            if (document.TryGetPropertyValue(field.Key, out JsonNode? node))
            {
                if (node is null && !field.Nullable) { throw new JsonException("Invalid confirmation preference: " + field.Key); }
                if (node is not null)
                {
                    if (node is not JsonValue scalar || !scalar.TryGetValue(out bool boolean))
                    { throw new JsonException("Invalid confirmation preference: " + field.Key); }
                    value = boolean;
                }
            }
            values = values.With(field.Name, value);
        }
        return values;
    }
    private static void Publish(string path, Snapshot expected, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(expected.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            token.ThrowIfCancellationRequested();
            File.WriteAllBytes(temporary, bytes);
            Snapshot latest = Read(path);
            if (latest.Existed != expected.Existed || !latest.Bytes.AsSpan().SequenceEqual(expected.Bytes))
            { throw new IOException("Confirmation preferences changed while saving: " + Path.GetFileName(path)); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
