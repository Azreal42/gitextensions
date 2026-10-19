using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

internal sealed class ViewPullRequestsDialog : PullRequestDialog
{
    private const string Category = "ViewPullRequestsForm";
    private readonly IReadOnlyList<PullRequestHostedRemote> _remotes;
    private readonly IGitHubHostingClient _client;
    private readonly Func<GitHubPullRequest, bool, CancellationToken, Task<bool>> _fetchAction;
    private readonly bool _canWrite;
    private readonly string? _chooseRemote;
    private readonly ComboBox _repositories = new() { Name = "_selectHostedRepoCB", MinWidth = 258, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
    private readonly ListBox _pullRequests = new() { Name = "_pullRequestsList", SelectionMode = SelectionMode.Single };
    private readonly TreeView _files = new() { Name = "_fileStatusList", SelectionMode = SelectionMode.Single };
    private readonly FileStatusListView _fileList;
    private readonly TextBox _diffSource = new();
    private readonly DiffView _diff;
    private readonly PullRequestDiscussionView _discussion = new();
    private readonly TextBox _postText = new() { Name = "_postCommentText", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
    private readonly Button _fetch, _addFetch, _closePr, _post, _refresh;
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _patches = new(StringComparer.Ordinal);
    private GitHubPullRequest? _selected;
    private GitHubRepositoryId? _selectedRepository;
    private long _repositoryGeneration, _selectionGeneration, _discussionGeneration, _draftRevision;
    private CancellationTokenSource? _repositoryRead, _selectionRead, _discussionRead;
    private bool _loading, _loadingDraft, _firstLoad = true;
    private Task? _initialization;
    internal GitHubPullRequest? SelectedPullRequest => _selected;
    internal FileStatusListView FilesView => _fileList;
    internal DiffView DiffViewer => _diff;
    internal PullRequestDiscussionView Discussion => _discussion;
    internal ViewPullRequestsDialog(GitRepository repository, IReadOnlyList<PullRequestHostedRemote> remotes,
        IGitHubHostingClient client, Func<GitHubPullRequest, bool, CancellationToken, Task<bool>> fetch,
        string? chooseRemote = null, bool canWrite = true)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _remotes = remotes.ToArray(); _client = client; _fetchAction = fetch; _chooseRemote = chooseRemote; _canWrite = canWrite;
        Name = "ViewPullRequestsForm"; Title = T(Category, "$this", "View Pull Requests"); Width = MinWidth = 754; Height = MinHeight = 511;
        _fileList = new(_files) { Name = "PullRequestFileStatusList" }; _diff = new(_diffSource) { Name = "_diffViewer", Mode = DiffViewMode.Unified };
        _fetch = ActionButton(Category, "_fetchBtn", "Fetch to pr/ branch", () => FetchAsync(false));
        _addFetch = ActionButton(Category, "_addAndFetchBtn", "Add remote and fetch", () => FetchAsync(true));
        _closePr = ActionButton(Category, "_closePullRequestBtn", "Close pull request", () => Run(ClosePullRequestAsync, mutation: true));
        _post = ActionButton(Category, "_postComment", "Post comment", () => Run(PostCommentAsync, mutation: true));
        _refresh = ActionButton(Category, "_refreshCommentsBtn", "Refresh", ReloadDiscussion);
        foreach (Button button in new[] { _fetch, _addFetch, _closePr }) { button.MinWidth = 155; button.MinHeight = 29; }
        _post.MinWidth = 131; _refresh.MinWidth = 100;
        Grid choose = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 7, Margin = new(3, 2) };
        choose.Children.Add(new TextBlock { Name = "_chooseRepo", Text = T(Category, "_chooseRepo", "Choose repository:"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_repositories, 1); choose.Children.Add(_repositories);
        StackPanel actions = new() { Spacing = 3, Margin = new(5, 0, 3, 0), Children = { _fetch, _addFetch, _closePr } };
        Grid list = new() { ColumnDefinitions = new("*,Auto"), Margin = new(3), RowDefinitions = new("Auto,*") };
        Grid headings = Columns(); string[] names = ["columnHeaderId", "columnHeaderHeading", "columnHeaderBy", "columnHeaderCreated", "columnHeaderBranch"];
        string[] texts = ["#", "Heading", "By", "Created", "Will be fetched to branch"];
        for (int column = 0; column < names.Length; column++)
        { TextBlock text = new() { Name = names[column], Text = T(Category, names[column], texts[column]), TextWrapping = TextWrapping.Wrap, Margin = new(2, 1) }; Grid.SetColumn(text, column); headings.Children.Add(text); }
        list.Children.Add(headings); Grid.SetRow(_pullRequests, 1); list.Children.Add(_pullRequests); Grid.SetColumn(actions, 1); Grid.SetRowSpan(actions, 2); list.Children.Add(actions);
        Grid top = new() { RowDefinitions = new("Auto,*") }; top.Children.Add(choose); Grid.SetRow(list, 1); top.Children.Add(list);
        Grid diffPanel = new() { RowDefinitions = new("116,4,*") };
        diffPanel.Children.Add(_fileList); GridSplitter fileSplitter = new() { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(fileSplitter, 1); diffPanel.Children.Add(fileSplitter); Grid.SetRow(_diff, 2); diffPanel.Children.Add(_diff);
        Grid comments = new() { RowDefinitions = new("*,80,Auto"), RowSpacing = 3, Margin = new(3) };
        comments.Children.Add(_discussion); Grid.SetRow(_postText, 1); comments.Children.Add(_postText);
        StackPanel commentActions = new() { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, Children = { _post, _refresh } };
        Grid.SetRow(commentActions, 2); comments.Children.Add(commentActions);
        TabControl tabs = new() { Name = "tabControl1", ItemsSource = new[]
        { new TabItem { Name = "tabPage1", Header = T(Category, "tabPage1", "Diffs"), Content = diffPanel }, new TabItem { Name = "tabPage2", Header = T(Category, "tabPage2", "Comments"), Content = comments } } };
        Grid layout = new() { RowDefinitions = new("146,4,*,Auto") };
        layout.Children.Add(top); GridSplitter splitter = new() { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(splitter, 1); layout.Children.Add(splitter); Grid.SetRow(tabs, 2); layout.Children.Add(tabs); Grid.SetRow(Notice, 3); layout.Children.Add(Notice); Content = layout;
        _repositories.SelectionChanged += (_, _) => { if (!_loading) { _ = ReloadRepositoryAsync(); } };
        _pullRequests.SelectionChanged += (_, _) => { if (!_loading) { SelectPullRequest((_pullRequests.SelectedItem as ListBoxItem)?.Tag as GitHubPullRequest); } };
        _files.SelectionChanged += (_, _) => ShowSelectedPatch(); _fileList.DisplayChanged += (_, _) => ShowSelectedPatch();
        _postText.PropertyChanged += (_, args) =>
        { if (!_loadingDraft && args.Property == TextBox.TextProperty) { _draftRevision++; SaveDraft(); StateChanged(); } };
        _discussion.LinkError += error => Notice.Text = error;
        Opened += async (_, _) => await InitializeAsync();
        StateChanged();
    }
    private static Grid Columns() => new() { ColumnDefinitions = new("32,*,72,112,145"), ColumnSpacing = 3 };
    internal Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        _loading = true; _repositories.ItemsSource = _remotes; _repositories.SelectedItem = _remotes.FirstOrDefault(remote => remote.Name == _chooseRemote) ?? _remotes.FirstOrDefault(); _loading = false;
        if (_repositories.SelectedItem is null) { Notice.Text = T("No hosted repository is available."); return; }
        await ReloadRepositoryAsync();
    }
    private Task ReloadRepositoryAsync()
    {
        SaveDraft(); _repositoryRead?.Cancel(); _selectionRead?.Cancel(); _discussionRead?.Cancel(); long generation = ++_repositoryGeneration; ++_selectionGeneration; ++_discussionGeneration;
        if (_repositories.SelectedItem is not PullRequestHostedRemote remote) { return Task.CompletedTask; }
        _selectedRepository = remote.Repository.Identity; _selected = null;
        _loading = true; _pullRequests.ItemsSource = Array.Empty<ListBoxItem>(); _loading = false;
        SelectPullRequest(null); StateChanged();
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); _repositoryRead = source;
        return Run(async _ =>
        {
            try
            {
                GitHubHostingPage<GitHubPullRequest> pulls;
                try { pulls = await _client.GetPullRequestsAsync(remote.Repository.Identity, cancellationToken: source.Token); }
                catch (Exception) when (_firstLoad && generation == _repositoryGeneration && !source.IsCancellationRequested && _repositories.SelectedIndex + 1 < _remotes.Count)
                { _loading = true; _repositories.SelectedIndex++; _loading = false; await ReloadRepositoryAsync(); return; }
                source.Token.ThrowIfCancellationRequested(); if (generation != _repositoryGeneration || ClosingRequested) { return; }
                if (_firstLoad && pulls.Items.Count == 0 && _repositories.SelectedIndex + 1 < _remotes.Count)
                { _loading = true; _repositories.SelectedIndex++; _loading = false; await ReloadRepositoryAsync(); return; }
                _firstLoad = false; SetPullRequests(pulls.Items);
                if (pulls.Truncated) { Notice.Text = T("The pull request list is incomplete."); }
            }
            finally { if (_repositoryRead == source) { _repositoryRead = null; } source.Dispose(); }
        }, current: () => generation == _repositoryGeneration);
    }
    private void SetPullRequests(IReadOnlyList<GitHubPullRequest> requests)
    {
        int? number = _selected?.Number; _loading = true;
        _pullRequests.ItemsSource = requests.Select(request =>
        {
            Grid row = Columns(); string[] values = [request.Number.ToString(), request.Title, request.Author, request.CreatedAt.LocalDateTime.ToString("G"), request.FetchBranch];
            for (int column = 0; column < values.Length; column++)
            { TextBlock text = new() { Text = values[column], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(2, 1) }; ToolTip.SetTip(text, values[column]); Grid.SetColumn(text, column); row.Children.Add(text); }
            return new ListBoxItem { Content = row, Tag = request, Padding = default, MinHeight = 20, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }).ToArray();
        _pullRequests.SelectedIndex = number is null ? -1 : requests.ToList().FindIndex(request => request.Number == number);
        if (_pullRequests.SelectedIndex < 0 && requests.Count > 0) { _pullRequests.SelectedIndex = 0; } _loading = false;
        SelectPullRequest((_pullRequests.SelectedItem as ListBoxItem)?.Tag as GitHubPullRequest);
    }
    private void SaveDraft()
    { if (_selected is not null && _selectedRepository is not null) { _drafts[Key(_selectedRepository, _selected.Number)] = _postText.Text ?? ""; } }
    private static string Key(GitHubRepositoryId repository, int number) => repository + "#" + number;
    private void SelectPullRequest(GitHubPullRequest? request)
    {
        SaveDraft(); _selectionRead?.Cancel(); _discussionRead?.Cancel(); long generation = ++_selectionGeneration; ++_discussionGeneration; _selected = request;
        _loadingDraft = true; _postText.Text = request is null || _selectedRepository is null ? "" : _drafts.GetValueOrDefault(Key(_selectedRepository, request.Number), ""); _loadingDraft = false; _draftRevision++;
        _patches.Clear(); _fileList.SetGroups([]); _diff.ShowDocument(new("", "", GitDiffAppearance.Patch, false)); _discussion.SetEntries([]); StateChanged();
        if (request is null || _selectedRepository is null) { return; }
        GitHubRepositoryId repository = _selectedRepository;
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); _selectionRead = source;
        _ = Run(async _ =>
        {
            try
            {
                Task<GitHubHostingPage<GitHubPullRequestFile>> files = _client.GetPullRequestFilesAsync(repository, request.Number, source.Token);
                Task<string> patch = _client.GetPullRequestDiffAsync(repository, request.Number, source.Token);
                await Task.WhenAll(files, patch); source.Token.ThrowIfCancellationRequested();
                if (generation != _selectionGeneration || ClosingRequested) { return; }
                SetFiles(files.Result.Items, patch.Result); if (files.Result.Truncated) { Notice.Text = T("The file list is incomplete."); }
            }
            finally { if (_selectionRead == source) { _selectionRead = null; } source.Dispose(); }
        }, current: () => generation == _selectionGeneration);
        _ = ReloadDiscussion();
    }
    private void SetFiles(IReadOnlyList<GitHubPullRequestFile> files, string diff)
    {
        // Shared Core projection uses the existing GitPatch parser and literal filename metadata.
        IReadOnlyList<GitHubPullRequestFileDiff> projected = GitHubPullRequestDiff.Project(diff, files);
        _patches.Clear(); foreach (GitHubPullRequestFileDiff file in projected) { _patches[file.File.Filename] = file.Diff; }
        FileStatusListEntry[] entries = projected.Select(file => new FileStatusListEntry(file.File.Filename, file.File.Filename, file.File.PreviousFilename,
            file.File.Status switch { "added" => 'A', "removed" => 'D', "renamed" => 'R', "copied" => 'C', _ => 'M' })).ToArray();
        _fileList.SetGroups([new(_selected!, "PullRequestFiles", new TextBlock { Text = T("Diffs") }, entries)]);
        if (entries.Length > 0) { _fileList.SelectTags([entries[0].Tag]); } ShowSelectedPatch();
    }
    private void ShowSelectedPatch()
    {
        if (_fileList.IsUpdating) { return; }
        string text = _fileList.SelectedTags.OfType<string>().FirstOrDefault() is { } path ? _patches.GetValueOrDefault(path, "") : "";
        if (_diff.CanonicalText != text) { _diff.ShowDocument(new(text, text, GitDiffAppearance.Patch, false)); }
    }
    private Task ReloadDiscussion()
    {
        _discussionRead?.Cancel(); long generation = ++_discussionGeneration;
        if (_selected is not { } request || _selectedRepository is not { } repository) { return Task.CompletedTask; }
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); _discussionRead = source;
        return Run(async _ =>
        {
            try
            {
                GitHubHostingPage<GitHubPullRequestDiscussionEntry> discussion = await _client.GetDiscussionAsync(repository, request.Number, source.Token); source.Token.ThrowIfCancellationRequested();
                if (generation != _discussionGeneration || ClosingRequested) { return; }
                _discussion.SetEntries(discussion.Items); if (discussion.Truncated) { Notice.Text = T("The discussion is incomplete."); }
            }
            finally { if (_discussionRead == source) { _discussionRead = null; } source.Dispose(); }
        }, current: () => generation == _discussionGeneration);
    }
    private Task FetchAsync(bool addRemote)
    {
        if (_selected is not { } request) { return Task.CompletedTask; }
        return Run(async token => { if (await _fetchAction(request, addRemote, token)) { token.ThrowIfCancellationRequested(); Accept(true); } }, mutation: true);
    }
    private async Task PostCommentAsync(CancellationToken token)
    {
        if (_selected is not { } request || _selectedRepository is not { } repository || !_canWrite || string.IsNullOrWhiteSpace(_postText.Text)) { return; }
        string body = _postText.Text!; long revision = _draftRevision; string key = Key(repository, request.Number);
        await _client.PostCommentAsync(repository, request.Number, body, token); token.ThrowIfCancellationRequested();
        if (_selectedRepository == repository && _selected?.Number == request.Number)
        { if (revision == _draftRevision) { _postText.Text = ""; _drafts.Remove(key); } await ReloadDiscussion(); }
        else if (_drafts.GetValueOrDefault(key) == body) { _drafts.Remove(key); }
    }
    private async Task ClosePullRequestAsync(CancellationToken token)
    {
        if (_selected is not { } request || _selectedRepository is not { } repository || !_canWrite) { return; }
        await _client.ClosePullRequestAsync(repository, request.Number, token); token.ThrowIfCancellationRequested();
        if (_selectedRepository == repository) { await ReloadRepositoryAsync(); }
    }
    protected override void StateChanged()
    {
        if (_fetch is null) { return; }
        bool selected = !ClosingRequested && !Mutating && _selected is not null;
        _fetch.IsEnabled = _addFetch.IsEnabled = selected && _selected!.Head.Repository is not null;
        _closePr.IsEnabled = selected && _canWrite && _selected!.IsOpen;
        _post.IsEnabled = selected && _canWrite && !string.IsNullOrWhiteSpace(_postText.Text); _refresh.IsEnabled = selected;
        _repositories.IsEnabled = _pullRequests.IsEnabled = !ClosingRequested && !Mutating;
        _postText.IsEnabled = !ClosingRequested && _canWrite && _selected is not null;
    }
    protected override Task CloseChildrenAsync()
    { _repositoryRead?.Cancel(); _selectionRead?.Cancel(); _discussionRead?.Cancel(); return Task.CompletedTask; }
    protected override void DisposeControls() { _fileList.Dispose(); _diff.Dispose(); }
}
