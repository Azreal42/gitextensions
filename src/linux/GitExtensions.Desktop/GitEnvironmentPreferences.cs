using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record GitEnvironmentPreferences
{
    public string GitExecutable { get; init; } = "";
    public string ToolsDirectory { get; init; } = "";
    public string SshExecutable { get; init; } = "";
    public string? CustomHomeDirectory { get; init; }
    public bool CheckAtStartup { get; init; } = true;
}

internal sealed record GitEnvironmentPreferencesLoadResult(GitEnvironmentPreferences Preferences, bool Existed,
    ReadOnlyMemory<byte> OriginalBytes, string ProfilePath, Exception? Error = null);

/// <summary>Application Git choices are independent of Git configuration and the inherited login environment.</summary>
internal sealed class GitEnvironmentPreferencesStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> Known = ["Version", "GitExecutable", "ToolsDirectory", "CustomHomeDirectory", "CheckAtStartup", "SshExecutable"];
    internal string FilePath { get; }

    internal GitEnvironmentPreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(Path.GetFullPath(root), "gitextensions", "git-environment.json");
    }

    internal GitEnvironmentPreferencesLoadResult Load()
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(FilePath);
            try { return Decode(bytes); }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
            { return new(new(), true, bytes, FilePath, error); }
        }
        catch (FileNotFoundException) { return new(new(), false, ReadOnlyMemory<byte>.Empty, FilePath); }
        catch (DirectoryNotFoundException) { return new(new(), false, ReadOnlyMemory<byte>.Empty, FilePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(new(), true, ReadOnlyMemory<byte>.Empty, FilePath, error); }
    }

    internal GitEnvironmentPreferencesLoadResult SaveChanges(GitEnvironmentPreferencesLoadResult baseline, GitEnvironmentPreferences draft)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(draft);
        if (!string.Equals(baseline.ProfilePath, FilePath, StringComparison.Ordinal))
        { throw new ArgumentException("The Git environment draft belongs to another profile.", nameof(baseline)); }
        RefuseUnreadable(baseline); Validate(draft);
        GitEnvironmentPreferencesLoadResult latest = Load(); RefuseUnreadable(latest);
        // A parent with no edits must not undo another window's explicit publication.
        if (draft == baseline.Preferences) { return latest; }
        RequireUnchanged(baseline, latest);
        byte[] bytes = Serialize(draft, latest.OriginalBytes);
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
            GitEnvironmentPreferencesLoadResult current = Load(); RefuseUnreadable(current); RequireUnchanged(latest, current);
            // A final observed-byte check and atomic rename, not an interprocess compare-and-swap.
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Preserve the publication failure. */ }
        }
        return Decode(bytes);
    }

    internal static void Validate(GitEnvironmentPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value.GitExecutable); ArgumentNullException.ThrowIfNull(value.ToolsDirectory); ArgumentNullException.ThrowIfNull(value.SshExecutable);
        foreach (string? text in new[] { value.GitExecutable, value.ToolsDirectory, value.CustomHomeDirectory, value.SshExecutable })
        { if (text?.Contains('\0') == true) { throw new ArgumentException("A Git environment path cannot contain NUL."); } }
    }

    private GitEnvironmentPreferencesLoadResult Decode(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(Utf8.GetString(bytes));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("The Git environment profile must be an object."); }
        HashSet<string> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        { if (!properties.Add(property.Name)) { throw new JsonException("Duplicate Git environment property: " + property.Name); } }
        if (!root.TryGetProperty("Version", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
        { throw new JsonException("The Git environment profile requires Version 1."); }
        string ReadString(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new JsonException(name + " must be a string.");
        if (!root.TryGetProperty("CustomHomeDirectory", out JsonElement home) || home.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        { throw new JsonException("CustomHomeDirectory must be a string or null."); }
        if (!root.TryGetProperty("CheckAtStartup", out JsonElement check) || check.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { throw new JsonException("CheckAtStartup must be a boolean."); }
        GitEnvironmentPreferences preferences = new()
        {
            GitExecutable = ReadString("GitExecutable"), ToolsDirectory = ReadString("ToolsDirectory"),
            SshExecutable = root.TryGetProperty("SshExecutable", out _) ? ReadString("SshExecutable") : "",
            CustomHomeDirectory = home.ValueKind == JsonValueKind.Null ? null : home.GetString(), CheckAtStartup = check.GetBoolean()
        };
        Validate(preferences); return new(preferences, true, bytes, FilePath);
    }

    private static byte[] Serialize(GitEnvironmentPreferences value, ReadOnlyMemory<byte> previous)
    {
        using JsonDocument? document = previous.IsEmpty ? null : JsonDocument.Parse(previous);
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (document is not null)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                { if (!Known.Contains(property.Name)) { property.WriteTo(writer); } }
            }
            writer.WriteNumber("Version", 1); writer.WriteString("GitExecutable", value.GitExecutable);
            writer.WriteString("ToolsDirectory", value.ToolsDirectory); writer.WriteString("CustomHomeDirectory", value.CustomHomeDirectory);
            writer.WriteBoolean("CheckAtStartup", value.CheckAtStartup);
            // Keep the old profile shape until an SSH choice is first published.
            if (value.SshExecutable.Length > 0 || document?.RootElement.TryGetProperty("SshExecutable", out _) == true)
            { writer.WriteString("SshExecutable", value.SshExecutable); }
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static void RefuseUnreadable(GitEnvironmentPreferencesLoadResult value)
    { if (value.Error is not null) { throw new InvalidDataException("The Git environment profile is unreadable; it was not overwritten.", value.Error); } }
    private static void RequireUnchanged(GitEnvironmentPreferencesLoadResult expected, GitEnvironmentPreferencesLoadResult actual)
    {
        if (expected.Existed != actual.Existed || !expected.OriginalBytes.Span.SequenceEqual(actual.OriginalBytes.Span))
        { throw new InvalidOperationException("The Git environment profile changed. Reload it before saving this draft."); }
    }
}
