using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

public sealed class PluginDialog : RepositoryDialog
{
    private readonly INativePlugin _plugin;
    private readonly Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task>? _applySettings;
    private readonly Dictionary<string, Func<string?>> _values = [];
    private readonly Dictionary<string, Control> _fields = [];
    private readonly ListBox _rows = new() { Name = "PluginRows", SelectionMode = SelectionMode.Multiple };
    private readonly Grid _headers = new() { Height = 24 };
    private readonly Grid _table = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly TextBox _text = new() { Name = "PluginText", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _links = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    public PluginResult? LastResult { get; private set; }

    public PluginDialog(GitRepository repository, INativePlugin plugin, IReadOnlyDictionary<string, string?>? savedValues = null,
        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task>? applySettings = null) : base(repository, plugin.Descriptor.Name)
    {
        DesktopFonts.Apply(_text, DesktopFontRole.Monospace);
        _plugin = plugin;
        _applySettings = applySettings;
        Width = 1040;
        Height = 740;
        MinWidth = 720;
        MinHeight = 500;
        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(10, 6) };
        foreach (PluginParameter parameter in plugin.Descriptor.Parameters)
        {
            AddParameter(fields, savedValues?.TryGetValue(parameter.Key, out string? saved) is true && saved is not null
                ? parameter with { DefaultValue = saved } : parameter);
        }
        StackPanel header = new() { Children = { new TextBlock { Text = T(plugin.Descriptor.Description), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) }, fields } };
        DockPanel.SetDock(header, Dock.Top);
        Root.Children.Add(header);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10) };
        buttons.Children.Add(Action("RunPlugin", "Run", ExecuteAsync));
        if (_fields.ContainsKey("branches"))
        {
            buttons.Children.Add(Action("SelectPluginRows", "Use selected branches", () =>
            {
                ((TextBox)_fields["branches"]).Text = string.Join('\n', _rows.SelectedItems?.OfType<PluginRow>().Select(row => row.Key) ?? []);
                return Task.CompletedTask;
            }));
        }
        buttons.Children.Add(Action("ClosePlugin", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Grid results = new() { RowDefinitions = new RowDefinitions("2*,4,1*,Auto"), Margin = new Thickness(10, 0) };
        _table.Children.Add(ClassicChrome.Border(_headers, new Thickness(0, 0, 0, 1)));
        Grid.SetRow(_rows, 1);
        _table.Children.Add(_rows);
        results.Children.Add(new ScrollViewer { Content = _table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        GridSplitter splitter = new() { Height = 4, ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 1);
        results.Children.Add(splitter);
        Grid.SetRow(_text, 2);
        results.Children.Add(_text);
        Grid.SetRow(_links, 3);
        results.Children.Add(_links);
        Root.Children.Add(results);
        Track(_rows);
        _rows.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(HeightProperty, 24d), new Setter(MinHeightProperty, 24d), new Setter(PaddingProperty, new Thickness(0)) }
        });
    }

    public override Task InitializeAsync() => Task.CompletedTask;

    private void AddParameter(Grid grid, PluginParameter parameter)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TextBlock label = new() { Text = T(parameter.Label), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(label, row);
        grid.Children.Add(label);
        Control input;
        switch (parameter.Kind)
        {
            case PluginParameterKind.Boolean:
                CheckBox boolean = new() { IsChecked = bool.TryParse(parameter.DefaultValue, out bool value) && value };
                _values[parameter.Key] = () => (boolean.IsChecked == true).ToString(CultureInfo.InvariantCulture);
                input = boolean;
                break;
            case PluginParameterKind.Choice:
                ComboBox choices = new() { ItemsSource = parameter.Choices, SelectedItem = parameter.DefaultValue, HorizontalAlignment = HorizontalAlignment.Stretch };
                _values[parameter.Key] = () => choices.SelectedItem as string;
                input = choices;
                break;
            case PluginParameterKind.Integer:
            case PluginParameterKind.Number:
                decimal.TryParse(parameter.DefaultValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number);
                NumericUpDown numeric = new() { Value = number, Minimum = 0, Increment = parameter.Kind == PluginParameterKind.Integer ? 1m : 0.1m, FormatString = parameter.Kind == PluginParameterKind.Integer ? "0" : "0.###", HorizontalAlignment = HorizontalAlignment.Left, Width = 140 };
                _values[parameter.Key] = () => numeric.Value?.ToString(CultureInfo.InvariantCulture);
                input = numeric;
                break;
            default:
                TextBox text = new() { Text = parameter.DefaultValue, AcceptsReturn = parameter.Kind == PluginParameterKind.Multiline, MinHeight = parameter.Kind == PluginParameterKind.Multiline ? 50 : 24, MaxHeight = parameter.Kind == PluginParameterKind.Multiline ? 80 : 24 };
                if (parameter.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) { text.PasswordChar = '●'; }
                _values[parameter.Key] = () => text.Text;
                input = text;
                break;
        }
        input.Name = parameter.Key;
        input.Margin = new Thickness(0, 2);
        if (parameter.Hint is not null) { ToolTip.SetTip(input, T(parameter.Hint)); }
        Grid.SetColumn(input, 1);
        Grid.SetRow(input, row);
        grid.Children.Add(input);
        _fields[parameter.Key] = input;
        Track(input);
    }

    private async Task ExecuteAsync()
    {
        Dictionary<string, string?> values = _values.ToDictionary(entry => entry.Key, entry => entry.Value());
        bool confirmed = false;
        if (_plugin.Descriptor.NeedsConfirmation(values))
        {
            string selected = values.GetValueOrDefault("branches") ?? "";
            string message = T(_plugin.Descriptor.ConfirmationText ?? "Continue with this action?") + (selected.Length == 0 ? "" : "\n\n" + selected);
            confirmed = await Dialogs.ConfirmAsync(this, Title ?? T("Confirm"), message);
            if (!confirmed) { return; }
        }
        await RunAsync(_plugin.Descriptor.Name, async token =>
        {
            LastResult = await _plugin.ExecuteAsync(new PluginContext(Repository, confirmed), values, token);
            // Keep acquired/possible Git effects even if persisting plugin preferences later fails.
            if (LastResult.RepositoryMayHaveChanges) { MarkRepositoryMutationAttempted(); }
            if (LastResult.RepositoryChanged) { MarkRepositoryChanged(); }
            if (LastResult.Success && _applySettings is not null) { await _applySettings(values, token); }
            _text.Text = LastResult.Text;
            ShowTable(LastResult.Table);
            _links.Children.Clear();
            foreach (PluginLink link in LastResult.Links ?? [])
            {
                if (link.Uri.Scheme is not ("http" or "https")) { continue; }
                _links.Children.Add(Action("PluginLink" + _links.Children.Count, link.Label, () =>
                {
                    LoggedExternalProcess.Start(DesktopResourceLauncher.CreateStartInfo(link.Uri.AbsoluteUri), GitCommandLogProcessKind.OpenResource);
                    return Task.CompletedTask;
                }));
            }
            Status.Text = T(LastResult.Success ? "Completed" : "Some operations failed. See results.");
        });
    }

    private void ShowTable(PluginTable? table)
    {
        _headers.Children.Clear();
        _rows.ItemsSource = table?.Rows;
        if (table is null) { return; }
        string definitions = string.Join(',', table.Columns.Select((_, index) => index == 0 ? "220" : "160"));
        _headers.ColumnDefinitions = new ColumnDefinitions(definitions);
        _table.MinWidth = 220 + (table.Columns.Count - 1) * 160;
        for (int index = 0; index < table.Columns.Count; index++)
        {
            TextBlock label = new() { Text = T(table.Columns[index]), Margin = new Thickness(4, 2) };
            Grid.SetColumn(label, index);
            _headers.Children.Add(label);
        }
        _rows.ItemTemplate = new FuncDataTemplate<PluginRow>((row, _) =>
        {
            if (row is null) { return new TextBlock(); }
            Grid cells = new() { ColumnDefinitions = new ColumnDefinitions(definitions), Height = 24 };
            for (int index = 0; index < row.Cells.Count; index++)
            {
                TextBlock cell = new() { Text = row.Cells[index], Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(cell, row.Cells[index]);
                Grid.SetColumn(cell, index);
                cells.Children.Add(cell);
            }
            return cells;
        });
    }
}
