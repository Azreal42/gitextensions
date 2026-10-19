using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed class ConsoleStyleSettingsSession
{
    private readonly ConsoleStylePreferencesStore _store;
    internal ConsoleStylePreferences Initial { get; private set; } = new();
    internal Exception? LoadError { get; }
    internal ConsoleStyleSettingsSession(ConsoleStylePreferencesStore? store = null)
    {
        _store = store ?? new();
        try { Initial = _store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { LoadError = error; }
    }
    internal bool Validate(ConsoleStylePreferences draft, out string? error)
    {
        try
        {
            draft.ValidateChanges(Initial);
            if (LoadError is not null && draft != Initial) { throw new IOException("Unable to read console preferences. " + LoadError.Message, LoadError); }
            error = null; return true;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException)
        { error = failure.Message; return false; }
    }
    internal ConsoleStylePreferences Save(ConsoleStylePreferences draft)
    {
        if (!Validate(draft, out string? error)) { throw new InvalidOperationException(error); }
        if (LoadError is not null) { return Initial; }
        Initial = _store.SaveChanges(Initial, draft);
        ConsoleStylePresentation.PublishSaved(_store.FilePath, Initial);
        return Initial;
    }
}
