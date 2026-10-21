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

/// <summary>The nine generated PluginSettingsPage controls, not another branch-preview form.</summary>
internal sealed class DeleteUnusedBranchesSettingsPage : UserControl
{
    private const string EmptyStringValue = "<empty string>";
    private readonly DeleteUnusedBranchesSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly Dictionary<GitDistributedSettingsScope, RadioButton> _scopes = [];
    private readonly Dictionary<string, Control> _editors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly TextBlock _notice = new() { Name = "DeleteUnusedBranchesSettingsNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Grid _table;
    private GitDistributedSettingsScope _scope;
    private bool _loading;

    internal DeleteUnusedBranchesSettingsPage(DeleteUnusedBranchesSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; _scope = session.InitialScope;
        Name = "DeleteUnusedBranchesSettingsPage";
        WrapPanel scopes = new() { Name = "DeleteUnusedBranchesSettingsHeader", Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        foreach ((GitDistributedSettingsScope scope, string item, string caption) in new[]
        {
            (GitDistributedSettingsScope.Effective, "EffectiveRB", "Effective"),
            (GitDistributedSettingsScope.Local, "LocalRB", "Local for current repository"),
            (GitDistributedSettingsScope.Distributed, "DistributedRB", "Distributed with current repository"),
            (GitDistributedSettingsScope.Global, "GlobalRB", "Global for all repositories")
        })
        {
            RadioButton choice = new() { Name = "DeleteUnusedBranches" + item, GroupName = "DeleteUnusedBranchesSettingsScope",
                Content = translations.Translate("SettingsPageHeader", item, "Text", caption), Margin = new(0, 0, 12, 0) };
            ReferenceDialogControls.Compact(choice); _scopes.Add(scope, choice); scopes.Children.Add(choice);
            choice.IsCheckedChanged += (_, _) =>
            { if (!_loading && choice.IsChecked == true && _scope != scope) { Flush(); _scope = scope; LoadView(); } };
        }
        // AutoLayoutSettingsPage/TableSettingsLayout uses label, fill editor, spare
        // auto column, in GetSettings order. Long translated labels may wrap.
        _table = new() { Name = "DeleteUnusedBranchesSettingsTable", ColumnDefinitions = new("Auto,*,Auto"),
            ColumnSpacing = 8, RowSpacing = 4, RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", 9))) };
        int row = 0;
        foreach (DeleteUnusedBranchesSetting setting in DeleteUnusedBranchesSettingsCatalog.Fields)
        {
            TextBlock label = new() { Name = setting.Control + "Caption", Text = translations.Translate("DeleteUnusedBranchesPlugin", setting.Control, "Caption", setting.Caption),
                MaxWidth = 285, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 0, 0) };
            Control editor;
            if (setting.Kind == DeleteUnusedBranchesSettingKind.Boolean)
            {
                CheckBox choice = new() { Name = setting.Control, IsThreeState = true, MinHeight = 24,
                    Padding = default, HorizontalAlignment = HorizontalAlignment.Left };
                ReferenceDialogControls.Compact(choice);
                choice.IsCheckedChanged += (_, _) => Changed(setting.Control); editor = choice;
            }
            else if (setting.Kind == DeleteUnusedBranchesSettingKind.Number)
            {
                // Same generated Int32 spinner as Windows. Apply additionally checks
                // explicit edits against the existing preview/engine's 100000-day cap.
                NumericUpDown number = ClassicNumericInput.Create(setting.Control, 0, int.MaxValue, width: 120, clipValueToMinMax: false);
                number.HorizontalAlignment = HorizontalAlignment.Left;
                number.PropertyChanged += (_, change) =>
                { if (change.Property == NumericUpDown.TextProperty || change.Property == NumericUpDown.ValueProperty) { Changed(setting.Control); } };
                editor = number;
            }
            else
            {
                TextBox text = new() { Name = setting.Control, Height = 24, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Watermark = string.Format(translations.Translate("DistributedSettingsPage", "_stringSettingPlaceholder", "Text",
                        "no value set; for empty string, enter \"{0}\" without the double quotes"), EmptyStringValue) };
                text.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { Changed(setting.Control); } };
                editor = text;
            }
            _editors.Add(setting.Control, editor); Grid.SetRow(label, row); Grid.SetRow(editor, row++); Grid.SetColumn(editor, 1);
            _table.Children.Add(label); _table.Children.Add(editor);
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
            DeleteUnusedBranchesSetting setting = DeleteUnusedBranchesSettingsCatalog.Find(control);
            string? value = _editors[control] switch
            {
                CheckBox choice => choice.IsChecked is { } flag ? flag ? "true" : "false" : null,
                NumericUpDown number => string.IsNullOrWhiteSpace(number.Text) ? null
                    : DeleteUnusedBranchesSettingsCatalog.Number(number.Text) is { } count ? count.ToString(System.Globalization.CultureInfo.CurrentCulture) : number.Text?.Trim(),
                TextBox text => string.IsNullOrWhiteSpace(text.Text) ? null
                    : text.Text?.Trim() == EmptyStringValue ? "" : text.Text?.Trim(),
                _ => throw new InvalidOperationException("Unknown plugin setting control.")
            };
            _session.Set(_scope, setting, value);
        }
        _dirty.Clear();
    }
    internal void PrepareSave() => Flush();
    internal bool Validate(out string? error)
    {
        Flush(); error = null;
        if (_session.ValidateEdits(out GitDistributedSettingsScope scope)) { return true; }
        _scope = scope; LoadView();
        error = _translations.Translate("Choose an age between 0 and 100000 days.");
        _notice.Text = error; _notice.IsVisible = true;
        return false;
    }
    // The host selects this page before focusing the real editor.
    internal void FocusInvalidControl()
    {
        Control number = _editors["_daysOlderThan"];
        (number.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() as Control ?? number).Focus();
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
            foreach (DeleteUnusedBranchesSetting setting in DeleteUnusedBranchesSettingsCatalog.Fields)
            {
                string? value = _session.Read(_scope, setting);
                switch (_editors[setting.Control])
                {
                    case CheckBox choice:
                        choice.IsThreeState = _scope != GitDistributedSettingsScope.Effective;
                        choice.IsChecked = DeleteUnusedBranchesSettingsCatalog.Boolean(value); break;
                    case NumericUpDown number:
                        number.Value = DeleteUnusedBranchesSettingsCatalog.Number(value);
                        number.Text = value ?? ""; break;
                    case TextBox text: text.Text = value is { Length: 0 } ? EmptyStringValue : value ?? ""; break;
                }
            }
            _table.IsEnabled = _session.Loaded && _scope != GitDistributedSettingsScope.Effective;
            _notice.Text = _session.LoadError is not null ? _translations.Translate("Unable to read plugin settings.")
                : _scope == GitDistributedSettingsScope.Effective
                    ? _translations.Translate("Effective settings are read-only. Select Local, Distributed or Global to edit.") : null;
            _notice.IsVisible = !string.IsNullOrEmpty(_notice.Text); _dirty.Clear();
        }
        finally { _loading = false; }
    }
}
