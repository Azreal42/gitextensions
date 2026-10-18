using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original two-tab FormCommitTemplateSettings. Every edit belongs to a private draft until OK.</summary>
public sealed class CommitMessageSettingsDialog : Window
{
    private const string Category = "FormCommitTemplateSettings";
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CommitMessagePreferencesStore _store;
    private readonly CommitMessagePreferences _baseline;
    private readonly Exception? _loadError;
    private CommitMessagePreferences _draft;
    private readonly ComboBox _slots = new() { Name = "_NO_TRANSLATE_comboBoxCommitTemplates", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Name = "_NO_TRANSLATE_textBoxCommitTemplateName", MaxLength = 80 };
    private readonly TextBox _text = new() { Name = "_NO_TRANSLATE_textCommitTemplateText", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 150 };
    private readonly CheckBox _regex;
    private readonly NumericUpDown _first, _line;
    private readonly CheckBox _wrap, _indent, _second;
    private readonly TextBox _validation = new() { Name = "_NO_TRANSLATE_textBoxCommitValidationRegex" };
    private readonly TextBlock _notice = new() { Name = "CommitMessageSettingsNotice", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool _updating;
    public CommitMessagePreferences Result { get; private set; }
    internal CommitMessagePreferences Draft => _draft;

    public CommitMessageSettingsDialog(CommitMessagePreferencesStore? store = null)
    {
        _store = store ?? new();
        try { _baseline = _store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _baseline = new(); _loadError = error; Notice(error.Message); }
        _draft = _baseline.Snapshot(); Result = _baseline;
        if (_loadError is null && (_baseline.MaxFirstLineLength > 100 || _baseline.MaxLineLength > 100))
        { Notice("A stored limit exceeds the original 0–100 range. It is preserved; a changed limit must be between 0 and 100."); }
        ClassicChoiceAppearance.Apply(this, "CommitMessageSettingsCompactChoices");
        Name = "FormCommitTemplateSettings"; Title = T("$this", "Commit message settings");
        Width = 698; Height = 395; MinWidth = 550; MinHeight = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DesktopFonts.Apply(_text, DesktopFontRole.Commit);
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        _regex = Choice("checkBoxRegexEnabled", "Enable regex");
        ToolTip.SetTip(_regex, _translations.Translate(Category, "checkBoxRegexEnabled", "toolTipRegex",
            "Use {{regex}}[regex group number] to extract branch name part\nGroup number is optional, default is 1\n\nExamples on branch name: \"feature/ABC-4587-commitMessageRegex\"\n\"Commit from: {{^feature/(.*)$}} branch\" -> \"Commit from: ABC-4587-commitMessageRegex branch\"\n\"{{([A-Z]+-\\d+)}}: My message is\" -> \"ABC-4587: My message is \"\n\"Name: {{([A-Z]+-\\d+)-(.*)}}[2], issue: {{([A-Z]+-\\d+)-(.*)}}[1]\" -> \"Name: commitMessageRegex, issue: ABC-4587\""));
        Grid templates = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 6, ColumnSpacing = 8, Margin = new Thickness(6) };
        Grid.SetColumnSpan(_slots, 2); templates.Children.Add(_slots);
        Label(templates, 1, "labelCommitTemplateName", "Name:"); Place(templates, _name, 1, 1);
        Label(templates, 2, "labelCommitTemplate", "Commit template:"); Place(templates, _text, 2, 1); Grid.SetRowSpan(_text, 2);
        Place(templates, _regex, 3, 0);
        _first = Number("_NO_TRANSLATE_numericMaxFirstLineLength", _draft.MaxFirstLineLength);
        _line = Number("_NO_TRANSLATE_numericMaxLineLength", _draft.MaxLineLength);
        _wrap = Choice("checkBoxAutoWrap", "", _draft.AutoWrap); _indent = Choice("checkBoxUseIndent", "", _draft.IndentAfterFirstLine);
        _second = Choice("checkBoxSecondLineEmpty", "", _draft.SecondLineMustBeEmpty); _validation.Text = _draft.ValidationRegex;
        Grid validation = new() { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8, Margin = new Thickness(6) };
        Row(0, "labelMaxFirstLineLength", "Maximum number of characters in the first line (0 = check disabled):", _first);
        Row(1, "labelMaxLineLength", "Maximum number of characters per line (0 = check disabled):", _line);
        Row(2, "labelAutoWrap", "Auto-wrap commit message (except subject line)", _wrap);
        Row(3, "labelRegExCheck", "Commit must match following RegEx (Empty = check disabled):", _validation); _validation.MinWidth = 180;
        Row(4, "labelUseIndent", "Indent lines after the first line:", _indent);
        Row(5, "labelSecondLineEmpty", "Second line must be empty:", _second);
        TabControl tabs = new() { Name = "tabControl1", ItemsSource = new[]
        {
            new TabItem { Name = "tabPage1", Header = T("tabPage1", "Commit templates"), Content = templates },
            new TabItem { Name = "tabPage2", Header = T("tabPage2", "Commit validation"), Content = new ScrollViewer { Content = validation } }
        }, SelectedIndex = 0 };
        Button ok = new() { Name = "buttonOk", Content = T("buttonOk", "OK"), IsDefault = true, MinWidth = 75, MinHeight = 25 };
        Button cancel = new() { Name = "buttonCancel", Content = T("buttonCancel", "Cancel"), IsCancel = true, MinWidth = 75, MinHeight = 25 };
        ok.Click += (_, _) => Save(); cancel.Click += (_, _) => Close(false);
        Grid body = new() { RowDefinitions = new("*,Auto,Auto"), Margin = new Thickness(9), RowSpacing = 8 };
        body.Children.Add(tabs); ScrollViewer notice = new() { Content = _notice, MaxHeight = 80 }; Place(body, notice, 1, 0);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { ok, cancel } };
        Place(body, buttons, 2, 0); Content = body;
        _slots.SelectionChanged += (_, _) => SelectSlot();
        _name.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_updating) { UpdateSlot(); RefreshSlots(); } };
        _text.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_updating) { UpdateSlot(); } };
        _regex.IsCheckedChanged += (_, _) => { if (!_updating) { UpdateSlot(); } };
        _first.ValueChanged += (_, _) => UpdateValidation(); _line.ValueChanged += (_, _) => UpdateValidation();
        _wrap.IsCheckedChanged += (_, _) => UpdateValidation(); _indent.IsCheckedChanged += (_, _) => UpdateValidation(); _second.IsCheckedChanged += (_, _) => UpdateValidation();
        _validation.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { UpdateValidation(); } };
        RefreshSlots(); _slots.SelectedIndex = 0; SelectSlot();
        Opened += (_, _) => { ModalWindowActivation.AfterMapping(this, () => IsVisible); _slots.Focus(); };
        AddHandler(KeyDownEvent, (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(false); } }, RoutingStrategies.Tunnel);

        void Row(int row, string name, string caption, Control control) { Label(validation, row, name, caption); Place(validation, control, row, 1); }
    }
    private string T(string name, string source) => _translations.Translate(Category, name, "Text", source);
    private void Notice(string text) { _notice.Text = text; _notice.IsVisible = true; }
    private CheckBox Choice(string name, string source, bool value = false) => new() { Name = name, Content = T(name, source), IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
    private static NumericUpDown Number(string name, int value)
    {
        NumericUpDown input = ClassicNumericInput.Create(name, 0, Math.Max(100, value), width: value > 100 ? 90 : 60);
        input.Value = value; return input;
    }
    private void Label(Grid grid, int row, string name, string source) => Place(grid, new TextBlock { Name = name, Text = T(name, source), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, row, 0);
    private static void Place(Grid grid, Control control, int row, int column) { Grid.SetRow(control, row); Grid.SetColumn(control, column); grid.Children.Add(control); }
    private void RefreshSlots()
    {
        int selected = Math.Max(0, _slots.SelectedIndex); _updating = true;
        try
        {
            _slots.ItemsSource = _draft.Templates.Select((template, index) => $"{index + 1} : {(template.Name.Length == 0 ? "<" + T("_emptyTemplate", "empty") + ">" : template.Name.Length > 50 ? template.Name[..47] + "..." : template.Name)}").ToArray();
            _slots.SelectedIndex = selected;
        }
        finally { _updating = false; }
    }
    private void SelectSlot()
    {
        if (_updating || _slots.SelectedIndex < 0) { return; }
        _updating = true;
        try { var item = _draft.Templates[_slots.SelectedIndex]; _text.Text = item.Text; _name.Text = item.Name; _regex.IsChecked = item.IsRegex; }
        finally { _updating = false; }
    }
    private void UpdateSlot()
    {
        if (_slots.SelectedIndex >= 0) { _draft = _draft.WithTemplate(_slots.SelectedIndex, new(_name.Text ?? "", _text.Text ?? "", _regex.IsChecked == true)); }
    }
    private void UpdateValidation() => _draft = _draft with
    {
        MaxFirstLineLength = Convert.ToInt32(_first.Value ?? 0), MaxLineLength = Convert.ToInt32(_line.Value ?? 0), AutoWrap = _wrap.IsChecked == true,
        ValidationRegex = _validation.Text ?? "", IndentAfterFirstLine = _indent.IsChecked == true, SecondLineMustBeEmpty = _second.IsChecked == true
    };
    private void Save()
    {
        try
        {
            UpdateValidation(); _draft.Validate();
            if (_draft.MaxFirstLineLength > 100 && _draft.MaxFirstLineLength != _baseline.MaxFirstLineLength
                || _draft.MaxLineLength > 100 && _draft.MaxLineLength != _baseline.MaxLineLength)
            { throw new ArgumentOutOfRangeException(nameof(_draft), "Changed limits must be between 0 and 100. The original stored value can remain unchanged."); }
            if (_loadError is not null) { if (!_draft.SameAs(_baseline)) { throw new InvalidOperationException(_loadError.Message, _loadError); } Result = _baseline; }
            else { Result = _store.SaveChanges(_baseline, _draft); }
            Close(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { Notice(error.Message); }
    }
}
