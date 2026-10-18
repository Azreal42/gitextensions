using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class AppearanceFontsSettingsPage : UserControl
{
    private readonly TranslationCatalog _translations;
    private readonly Dictionary<DesktopFontRole, Button> _buttons = [];
    private readonly TextBlock _notice = new() { Name = "FontsNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Exception? _loadError;
    private FontPreferences _baseline;
    private readonly CheckBox _eol;
    internal FontPreferences Draft { get; private set; }

    internal AppearanceFontsSettingsPage(FontPreferences initial, TranslationCatalog translations, Exception? loadError = null)
    {
        _baseline = initial; Draft = initial; _translations = translations; _loadError = loadError;
        Name = "AppearanceFontsSettingsPage";
        Grid rows = new() { ColumnDefinitions = new ColumnDefinitions("Auto,245,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        Add(DesktopFontRole.Code, 0, "label56", "Code font", "diffFontChangeButton");
        Add(DesktopFontRole.Application, 1, "label26", "Application font", "applicationFontChangeButton");
        Add(DesktopFontRole.Commit, 2, "label34", "Commit font", "commitFontChangeButton");
        Add(DesktopFontRole.Monospace, 3, "label36", "Monospace font", "monospaceFontChangeButton");
        _eol = new() { Name = "ShowEolMarkerAsGlyph", IsChecked = initial.ShowEolMarkerAsGlyph,
            VerticalAlignment = VerticalAlignment.Center, Content = new TextBlock { Text = Text("ShowEolMarkerAsGlyph", "Show end-of-line markers as glyph instead of \"\\r\\n\" etc."), TextWrapping = TextWrapping.Wrap } };
        _eol.IsCheckedChanged += (_, _) => Draft = Draft with { ShowEolMarkerAsGlyph = _eol.IsChecked == true };
        Grid.SetColumn(_eol, 2); rows.Children.Add(_eol);
        StackPanel body = new() { Spacing = 10, Children = { ClassicChrome.Group("gbFonts", Text("gbFonts", "Fonts (restart required)"), rows),
            new ScrollViewer { MaxHeight = 110, Content = _notice } } };
        Content = new ScrollViewer { Content = body };
        if (loadError is not null) { _notice.Text = "Unable to read font preferences. " + loadError.Message; }
        else { RefreshNotice(); }

        void Add(DesktopFontRole role, int row, string labelName, string caption, string buttonName)
        {
            TextBlock label = new() { Name = labelName, Text = Text(labelName, caption), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row); rows.Children.Add(label);
            Button button = new() { Name = buttonName, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center };
            _buttons.Add(role, button); RefreshButton(role);
            button.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner) { return; }
                FontPickerDialog picker = new(Draft.For(role), role, _translations) { RequestedThemeVariant = owner.ActualThemeVariant };
                if (await picker.ShowDialog<bool>(owner) && owner.IsVisible)
                { Draft = Draft.With(role, picker.Result); RefreshButton(role); RefreshNotice(); }
            };
            Grid.SetRow(button, row); Grid.SetColumn(button, 1); rows.Children.Add(button);
        }
    }

    internal bool Validate(out string? error)
    {
        try { Draft.Validate(); }
        catch (ArgumentException exception) { error = exception.Message; _notice.Text = error; return false; }
        if (_loadError is not null && Draft != _baseline) { error = "Unable to read font preferences. " + _loadError.Message; _notice.Text = error; return false; }
        error = null; return true;
    }
    internal void ShowSaveError(Exception error) => _notice.Text = "Unable to save font preferences. " + error.Message;
    internal void AcceptSaved(FontPreferences published)
    {
        _baseline = Draft = published;
        _eol.IsChecked = published.ShowEolMarkerAsGlyph;
        foreach (DesktopFontRole role in Enum.GetValues<DesktopFontRole>()) { RefreshButton(role); }
        RefreshNotice();
    }
    private string Text(string name, string source) => _translations.Translate("AppearanceFontsSettingsPage", name, "Text", source);
    private void RefreshButton(DesktopFontRole role)
    {
        FontSpec spec = Draft.For(role); Button button = _buttons[role];
        button.Content = new TextBlock { Text = spec.Family + ", " + Math.Floor(spec.SizeInPoints + .5).ToString(CultureInfo.CurrentCulture), TextTrimming = TextTrimming.CharacterEllipsis };
        ResolvedDesktopFont resolved = DesktopFontResolver.Resolve(spec, role); DesktopFontResolver.Apply(button, resolved);
        double textHeight = new FormattedText("By", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(resolved.Family, resolved.Style, resolved.Weight), resolved.SizeInDips, Brushes.Black).Height;
        button.Height = Math.Max(32, Math.Ceiling(textHeight) + 8);
        ToolTip.SetTip(button, spec.Family + ", " + spec.SizeInPoints.ToString("G17", CultureInfo.CurrentCulture) + " pt" + (resolved.Diagnostic is null ? "" : "\n" + resolved.Diagnostic + " (" + resolved.EffectiveFamilyName + ")"));
    }
    private void RefreshNotice()
    {
        if (_loadError is not null) { return; }
        _notice.Text = string.Join("\n", Enum.GetValues<DesktopFontRole>().Select(role => (Role: role, Font: DesktopFontResolver.Resolve(Draft.For(role), role)))
            .Where(item => item.Font.Diagnostic is not null).Select(item => Text(item.Role switch { DesktopFontRole.Code => "label56", DesktopFontRole.Application => "label26", DesktopFontRole.Commit => "label34", _ => "label36" }, item.Role + " font") + ": " + item.Font.Diagnostic + " (" + item.Font.EffectiveFamilyName + ")"));
    }
}
