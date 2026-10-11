using System.Text.Json;
using System.Text.Json.Serialization;
using GitCommands;
using GitCommands.Git;

namespace GitExtensions.Linux.Desktop;

internal sealed record ReferencePreferences(LocalChangesAction CheckoutChanges = LocalChangesAction.DontChange,
    CheckoutNewBranchMode RemoteCheckoutMode = CheckoutNewBranchMode.Create, bool IncludeUntracked = true, bool ReapplyStash = false);

internal sealed class ReferencePreferencesStore
{
    private readonly string _file = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "reference-preferences.json");
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    internal ReferencePreferences Load()
    {
        try
        {
            ReferencePreferences value = File.Exists(_file) ? JsonSerializer.Deserialize<ReferencePreferences>(File.ReadAllText(_file), Json) ?? new() : new();
            return value with
            {
                CheckoutChanges = Enum.IsDefined(value.CheckoutChanges) && value.CheckoutChanges != LocalChangesAction.Reset ? value.CheckoutChanges : LocalChangesAction.DontChange,
                RemoteCheckoutMode = Enum.IsDefined(value.RemoteCheckoutMode) ? value.RemoteCheckoutMode : CheckoutNewBranchMode.Create
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(ReferencePreferences value)
    {
        string temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
