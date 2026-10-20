using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using System.Text.Json;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Desktop.Controls;
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
    internal GeneralSettingsValues GeneralSettingsResult { get; private set; }
    private readonly GeneralSettingsPage _generalSettingsPage;
    internal ConfirmationsSettingsSession ConfirmationsSettingsSession { get; }
    private readonly ConfirmationsSettingsPage _confirmationsSettingsPage;
    internal AdvancedSettingsSession AdvancedSettingsSession { get; }
    private readonly AdvancedSettingsPage _advancedSettingsPage;
    internal ColorsSettingsSession ColorsSettingsSession { get; }
    private readonly ColorsSettingsPage _colorsSettingsPage;
    internal AppearanceSettingsValues AppearanceSettingsResult { get; private set; }
    private readonly AppearanceSettingsPage _appearanceSettingsPage;
    internal DetailedSettingsSession DetailedSettingsSession { get; }
    private readonly DetailedSettingsPage _detailedSettingsPage;
    internal RevisionLinksSettingsSession RevisionLinksSettingsSession { get; }
    private readonly RevisionLinksSettingsPage _revisionLinksSettingsPage;
    internal GitHubActionsSettingsSession GitHubActionsSettingsSession { get; }
    private readonly BuildServerIntegrationSettingsPage _buildServerSettingsPage;
    private readonly ConsoleStyleSettingsSession _consoleStyleSession;
    private readonly ConsoleStyleSettingsPage _consoleStylePage;
    private readonly CancellationTokenSource _detailedCancellation = new();
    internal void AcceptDetailedSaved() => _detailedSettingsPage.AcceptSaved();
    internal void AcceptAppearanceSaved(AppearanceSettingsPublication publication)
    {
        Result = publication.Desktop;
        AppearanceSettingsResult = publication.Values; _appearanceSettingsPage.AcceptSaved(publication.Values);
    }
    internal void AcceptGeneralSaved(GeneralSettingsPublication publication)
    {
        Result = publication.Desktop; FileHistoryResult = publication.FileHistory;
        GeneralSettingsResult = publication.Values; _generalSettingsPage.AcceptSaved(publication.Values);
    }
    private static string? _lastSelectedPage;
    internal SettingsNavigationView Navigation { get; private set; } = null!;
    internal GitEnvironmentSettingsSession? GitEnvironmentSession { get; private set; }
    internal void SelectGitEnvironmentPage(bool checklist)
    {
        Navigation.SelectPage(checklist ? "Checklist" : "GitPaths", focusContent: true);
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
        bool showChecklist = false, string? initialPage = null, GeneralSettingsValues? generalSettings = null,
        IReadOnlyList<string>? cloneDestinations = null, AppearanceSettingsValues? appearanceSettings = null,
        Func<Window, Task>? helpTranslateAsync = null,
        Func<Window, CancellationToken, Task>? clearAvatarCacheAsync = null,
        Func<Window, string, Task>? avatarHelpAsync = null, CancellationToken appearanceCancellationToken = default,
        DetailedSettingsSession? detailedSettings = null,
        RevisionLinksSettingsSession? revisionLinksSettings = null,
        GitHubActionsSettingsSession? gitHubActionsSettings = null,
        ConsoleStyleSettingsSession? consoleStyleSettings = null,
        Func<string, Task>? openSettingsUrlAsync = null,
        Func<Window, Task>? configureGitAdvanced = null,
        ConfirmationsSettingsSession? confirmations = null,
        ColorsSettingsSession? colors = null,
        Func<ColorsSettingsValues, CancellationToken, ColorsSettingsPublication>? publishColors = null,
        AdvancedSettingsSession? advancedSettings = null,
        Action<AdvancedSettingsSaveReceipt>? publishAdvanced = null)
    {
        Result = initial; HistorySortResult = historySort;
        GitEnvironmentSession = gitEnvironment;
        FileHistoryResult = fileHistoryPreferences ?? new FileHistoryPreferencesStore().Load();
        GeneralSettingsResult = generalSettings ?? new GeneralSettingsSession(initial, FileHistoryResult).Initial;
        _generalSettingsPage = new(GeneralSettingsResult, translations, cloneDestinations);
        ConfirmationsSettingsSession = confirmations ?? new();
        _confirmationsSettingsPage = new(ConfirmationsSettingsSession, translations);
        AdvancedSettingsSession = advancedSettings ?? new(initialDesktop: initial);
        _advancedSettingsPage = new(AdvancedSettingsSession, translations);
        ColorsSettingsSession = colors ?? new(new HistoryViewPreferencesStore().Load());
        _colorsSettingsPage = new(ColorsSettingsSession.Initial, translations);
        DetailedSettingsSession = detailedSettings ?? new(null, initial);
        DistributedSettingsWorkspace workspace = DetailedSettingsSession.Workspace;
        GitRepository? scopedRepository = DetailedSettingsSession.Repository;
        RevisionLinksSettingsSession = revisionLinksSettings ?? new(scopedRepository, workspace);
        _revisionLinksSettingsPage = new(RevisionLinksSettingsSession, translations) { Margin = new Thickness(12) };
        GitHubActionsSettingsSession = gitHubActionsSettings ?? new(scopedRepository, workspace);
        _buildServerSettingsPage = new(GitHubActionsSettingsSession, translations, openSettingsUrlAsync is null ? null : () => { _ = openSettingsUrlAsync("https://github.com/settings/personal-access-tokens/new"); }, openSettingsUrlAsync is null ? null : uri => { _ = openSettingsUrlAsync(uri.AbsoluteUri); }) { Margin = new Thickness(12) };
        _consoleStyleSession = consoleStyleSettings ?? new();
        _consoleStylePage = new(_consoleStyleSession, translations) { Margin = new Thickness(12) };
        _detailedSettingsPage = new(DetailedSettingsSession, translations) { Margin = new Thickness(12) };
        AppearanceSettingsResult = appearanceSettings
            ?? new AppearanceSettingsSession(initial, new HistoryViewPreferencesStore().Load()).Initial;
        _appearanceSettingsPage = new(AppearanceSettingsResult, translations, helpTranslateAsync,
            clearAvatarCacheAsync: clearAvatarCacheAsync, avatarHelpAsync: avatarHelpAsync,
            cancellationToken: appearanceCancellationToken) { Margin = new Thickness(12) };
        HotkeyBindings activeHotkeys = hotkeys ?? new(); HotkeysResult = activeHotkeys.Clone();
        DesktopFontSession? fontSession = fonts is null ? DesktopFonts.CreateSession() : null;
        FontPreferences fontBaseline = fonts ?? fontSession!.Preferences; FontsResult = fontBaseline;
        Exception? fontError = fontsLoadError ?? fontSession?.LoadError;
        ScriptsResult = scripts ?? new(ScriptConfiguration.Read(null), false, ReadOnlyMemory<byte>.Empty);
        Name = "DesktopSettings"; Title = translations.Translate("Settings");
        Width = 958; Height = 646; MinWidth = 740; MinHeight = 400; CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Every scoped page may defer closing while it drains owned work.
        // Preserve the owner request independently of optional Git configuration callbacks.
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        Window? closingOwner = null;
        Closing += (_, args) => { if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { closingOwner = Owner as Window; } };
        Closed += (_, _) =>
        {
            if (closingOwner is { } owner)
            { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
        TextBlock notice = new() { Name = "SettingsNotice", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) };
        Button configuration = new() { Name = "OpenGitConfiguration", Content = translations.Translate("Edit Git configuration") + "…", IsEnabled = canConfigureGit, HorizontalAlignment = HorizontalAlignment.Left };
        configuration.Click += (_, _) => { OpenGitConfiguration = true; Close(false); };
        Button tools = new() { Name = "OpenGitToolsConfiguration", Content = translations.Translate("Git configuration") + "…", IsEnabled = configureGitTools is not null, HorizontalAlignment = HorizontalAlignment.Left };
        Button advancedConfiguration = new() { Name = "OpenGitAdvancedConfiguration", Content = translations.Translate("GitConfigAdvancedSettingsPage", "$this", "Text", "Advanced") + "…",
            IsEnabled = configureGitAdvanced is not null, HorizontalAlignment = HorizontalAlignment.Left };
        bool configuringTools = false;
        tools.Click += async (_, _) => await OpenConfigurationAsync(configureGitTools);
        advancedConfiguration.Click += async (_, _) => await OpenConfigurationAsync(configureGitAdvanced);
        async Task OpenConfigurationAsync(Func<Window, Task>? configure)
        {
            if (configuringTools || configure is null || !IsVisible) { return; }
            configuringTools = true; tools.IsEnabled = advancedConfiguration.IsEnabled = false;
            try { await configure(this); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (IsVisible) { notice.Text = error.Message; } }
            finally
            {
                configuringTools = false;
                if (IsVisible) { tools.IsEnabled = configureGitTools is not null; advancedConfiguration.IsEnabled = configureGitAdvanced is not null; }
            }
        }
        StackPanel configurationFields = new() { Name = "SettingsGitConfiguration", Margin = new Thickness(12), Spacing = 10, Children = { tools, configuration } };

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
        HotkeysSettingsPage hotkeysPage = new(activeHotkeys, translations) { Margin = new Thickness(12) };
        AppearanceFontsSettingsPage fontsPage = new(fontBaseline, translations, fontError) { Margin = new Thickness(12), Height = 315 };
        ScriptsSettingsPage scriptsPage = new(ScriptsResult, translations) { Margin = new Thickness(12) };
        hotkeysPage.UpdateScripts(scriptsPage.Draft);
        scriptsPage.DraftChanged += draft => hotkeysPage.UpdateScripts(draft);
        string PageTitle(string category, string fallback) => translations.Translate(category, "$this", "Text", fallback);
        SettingsNavigationPage generalPage = new("General", PageTitle("GeneralSettingsPage", "General"), _generalSettingsPage, "GeneralSettings.png");
        SettingsNavigationPage appearancePage = new("Appearance", PageTitle("AppearanceSettingsPage", "Appearance"), _appearanceSettingsPage, "Appearance.png", Children:
        [
            new("Sorting", PageTitle("SortingSettingsPage", "Sorting"), sorting, "SortBy.png"),
            new("Colors", PageTitle("ColorsSettingsPage", "Colors"), _colorsSettingsPage, "Colors.png"),
            new("Fonts", PageTitle("AppearanceFontsSettingsPage", "Fonts"), fontsPage, "Font.png"),
            new("ConsoleStyle", PageTitle("ConsoleStyleSettingsPage", "Console"), _consoleStylePage, "Console.png")
        ]);
        SettingsNavigationPage[] extensionChildren =
        [
            generalPage, appearancePage,
            new("RevisionLinks", PageTitle("RevisionLinksSettingsPage", "Revision links"), _revisionLinksSettingsPage, "Link.png"),
            new("BuildServerIntegration", PageTitle("BuildServerIntegrationSettingsPage", "Build server integration"), _buildServerSettingsPage, "Integration.png"),
            new("Scripts", PageTitle("ScriptsSettingsPage", "Scripts"), scriptsPage, "Console.png"),
            new("Hotkeys", PageTitle("HotkeysSettingsPage", "Hotkeys"), hotkeysPage, "Hotkey.png"),
            new("Advanced", PageTitle("AdvancedSettingsPage", "Advanced"), _advancedSettingsPage, "AdvancedSettings.png", Children:
            [ new("Confirmations", PageTitle("ConfirmationsSettingsPage", "Confirmations"), _confirmationsSettingsPage, "BisectGood.png") ]),
            new("Detailed", PageTitle("DetailedSettingsPage", "Detailed"), _detailedSettingsPage, "Settings.png")
        ];
        if (gitEnvironment is not null)
        { extensionChildren = [.. extensionChildren, new("GitSsh", PageTitle("SshSettingsPage", "SSH"), gitEnvironment.SshPage, "Key.png")]; }
        List<SettingsNavigationPage> roots = [];
        List<SettingsNavigationPage> gitChildren = [];
        if (gitEnvironment is not null)
        {
            gitEnvironment.Checklist.Margin = gitEnvironment.Page.Margin = gitEnvironment.SshPage.Margin = new Thickness(12);
            roots.Add(new("Checklist", PageTitle("ChecklistSettingsPage", "Checklist"), gitEnvironment.Checklist,
                "git-extensions-logo-16px.png", IsInstantSave: true, Children: extensionChildren, NavigationTitle: "Git Extensions"));
            gitChildren.Add(new("GitPaths", PageTitle("GitSettingsPage", "Paths"), gitEnvironment.Page, "FolderOpen.png"));
        }
        else
        {
            roots.Add(new("GitExtensions", "Git Extensions", new TextBlock { Margin = new Thickness(12), Text = translations.Translate("Select one of the subnodes to view or edit the Git Extensions settings"), TextWrapping = TextWrapping.Wrap },
                "git-extensions-logo-16px.png", Children: extensionChildren));
        }
        if (configureGitTools is not null || canConfigureGit)
        { gitChildren.Add(new("Config", PageTitle("GitConfigSettingsPage", "Config"), configurationFields, "GeneralSettings.png")); }
        if (configureGitAdvanced is not null)
        {
            gitChildren.Add(new("GitAdvanced", PageTitle("GitConfigAdvancedSettingsPage", "Advanced"),
                new StackPanel { Name = "SettingsGitAdvancedConfiguration", Margin = new Thickness(12), Children = { advancedConfiguration } }, "AdvancedSettings.png"));
        }
        if (gitChildren.Count > 0)
        {
            roots.Add(new("Git", PageTitle("GitRootIntroductionPage", "Git Settings"), new TextBlock
                { Name = "GitRootIntroduction", Margin = new Thickness(12), Text = translations.Translate("GitRootIntroductionPage", "label1", "Text", "Select one of the subnodes to view or edit the Git settings"), TextWrapping = TextWrapping.Wrap },
                "GitLogo16.png", Children: gitChildren, NavigationTitle: "Git"));
        }
        roots.Add(new("Plugins", PageTitle("PluginRootIntroductionPage", "Plugins Settings"),
            new PluginRootIntroductionPage(translations), "plugin.png",
            NavigationTitle: translations.Translate("PluginsSettingsGroup", "$this", "Title", "Plugins")));
        Navigation = new(roots, translations);
        Button save = new() { Name = "SaveDesktopSettings", Content = translations.Translate("OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "CancelDesktopSettings", Content = translations.Translate("Cancel"), MinWidth = 75, IsCancel = true };
        Button apply = new() { Name = "ApplyDesktopSettings", Content = translations.Translate("Apply"), MinWidth = 75 };
        bool saving = false, closeWhenIdle = false, accepting = false, saved = false;
        bool detailedLoading = false;
        Task? detailedRead = null;
        void UpdateScopedPageBusy()
        {
            save.IsEnabled = apply.IsEnabled = !saving && !detailedLoading && gitEnvironment?.IsBusy != true && !_revisionLinksSettingsPage.IsBusy && !_buildServerSettingsPage.IsBusy;
            if (closeWhenIdle && !saving && !detailedLoading && !_revisionLinksSettingsPage.IsBusy && !_buildServerSettingsPage.IsBusy)
            { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(saved); } }); }
        }
        _revisionLinksSettingsPage.BusyChanged += UpdateScopedPageBusy;
        _buildServerSettingsPage.BusyChanged += UpdateScopedPageBusy;
        Closed += async (_, _) =>
        {
            _detailedCancellation.Cancel();
            try { await Task.WhenAll(_revisionLinksSettingsPage.BeginClose(), _buildServerSettingsPage.BeginClose()); }
            catch (OperationCanceledException) { }
            finally { _detailedCancellation.Dispose(); }
        };
        cancel.Click += (_, _) => Close(saved);
        save.Click += async (_, _) => await SaveAllAsync(closeOnSuccess: true);
        apply.Click += async (_, _) => await SaveAllAsync(closeOnSuccess: false);
        Task EnsureDetailedReadyAsync()
        {
            if (detailedRead is not null) { return detailedRead; }
            return detailedRead ??= ReadAsync();
            async Task ReadAsync()
            {
                detailedLoading = true; save.IsEnabled = apply.IsEnabled = false;
                try
                {
                    await DetailedSettingsSession.InitializeAsync(_detailedCancellation.Token);
                    await RevisionLinksSettingsSession.InitializeAsync(_detailedCancellation.Token);
                    await GitHubActionsSettingsSession.InitializeAsync(_detailedCancellation.Token);
                    _detailedSettingsPage.ShowLoadState();
                    _revisionLinksSettingsPage.ShowLoadState();
                    _buildServerSettingsPage.ShowLoadState();
                }
                catch (OperationCanceledException) when (_detailedCancellation.IsCancellationRequested) { }
                finally
                {
                    detailedLoading = false; save.IsEnabled = apply.IsEnabled = !saving && gitEnvironment?.IsBusy != true && !_revisionLinksSettingsPage.IsBusy && !_buildServerSettingsPage.IsBusy;
                    if (closeWhenIdle && !saving && gitEnvironment?.IsBusy != true)
                    { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(saved); } }); }
                }
            }
        }
        async Task<bool> SaveAllAsync(bool closeOnSuccess)
        {
            if (saving || _revisionLinksSettingsPage.IsBusy || _buildServerSettingsPage.IsBusy || gitEnvironment?.IsBusy == true) { return false; }
            saving = true; save.IsEnabled = apply.IsEnabled = false;
            string activeSavePage = "Detailed";
            try
            {
                await EnsureDetailedReadyAsync();
                if (closeWhenIdle) { return false; }
                _detailedSettingsPage.PrepareSave();
                activeSavePage = "RevisionLinks";
                await _revisionLinksSettingsPage.PrepareSaveAsync(_detailedCancellation.Token);
                activeSavePage = "BuildServerIntegration";
                _buildServerSettingsPage.PrepareSave();
                if (!_consoleStylePage.Validate(out string? consoleValidation))
                { Navigation.SelectPage("ConsoleStyle", true); notice.Text = consoleValidation; return false; }
                if (!_generalSettingsPage.Validate(out string? generalValidation))
                { Navigation.SelectPage("General"); _generalSettingsPage.FocusInvalidControl(); notice.Text = generalValidation; return false; }
                if (!_appearanceSettingsPage.Validate(out string? appearanceValidation))
                { Navigation.SelectPage("Appearance", true); notice.Text = appearanceValidation; return false; }
                foreach (TextBox input in new[] { branches, remotes })
                {
                    try { GitReferencePriorities.Validate(input.Text ?? ""); }
                    catch (ArgumentException error) { Navigation.SelectPage("Sorting", true); notice.Text = error.Message; input.Focus(); return false; }
                }
                if (!scriptsPage.Validate(out string? scriptValidation))
                { Navigation.SelectPage("Scripts", true); notice.Text = scriptValidation; return false; }
                if (!fontsPage.Validate(out string? fontValidation))
                { Navigation.SelectPage("Fonts", true); notice.Text = fontValidation; return false; }
                if (gitEnvironment is not null && !gitEnvironment.Validate(out string? environmentValidation))
                { Navigation.SelectPage(gitEnvironment.ValidationPage, true); notice.Text = environmentValidation; return false; }
                if (scriptsPage.IsDirty)
                {
                    try
                    {
                        ScriptsResult = saveScripts?.Invoke(ScriptsResult, scriptsPage.Draft)
                            ?? new(new(scriptsPage.Draft, ScriptConfigurationSource.Xml), ScriptsResult.Existed, ScriptsResult.OriginalBytes);
                        scriptsPage.AcceptSaved(ScriptsResult);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
                    { Navigation.SelectPage("Scripts", true); scriptsPage.ShowSaveError(error); notice.Text = translations.Translate("Unable to save scripts.") + " " + error.Message; return false; }
                }
                HotkeyBindings acceptedHotkeys = hotkeysPage.Draft.Clone();
                try { saveHotkeys?.Invoke(acceptedHotkeys.Clone()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                { Navigation.SelectPage("Hotkeys", true); notice.Text = translations.Translate("Unable to save hotkeys.") + " " + error.Message; return false; }
                try
                {
                    // An unreadable, untouched font profile is not replaced by unrelated Settings changes.
                    FontsResult = fontError is not null && fontsPage.Draft == fontBaseline ? fontBaseline
                        : saveFonts?.Invoke(fontBaseline, fontsPage.Draft) ?? fontsPage.Draft;
                    fontsPage.AcceptSaved(FontsResult);
                    fontBaseline = FontsResult;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { Navigation.SelectPage("Fonts", true); fontsPage.ShowSaveError(error); notice.Text = translations.Translate("Unable to save font preferences.") + " " + error.Message; return false; }
                if (gitEnvironment is not null && !gitEnvironment.TrySave())
                { Navigation.SelectPage(gitEnvironment.ValidationPage, true); return false; }
                GeneralSettingsResult = _generalSettingsPage.Draft;
                AppearanceSettingsResult = _appearanceSettingsPage.Draft;
                AdvancedSettingsValues advancedDraft = _advancedSettingsPage.Draft;
                Result = initial with
                {
                    Theme = AppearanceSettingsResult.Theme, Language = AppearanceSettingsResult.Language,
                    MergeGraphLanesHavingCommonParent = DetailedSettingsSession.Graph.MergeGraphLanesHavingCommonParent,
                    RenderGraphWithDiagonals = DetailedSettingsSession.Graph.RenderGraphWithDiagonals,
                    StraightenGraphDiagonals = DetailedSettingsSession.Graph.StraightenGraphDiagonals,
                    ShowRepoCurrentBranch = AppearanceSettingsResult.ShowRepoCurrentBranch,
                    ShowAuthorAvatarInCommitInfo = AppearanceSettingsResult.ShowAuthorAvatarInCommitInfo,
                    AvatarImageCacheDays = AppearanceSettingsResult.AvatarImageCacheDays,
                    AvatarProvider = AppearanceSettingsResult.AvatarProvider,
                    AvatarFallbackType = AppearanceSettingsResult.AvatarFallbackType,
                    CustomAvatarTemplate = AppearanceSettingsResult.CustomAvatarTemplate,
                    AutoNormaliseBranchName = advancedDraft.AutoNormaliseBranchName, AutoNormaliseSymbol = advancedDraft.AutoNormaliseSymbol, AlwaysShowAdvancedOptions = advancedDraft.AlwaysShowAdvancedOptions,
                    CheckForUpdates = advancedDraft.CheckForUpdates, CheckForReleaseCandidates = advancedDraft.CheckForReleaseCandidates,
                    DontShowHelpImages = advancedDraft.DontShowHelpImages,
                    UseBrowseForFileHistory = GeneralSettingsResult.UseBrowseForFileHistory,
                    MaxRevisionGraphCommits = GeneralSettingsResult.MaxRevisionGraphCommits,
                    RevisionGridQuickSearchTimeout = GeneralSettingsResult.RevisionGridQuickSearchTimeout,
                    CheckForUncommittedChangesInCheckoutBranch = GeneralSettingsResult.CheckForUncommittedChangesInCheckoutBranch,
                    StartWithRecentWorkingDir = GeneralSettingsResult.StartWithRecentWorkingDir,
                    ShowGitStatusInBrowseToolbar = GeneralSettingsResult.ShowGitStatusInBrowseToolbar,
                    ShowGitStatusForArtificialCommits = GeneralSettingsResult.ShowGitStatusForArtificialCommits,
                    ShowSubmoduleStatus = GeneralSettingsResult.ShowSubmoduleStatus,
                    ShowStashCount = GeneralSettingsResult.ShowStashCount,
                    ShowAheadBehindData = GeneralSettingsResult.ShowAheadBehindData,
                    ReferenceSorting = new((GitRefsSortBy)references.SelectedItem!, (GitRefsSortOrder)order.SelectedItem!),
                    PrioritizedBranchNames = branches.Text ?? "", PrioritizedRemoteNames = remotes.Text ?? ""
                };
                HistorySortResult = (GitLogSort)revisions.SelectedItem!;
                FileHistoryResult = FileHistoryResult with
                { FollowRenames = GeneralSettingsResult.FollowRenamesInFileHistory, ExactRenames = GeneralSettingsResult.FollowRenamesInFileHistoryExact };
                HotkeysResult = acceptedHotkeys;
                if (publishSettings is not null)
                {
                    Navigation.IsEnabled = false;
                    try { await publishSettings(this); }
                    catch (OperationCanceledException) when (closeWhenIdle) { return false; }
                    catch (Exception error)
                    {
                        notice.Text = translations.Translate("Unable to save settings.") + " " + error.Message + "\n"
                            + translations.Translate("Some settings may already have been saved. Cancel discards only unsaved changes.");
                        return false;
                    }
                }
                try
                {
                    activeSavePage = "Advanced";
                    AdvancedSettingsSession.Save(_advancedSettingsPage.Draft, receipt =>
                    {
                        _advancedSettingsPage.AcceptSaved(receipt);
                        Result = AdvancedSettingsValues.ApplyDesktop(Result, receipt.Values, receipt.Fields);
                        saved |= receipt.Written;
                        publishAdvanced?.Invoke(receipt);
                    }, _detailedCancellation.Token);
                    activeSavePage = "Colors";
                    ColorsSettingsPublication colorsSaved = publishColors is null
                        ? ColorsSettingsSession.Save(_colorsSettingsPage.Draft, cancellationToken: _detailedCancellation.Token)
                        : publishColors(_colorsSettingsPage.Draft, _detailedCancellation.Token);
                    _colorsSettingsPage.AcceptSaved(colorsSaved);
                    saved |= colorsSaved.Written;
                    activeSavePage = "Confirmations";
                    ConfirmationsSettingsSession.Save(_confirmationsSettingsPage.Draft, receipt =>
                    { _confirmationsSettingsPage.AcceptSaved(receipt); saved |= receipt.Written; }, _detailedCancellation.Token);
                    if (publishSettings is null) { DetailedSettingsSession.DesktopSaved(Result); }
                    activeSavePage = "Detailed";
                    await DetailedSettingsSession.SaveFilesAsync(_detailedCancellation.Token);
                    AcceptDetailedSaved();
                    activeSavePage = "RevisionLinks";
                    await RevisionLinksSettingsSession.SaveFilesAsync(_detailedCancellation.Token);
                    _revisionLinksSettingsPage.AcceptSaved();
                    activeSavePage = "BuildServerIntegration";
                    if (GitHubActionsSettingsSession.HasChanges)
                    { await GitHubActionsSettingsSession.SaveFilesAsync(_detailedCancellation.Token); _buildServerSettingsPage.AcceptSaved(); }
                    activeSavePage = "ConsoleStyle";
                    _consoleStylePage.AcceptSaved(_consoleStyleSession.Save(_consoleStylePage.Draft));
                }
                catch (OperationCanceledException) when (closeWhenIdle) { return false; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException
                    or ArgumentException or InvalidOperationException or JsonException)
                {
                    Navigation.SelectPage(activeSavePage, true);
                    notice.Text = translations.Translate("Unable to save settings.") + " " + error.Message + "\n"
                        + translations.Translate("Some settings may already have been saved. Cancel discards only unsaved changes.");
                    return false;
                }
                notice.Text = null;
                saved = true;
                if (closeOnSuccess && !closeWhenIdle) { accepting = true; try { Close(true); } finally { accepting = false; } }
                return !closeWhenIdle;
            }
            catch (OperationCanceledException) when (_detailedCancellation.IsCancellationRequested) { return false; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException
                or ArgumentException or InvalidOperationException or JsonException)
            { Navigation.SelectPage(activeSavePage, true); notice.Text = translations.Translate("Unable to save settings.") + " " + error.Message; return false; }
            finally
            {
                saving = false; Navigation.IsEnabled = true; save.IsEnabled = apply.IsEnabled = gitEnvironment?.IsBusy != true && !_revisionLinksSettingsPage.IsBusy && !_buildServerSettingsPage.IsBusy;
                if (closeWhenIdle && !detailedLoading && !_revisionLinksSettingsPage.IsBusy && !_buildServerSettingsPage.IsBusy && gitEnvironment?.IsBusy != true) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(saved); } }); }
            }
        }
        if (gitEnvironment is not null)
        {
            gitEnvironment.SaveAllRequested = () => SaveAllAsync(closeOnSuccess: false);
            gitEnvironment.RepairRequested = id => repairEnvironment is null ? Task.CompletedTask : repairEnvironment(id, this);
            gitEnvironment.BusyChanged += () => { save.IsEnabled = apply.IsEnabled = !saving && !detailedLoading && !gitEnvironment.IsBusy && !_revisionLinksSettingsPage.IsBusy && !_buildServerSettingsPage.IsBusy; };
            gitEnvironment.Settled += () =>
            {
                if (closeWhenIdle && !saving) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(saved); } }); }
            };
            Closed += (_, _) => gitEnvironment.Dispose();
        }
        async Task CloseAfterRevisionLinksAsync()
        {
            try { await Task.WhenAll(_revisionLinksSettingsPage.BeginClose(), _buildServerSettingsPage.BeginClose()); }
            catch (OperationCanceledException) { }
            if (!saving && !detailedLoading && gitEnvironment?.IsBusy != true && IsVisible) { Close(saved); }
        }
        Closing += (_, args) =>
        {
            if (!accepting && (saving || detailedLoading || _revisionLinksSettingsPage.IsBusy || _buildServerSettingsPage.IsBusy || gitEnvironment?.IsBusy == true))
            {
                args.Cancel = true; closeWhenIdle = true; gitEnvironment?.Cancel(); _detailedCancellation.Cancel();
                _ = CloseAfterRevisionLinksAsync();
                return;
            }
            // WinForms keeps DialogResult.OK after Apply/SaveAll, even when the
            // final dismissal is Cancel, the window close button or owner close.
            if (saved && !accepting && !saving && gitEnvironment?.IsBusy != true)
            {
                args.Cancel = true; accepting = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(true); } });
            }
        };
        DockPanel root = new() { LastChildFill = true, Margin = new Thickness(8) };
        TextBlock instantSaveNotice = new()
        {
            Name = "SettingsInstantSaveNotice", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Text = translations.Translate("FormSettings", "labelInstantSaveNotice", "Text", "Changes on the selected page will be saved instantly. \r\nTherefore the Cancel button does NOT revert any changes made."),
            IsVisible = Navigation.SelectedPage.IsInstantSave
        };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { save, cancel, apply } };
        Grid footerRow = new() { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(12, 6), ColumnSpacing = 12 };
        footerRow.Children.Add(instantSaveNotice); Grid.SetColumn(footer, 1); footerRow.Children.Add(footer);
        Button? reloadEnvironment = null;
        if (gitEnvironment is not null)
        {
            reloadEnvironment = new() { Name = "ReloadGitEnvironment", Content = translations.Translate("Reload"), MinWidth = 75 };
            reloadEnvironment.Click += (_, _) => gitEnvironment.Reload();
            gitEnvironment.BusyChanged += () => reloadEnvironment.IsEnabled = !gitEnvironment.IsBusy && !saving;
            footer.Children.Insert(0, reloadEnvironment);
        }
        Navigation.PageSelected += async (page, _) =>
        {
            Title = translations.Translate("Settings") + " - " + page.Title;
            instantSaveNotice.IsVisible = page.IsInstantSave;
            if (reloadEnvironment is not null) { reloadEnvironment.IsVisible = page.Id is "Checklist" or "GitPaths" or "GitSsh"; }
            if (IsVisible)
            {
                _lastSelectedPage = page.Id;
                if (page.Id == "Checklist" && gitEnvironment is not null) { await gitEnvironment.ScanAsync(); }
            }
        };
        string requestedPage = showChecklist ? "Checklist" : initialPage ?? _lastSelectedPage ?? (gitEnvironment is not null ? "Checklist" : "General");
        if (!Navigation.SelectPage(requestedPage)) { Navigation.SelectPage(gitEnvironment is not null ? "Checklist" : "General"); }
        DockPanel.SetDock(footerRow, Dock.Bottom); root.Children.Add(footerRow); DockPanel.SetDock(notice, Dock.Bottom); root.Children.Add(notice); root.Children.Add(Navigation); Content = root;
        Opened += async (_, _) =>
        {
            await EnsureDetailedReadyAsync();
            if (closeWhenIdle) { return; }
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                MaxHeight = Math.Max(260, screen.WorkingArea.Height / screen.Scaling - 40);
                MaxWidth = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 40);
                MinHeight = Math.Min(MinHeight, MaxHeight); MinWidth = Math.Min(MinWidth, MaxWidth);
                Height = Math.Min(Height, MaxHeight); Width = Math.Min(Width, MaxWidth);
            }
            _lastSelectedPage = Navigation.SelectedPage.Id;
            Navigation.GetLogicalDescendants().OfType<TreeView>().First().Focus();
            ModalWindowActivation.AfterMapping(this, () => IsVisible);
            if (Navigation.SelectedPage.Id == "Checklist" && gitEnvironment is not null) { await gitEnvironment.ScanAsync(); }
        };

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
