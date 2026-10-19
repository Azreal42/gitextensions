using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original two-tab hosted Fork/Clone workflow. REST and native Git remain separate operations.</summary>
public sealed partial class ForkCloneDialog : StartupRepositoryDialog
{
    private const string Category = "ForkAndCloneForm";
    private readonly IGitHubForkCloneClient _client;
    private readonly bool _authenticated;
    private readonly Action<Uri> _openUrl;
    private readonly ListBox _mine = new() { Name = "myReposLV", SelectionMode = SelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _results = new() { Name = "searchResultsLV", SelectionMode = SelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TabControl _tabs = new() { Name = "tabControl" };
    private readonly TextBox _query = new() { Name = "searchTB", Height = 24 };
    private readonly TextBox _description = new() { Name = "searchResultItemDescription", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _myHelp = new() { Name = "helpTextLbl", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _searchNotice = new() { Name = "SearchRepositoryNotice", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _destination = new() { Name = "destinationTB", Height = 24, MaxHeight = 80 };
    private readonly TextBox _subdirectory = new() { Name = "createDirTB", Height = 24 };
    private readonly TextBox _upstream = new() { Name = "addUpstreamRemoteAsCB", Height = 24 };
    private readonly ComboBox _upstreamChoices = new() { Name = "UpstreamRemoteChoices", Width = 28, Height = 24 };
    private readonly ComboBox _protocol = new() { Name = "ProtocolDropdownList", ItemsSource = new[] { "SSH", "HTTPS" }, SelectedIndex = 0, Height = 24 };
    private readonly NumericUpDown _depth = new() { Name = "depthUpDown", Minimum = 0, Maximum = 999, Value = 0, Increment = 1, FormatString = "0", Height = 24, Width = 100 };
    private readonly TextBlock _cloneInfo = new() { Name = "cloneInfoText", TextWrapping = TextWrapping.Wrap };
    private readonly Button _clone, _fork, _search, _getUser;
    private GitHubHostedRepository? _selected, _parent;
    private long _selectionVersion;
    private bool _updating, _closing;
    private readonly HashSet<GitHubRepositoryId> _forkAttempts = [];
    private Window? _closingOwner;
    private Task? _initialization;
    private Task _completion = Task.CompletedTask;

    public GitProcessDialogResult? LastProcess { get; private set; }
    public string? CloneWarning { get; private set; }
    public bool ForkAccepted { get; private set; }
    public bool ForkOutcomeUnknown { get; private set; }
    internal Task Completion => _completion;

    public ForkCloneDialog(IGitHubForkCloneClient client, bool authenticated, string destination = "", Action<Uri>? openUrl = null)
        : base("Remote repository fork and clone")
    {
        ArgumentNullException.ThrowIfNull(client); _client = client; _authenticated = authenticated;
        _openUrl = openUrl ?? (uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
            { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } }));
        Name = "ForkAndCloneForm"; Title = "GitHub: " + Tr("$this", "Remote repository fork and clone");
        Width = 744; Height = 552; MinWidth = 650; MinHeight = 500; SizeToContent = SizeToContent.Manual; CanResize = true;
        ShowInTaskbar = false; CanMinimize = false; ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        using (Stream icon = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/git-extensions-logo.ico"))) { Icon = new WindowIcon(icon); }
        _myHelp.Text = authenticated
            ? Tr("helpTextLbl", "If you want to fork a repository owned by somebody else, go to the Search for repositories tab.")
            : T("Choose GitHub authentication in Git hosts → GitHub settings to load My repositories or fork. Public search and clone are available as Guest.");
        _mine.ItemTemplate = RepositoryTemplate(false); _results.ItemTemplate = RepositoryTemplate(true);
        Grid mine = new() { ColumnDefinitions = new("7*,3*"), ColumnSpacing = 6, Margin = new(3) };
        mine.Children.Add(RepositoryList(_mine, false)); Grid.SetColumn(_myHelp, 1); mine.Children.Add(_myHelp);
        _search = MakeButton("searchBtn", "Search", () => SearchAsync(false)); _search.MinWidth = 93;
        _getUser = MakeButton("getFromUserBtn", "Get from user", () => SearchAsync(true)); _getUser.MinWidth = 124;
        _query.Width = 198;
        StackPanel searchBar = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            { _query, _search, new TextBlock { Text = Tr("orLbl", "or"), VerticalAlignment = VerticalAlignment.Center }, _getUser } };
        Button homepage = MakeButton("openGitupPageBtn", "Open github page", OpenHomepageAsync);
        Grid description = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 4 };
        description.Children.Add(new TextBlock { Text = Tr("descriptionLbl", "Description:") });
        Grid.SetRow(_description, 1); description.Children.Add(_description); Grid.SetRow(homepage, 2); description.Children.Add(homepage);
        Grid searchResults = new() { ColumnDefinitions = new("6*,4*"), ColumnSpacing = 6 };
        searchResults.Children.Add(RepositoryList(_results, true)); Grid.SetColumn(description, 1); searchResults.Children.Add(description);
        _fork = MakeButton("forkBtn", "Fork!", ForkAsync); _fork.Width = 150; _fork.HorizontalAlignment = HorizontalAlignment.Left;
        Grid searchPage = new() { RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 5, Margin = new(3) };
        searchPage.Children.Add(searchBar); Grid.SetRow(searchResults, 1); searchPage.Children.Add(searchResults);
        Grid.SetRow(_searchNotice, 2); searchPage.Children.Add(_searchNotice); Grid.SetRow(_fork, 3); searchPage.Children.Add(_fork);
        _tabs.ItemsSource = new[]
        {
            new TabItem { Name = "myReposPage", Header = Tr("myReposPage", "My repositories"), Content = mine },
            new TabItem { Name = "searchReposPage", Header = Tr("searchReposPage", "Search for repositories"), Content = searchPage }
        };
        _tabs.SelectedIndex = 0;
        _clone = MakeButton("cloneBtn", "Clone", CloneAsync); _clone.Width = 120; _clone.Height = 30; _clone.IsDefault = true;
        Button close = new() { Name = "_NO_TRANSLATE_closeBtn", Content = T("Close"), Width = 120, Height = 30, IsCancel = true };
        close.Click += (_, _) => Close();
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { _clone, close } };
        Grid layout = new() { Margin = new(3), RowDefinitions = new("*,175,34"), RowSpacing = 3 };
        layout.Children.Add(_tabs); Control cloneGroup = BuildCloneGroup(); Grid.SetRow(cloneGroup, 1); layout.Children.Add(cloneGroup);
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons); Root.Children.Add(layout);
        Track(_mine, _results, _tabs, _query, _destination, _subdirectory, _upstream, _upstreamChoices, _protocol, _depth, homepage);
        _mine.SelectionChanged += (_, _) => SelectionChanged(); _results.SelectionChanged += (_, _) => SelectionChanged();
        _tabs.SelectionChanged += (_, args) =>
        {
            if (args.Source != _tabs) { return; }
            SelectionChanged(); if (_tabs.SelectedIndex == 1) { Dispatcher.UIThread.Post(() => _query.Focus()); }
            else if (!_updating && _authenticated && _initialization is not null && !IsBusy) { _ = RunOwned("Load repositories", LoadMineAsync); }
        };
        _protocol.SelectionChanged += (_, _) => { UpdateCloneInfo(); UpdateActions(); };
        _upstreamChoices.SelectionChanged += (_, _) => { if (_upstreamChoices.SelectedItem is string value) { _upstream.Text = value; } };
        foreach (TextBox field in new[] { _destination, _subdirectory, _upstream, _query })
        { field.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { UpdateCloneInfo(); UpdateActions(); } }; }
        ClonePreferences preferences = new ClonePreferencesStore().Load(); _destination.Text = destination.Length > 0 ? destination : preferences.EffectiveDestination;
        if (string.IsNullOrWhiteSpace(_destination.Text))
        { _destination.Text = LoadHistory().Recent.FirstOrDefault()?.Path is string path ? Path.GetDirectoryName(path) : ""; }
        AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.Enter && _query.IsKeyboardFocusWithin)
            { args.Handled = true; if (!IsBusy) { await SearchAsync(false); } }
        }, RoutingStrategies.Tunnel);
        Closing += (_, args) => { _closing = true; if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; } };
        Closed += (_, _) =>
        {
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
        Opened += async (_, _) => { ModalWindowActivation.AfterMapping(this, () => !_closing); await InitializeAsync(); };
        UpdateActions();
    }

    private string Tr(string item, string source) => Translations.Translate(Category, item, "Text", source);
    private Button MakeButton(string name, string text, Func<Task> action)
    { Button button = Action(name, Tr(name, text), action); button.Name = name; return button; }
    private Control BuildCloneGroup()
    {
        Button browse = MakeButton("browseForCloneToDirbtn", "Browse", () => RunOwned("Choose directory", async token =>
        { string? path = await ExportDialogControls.FolderAsync(this, T("Choose directory"), _destination.Text); EnsureCurrent(token); if (path is not null) { _destination.Text = path; } }));
        Grid destination = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        destination.Children.Add(_destination); Grid.SetColumn(browse, 1); destination.Children.Add(browse);
        _upstreamChoices.Classes.Add("classic-arrow-only"); _upstreamChoices.Padding = new(0); _upstreamChoices.Resources["ComboBoxThemeMinWidth"] = 0d;
        _upstreamChoices.SelectionBoxItemTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock());
        Grid upstream = new() { ColumnDefinitions = new("*,Auto") }; upstream.Children.Add(_upstream); Grid.SetColumn(_upstreamChoices, 1); upstream.Children.Add(_upstreamChoices);
        Grid fields = new() { ColumnDefinitions = new("*,200"), RowDefinitions = new("Auto,Auto,*"), ColumnSpacing = 12, RowSpacing = 4 };
        AddField(fields, 0, 0, "label1", "Destination folder:", destination);
        AddField(fields, 0, 1, "ProtocolLabel", "Protocol:", _protocol);
        AddField(fields, 1, 0, "createDirectoryLbl", "Create directory:", _subdirectory);
        AddField(fields, 1, 1, "label3", "Add upstream remote as:", upstream);
        Grid.SetRow(_cloneInfo, 2); fields.Children.Add(_cloneInfo); AddField(fields, 2, 1, "depthLabel", "Limit Depth:", _depth);
        return ClassicChrome.Group("cloneSetupGB", Tr("cloneSetupGB", "Clone"), fields);
    }
    private void AddField(Grid grid, int row, int column, string name, string caption, Control input)
    {
        StackPanel body = new() { Spacing = 2, Children = { new TextBlock { Text = Tr(name, caption) }, input } };
        Grid.SetRow(body, row); Grid.SetColumn(body, column); grid.Children.Add(body);
    }
    private Control RepositoryList(ListBox list, bool search)
    {
        Grid panel = new() { RowDefinitions = new("Auto,*") };
        Avalonia.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid header = RepositoryRow(null, search); header.Background = Brushes.Transparent;
        panel.Children.Add(header); Grid.SetRow(list, 1); panel.Children.Add(list); return panel;
    }
    private FuncDataTemplate<GitHubHostedRepository> RepositoryTemplate(bool search) => new((repository, _) => RepositoryRow(repository, search));
    private Grid RepositoryRow(GitHubHostedRepository? repository, bool search)
    {
        Grid row = new() { ColumnDefinitions = new(search ? "180,110,41,40" : "180,45,50,45"), Margin = new(3, 2) };
        string yes = T("Yes"), no = T("No");
        string[] values = repository is null ? search ? new[] { Tr("columnHeaderSearchName", "Name"), Tr("columnHeaderSearchOwner", "Owner"), Tr("columnHeaderSearchIsFork", "Is fork"), Tr("columnHeaderSearchForks", "# Forks") }
            : new[] { Tr("columnHeaderMyReposName", "Name"), Tr("columnHeaderMyReposIsFork", "Is fork"), Tr("columnHeaderMyReposForks", "# Forks"), Tr("columnHeaderMyReposIsPrivate", "Private") }
            : search ? new[] { repository.Name, repository.Owner, repository.Fork ? yes : no, repository.ForksCount?.ToString(CultureInfo.CurrentCulture) ?? "" }
            : new[] { repository.Name, repository.Fork ? yes : no, repository.ForksCount?.ToString(CultureInfo.CurrentCulture) ?? "", repository.Private ? yes : no };
        for (int column = 0; column < values.Length; column++)
        {
            TextBlock text = new() { Text = values[column], TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = column == (search ? 3 : 2) ? TextAlignment.Right : column == (search ? 2 : 1) || !search && column == 3 ? TextAlignment.Center : TextAlignment.Left };
            Grid.SetColumn(text, column); row.Children.Add(text);
        }
        return row;
    }
    internal Task InitializeAsync() => _initialization ??= _authenticated ? RunOwned("Load repositories", LoadMineAsync) : Task.CompletedTask;
    private Task RunOwned(string label, Func<CancellationToken, Task> action)
    {
        if (IsBusy || _closing) { return Task.CompletedTask; }
        return _completion = RunAsync(label, action);
    }
    private async Task LoadMineAsync(CancellationToken token)
    {
        GitHubHostingPage<GitHubHostedRepository> page = await _client.GetRepositoriesAsync(cancellationToken: token);
        EnsureCurrent(token); _mine.ItemsSource = page.Items.OrderBy(repository => repository.Name, StringComparer.CurrentCulture).ToArray();
        if (page.Truncated) { _myHelp.Text = T("The repository list is incomplete."); }
    }
    private Task SearchAsync(bool user) => RunOwned(user ? "Load repositories" : "Search", async token =>
    {
        string query = _query.Text ?? ""; if (string.IsNullOrWhiteSpace(query)) { return; }
        _results.ItemsSource = Array.Empty<GitHubHostedRepository>(); _searchNotice.Text = "";
        IReadOnlyList<GitHubHostedRepository> items; bool truncated;
        if (user)
        { GitHubHostingPage<GitHubHostedRepository> page = await _client.GetRepositoriesAsync(query.Trim(), token); items = page.Items; truncated = page.Truncated; }
        else
        { GitHubRepositorySearchResult page = await _client.SearchRepositoriesAsync(query, token); items = page.Items; truncated = page.Truncated; }
        EnsureCurrent(token); _results.ItemsSource = items.OrderBy(repository => repository.Name, StringComparer.CurrentCulture).ToArray();
        _searchNotice.Text = truncated ? T("The repository list is incomplete. Refine the search to see other matches.") : items.Count == 0 ? T("No repositories found.") : "";
    });
    private void SelectionChanged()
    {
        if (_updating || _closing) { return; }
        _selectionVersion++; _selected = (_tabs.SelectedIndex == 1 ? _results : _mine).SelectedItem as GitHubHostedRepository; _parent = null;
        _description.Text = _selected?.Description ?? ""; _subdirectory.Text = _selected?.Name ?? ""; _upstream.Text = _selected?.Parent?.Owner ?? "";
        _upstreamChoices.ItemsSource = _selected?.Parent is { } parent ? new[] { parent.Owner, "upstream" }.Distinct().ToArray() : Array.Empty<string>();
        UpdateCloneInfo(); UpdateActions();
        if (_selected is { Fork: true } repository && !IsBusy) { _ = RunOwned("Load repository", token => LoadParentAsync(repository, token)); }
    }
    private async Task LoadParentAsync(GitHubHostedRepository repository, CancellationToken token)
    {
        long version = _selectionVersion; GitHubRepositoryId requested = repository.Identity;
        if (repository.Parent is null) { repository = await _client.GetRepositoryAsync(requested, token); }
        EnsureCurrent(token); if (version != _selectionVersion) { return; }
        if (!SameRepository(repository.Identity, requested)) { throw new InvalidOperationException(T("The repository details do not match the selected fork.")); }
        _selected = repository;
        if (repository.Parent is not { } parent) { return; }
        GitHubHostedRepository details = await _client.GetRepositoryAsync(parent, token);
        EnsureCurrent(token); if (version != _selectionVersion) { return; }
        if (!SameRepository(details.Identity, parent)) { throw new InvalidOperationException(T("The parent repository does not match the selected fork.")); }
        _parent = details;
        if (string.IsNullOrEmpty(_upstream.Text)) { _upstream.Text = parent.Owner; }
        _upstreamChoices.ItemsSource = new[] { parent.Owner, "upstream" }.Distinct().ToArray(); UpdateCloneInfo(); UpdateActions();
    }
    private Task ForkAsync() => RunOwned("Fork", async token =>
    {
        if (!_authenticated || _tabs.SelectedIndex != 1 || _selected is not { } source) { return; }
        ForkAccepted = ForkOutcomeUnknown = false;
        if (!_forkAttempts.Add(source.Identity)) { throw new InvalidOperationException(T("A fork request was already sent for this repository. Refresh My repositories instead of sending it again.")); }
        GitHubHostedRepository fork;
        try { fork = await _client.ForkRepositoryAsync(source.Identity, cancellationToken: token); ForkAccepted = true; }
        catch (GitHubHostingException error) when (error.StatusCode is 400 or 401 or 403 or 404 or 422)
        { _forkAttempts.Remove(source.Identity); throw; }
        catch (Exception error) when (error is OperationCanceledException or GitHubHostingException)
        { ForkOutcomeUnknown = true; _searchNotice.Text = T("The fork request may have reached GitHub. Refresh My repositories before trying Fork again."); throw; }
        EnsureCurrent(token); _searchNotice.Text = T("The fork was accepted. GitHub may still be preparing it.");
        // The accepted POST is already authoritative, even if the following list refresh fails.
        SelectAcceptedFork(fork);
        string accepted = T("The fork was accepted. GitHub may still be preparing its Git objects; refresh before retrying a failed clone.");
        _myHelp.Text = accepted;
        try
        {
            await LoadMineAsync(token); EnsureCurrent(token); SelectAcceptedFork(fork); _myHelp.Text = accepted;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            EnsureCurrent(token); SelectAcceptedFork(fork);
            _myHelp.Text = accepted + "\n" + T("Could not refresh My repositories.") + "\n" + error.Message;
        }
        if (fork.Fork) { await LoadParentAsync(fork, token); }
    });
    private void SelectAcceptedFork(GitHubHostedRepository fork)
    {
        GitHubHostedRepository[] repos = _mine.Items.OfType<GitHubHostedRepository>().Where(repository => repository.Id != fork.Id).Append(fork)
            .OrderBy(repository => repository.Name, StringComparer.CurrentCulture).ToArray();
        _updating = true;
        try { _mine.ItemsSource = repos; _tabs.SelectedIndex = 0; _mine.SelectedItem = fork; }
        finally { _updating = false; }
        SelectionChanged();
    }
    private Task OpenHomepageAsync() => RunOwned("Open github page", _ =>
    {
        if (_selected?.Homepage is not { } value || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || value.Any(char.IsControl))
        { throw new InvalidOperationException(Tr("_strNoHomepageDefined", "No homepage defined")); }
        _openUrl(uri); return Task.CompletedTask;
    });
    private void EnsureCurrent(CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { throw new OperationCanceledException(token); } }
    private void UpdateCloneInfo()
    {
        if (_cloneInfo is null) { return; }
        bool multiline = (_destination.Text ?? "").IndexOfAny(['\r', '\n']) >= 0;
        _destination.Height = multiline ? double.NaN : 24; _destination.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (_selected is not { } repository) { _cloneInfo.Text = ""; return; }
        string access = repository.CanPush switch { true => T("You have push access."), false => T("You do not have push access."), _ => T("Push access is unknown.") };
        string path; try { path = Path.Combine(_destination.Text ?? "", _subdirectory.Text ?? ""); } catch (ArgumentException) { path = ""; }
        _cloneInfo.Text = T("Will clone") + " " + repository.Identity + " → " + ExportDialogControls.DisplayPath(path) + "\n" + access;
    }
    protected override void UpdateActions()
    {
        if (_clone is null) { return; }
        bool idle = !IsBusy && !_closing;
        _search.IsEnabled = _getUser.IsEnabled = idle && !string.IsNullOrWhiteSpace(_query.Text);
        _fork.IsEnabled = idle && _authenticated && _tabs.SelectedIndex == 1 && _selected is not null && !_forkAttempts.Contains(_selected.Identity);
        _clone.IsEnabled = idle && _selected is not null && !string.IsNullOrWhiteSpace(_destination.Text)
            && !string.IsNullOrEmpty(_protocol.SelectedIndex == 0 ? _selected.SshUrl : _selected.CloneUrl);
        _upstream.IsEnabled = _upstreamChoices.IsEnabled = idle && _selected?.Parent is not null;
        _protocol.IsEnabled = idle && _selected is not null;
    }
}
