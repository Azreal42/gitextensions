using System.Text.Json;
using System.Text.Json.Serialization;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

internal sealed record PushPreferences(GitPushRecursion Recursion = GitPushRecursion.Check,
    bool DontConfirmNewBranch = false, bool DontConfirmAddTracking = false,
    GitPullAction? RejectedAction = null);

internal sealed class PushPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "push-preferences.json");

    internal PushPreferences Load()
    {
        try
        {
            PushPreferences value = File.Exists(FilePath) ? JsonSerializer.Deserialize<PushPreferences>(File.ReadAllText(FilePath), Json) ?? new() : new();
            return value with
            {
                Recursion = Enum.IsDefined(value.Recursion) ? value.Recursion : GitPushRecursion.Check,
                RejectedAction = value.RejectedAction is null or GitPullAction.None or GitPullAction.Default or GitPullAction.Merge or GitPullAction.Rebase ? value.RejectedAction : null
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(PushPreferences value)
    {
        if (!Enum.IsDefined(value.Recursion) || value.RejectedAction is not (null or GitPullAction.None or GitPullAction.Default or GitPullAction.Merge or GitPullAction.Rebase))
        { throw new ArgumentOutOfRangeException(nameof(value)); }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, FilePath, overwrite: true); }
        finally { File.Delete(temporary); }
    }
}
