using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

/// <summary>A compact read-only full-row grid, preserving native order and complete hashes.</summary>
internal sealed class WorktreeTable : UserControl
{
    internal ListBox Rows { get; } = new() { Name = "WorktreeList", SelectionMode = SelectionMode.Single, Focusable = true, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly Grid _headers = new() { Name = "WorktreeHeaders", Height = 24 };
    private readonly Grid _body = new() { RowDefinitions = new("Auto,*") };
    private readonly List<WeakReference<Grid>> _rowGrids = [];
    private readonly double[] _widths = [265, 65, 155, 90];
    private readonly Func<string, string> _translate;
    private double _available;
    private readonly DesktopFontSession _fonts = DesktopFonts.Active;

    internal WorktreeTable(Func<string, string> translate)
    {
        _translate = translate;
        _fonts.Apply(this, DesktopFontRole.Application);
        double rowHeight = DesktopFontMetrics.WorktreeRowHeight(_fonts);
        _headers.Height = rowHeight + 2;
        Rows.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        {
            new Setter(MinHeightProperty, rowHeight), new Setter(PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0)),
            new Setter(BorderThicknessProperty, new Thickness(0)),
            new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
        } });
        ScrollViewer.SetHorizontalScrollBarVisibility(Rows, ScrollBarVisibility.Disabled);
        string[] captions = ["Path", "Type", "Branch", "SHA-1"], names = ["Path", "Type", "Branch", "Sha"];
        for (int index = 0; index < captions.Length; index++)
        {
            TextBlock label = new() { Name = "Worktree" + names[index] + "Header", Text = translate(captions[index]), Margin = new Thickness(4, 3), VerticalAlignment = VerticalAlignment.Center };
            Border heading = ClassicChrome.Border(label, new Thickness(0, 0, 1, 1));
            Grid.SetColumn(heading, index); _headers.Children.Add(heading);
            Thumb resize = new() { Name = "ResizeWorktree" + names[index], Width = 5, HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast),
                Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brushes.Transparent }) };
            int column = index;
            resize.DragDelta += (_, args) => { _widths[column] = Math.Max(column == 3 ? 90 : 45, _widths[column] + args.Vector.X); UpdateWidths(); };
            Grid.SetColumn(resize, index); _headers.Children.Add(resize);
        }
        Rows.ItemTemplate = new FuncDataTemplate<WorktreeSnapshot>((worktree, _) =>
        {
            ColumnDefinitions columns = Columns();
            Grid row = new() { Height = rowHeight, ClipToBounds = true, ColumnDefinitions = columns,
                Width = columns.Sum(column => column.Width.Value), HorizontalAlignment = HorizontalAlignment.Left };
            _rowGrids.Add(new(row));
            if (worktree is null) { return row; }
            string branch = worktree.Branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? worktree.Branch[11..] : worktree.Branch ?? "";
            string[] values = [ExportDialogControls.DisplayPath(worktree.Path), translate(worktree.IsBare ? "Bare" : worktree.IsDetached ? "Detached" : "Branch"), branch, worktree.Head ?? ""];
            for (int column = 0; column < values.Length; column++)
            {
                TextBlock cell = new() { Name = "Worktree" + names[column], Text = values[column], Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = column == 3 ? TextWrapping.Wrap : TextWrapping.NoWrap };
                _fonts.Apply(cell, column == 3 ? DesktopFontRole.Monospace : DesktopFontRole.Application);
                if (!worktree.Exists) { cell.TextDecorations = TextDecorations.Strikethrough; }
                Border border = ClassicChrome.Border(cell, new Thickness(0, 0, 1, 1)); border.ClipToBounds = true; Grid.SetColumn(border, column); row.Children.Add(border);
            }
            ToolTip.SetTip(row, ExportDialogControls.DisplayPath(worktree.Path) + (worktree.Head is null ? "" : "\n" + worktree.Head)
                + (worktree.IsCurrent ? "\n" + translate("Current worktree") : "")
                + (worktree.IsMain ? "\n" + translate("Main worktree") : "")
                + (worktree.IsLocked ? "\n" + translate("Locked") + ": " + worktree.LockedReason : "")
                + (worktree.ValidationError is null ? "" : "\n" + worktree.ValidationError)
                + (worktree.PrunableReason is null ? "" : "\n" + worktree.PrunableReason));
            return row;
        });
        Rows.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.C && args.KeyModifiers == KeyModifiers.Control && Rows.SelectedItem is WorktreeSnapshot selected && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                args.Handled = true;
                await clipboard.SetTextAsync(string.Join('\t', selected.Path, selected.IsBare ? "Bare" : selected.IsDetached ? "Detached" : "Branch",
                    selected.Branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? selected.Branch[11..] : selected.Branch ?? "", selected.Head ?? ""));
            }
        };
        _body.Children.Add(_headers); Grid.SetRow(Rows, 1); _body.Children.Add(Rows);
        ScrollViewer viewport = new() { Name = "WorktreeTableScroll", Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewport.SizeChanged += (_, _) => { _available = Math.Max(0, viewport.Bounds.Width - 2); UpdateWidths(); };
        Content = ClassicChrome.Border(viewport, new Thickness(1)); UpdateWidths();
    }

    internal void SetItems(IReadOnlyList<WorktreeSnapshot> values)
    {
        // The original Path/Type/Branch columns use AllCells. Long paths remain
        // readable through horizontal scrolling rather than silently losing their suffix.
        _widths[0] = Measure(values.Select(value => ExportDialogControls.DisplayPath(value.Path)).Prepend(_translate("Path")));
        _widths[1] = Measure(values.Select(value => _translate(value.IsBare ? "Bare" : value.IsDetached ? "Detached" : "Branch")).Prepend(_translate("Type")));
        _widths[2] = Measure(values.Select(value => value.Branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? value.Branch[11..] : value.Branch ?? "").Prepend(_translate("Branch")));
        Rows.ItemsSource = values; UpdateWidths();
    }

    private double Measure(IEnumerable<string> values)
    {
        return Math.Max(45, values.Max(value =>
        {
            TextBlock text = new() { Text = value };
            _fonts.Apply(text, DesktopFontRole.Application);
            text.Measure(Size.Infinity); return Math.Ceiling(text.DesiredSize.Width) + 10;
        }));
    }

    private ColumnDefinitions Columns()
    {
        ColumnDefinitions result = new();
        for (int column = 0; column < _widths.Length; column++)
        {
            double width = _widths[column];
            if (column == 3) { width = Math.Max(width, _available - _widths.Take(3).Sum() - 16); }
            result.Add(new ColumnDefinition(width, GridUnitType.Pixel) { MinWidth = width, MaxWidth = width });
        }
        return result;
    }

    private void UpdateWidths()
    {
        _headers.ColumnDefinitions = Columns();
        _body.Width = Math.Max(_available, _headers.ColumnDefinitions.Sum(column => column.Width.Value) + 16);
        _headers.Margin = new Thickness(0, 0, 16, 0);
        foreach (WeakReference<Grid> reference in _rowGrids.ToArray())
        {
            if (reference.TryGetTarget(out Grid? row)) { row.ColumnDefinitions = Columns(); row.Width = row.ColumnDefinitions.Sum(column => column.Width.Value); }
            else { _rowGrids.Remove(reference); }
        }
    }
}
