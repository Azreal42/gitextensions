using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record SubmoduleCheckoutPreferences(bool? UpdateSubmodulesOnCheckout = null, bool? DontConfirmUpdateSubmodulesOnCheckout = null)
{
    internal bool? Decision => UpdateSubmodulesOnCheckout ?? DontConfirmUpdateSubmodulesOnCheckout;
}

internal sealed class SubmoduleCheckoutPreferencesStore
{
    internal string FilePath { get; } = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "submodule-checkout-preferences.json");

    internal SubmoduleCheckoutPreferences Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<SubmoduleCheckoutPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Remember(bool update)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SubmoduleCheckoutPreferences(update, update)));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
}
