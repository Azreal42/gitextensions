using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The Windows TableSettingsLayout bindings over a shared scoped plugin session.</summary>
internal class PluginScopedSettingsPage : UserControl
{
    private const string EmptyStringValue = "<empty string>";
    private readonly PluginScopedSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly Dictionary<GitDistributedSettingsScope, RadioButton> _scopes = [];
    private readonly Dictionary<string, Control> _editors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly PluginScopedSettingsDefinition _definition;
    private string? _invalidControl;
    private readonly Grid _table;
    private GitDistributedSettingsScope _scope;
    private bool _loading;

    internal PluginScopedSettingsPage(PluginScopedSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; _scope = session.InitialScope; _definition = session.Definition;
        _notice.Name = _definition.ControlPrefix + "SettingsNotice";
        Name = _definition.ControlPrefix + "SettingsPage";
        WrapPanel scopes = new() { Name = _definition.ControlPrefix + "SettingsHeader", Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        foreach ((GitDistributedSettingsScope scope, string item, string caption) in new[]
        {
            (GitDistributedSettingsScope.Effective, "EffectiveRB", "Effective"),
            (GitDistributedSettingsScope.Local, "LocalRB", "Local for current repository"),
            (GitDistributedSettingsScope.Distributed, "DistributedRB", "Distributed with current repository"),
            (GitDistributedSettingsScope.Global, "GlobalRB", "Global for all repositories")
        })
        {
            RadioButton choice = new() { Name = _definition.ControlPrefix + item, GroupName = _definition.ControlPrefix + "SettingsScope",
                Content = translations.Translate("SettingsPageHeader", item, "Text", caption), Margin = new(0, 0, 12, 0) };
            ReferenceDialogControls.Compact(choice); _scopes.Add(scope, choice); scopes.Children.Add(choice);
            choice.IsCheckedChanged += (_, _) =>
            { if (!_loading && choice.IsChecked == true && _scope != scope) { Flush(); _scope = scope; LoadView(); } };
        }
        // AutoLayoutSettingsPage/TableSettingsLayout uses label, fill editor, spare
        // auto column, in GetSettings order. Long translated labels may wrap.
        _table = new() { Name = _definition.ControlPrefix + "SettingsTable", ColumnDefinitions = new("Auto,*,Auto"),
            ColumnSpacing = 8, RowSpacing = 4, RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", _definition.Fields.Count + (_definition.Warning is null ? 0 : 1)))) };
        int row = 0;
        foreach (PluginScopedSetting setting in _definition.Fields)
        {
            TextBlock label = new() { Name = setting.Control + "Caption", Text = translations.Translate(_definition.Category, setting.Control, "Caption", setting.Caption),
                MaxWidth = 285, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 0, 0) };
            Control editor;
            if (setting.Kind == PluginScopedSettingKind.Boolean)
            {
                CheckBox choice = new() { Name = setting.Control, IsThreeState = true, MinHeight = 24,
                    Padding = default, HorizontalAlignment = HorizontalAlignment.Left };
                ReferenceDialogControls.Compact(choice);
                choice.IsCheckedChanged += (_, _) => Changed(setting.Control); editor = choice;
            }
            else if (setting.Kind == PluginScopedSettingKind.Integer)
            {
                // Original generated Int32 spinner; no runtime interval cap is introduced.
                NumericUpDown number = ClassicNumericInput.Create(setting.Control, 0, int.MaxValue, width: 120, clipValueToMinMax: false);
                number.HorizontalAlignment = HorizontalAlignment.Left;
                number.PropertyChanged += (_, change) =>
                { if (change.Property == NumericUpDown.TextProperty || change.Property == NumericUpDown.ValueProperty) { Changed(setting.Control); } };
                editor = number;
            }
            else
            {
                TextBox text = new() { Name = setting.Control, Height = 24, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Watermark = setting.Kind == PluginScopedSettingKind.Single
                        ? translations.Translate("SettingsPageBase", "_numberSettingPlaceholder", "Text", "no value set")
                        : string.Format(translations.Translate("SettingsPageBase", "_stringSettingPlaceholder", "Text",
                            "no value set; for empty string, enter \"{0}\" without the double quotes"), EmptyStringValue) };
                if (setting.Kind == PluginScopedSettingKind.Password) { text.PasswordChar = '●'; }
                text.PropertyChanged += (_, change) =>
                {
                    if (change.Property != TextBox.TextProperty) { return; }
                    Changed(setting.Control);
                    if (setting.Kind == PluginScopedSettingKind.Single) { ShowSingleState(text); }
                };
                editor = text;
            }
            _editors.Add(setting.Control, editor); Grid.SetRow(label, row); Grid.SetRow(editor, row++); Grid.SetColumn(editor, 1);
            _table.Children.Add(label); _table.Children.Add(editor);
        }
        if (_definition.Warning is { } warning)
        {
            TextBlock caption = new() { Text = "    " };
            TextBox text = new() { Name = warning.Control,
                Text = translations.Translate(_definition.Category, warning.Control, "Text", warning.Text),
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Height = warning.Height, BorderThickness = default, Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
            Grid.SetRow(caption, row); Grid.SetRow(text, row); Grid.SetColumn(text, 1);
            _table.Children.Add(caption); _table.Children.Add(text);
        }
        Content = new StackPanel { Spacing = 6, Margin = new Thickness(8), Children = { scopes, _table, _notice } };
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None
            && (args.Source is not Button || args.Source is CheckBox || args.Source is RadioButton)) { args.Handled = true; } }, RoutingStrategies.Bubble);
        LoadView();
    }
    private void Changed(string control)
    { if (!_loading && _session.Loaded && _scope != GitDistributedSettingsScope.Effective) { _dirty.Add(control); } }
    private void Flush()
    {
        if (_loading || !_session.Loaded || _scope == GitDistributedSettingsScope.Effective) { return; }
        foreach (string control in _dirty)
        {
            PluginScopedSetting setting = _definition.Find(control);
            string? value = _editors[control] switch
            {
                CheckBox choice => choice.IsChecked is { } flag ? flag ? "true" : "false" : null,
                NumericUpDown number => string.IsNullOrWhiteSpace(number.Text) ? null
                    : PluginScopedSettingsDefinition.Integer(number.Text) is { } count ? count.ToString(System.Globalization.CultureInfo.CurrentCulture) : number.Text?.Trim(),
                TextBox text when setting.Kind == PluginScopedSettingKind.Single =>
                    PluginScopedSettingsDefinition.Single(text.Text) is { } single ? single.ToString(System.Globalization.CultureInfo.CurrentCulture) : null,
                TextBox text => string.IsNullOrWhiteSpace(text.Text) ? null
                    : text.Text?.Trim() == EmptyStringValue ? "" : text.Text?.Trim(),
                _ => throw new InvalidOperationException("Unknown plugin setting control.")
            };
            _session.Set(_scope, setting, value);
        }
        _dirty.Clear();
    }
    private static void ShowSingleState(TextBox text)
    {
        if (!string.IsNullOrEmpty(text.Text) && PluginScopedSettingsDefinition.Single(text.Text) is null)
        {
            // Same invalid-number indication as NumberSettingTextBoxBinding<T>.
            text.Background = Brushes.Red;
            text.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(Colors.Red, Colors.Black));
        }
        else { text.ClearValue(TextBox.BackgroundProperty); text.ClearValue(TextBox.ForegroundProperty); }
    }
    internal void PrepareSave() => Flush();
    internal bool Validate(out string? error)
    {
        Flush(); error = null;
        if (_session.ValidateEdits(out GitDistributedSettingsScope scope, out _invalidControl, out string? message)) { return true; }
        _scope = scope; LoadView();
        error = _translations.Translate(message!);
        _notice.Text = error; _notice.IsVisible = true;
        return false;
    }
    // The host selects this page before focusing the real invalid editor.
    internal void FocusInvalidControl()
    {
        if (_invalidControl is null || !_editors.TryGetValue(_invalidControl, out Control? editor)) { return; }
        (editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() as Control ?? editor).Focus();
    }
    internal void ClearSecrets()
    {
        bool wasLoading = _loading; _loading = true;
        try
        {
            foreach (PluginScopedSetting setting in _definition.Fields.Where(field => field.Kind == PluginScopedSettingKind.Password))
            { ((TextBox)_editors[setting.Control]).Text = null; _dirty.Remove(setting.Control); }
        }
        finally { _loading = wasLoading; }
    }
    internal void AcceptSaved() => LoadView();
    internal void ShowLoadState() => LoadView();
    private void LoadView()
    {
        _loading = true;
        try
        {
            foreach ((GitDistributedSettingsScope scope, RadioButton choice) in _scopes)
            {
                choice.IsVisible = scope == GitDistributedSettingsScope.Global || _session.HasRepository;
                choice.IsEnabled = scope != GitDistributedSettingsScope.Distributed || _session.HasDistributed;
                choice.IsChecked = scope == _scope; ToolTip.SetTip(choice, _session.ScopePath(scope));
            }
            foreach (PluginScopedSetting setting in _definition.Fields)
            {
                Control editor = _editors[setting.Control];
                editor.IsEnabled = _session.Loaded && _scope != GitDistributedSettingsScope.Effective;
                string? value = _session.Read(_scope, setting);
                switch (_editors[setting.Control])
                {
                    case CheckBox choice:
                        choice.IsThreeState = _scope != GitDistributedSettingsScope.Effective;
                        choice.IsChecked = PluginScopedSettingsDefinition.Boolean(value); break;
                    case NumericUpDown number:
                        number.Value = PluginScopedSettingsDefinition.Integer(value);
                        number.Text = value ?? ""; break;
                    case TextBox text:
                        text.Text = setting.Kind == PluginScopedSettingKind.Single ? value ?? "" : value is { Length: 0 } ? EmptyStringValue : value ?? "";
                        if (setting.Kind == PluginScopedSettingKind.Single) { ShowSingleState(text); }
                        break;
                }
            }
            _notice.Text = _session.LoadError is not null ? _translations.Translate("Unable to read plugin settings.")
                : _scope == GitDistributedSettingsScope.Effective
                    ? _translations.Translate("Effective settings are read-only. Select Local, Distributed or Global to edit.") : null;
            _notice.IsVisible = !string.IsNullOrEmpty(_notice.Text); _dirty.Clear();
        }
        finally { _loading = false; }
    }
}
