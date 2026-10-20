using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed record AdvancedSymbolChoice(string Value, string Caption);

/// <summary>The portable original Checkout/General/Commit controls; no inactive updater or terminal options.</summary>
internal sealed class AdvancedSettingsPage : UserControl
{
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly ComboBox _symbol = new() { Name = "SettingsNormaliseSymbol", IsEditable = false, MinWidth = 85, Height = 25, HorizontalAlignment = HorizontalAlignment.Left };
    private AdvancedSettingsValues _baseline;
    internal AdvancedSettingsPage(AdvancedSettingsSession session, TranslationCatalog translations)
    {
        _baseline = session.Initial; Name = "AdvancedSettingsPage";
        string Text(string name, string fallback) => translations.Translate("AdvancedSettingsPage", name, "Text", fallback).Replace("&&", "&", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);
        StackPanel checkout = new() { Spacing = 1 }, general = new() { Spacing = 1 }, commit = new() { Spacing = 1 };
        Add(checkout, nameof(AdvancedSettingsValues.AlwaysShowCheckoutDialog));
        Add(checkout, nameof(AdvancedSettingsValues.UseDefaultCheckoutChanges));
        Add(general, nameof(AdvancedSettingsValues.AlwaysShowAdvancedOptions));
        Add(general, nameof(AdvancedSettingsValues.AutoNormaliseBranchName));
        List<AdvancedSymbolChoice> symbols = [new("_", "_"), new("-", "-"), new("", "(none)")];
        // A foreign saved token is displayed read-only; it never becomes an extra explicit menu choice.
        _symbol.ItemsSource = symbols;
        _symbol.ItemTemplate = new FuncDataTemplate<AdvancedSymbolChoice>((choice, _) => new TextBlock { Text = choice?.Caption });
        SelectSymbol(_baseline.AutoNormaliseSymbol);
        Grid replacement = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(0, 2) };
        replacement.Children.Add(new TextBlock { Name = "label1", Text = Text("label1", "Symbol to use:"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_symbol, 1); replacement.Children.Add(_symbol); general.Children.Add(replacement);
        Add(commit, nameof(AdvancedSettingsValues.CommitAndPushForcedWhenAmend));
        Content = new StackPanel { Name = "tableLayoutPanel2", Margin = new Thickness(8), Spacing = 6, Children =
        {
            ClassicChrome.Group("CheckoutGB", Text("CheckoutGB", "Checkout"), checkout),
            ClassicChrome.Group("GeneralGB", Text("GeneralGB", "General"), general),
            ClassicChrome.Group("grpCommit", Text("grpCommit", "Commit"), commit)
        } };
        _choices[nameof(AdvancedSettingsValues.AutoNormaliseBranchName)].IsCheckedChanged += (_, _) => UpdateSymbolAvailability();
        UpdateSymbolAvailability();
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && (args.Source is not Button || args.Source is CheckBox))
            { args.Handled = true; }
        }, RoutingStrategies.Bubble);
        void Add(StackPanel group, string name)
        {
            AdvancedSetting setting = AdvancedSettingsCatalog.Fields.Single(setting => setting.Name == name);
            CheckBox choice = new() { Name = setting.Control, Tag = setting.OriginalControl, IsChecked = (bool)_baseline.Get(name), MinHeight = 24,
                Padding = default, HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = Text(setting.OriginalControl, setting.Caption), TextWrapping = TextWrapping.Wrap } };
            _choices.Add(name, choice); group.Children.Add(choice);
        }
    }
    internal AdvancedSettingsValues Draft
    {
        get
        {
            AdvancedSettingsValues values = _baseline;
            foreach (AdvancedSetting setting in AdvancedSettingsCatalog.Fields.Where(setting => !setting.IsSymbol))
            { values = values.With(setting.Name, _choices[setting.Name].IsChecked == true); }
            return values with { AutoNormaliseSymbol = (_symbol.SelectedItem as AdvancedSymbolChoice)?.Value ?? _baseline.AutoNormaliseSymbol };
        }
    }
    internal void AcceptSaved(AdvancedSettingsSaveReceipt receipt)
    {
        foreach (AdvancedSetting setting in receipt.Fields)
        {
            object value = receipt.Values.Get(setting.Name); _baseline = _baseline.With(setting.Name, value);
            if (!setting.IsSymbol) { _choices[setting.Name].IsChecked = (bool)value; }
            else { SelectSymbol((string)value); }
        }
        UpdateSymbolAvailability();
    }
    private void SelectSymbol(string value)
    {
        AdvancedSymbolChoice? choice = _symbol.Items.OfType<AdvancedSymbolChoice>().SingleOrDefault(choice => choice.Value == value);
        _symbol.PlaceholderText = choice is null ? value : "";
        _symbol.SelectedItem = choice;
    }
    private void UpdateSymbolAvailability() => _symbol.IsEnabled = _choices[nameof(AdvancedSettingsValues.AutoNormaliseBranchName)].IsChecked == true;
}
