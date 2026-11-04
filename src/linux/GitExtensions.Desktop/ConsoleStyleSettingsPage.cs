using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class ConsoleStyleSettingsPage : UserControl
{
    private readonly ConsoleStyleSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly ComboBox _terminal = new() { Name = "cboConsoleEmulator", MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _style = new() { Name = "_NO_TRANSLATE_cboStyle", MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _font = new() { Name = "consoleFontChangeButton", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _reset = new() { Name = "consoleFontResetButton", MinWidth = 26 };
    private readonly TextBox _preview = new() { Name = "ConsoleStylePreview", Text = "$ git status\nOn branch main\nChanges to be committed:\n  modified: README.md", IsReadOnly = true, AcceptsReturn = true, Height = 110, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _notice = new() { Name = "ConsoleStyleNotice", TextWrapping = TextWrapping.Wrap };
    private bool _loading;
    internal ConsoleStylePreferences Draft { get; private set; }
    internal ConsoleStyleSettingsSession Session => _session;

    internal ConsoleStyleSettingsPage(ConsoleStyleSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; Draft = session.Initial; Name = "ConsoleStyleSettingsPage";
        Grid rows = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        Add(0, "lblConsoleEmulator", "Console emulator", _terminal);
        Add(1, "label1", "Console style", _style);
        Add(2, "lblFontName", "Font", _font);
        _reset.Content = translations.Translate("Reset"); ToolTip.SetTip(_reset, T("consoleFontToolTip", "Reset to default"));
        Grid.SetRow(_reset, 2); Grid.SetColumn(_reset, 2); rows.Children.Add(_reset);
        Content = new ScrollViewer { Content = new StackPanel { Spacing = 10, Children =
        {
            ClassicChrome.Group("groupBoxConsoleSettings", translations.Translate("Console settings"), rows), _preview,
            new TextBlock { Name = "ConsoleStyleScope", Text = translations.Translate("Style and font apply to built-in Git output immediately after Apply. Interactive terminals use their own font and colour profiles."), TextWrapping = TextWrapping.Wrap }, _notice
        } } };
        Refresh();
        _terminal.SelectionChanged += (_, _) =>
        { if (!_loading && _terminal.SelectedItem is ComboBoxItem { IsEnabled: true, Tag: string value }) { Draft = Draft with { Terminal = value }; RefreshPreview(); } };
        _style.SelectionChanged += (_, _) =>
        { if (!_loading && _style.SelectedItem is ComboBoxItem { IsEnabled: true, Tag: string value }) { Draft = Draft with { Style = value }; RefreshPreview(); } };
        _reset.Click += (_, _) => { Draft = Draft with { Font = null }; RefreshPreview(); };
        _font.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) { return; }
            FontPickerDialog picker = new(Draft.Font ?? DesktopFonts.For(DesktopFontRole.Monospace).Requested, DesktopFontRole.Monospace, translations)
                { RequestedThemeVariant = owner.ActualThemeVariant };
            if (await picker.ShowDialog<bool>(owner) && owner.IsVisible) { Draft = Draft with { Font = picker.Result }; RefreshPreview(); }
        };

        void Add(int row, string item, string source, Control editor)
        {
            TextBlock label = new() { Name = item, Text = T(item, source), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row); rows.Children.Add(label); Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); rows.Children.Add(editor);
        }
    }
    internal bool Validate(out string? error)
    {
        bool valid = _session.Validate(Draft, out error);
        if (!valid) { _notice.Text = error; }
        return valid;
    }
    internal void ShowSaveError(Exception error) => _notice.Text = error.Message;
    internal void AcceptSaved(ConsoleStylePreferences value) { Draft = value; Refresh(); }
    private string T(string item, string source) => _translations.Translate("ConsoleStyleSettingsPage", item, "Text", source);
    private void Refresh()
    {
        _loading = true;
        try
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            _terminal.Items.Clear();
            foreach (string name in ConsoleStylePreferences.Terminals)
            {
                if (name != "Automatic" && !ConsoleStyleTerminal.IsAvailable(name, path) && name != Draft.Terminal) { continue; }
                Add(_terminal, name, name == "Automatic" ? _translations.Translate("Automatic") : name,
                    ConsoleStyleTerminal.IsAvailable(name, path));
            }
            if (!ConsoleStylePreferences.Terminals.Contains(Draft.Terminal, StringComparer.Ordinal)) { Add(_terminal, Draft.Terminal, Draft.Terminal, false); }
            _style.Items.Clear();
            foreach (string style in ConsoleStylePreferences.Styles) { Add(_style, style, _translations.Translate(style), true); }
            if (!ConsoleStylePreferences.Styles.Contains(Draft.Style, StringComparer.Ordinal)) { Add(_style, Draft.Style, Draft.Style, false); }
            _terminal.SelectedItem = _terminal.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == Draft.Terminal);
            _style.SelectedItem = _style.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == Draft.Style);
            RefreshPreview();
        }
        finally { _loading = false; }
        void Add(ComboBox combo, string value, string caption, bool available)
            => combo.Items.Add(new ComboBoxItem { Tag = value, Content = caption + (available ? "" : " (" + _translations.Translate("Unavailable") + ")"), IsEnabled = available });
    }
    private void RefreshPreview()
    {
        _font.Content = Draft.Font is null ? T("_consoleDefaultFontText", "Console Default")
            : new TextBlock { Text = Draft.Font.Family + ", " + Draft.Font.SizeInPoints.ToString("G", CultureInfo.CurrentCulture), TextTrimming = TextTrimming.CharacterEllipsis };
        _reset.IsVisible = Draft.Font is not null;
        ConsoleStylePresentation.Apply(_preview, Draft);
        List<string> notices = [];
        if (_session.LoadError is not null) { notices.Add(_translations.Translate("Unable to read console preferences.") + " " + _session.LoadError.Message); }
        if (Draft.Terminal == "Automatic" && !ConsoleStylePreferences.Terminals.Skip(1).Any(name => ConsoleStyleTerminal.IsAvailable(name, Environment.GetEnvironmentVariable("PATH"))))
        { notices.Add(_translations.Translate(OperatingSystem.IsLinux()
            ? "No supported terminal is installed. Install GNOME Terminal, Konsole or XTerm to use interactive Git."
            : "No supported terminal is available on this platform.")); }
        if (Draft.Terminal != "Automatic" && !ConsoleStyleTerminal.IsAvailable(Draft.Terminal, Environment.GetEnvironmentVariable("PATH")))
        { notices.Add(_translations.Translate("The saved terminal is unavailable. Choose an installed terminal before opening an interactive console.")); }
        if (!ConsoleStylePreferences.Styles.Contains(Draft.Style, StringComparer.Ordinal))
        { notices.Add(_translations.Translate("The saved console style is unavailable. Default is displayed until an explicit choice.")); }
        if (Draft.Font is { } font && DesktopFontResolver.Resolve(font, DesktopFontRole.Monospace).Diagnostic is { } diagnostic) { notices.Add(diagnostic); }
        _notice.Text = string.Join("\n", notices);
    }
}
