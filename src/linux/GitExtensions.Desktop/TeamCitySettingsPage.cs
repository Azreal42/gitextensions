using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Localization;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

/// <summary>Original TeamCity fields and explicit chooser/paste actions, sharing the CI scope and save draft.</summary>
internal sealed class TeamCitySettingsPage : BuildServerScopedSettingsPage
{
    private readonly Button _choose, _credentials, _paste;
    private readonly TextBlock _error = new() { Name = "TeamCitySettingsNotice", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _regex = new() { Name = "labelRegexError", Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private Window? _child;
    private Task _work = Task.CompletedTask;
    private bool _busy, _closing;
    private int _generation;
    internal bool IsBusy => _busy;
    internal Task Completion => _work;
    internal event Action? BusyChanged;
    internal Func<TeamCitySettings, IBuildServerCredentials, ITeamCityClient> ClientFactory { get; set; } = (settings, credentials) => new TeamCityApiClient(settings, credentials);
    internal Func<CancellationToken, Task<string?>>? ReadClipboard { get; set; }

    internal TeamCitySettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations, bool embedded = false)
        : base(session, translations, "TeamCitySettingsPage")
    {
        _paste = new() { Name = "lnkExtractDataFromBuildUrlCopiedInTheClipboard", Content = translations.Translate("Extract the data from the build url copied in the clipboard"),
            HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brushes.Blue, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0 };
        Add(_paste);
        Text(TeamCitySettings.ServerUrlKey, "TeamCityServerUrl", "TeamCity server URL");
        TextBox project = Text(TeamCitySettings.ProjectNameKey, "TeamCityProjectName", "Project name");
        _choose = new() { Name = "buttonProjectChooser", Content = "...", Width = 26, Height = 24, MinHeight = 0, Margin = new Thickness(3, 0, 0, 0) };
        Grid line = (Grid)project.Parent!; line.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(_choose, 2); line.Children.Add(_choose);
        Notice("Several names splitted by | char");
        Text(TeamCitySettings.BuildIdFilterKey, "TeamCityBuildIdFilter", "Build Id Filter");
        Notice("Regexp"); Add(_regex);
        Boolean(TeamCitySettings.LogAsGuestKey, "CheckBoxLogAsGuest", "Log as guest to display the build report");
        _credentials = new() { Name = "TeamCityCredentials", Content = translations.Translate("Credentials..."), HorizontalAlignment = HorizontalAlignment.Left, Height = 25, MinHeight = 0 };
        Add(_credentials); Add(_error);
        Notice("Credentials are saved in your Linux user profile. Choose Guest, username/password or bearer token explicitly; Windows integrated sign-in is unavailable.");
        _choose.Click += (_, _) => _ = StartAsync(ChooseAsync);
        _credentials.Click += (_, _) => _ = StartAsync(async (settings, token) => { _ = await EditCredentialsAsync(settings, token); });
        _paste.Click += (_, _) => _ = StartAsync(PasteAsync, allowEmptyServer: true);
        DraftChanged += UpdateActions;
        ScopeChanged += _ => { ++_generation; _request?.Cancel(); _child?.Close(); UpdateActions(); };
        if (embedded) { HideHeader(); }
        ShowLoadState();
    }

    internal override void ShowLoadState() { base.ShowLoadState(); if (_choose is not null) { UpdateActions(); } }
    private void UpdateActions()
    {
        bool editable = !_closing && !_busy && Session.Loaded && CurrentScope != GitDistributedSettingsScope.Effective;
        bool endpoint = !string.IsNullOrWhiteSpace(Session.Read(CurrentScope, TeamCitySettings.ServerUrlKey));
        _choose.IsEnabled = _credentials.IsEnabled = editable && endpoint; _paste.IsEnabled = editable;
        try { _ = Session.TeamCity(CurrentScope).CreateBuildIdRegex(); _regex.Text = ""; }
        catch (ArgumentException) { _regex.Text = Translations.Translate("The \"Build Id Filter\" regular expression is not valid and won't be saved!"); }
        _regex.IsVisible = !string.IsNullOrEmpty(_regex.Text);
    }
    private Task StartAsync(Func<TeamCitySettings, CancellationToken, Task> action, bool allowEmptyServer = false)
    {
        if (_busy || _closing || !Session.Loaded || CurrentScope == GitDistributedSettingsScope.Effective) { return Task.CompletedTask; }
        _busy = true; _error.Text = ""; SetFieldsEnabled(false); UpdateActions(); BusyChanged?.Invoke();
        return _work = RunAsync();
        async Task RunAsync()
        {
            using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _request = request;
            try
            {
                TeamCitySettings settings = Session.TeamCity(CurrentScope);
                if (!allowEmptyServer) { _ = settings.ServerUri; }
                await action(settings, request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error) when (error is TeamCityException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { if (!_closing && !request.IsCancellationRequested) { _error.Text = Translations.Translate(error is TeamCityException ? error.Message : "Could not read TeamCity. Check the server URL, project, filter and credentials."); } }
            finally { if (ReferenceEquals(_request, request)) { _request = null; } _busy = false; SetFieldsEnabled(true); UpdateActions(); BusyChanged?.Invoke(); }
        }
    }
    private async Task<BuildServerCredentials?> EditCredentialsAsync(TeamCitySettings settings, CancellationToken token)
    {
        if (this.GetVisualRoot() is not Window owner || !owner.IsVisible) { return null; }
        BuildServerCredentials initial = Session.CredentialDraft(settings.ServerUri);
        BuildServerCredentialsDialog dialog = new(settings.ServerUri.AbsoluteUri, initial, Translations); _child = dialog;
        int generation = _generation;
        using CancellationTokenRegistration cancel = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
        try { await dialog.ShowDialog<BuildServerCredentials?>(owner); }
        finally { if (ReferenceEquals(_child, dialog)) { _child = null; } }
        if (_closing || token.IsCancellationRequested || generation != _generation || dialog.Result is null) { return null; }
        Session.StageCredentials(settings.ServerUri, dialog.Result); return dialog.Result;
    }
    private async Task<BuildServerCredentials?> GetCredentialsAsync(TeamCitySettings settings, CancellationToken token)
        => Session.CredentialsFor(settings) ?? await EditCredentialsAsync(settings, token);
    private async Task ChooseAsync(TeamCitySettings settings, CancellationToken token)
    {
        if (this.GetVisualRoot() is not Window owner) { return; }
        BuildServerCredentials? credentials = await GetCredentialsAsync(settings, token); if (credentials is null || token.IsCancellationRequested) { return; }
        await using ITeamCityClient client = ClientFactory(new TeamCitySettings { ServerUrl = settings.ServerUrl, LogAsGuest = settings.LogAsGuest }, credentials);
        TeamCityBuildChooserDialog dialog = new(client, new(settings.ProjectName, settings.BuildIdFilter), Translations); _child = dialog;
        int generation = _generation;
        using CancellationTokenRegistration cancel = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
        try { await dialog.ShowDialog<TeamCityBuildSelection?>(owner); await dialog.BeginClose(); }
        finally { if (ReferenceEquals(_child, dialog)) { _child = null; } }
        if (_closing || token.IsCancellationRequested || generation != _generation || dialog.Result is not { } result) { return; }
        Session.Set(CurrentScope, TeamCitySettings.ProjectNameKey, result.ProjectId);
        Session.Set(CurrentScope, TeamCitySettings.BuildIdFilterKey, result.BuildTypeId); ShowLoadState();
    }
    private async Task PasteAsync(TeamCitySettings settings, CancellationToken token)
    {
        string? text = ReadClipboard is { } read ? await read(token)
            : TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await ClipboardExtensions.TryGetTextAsync(clipboard).WaitAsync(token) : null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        { throw new ArgumentException("The clipboard does not contain a TeamCity build URL."); }
        string? id = HttpUtility.ParseQueryString(uri.Query)["buildTypeId"];
        if (string.IsNullOrWhiteSpace(id)) { throw new ArgumentException("The clipboard does not contain a TeamCity build URL."); }
        // Keep a TeamCity instance's base path when pasting its build page.
        Uri endpoint = new(uri, ".");
        TeamCitySettings candidate = new() { ServerUrl = endpoint.AbsoluteUri, ProjectName = "", LogAsGuest = settings.LogAsGuest };
        candidate.Validate(requireProjects: false);
        BuildServerCredentials? credentials = await GetCredentialsAsync(candidate, token); if (credentials is null || token.IsCancellationRequested) { return; }
        int generation = _generation;
        await using ITeamCityClient client = ClientFactory(candidate, credentials);
        TeamCityBuildType build = await client.GetBuildTypeAsync(id, token);
        if (_closing || token.IsCancellationRequested || generation != _generation) { return; }
        Session.Set(CurrentScope, TeamCitySettings.ServerUrlKey, endpoint.AbsoluteUri);
        Session.Set(CurrentScope, TeamCitySettings.ProjectNameKey, build.ProjectId);
        Session.Set(CurrentScope, TeamCitySettings.BuildIdFilterKey, build.Id); ShowLoadState();
    }
    internal Task BeginClose()
    {
        if (!_closing) { _closing = true; ++_generation; _lifetime.Cancel(); _child?.Close(); UpdateActions(); }
        return _work;
    }
}
