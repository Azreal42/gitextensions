namespace GitExtensions.Linux.Desktop;

internal sealed record BlameViewerSetting(string Name, string Control, string Caption, string Group, string? ManualAnchor = null);

/// <summary>The ten independent Windows page controls; histogram remains on General and the Blame menu.</summary>
internal static class BlameViewerSettingsCatalog
{
    internal const string Warning = "Could prevent blame to calculate the accurate line number when blaming previous revisions.";
    internal static IReadOnlyList<BlameViewerSetting> Fields { get; } = Array.AsReadOnly<BlameViewerSetting>(
    [
        new(nameof(BlamePreferences.IgnoreWhitespace), "cbIgnoreWhitespace", "Ignore whitespace", "Query"),
        new(nameof(BlamePreferences.DetectMoves), "cbDetectMoveAndCopyInThisFile", "Detect moved or copied lines within blamed file", "Query", "blame-viewer-blame-settings-detect-move-and-copy-in-this-file"),
        new(nameof(BlamePreferences.DetectCopies), "cbDetectMoveAndCopyInAllFiles", "Detect moved or copied lines from all files in same commit", "Query", "blame-viewer-blame-settings-detect-move-and-copy-in-all-files"),
        new(nameof(BlamePreferences.DisplayAuthorFirst), "cbDisplayAuthorFirst", "Display author first", "Display"),
        new(nameof(BlamePreferences.ShowAuthor), "cbShowAuthor", "Show author", "Display"),
        new(nameof(BlamePreferences.ShowAuthorDate), "cbShowAuthorDate", "Show author date", "Display"),
        new(nameof(BlamePreferences.ShowAuthorTime), "cbShowAuthorTime", "Show author time", "Display"),
        new(nameof(BlamePreferences.ShowLineNumbers), "cbShowLineNumbers", "Show line numbers", "Display"),
        new(nameof(BlamePreferences.ShowOriginalFilePath), "cbShowOriginalFilePath", "Show original file path", "Display"),
        new(nameof(BlamePreferences.ShowAuthorAvatar), "cbShowAuthorAvatar", "Show author avatar", "Display")
    ]);
    internal static IReadOnlyList<string> AllPreferenceNames { get; } = Array.AsReadOnly<string>(
        [.. Fields.Select(field => field.Name), nameof(BlamePreferences.UseHistogramDiffAlgorithm)]);

    internal static bool Get(BlamePreferences value, string name) => name switch
    {
        nameof(BlamePreferences.IgnoreWhitespace) => value.IgnoreWhitespace,
        nameof(BlamePreferences.DetectMoves) => value.DetectMoves,
        nameof(BlamePreferences.DetectCopies) => value.DetectCopies,
        nameof(BlamePreferences.DisplayAuthorFirst) => value.DisplayAuthorFirst,
        nameof(BlamePreferences.ShowAuthor) => value.ShowAuthor,
        nameof(BlamePreferences.ShowAuthorDate) => value.ShowAuthorDate,
        nameof(BlamePreferences.ShowAuthorTime) => value.ShowAuthorTime,
        nameof(BlamePreferences.ShowLineNumbers) => value.ShowLineNumbers,
        nameof(BlamePreferences.ShowOriginalFilePath) => value.ShowOriginalFilePath,
        nameof(BlamePreferences.ShowAuthorAvatar) => value.ShowAuthorAvatar,
        nameof(BlamePreferences.UseHistogramDiffAlgorithm) => value.UseHistogramDiffAlgorithm,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static BlamePreferences With(BlamePreferences value, string name, bool choice) => name switch
    {
        nameof(BlamePreferences.IgnoreWhitespace) => value with { IgnoreWhitespace = choice },
        nameof(BlamePreferences.DetectMoves) => value with { DetectMoves = choice },
        nameof(BlamePreferences.DetectCopies) => value with { DetectCopies = choice },
        nameof(BlamePreferences.DisplayAuthorFirst) => value with { DisplayAuthorFirst = choice },
        nameof(BlamePreferences.ShowAuthor) => value with { ShowAuthor = choice },
        nameof(BlamePreferences.ShowAuthorDate) => value with { ShowAuthorDate = choice },
        nameof(BlamePreferences.ShowAuthorTime) => value with { ShowAuthorTime = choice },
        nameof(BlamePreferences.ShowLineNumbers) => value with { ShowLineNumbers = choice },
        nameof(BlamePreferences.ShowOriginalFilePath) => value with { ShowOriginalFilePath = choice },
        nameof(BlamePreferences.ShowAuthorAvatar) => value with { ShowAuthorAvatar = choice },
        nameof(BlamePreferences.UseHistogramDiffAlgorithm) => value with { UseHistogramDiffAlgorithm = choice },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
