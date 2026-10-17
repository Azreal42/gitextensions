using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

public sealed record ScriptsPreferencesLoadResult(ScriptConfigurationResult Configuration, bool Existed, ReadOnlyMemory<byte> OriginalBytes)
{
    public IReadOnlyList<ScriptDefinition> Scripts => Configuration.Scripts;
    public Exception? Error => Configuration.Error;
}

/// <summary>Global Linux profile only. Windows ownScripts import remains explicit.</summary>
public sealed class ScriptsPreferencesStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static event Action<string>? Saved;
    public string FilePath { get; }

    public ScriptsPreferencesStore(string? filePath = null)
        => FilePath = Path.GetFullPath(filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "scripts.json"));

    public ScriptsPreferencesLoadResult Load()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        { return new(ScriptConfiguration.Read(null), false, ReadOnlyMemory<byte>.Empty); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(new(Array.Empty<ScriptDefinition>(), ScriptConfigurationSource.Invalid, Error: error), true, ReadOnlyMemory<byte>.Empty); }
        return Decode(bytes);
    }

    public ScriptConfigurationResult ParseImport(string? ownScriptsText) => ScriptConfiguration.Read(ownScriptsText);

    /// <summary>No IO for an unchanged draft. Conflicting known definitions are never silently merged.</summary>
    public ScriptsPreferencesLoadResult SaveChanges(ScriptsPreferencesLoadResult baseline, IReadOnlyList<ScriptDefinition> draft)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(draft);
        ScriptDefinition[] snapshot = draft.ToArray();
        if (snapshot.SequenceEqual(baseline.Scripts)) { return baseline; }
        string xml = ScriptConfiguration.WriteXml(snapshot);
        RefuseUnreadable(baseline);
        ScriptsPreferencesLoadResult latest = Load();
        RefuseUnreadable(latest);
        if (!latest.Scripts.SequenceEqual(baseline.Scripts))
        { throw new IOException("The scripts changed after Settings opened. Reopen Settings before saving this draft."); }
        return Publish(xml, latest, replaceUnreadable: false);
    }

    /// <param name="replaceUnreadable">An explicit replacement decision, never used by ordinary Settings.</param>
    public void Save(IEnumerable<ScriptDefinition> scripts, bool replaceUnreadable = false)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        string xml = ScriptConfiguration.WriteXml(scripts.ToArray());
        ScriptsPreferencesLoadResult previous = Load();
        if (!replaceUnreadable) { RefuseUnreadable(previous); }
        _ = Publish(xml, previous, replaceUnreadable);
    }

    private ScriptsPreferencesLoadResult Publish(string xml, ScriptsPreferencesLoadResult previous, bool replaceUnreadable)
    {
        byte[] bytes = Serialize(xml, previous.OriginalBytes, replaceUnreadable);
        string directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
            using (FileStream stream = new(temporary, options)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            ScriptsPreferencesLoadResult current = Load();
            // Detect an observed race immediately before rename; this is not interprocess CAS.
            if (current.Existed != previous.Existed || !current.OriginalBytes.Span.SequenceEqual(previous.OriginalBytes.Span)
                || current.Error is not null && !replaceUnreadable)
            { throw new IOException("The scripts profile changed while saving. Reopen Settings before saving again.", current.Error); }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { /* Preserve the actual publication error; a cleanup failure cannot replace it. */ }
        }
        ScriptsPreferencesLoadResult saved = Decode(bytes);
        Saved?.Invoke(FilePath);
        return saved;
    }

    private static void RefuseUnreadable(ScriptsPreferencesLoadResult loaded)
    {
        if (loaded.Error is not null)
        { throw new InvalidDataException("The scripts profile is unreadable. Explicitly replace or repair it before saving.", loaded.Error); }
    }

    private static ScriptsPreferencesLoadResult Decode(byte[] bytes)
    {
        try
        {
            PreferencesDocument document = JsonSerializer.Deserialize<PreferencesDocument>(Utf8.GetString(bytes))
                ?? throw new JsonException("The scripts preferences document cannot be null.");
            return new(ScriptConfiguration.Read(document.OwnScripts), true, bytes);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { return new(new(Array.Empty<ScriptDefinition>(), ScriptConfigurationSource.Invalid, Error: error), true, bytes); }
    }

    private static byte[] Serialize(string xml, ReadOnlyMemory<byte> previous, bool replaceUnreadable)
    {
        JsonDocument? existing = null;
        try
        {
            if (!previous.IsEmpty)
            {
                try
                {
                    existing = JsonDocument.Parse(Utf8.GetString(previous.Span));
                    if (existing.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("The scripts profile must be an object."); }
                }
                catch (Exception error) when (replaceUnreadable && (error is JsonException or DecoderFallbackException))
                { existing?.Dispose(); existing = null; }
            }
            using MemoryStream output = new();
            using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                if (existing is not null)
                {
                    foreach (JsonProperty property in existing.RootElement.EnumerateObject())
                    { if (!property.NameEquals("OwnScripts")) { property.WriteTo(writer); } }
                }
                writer.WriteString("OwnScripts", xml);
                writer.WriteEndObject();
            }
            return output.ToArray();
        }
        finally { existing?.Dispose(); }
    }

    private sealed record PreferencesDocument(string? OwnScripts);
}
