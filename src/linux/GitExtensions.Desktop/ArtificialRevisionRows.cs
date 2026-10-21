using GitExtensions.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>The compact worktree/index rows shared by Browse and the comparison commit chooser.</summary>
internal sealed record ArtificialRevisionRows(Grid View, Button Working, Button Index, TextBlock WorkingState, TextBlock IndexState)
{
    /// <summary>The same label painter as real reference badges, hosted in a selectable native history row.</summary>
    internal static Grid CreateItem(ArtificialHistoryRevision revision, Func<string, string> translate,
        HistoryViewPreferences view, bool showStatus, string prefix = "Choose")
    {
        Grid row = new() { Name = "ArtificialRevisionHistoryRow", Tag = revision,
            ColumnDefinitions = RevisionHistoryColumns.From(view).CreateDefinitions(), ClipToBounds = true };
        Button label = new() { Name = prefix + (revision.Kind == GitCompareRevisionKind.WorkingTree ? "WorkingDirectoryRevision" : "IndexRevision"),
            Focusable = false, MinHeight = 0, MinWidth = 0, Padding = default, BorderThickness = default, Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Content = RevisionHistoryRow.CreateArtificialLabel(translate(revision.Subject), view.FillRefLabels) };
        Image status = Controls.OriginalMenuItems.Icon("RepoStateUnknown.png");
        status.Name = "ArtificialRevisionStatus"; status.IsVisible = showStatus; status.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(status, translate("Unknown"));
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { label, status } });
        return row;
    }

    internal static ArtificialRevisionRows Create(double rowHeight, Func<string, string> translate, string prefix = "", double graphWidth = 20)
    {
        Grid rows = new() { Name = prefix + "WorkingRevisions", ColumnDefinitions = new ColumnDefinitions { new(graphWidth, GridUnitType.Pixel), new(1, GridUnitType.Star) },
            RowDefinitions = new RowDefinitions { new(rowHeight, GridUnitType.Pixel), new(rowHeight, GridUnitType.Pixel) } };
        Button[] buttons = new Button[2]; TextBlock[] states = new TextBlock[2];
        for (int i = 0; i < 2; i++)
        {
            Canvas graph = new() { Width = graphWidth, Height = rowHeight, IsVisible = graphWidth > 0 };
            graph.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(8, 0), EndPoint = new Point(8, rowHeight), Stroke = Brush.Parse("#d3d3d3"), StrokeThickness = 1.5 });
            Avalonia.Controls.Shapes.Ellipse node = new() { Width = 7, Height = 7, Fill = Brush.Parse("#d3d3d3") };
            Canvas.SetLeft(node, 4.5); Canvas.SetTop(node, (rowHeight - 7) / 2); graph.Children.Add(node);
            Grid.SetRow(graph, i); rows.Children.Add(graph);
            Button action = new RevisionSearchButton { Name = prefix + (i == 0 ? "WorkingDirectoryRevision" : "IndexRevision"), Height = rowHeight, MinHeight = rowHeight,
                MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 0), Margin = new Thickness(2, 0, 0, 0) };
            action.Content = new TextBlock { Text = translate(i == 0 ? "Working directory" : "Commit index"), VerticalAlignment = VerticalAlignment.Center };
            TextBlock state = new() { Text = "✓", Foreground = Brush.Parse("#24c221"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            StackPanel content = new() { Orientation = Orientation.Horizontal, Height = rowHeight, Children = { action, state } };
            Grid.SetRow(content, i); Grid.SetColumn(content, 1); rows.Children.Add(content); buttons[i] = action; states[i] = state;
        }
        rows.Bind(Panel.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ClassicWindowBrush"));
        return new(rows, buttons[0], buttons[1], states[0], states[1]);
    }
}
