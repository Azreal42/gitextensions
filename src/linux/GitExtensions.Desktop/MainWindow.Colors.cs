namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private HistoryGraphGrayIntent? _colorsViewGraphGrayIntent;

    /// <summary>Called only when View changes its shared graph flag, before replacing the active View.</summary>
    internal void TrackColorsViewChange(HistoryViewPreferences before, HistoryViewPreferences after)
    {
        if (before.DrawNonRelativesGray == after.DrawNonRelativesGray) { return; }
        bool? saved = _colorsViewGraphGrayIntent?.SavedBaseline;
        if (_colorsViewGraphGrayIntent is null)
        {
            try { saved = _historyViewStore.ReadColors().DrawNonRelativesGray; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { saved = null; }
        }
        _colorsViewGraphGrayIntent = new(saved, after.DrawNonRelativesGray);
    }

    /// <summary>Durable Colors receipt followed by an in-place publication to this Browse only.</summary>
    internal ColorsSettingsPublication PublishColorsSettings(ColorsSettingsSession session, ColorsSettingsValues draft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _lifetime.Token.ThrowIfCancellationRequested();
        ColorsSettingsPublication publication = session.Save(draft, _historyView, cancellationToken);
        _historyView = publication.History;
        if (publication.Fields.Contains(nameof(ColorsSettingsValues.DrawNonRelativesGray))) { _colorsViewGraphGrayIntent = null; }
        UpdateHistoryPresentation(); RefreshHistoryMenuStates();
        return publication;
    }

    /// <summary>The real Save View command preserves other windows' Colors defaults.</summary>
    internal void SaveCurrentColorsAwareViewDefaults(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _lifetime.Token.ThrowIfCancellationRequested();
        _historyView = _historyViewStore.SaveCurrentViewDefaults(_historyView, _colorsViewGraphGrayIntent, cancellationToken);
        _colorsViewGraphGrayIntent = null;
        UpdateHistoryPresentation(); RefreshHistoryMenuStates();
    }
}
