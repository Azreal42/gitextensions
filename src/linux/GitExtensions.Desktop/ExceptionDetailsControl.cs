using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class ExceptionDetailsControl : UserControl
{
    private readonly TreeView _tree = new() { Name = "exceptionTreeView", Height = 60, Focusable = true };
    private readonly ListBox _properties = new() { Name = "exceptionDetailsListView", SelectionMode = SelectionMode.Single, Focusable = true };
    internal ExceptionReportNode? SelectedException => (_tree.SelectedItem as TreeViewItem)?.Tag as ExceptionReportNode;

    internal ExceptionDetailsControl(ExceptionReportNode root, TranslationCatalog translations, Func<ExceptionReportProperty, Task> openDetail)
    {
        Name = "ExceptionDetails";
        _properties.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        { new Setter(HeightProperty, DesktopFontMetrics.TreeRowHeight), new Setter(MinHeightProperty, DesktopFontMetrics.TreeRowHeight),
          new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0)),
          new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _properties.ItemTemplate = new FuncDataTemplate<ExceptionReportProperty>((property, _) => Row(property));
        _tree.SelectionChanged += (_, _) =>
        {
            _properties.ItemsSource = SelectedException?.Properties;
            _properties.SelectedIndex = _properties.Items.Count > 0 ? 0 : -1;
        };
        _properties.DoubleTapped += async (_, args) =>
        {
            if (args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any()
                && _properties.SelectedItem is ExceptionReportProperty property) { await openDetail(property); }
        };
        _properties.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && _properties.SelectedItem is ExceptionReportProperty property)
            { args.Handled = true; await openDetail(property); }
        };
        TreeViewItem first = Node(root); _tree.Items.Add(first);
        Stack<(ExceptionReportNode Value, TreeViewItem Item)> pending = new(); pending.Push((root, first));
        while (pending.TryPop(out var pair))
        {
            foreach (ExceptionReportNode child in pair.Value.Children)
            { TreeViewItem item = Node(child); pair.Item.Items.Add(item); pending.Push((child, item)); }
        }
        Grid header = new() { ColumnDefinitions = new("101,350"), HorizontalAlignment = HorizontalAlignment.Left };
        header.Children.Add(Cell(translations.Translate("Property"), false));
        Control information = Cell(translations.Translate("Information"), false); Grid.SetColumn(information, 1); header.Children.Add(information);
        Grid list = new() { RowDefinitions = new("Auto,*"), MinWidth = 451, Children = { header, At(_properties, 1) } };
        ScrollViewer scroller = new() { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new Grid { RowDefinitions = new("Auto,60,Auto,*"), Children =
        {
            new TextBlock { Name = "exceptionLabel", Text = translations.Translate("Exception(s):") },
            At(ClassicTreeViewAppearance.Wrap(_tree, inset: 0), 1),
            At(new TextBlock { Name = "exceptionDetailsLabel", Text = translations.Translate("Exception Details (double click on items to see details):"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 2) }, 2), At(scroller, 3)
        } };
        _tree.SelectedItem = first;
    }
    internal void FocusDetails() => _tree.Focus();
    internal void RestorePropertyFocus() => _properties.Focus();
    private static TreeViewItem Node(ExceptionReportNode node) => new() { Tag = node, IsExpanded = true, Header = new TextBlock { Text = node.Type } };
    private static Grid Row(ExceptionReportProperty? property)
    {
        Grid row = new() { ColumnDefinitions = new("101,350"), HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(Cell(property?.Name ?? "", property?.Extended == true));
        Control information = Cell(property?.Value ?? "", false); Grid.SetColumn(information, 1); row.Children.Add(information);
        ToolTip.SetTip(row, property?.Value); return row;
    }
    private static Border Cell(string text, bool bold) => ClassicChrome.Border(new TextBlock
        { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center }, new Thickness(0, 0, 1, 1));
    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
