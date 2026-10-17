using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Styling;
using GitExtensions.Linux.Scripts;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The draft-only ControlHotkeys editor shared by the Settings window.</summary>
internal sealed class HotkeysSettingsPage : UserControl
{
    private readonly ListBox _contexts = new() { Name = "cmbSettings", SelectionMode = SelectionMode.Single };
    private readonly ListBox _mappings = new() { Name = "listMappings", SelectionMode = SelectionMode.Single };
    private readonly TranslationCatalog _translations;
    internal HotkeyBindings Draft { get; }
    internal HotkeyCaptureTextBox Capture { get; }

    internal HotkeysSettingsPage(HotkeyBindings initial, TranslationCatalog? translations = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        Draft = initial.Clone(); _translations = translations ?? TranslationCatalog.LoadDefault();
        // Settings sizes itself to its active page. Bound the viewport so that
        // the command list scrolls instead of growing the whole dialog.
        Name = "HotkeysSettingsPage"; MinWidth = 670; MinHeight = 330; Height = 330;
        Capture = new(gesture => Draft.IsAssigned(gesture), Text("txtHotkey", "None")) { Name = "txtHotkey" };
        _contexts.ItemsSource = HotkeyCatalog.Contexts;
        _contexts.SelectionChanged += (_, _) => ReloadMappings();
        _mappings.ItemTemplate = new FuncDataTemplate<HotkeyCommand>((command, _) =>
        {
            if (command is null) { return new TextBlock(); }
            Grid row = Columns();
            row.Children.Add(new TextBlock { Name = "HotkeyCommandName", Text = command.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            TextBlock key = new() { Name = "HotkeyGesture", Text = Draft.Get(command.Context, command.Code)?.ToString() ?? Text("txtHotkey", "None") };
            Grid.SetColumn(key, 1); row.Children.Add(key); return row;
        });
        _mappings.SelectionChanged += (_, _) => Capture.CapturedGesture = _mappings.SelectedItem is HotkeyCommand command
            ? Draft.Get(command.Context, command.Code) : null;
        foreach (ListBox list in new[] { _contexts, _mappings })
        {
            list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
            {
                Setters = { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(MinHeightProperty, 22d) }
            });
            ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        }

        Grid root = new() { Name = "controlHotkeys", RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 6 };
        root.Children.Add(new TextBlock { Name = "lHotkeyableItems", Text = Text("lHotkeyableItems", "Hotkeyable Items") });
        Grid lists = new() { ColumnDefinitions = new ColumnDefinitions("200,8,*") };
        lists.Children.Add(ClassicChrome.Border(_contexts, new Thickness(1)));
        Grid table = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid headers = Columns(); headers.Margin = new Thickness(4, 2);
        headers.Children.Add(new TextBlock { Name = "columnCommand", Text = Text("columnCommand", "Command") });
        TextBlock keyHeader = new() { Name = "columnKey", Text = Text("columnKey", "Key") };
        Grid.SetColumn(keyHeader, 1); headers.Children.Add(keyHeader);
        table.Children.Add(ClassicChrome.Border(headers, new Thickness(0, 0, 0, 1)));
        Grid.SetRow(_mappings, 1); table.Children.Add(_mappings);
        Border mappingFrame = ClassicChrome.Border(table, new Thickness(1)); Grid.SetColumn(mappingFrame, 2); lists.Children.Add(mappingFrame);
        Grid.SetRow(lists, 1); root.Children.Add(lists);

        Grid editor = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        editor.Children.Add(new TextBlock { Name = "lHotkey", Text = Text("lHotkey", "Hotkey"), MinWidth = 100, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(Capture, 1); editor.Children.Add(Capture);
        Button apply = new() { Name = "bApply", Content = Text("bApply", "Apply"), MinWidth = 80 };
        apply.Click += (_, _) =>
        {
            if (_mappings.SelectedItem is not HotkeyCommand command) { return; }
            Draft.Set(command.Context, command.Code, Capture.CapturedGesture); ReloadMappings();
        };
        Grid.SetColumn(apply, 2); editor.Children.Add(apply);
        Button clear = new() { Name = "bClear", Content = Text("bClear", "Clear"), MinWidth = 80 };
        clear.Click += (_, _) =>
        {
            if (_mappings.SelectedItem is not HotkeyCommand command) { return; }
            Draft.Set(command.Context, command.Code, null); ReloadMappings();
        };
        Grid.SetColumn(clear, 3); editor.Children.Add(clear);
        Grid.SetRow(editor, 2); root.Children.Add(editor);
        Button reset = new() { Name = "bResetToDefaults", Content = Text("bResetToDefaults", "Reset all Hotkeys to defaults"), MinWidth = 209, HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { Draft.ResetToDefaults(); _contexts.SelectedItem = null; ReloadMappings(); };
        Grid.SetRow(reset, 3); root.Children.Add(reset);
        Content = root;
    }

    internal void UpdateScripts(IEnumerable<ScriptDefinition> scripts)
    {
        Draft.UpdateScripts(scripts);
        ReloadMappings();
    }

    private void ReloadMappings()
    {
        _mappings.SelectedItem = null;
        // The template reads the mutable draft. Recreate its rows even when
        // the catalogue collection itself is unchanged after Apply or Clear.
        _mappings.ItemsSource = null;
        _mappings.ItemsSource = _contexts.SelectedItem is string context ? Draft.Commands(context) : [];
        Capture.CapturedGesture = null;
    }

    private string Text(string item, string source) => _translations.Translate("ControlHotkeys", item, "Text", source);
    private static Grid Columns() => new() { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 6 };
}
