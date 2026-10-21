using Avalonia.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

/// <summary>Shared revision-grid column widths; each host keeps its own row height and graph gutter.</summary>
internal sealed record RevisionHistoryColumns(bool ShowAvatar = true, bool ShowAuthor = true, bool ShowDate = true,
    bool ShowId = true, bool ShowNotesColumn = false)
{
    internal const double GraphWidth = 90;
    internal static RevisionHistoryColumns From(HistoryViewPreferences view)
        => new(view.ShowAvatar, view.ShowAuthor, view.ShowDate, view.ShowId, view.ShowNotesColumn);
    internal ColumnDefinitions CreateDefinitions(bool includeGraph = false, bool showGraph = true)
    {
        ColumnDefinitions result = new("*,150,100,75");
        Apply(result);
        if (includeGraph) { result.Insert(0, new ColumnDefinition(showGraph ? GraphWidth : 0, GridUnitType.Pixel)); }
        return result;
    }
    internal void Apply(ColumnDefinitions columns)
    {
        columns[1].Width = new GridLength(ShowAuthor ? 150 : ShowAvatar ? 20 : 0);
        columns[2].Width = new GridLength(ShowDate ? 100 : 0);
        columns[3].Width = new GridLength(ShowId ? DesktopFontMetrics.HashWidth(DesktopFonts.For(DesktopFontRole.Monospace)) : 0);
        if (ShowNotesColumn && columns.Count < 5) { columns.Add(new ColumnDefinition(220, GridUnitType.Pixel)); }
        if (columns.Count > 4) { columns[4].Width = new GridLength(ShowNotesColumn ? 220 : 0); }
    }
}

/// <summary>Pure presentation shared by Browse and modal revision lists; this helper never reads Git or saves preferences.</summary>
internal static class RevisionHistoryPresentation
{
    internal static GitReference[] FilterReferences(IEnumerable<GitReference> references, HistoryViewPreferences view)
        => references.Where(r => (!r.IsRemote || view.ShowRemoteBranches) && (!r.IsTag || view.ShowTags)
            && (!r.Name.StartsWith("refs/sessions/", StringComparison.Ordinal) || view.ShowSessionCheckpoints)
            && (r.Name != "refs/stash" || view.ShowStashes)).ToArray();

    internal static void Apply(Control row, GitCommit commit, HistoryViewPreferences view, IReadOnlyList<GitReference> references,
        (DateTimeOffset Date, string Notes) metadata = default, bool dim = false, Func<string, string>? translate = null)
    {
        DateTimeOffset date = view.ShowAuthorDate || metadata.Date == default ? commit.AuthoredAt : metadata.Date;
        string body = view.ShowBody ? string.Join(' ', commit.Body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) : "";
        string note = metadata.Notes ?? "";
        string details = body + (view.ShowNotes && !view.ShowNotesColumn && note.Length > 0
            ? "  [" + (translate?.Invoke("Notes") ?? "Notes") + "] " + string.Join(' ', note.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) : "");
        RevisionHistoryRow.ApplyPresentation(row, new(view.ShowAvatar, view.ShowAuthor, view.ShowDate, view.ShowId, view.RelativeDate,
            view.ShowNotesColumn, date, details, note, dim, references, view.FillRefLabels));
    }
}
