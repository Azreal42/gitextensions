using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // Host budget for the flexible message cell, in addition to the visible fixed columns and graph.
    // These limits do not change shared information views or the user's explicit Left/Right/Below choice.
    private const double MinimumHistoryMessageWidth = 160;
    private const double MinimumSideCommitInfoWidth = 120;

    private void RememberCommitInfoWidth()
    {
        if (_revisionPanels is null || _commitInfo.Parent != _revisionPanels || _commitInfoPosition == CommitInfoPosition.BelowList) { return; }
        ColumnDefinition column = _revisionPanels.ColumnDefinitions[_commitInfoPosition == CommitInfoPosition.LeftwardFromList ? 0 : 2];
        // A narrow host only limits MaxWidth. Preserve the requested pixel width, including a real splitter drag,
        // rather than replacing it with the clamped Bounds.Width when moving through Below.
        if (column.Width.IsAbsolute && double.IsFinite(column.Width.Value) && column.Width.Value > 0) { _commitInfoWidth = column.Width.Value; }
    }

    private void LimitCommitInfoWidth()
    {
        if (_revisionPanels is null || _lifetime.IsCancellationRequested || _commitInfoPosition == CommitInfoPosition.BelowList || _revisionPanels.Bounds.Width <= 0) { return; }
        double fixedColumns = FixedHistoryColumnWidth(RevisionHistoryColumns.From(_historyView).CreateDefinitions());
        double rowChrome = 0;
        foreach (Grid row in _commits.GetVisualDescendants().OfType<Grid>().Where(row => row.Name is "RevisionHistoryRow" or "ArtificialRevisionHistoryRow"))
        {
            // CI adds its columns to realized rows after the canonical definitions. Read their actual allocation
            // instead of copying the author/date/hash/notes/CI width policy into this host.
            fixedColumns = Math.Max(fixedColumns, FixedHistoryColumnWidth(row.ColumnDefinitions));
            double inset = row.Margin.Left + row.Margin.Right;
            if (row.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is { } item)
            {
                inset += item.Padding.Left + item.Padding.Right + item.BorderThickness.Left + item.BorderThickness.Right;
            }
            Control? message = row.Children.FirstOrDefault(child => child.Name == "RevisionMessage");
            if (message is not null) { inset += message.Margin.Left + message.Margin.Right; }
            rowChrome = Math.Max(rowChrome, inset);
        }
        double listChrome = 0;
        if (_commits.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll
            && scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
            .FirstOrDefault(presenter => ReferenceEquals(presenter.TemplatedParent, scroll)) is { Bounds.Width: > 0 } viewport)
        {
            // Bounds are DIPs even when a virtualizing panel gives ScrollViewer a logical Viewport.
            listChrome = Math.Max(0, _commits.Bounds.Width - viewport.Bounds.Width);
        }
        double graph = _historyGraph.IsVisible ? Math.Max(_historyGraph.Bounds.Width, _historyGraph.DesiredSize.Width) : 0;
        ColumnDefinition separator = _revisionPanels.ColumnDefinitions[1];
        double splitter = separator.Width.IsAbsolute ? separator.Width.Value : separator.ActualWidth;
        double history = fixedColumns + rowChrome + listChrome + graph + MinimumHistoryMessageWidth;
        double maximum = Math.Max(MinimumSideCommitInfoWidth, _revisionPanels.Bounds.Width - splitter - history);
        ColumnDefinition information = _revisionPanels.ColumnDefinitions[_commitInfoPosition == CommitInfoPosition.LeftwardFromList ? 0 : 2];
        // LayoutUpdated also observes font, graph, visible-column and CI changes. No work is scheduled and
        // no control is reparented; a stable budget must not invalidate another layout.
        if (double.IsFinite(maximum) && information.MaxWidth != maximum) { information.MaxWidth = maximum; }
    }

    private static double FixedHistoryColumnWidth(ColumnDefinitions columns)
        => columns.Where(column => column.Width.IsAbsolute).Sum(column => column.Width.Value);
}
