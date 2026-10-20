using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using GitCommands;
using GitCommands.Git;

namespace GitExtensions.Linux.Desktop;

internal sealed record ReferencePreferences(LocalChangesAction CheckoutChanges = LocalChangesAction.DontChange,
    CheckoutNewBranchMode RemoteCheckoutMode = CheckoutNewBranchMode.Create, bool IncludeUntracked = true, bool ReapplyStash = false,
    bool UseDefaultCheckoutChanges = false, bool AlwaysShowCheckoutDialog = false);

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
            byte[]? before = File.Exists(_file) ? File.ReadAllBytes(_file) : null;
            JsonObject current = before is null ? new() : JsonNode.Parse(before) as JsonObject ?? throw new JsonException("Reference preferences must be an object.");
            JsonObject legacy = JsonSerializer.SerializeToNode(value, Json)!.AsObject();
            // This legacy writer owns only its six original keys. Confirmations/Advanced and unknown peers survive.
            foreach ((string key, JsonNode? node) in legacy) { current[key] = node?.DeepClone(); }
            File.WriteAllText(temporary, current.ToJsonString(Json));
            byte[]? latest = File.Exists(_file) ? File.ReadAllBytes(_file) : null;
            if ((before is null) != (latest is null) || before is not null && !before.AsSpan().SequenceEqual(latest))
            { throw new IOException("Reference preferences changed while saving."); }
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { } }
    }
}
