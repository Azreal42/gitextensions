using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

public sealed record ScriptsPreferencesLoadResult(ScriptConfigurationResult Configuration, bool Existed, ReadOnlyMemory<byte> OriginalBytes)
{
    public IReadOnlyList<ScriptDefinition> Scripts => Configuration.Scripts;
    public Exception? Error => Configuration.Error;
}

/// <summary>Global Linux profile only. Importing Windows ownScripts text is a separate, explicit operation.</summary>
public sealed class ScriptsPreferencesStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string FilePath { get; }
    public ScriptsPreferencesStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "scripts.json");

    public ScriptsPreferencesLoadResult Load()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        { return new(ScriptConfiguration.Read(null), false, ReadOnlyMemory<byte>.Empty); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(new(Array.Empty<ScriptDefinition>(), ScriptConfigurationSource.Invalid, Error: error), true, ReadOnlyMemory<byte>.Empty); }
        try
        {
            PreferencesDocument document = JsonSerializer.Deserialize<PreferencesDocument>(Utf8.GetString(bytes))
                ?? throw new JsonException("The scripts preferences document cannot be null.");
            return new(ScriptConfiguration.Read(document.OwnScripts), true, bytes);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { return new(new(Array.Empty<ScriptDefinition>(), ScriptConfigurationSource.Invalid, Error: error), true, bytes); }
    }

    public ScriptConfigurationResult ParseImport(string? ownScriptsText) => ScriptConfiguration.Read(ownScriptsText);

    /// <param name="replaceUnreadable">An explicit replacement decision; a failed Load must never silently become an empty Save.</param>
    public void Save(IEnumerable<ScriptDefinition> scripts, bool replaceUnreadable = false)
    {
        string xml = ScriptConfiguration.WriteXml(scripts);
        ScriptsPreferencesLoadResult previous = Load();
        if (previous.Error is not null && !replaceUnreadable)
        { throw new InvalidDataException("The scripts profile is unreadable. Explicitly replace or repair it before saving.", previous.Error); }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PreferencesDocument(xml), new JsonSerializerOptions { WriteIndented = true });
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private sealed record PreferencesDocument(string? OwnScripts);
}
