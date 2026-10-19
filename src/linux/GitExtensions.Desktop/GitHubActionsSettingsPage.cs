using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using Avalonia.Controls.Templates;

namespace GitExtensions.Linux.Desktop;

internal abstract class BuildServerScopedSettingsPage : UserControl
{
    protected readonly GitHubActionsSettingsSession Session;
    protected readonly TranslationCatalog Translations;
    private readonly StackPanel _fields = new() { Spacing = 5 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, Control> _editors = [];
    private readonly Dictionary<GitDistributedSettingsScope, RadioButton> _scopes = [];
    private readonly WrapPanel _header;
    internal event Action<GitDistributedSettingsScope>? ScopeChanged;
    internal event Action? DraftChanged;
    private bool _loading;
    private GitDistributedSettingsScope _scope;
    protected GitDistributedSettingsScope CurrentScope => _scope;

    protected BuildServerScopedSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations, string name)
    {
        Session = session; Translations = translations; Name = name; _scope = session.InitialScope;
        WrapPanel scopes = new() { Orientation = Orientation.Horizontal };
        _header = scopes;
        foreach ((GitDistributedSettingsScope scope, string text) in new[]
        { (GitDistributedSettingsScope.Effective, "Effective"), (GitDistributedSettingsScope.Local, "Local for current repository"),
          (GitDistributedSettingsScope.Distributed, "Distributed with current repository"), (GitDistributedSettingsScope.Global, "Global for all repositories") })
        {
            RadioButton choice = new() { Name = name + scope, GroupName = name + "Scope", Content = translations.Translate(text), Margin = new Thickness(0, 0, 10, 5) };
            ReferenceDialogControls.Compact(choice); _scopes.Add(scope, choice); scopes.Children.Add(choice);
            choice.IsCheckedChanged += (_, _) => { if (!_loading && choice.IsChecked == true) { SetScope(scope); } };
        }
        Content = new StackPanel { Spacing = 8, Children = { scopes, _fields, _notice } };
    }
    protected TextBox Text(string key, string name, string caption, bool password = false)
    {
        TextBox box = new() { Name = name, Height = 24, MinHeight = 24, PasswordChar = password ? '●' : '\0' };
        _editors.Add(key, box);
        box.PropertyChanged += (_, args) => { if (!_loading && args.Property == TextBox.TextProperty) { Edit(key, string.IsNullOrEmpty(box.Text) ? null : box.Text); } };
        Grid line = new() { ColumnDefinitions = new ColumnDefinitions("155,*") };
        line.Children.Add(new TextBlock { Text = Translations.Translate(caption), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(box, 1); line.Children.Add(box); _fields.Children.Add(line); return box;
    }
    protected void LoadText(TextBox box, string? value)
    { bool loading = _loading; _loading = true; try { box.Text = value; } finally { _loading = loading; } }
    protected void Boolean(string key, string name, string caption)
    {
        CheckBox box = new() { Name = name, Content = Translations.Translate(caption), IsThreeState = true };
        ReferenceDialogControls.Compact(box); _editors.Add(key, box); _fields.Children.Add(box);
        box.IsCheckedChanged += (_, _) => { if (!_loading) { Edit(key, box.IsChecked is bool value ? value ? "true" : "false" : null); } };
    }
    protected void Provider()
    {
        ComboBox provider = new() { Name = "BuildServerProvider", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
        provider.ItemTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock { Text = string.IsNullOrEmpty(value) ? Translations.Translate("None") : value });
        _editors.Add(GitHubActionsSettings.ProviderKey, provider);
        provider.SelectionChanged += (_, _) => { if (!_loading) { Edit(GitHubActionsSettings.ProviderKey, provider.SelectedItem as string is { Length: > 0 } value ? value : null); } };
        _fields.Children.Add(new TextBlock { Text = Translations.Translate("Build server") }); _fields.Children.Add(provider);
    }
    protected void Notice(string text) => _fields.Children.Add(new TextBlock { Text = Translations.Translate(text), TextWrapping = TextWrapping.Wrap });
    protected void Add(Control control) => _fields.Children.Add(control);
    protected void SetFieldsEnabled(bool enabled) => _fields.IsEnabled = enabled;
    protected void HideHeader() { _header.IsVisible = false; _notice.IsVisible = false; }
    internal void SetScope(GitDistributedSettingsScope scope) { _scope = scope; ShowLoadState(); ScopeChanged?.Invoke(scope); }
    private void Edit(string key, string? value)
    { if (Session.Loaded && _scope != GitDistributedSettingsScope.Effective) { Session.Set(_scope, key, value); DraftChanged?.Invoke(); } }
    internal void PrepareSave() { /* Drafts are recorded on actual edits; untouched unknown values are retained. */ }
    internal void AcceptSaved() => ShowLoadState();
    internal virtual void ShowLoadState()
    {
        _loading = true;
        try
        {
            foreach ((GitDistributedSettingsScope scope, RadioButton choice) in _scopes)
            {
                choice.IsVisible = scope == GitDistributedSettingsScope.Global || Session.HasRepository;
                choice.IsEnabled = scope != GitDistributedSettingsScope.Distributed || Session.HasDistributed;
                choice.IsChecked = scope == _scope; ToolTip.SetTip(choice, Session.ScopePath(scope));
            }
            foreach ((string key, Control editor) in _editors)
            {
                string? value = Session.Read(_scope, key);
                editor.IsEnabled = Session.Loaded && _scope != GitDistributedSettingsScope.Effective;
                if (editor is TextBox box)
                {
                    (string owner, string repository) = Session.DetectedRepository;
                    box.Text = key == GitHubActionsSettings.OwnerKey && string.IsNullOrWhiteSpace(value) ? owner
                        : key == GitHubActionsSettings.RepositoryKey && string.IsNullOrWhiteSpace(value) ? repository
                        : key == TeamCitySettings.ProjectNameKey && value is null ? Session.DefaultProjectName
                        : value ?? (key == GitHubActionsSettings.ApiUrlKey ? Session.Read(GitDistributedSettingsScope.Effective, key) ?? GitHubActionsSettings.DefaultApiUrl : "");
                }
                if (editor is CheckBox check) { check.IsThreeState = _scope != GitDistributedSettingsScope.Effective; check.IsChecked = GitHubActionsSettings.Boolean(value) ?? (_scope == GitDistributedSettingsScope.Effective ? false : null); }
                if (editor is ComboBox combo)
                { combo.ItemsSource = new[] { "", GitHubActionsSettings.ProviderName, TeamCitySettings.ProviderName, GitLabSettings.ProviderName }.Concat(value is { Length: > 0 } && value != GitHubActionsSettings.ProviderName && value != TeamCitySettings.ProviderName && value != GitLabSettings.ProviderName ? [value] : []).ToArray(); combo.SelectedItem = value ?? ""; }
            }
            _notice.Text = Session.LoadError is not null ? Translations.Translate("CI settings could not be read. No settings have been changed.")
                : _scope == GitDistributedSettingsScope.Effective ? Translations.Translate("Effective settings are read-only. Select Local, Distributed or Global to edit.") : Session.DetectionError ?? "";
        }
        finally { _loading = false; }
    }
}

internal sealed class GitHubActionsSettingsPage : BuildServerScopedSettingsPage
{
    internal GitHubActionsSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations, Action? createToken = null, bool embedded = false) : base(session, translations, "GitHubActionsSettingsPage")
    {
        Text(GitHubActionsSettings.ApiUrlKey, "GitHubActionsApiUrl", "API URL");
        Text(GitHubActionsSettings.OwnerKey, "GitHubActionsOwner", "Owner"); Text(GitHubActionsSettings.RepositoryKey, "GitHubActionsRepository", "Repository");
        Text(GitHubActionsSettings.TokenKey, "GitHubActionsApiToken", "API token", password: true);
        Button token = new() { Name = "CreatePersonalAccessToken", Content = translations.Translate("Create personal access token"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = createToken is not null };
        token.Click += (_, _) => createToken?.Invoke(); Add(token);
        Notice("Public repositories can be read anonymously. Private repositories require Actions read permission. Tokens require HTTPS and are stored in the selected settings file.");
        if (embedded) { HideHeader(); }
        ShowLoadState();
    }
}

internal sealed class BuildServerIntegrationSettingsPage : BuildServerScopedSettingsPage
{
    internal GitHubActionsSettingsPage GitHubPage { get; }
    internal TeamCitySettingsPage TeamCityPage { get; }
    internal GitLabSettingsPage GitLabPage { get; }
    internal bool IsBusy => TeamCityPage.IsBusy || GitLabPage.IsBusy;
    internal event Action? BusyChanged;
    internal Task BeginClose() => Task.WhenAll(TeamCityPage.BeginClose(), GitLabPage.BeginClose());
    internal BuildServerIntegrationSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations, Action? createToken = null, Action<Uri>? openBuildServerUri = null) : base(session, translations, "BuildServerIntegrationSettingsPage")
    {
        Boolean(GitHubActionsSettings.EnableKey, "EnableIntegration", "Enable build server integration");
        Boolean(GitHubActionsSettings.ShowPageKey, "ShowBuildResultPage", "Show build result page"); Provider();
        GitHubPage = new(session, translations, createToken, embedded: true); Add(GitHubPage);
        TeamCityPage = new(session, translations, embedded: true); Add(TeamCityPage);
        GitLabPage = new(session, translations, openBuildServerUri, embedded: true); Add(GitLabPage);
        GitLabPage.BusyChanged += () => BusyChanged?.Invoke();
        TeamCityPage.BusyChanged += () => { SetFieldsEnabled(!TeamCityPage.IsBusy); BusyChanged?.Invoke(); };
        ScopeChanged += GitHubPage.SetScope; ScopeChanged += TeamCityPage.SetScope; ScopeChanged += GitLabPage.SetScope;
        DraftChanged += UpdateProvider;
        Notice("Build pages open only when you explicitly choose Show build page."); ShowLoadState();
    }
    private void UpdateProvider() { if (GitHubPage is not null) { GitHubPage.IsVisible = Session.Read(CurrentScope, GitHubActionsSettings.ProviderKey) == GitHubActionsSettings.ProviderName; }
        if (TeamCityPage is not null) { TeamCityPage.IsVisible = Session.Read(CurrentScope, GitHubActionsSettings.ProviderKey) == TeamCitySettings.ProviderName; }
        if (GitLabPage is not null) { bool active = Session.Read(CurrentScope, GitHubActionsSettings.ProviderKey) == GitLabSettings.ProviderName; GitLabPage.IsVisible = active; GitLabPage.SetProviderActive(active); } }
    internal override void ShowLoadState() { base.ShowLoadState(); UpdateProvider(); GitHubPage?.ShowLoadState(); TeamCityPage?.ShowLoadState(); GitLabPage?.ShowLoadState(); }
}
