using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original Appearance groups, with unavailable Windows consumers explicitly disabled.</summary>
internal sealed class AppearanceSettingsPage : UserControl
{
    internal const string TranslationHelpUrl = "https://github.com/gitextensions/gitextensions/wiki/Translations";
    private static readonly string[] Themes = ["Light", "Dark", "System"];
    private readonly TranslationCatalog _translations;
    private readonly LanguageCatalog _languages;
    private readonly Func<Window, Task>? _helpTranslateAsync;
    private readonly CheckBox _relativeDate, _repoBranch, _avatar;
    private readonly ComboBox _theme, _language;
    private readonly HyperlinkButton _helpTranslate;
    private readonly TextBlock _notice = new()
    { Name = "AppearanceSettingsNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private AppearanceSettingsValues _accepted;
    private bool _loading, _languageEdited, _helpBusy;
    private long _revision;

    internal AppearanceSettingsValues Draft => _accepted with
    {
        RelativeDate = _relativeDate.IsChecked == true,
        ShowRepoCurrentBranch = _repoBranch.IsChecked == true,
        ShowAvatar = _avatar.IsChecked == true,
        Theme = _theme.SelectedItem is string theme && Themes.Contains(theme) ? theme : _accepted.Theme,
        Language = _languageEdited && _language.SelectedItem is LanguageChoice choice ? choice.Name : _accepted.Language
    };

    internal AppearanceSettingsPage(AppearanceSettingsValues initial, TranslationCatalog translations,
        Func<Window, Task>? helpTranslateAsync = null, LanguageCatalog? languages = null)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(translations);
        _accepted = initial; _translations = translations; _languages = languages ?? LanguageCatalog.LoadDefault();
        _helpTranslateAsync = helpTranslateAsync; Name = "AppearanceSettingsPage";

        Grid general = Rows("tlpnlGeneral");
        _relativeDate = Check("chkShowRelativeDate", "Show relative date instead of full date");
        _repoBranch = Check("chkShowRepoCurrentBranch",
            "Show current branch names in the dashboard and the recent repositories dropdown menu");
        AddWide(general, _relativeDate); AddWide(general, _repoBranch);
        AddWide(general, UnavailableCheck("chkShowCurrentBranchInVisualStudio", "Show current branch in Visual Studio",
            "Visual Studio branch integration is not available in this Linux version."));
        CheckBox scaling = UnavailableCheck("chkEnableAutoScale", "Auto scale user interface when high DPI is used",
            "Avalonia scales the Linux interface automatically. The Windows auto-scale setting is not available.");
        scaling.IsChecked = true;
        AddWide(general, scaling);
        AddField(general, Caption("truncateLongFilenames", "Truncate long filenames"),
            UnavailableChoice("truncatePathMethod",
                "The Windows path truncation choices and their file-name filter behavior are not connected on Linux."));
        _theme = Choice("SettingsTheme", Themes);
        _theme.ItemTemplate = new FuncDataTemplate<string>((theme, _) => new TextBlock { Text = _translations.Translate(theme ?? "") });
        AddField(general, new TextBlock { Name = "SettingsThemeLabel", Text = _translations.Translate("Theme") + " (Linux)",
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, _theme);

        Grid author = Rows("tlpnlAuthor");
        _avatar = Check("ShowAuthorAvatarInCommitGraph", "Show author's avatar column in the commit graph");
        ToolTip.SetTip(_avatar, _translations.Translate("The revision grid currently displays locally generated author initials."));
        AddWide(author, _avatar);
        AddWide(author, UnavailableCheck("ShowAuthorAvatarInCommitInfo", "Show author's avatar in the commit info view",
            "Author images in the commit information view are not available in this Linux version."));
        NumericUpDown cacheDays = ClassicNumericInput.Create("_NO_TRANSLATE_DaysToCacheImages", 0, 400);
        cacheDays.TextAlignment = TextAlignment.Right; cacheDays.Value = null;
        Disable(cacheDays, "The configurable Windows avatar cache is not connected on Linux. This field does not change the existing avatar cache.");
        AddField(author, Caption("lblCacheDays", "Cache images (days)"), cacheDays, UnavailableCaption());
        const string providerLimit = "Configurable avatar providers are not connected to the Linux revision grid. The grid uses local author initials.";
        const string fallbackLimit = "Configurable generated avatar styles are not available in the Linux revision grid.";
        AddField(author, Caption("lblAvatarProvider", "Avatar provider"), UnavailableChoice("AvatarProvider", providerLimit),
            UnavailableInfo("avatarProviderHelp", providerLimit));
        AddField(author, Caption("lblNoImageService", "Fallback generated avatar style"),
            UnavailableChoice("_NO_TRANSLATE_NoImageService", fallbackLimit), UnavailableInfo("pictureAvatarHelp", fallbackLimit));
        TextBox template = new() { Name = "txtCustomAvatarTemplate", Text = "", Watermark = UnavailableText,
            Height = 24, MinHeight = 24, Padding = new Thickness(4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        Disable(template, "Custom avatar templates are not interpreted by the Linux revision grid.");
        AddField(author, Caption("lblCustomAvatarTemplate", "Custom avatar template"), template);
        Button clearCache = new() { Name = "ClearImageCache", Content = Text("ClearImageCache", "Clear image cache"),
            Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        Disable(clearCache, "There is no shared configurable Appearance avatar cache to clear in this Linux version.");
        int clearRow = AddRow(author); Put(author, clearCache, clearRow, 1);

        Grid language = Rows("tlpnlLanguage");
        _language = Choice("Language", _languages.Choices);
        _language.MinWidth = 180;
        _language.ItemTemplate = new FuncDataTemplate<LanguageChoice>((choice, _) => new TextBlock { Text = choice?.Name });
        _language.SelectionChanged += (_, _) =>
        {
            if (_loading) { return; }
            _languageEdited = true; UpdateLanguageDiagnostic(); ShowError(null);
        };
        _helpTranslate = new() { Name = "helpTranslate", Content = Text("helpTranslate", "Help translate"),
            Padding = default, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = helpTranslateAsync is not null };
        ToolTip.SetTip(_helpTranslate, helpTranslateAsync is null
            ? _translations.Translate("Translation help is not connected to a browser launcher.")
            : TranslationHelpUrl);
        _helpTranslate.Click += async (_, _) => await OpenTranslationHelpAsync();
        AddField(language, Caption("lblLanguage", "Language (restart required)"), _language, _helpTranslate);
        HyperlinkButton dictionaries = new() { Name = "downloadDictionary", Content = Text("downloadDictionary", "Download dictionary"),
            Padding = default, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        Disable(dictionaries, "The Linux commit editor does not yet have a spelling dictionary engine. Downloading a Windows dictionary would not enable spell checking.");
        AddField(language, Caption("lblSpellingDictionary", "Dictionary for spelling checker"),
            UnavailableChoice("Dictionary", "Spelling dictionaries are not connected to the Linux commit editor."), dictionaries);

        TextBlock unsupported = new() { Name = "AppearanceUnsupportedNotice",
            Text = _translations.Translate("Some Windows appearance options are not yet available on Linux. Their controls are disabled."),
            TextWrapping = TextWrapping.Wrap };
        StackPanel body = new() { Name = "tlpnlMain", Spacing = 5, Children =
        {
            ClassicChrome.Group("gbGeneral", Text("gbGeneral", "General"), general),
            ClassicChrome.Group("gbAuthorImages", Text("gbAuthorImages", "Author images"), author),
            ClassicChrome.Group("gbLanguages", Text("gbLanguages", "Language"), language), unsupported, _notice
        } };
        Grid.SetIsSharedSizeScope(body, true);
        Content = body;
        // A selection or text editor consuming Enter must not also activate the host's default OK button.
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && args.Source is not Button)
            { args.Handled = true; }
        }, RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => ++_revision;
        AcceptSaved(initial);
    }

    internal bool Validate(out string? error)
    {
        if (_theme.SelectedItem is not string theme || !Themes.Contains(theme))
        { return Invalid(_theme, "Select a theme.", out error); }
        if (_language.SelectedItem is LanguageChoice choice)
        {
            if (!_languages.Choices.Contains(choice))
            { return Invalid(_language, "Select an installed translation.", out error); }
        }
        else if (_languageEdited)
        { return Invalid(_language, "Select an installed translation.", out error); }
        // An unknown initial language intentionally remains unchanged until the user chooses a replacement.
        // It is diagnosed with the same English fallback used by the existing Settings language selector.
        ShowError(null); error = null; return true;
    }

    internal void AcceptSaved(AppearanceSettingsValues published)
    {
        ArgumentNullException.ThrowIfNull(published);
        ++_revision; _loading = true;
        try
        {
            _accepted = published; _languageEdited = false;
            _relativeDate.IsChecked = published.RelativeDate; _repoBranch.IsChecked = published.ShowRepoCurrentBranch;
            _avatar.IsChecked = published.ShowAvatar;
            _theme.SelectedItem = Themes.Contains(published.Theme) ? published.Theme : "Light";
            _language.SelectedItem = string.IsNullOrEmpty(published.Language) ? null : _languages.Find(published.Language);
        }
        finally { _loading = false; }
        UpdateLanguageDiagnostic(); ShowError(null);
    }

    private async Task OpenTranslationHelpAsync()
    {
        if (_helpBusy || _helpTranslateAsync is null || TopLevel.GetTopLevel(this) is not Window { IsVisible: true } owner)
        { return; }
        _helpBusy = true; _helpTranslate.IsEnabled = false;
        long revision = _revision; ShowError(null);
        try { await _helpTranslateAsync(owner); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (revision == _revision && owner.IsVisible && TopLevel.GetTopLevel(this) == owner) { ShowError(error.Message); }
        }
        finally { _helpBusy = false; _helpTranslate.IsEnabled = _helpTranslateAsync is not null; }
    }

    private void UpdateLanguageDiagnostic()
    {
        bool unavailable = !_languageEdited && !string.IsNullOrEmpty(_accepted.Language) && _languages.Find(_accepted.Language) is null;
        _language.PlaceholderText = unavailable ? "English (" + _translations.Translate("fallback") + ")"
            : !_languageEdited && string.IsNullOrEmpty(_accepted.Language) ? LanguageCatalog.English.Name : null;
        ToolTip.SetTip(_language, unavailable
            ? _accepted.Language + "\n" + _translations.Translate("Translation unavailable; using English") : null);
    }

    private string UnavailableText => _translations.Translate("unavailable on Linux");
    private TextBlock UnavailableCaption() => new() { Text = UnavailableText, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center };
    private CheckBox Check(string name, string source) => new()
    {
        Name = name, Content = new TextBlock { Text = Text(name, source), TextWrapping = TextWrapping.Wrap },
        MinHeight = 23, Padding = default, VerticalAlignment = VerticalAlignment.Center
    };
    private CheckBox UnavailableCheck(string name, string source, string explanation)
    {
        CheckBox check = Check(name, source);
        ((TextBlock)check.Content!).Text += " (" + UnavailableText + ")";
        Disable(check, explanation); return check;
    }
    private ComboBox UnavailableChoice(string name, string explanation)
    {
        ComboBox choice = Choice(name, new[] { UnavailableText }); choice.SelectedIndex = 0;
        Disable(choice, explanation); return choice;
    }
    private Button UnavailableInfo(string name, string explanation)
    {
        Button info = new() { Name = name, Content = OriginalMenuItems.Icon("information.png"),
            Width = 20, Height = 24, MinWidth = 0, MinHeight = 0, Padding = default,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        Disable(info, explanation); return info;
    }
    private void Disable(Control control, string explanation)
    { control.IsEnabled = false; ToolTip.SetTip(control, _translations.Translate(explanation)); }
    private static ComboBox Choice(string name, System.Collections.IEnumerable choices) => new()
    {
        Name = name, ItemsSource = choices, MinWidth = 150, MinHeight = 24, Height = 24,
        Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private TextBlock Caption(string name, string source) => new()
    { Name = name, Text = Text(name, source), MaxWidth = 285, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Grid Rows(string name)
    {
        Grid grid = new() { Name = name, ColumnSpacing = 8, RowSpacing = 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "AppearanceCaption" });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "AppearanceEditor" });
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        return grid;
    }
    private static int AddRow(Grid grid)
    { int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); return row; }
    private static void AddWide(Grid grid, Control control) => Put(grid, control, AddRow(grid), 0, 3);
    private static void AddField(Grid grid, Control caption, Control editor, Control? trailing = null)
    {
        int row = AddRow(grid); Put(grid, caption, row, 0); Put(grid, editor, row, 1);
        if (trailing is not null) { Put(grid, trailing, row, 2); }
    }
    private static void Put(Grid grid, Control control, int row, int column, int span = 1)
    { Grid.SetRow(control, row); Grid.SetColumn(control, column); Grid.SetColumnSpan(control, span); grid.Children.Add(control); }
    private string Text(string name, string source) => _translations.Translate("AppearanceSettingsPage", name, "Text", source);
    private bool Invalid(Control control, string message, out string? error)
    { error = _translations.Translate(message); ShowError(error); control.Focus(); return false; }
    private void ShowError(string? error) { _notice.Text = error; _notice.IsVisible = !string.IsNullOrEmpty(error); }
}
