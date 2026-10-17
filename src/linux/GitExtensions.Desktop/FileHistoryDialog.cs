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
    private readonly TextBlock _sourceNotice = new() { Name = "FileHistorySourceNotice", IsVisible = false, Margin = new Avalonia.Thickness(9, 0, 9, 4), TextWrapping = TextWrapping.Wrap };
    public GitCompareRevision InitialSource { get; private set; } = new();
    private readonly CheckBox _follow = new() { Name = "FileHistoryFollow", IsChecked = true };
    private readonly CheckBox _exact = new() { Name = "FileHistoryExact" };
    private readonly CheckBox _full = new() { Name = "FileHistoryFull" };
    private readonly CheckBox _simplify = new() { Name = "FileHistorySimplify" };
    private readonly CommitGraph _graph = new() { Name = "FileHistoryGraph", Width = 180 };
    private readonly ListBox _revisions = new() { Name = "FileHistoryRevisions", SelectionMode = SelectionMode.Multiple };
    private readonly TabControl _tabs = new() { Name = "FileHistoryTabs" };
    private readonly CommitInformationView _commit = new() { Name = "FileHistoryCommitInfo" };
    private readonly FileHistoryContentView _diff = new() { Name = "FileHistoryDiff" };
    private readonly FileHistoryContentView _view = new() { Name = "FileHistoryView" };
    private readonly BlameView _blame = new() { Name = "FileHistoryBlame" };
    private readonly List<Bitmap> _tabIcons = [];
    private IReadOnlyList<GitFileHistoryEntry> _history = [];
    private string? _loadedHistoryPath;
    private bool _loading;
    private (int Line, string? Encoding)? _initialBlamePosition;
    private int _limit = 300;
    private readonly CancellationTokenSource _informationLifetime = new();
    private string? _informationHash, _informationPath;
    private long _informationGeneration;
    private Exception? _informationError;
    private bool _informationClosing, _informationCloseReady;
    private Window? _closingInformationOwner;
    private readonly List<string> _informationNavigation = [];
    private int _informationNavigationIndex = -1;
    private bool _movingInformationNavigation;
    internal Task InformationCompletion => _commit.Completion;
    public IReadOnlyList<GitFileHistoryEntry> History => _history;
    public GitFileHistoryEntry? SelectedEntry => _revisions.SelectedItem is GitCommit commit ? _history.FirstOrDefault(entry => entry.Commit.Hash == commit.Hash) : null;

    public FileHistoryDialog(GitRepository repository, string path, GitCompareRevision source, bool initialBlame = false)
        : this(repository, path, HistoryAnchor(source), initialBlame)
    {
        InitialSource = source;
        if (source.Kind == GitCompareRevisionKind.Commit) { return; }
        _sourceNotice.Text = source.Kind == GitCompareRevisionKind.Index
            ? T("Showing HEAD history for the index.") : T("Showing HEAD history for the working directory.");
        _sourceNotice.IsVisible = true;
        _revision.PropertyChanged += (_, change) =>
        { if (change.Property == TextBox.TextProperty) { _sourceNotice.IsVisible = _revision.Text == "HEAD"; } };
    }
    private static string HistoryAnchor(GitCompareRevision source)
    { ArgumentNullException.ThrowIfNull(source); return source.Kind == GitCompareRevisionKind.Commit ? source.Revision : "HEAD"; }

    public FileHistoryDialog(GitRepository repository, string path, string revision = "HEAD", bool initialBlame = false) : base(repository, "File History")
    {
        InitialSource = new(revision);
        _diff.TextView.ContextMenu = new ContextMenu { ItemsSource = _diff.TextView.CreateContextMenuItems("FileHistoryDiff") };
        _diff.Toolbar.SetNames("FileHistoryDiff");
        _diff.TextView.ComparisonMenu.OptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 1) { await RequestDiffReloadAsync(LoadSelectionAsync); }
        };
        _blame.CommitInformationProvider = (line, token) => BlameDialog.ReadCommitInformationAsync(repository, line, token);
        _blame.InteractionError += error => Status.Text = error;
        _commit.InteractionError += error => { if (!_informationClosing) { Status.Text = error; } };
        _commit.CommandRequested += async (command, argument) => await InformationCommandAsync(command, argument);
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
        DockPanel.SetDock(_sourceNotice, Dock.Top); Root.Children.Add(_sourceNotice);
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
        _tabs.SelectedIndex = initialBlame ? 3 : 1;
        Grid layout = new() { RowDefinitions = new RowDefinitions("1*,4,2*") };
        layout.Children.Add(table); GridSplitter splitter = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1); layout.Children.Add(splitter); Grid.SetRow(_tabs, 2); layout.Children.Add(_tabs); Root.Children.Add(layout);
        _revisions.SelectionChanged += async (_, _) => { if (!_loading && !IsBusy) { RememberInformationNavigation(); await LoadSelectionAsync(); } };
        _tabs.SelectionChanged += async (_, args) =>
        {
            if (args.Source != _tabs || _loading || IsBusy) { return; }
            if (_tabs.SelectedIndex == 3 && SelectedEntry is { } entry)
            {
                TabItem? previous = args.RemovedItems.OfType<TabItem>().FirstOrDefault();
                if (previous?.Name == "FileHistoryViewTab" && _view.TextView.IsDocumentReady && _view.TextView.DocumentIdentity == string.Join('\0', Repository.Path, entry.Path, entry.Commit.Hash, "blob"))
                { _initialBlamePosition = (_view.TextView.CurrentFileLine, _view.TextView.FileEncodingName); }
                else if (previous?.Name == "FileHistoryDiffTab" && _diff.TextView.IsDocumentReady && _diff.TextView.DocumentIdentity?.StartsWith(string.Join('\0', Repository.Path, entry.Path, entry.Commit.Hash) + '\0', StringComparison.Ordinal) == true)
                { _initialBlamePosition = (_diff.TextView.CurrentFileLine, _diff.TextView.FileEncodingName); }
            }
            await LoadSelectionAsync();
        };
        BlamePreferences previousBlamePreferences = _blame.Preferences;
        _blame.PreferencesChanged += async preferences =>
        {
            bool reload = previousBlamePreferences.GitOptions != preferences.GitOptions;
            previousBlamePreferences = preferences;
            if (reload && _tabs.SelectedIndex == 3 && !IsBusy && !_loading) { await LoadSelectionAsync(); }
        };
        _blame.CommandRequested += async (_, args) => await OpenBlameCommandAsync(args);
        _blame.TextView.FileEncodingChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 3 && !IsBusy && !_loading) { await LoadSelectionAsync(); }
        };
        Track(_file, _revision, _follow, _exact, _full, _simplify, blameOptions, _revisions, _tabs);
        _diff.TextView.DisplayOptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 1) { await RequestDiffReloadAsync(LoadSelectionAsync); }
        };
        Closing += (_, args) =>
        {
            if (_informationCloseReady) { return; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingInformationOwner = Owner as Window; }
            _informationClosing = true; _informationLifetime.Cancel(); _commit.BeginClose();
            Task pending = _commit.Completion;
            if (!pending.IsCompleted) { args.Cancel = true; _ = CloseAfterInformationAsync(pending); }
        };
        Closed += (_, _) =>
        {
            _informationCloseReady = true;
            _commit.Dispose(); _informationLifetime.Dispose(); _view.Dispose(); _diff.Dispose(); _blame.Dispose();
            foreach (Bitmap icon in _tabIcons) { icon.Dispose(); }
            Window? owner = _closingInformationOwner; _closingInformationOwner = null;
            if (owner is not null) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }

    public override Task InitializeAsync() => RunAsync("File History", async token =>
    {
        _loading = true;
        try
        {
            string path = _file.Text ?? "";
            string? selected = _loadedHistoryPath == path ? SelectedEntry?.Commit.Hash : null;
            _history = await Repository.GetFileHistoryAsync(path, _revision.Text ?? "HEAD",
                new(_follow.IsChecked == true, _exact.IsChecked == true, _full.IsChecked == true, _simplify.IsChecked == true, _limit), token);
            if (_loadedHistoryPath != path) { _informationNavigation.Clear(); _informationNavigationIndex = -1; }
            _loadedHistoryPath = path;
            _graph.Commits = _history.Select(entry => entry.Commit).ToArray();
            _graph.References = await Repository.GetReferencesAsync(token);
            _graph.CurrentHead = await Repository.ResolveRevisionAsync("HEAD", token);
            _revisions.ItemsSource = _graph.OrderedCommits;
            _revisions.SelectedItem = _graph.OrderedCommits.FirstOrDefault(commit => commit.Hash == selected) ?? _graph.OrderedCommits.FirstOrDefault();
            RememberInformationNavigation();
            UpdateInformationSelection(force: true);
            await LoadSelectedAsync(token);
            if (LastError is null) { Status.Text = _history.Count + " " + T("commits"); }
        }
        finally { _loading = false; }
    });
    private Task LoadSelectionAsync()
    {
        if (_informationClosing) { return Task.CompletedTask; }
        UpdateInformationSelection();
        if (_tabs.SelectedIndex == 0) { return LoadInformationAsync(_informationLifetime.Token); }
        _informationGeneration++; _commit.Cancel();
        return RunAsync("Load file revision", LoadSelectedAsync);
    }

    private void UpdateInformationSelection(bool force = false)
    {
        if (_informationClosing) { return; }
        GitFileHistoryEntry? entry = SelectedEntry;
        if (!force && _informationHash == entry?.Commit.Hash && _informationPath == entry?.Path) { return; }
        _informationGeneration++;
        _informationHash = entry?.Commit.Hash; _informationPath = entry?.Path;
        if (entry is null) { _commit.SetMessage(""); }
        else { _commit.SetRevision(Repository, entry.Commit, _graph.OrderedCommits, entry.Path); }
    }

    private async Task LoadInformationAsync(CancellationToken token)
    {
        long generation = _informationGeneration;
        if (_informationError is not null && ReferenceEquals(LastException, _informationError)) { ClearError(); }
        _informationError = null;
        try { await _commit.LoadAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_informationClosing && generation == _informationGeneration && _tabs.SelectedIndex == 0)
            { _informationError = exception; ReportError(exception); }
        }
    }

    private async Task CloseAfterInformationAsync(Task pending)
    {
        try { await pending; }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The current request reports its own error; closing still drains it. */ }
        finally
        {
            _informationCloseReady = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(); } });
        }
    }

    private void RememberInformationNavigation()
    {
        if (_movingInformationNavigation || SelectedEntry is not { } entry) { return; }
        string hash = entry.Commit.Hash;
        if (_informationNavigationIndex >= 0 && _informationNavigation[_informationNavigationIndex] == hash) { return; }
        _informationNavigation.RemoveRange(_informationNavigationIndex + 1, _informationNavigation.Count - _informationNavigationIndex - 1);
        _informationNavigation.Add(hash); _informationNavigationIndex = _informationNavigation.Count - 1;
    }

    private async Task InformationCommandAsync(CommitInformationCommand command, string? argument)
    {
        if (IsBusy || _informationClosing) { return; }
        try
        {
            if (command == CommitInformationCommand.AddNotes && argument is not null)
            {
                NoteDialog dialog = new(Repository, argument) { RequestedThemeVariant = ActualThemeVariant };
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await dialog.ShowDialog(this);
                if (_informationClosing) { return; }
                if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
                UpdateInformationSelection(force: true);
                if (_tabs.SelectedIndex == 0) { await LoadInformationAsync(_informationLifetime.Token); }
                return;
            }
            if (command is CommitInformationCommand.Backward or CommitInformationCommand.Forward)
            {
                int next = _informationNavigationIndex + (command == CommitInformationCommand.Backward ? -1 : 1);
                if (next < 0 || next >= _informationNavigation.Count) { return; }
                _movingInformationNavigation = true;
                try { _informationNavigationIndex = next; await SelectInformationRevisionAsync(_informationNavigation[next]); }
                finally { _movingInformationNavigation = false; }
            }
            else if (command == CommitInformationCommand.NavigateRevision && argument is not null) { await SelectInformationRevisionAsync(argument); }
            else if (command == CommitInformationCommand.NavigateReference && argument is not null)
            {
                string hash = await Repository.ResolveRevisionAsync(argument, _informationLifetime.Token);
                if (!_informationClosing) { await SelectInformationRevisionAsync(hash); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_informationClosing) { ReportError(exception); } }
    }

    private async Task SelectInformationRevisionAsync(string hash)
    {
        GitCommit? commit = _graph.OrderedCommits.FirstOrDefault(commit => commit.Hash == hash);
        if (commit is null)
        {
            string message = Translations.Translate("MessageBoxes", "_cannotFindRevisionFilter", "Text", "Revision \"{0}\" is not visible in the revision grid. Remove the revision filter.");
            await Dialogs.ShowErrorAsync(this, string.Format(System.Globalization.CultureInfo.CurrentCulture, message, hash[..Math.Min(7, hash.Length)]));
            return;
        }
        _revisions.SelectedItems?.Clear(); _revisions.SelectedItem = commit; _revisions.ScrollIntoView(commit);
        _revisions.Focus();
    }
    private async Task LoadSelectedAsync(CancellationToken token)
    {
        GitFileHistoryEntry? entry = SelectedEntry;
        TabItem[] tabs = _tabs.Items.OfType<TabItem>().ToArray();
        if (entry is null)
        {
            Title = T("File History") + " — " + _file.Text;
            _commit.SetMessage(""); _diff.ShowDiff(T("No commits found.")); _view.ShowDiff(T("No commits found."));
            _blame.SetBlame(new(_file.Text ?? "", null, [], []));
            tabs[2].IsEnabled = tabs[3].IsEnabled = false;
            if (_tabs.SelectedIndex >= 2) { _tabs.SelectedIndex = 1; }
            return;
        }
        GitCommit commit = entry.Commit;
        Title = T("File History") + " — " + entry.Path;
        UpdateInformationSelection();
        tabs[2].IsEnabled = tabs[3].IsEnabled = !entry.IsDeleted;
        if (entry.IsDeleted && _tabs.SelectedIndex >= 2) { _tabs.SelectedIndex = 1; }
        if (_tabs.SelectedIndex == 0)
        {
            if (!_informationClosing) { await LoadInformationAsync(token); }
        }
        else if (_tabs.SelectedIndex == 1)
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
            _view.TextView.BeginDocumentLoad();
            _view.TextView.DocumentIdentity = string.Join('\0', Repository.Path, entry.Path, commit.Hash, "blob");
            await _view.TextView.ConfigureRepositoryAsync(Repository, token);
            _view.ShowFile(entry.Path, await Repository.GetFileBytesAsync(entry.Path, commit.Hash, token), await Repository.GetFileEncodingNameAsync(token));
            if (!_view.IsImage) { _view.TextView.CompletePlainTextDocumentLoad(); }
        }
        else if (_tabs.SelectedIndex == 3)
        {
            (int Line, string? Encoding)? initialPosition = _initialBlamePosition;
            _initialBlamePosition = null;
            await _blame.ConfigureRepositoryAsync(Repository, token);
            if (initialPosition is { } position) { _blame.TextView.FileEncodingName = position.Encoding; }
            _blame.TextView.BeginDocumentLoad();
            _blame.SetBlame(await Repository.GetFileBlameAsync(entry.Path, commit.Hash, _blame.Preferences.GitOptions, token,
                _blame.TextView.FileEncodingName), initialPosition?.Line);
        }
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
        BlameDialog dialog = new(Repository, path, hash, line, fileEncodingName: _blame.TextView.FileEncodingName);
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
