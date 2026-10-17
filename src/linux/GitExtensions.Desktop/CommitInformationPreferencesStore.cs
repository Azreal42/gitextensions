using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

public sealed class CommitInformationPreferencesStore
{
    public string FilePath { get; }

    public CommitInformationPreferencesStore(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = directory ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        FilePath = Path.Combine(root, "gitextensions", "commit-information.json");
    }

    public CommitInformationOptions Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<CommitInformationOptions>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(CommitInformationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
