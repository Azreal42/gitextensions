using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original Azure/TFS settings table; only the Extract link starts a clipboard/network operation.</summary>
internal sealed class AzureDevOpsSettingsPage : BuildServerScopedSettingsPage
{
    internal const string ClipboardFailure = "The clipboard doesn't contain a valid build url.\n\nPlease copy the url of the build into the clipboard before retrying.\n(Should contain at least the \"buildId\" parameter)";
    internal const string LookupFailure = "Error while trying to retrieve build definition information from url.\n\nPlease ensure that the url is valid and that the API token has access to build and project information.";
    internal const string NoTokenInformation = "Unable to retrieve build definition information without API token. Field will be left blank.";
    internal const string TokenProjectMismatch = "Enter the Azure DevOps PAT again for this project URL. A saved PAT cannot be reused for another project, origin or base path.";
    internal const string BrowserFailure = "The token management page could not be opened in the browser.";
    private readonly TextBox _project, _filter, _token;
    private readonly Button _extract, _manage;
    private readonly TextBlock _regexError;
    private readonly Action<Uri>? _openBrowser;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _work = [];
    private readonly HashSet<GitDistributedSettingsScope> _projectEdited = [], _activated = [];
    private readonly Dictionary<GitDistributedSettingsScope, string> _defaults = [];
    private CancellationTokenSource? _current;
    private Task? _close;
    private long _generation;
    private bool _active, _closing, _rendering;

    internal bool IsBusy => _work.Count > 0;
    internal Task Completion => Task.WhenAll(_work.ToArray());
    internal event Action? BusyChanged;
    internal Func<AzureDevOpsSettings, IAzureDevOpsClient> ClientFactory { get; set; } = settings => new AzureDevOpsApiClient(settings);
    internal Func<CancellationToken, Task<string?>>? ReadClipboardAsync { get; set; }

    internal AzureDevOpsSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations,
        Action<Uri>? openBrowser = null, bool embedded = false) : base(session, translations, "AzureDevOpsSettingsPage")
    {
        _openBrowser = openBrowser;
        _project = Text(AzureDevOpsSettings.ProjectUrlKey, "TfsServer", "Project Url");
        _filter = Text(AzureDevOpsSettings.BuildDefinitionFilterKey, "TfsBuildDefinitionNameFilter", "Build definition name\n(all existing if left empty)");
        _token = Text(AzureDevOpsSettings.ApiTokenKey, "RestApiToken", "Rest Api Token", password: true); _token.RevealPassword = false;
        _extract = SettingsLink("ExtractLink", T("ExtractLink", "Extract data from a build result url copied in the clipboard"), 1);
        _manage = SettingsLink("TokenManagementLink", T("TokenManagementLink", "Go to token management page"), 5);
        _regexError = new()
        {
            Name = "labelRegexError", Text = T("labelRegexError", "The 'Build definition name' regular expression is not valid and won't be saved!"),
            Margin = new Thickness(3, 5), TextWrapping = TextWrapping.Wrap, IsVisible = false, Foreground = Brushes.Red
        };
        Grid table = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new("Auto,*"),
            RowDefinitions = new("Auto,Auto,Auto,20,Auto,Auto,Auto,20,Auto,Auto,Auto") };
        MoveEditor(_project, 1, "label13", "Project Url", 2);
        MoveEditor(_filter, 4, "label3", "Build definition name\n(all existing if left empty)", 3);
        MoveEditor(_token, 8, "RestApiTokenLabel", "Rest Api Token", 4);
        Place(_extract, 0); Place(_manage, 9); Place(_regexError, 6);
        Help(2, "label1", "Examples:\n - https://dev.azure.com/yourorganization/projectname/\n - https://yourhost:8080/tfs/collectionname/projectname/\n - https://yourorganization.visualstudio.com/projectname/", bottom: 5);
        Help(5, "labelBuildIdFilter", "If needed, use the '*' wildcard or enter a Regular Expression");
        Help(10, "label4", "You need to create a token with the following scopes:\n - 'Build (read)'\n - 'Project and team (read)'");
        Add(table);
        _extract.Click += (_, _) => { if (Editable && IsEffectivelyVisible) { StartExtraction(); } };
        _manage.Click += (_, _) => StartTokenManagement();
        ScopeChanged += _ => { InvalidateRead(); UpdateActions(); };
        DetachedFromVisualTree += (_, _) => InvalidateRead();
        if (embedded) { HideHeader(); }
        ShowLoadState();

        void Place(Control control, int row) { Grid.SetColumn(control, 1); Grid.SetRow(control, row); table.Children.Add(control); }
        void Help(int row, string name, string caption, double bottom = 0)
        { Place(new TextBlock { Name = name, Text = T(name, caption), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, bottom) }, row); }
        void MoveEditor(TextBox box, int row, string labelName, string caption, int tab)
        {
            Grid line = (Grid)box.Parent!; ((Panel)line.Parent!).Children.Remove(line);
            TextBlock label = line.Children.OfType<TextBlock>().Single(); line.Children.Remove(label); line.Children.Remove(box);
            label.Name = labelName; label.Text = T(labelName, caption); label.Margin = new Thickness(3, 0);
            label.TextWrapping = TextWrapping.Wrap; label.HorizontalAlignment = HorizontalAlignment.Left;
            box.Margin = new Thickness(3, 2); box.Height = box.MinHeight = DesktopFontMetrics.Height(DesktopFonts.For(DesktopFontRole.Application), 23, 6);
            box.TabIndex = tab; box.AcceptsReturn = box.AcceptsTab = false;
            box.AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; } }, RoutingStrategies.Tunnel);
            Grid.SetRow(label, row); table.Children.Add(label); Grid.SetColumn(box, 1); Grid.SetRow(box, row); table.Children.Add(box);
            box.PropertyChanged += (_, args) =>
            {
                if (_rendering || args.Property != TextBox.TextProperty) { return; }
                if (ReferenceEquals(box, _project)) { _projectEdited.Add(CurrentScope); }
                if (ReferenceEquals(box, _token))
                { Session.BindAzureDevOpsTokenToDisplayedProject(CurrentScope, box.Text, _project.Text ?? ""); }
                InvalidateRead(); UpdateActions();
            };
        }
    }

    private bool Editable => !_closing && _active && Session.Loaded && CurrentScope != GitDistributedSettingsScope.Effective;
    internal void SetProviderActive(bool active, bool userInitiated = false)
    {
        if (_closing) { return; }
        if (_active != active) { _active = active; InvalidateRead(); }
        if (active && userInitiated && Session.Loaded && CurrentScope != GitDistributedSettingsScope.Effective) { _activated.Add(CurrentScope); }
        UpdateActions();
    }
    internal override void ShowLoadState()
    {
        InvalidateRead(); _rendering = true;
        try
        {
            base.ShowLoadState();
            if (_project is not null && Session.Loaded && !_projectEdited.Contains(CurrentScope)
                && string.IsNullOrWhiteSpace(Session.Read(CurrentScope, AzureDevOpsSettings.ProjectUrlKey)))
            {
                string suggestion = Session.DetectedAzureDevOpsProject(); _defaults[CurrentScope] = suggestion;
                LoadText(_project, suggestion);
            }
        }
        finally { _rendering = false; }
        if (_extract is not null) { UpdateActions(); }
    }
    internal override void PrepareSave()
    {
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        { Session.PrepareAzureDevOpsProject(scope, _defaults.GetValueOrDefault(scope, Session.DetectedAzureDevOpsProject()), _projectEdited.Contains(scope), _activated.Contains(scope)); }
    }
    private void UpdateActions()
    {
        _extract.IsEnabled = Editable;
        _manage.IsEnabled = !_closing && Session.Loaded && _openBrowser is not null && AzureDevOpsProjectUrls.TryGetTokenManagementUri(_project.Text, out _);
        try { _ = new AzureDevOpsSettings { BuildDefinitionFilter = _filter.Text ?? "" }.CreateBuildDefinitionRegex(); _regexError.IsVisible = false; }
        catch (ArgumentException) { _regexError.IsVisible = true; }
    }
    private void InvalidateRead() { ++_generation; _current?.Cancel(); }
    private void Track(Task operation) { _work.Add(operation); BusyChanged?.Invoke(); _ = FinishAsync(operation); }
    private void StartTokenManagement()
    {
        if (_closing || !_active || _openBrowser is null || !IsEffectivelyVisible
            || !AzureDevOpsProjectUrls.TryGetTokenManagementUri(_project.Text, out Uri? uri)) { return; }
        Action<Uri> launch = _openBrowser; Uri target = uri;
        InvalidateRead(); long generation = _generation; GitDistributedSettingsScope scope = CurrentScope;
        string project = _project.Text ?? "";
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _current = request;
        Track(OpenAsync());
        bool Current() => !_closing && _active && !request.IsCancellationRequested && generation == _generation && scope == CurrentScope
            && IsEffectivelyVisible && TopLevel.GetTopLevel(this)?.IsVisible == true && (_project.Text ?? "") == project;
        async Task OpenAsync()
        {
            await Task.Yield();
            try { if (Current()) { launch(target); } }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (Current() && TopLevel.GetTopLevel(this) is Window owner)
                {
                    try { await Dialogs.ShowMessageAsync(owner, Translations.Translate("Error"), Translations.Translate(BrowserFailure), false, request.Token); }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                }
            }
            finally { if (ReferenceEquals(_current, request)) { _current = null; } request.Dispose(); }
        }
    }
    private void StartExtraction()
    {
        InvalidateRead(); long generation = _generation; GitDistributedSettingsScope scope = CurrentScope;
        string project = _project.Text ?? "", filter = _filter.Text ?? "", token = _token.Text ?? "";
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _current = request;
        Track(ExtractAsync());
        bool Current() => !_closing && _active && !request.IsCancellationRequested && generation == _generation && scope == CurrentScope
            && IsEffectivelyVisible && TopLevel.GetTopLevel(this)?.IsVisible == true
            && (_project.Text ?? "") == project && (_filter.Text ?? "") == filter && (_token.Text ?? "") == token;
        async Task NoticeAsync(string key, string text, bool warning = false)
        {
            if (Current() && TopLevel.GetTopLevel(this) is Window owner)
            {
                try { await Dialogs.ShowMessageAsync(owner, T("_failToExtractDataFromClipboardCaption", "Could not extract data"), T(key, text), warning, request.Token); }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            }
        }
        void ApplyExtraction(string url, string definition)
        {
            _rendering = true;
            try
            {
                _projectEdited.Add(scope);
                Session.Set(scope, AzureDevOpsSettings.ProjectUrlKey, url); Session.Set(scope, AzureDevOpsSettings.BuildDefinitionFilterKey, definition);
                LoadText(_project, url); LoadText(_filter, definition); project = url; filter = definition;
            }
            finally { _rendering = false; }
            UpdateActions();
        }
        async Task ExtractAsync()
        {
            await Task.Yield();
            try
            {
                if (!Current()) { return; }
                string? value = ReadClipboardAsync is { } read ? await read(request.Token)
                    : TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await ClipboardExtensions.TryGetTextAsync(clipboard) : null;
                if (!Current()) { return; }
                if (!AzureDevOpsProjectUrls.TryParseBuildUrl(value, out AzureDevOpsBuildUrl? build))
                { await NoticeAsync("_failToExtractDataFromClipboardMessage", ClipboardFailure); return; }
                string definition;
                if (string.IsNullOrWhiteSpace(token))
                { definition = ""; await NoticeAsync("_infoNoApiTokenMessage", NoTokenInformation, warning: true); }
                else
                {
                    if (!Session.CanUseAzureDevOpsToken(scope, token, build.ProjectUri.AbsoluteUri))
                    {
                        ApplyExtraction(build.ProjectUri.AbsoluteUri.TrimEnd('/'), "");
                        await NoticeAsync("TokenProjectMismatch", TokenProjectMismatch); return;
                    }
                    await using IAzureDevOpsClient client = ClientFactory(new()
                    { Provider = AzureDevOpsSettings.ProviderName, ProjectUrl = build.ProjectUri.AbsoluteUri.TrimEnd('/'), ApiToken = token });
                    definition = await client.GetBuildDefinitionNameFromIdAsync(build.BuildId, request.Token) ?? "";
                }
                if (!Current()) { return; }
                ApplyExtraction(build.ProjectUri.AbsoluteUri.TrimEnd('/'), definition);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception) { if (Current()) { await NoticeAsync("_failToLoadBuildDefinitionInfoMessage", LookupFailure); } }
            finally
            { if (ReferenceEquals(_current, request)) { _current = null; } request.Dispose(); }
        }
    }
    private async Task FinishAsync(Task operation)
    { try { await operation; } finally { _work.Remove(operation); BusyChanged?.Invoke(); } }
    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closing = true; InvalidateRead(); _lifetime.Cancel(); UpdateActions();
        return _close = CloseCoreAsync();
    }
    private async Task CloseCoreAsync() { try { await Completion; } finally { _lifetime.Dispose(); } }
    private string T(string item, string source) => Translations.Translate("SettingsUserControl", item, "Text", source);
}
