using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class ColorsSettingsPage : UserControl
{
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    internal ColorsSettingsPage(ColorsSettingsValues initial, TranslationCatalog translations)
    {
        Name = "ColorsSettingsPage";
        string Text(string name, string fallback) => translations.Translate("ColorsSettingsPage", name, "Text", fallback);
        StackPanel options = new() { Name = "tlpnlRevisionGraph", Spacing = 1 };
        foreach (ColorsSetting field in ColorsSettingsCatalog.Fields)
        {
            CheckBox choice = new()
            {
                Name = field.Control, IsChecked = initial.Get(field.Name), MinHeight = 24,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = Text(field.Control, field.Caption), TextWrapping = TextWrapping.Wrap }
            };
            _choices.Add(field.Name, choice); options.Children.Add(choice);
        }
        // Windows' compact first group. Theme remains on the existing Appearance page;
        // unavailable CSS/Multicolor controls are not presented as functional choices.
        Content = new StackPanel { Name = "tlpnlMain", Margin = new Thickness(8), Children =
        { ClassicChrome.Group("gbRevisionGraph", Text("gbRevisionGraph", "Revision graph"), options) } };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && (args.Source is not Button || args.Source is CheckBox))
            { args.Handled = true; }
        }, RoutingStrategies.Bubble);
    }

    internal ColorsSettingsValues Draft => new()
    {
        DrawAlternateBackground = _choices[nameof(ColorsSettingsValues.DrawAlternateBackground)].IsChecked == true,
        DrawNonRelativesGray = _choices[nameof(ColorsSettingsValues.DrawNonRelativesGray)].IsChecked == true,
        DrawNonRelativesTextGray = _choices[nameof(ColorsSettingsValues.DrawNonRelativesTextGray)].IsChecked == true,
        HighlightAuthoredRevisions = _choices[nameof(ColorsSettingsValues.HighlightAuthoredRevisions)].IsChecked == true,
        FillRefLabels = _choices[nameof(ColorsSettingsValues.FillRefLabels)].IsChecked == true
    };

    internal void AcceptSaved(ColorsSettingsPublication publication)
    {
        foreach (ColorsSetting field in ColorsSettingsCatalog.Fields)
        { _choices[field.Name].IsChecked = publication.Values.Get(field.Name); }
    }
}
