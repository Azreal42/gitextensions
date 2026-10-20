namespace GitExtensions.Linux.Desktop;

/// <summary>An optimistic draft for the existing global Blame profile, without a publication to open viewers.</summary>
internal sealed class BlameViewerSettingsSession
{
    private readonly BlamePreferencesStore _store;
    internal BlamePreferences Initial { get; private set; } = new();
    internal Exception? LoadError { get; }

    internal BlameViewerSettingsSession(BlamePreferencesStore? store = null)
    {
        _store = store ?? new();
        try { Initial = _store.ReadSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { LoadError = error; }
    }

    internal BlameSettingsSaveReceipt Save(BlamePreferences draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft); cancellationToken.ThrowIfCancellationRequested();
        string[] edited = BlameViewerSettingsCatalog.Fields.Where(field =>
            BlameViewerSettingsCatalog.Get(Initial, field.Name) != BlameViewerSettingsCatalog.Get(draft, field.Name)).Select(field => field.Name).ToArray();
        if (edited.Length == 0) { return new(Initial, [], false); }
        if (LoadError is not null) { throw new IOException("Blame preferences were unreadable. Reopen Settings before saving Blame viewer.", LoadError); }
        BlameSettingsSaveReceipt receipt = _store.SaveChanges(Initial, draft, edited, cancellationToken);
        // The one durable file is acknowledged before any later Settings page may fail.
        Initial = receipt.Values;
        return receipt;
    }
}
