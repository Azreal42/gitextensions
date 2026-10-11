using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed class CloneSourcesStore
{
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "clone-sources.json");

    internal IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(FilePath) ? Normalize(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? []) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal void Visit(string source, int capacity)
    {
        if (!CanRemember(source)) { return; }
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            List<string> sources = Normalize(new[] { source }.Concat(Load())).Take(Math.Clamp(capacity, 1, 5000)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(sources));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> sources)
        => sources.Where(CanRemember).Distinct(StringComparer.Ordinal).ToArray();

    private static bool CanRemember(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Contains('\0')) { return false; }
        if (Path.IsPathFullyQualified(source)) { return true; }
        // Leave credential-bearing URLs with Git's credential flow, rather than duplicating them in XDG history.
        return !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Query) && (string.IsNullOrEmpty(uri.UserInfo) || uri.Scheme is not ("http" or "https") && !uri.UserInfo.Contains(':'));
    }
}
