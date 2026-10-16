using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

public sealed partial class SubmoduleRemovalDialog
{
    private readonly Grid _layout = new() { Name = "SubmodulesLayout", ColumnDefinitions = new("222,5,*") };
    private readonly Grid _tableHeaders = new() { Name = "SubmodulesHeaders", Height = 24, Margin = new Thickness(0, 0, 16, 0) };
    private readonly Grid _tableBody = new() { RowDefinitions = new("Auto,*") };
    private readonly List<WeakReference<Grid>> _rowGrids = [];
    private double _tableAvailable, _statusWidth = 100;

    private Button OriginalButton(string name, string caption, Func<Task> action, string? icon = null)
    {
        Button button = Action(name, Original(name, caption), action);
        button.Content = icon is null ? Original(name, caption)
            : WorktreeDialogControls.ButtonContent(Original(name, caption), icon);
        return button;
    }

    private void BuildOriginalLayout()
    {
        _layout.ColumnDefinitions[0].MinWidth = 210; _layout.ColumnDefinitions[2].MinWidth = 380;
        _layout.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(_preferences.Load().SplitterDistance, 210, Width - 385));
        Grid left = new() { RowDefinitions = new("*,Auto") };
        left.Children.Add(BuildTable());
        _add.Margin = new Thickness(4); Grid.SetRow(_add, 1); left.Children.Add(_add);
        _layout.Children.Add(left);
        GridSplitter splitter = new() { Name = "SubmodulesSplitter", Width = 5, MinWidth = 5, ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); _layout.Children.Add(splitter);
        Grid right = new() { RowDefinitions = new("*,Auto") };
        Grid details = new() { Name = "SubmoduleDetailsFields", ColumnDefinitions = new("112,*"), RowDefinitions = new("36,36,36,36,36,36") };
        (string Key, string Label, TextBox Input)[] fields = [("label1", "Name", _name), ("label2", "Remote path", _remote),
            ("label3", "Local path", _path), ("label4", "Commit", _commit), ("label5", "Branch", _branch), ("label6", "Status", _state)];
        for (int i = 0; i < fields.Length; i++)
        {
            Label label = new() { Content = Original(fields[i].Key, fields[i].Label), Target = fields[i].Input,
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(4, 0) };
            Grid.SetRow(label, i); details.Children.Add(label);
            Control input = fields[i].Input; input.VerticalAlignment = VerticalAlignment.Center;
            if (input == _branch)
            {
                Grid branch = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 3 };
                branch.Children.Add(_branch); Grid.SetColumn(_pull, 1); branch.Children.Add(_pull); input = branch;
            }
            else if (input == _name || input == _state)
            {
                // These two original fields occupy one of the two equal value columns.
                Grid half = new() { ColumnDefinitions = new("*,*") }; half.Children.Add(input); input = half;
            }
            input.Margin = new Thickness(3, 0); Grid.SetRow(input, i); Grid.SetColumn(input, 1); details.Children.Add(input);
        }
        Grid group = ClassicChrome.Group("SubmoduleDetails", Original("groupBox1", "Details"), details, new Thickness(6, 9, 6, 6));
        group.Margin = new Thickness(4, 0, 4, 4); right.Children.Add(group);
        StackPanel actions = ExportDialogControls.Buttons(_sync, _update, _remove); actions.Name = "SubmoduleActions";
        actions.Spacing = 8; actions.Margin = new Thickness(4); Grid.SetRow(actions, 1); right.Children.Add(actions);
        Grid.SetColumn(right, 2); _layout.Children.Add(right); Root.Children.Add(_layout);
        SizeChanged += (_, _) =>
        {
            // The original FixedPanel.Panel1 preserves an absolute width, not a ratio.
            double available = Math.Max(210, ClientSize.Width - 385);
            if (_layout.ColumnDefinitions[0].Width.Value > available) { _layout.ColumnDefinitions[0].Width = new GridLength(available); }
        };
    }

    private Control BuildTable()
    {
        _modules.Padding = new Thickness(0); _modules.BorderThickness = new Thickness(0);
        _modules.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        {
            new Setter(MinHeightProperty, 22d), new Setter(HeightProperty, 22d), new Setter(PaddingProperty, new Thickness(0)),
            new Setter(MarginProperty, new Thickness(0)), new Setter(BorderThicknessProperty, new Thickness(0)),
            new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
        } });
        ScrollViewer.SetHorizontalScrollBarVisibility(_modules, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_modules, ScrollBarVisibility.Visible);
        (string Name, string Key, string Caption)[] columns = [("RemovalNameHeader", "nameDataGridViewTextBoxColumn", "Name"), ("RemovalStatusHeader", "Status", "Status")];
        for (int i = 0; i < columns.Length; i++)
        {
            TextBlock caption = new() { Name = columns[i].Name, Text = Translations.Translate("FormSubmodules", columns[i].Key, "HeaderText", columns[i].Caption),
                Margin = new Thickness(4, 3), VerticalAlignment = VerticalAlignment.Center };
            Border cell = ClassicChrome.Border(caption, new Thickness(0, 0, 1, 1)); Grid.SetColumn(cell, i); _tableHeaders.Children.Add(cell);
            Thumb resize = new() { Name = i == 0 ? "ResizeSubmoduleName" : "ResizeSubmoduleStatus", Width = 5,
                HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast),
                Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brushes.Transparent }) };
            int column = i;
            resize.DragDelta += (_, args) => { _statusWidth = Math.Max(45, _statusWidth + (column == 0 ? -args.Vector.X : args.Vector.X)); UpdateTableWidths(); };
            Grid.SetColumn(resize, i); _tableHeaders.Children.Add(resize);
        }
        _modules.ItemTemplate = new FuncDataTemplate<GitSubmoduleSnapshot>((item, _) =>
        {
            ColumnDefinitions columns = TableColumns();
            Grid row = new() { ColumnDefinitions = columns, Width = columns.Sum(column => column.Width.Value), Height = 22,
                ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
            _rowGrids.Add(new(row));
            TextBlock name = new() { Name = "RemovalNameCell", Text = item is null ? "" : ExportDialogControls.DisplayPath(item.Name),
                Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock status = new() { Name = "RemovalStatusCell", Text = item?.Status,
                Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Border first = ClassicChrome.Border(name, new Thickness(0, 0, 1, 1)); row.Children.Add(first);
            Border second = ClassicChrome.Border(status, new Thickness(0, 0, 1, 1)); Grid.SetColumn(second, 1); row.Children.Add(second);
            ToolTip.SetTip(row, item is null ? null : ExportDialogControls.DisplayPath(item.Path) + "\n" + item.Status
                + (item.ValidationError is null ? "" : "\n" + item.ValidationError));
            return row;
        });
        _modules.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.C && args.KeyModifiers == KeyModifiers.Control && Selected is { } selected && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                args.Handled = true;
                // DataGridView's UnicodeText replaces embedded TABs, retaining CR/LF.
                string text = CopyRow(selected.Name, selected.Status);
                // FullRowSelect + AutoHeader includes headers when every visible row is selected.
                if (_modules.Items.Count == 1)
                {
                    string[] headers = _tableHeaders.Children.OfType<Border>().Select(cell => ((TextBlock)cell.Child!).Text ?? "").ToArray();
                    text = CopyRow(headers) + "\r\n" + text;
                }
                await clipboard.SetTextAsync(text);
            }
        };
        _tableBody.Children.Add(_tableHeaders); Grid.SetRow(_modules, 1); _tableBody.Children.Add(_modules);
        ScrollViewer viewport = new() { Name = "SubmodulesTableScroll", Content = _tableBody,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewport.SizeChanged += (_, _) => { _tableAvailable = Math.Max(0, viewport.Bounds.Width); UpdateTableWidths(); };
        UpdateTableWidths(); return viewport;
    }

    private static string CopyRow(params string[] values) => string.Join('\t', values.Select(value => value.Replace('\t', ' ')));

    private ColumnDefinitions TableColumns() => new()
    {
        new ColumnDefinition(Math.Max(45, _tableAvailable - _statusWidth - 16), GridUnitType.Pixel),
        new ColumnDefinition(_statusWidth, GridUnitType.Pixel)
    };

    private void UpdateTableWidths()
    {
        _tableHeaders.ColumnDefinitions = TableColumns();
        double width = _tableHeaders.ColumnDefinitions.Sum(column => column.Width.Value);
        _tableBody.Width = Math.Max(_tableAvailable, width + 16);
        foreach (WeakReference<Grid> reference in _rowGrids.ToArray())
        {
            if (reference.TryGetTarget(out Grid? row)) { row.ColumnDefinitions = TableColumns(); row.Width = width; }
            else { _rowGrids.Remove(reference); }
        }
    }
}
