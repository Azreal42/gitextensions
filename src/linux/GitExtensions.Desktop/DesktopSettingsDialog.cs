using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

internal sealed class DesktopSettingsDialog : Window
{
    internal static readonly (GitRefsSortBy Value, string Label)[] ReferenceSortChoices =
    [
        (GitRefsSortBy.GitDefault, "Git default"), (GitRefsSortBy.AuthorDate, "Author date"),
        (GitRefsSortBy.CommitterDate, "Committer date"), (GitRefsSortBy.CreatorDate, "Creator date"),
        (GitRefsSortBy.TaggerDate, "Tagger date"), (GitRefsSortBy.RefName, "Alpha-numeric"),
        (GitRefsSortBy.VersionRefName, "Version"), (GitRefsSortBy.ObjectSize, "Object size"),
        (GitRefsSortBy.Upstream, "Originating remote")
    ];
    internal static string ReferenceOrderLabel(GitRefsSortOrder order) => order == GitRefsSortOrder.Ascending ? "A ↓ Z" : "Z ↑ A";
    public DesktopPreferences Result { get; private set; }
    public GitLogSort HistorySortResult { get; private set; }
    public FileHistoryPreferences FileHistoryResult { get; private set; }
    public HotkeyBindings HotkeysResult { get; private set; }
    public bool OpenGitConfiguration { get; private set; }
    internal FontPreferences FontsResult { get; private set; }
    public ScriptsPreferencesLoadResult ScriptsResult { get; private set; }
    private TabControl? _settingsPages;
    private TabItem? _gitEnvironmentTab, _checklistTab;
    internal GitEnvironmentSettingsSession? GitEnvironmentSession { get; private set; }
    internal void SelectGitEnvironmentPage(bool checklist)
    {
        if (_settingsPages is not null) { _settingsPages.SelectedItem = checklist ? _checklistTab : _gitEnvironmentTab; }
    }

    public DesktopSettingsDialog(DesktopPreferences initial, TranslationCatalog translations, bool canConfigureGit,
        GitLogSort historySort = GitLogSort.GitDefault, FileHistoryPreferences? fileHistoryPreferences = null,
        HotkeyBindings? hotkeys = null, Action<HotkeyBindings>? saveHotkeys = null,
        FontPreferences? fonts = null, Func<FontPreferences, FontPreferences, FontPreferences>? saveFonts = null, Exception? fontsLoadError = null,
        ScriptsPreferencesLoadResult? scripts = null,
        Func<ScriptsPreferencesLoadResult, IReadOnlyList<ScriptDefinition>, ScriptsPreferencesLoadResult>? saveScripts = null,
        Func<Window, Task>? configureGitTools = null,
        GitEnvironmentSettingsSession? gitEnvironment = null,
        Func<DesktopSettingsDialog, Task>? publishSettings = null,
        Func<GitEnvironmentCheckId, Window, Task>? repairEnvironment = null,
        bool showChecklist = false)
    {
        Result = initial; HistorySortResult = historySort;
        GitEnvironmentSession = gitEnvironment;
        FileHistoryResult = fileHistoryPreferences ?? new FileHistoryPreferencesStore().Load();
        HotkeyBindings activeHotkeys = hotkeys ?? new(); HotkeysResult = activeHotkeys.Clone();
        DesktopFontSession? fontSession = fonts is null ? DesktopFonts.CreateSession() : null;
        FontPreferences fontBaseline = fonts ?? fontSession!.Preferences; FontsResult = fontBaseline;
        Exception? fontError = fontsLoadError ?? fontSession?.LoadError;
        ScriptsResult = scripts ?? new(ScriptConfiguration.Read(null), false, ReadOnlyMemory<byte>.Empty);
        Name = "DesktopSettings"; Title = translations.Translate("Settings");
        Width = 740; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (configureGitTools is not null || gitEnvironment is not null)
        {
            // Preserve Main -> Settings -> busy Config close requests before the child veto.
            ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
            Window? closingOwner = null;
            Closing += (_, args) => { if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { closingOwner = Owner as Window; } };
            Closed += (_, _) =>
            {
                if (closingOwner is { } owner)
                { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
            };
        }
        ComboBox theme = new() { Name = "SettingsTheme", ItemsSource = new[] { "Light", "Dark", "System" }, SelectedItem = initial.Theme, MinWidth = 150 };
        var languageCatalog = LanguageCatalog.LoadDefault();
        LanguageChoice? initialLanguage = string.IsNullOrEmpty(initial.Language) ? LanguageCatalog.English : languageCatalog.Find(initial.Language);
        ComboBox language = new() { Name = "Language", ItemsSource = languageCatalog.Choices,
            SelectedItem = initialLanguage, PlaceholderText = initialLanguage is null ? "English (" + translations.Translate("fallback") + ")" : null, MinWidth = 180,
            ItemTemplate = new FuncDataTemplate<LanguageChoice>((choice, _) => new TextBlock { Text = choice?.Name }) };
        if (initialLanguage is null)
        { ToolTip.SetTip(language, initial.Language + "\n" + translations.Translate("Translation unavailable; using English")); }
        NumericUpDown history = new() { Name = "SettingsHistoryLimit", Minimum = 1, Maximum = 5000, Value = Math.Clamp(initial.HistoryLimit, 1, 5000), FormatString = "0", Width = 150 };
        CheckBox normalise = new() { Name = "SettingsNormaliseBranches", Content = translations.Translate("Auto normalise branch name"), IsChecked = initial.AutoNormaliseBranchName };
        CheckBox advanced = new() { Name = "SettingsAdvancedOptions", Content = translations.Translate("Always show advanced options"), IsChecked = initial.AlwaysShowAdvancedOptions };
        CheckBox fileHistory = new() { Name = "SettingsUseBrowseForFileHistory", Content = translations.Translate("Show file history in the main window"), IsChecked = initial.UseBrowseForFileHistory };
        CheckBox follow = new() { Name = "chkFollowRenamesInFileHistory", Content = new TextBlock { Text = translations.Translate("GeneralSettingsPage", "chkFollowRenamesInFileHistory", "Text", "Follow renames in file history"), TextWrapping = TextWrapping.Wrap }, IsChecked = FileHistoryResult.FollowRenames };
        CheckBox exact = new() { Name = "chkFollowRenamesInFileHistoryExact", Content = new TextBlock { Text = translations.Translate("GeneralSettingsPage", "chkFollowRenamesInFileHistoryExact", "Text", "Follow exact renames and copies only"), TextWrapping = TextWrapping.Wrap }, IsChecked = FileHistoryResult.ExactRenames };
        TextBox symbol = new() { Name = "SettingsNormaliseSymbol", Text = initial.AutoNormaliseSymbol, Width = 150 };
        TextBlock notice = new() { Name = "SettingsNotice", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) };
        StackPanel fields = new() { Margin = new Thickness(12), Spacing = 10 };
        GeneralField("Theme", theme);
        GeneralField(translations.Translate("AppearanceSettingsPage", "lblLanguage", "Text", "Language (restart required)"), language);
        GeneralField("Number of commits to load", history);
        fields.Children.Add(normalise); GeneralField("Replacement symbol", symbol); fields.Children.Add(advanced);
        fields.Children.Add(fileHistory);
        Grid fileHistoryOptions = new() { ColumnDefinitions = new ColumnDefinitions("*,*") };
        fileHistoryOptions.Children.Add(follow); Grid.SetColumn(exact, 1); fileHistoryOptions.Children.Add(exact); fields.Children.Add(fileHistoryOptions);
        fields.Children.Add(new TextBlock { Name = "TelemetryStatus", Text = translations.Translate("Telemetry is disabled. No usage data is sent."), TextWrapping = TextWrapping.Wrap });
        Button configuration = new() { Name = "OpenGitConfiguration", Content = translations.Translate("Edit Git configuration") + "…", IsEnabled = canConfigureGit, HorizontalAlignment = HorizontalAlignment.Left };
        configuration.Click += (_, _) => { OpenGitConfiguration = true; Close(false); }; fields.Children.Add(configuration);
        Button tools = new() { Name = "OpenGitToolsConfiguration", Content = translations.Translate("Git configuration") + "…", IsEnabled = configureGitTools is not null, HorizontalAlignment = HorizontalAlignment.Left };
        bool configuringTools = false;
        tools.Click += async (_, _) =>
        {
            if (configuringTools || configureGitTools is null || !IsVisible) { return; }
            configuringTools = true; tools.IsEnabled = false;
            try { await configureGitTools(this); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (IsVisible) { notice.Text = error.Message; } }
            finally { configuringTools = false; if (IsVisible) { tools.IsEnabled = configureGitTools is not null; } }
        };
        fields.Children.Add(tools);

        ComboBox revisions = new() { Name = "_NO_TRANSLATE_cmbRevisionsSortBy", ItemsSource = historySort == GitLogSort.Date ? new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate, GitLogSort.Topology, GitLogSort.Date } : new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate, GitLogSort.Topology }, SelectedItem = historySort,
            ItemTemplate = new FuncDataTemplate<GitLogSort>((sort, _) => new TextBlock { Text = sort switch { GitLogSort.GitDefault => "Git default", GitLogSort.AuthorDate => "Author date", GitLogSort.Topology => "Topo (Ancestor)", _ => "Date order" } }) };
        ComboBox references = new() { Name = "_NO_TRANSLATE_cmbBranchesSortBy", ItemsSource = ReferenceSortChoices.Select(choice => choice.Value).ToArray(), SelectedItem = initial.ReferenceSorting.By,
            ItemTemplate = new FuncDataTemplate<GitRefsSortBy>((sort, _) => new TextBlock { Text = ReferenceSortChoices.Single(choice => choice.Value == sort).Label }) };
        ComboBox order = new() { Name = "_NO_TRANSLATE_cmbBranchesOrder", ItemsSource = Enum.GetValues<GitRefsSortOrder>(), SelectedItem = initial.ReferenceSorting.Order,
            ItemTemplate = new FuncDataTemplate<GitRefsSortOrder>((sort, _) => new TextBlock { Text = ReferenceOrderLabel(sort) }) };
        TextBox branches = new() { Name = "txtPrioBranchNames", Text = initial.PrioritizedBranchNames };
        TextBox remotes = new() { Name = "txtPrioRemoteNames", Text = initial.PrioritizedRemoteNames };
        Grid sorting = new() { Name = "SortingSettingsPage", Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("Auto,*,20"), RowDefinitions = new RowDefinitions("29,29,29,29,29") };
        SortingField(0, "Sort revisions by", revisions, "Sorting revisions may delay rendering of the revision graph.");
        SortingField(1, "Sort branches by", references);
        // Windows Settings leaves direction editable under Git default; only the tree menu disables it.
        SortingField(2, "Order branches", order);
        SortingField(3, "Prioritized branches", branches, "Regex to prioritize branch names in the left panel.\nThe branches matching the pattern will be shown before the others.\nSeparate the priorities with ';'.");
        SortingField(4, "Prioritized remotes", remotes, "Regex to prioritize remote names in the left panel.\nThe remotes matching the pattern will be shown before the others.\nSeparate the priorities with ';'.");
        TabItem generalTab = new() { Name = "SettingsGeneralTab", Header = translations.Translate("General"), Content = fields };
        TabItem sortingTab = new() { Name = "SettingsSortingTab", Header = translations.Translate("Sorting"), Content = sorting };
        HotkeysSettingsPage hotkeysPage = new(activeHotkeys, translations) { Margin = new Thickness(12) };
        TabItem hotkeysTab = new() { Name = "SettingsHotkeysTab", Header = translations.Translate("HotkeysSettingsPage", "$this", "Text", "Hotkeys"), Content = hotkeysPage };
        AppearanceFontsSettingsPage fontsPage = new(fontBaseline, translations, fontError) { Margin = new Thickness(12), Height = 315 };
        TabItem fontsTab = new() { Name = "SettingsFontsTab", Header = translations.Translate("AppearanceFontsSettingsPage", "$this", "Text", "Fonts"), Content = fontsPage };
        ScriptsSettingsPage scriptsPage = new(ScriptsResult, translations) { Margin = new Thickness(12) };
        hotkeysPage.UpdateScripts(scriptsPage.Draft);
        scriptsPage.DraftChanged += draft => hotkeysPage.UpdateScripts(draft);
        TabItem scriptsTab = new() { Name = "SettingsScriptsTab", Header = translations.Translate("ScriptsSettingsPage", "$this", "Text", "Scripts"), Content = scriptsPage };
        TabControl tabs = new() { Name = "DesktopSettingsPages", MinHeight = 315, ItemsSource = new[] { generalTab, sortingTab, hotkeysTab, fontsTab, scriptsTab }, SelectedItem = generalTab };
        _settingsPages = tabs;
        if (gitEnvironment is not null)
        {
            _gitEnvironmentTab = new() { Name = "SettingsGitTab", Header = translations.Translate("GitSettingsPage", "$this", "Text", "Git"),
                Content = new ScrollViewer { Content = gitEnvironment.Page, Margin = new Thickness(12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } };
            _checklistTab = new() { Name = "SettingsChecklistTab", Header = translations.Translate("ChecklistSettingsPage", "$this", "Text", "Checklist"),
                Content = new ScrollViewer { Content = gitEnvironment.Checklist, Margin = new Thickness(12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } };
            tabs.ItemsSource = new[] { generalTab, sortingTab, hotkeysTab, fontsTab, scriptsTab, _gitEnvironmentTab, _checklistTab };
            if (showChecklist) { tabs.SelectedItem = _checklistTab; }
        }
        Button save = new() { Name = "SaveDesktopSettings", Content = translations.Translate("OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "CancelDesktopSettings", Content = translations.Translate("Cancel"), MinWidth = 75, IsCancel = true };
        bool saving = false, closeWhenIdle = false, accepting = false;
        cancel.Click += (_, _) => Close(false);
        save.Click += async (_, _) => await SaveAllAsync(closeOnSuccess: true);
        async Task<bool> SaveAllAsync(bool closeOnSuccess)
        {
            if (saving || gitEnvironment?.IsBusy == true) { return false; }
            saving = true; save.IsEnabled = false;
            try
            {
                if (history.Value is null || history.Value < 1 || history.Value > 5000 || (normalise.IsChecked == true && string.IsNullOrWhiteSpace(symbol.Text)))
                { tabs.SelectedItem = generalTab; notice.Text = translations.Translate("Enter a valid history limit and replacement symbol."); return false; }
                foreach (TextBox input in new[] { branches, remotes })
                {
                    try { GitReferencePriorities.Validate(input.Text ?? ""); }
                    catch (ArgumentException error) { tabs.SelectedItem = sortingTab; notice.Text = error.Message; input.Focus(); return false; }
                }
                if (!scriptsPage.Validate(out string? scriptValidation))
                { tabs.SelectedItem = scriptsTab; notice.Text = scriptValidation; return false; }
                if (!fontsPage.Validate(out string? fontValidation))
                { tabs.SelectedItem = fontsTab; notice.Text = fontValidation; return false; }
                if (gitEnvironment is not null && !gitEnvironment.Page.Validate(out string? environmentValidation))
                { tabs.SelectedItem = _gitEnvironmentTab; notice.Text = environmentValidation; return false; }
                if (scriptsPage.IsDirty)
                {
                    try
                    {
                        ScriptsResult = saveScripts?.Invoke(ScriptsResult, scriptsPage.Draft)
                            ?? new(new(scriptsPage.Draft, ScriptConfigurationSource.Xml), ScriptsResult.Existed, ScriptsResult.OriginalBytes);
                        scriptsPage.AcceptSaved(ScriptsResult);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
                    { tabs.SelectedItem = scriptsTab; scriptsPage.ShowSaveError(error); notice.Text = translations.Translate("Unable to save scripts.") + " " + error.Message; return false; }
                }
                HotkeyBindings acceptedHotkeys = hotkeysPage.Draft.Clone();
                try { saveHotkeys?.Invoke(acceptedHotkeys.Clone()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                { tabs.SelectedItem = hotkeysTab; notice.Text = translations.Translate("Unable to save hotkeys.") + " " + error.Message; return false; }
                try
                {
                    // An unreadable, untouched font profile is not replaced by unrelated Settings changes.
                    FontsResult = fontError is not null && fontsPage.Draft == fontBaseline ? fontBaseline
                        : saveFonts?.Invoke(fontBaseline, fontsPage.Draft) ?? fontsPage.Draft;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { tabs.SelectedItem = fontsTab; fontsPage.ShowSaveError(error); notice.Text = translations.Translate("Unable to save font preferences.") + " " + error.Message; return false; }
                if (gitEnvironment is not null && !gitEnvironment.TrySave())
                { tabs.SelectedItem = _gitEnvironmentTab; return false; }
                Result = initial with
                {
                    Theme = theme.SelectedItem as string ?? "Light", HistoryLimit = (int)history.Value,
                    Language = (language.SelectedItem as LanguageChoice)?.Name ?? initial.Language,
                    AutoNormaliseBranchName = normalise.IsChecked == true, AutoNormaliseSymbol = symbol.Text ?? "_", AlwaysShowAdvancedOptions = advanced.IsChecked == true,
                    UseBrowseForFileHistory = fileHistory.IsChecked == true,
                    ReferenceSorting = new((GitRefsSortBy)references.SelectedItem!, (GitRefsSortOrder)order.SelectedItem!),
                    PrioritizedBranchNames = branches.Text ?? "", PrioritizedRemoteNames = remotes.Text ?? ""
                };
                HistorySortResult = (GitLogSort)revisions.SelectedItem!;
                FileHistoryResult = FileHistoryResult with { FollowRenames = follow.IsChecked == true, ExactRenames = exact.IsChecked == true };
                HotkeysResult = acceptedHotkeys;
                if (publishSettings is not null)
                {
                    tabs.IsEnabled = false;
                    try { await publishSettings(this); }
                    catch (OperationCanceledException) when (closeWhenIdle) { return false; }
                    catch (Exception error)
                    {
                        notice.Text = translations.Translate("Unable to save settings.") + " " + error.Message + "\n"
                            + translations.Translate("Some settings may already have been saved. Cancel discards only unsaved changes.");
                        return false;
                    }
                }
                fontBaseline = FontsResult;
                if (closeOnSuccess && !closeWhenIdle) { accepting = true; try { Close(true); } finally { accepting = false; } }
                return !closeWhenIdle;
            }
            finally
            {
                saving = false; tabs.IsEnabled = true; save.IsEnabled = gitEnvironment?.IsBusy != true;
                if (closeWhenIdle && gitEnvironment?.IsBusy != true) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(false); } }); }
            }
        }
        if (gitEnvironment is not null)
        {
            gitEnvironment.SaveAllRequested = () => SaveAllAsync(closeOnSuccess: false);
            gitEnvironment.RepairRequested = id => repairEnvironment is null ? Task.CompletedTask : repairEnvironment(id, this);
            gitEnvironment.BusyChanged += () => { save.IsEnabled = !saving && !gitEnvironment.IsBusy; };
            gitEnvironment.Settled += () =>
            {
                if (closeWhenIdle && !saving) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(false); } }); }
            };
            Closing += (_, args) =>
            {
                if (!accepting && (gitEnvironment.IsBusy || saving))
                { args.Cancel = true; closeWhenIdle = true; gitEnvironment.Cancel(); }
            };
            Closed += (_, _) => gitEnvironment.Dispose();
            tabs.SelectionChanged += async (_, _) =>
            {
                if (IsVisible && tabs.SelectedItem == _checklistTab && gitEnvironment.LastScan is null)
                { await gitEnvironment.ScanAsync(); }
            };
        }
        DockPanel root = new() { LastChildFill = true };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(12, 6), Children = { save, cancel } };
        if (gitEnvironment is not null)
        {
            Button reloadEnvironment = new() { Name = "ReloadGitEnvironment", Content = translations.Translate("Reload"), MinWidth = 75 };
            reloadEnvironment.Click += (_, _) => gitEnvironment.Reload();
            gitEnvironment.BusyChanged += () => reloadEnvironment.IsEnabled = !gitEnvironment.IsBusy && !saving;
            footer.Children.Insert(0, reloadEnvironment);
        }
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); DockPanel.SetDock(notice, Dock.Bottom); root.Children.Add(notice); root.Children.Add(tabs); Content = root;
        Opened += async (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen) { MaxHeight = Math.Max(260, screen.WorkingArea.Height / screen.Scaling - 40); }
            if (showChecklist) { gitEnvironment?.Checklist.Focus(); } else { theme.Focus(); }
            ModalWindowActivation.AfterMapping(this, () => IsVisible);
            if (showChecklist && gitEnvironment is not null) { await gitEnvironment.ScanAsync(); }
        };

        void GeneralField(string label, Control editor)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = translations.Translate(label), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(editor, 1); row.Children.Add(editor); fields.Children.Add(row);
        }
        void SortingField(int row, string label, Control editor, string? help = null)
        {
            TextBlock caption = new() { Text = translations.Translate(label), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(caption, row); sorting.Children.Add(caption); editor.Margin = new Thickness(0, 2, 0, 2);
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); sorting.Children.Add(editor);
            if (help is null) { return; }
            Control icon = GitExtensions.Linux.Desktop.Controls.OriginalMenuItems.Icon("information.png");
            icon.VerticalAlignment = VerticalAlignment.Center; icon.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(icon, translations.Translate(help));
            Grid.SetRow(icon, row); Grid.SetColumn(icon, 2); sorting.Children.Add(icon);
        }
    }
}
