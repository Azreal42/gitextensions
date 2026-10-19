using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

internal sealed class CreatePullRequestDialog : PullRequestDialog
{
    private const string Category = "CreatePullRequestForm";
    private readonly GitRepository _repository;
    private readonly IReadOnlyList<PullRequestHostedRemote> _remotes;
    private readonly IGitHubHostingClient _client;
    private readonly ComboBox _target = new() { Name = "_pullReqTargetsCB", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
    private readonly ComboBox _source = new() { Name = "_yourBranchesCB", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
    private readonly ComboBox _base = new() { Name = "_remoteBranchesCB", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 };
    private readonly TextBox _title = new() { Name = "_titleTB", Height = 24, MinHeight = 24 };
    private readonly TextBox _body = new() { Name = "_bodyTB", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
    private readonly Button _create;
    private readonly Func<CancellationToken, Task<string?>> _templateReader;
    private readonly Func<PullRequestHostedRemote, string, CancellationToken, Task<string?>> _titleReader;
    private readonly string? _chooseRemote, _chooseBranch;
    private bool _loading, _settingBody, _sourceReady, _baseReady;
    private long _bodyRevision, _targetGeneration, _titleGeneration;
    private string _lastTitle = "";
    private CancellationTokenSource? _targetRead, _titleRead;
    private Task? _initialization;
    internal GitHubPullRequest? CreatedPullRequest { get; private set; }
    internal CreatePullRequestDialog(GitRepository repository, IReadOnlyList<PullRequestHostedRemote> remotes, IGitHubHostingClient client,
        string? chooseRemote = null, string? chooseBranch = null,
        Func<CancellationToken, Task<string?>>? templateReader = null,
        Func<PullRequestHostedRemote, string, CancellationToken, Task<string?>>? titleReader = null)
    {
        _repository = repository; _remotes = remotes.ToArray(); _client = client; _chooseRemote = chooseRemote;
        _chooseBranch = chooseBranch;
        _templateReader = templateReader ?? ReadTemplateAsync; _titleReader = titleReader ?? ReadTitleAsync;
        Name = "CreatePullRequestForm"; Title = T(Category, "$this", "Create Pull Request");
        Width = MinWidth = 546; Height = MinHeight = 323;
        _create = ActionButton(Category, "_createBtn", "Create", () => Run(CreateAsync, mutation: true)); _create.MinWidth = 112; _create.MinHeight = 33;
        Grid branches = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 3 };
        Row(branches, 0, "label3", "Target repository:", _target); Row(branches, 1, "label4", "Your branch:", _source); Row(branches, 2, "label5", "Target branch:", _base);
        Grid data = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,*"), ColumnSpacing = 7, RowSpacing = 5 };
        Row(data, 0, "label1", "Title:", _title); Row(data, 1, "label2", "Body:", _body);
        Control group = ClassicChrome.Group("groupBox1", T(Category, "groupBox1", "Pull request data"), data);
        Grid layout = new() { Margin = new(9), RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 5 };
        layout.Children.Add(branches); Grid.SetRow(group, 1); layout.Children.Add(group);
        Grid.SetRow(Notice, 2); layout.Children.Add(Notice); Grid.SetRow(_create, 3); _create.HorizontalAlignment = HorizontalAlignment.Right; layout.Children.Add(_create);
        Content = layout;
        _target.SelectionChanged += (_, _) => { if (!_loading) { _ = StartTargetRead(); } };
        _source.SelectionChanged += (_, _) => { if (!_loading) { _ = StartTitleRead(); } StateChanged(); };
        _base.SelectionChanged += (_, _) => StateChanged();
        _title.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { StateChanged(); } };
        _body.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_settingBody) { _bodyRevision++; } };
        Opened += async (_, _) => await InitializeAsync();
        StateChanged();
    }
    private void Row(Grid grid, int row, string name, string source, Control editor)
    {
        TextBlock label = new() { Name = name, Text = T(Category, name, source), VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 4, 0, 0) };
        Grid.SetRow(label, row); Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); grid.Children.Add(label); grid.Children.Add(editor);
    }
    internal Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        PullRequestHostedRemote[] targets = _remotes.Where(remote => !remote.IsOwnedByMe).ToArray();
        _loading = true; _target.ItemsSource = targets;
        _target.SelectedItem = targets.FirstOrDefault(remote => remote.Name == _chooseRemote) ?? targets.FirstOrDefault(); _loading = false;
        if (targets.Length == 0) { Notice.Text = T("Please clone GitHub repository before pull request."); }
        Task target = StartTargetRead(); Task source = Run(LoadSourceAsync); Task template = Run(LoadTemplateAsync);
        await Task.WhenAll(target, source, template);
    }
    private Task StartTargetRead()
    {
        _targetRead?.Cancel(); long generation = ++_targetGeneration; _baseReady = false; StateChanged();
        if (_target.SelectedItem is not PullRequestHostedRemote remote) { return Task.CompletedTask; }
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); _targetRead = source;
        return Run(async _ =>
        {
            try
            {
                GitHubHostingPage<GitHubHostedBranch> branches = await _client.GetBranchesAsync(remote.Repository.Identity, source.Token);
                source.Token.ThrowIfCancellationRequested(); if (generation != _targetGeneration) { return; }
                string? previous = _base.SelectedItem as string;
                _base.ItemsSource = branches.Items.Select(branch => branch.Name).ToArray();
                _base.SelectedItem = branches.Items.Any(branch => branch.Name == previous) ? previous : branches.Items.FirstOrDefault(branch => branch.Name == remote.Repository.DefaultBranch)?.Name ?? branches.Items.FirstOrDefault()?.Name;
                _baseReady = _base.SelectedItem is string; if (branches.Truncated) { Notice.Text = T("The branch list is incomplete."); }
            }
            finally { if (_targetRead == source) { _targetRead = null; } source.Dispose(); }
        }, current: () => generation == _targetGeneration);
    }
    private PullRequestHostedRemote? MyRemote => _remotes.FirstOrDefault(remote => remote.IsOwnedByMe);
    private async Task LoadSourceAsync(CancellationToken token)
    {
        if (MyRemote is not { } remote) { Notice.Text = T("Please configure your own remote before creating a pull request."); return; }
        GitHubHostingPage<GitHubHostedBranch> branches = await _client.GetBranchesAsync(remote.Repository.Identity, token);
        token.ThrowIfCancellationRequested(); if (ClosingRequested) { return; }
        string? previous = _source.SelectedItem as string;
        string? preferred = _chooseBranch ?? (await _repository.GetStatusAsync(token)).Branch;
        _loading = true; _source.ItemsSource = branches.Items.Select(branch => branch.Name).ToArray();
        _source.SelectedItem = branches.Items.Any(branch => branch.Name == previous) ? previous : branches.Items.FirstOrDefault(branch => branch.Name == preferred)?.Name
            ?? branches.Items.FirstOrDefault(branch => branch.Name == remote.Repository.DefaultBranch)?.Name ?? branches.Items.FirstOrDefault()?.Name;
        _loading = false; _sourceReady = _source.SelectedItem is string;
        if (branches.Truncated) { Notice.Text = T("The branch list is incomplete."); }
        await StartTitleRead();
    }
    private Task StartTitleRead()
    {
        _titleRead?.Cancel(); long generation = ++_titleGeneration;
        if (_source.SelectedItem is not string branch || MyRemote is not { } remote || (_title.Text ?? "") != _lastTitle) { return Task.CompletedTask; }
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); _titleRead = source;
        return Run(async _ =>
        {
            try
            {
                string? subject = await _titleReader(remote, branch, source.Token); source.Token.ThrowIfCancellationRequested();
                if (generation != _titleGeneration || (_title.Text ?? "") != _lastTitle || ClosingRequested) { return; }
                _title.Text = _lastTitle = subject ?? "";
            }
            finally { if (_titleRead == source) { _titleRead = null; } source.Dispose(); }
        }, current: () => generation == _titleGeneration);
    }
    private async Task LoadTemplateAsync(CancellationToken token)
    {
        long revision = _bodyRevision; string? template = await _templateReader(token); token.ThrowIfCancellationRequested();
        if (ClosingRequested || revision != _bodyRevision || !string.IsNullOrEmpty(_body.Text) || template is null) { return; }
        _settingBody = true; try { _body.Text = template; } finally { _settingBody = false; }
    }
    private async Task<string?> ReadTemplateAsync(CancellationToken token)
        => await GitHubHostingTemplates.ReadAsync(_repository.Path, token);
    private async Task<string?> ReadTitleAsync(PullRequestHostedRemote remote, string branch, CancellationToken token)
    { return (await _repository.GetLogAsync(1, remote.Name + "/" + branch, cancellationToken: token, all: false)).FirstOrDefault()?.Subject; }
    private async Task CreateAsync(CancellationToken token)
    {
        string title = (_title.Text ?? "").Trim(), body = (_body.Text ?? "").Trim();
        if (title.Length == 0) { Notice.Text = T("You must specify a title."); return; }
        if (!_sourceReady || !_baseReady || _target.SelectedItem is not PullRequestHostedRemote target || MyRemote is not { } mine
            || _source.SelectedItem is not string source || _base.SelectedItem is not string basis) { return; }
        GitHubPullRequestCreate request = new(mine.Repository.Owner + ":" + source, basis, title, body);
        CreatedPullRequest = await _client.CreatePullRequestAsync(target.Repository.Identity, request, token);
        token.ThrowIfCancellationRequested(); Accept(true);
    }
    protected override void StateChanged()
    {
        if (_create is null) { return; }
        _create.IsEnabled = !ClosingRequested && !IsBusy && !Mutating && _sourceReady && _baseReady && !string.IsNullOrWhiteSpace(_title.Text);
        _target.IsEnabled = _source.IsEnabled = _base.IsEnabled = !ClosingRequested && !Mutating;
        _title.IsEnabled = _body.IsEnabled = !ClosingRequested && !Mutating;
    }
    protected override Task CloseChildrenAsync() { _targetRead?.Cancel(); _titleRead?.Cancel(); return Task.CompletedTask; }
}
