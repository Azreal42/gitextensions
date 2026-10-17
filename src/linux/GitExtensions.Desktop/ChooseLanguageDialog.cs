using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The one-activation first-run chooser. Closing without a choice persists English, as on Windows.</summary>
internal sealed class ChooseLanguageDialog : Window
{
    private readonly ListBox _languages = new() { Name = "lvTranslations", SelectionMode = SelectionMode.Single };
    private readonly TextBlock _error = new() { Name = "LanguageSaveError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly PreferencesStore _store;
    private readonly Dictionary<string, Bitmap> _flags = new(StringComparer.Ordinal);
    private LanguageChoice? _accepted;
    internal bool WasSaved { get; private set; }
    internal LanguageChoice? SelectedChoice => _accepted;
    internal ChooseLanguageDialog(LanguageCatalog catalog, PreferencesStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); _store = store ?? new();
        TranslationCatalog text = TranslationCatalog.LoadDefault();
        string T(string control, string source) => text.Translate("FormChooseTranslation", control, "Text", source);
        Name = "FormChooseTranslation"; Title = T("$this", "Choose language");
        Width = 800; Height = 539; MinWidth = 440; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _languages.ItemsSource = catalog.Choices;
        _languages.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel());
        _languages.ItemTemplate = new FuncDataTemplate<LanguageChoice>((choice, _) =>
        {
            if (choice is null) { return new TextBlock(); }
            StackPanel tile = new() { Width = 170, MinHeight = 100, Spacing = 4, Margin = new Thickness(3) };
            string image = Path.Combine(catalog.DirectoryPath, choice.Name + ".gif");
            if (File.Exists(image))
            {
                try
                {
                    if (!_flags.TryGetValue(image, out Bitmap? bitmap)) { bitmap = new(image); _flags.Add(image, bitmap); }
                    tile.Children.Add(new Image { Source = bitmap, Width = 150, Height = 75, Stretch = Stretch.Uniform });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { _error.Text = error.Message; _error.IsVisible = true; }
            }
            tile.Children.Add(new TextBlock { Text = choice.Name, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            return tile;
        });
        _languages.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left || _languages.SelectedItem is not LanguageChoice choice) { return; }
            ListBoxItem? row = (_languages.InputHitTest(args.GetPosition(_languages)) as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is LanguageChoice hit && hit == choice) { _accepted = choice; Close(); }
        };
        _languages.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && _languages.SelectedItem is LanguageChoice choice)
            { args.Handled = true; _accepted = choice; Close(); }
        };
        Grid content = new() { Margin = new Thickness(12), RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8 };
        content.Children.Add(new TextBlock { Name = "label1", Text = T("label1", "Choose your language"), TextWrapping = TextWrapping.Wrap });
        TextBlock hint = new() { Name = "label2", Text = T("label2", "You can change the language at any time in the settings dialog"), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(hint, 1); content.Children.Add(hint); Grid.SetRow(_languages, 2); content.Children.Add(_languages);
        Grid.SetRow(_error, 3); content.Children.Add(_error); Content = content;
        if (catalog.Failures.Count > 0) { _error.Text = string.Join("\n", catalog.Failures); _error.IsVisible = true; }
        Closing += (_, args) =>
        {
            if (WasSaved) { return; }
            try
            {
                DesktopPreferences latest = _store.Load();
                string language = _accepted?.Name ?? (string.IsNullOrEmpty(latest.Language) ? LanguageCatalog.English.Name : latest.Language);
                _store.Save(latest with { Language = language });
                WasSaved = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { args.Cancel = true; _error.Text = error.Message; _error.IsVisible = true; }
        };
        Opened += (_, _) => { _languages.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
        Closed += (_, _) => { foreach (Bitmap image in _flags.Values) { image.Dispose(); } _flags.Clear(); };
    }
}
