using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

internal sealed record CommitDialogSettingsSaveReceipt(string Path, string Profile, CommitDialogSettingsValues Values,
    IReadOnlyList<CommitDialogSetting> Fields, bool Written, DesktopPreferences? Desktop);

/// <summary>Global field intentions over the two existing Commit/Dictionary profiles, never a second settings store.</summary>
internal sealed class CommitDialogSettingsSession
{
    private readonly string _directory;
    private CommitDialogSettingsValues _durable = new();
    private readonly Dictionary<string, Exception> _errors = new(StringComparer.Ordinal);
    internal CommitDialogSettingsValues Initial { get; private set; }
    internal IReadOnlyDictionary<string, Exception> LoadErrors => _errors;

    internal CommitDialogSettingsSession(string? profileDirectory = null, DesktopPreferences? initialDesktop = null)
    {
        _directory = profileDirectory ?? Path.GetDirectoryName(new PreferencesStore().FilePath)!;
        foreach (IGrouping<string, CommitDialogSetting> group in CommitDialogSettingsCatalog.Fields.GroupBy(choice => choice.Profile))
        {
            try { _durable = ReadValues(Read(System.IO.Path.Combine(_directory, group.Key)).Document, group, _durable); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { _errors[group.Key] = error; }
        }
        Initial = initialDesktop is null ? _durable : _durable with { ProvideAutocompletion = initialDesktop.ProvideAutocompletion };
    }

    // General publishes other desktop intentions first. This key must retain its
    // current durable value until this session performs its own checked write.
    internal DesktopPreferences MergeDesktop(DesktopPreferences desired)
    {
        CommitDialogSetting choice = CommitDialogSettingsCatalog.Fields.Single(item => item.Profile == CommitDialogSettingsCatalog.DesktopProfile);
        CommitDialogSettingsValues current = ReadValues(Read(System.IO.Path.Combine(_directory, choice.Profile)).Document, [choice], new());
        return desired with { ProvideAutocompletion = current.ProvideAutocompletion };
    }

    internal CommitDialogSettingsValues Save(CommitDialogSettingsValues draft,
        Action<CommitDialogSettingsSaveReceipt>? accepted = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        foreach (IGrouping<string, CommitDialogSetting> group in CommitDialogSettingsCatalog.Fields.GroupBy(choice => choice.Profile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitDialogSetting[] edited = group.Where(choice => !Equals(Initial.Get(choice.Name), draft.Get(choice.Name))).ToArray();
            if (edited.Length == 0) { continue; }
            if (_errors.TryGetValue(group.Key, out Exception? error))
            { throw new IOException("Commit preferences were unreadable. Reopen Settings before saving this profile: " + group.Key, error); }
            if (edited.Any(choice => choice.IsNumber) && (draft.NumberOfPreviousMessages is < 1 or > 999))
            { throw new ArgumentOutOfRangeException(nameof(draft), "Choose between 1 and 999 previous commit messages."); }
            string path = System.IO.Path.Combine(_directory, group.Key); Snapshot current = Read(path);
            CommitDialogSettingsValues values = ReadValues(current.Document, group, _durable);
            foreach (CommitDialogSetting choice in edited)
            {
                object actual = values.Get(choice.Name), wanted = draft.Get(choice.Name);
                if (!Equals(actual, _durable.Get(choice.Name)) && !Equals(actual, wanted))
                { throw new IOException("Commit preferences changed after Settings opened: " + group.Key); }
            }
            bool written = false;
            foreach (CommitDialogSetting choice in edited)
            {
                object wanted = draft.Get(choice.Name);
                if (Equals(values.Get(choice.Name), wanted)) { continue; }
                current.Document[choice.Name] = choice.IsNumber ? JsonValue.Create((int)wanted) : JsonValue.Create((bool)wanted);
                written = true;
            }
            // Validate the actual full desktop record before committing it, not
            // after acknowledgement; Dictionary receives this exact saved value.
            DesktopPreferences? desktop = group.Key == CommitDialogSettingsCatalog.DesktopProfile
                ? current.Document.Deserialize<DesktopPreferences>() ?? throw new JsonException("Desktop preferences must be an object.") : null;
            if (desktop is not null) { desktop = desktop with { Dictionary = GitDictionaryName.NormalizeDisabledIdentity(desktop.Dictionary) }; }
            if (written) { Publish(path, current, cancellationToken); }
            foreach (CommitDialogSetting choice in edited)
            {
                object value = draft.Get(choice.Name);
                Initial = Initial.With(choice.Name, value); _durable = _durable.With(choice.Name, value);
            }
            accepted?.Invoke(new(path, group.Key, Initial, Array.AsReadOnly(edited), written, desktop));
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
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Commit settings require a JSON object."); }
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
        { if (!names.Add(property.Name)) { throw new JsonException("Duplicate commit preference: " + property.Name); } }
        return new(true, bytes, JsonNode.Parse(bytes)!.AsObject());
    }
    private static CommitDialogSettingsValues ReadValues(JsonObject document, IEnumerable<CommitDialogSetting> settings, CommitDialogSettingsValues values)
    {
        CommitDialogSettingsValues defaults = new();
        foreach (CommitDialogSetting choice in settings)
        {
            object value = defaults.Get(choice.Name);
            if (document.TryGetPropertyValue(choice.Name, out JsonNode? node))
            {
                if (choice.IsNumber && node is JsonValue number && number.TryGetValue(out int integer)) { value = integer; }
                else if (!choice.IsNumber && node is JsonValue boolean && boolean.TryGetValue(out bool flag)) { value = flag; }
                else { throw new JsonException("Invalid commit preference: " + choice.Name); }
            }
            values = values.With(choice.Name, value);
        }
        return values;
    }
    private static void Publish(string path, Snapshot expected, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, expected.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows() && expected.Existed) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(path)); }
            Snapshot latest = Read(path);
            if (latest.Existed != expected.Existed || !latest.Bytes.AsSpan().SequenceEqual(expected.Bytes))
            { throw new IOException("Commit preferences changed while saving: " + System.IO.Path.GetFileName(path)); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}

internal static class CommitDialogSettingsPublication
{
    internal static event Action<CommitDialogSettingsSaveReceipt>? Saved;
    internal static void Publish(CommitDialogSettingsSaveReceipt receipt)
    {
        if (receipt.Desktop is { } desktop) { CommitSpellingController.PublishSavedDesktop(receipt.Path, desktop); }
        Saved?.Invoke(receipt);
    }
}
