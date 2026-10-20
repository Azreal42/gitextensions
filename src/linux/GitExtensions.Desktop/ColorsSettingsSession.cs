namespace GitExtensions.Linux.Desktop;

internal sealed record ColorsSettingsPublication(HistoryViewPreferences History, ColorsSettingsValues Values, bool Written, IReadOnlyList<string> Fields);

/// <summary>Only edited controls become defaults; unedited View choices remain local to Browse.</summary>
internal sealed class ColorsSettingsSession
{
    private readonly HistoryViewPreferencesStore _store;
    private HistoryViewPreferences _active;
    private ColorsSettingsValues? _savedBaseline;
    internal Exception? LoadError { get; }
    internal ColorsSettingsValues Initial { get; private set; }

    internal ColorsSettingsSession(HistoryViewPreferences activeView, HistoryViewPreferencesStore? store = null)
    {
        _active = activeView ?? throw new ArgumentNullException(nameof(activeView));
        _store = store ?? new(); Initial = ColorsSettingsValues.From(activeView);
        try { _savedBaseline = _store.ReadColors(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { LoadError = error; }
    }

    internal ColorsSettingsPublication Save(ColorsSettingsValues draft, HistoryViewPreferences? activeView = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft); cancellationToken.ThrowIfCancellationRequested();
        HistoryViewPreferences active = activeView ?? _active;
        ColorsSetting[] edited = ColorsSettingsCatalog.Fields.Where(field => Initial.Get(field.Name) != draft.Get(field.Name)).ToArray();
        bool written = false;
        if (edited.Length != 0)
        {
            if (_savedBaseline is null) { throw new InvalidDataException("History view preferences were unreadable. Reopen Settings before saving Colors.", LoadError); }
            written = _store.SaveColors(_savedBaseline, draft, edited, cancellationToken);
            foreach (ColorsSetting choice in edited) { _savedBaseline = _savedBaseline.With(choice.Name, draft.Get(choice.Name)); }
        }
        // Preserve the current active View and its identities; only this page's intentions apply.
        foreach (ColorsSetting field in edited) { active = ColorsSettingsValues.Set(active, field.Name, draft.Get(field.Name)); }
        _active = active; Initial = ColorsSettingsValues.From(active);
        return new(active, Initial, written, Array.AsReadOnly(edited.Select(choice => choice.Name).ToArray()));
    }
}
