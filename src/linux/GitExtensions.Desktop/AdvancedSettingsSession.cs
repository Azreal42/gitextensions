using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

internal sealed record AdvancedSettingsSaveReceipt(string Profile, AdvancedSettingsValues Values,
    IReadOnlyList<AdvancedSetting> Fields, bool Written);

/// <summary>Confirmations' per-field JSON/CAS protocol over the existing three workflow authorities.</summary>
internal sealed class AdvancedSettingsSession
{
    private readonly string _directory;
    private AdvancedSettingsValues _durable = new();
    private readonly Dictionary<string, Exception> _errors = new(StringComparer.Ordinal);
    internal AdvancedSettingsValues Initial { get; private set; } = new();
    internal IReadOnlyDictionary<string, Exception> LoadErrors => _errors;

    internal AdvancedSettingsSession(string? profileDirectory = null, DesktopPreferences? initialDesktop = null)
    {
        _directory = profileDirectory ?? Path.GetDirectoryName(new PreferencesStore().FilePath)!;
        foreach (IGrouping<string, AdvancedSetting> group in AdvancedSettingsCatalog.Fields.GroupBy(setting => setting.Profile))
        {
            try { _durable = ReadValues(Read(Path.Combine(_directory, group.Key)).Document, group, _durable); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { _errors[group.Key] = error; }
        }
        // Active caller values are the visible draft baseline, never a substitute for the durable CAS baseline.
        Initial = initialDesktop is null ? _durable : _durable with
        {
            AutoNormaliseBranchName = initialDesktop.AutoNormaliseBranchName,
            AutoNormaliseSymbol = initialDesktop.AutoNormaliseSymbol,
            AlwaysShowAdvancedOptions = initialDesktop.AlwaysShowAdvancedOptions,
            DontShowHelpImages = initialDesktop.DontShowHelpImages,
            CheckForUpdates = initialDesktop.CheckForUpdates,
            CheckForReleaseCandidates = initialDesktop.CheckForReleaseCandidates
        };
    }

    /// <summary>General may write its desktop file first. Defer these fields to this session's own CAS publication.</summary>
    internal DesktopPreferences MergeDesktop(DesktopPreferences desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        AdvancedSetting[] owned = AdvancedSettingsCatalog.Fields.Where(setting => setting.Profile == AdvancedSettingsCatalog.DesktopProfile).ToArray();
        AdvancedSettingsValues current = ReadValues(Read(Path.Combine(_directory, AdvancedSettingsCatalog.DesktopProfile)).Document, owned, new());
        // Observing a peer for another writer must not rebase our visible draft or saved baseline.
        return AdvancedSettingsValues.ApplyDesktop(desired, current, owned);
    }

    internal AdvancedSettingsValues Save(AdvancedSettingsValues draft, Action<AdvancedSettingsSaveReceipt>? accepted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        foreach (IGrouping<string, AdvancedSetting> group in AdvancedSettingsCatalog.Fields.GroupBy(setting => setting.Profile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvancedSetting[] edited = group.Where(setting => !Equals(Initial.Get(setting.Name), draft.Get(setting.Name))).ToArray();
            if (edited.Length == 0) { continue; }
            if (_errors.TryGetValue(group.Key, out Exception? loadError))
            { throw new InvalidDataException("Advanced preferences were unreadable. Reopen Settings before saving this profile: " + group.Key, loadError); }
            if (edited.Any(setting => setting.IsSymbol) && draft.AutoNormaliseSymbol is not ("_" or "-" or ""))
            { throw new ArgumentException("Choose a replacement symbol from the list.", nameof(draft)); }
            string path = Path.Combine(_directory, group.Key); Snapshot snapshot = Read(path);
            AdvancedSettingsValues current = ReadValues(snapshot.Document, edited, _durable);
            foreach (AdvancedSetting setting in edited)
            {
                object actual = current.Get(setting.Name), wanted = draft.Get(setting.Name);
                if (!Equals(actual, _durable.Get(setting.Name)) && !Equals(actual, wanted))
                { throw new IOException("Advanced preferences changed after Settings opened: " + group.Key); }
            }
            bool written = false;
            foreach (AdvancedSetting setting in edited)
            {
                object wanted = draft.Get(setting.Name);
                if (Equals(current.Get(setting.Name), wanted)) { continue; }
                snapshot.Document[setting.Name] = setting.IsSymbol ? JsonValue.Create((string)wanted) : JsonValue.Create((bool)wanted);
                written = true;
            }
            if (written) { Publish(path, snapshot, cancellationToken); }
            // Acknowledge only this file's edited keys before calling the host or attempting the next file.
            foreach (AdvancedSetting setting in edited)
            {
                object value = draft.Get(setting.Name);
                Initial = Initial.With(setting.Name, value);
                _durable = _durable.With(setting.Name, value);
            }
            accepted?.Invoke(new(group.Key, Initial, Array.AsReadOnly(edited), written));
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
        return new(true, bytes, JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Advanced preferences must contain an object."));
    }
    private static AdvancedSettingsValues ReadValues(JsonObject document, IEnumerable<AdvancedSetting> settings, AdvancedSettingsValues current)
    {
        AdvancedSettingsValues defaults = new();
        foreach (AdvancedSetting setting in settings)
        {
            object value = defaults.Get(setting.Name);
            if (document.TryGetPropertyValue(setting.Name, out JsonNode? node))
            {
                if (setting.IsSymbol && node is JsonValue text && text.TryGetValue(out string? literal) && literal is not null) { value = literal; }
                else if (!setting.IsSymbol && node is JsonValue scalar && scalar.TryGetValue(out bool boolean)) { value = boolean; }
                else { throw new JsonException("Invalid advanced preference: " + setting.Name); }
            }
            current = current.With(setting.Name, value);
        }
        return current;
    }
    private static void Publish(string path, Snapshot snapshot, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, snapshot.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Snapshot current = Read(path);
            if (current.Existed != snapshot.Existed || !current.Bytes.AsSpan().SequenceEqual(snapshot.Bytes))
            { throw new IOException("Advanced preferences changed while saving: " + Path.GetFileName(path)); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}
