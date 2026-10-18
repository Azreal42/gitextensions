using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed record AvailableEncodingsPreferencesLoadResult(IReadOnlyList<string> Names, bool Existed,
    ReadOnlyMemory<byte> OriginalBytes, Exception? Error = null)
{
    internal string? ProfilePath { get; init; }
}

/// <summary>A profile-scoped preferred list, independent of Git configuration and codec availability.</summary>
public sealed class AvailableEncodingsPreferencesStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static event Action<string>? Saved;
    public string FilePath { get; }
    public AvailableEncodingsCatalog Catalog { get; }

    public AvailableEncodingsPreferencesStore(string? directory = null, AvailableEncodingsCatalog? catalog = null)
    {
        Catalog = catalog ?? new();
        FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "encodings.json"));
    }

    public AvailableEncodingsPreferencesLoadResult Load()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        { return Result(Catalog.Defaults, false, ReadOnlyMemory<byte>.Empty); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Result(Catalog.Defaults, true, ReadOnlyMemory<byte>.Empty, error); }
        return Decode(bytes);
    }

    /// <summary>Untouched drafts do not write. Unsafe, stale or cross-profile baselines cannot replace data.</summary>
    public AvailableEncodingsPreferencesLoadResult SaveChanges(AvailableEncodingsPreferencesLoadResult baseline, IEnumerable<string> draft)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(draft);
        if (!string.Equals(baseline.ProfilePath, FilePath, StringComparison.Ordinal))
        { throw new ArgumentException("The preferred encodings draft belongs to a different profile.", nameof(baseline)); }
        RefuseUnreadable(baseline);
        string[] names = Normalize(draft);
        AvailableEncodingsPreferencesLoadResult latest = Load();
        RefuseUnreadable(latest); RequireUnchanged(baseline, latest);
        if (names.SequenceEqual(baseline.Names, StringComparer.Ordinal)) { return latest; }
        byte[] bytes = Serialize(names, latest.OriginalBytes);
        string directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
            using (FileStream stream = new(temporary, options)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (!OperatingSystem.IsWindows() && latest.Existed) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(FilePath)); }
            // Check the last observed bytes before rename; this is not interprocess compare-and-swap.
            AvailableEncodingsPreferencesLoadResult current = Load(); RefuseUnreadable(current); RequireUnchanged(latest, current);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Keep the publication error. */ }
        }
        AvailableEncodingsPreferencesLoadResult saved = Decode(bytes);
        Saved?.Invoke(FilePath);
        return saved;
    }

    private AvailableEncodingsPreferencesLoadResult Decode(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Utf8.GetString(bytes));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("The preferred encodings profile must be an object."); }
            HashSet<string> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            { if (!properties.Add(property.Name)) { throw new JsonException("Duplicate preferred encodings property: " + property.Name); } }
            if (!root.TryGetProperty("Version", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
            { throw new JsonException("The preferred encodings profile requires Version 1."); }
            if (!root.TryGetProperty("AvailableEncodings", out JsonElement list) || list.ValueKind != JsonValueKind.String)
            { throw new JsonException("AvailableEncodings must be a semicolon-separated string."); }
            string value = list.GetString()!;
            string[] names;
            try { names = Normalize(value.Length == 0 ? [] : value.Split(';')); }
            catch (ArgumentException error) { throw new JsonException(error.Message, error); }
            return Result(Array.AsReadOnly(names), true, bytes);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { return Result(Catalog.Defaults, true, bytes, error); }
    }

    private string[] Normalize(IEnumerable<string> draft)
    {
        List<string> names = [];
        foreach (string name in draft)
        {
            GitFileEncodingChoice choice = Catalog.Resolve(name) ?? throw new ArgumentException("Unsupported preferred encoding: " + name, nameof(draft));
            if (!names.Contains(choice.Name, StringComparer.Ordinal)) { names.Add(choice.Name); }
        }
        return names.ToArray();
    }

    private static byte[] Serialize(IReadOnlyList<string> names, ReadOnlyMemory<byte> previous)
    {
        using JsonDocument? document = previous.IsEmpty ? null : JsonDocument.Parse(previous);
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (document is not null)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                { if (property.Name is not "Version" and not "AvailableEncodings") { property.WriteTo(writer); } }
            }
            writer.WriteNumber("Version", 1);
            writer.WriteString("AvailableEncodings", string.Join(';', names.Select(name =>
                string.Equals(name, Encoding.Default.WebName, StringComparison.OrdinalIgnoreCase) ? "Default" : name)));
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private AvailableEncodingsPreferencesLoadResult Result(IReadOnlyList<string> names, bool existed, ReadOnlyMemory<byte> bytes, Exception? error = null)
        => new(names, existed, bytes, error) { ProfilePath = FilePath };
    private static void RefuseUnreadable(AvailableEncodingsPreferencesLoadResult result)
    {
        if (result.Error is not null)
        { throw new InvalidDataException("The preferred encodings profile is unreadable. Repair it before saving.", result.Error); }
    }
    private static void RequireUnchanged(AvailableEncodingsPreferencesLoadResult baseline, AvailableEncodingsPreferencesLoadResult latest)
    {
        if (baseline.Existed != latest.Existed || !baseline.OriginalBytes.Span.SequenceEqual(latest.OriginalBytes.Span))
        { throw new IOException("The preferred encodings profile changed after the dialog opened. Reopen the dialog before saving."); }
    }
}
