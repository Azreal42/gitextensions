using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed class FileHistoryDialog : RepositoryDialog
{
    private readonly TextBox _file = new() { Name = "FileHistoryPath" };
    private readonly TextBox _revision = new() { Name = "FileHistoryRevision" };
    private readonly CheckBox _follow = new() { Name = "FileHistoryFollow", IsChecked = true };
    private readonly CheckBox _exact = new() { Name = "FileHistoryExact" };
    private readonly CheckBox _full = new() { Name = "FileHistoryFull" };
    private readonly CheckBox _simplify = new() { Name = "FileHistorySimplify" };
    private readonly CommitGraph _graph = new() { Name = "FileHistoryGraph", Width = 180 };
    private readonly ListBox _revisions = new() { Name = "FileHistoryRevisions", SelectionMode = SelectionMode.Multiple };
    private readonly TabControl _tabs = new() { Name = "FileHistoryTabs" };
    private readonly TextBox _commit = new() { Name = "FileHistoryCommitInfo", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly FileHistoryContentView _diff = new() { Name = "FileHistoryDiff" };
    private readonly FileHistoryContentView _view = new() { Name = "FileHistoryView" };
    private readonly BlameView _blame = new() { Name = "FileHistoryBlame" };
    private readonly List<Bitmap> _tabIcons = [];
    private IReadOnlyList<GitFileHistoryEntry> _history = [];
    private bool _loading;
    private int _limit = 300;
    public IReadOnlyList<GitFileHistoryEntry> History => _history;
    public GitFileHistoryEntry? SelectedEntry => _revisions.SelectedItem is GitCommit commit ? _history.FirstOrDefault(entry => entry.Commit.Hash == commit.Hash) : null;

    public FileHistoryDialog(GitRepository repository, string path, string revision = "HEAD") : base(repository, "File History")
    {
        _diff.TextView.ContextMenu = new ContextMenu { ItemsSource = _diff.TextView.CreateContextMenuItems("FileHistoryDiff") };
        _diff.Toolbar.SetNames("FileHistoryDiff");
        _diff.TextView.ComparisonMenu.OptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 1) { await RequestDiffReloadAsync(LoadSelectionAsync); }
        };
        _blame.CommitInformationProvider = (line, token) => BlameDialog.ReadCommitInformationAsync(repository, line, token);
        _blame.InteractionError += error => Status.Text = error;
        Width = 1100; Height = 760; MinWidth = 740; MinHeight = 480;
        _file.Text = path; _revision.Text = revision;
        _follow.Content = T("Detect and follow renames"); _exact.Content = T("Exact renames and copies only");
        _full.Content = T("Show full history"); _simplify.Content = T("Simplify merges");
        Grid paths = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,240,Auto"), Margin = new Avalonia.Thickness(6, 4) };
        Control[] fields = [new TextBlock { Text = T("File") + ":", VerticalAlignment = VerticalAlignment.Center }, _file,
            new TextBlock { Text = T("Revision") + ":", VerticalAlignment = VerticalAlignment.Center }, _revision, Action("LoadFileHistory", "Load", InitializeAsync)];
        for (int index = 0; index < fields.Length; index++) { Grid.SetColumn(fields[index], index); fields[index].Margin = new Avalonia.Thickness(3, 0); paths.Children.Add(fields[index]); }
        Button blameOptions = _blame.CreateOptionsButton();
        StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(9, 2), Children = { _follow, _exact, _full, _simplify, blameOptions } };
        DockPanel.SetDock(paths, Dock.Top); Root.Children.Add(paths); DockPanel.SetDock(options, Dock.Top); Root.Children.Add(options);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(6, 4), Children =
        {
            Action("MoreFileHistory", "Load more", async () => { _limit = Math.Min(100000, _limit + 300); await InitializeAsync(); }),
            Action("SaveFileHistory", "Save as", SaveAsAsync),
            new Button { Name = "CancelFileHistory", Content = T("Close"), IsCancel = true }
        } };
        ((Button)actions.Children[^1]).Click += (_, _) => Close();
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        _revisions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(HeightProperty, 24d), new Setter(MinHeightProperty, 24d), new Setter(PaddingProperty, new Avalonia.Thickness(0)) } });
        _revisions.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) => Row(commit));
        _revisions.AddHandler(ScrollViewer.ScrollChangedEvent, (_, args) => { if (args.Source is ScrollViewer viewer) { _graph.VerticalOffset = viewer.Offset.Y; } });
        DockPanel table = new();
        Grid headers = new() { ColumnDefinitions = new ColumnDefinitions("180,*,150,110,90"), Height = 24 };
        string[] labels = ["Graph", "Message", "Author", "Date", "Commit"];
        for (int index = 0; index < labels.Length; index++) { TextBlock label = new() { Text = T(labels[index]), Margin = new Avalonia.Thickness(4, 2) }; Grid.SetColumn(label, index); headers.Children.Add(label); }
        Border header = ClassicChrome.Border(headers, new Avalonia.Thickness(0, 0, 0, 1));
        header.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        DockPanel.SetDock(header, Dock.Top); table.Children.Add(header);
        Grid rows = new() { ColumnDefinitions = new ColumnDefinitions("180,*") };
        rows.Children.Add(_graph); Grid.SetColumn(_revisions, 1); rows.Children.Add(_revisions); table.Children.Add(rows);
        _tabs.ItemsSource = new[]
        {
            new TabItem { Name = "FileHistoryCommitTab", Header = TabHeader("Commit", "CommitSummary.png"), Content = _commit },
            new TabItem { Name = "FileHistoryDiffTab", Header = TabHeader("Diff", "Diff.png"), Content = _diff },
            new TabItem { Name = "FileHistoryViewTab", Header = TabHeader("View", "ViewFile.png"), Content = _view },
            new TabItem { Name = "FileHistoryBlameTab", Header = TabHeader("Blame", "Blame.png"), Content = _blame }
        };
        _tabs.SelectedIndex = 1;
        Grid layout = new() { RowDefinitions = new RowDefinitions("1*,4,2*") };
        layout.Children.Add(table); GridSplitter splitter = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1); layout.Children.Add(splitter); Grid.SetRow(_tabs, 2); layout.Children.Add(_tabs); Root.Children.Add(layout);
        _revisions.SelectionChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _tabs.SelectionChanged += async (_, args) => { if (args.Source == _tabs && !_loading && !IsBusy) { await LoadSelectionAsync(); } };
        BlamePreferences previousBlamePreferences = _blame.Preferences;
        _blame.PreferencesChanged += async preferences =>
        {
            bool reload = previousBlamePreferences.GitOptions != preferences.GitOptions;
            previousBlamePreferences = preferences;
            if (reload && _tabs.SelectedIndex == 3 && !IsBusy && !_loading) { await LoadSelectionAsync(); }
        };
        _blame.CommandRequested += async (_, args) => await OpenBlameCommandAsync(args);
        Track(_file, _revision, _follow, _exact, _full, _simplify, blameOptions, _revisions, _tabs);
        _diff.TextView.DisplayOptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 1) { await RequestDiffReloadAsync(LoadSelectionAsync); }
        };
        Closed += (_, _) => { _view.Dispose(); _diff.Dispose(); _blame.Dispose(); foreach (Bitmap icon in _tabIcons) { icon.Dispose(); } };
    }

    public override Task InitializeAsync() => RunAsync("File History", async token =>
    {
        _loading = true;
        try
        {
            string? selected = SelectedEntry?.Commit.Hash;
            _history = await Repository.GetFileHistoryAsync(_file.Text ?? "", _revision.Text ?? "HEAD",
                new(_follow.IsChecked == true, _exact.IsChecked == true, _full.IsChecked == true, _simplify.IsChecked == true, _limit), token);
            _graph.Commits = _history.Select(entry => entry.Commit).ToArray();
            _graph.References = await Repository.GetReferencesAsync(token);
            _graph.CurrentHead = await Repository.ResolveRevisionAsync("HEAD", token);
            _revisions.ItemsSource = _graph.OrderedCommits;
            _revisions.SelectedItem = _graph.OrderedCommits.FirstOrDefault(commit => commit.Hash == selected) ?? _graph.OrderedCommits.FirstOrDefault();
            await LoadSelectedAsync(token);
            Status.Text = _history.Count + " " + T("commits");
        }
        finally { _loading = false; }
    });
    private Task LoadSelectionAsync() => RunAsync("Load file revision", LoadSelectedAsync);
    private async Task LoadSelectedAsync(CancellationToken token)
    {
        GitFileHistoryEntry? entry = SelectedEntry;
        TabItem[] tabs = _tabs.Items.OfType<TabItem>().ToArray();
        if (entry is null)
        {
            Title = T("File History") + " — " + _file.Text;
            _commit.Text = ""; _diff.ShowDiff(T("No commits found.")); _view.ShowDiff(T("No commits found."));
            _blame.SetBlame(new(_file.Text ?? "", null, [], []));
            tabs[2].IsEnabled = tabs[3].IsEnabled = false;
            if (_tabs.SelectedIndex >= 2) { _tabs.SelectedIndex = 1; }
            return;
        }
        GitCommit commit = entry.Commit;
        Title = T("File History") + " — " + entry.Path;
        _commit.Text = $"{T("Commit")}: {commit.Hash}\n{T("Author")}: {commit.AuthorName} <{commit.AuthorEmail}>\n{T("Date")}: {commit.AuthoredAt:O}\n{T("File")}: {entry.Path}\n\n{commit.Subject}\n{commit.Body}";
        tabs[2].IsEnabled = tabs[3].IsEnabled = !entry.IsDeleted;
        if (entry.IsDeleted && _tabs.SelectedIndex >= 2) { _tabs.SelectedIndex = 1; }
        if (_tabs.SelectedIndex == 1)
        {
            _diff.TextView.BeginDocumentLoad();
            GitCommit? other = _revisions.SelectedItems?.OfType<GitCommit>().LastOrDefault(selected => selected.Hash != commit.Hash);
            GitRevisionChanges revisions = await Repository.GetRevisionChangesAsync(commit.Hash, other?.Hash, token);
            GitRevisionFile file = revisions.Files.FirstOrDefault(file => file.Path == entry.Path || file.OriginalPath == entry.Path)
                ?? new(entry.Path, entry.Status, entry.PreviousPath);
            await _diff.TextView.ConfigureRepositoryAsync(Repository, token);
            _diff.TextView.DocumentIdentity = string.Join('\0', Repository.Path, entry.Path, commit.Hash, revisions.BaseRevision, "diff");
            _diff.ShowDiff(await Repository.GetRevisionFileDiffDocumentAsync(revisions, file,
                options: _diff.TextView.ComparisonMenu.Options, displayOptions: _diff.TextView.DisplayOptions, cancellationToken: token), file.Path);
        }
        else if (_tabs.SelectedIndex == 2)
        {
            _view.TextView.DocumentIdentity = string.Join('\0', Repository.Path, entry.Path, commit.Hash, "blob");
            _view.ShowFile(entry.Path, await Repository.GetFileBytesAsync(entry.Path, commit.Hash, token), await Repository.GetFileEncodingNameAsync(token));
        }
        else if (_tabs.SelectedIndex == 3) { _blame.SetBlame(await Repository.GetFileBlameAsync(entry.Path, commit.Hash, _blame.Preferences.GitOptions, token)); }
    }

    private async Task OpenBlameCommandAsync(BlameCommandEventArgs args)
    {
        if (IsBusy) { return; }
        string hash = args.Line.Line.Hash, path = args.Line.OriginalPath;
        int line = args.Line.Line.OriginalLine;
        if (args.Command == BlameCommand.PreviousRevision)
        {
            GitBlamePreviousRevision? previous = null;
            await RunAsync("Blame previous revision", async token => previous = await Repository.GetBlamePreviousRevisionAsync(args.Line,
                _blame.Preferences.GitOptions, _blame.Preferences.UseHistogramDiffAlgorithm, token));
            if (LastError is not null || previous is null) { return; }
            hash = previous.Revision; path = previous.Path; line = previous.Line;
        }
        BlameDialog dialog = new(Repository, path, hash, line);
        dialog.Opened += async (_, _) =>
        {
            await dialog.InitializeAsync();
            if (args.Command == BlameCommand.ShowChanges && dialog.LastError is null) { await dialog.ShowChangesAsync(args.Line); }
        };
        await dialog.ShowDialog(this);
    }

    public async Task SaveSelectedFileAsync(string destination, CancellationToken cancellationToken = default)
    {
        GitFileHistoryEntry entry = SelectedEntry ?? throw new InvalidOperationException(T("Select a revision first."));
        if (entry.IsDeleted) { throw new InvalidOperationException(T("The file was deleted in this revision.")); }
        byte[] bytes = await Repository.GetFileBytesAsync(entry.Path, entry.Commit.Hash, cancellationToken);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
    }
    private async Task SaveAsAsync()
    {
        if (SelectedEntry is null || SelectedEntry.IsDeleted) { return; }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("Save as"), SuggestedFileName = Path.GetFileName(SelectedEntry.Path) });
        if (file?.TryGetLocalPath() is string path) { await RunAsync("Save as", token => SaveSelectedFileAsync(path, token)); }
    }
    private static Control Row(GitCommit? commit)
    {
        if (commit is null) { return new TextBlock(); }
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,150,110,90"), Height = 24 };
        string[] values = [commit.Subject, commit.AuthorName, commit.AuthoredAt.ToString("yyyy-MM-dd"), commit.Hash[..Math.Min(9, commit.Hash.Length)]];
        for (int index = 0; index < values.Length; index++)
        { TextBlock text = new() { Text = values[index], Margin = new Avalonia.Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(text, index); row.Children.Add(text); }
        return row;
    }
    private Control TabHeader(string label, string iconName)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + iconName));
        Bitmap bitmap = new(stream); _tabIcons.Add(bitmap);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children =
        {
            new Image { Source = bitmap, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = T(label), VerticalAlignment = VerticalAlignment.Center }
        } };
    }
}
