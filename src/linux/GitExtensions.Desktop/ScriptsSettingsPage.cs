using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>A draft editor, never a script executor or a preview of expansion.</summary>
internal sealed class ScriptsSettingsPage : UserControl
{
    private readonly List<ScriptDefinition> _scripts;
    private readonly TranslationCatalog _translations;
    private readonly ListBox _list = new() { Name = "lvScripts", SelectionMode = SelectionMode.Single };
    private readonly TextBlock _notice = new() { Name = "ScriptsNotice", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Dictionary<string, Control> _editors = [];
    private readonly Button _up, _down, _delete;
    private readonly string[] _iconKeys;
    private ScriptsPreferencesLoadResult _baseline;
    private int? _selectedId;
    private bool _loading, _closed;
    private Window? _owner;
    private SimpleHelpDisplayDialog? _help;

    internal IReadOnlyList<ScriptDefinition> Draft => Array.AsReadOnly(_scripts.ToArray());
    internal bool IsDirty => !_scripts.SequenceEqual(_baseline.Scripts);
    internal event Action<IReadOnlyList<ScriptDefinition>>? DraftChanged;

    internal ScriptsSettingsPage(ScriptsPreferencesLoadResult baseline, TranslationCatalog translations)
    {
        _baseline = baseline; _translations = translations; _scripts = baseline.Scripts.ToList();
        Name = "ScriptsSettingsPage"; MinWidth = 670; Height = 390;
        _iconKeys = ScriptPresentation.IconKeys.ToArray();
        _list.ItemTemplate = new FuncDataTemplate<ScriptDefinition>((script, _) => script is null ? new TextBlock() : Row(script));
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(MinHeightProperty, 24d) } });
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) { return; }
            _selectedId = (_list.SelectedItem as ScriptDefinition)?.HotkeyCommandIdentifier;
            ShowSelected();
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid header = Columns(); header.Margin = new Thickness(4, 2);
        header.Children.Add(new TextBlock { Text = Text("chdrNames", "Name"), Margin = new Thickness(24, 0, 0, 0) });
        TextBlock eventHeader = new() { Text = Text("chdrEvents", "Event") }; Grid.SetColumn(eventHeader, 1); header.Children.Add(eventHeader);
        TextBlock commandHeader = new() { Text = Text("chdrCommand", "Command") }; Grid.SetColumn(commandHeader, 2); header.Children.Add(commandHeader);
        Grid table = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        table.Children.Add(ClassicChrome.Border(header, new Thickness(0, 0, 0, 1)));
        Grid.SetRow(_list, 1); table.Children.Add(_list);
        StackPanel buttons = new() { Spacing = 4 };
        Button add = ToolButton("btnAdd", "RemoteAdd.png", "Add", () =>
        {
            try
            {
                ScriptDefinition script = new() { HotkeyCommandIdentifier = ScriptConfiguration.NextIdentifier(_scripts), Name = "<New Script>", Enabled = true };
                _scripts.Add(script); _selectedId = script.HotkeyCommandIdentifier;
                RefreshRows(); ShowSelected(); Changed(); _editors["Name"].Focus();
            }
            catch (OverflowException error) { ShowSaveError(error); }
        });
        _delete = ToolButton("btnDelete", "RemoteDelete.png", "Delete", () =>
        {
            int index = SelectedIndex(); if (index < 0) { return; }
            _scripts.RemoveAt(index); _selectedId = _scripts.FirstOrDefault()?.HotkeyCommandIdentifier;
            RefreshRows(); ShowSelected(); Changed();
            if (_scripts.Count == 0) { add.Focus(); }
        });
        _up = ToolButton("btnMoveUp", "ArrowUp.png", "Move up", () => Move(-1));
        _down = ToolButton("btnMoveDown", "ArrowDown.png", "Move down", () => Move(1));
        Button help = new() { Name = "btnArgumentsHelp", Content = "?", MinWidth = 26, MinHeight = 26 };
        ToolTip.SetTip(help, Text("_scriptSettingsPageHelpDisplayArgumentsHelp", "Arguments help"));
        help.Click += (_, _) => ShowHelp();
        buttons.Children.Add(add); buttons.Children.Add(_delete); buttons.Children.Add(_up); buttons.Children.Add(_down); buttons.Children.Add(help);
        Grid upper = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        upper.Children.Add(ClassicChrome.Border(table, new Thickness(1))); Grid.SetColumn(buttons, 1); upper.Children.Add(buttons);

        StackPanel editor = new() { Name = "propertyGrid1", Spacing = 5 };
        StackPanel scriptFields = new() { Spacing = 3 };
        scriptFields.Children.Add(StringField("Name", "Name", s => s.Name, (s, v) => s with { Name = v }));
        scriptFields.Children.Add(BoolField("Enabled", "Enabled", s => s.Enabled, (s, v) => s with { Enabled = v }));
        scriptFields.Children.Add(StringField("Command", "Command", s => s.Command, (s, v) => s with { Command = v }, filePicker: true));
        scriptFields.Children.Add(StringField("Arguments", "Arguments", s => s.Arguments, (s, v) => s with { Arguments = v }, multiline: true));
        ComboBox events = new() { Name = "ScriptOnEvent", ItemsSource = Enum.GetValues<ScriptEvent>(), HorizontalAlignment = HorizontalAlignment.Stretch };
        _editors.Add("OnEvent", events);
        events.SelectionChanged += (_, _) => { if (!_loading && events.SelectedItem is ScriptEvent value) { Update(s => s with { OnEvent = value }); } };
        scriptFields.Children.Add(Field("Execute on event", events));
        ComboBox icons = new() { Name = "ScriptIcon", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<IconChoice>((choice, _) => choice is null ? new TextBlock() : IconCaption(choice)) };
        _editors.Add("Icon", icons);
        icons.SelectionChanged += (_, _) => { if (!_loading && icons.SelectedItem is IconChoice value) { Update(s => s with { Icon = value.Value }); } };
        scriptFields.Children.Add(Field("Icon", icons));
        scriptFields.Children.Add(StringField("IconFilePath", "Icon or associated file path", s => s.IconFilePath,
            (s, v) => s with { IconFilePath = v }, filePicker: true));
        editor.Children.Add(ClassicChrome.Group("ScriptProperties", "Script", scriptFields));
        StackPanel behaviour = new() { Spacing = 3 };
        behaviour.Children.Add(BoolField("AskConfirmation", "Ask confirmation", s => s.AskConfirmation, (s, v) => s with { AskConfirmation = v }));
        behaviour.Children.Add(BoolField("RunInBackground", "Run in background", s => s.RunInBackground, (s, v) => s with { RunInBackground = v }));
        behaviour.Children.Add(BoolField("IsPowerShell", "Is PowerShell script", s => s.IsPowerShell, (s, v) => s with { IsPowerShell = v }));
        editor.Children.Add(ClassicChrome.Group("ScriptBehaviour", "Script behaviour", behaviour));
        editor.Children.Add(ClassicChrome.Group("ScriptContext", "Script context", BoolField("AddToRevisionGridContextMenu", "Show in RevisionGrid",
            s => s.AddToRevisionGridContextMenu, (s, v) => s with { AddToRevisionGridContextMenu = v })));
        Grid root = new() { RowDefinitions = new RowDefinitions("150,8,*,Auto") };
        root.Children.Add(upper);
        ScrollViewer properties = new() { Name = "ScriptsPropertiesScroll", Content = editor, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(properties, 2); root.Children.Add(properties);
        ScrollViewer notice = new() { Content = _notice, MaxHeight = 65 }; Grid.SetRow(notice, 3); root.Children.Add(notice);
        Content = root;
        AttachedToVisualTree += (_, _) => AttachOwner();
        DetachedFromVisualTree += (_, _) => _help?.Close();
        RefreshRows(); ShowSelected();
        if (baseline.Error is not null) { ShowSaveError(baseline.Error); }
    }

    internal bool Validate(out string? error)
    {
        try { _ = ScriptConfiguration.WriteXml(Draft); error = null; return true; }
        catch (Exception invalid) when (invalid is ArgumentException or InvalidOperationException)
        { error = invalid.Message; return false; }
    }

    internal void AcceptSaved(ScriptsPreferencesLoadResult saved) => _baseline = saved;
    internal void ShowSaveError(Exception error) => _notice.Text = _translations.Translate("Unable to save scripts.") + " " + error.Message;

    private int SelectedIndex() => _selectedId is int id ? _scripts.FindIndex(s => s.HotkeyCommandIdentifier == id) : -1;
    private void Changed() => DraftChanged?.Invoke(Draft);
    private void Move(int direction)
    {
        int index = SelectedIndex(), next = index + direction;
        if (index < 0 || next < 0 || next >= _scripts.Count) { return; }
        (_scripts[index], _scripts[next]) = (_scripts[next], _scripts[index]); RefreshRows(); Changed();
    }

    private void Update(Func<ScriptDefinition, ScriptDefinition> update)
    {
        if (_closed || _loading) { return; }
        int index = SelectedIndex(); if (index < 0) { return; }
        _scripts[index] = update(_scripts[index]); RefreshRows(); Changed();
    }

    private void RefreshRows()
    {
        _loading = true;
        try
        {
            _selectedId ??= _scripts.FirstOrDefault()?.HotkeyCommandIdentifier;
            _list.ItemsSource = _scripts.ToArray();
            _list.SelectedItem = _scripts.FirstOrDefault(s => s.HotkeyCommandIdentifier == _selectedId);
        }
        finally { _loading = false; }
        int index = SelectedIndex(); _delete.IsEnabled = index >= 0; _up.IsEnabled = index > 0; _down.IsEnabled = index >= 0 && index + 1 < _scripts.Count;
    }

    private void ShowSelected()
    {
        int index = SelectedIndex(); ScriptDefinition? selected = index < 0 ? null : _scripts[index];
        _loading = true;
        try
        {
            foreach (Control control in _editors.Values) { control.IsEnabled = selected is not null; }
            foreach ((string key, Control control) in _editors)
            {
                if (control is TextBox input)
                {
                    input.Text = selected is null ? null : key switch
                    { "Name" => selected.Name, "Command" => selected.Command, "Arguments" => selected.Arguments, "IconFilePath" => selected.IconFilePath, _ => null };
                }
                else if (control is CheckBox check)
                {
                    check.IsChecked = selected is not null && (key switch
                    { "Enabled" => selected.Enabled, "AskConfirmation" => selected.AskConfirmation, "RunInBackground" => selected.RunInBackground,
                        "IsPowerShell" => selected.IsPowerShell, "AddToRevisionGridContextMenu" => selected.AddToRevisionGridContextMenu, _ => false });
                }
            }
            ((ComboBox)_editors["OnEvent"]).SelectedItem = selected?.OnEvent;
            ComboBox icons = (ComboBox)_editors["Icon"];
            List<IconChoice> choices = [new(null, _translations.Translate("None"))];
            choices.AddRange(_iconKeys.Select(key => new IconChoice(key, key)));
            if (selected?.Icon is string icon && !choices.Any(choice => choice.Value == icon)) { choices.Add(new(icon, icon)); }
            icons.ItemsSource = choices; icons.SelectedItem = choices.FirstOrDefault(choice => choice.Value == selected?.Icon);
        }
        finally { _loading = false; }
    }

    private Control Row(ScriptDefinition script)
    {
        Grid row = Columns(); row.Name = "ScriptRow" + script.HotkeyCommandIdentifier;
        StackPanel name = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
        CheckBox enabled = new() { Name = "ScriptRowEnabled" + script.HotkeyCommandIdentifier, IsChecked = script.Enabled, MinWidth = 20 };
        enabled.Click += (_, e) =>
        {
            _list.SelectedItem = _scripts.FirstOrDefault(s => s.HotkeyCommandIdentifier == script.HotkeyCommandIdentifier);
            enabled.IsChecked = script.Enabled; e.Handled = true;
        };
        name.Children.Add(enabled);
        if (ScriptPresentation.Icon(script) is Control icon) { name.Children.Add(icon); }
        name.Children.Add(Label(script.Name)); row.Children.Add(name);
        TextBlock onEvent = Label(script.OnEvent.ToString()); Grid.SetColumn(onEvent, 1); row.Children.Add(onEvent);
        TextBlock command = Label(string.Concat(script.Command, " ", script.Arguments)); Grid.SetColumn(command, 2); row.Children.Add(command);
        ToolTip.SetTip(row, string.Concat(script.Command, " ", script.Arguments));
        return row;
        TextBlock Label(string? text)
        {
            TextBlock result = new() { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (!script.Enabled) { result.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicDisabledBrush")); }
            return result;
        }
    }

    private Control StringField(string key, string caption, Func<ScriptDefinition, string?> read, Func<ScriptDefinition, string?, ScriptDefinition> write,
        bool multiline = false, bool filePicker = false)
    {
        TextBox input = new() { Name = "Script" + key, AcceptsReturn = multiline, TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap };
        if (multiline) { input.MinHeight = 60; }
        input.PropertyChanged += (_, change) =>
        { if (change.Property == TextBox.TextProperty && !_loading) { Update(s => write(s, input.Text)); } };
        _editors.Add(key, input);
        if (!filePicker) { return Field(caption, input); }
        Grid choice = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 3 };
        choice.Children.Add(input);
        Button browse = new() { Name = "PickScript" + key, Content = "…", MinWidth = 26 };
        browse.Click += async (_, _) =>
        {
            Window? owner = TopLevel.GetTopLevel(this) as Window; int? id = _selectedId;
            if (_closed || owner is null || SelectedIndex() < 0) { return; }
            string? before = read(_scripts[SelectedIndex()]);
            try
            {
                IReadOnlyList<IStorageFile> selected = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = caption, AllowMultiple = false });
                string? path = selected.Count == 0 ? null : selected[0].TryGetLocalPath();
                if (!_closed && owner.IsVisible && _selectedId == id && SelectedIndex() >= 0 && read(_scripts[SelectedIndex()]) == before && path is not null)
                { input.Text = path; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            { if (!_closed) { ShowSaveError(error); } }
        };
        Grid.SetColumn(browse, 1); choice.Children.Add(browse); return Field(caption, choice);
    }

    private Control BoolField(string key, string caption, Func<ScriptDefinition, bool> read, Func<ScriptDefinition, bool, ScriptDefinition> write)
    {
        CheckBox input = new() { Name = "Script" + key, HorizontalAlignment = HorizontalAlignment.Left };
        input.IsCheckedChanged += (_, _) => { if (!_loading) { Update(s => write(s, input.IsChecked == true)); } };
        _editors.Add(key, input); return Field(caption, input);
    }

    private static Grid Field(string caption, Control editor)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 5 };
        row.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(editor, 1); row.Children.Add(editor); return row;
    }

    private Button ToolButton(string name, string icon, string tooltip, Action action)
    {
        Button button = new() { Name = name, Content = OriginalMenuItems.Icon(icon), MinWidth = 26, MinHeight = 26, Padding = new Thickness(3) };
        ToolTip.SetTip(button, _translations.Translate(tooltip)); button.Click += (_, _) => action(); return button;
    }

    private Control IconCaption(IconChoice choice)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (ScriptPresentation.Icon(new ScriptDefinition { Icon = choice.Value }) is Control icon) { row.Children.Add(icon); }
        row.Children.Add(new TextBlock { Text = choice.Caption }); return row;
    }

    private void AttachOwner()
    {
        Window? owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ReferenceEquals(owner, _owner)) { return; }
        if (_owner is not null) { _owner.Closing -= Closing; _owner.Closed -= Closed; }
        _help?.Close(); _owner = owner; _closed = false; owner.Closing += Closing; owner.Closed += Closed;
    }

    private void Closing(object? sender, WindowClosingEventArgs args) { _closed = true; _help?.Close(); }
    private void Closed(object? sender, EventArgs args)
    {
        _closed = true; _help?.Close();
        if (_owner is not null) { _owner.Closing -= Closing; _owner.Closed -= Closed; _owner = null; }
    }

    private void ShowHelp()
    {
        AttachOwner(); if (_closed || _owner is null) { return; }
        if (_help is { IsVisible: true }) { _help.Activate(); return; }
        SimpleHelpDisplayDialog help = new("ScriptArgumentsHelpText")
        {
            Name = "ScriptArgumentsHelp", RequestedThemeVariant = _owner.ActualThemeVariant,
            DialogTitle = Text("_scriptSettingsPageHelpDisplayArgumentsHelp", "Arguments help"),
            ContentText = Text("_scriptSettingsPageHelpDisplayContent", ArgumentsHelp)
        };
        help.Closed += (_, _) => { if (ReferenceEquals(_help, help)) { _help = null; } };
        _help = help; help.Show(_owner);
    }

    private static Grid Columns() => new() { ColumnDefinitions = new ColumnDefinitions("2*,145,3*"), ColumnSpacing = 5 };
    private string Text(string name, string fallback) => _translations.Translate("ScriptsSettingsPage", name, "Text", fallback);
    private sealed record IconChoice(string? Value, string Caption);

    private const string ArgumentsHelp = """
Use {option} for normal replacement.
Use {{option}} for quoted replacement.

User inputs:
{UserInput}
{UserInput:a popup label}
{UserInput:a popup label=a default value}
{UserInput:a popup label=a default value using {sLocalBranch}}
{UserFiles}

Working directory:
{WorkingDir}

Repository:
{RepoName}

Selected commits:
{sHashes}

Selected revision:
{sTag}
{sBranch}
{sLocalBranch}
{sRemoteBranch}
{sRemoteBranchName}   (without the remote's name)
{sRemote}
{sRemoteUrl}
{sRemotePathFromUrl}
{sHash}
{sMessage}
{sSubject}
{sAuthor}
{sCommitter}
{sAuthorDate}
{sCommitDate}

Currently checked out revision:
{HEAD}   (checked out branch name or checked out commit hash)
{cTag}
{cBranch}
{cLocalBranch}
{cRemoteBranch}
{cRemoteBranchName}   (without the remote's name)
{cHash}
{cMessage}
{cSubject}
{cAuthor}
{cCommitter}
{cAuthorDate}
{cCommitDate}
{cDefaultRemote}
{cDefaultRemoteUrl}
{cDefaultRemotePathFromUrl}

Diff selection:
{SelectedRelativePaths}   (relative paths as they were in the selected commit)
{LineNumber}
{ColumnNumber}
""";
}
