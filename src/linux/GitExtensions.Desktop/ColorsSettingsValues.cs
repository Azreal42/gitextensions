namespace GitExtensions.Linux.Desktop;

/// <summary>The five portable Colors controls, over the existing saved View authority.</summary>
internal sealed record ColorsSettingsValues
{
    public bool DrawAlternateBackground { get; init; } = true;
    public bool DrawNonRelativesGray { get; init; } = true;
    public bool DrawNonRelativesTextGray { get; init; }
    public bool HighlightAuthoredRevisions { get; init; } = true;
    public bool FillRefLabels { get; init; }

    internal static ColorsSettingsValues From(HistoryViewPreferences view) => new()
    {
        DrawAlternateBackground = view.DrawAlternateBackground,
        DrawNonRelativesGray = view.DrawNonRelativesGray,
        DrawNonRelativesTextGray = view.DrawNonRelativesTextGray,
        HighlightAuthoredRevisions = view.HighlightAuthoredRevisions,
        FillRefLabels = view.FillRefLabels
    };
    internal bool Get(string name) => name switch
    {
        nameof(DrawAlternateBackground) => DrawAlternateBackground,
        nameof(DrawNonRelativesGray) => DrawNonRelativesGray,
        nameof(DrawNonRelativesTextGray) => DrawNonRelativesTextGray,
        nameof(HighlightAuthoredRevisions) => HighlightAuthoredRevisions,
        nameof(FillRefLabels) => FillRefLabels,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal ColorsSettingsValues With(string name, bool value) => name switch
    {
        nameof(DrawAlternateBackground) => this with { DrawAlternateBackground = value },
        nameof(DrawNonRelativesGray) => this with { DrawNonRelativesGray = value },
        nameof(DrawNonRelativesTextGray) => this with { DrawNonRelativesTextGray = value },
        nameof(HighlightAuthoredRevisions) => this with { HighlightAuthoredRevisions = value },
        nameof(FillRefLabels) => this with { FillRefLabels = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static HistoryViewPreferences Set(HistoryViewPreferences view, string name, bool value) => name switch
    {
        nameof(DrawAlternateBackground) => view with { DrawAlternateBackground = value },
        nameof(DrawNonRelativesGray) => view with { DrawNonRelativesGray = value },
        nameof(DrawNonRelativesTextGray) => view with { DrawNonRelativesTextGray = value },
        nameof(HighlightAuthoredRevisions) => view with { HighlightAuthoredRevisions = value },
        nameof(FillRefLabels) => view with { FillRefLabels = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}

internal sealed record ColorsSetting(string Name, string Control, string Caption);

internal static class ColorsSettingsCatalog
{
    internal static readonly IReadOnlyList<ColorsSetting> Fields = Array.AsReadOnly<ColorsSetting>(
    [
        new(nameof(ColorsSettingsValues.DrawAlternateBackground), "chkDrawAlternateBackColor", "Draw alternate background"),
        new(nameof(ColorsSettingsValues.DrawNonRelativesGray), "DrawNonRelativesGray", "Draw non relatives graph gray"),
        new(nameof(ColorsSettingsValues.DrawNonRelativesTextGray), "DrawNonRelativesTextGray", "Draw non relatives text gray"),
        new(nameof(ColorsSettingsValues.HighlightAuthoredRevisions), "chkHighlightAuthored", "Highlight authored revisions"),
        new(nameof(ColorsSettingsValues.FillRefLabels), "chkFillRefLabels", "Fill git ref labels")
    ]);
}

/// <summary>Saved value observed at the first explicit View toggle; null means the profile was unreadable.</summary>
internal sealed record HistoryGraphGrayIntent(bool? SavedBaseline, bool Value);
