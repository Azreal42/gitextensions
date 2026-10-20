using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class BlameViewerSettingsPage : UserControl
{
    private readonly BlameViewerSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly TextBlock _notice = new() { Name = "BlameViewerSettingsNotice", TextWrapping = TextWrapping.Wrap };
    private BlamePreferences _baseline;

    internal BlameViewerSettingsPage(BlameViewerSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; _baseline = session.Initial; Name = "BlameViewerSettingsPage";
        string Text(string name, string fallback) => translations.Translate("BlameViewerSettingsPage", name, "Text", fallback);
        StackPanel query = new() { Name = "tableLayoutPanelBlameSettings", Spacing = 1 };
        StackPanel display = new() { Name = "tableLayoutPanelDisplayResult", Spacing = 1 };
        foreach (BlameViewerSetting setting in BlameViewerSettingsCatalog.Fields)
        {
            CheckBox choice = new()
            {
                Name = setting.Control, IsChecked = BlameViewerSettingsCatalog.Get(_baseline, setting.Name), MinHeight = 24,
                Padding = default, HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = Text(setting.Control, setting.Caption), TextWrapping = TextWrapping.Wrap }
            };
            if (setting.ManualAnchor is not null)
            {
                choice.Tag = setting.ManualAnchor;
                ToolTip.SetTip(choice, Text("_blameWarningTooltip", BlameViewerSettingsCatalog.Warning));
            }
            _choices.Add(setting.Name, choice); (setting.Group == "Query" ? query : display).Children.Add(choice);
        }
        // These global AppSettings fields have no invented Local/Distributed source selector.
        RadioButton global = new() { Name = "BlameViewerGlobalRB", Content = translations.Translate("Global"), IsChecked = true, MinHeight = 24 };
        Content = new StackPanel { Margin = new Thickness(8), Spacing = 6, Children =
        {
            global,
            ClassicChrome.Group("groupBoxBlameSettings", Text("groupBoxBlameSettings", "Blame settings"), query),
            ClassicChrome.Group("groupBoxDisplayResult", Text("groupBoxDisplayResult", "Display result settings"), display),
            _notice
        } };
        ShowError();
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && (args.Source is not Button || args.Source is CheckBox or RadioButton)) { args.Handled = true; } }, RoutingStrategies.Bubble);
    }

    internal BlamePreferences Draft
    {
        get
        {
            BlamePreferences value = _baseline;
            foreach (BlameViewerSetting setting in BlameViewerSettingsCatalog.Fields)
            { value = BlameViewerSettingsCatalog.With(value, setting.Name, _choices[setting.Name].IsChecked == true); }
            // Windows PageToSettings saves author/date/time independently. Menu invariants do not belong here.
            return value;
        }
    }
    internal void AcceptSaved(BlameSettingsSaveReceipt receipt)
    {
        _baseline = receipt.Values;
        foreach (BlameViewerSetting setting in BlameViewerSettingsCatalog.Fields)
        { _choices[setting.Name].IsChecked = BlameViewerSettingsCatalog.Get(receipt.Values, setting.Name); }
        ShowError();
    }
    private void ShowError() => _notice.Text = _session.LoadError is null ? null
        : _translations.Translate("Unable to read blame preferences.") + " " + _session.LoadError.Message;
}
