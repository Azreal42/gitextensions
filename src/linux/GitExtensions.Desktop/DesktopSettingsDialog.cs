using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class DesktopSettingsDialog : Window
{
    public DesktopPreferences Result { get; private set; }
    public bool OpenGitConfiguration { get; private set; }

    public DesktopSettingsDialog(DesktopPreferences initial, TranslationCatalog translations, bool canConfigureGit)
    {
        Result = initial;
        Name = "DesktopSettings"; Title = translations.Translate("Settings");
        Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ComboBox theme = new() { Name = "SettingsTheme", ItemsSource = new[] { "Light", "Dark", "System" }, SelectedItem = initial.Theme, MinWidth = 150 };
        NumericUpDown history = new() { Name = "SettingsHistoryLimit", Minimum = 1, Maximum = 5000, Value = Math.Clamp(initial.HistoryLimit, 1, 5000), FormatString = "0", Width = 150 };
        CheckBox normalise = new() { Name = "SettingsNormaliseBranches", Content = translations.Translate("Auto normalise branch name"), IsChecked = initial.AutoNormaliseBranchName };
        TextBox symbol = new() { Name = "SettingsNormaliseSymbol", Text = initial.AutoNormaliseSymbol, Width = 150 };
        TextBlock notice = new() { Name = "SettingsNotice", TextWrapping = TextWrapping.Wrap };
        StackPanel fields = new() { Margin = new Thickness(12), Spacing = 10 };
        Field("Theme", theme); Field("Number of commits to load", history);
        fields.Children.Add(normalise); Field("Replacement symbol", symbol);
        fields.Children.Add(new TextBlock { Name = "TelemetryStatus", Text = translations.Translate("Telemetry is disabled. No usage data is sent."), TextWrapping = TextWrapping.Wrap });
        Button configuration = new() { Name = "OpenGitConfiguration", Content = translations.Translate("Git configuration") + "…", IsEnabled = canConfigureGit, HorizontalAlignment = HorizontalAlignment.Left };
        configuration.Click += (_, _) => { OpenGitConfiguration = true; Close(false); };
        fields.Children.Add(configuration);
        fields.Children.Add(notice);
        Button save = new() { Name = "SaveDesktopSettings", Content = translations.Translate("OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "CancelDesktopSettings", Content = translations.Translate("Cancel"), MinWidth = 75, IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) =>
        {
            if (history.Value is null || history.Value < 1 || history.Value > 5000 || (normalise.IsChecked == true && string.IsNullOrWhiteSpace(symbol.Text)))
            { notice.Text = translations.Translate("Enter a valid history limit and replacement symbol."); return; }
            Result = initial with { Theme = theme.SelectedItem as string ?? "Light", HistoryLimit = (int)history.Value, AutoNormaliseBranchName = normalise.IsChecked == true, AutoNormaliseSymbol = symbol.Text ?? "_" };
            Close(true);
        };
        fields.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { save, cancel } });
        Content = fields;

        void Field(string label, Control editor)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = translations.Translate(label), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(editor, 1); row.Children.Add(editor); fields.Children.Add(row);
        }
    }
}
