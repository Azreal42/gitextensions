using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed class CompareRevisionsDialog : RepositoryDialog
{
    private readonly ComparisonRevisionSelector _base;
    private readonly ComparisonRevisionSelector _compare;
    private readonly CheckBox _mergeBase = new() { Name = "CompareToMergeBase" };
    private readonly ComboBox _whitespace = ReferenceDialogControls.Choice("ComparisonWhitespace");
    private readonly NumericUpDown _context = new() { Name = "ComparisonContextLines", Minimum = 0, Maximum = 100000, Value = 3, Width = 100, Height = 24, MinHeight = 24, Padding = new Thickness(2), FormatString = "0" };
    private readonly CheckBox _entire = new() { Name = "ComparisonEntireFile" };
    private readonly CheckBox _asText = new() { Name = "ComparisonTreatAsText" };
    private readonly TextBox _filter = ReferenceDialogControls.Text("ComparisonFileFilter");
    private readonly TextBox _find = ReferenceDialogControls.Text("ComparisonFind");
    private readonly TextBlock _fileCaption = new() { Name = "ComparisonSelectedPath", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListBox _files = new() { Name = "ComparisonFiles" };
    private readonly TabControl _tabs = new() { Name = "ComparisonTabs" };
    private readonly FileHistoryContentView _diff = new() { Name = "ComparisonDiff" };
    private readonly FileHistoryContentView _baseView = new() { Name = "ComparisonBaseView" };
    private readonly FileHistoryContentView _compareView = new() { Name = "ComparisonCompareView" };
    private readonly Button _directoryTool;
    private readonly Button _fileTool;
    private readonly Button _saveBase;
    private readonly Button _saveCompare;
    private readonly List<Bitmap> _icons = [];
    private readonly Dictionary<char, Bitmap> _fileIcons = [];
    private GitComparison? _comparison;
    private string? _fixedMergeBase;
    private bool _mergeBaseLoaded;
    private bool _loading;
    private bool _bare;
    public GitComparison? Comparison => _comparison;
    public GitRevisionFile? SelectedFile => _files.SelectedItem as GitRevisionFile;
    public string? MergeBaseRevision => _fixedMergeBase;

    public CompareRevisionsDialog(GitRepository repository, GitCompareRevision baseRevision, GitCompareRevision compareRevision)
        : this(repository, baseRevision?.Revision ?? throw new ArgumentNullException(nameof(baseRevision)), compareRevision?.Revision ?? throw new ArgumentNullException(nameof(compareRevision)))
    {
        _loading = true;
        try { _base.Set(baseRevision); _compare.Set(compareRevision); }
        finally { _loading = false; }
    }

    public CompareRevisionsDialog(GitRepository repository, string baseRevision = "HEAD~1", string compareRevision = "HEAD") : base(repository, "Diff")
    {
        Width = 1100; Height = 720; MinWidth = 800; MinHeight = 440;
        _base = new("ComparisonBase", true, baseRevision, T);
        _compare = new("ComparisonCompare", false, compareRevision, T);
        Button swap = IconAction("ComparisonSwap", "Renamed.png", "Swap BASE and Compare commits", SwapAsync);
        Grid revisions = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(6, 4) };
        revisions.Children.Add(_base); swap.Margin = new Thickness(6, 14, 6, 0); Grid.SetColumn(swap, 1); revisions.Children.Add(swap); Grid.SetColumn(_compare, 2); revisions.Children.Add(_compare);
        DockPanel.SetDock(revisions, Dock.Top); Root.Children.Add(revisions);
        _mergeBase.Content = T("Compare to merge base");
        _entire.Content = T("Show entire file"); _asText.Content = T("Treat all files as text");
        ReferenceDialogControls.Compact(_mergeBase, _entire, _asText);
        _whitespace.Width = 190;
        _whitespace.ItemsSource = new[] { T("Do not ignore whitespace"), T("Ignore whitespace at EOL"), T("Ignore whitespace changes"), T("Ignore all whitespace") };
        _whitespace.SelectedIndex = 0;
        _directoryTool = Action("ComparisonDirectoryTool", "Open diff using directory diff tool", () => OpenToolAsync(true));
        Button load = Action("CompareRevisions", "Compare", InitializeAsync); load.IsDefault = true;
        StackPanel compareOptions = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(9, 0, 9, 4), Children = { _mergeBase, _directoryTool, load } };
        DockPanel.SetDock(compareOptions, Dock.Top); Root.Children.Add(compareOptions);
        StackPanel diffOptions = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(9, 0, 9, 4), Children =
        { _whitespace, new TextBlock { Text = T("Context lines"), VerticalAlignment = VerticalAlignment.Center }, _context, _entire, _asText } };
        DockPanel.SetDock(diffOptions, Dock.Top); Root.Children.Add(diffOptions);
        _fileTool = Action("ComparisonFileTool", "Open with diff tool", () => OpenToolAsync(false));
        _saveBase = Action("ComparisonSaveBase", "Save BASE as…", () => SaveAsAsync(true));
        _saveCompare = Action("ComparisonSaveCompare", "Save Compare as…", () => SaveAsAsync(false));
        Button close = new() { Name = "CloseComparison", Content = T("Close"), IsCancel = true }; close.Click += (_, _) => Close();
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 4), Children = { _fileTool, _saveBase, _saveCompare, close } };
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        _filter.Watermark = T("Filter files");
        DockPanel files = new(); DockPanel.SetDock(_filter, Dock.Top); files.Children.Add(_filter); files.Children.Add(_files);
        _files.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(HeightProperty, 24d), new Setter(MinHeightProperty, 24d), new Setter(PaddingProperty, new Thickness(2, 0)) } });
        _files.ItemTemplate = new FuncDataTemplate<GitRevisionFile>((file, _) => FileRow(file));
        _tabs.ItemsSource = new[]
        {
            new TabItem { Name = "ComparisonDiffTab", Header = T("Diff"), Content = _diff },
            new TabItem { Name = "ComparisonBaseTab", Header = T("BASE"), Content = _baseView },
            new TabItem { Name = "ComparisonCompareTab", Header = T("Compare"), Content = _compareView }
        };
        _tabs.SelectedIndex = 0;
        _find.Width = 180; _find.Watermark = T("Find");
        DockPanel viewer = new();
        StackPanel search = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 2), Children =
        { IconAction("ComparisonPreviousChange", "ArrowUp.png", "Previous change (Alt+Up)", () => MoveChangeAsync(false)),
          IconAction("ComparisonNextChange", "ArrowDown.png", "Next change (Alt+Down)", () => MoveChangeAsync(true)), _find,
          Action("ComparisonFindPrevious", "Previous", () => { Find(false); return Task.CompletedTask; }), Action("ComparisonFindNext", "Next", () => { Find(true); return Task.CompletedTask; }) } };
        DockPanel.SetDock(search, Dock.Top); viewer.Children.Add(search);
        _fileCaption.Margin = new Thickness(5, 2); DockPanel.SetDock(_fileCaption, Dock.Top); viewer.Children.Add(_fileCaption); viewer.Children.Add(_tabs);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("300,4,*"), Margin = new Thickness(6, 0, 6, 4) };
        body.Children.Add(ClassicChrome.Border(files, new Thickness(1)));
        GridSplitter splitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        Grid.SetColumn(viewer, 2); body.Children.Add(viewer); Root.Children.Add(body);
        Track(_base, _compare, _mergeBase, _whitespace, _context, _entire, _asText, _filter, _find, _files, _tabs);
        _base.Edited += (_, _) => InputsEdited(); _compare.Edited += (_, _) => InputsEdited();
        _base.BranchButton.Click += async (_, _) => await ChooseBranchAsync(_base);
        _compare.BranchButton.Click += async (_, _) => await ChooseBranchAsync(_compare);
        _base.CommitButton.Click += async (_, _) => await ChooseCommitAsync(_base);
        _compare.CommitButton.Click += async (_, _) => await ChooseCommitAsync(_compare);
        _mergeBase.IsCheckedChanged += async (_, _) => { if (!_loading && !IsBusy) { await InitializeAsync(); } };
        _whitespace.SelectionChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _context.ValueChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _entire.IsCheckedChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _asText.IsCheckedChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _files.SelectionChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _tabs.SelectionChanged += async (_, args) => { if (args.Source == _tabs && !_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _filter.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty && !_loading) { ApplyFileFilter(); } };
        _find.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; Find(args.KeyModifiers != KeyModifiers.Shift); } };
        AddHandler(KeyDownEvent, async (_, args) => await HandleKeyAsync(args), RoutingStrategies.Tunnel);
        _diff.TextView.DisplayOptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 0) { await RequestDiffReloadAsync(LoadSelectionAsync); }
        };
        Closed += (_, _) => { _base.Dispose(); _compare.Dispose(); _diff.Dispose(); _baseView.Dispose(); _compareView.Dispose(); foreach (Bitmap icon in _icons) { icon.Dispose(); } };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Compare revisions", async token =>
    {
        _loading = true;
        try
        {
            _bare = (await Repository.GetInfoAsync(token)).IsBare;
            _base.SetBare(_bare); _compare.SetBare(_bare);
            if (_base.Read().Kind == GitCompareRevisionKind.Commit) { _base.SetSummary(await ReferenceDialogControls.SummaryAsync(Repository, _base.Read().Revision, token)); }
            if (_compare.Read().Kind == GitCompareRevisionKind.Commit) { _compare.SetSummary(await ReferenceDialogControls.SummaryAsync(Repository, _compare.Read().Revision, token)); }
            if (!_mergeBaseLoaded)
            {
                _fixedMergeBase = await Repository.GetComparisonMergeBaseAsync(_base.Read(), _compare.Read(), token);
                _mergeBaseLoaded = true;
                _mergeBase.Content = T("Compare to merge base") + (_fixedMergeBase is null ? "" : " (" + _fixedMergeBase[..Math.Min(9, _fixedMergeBase.Length)] + ")");
            }
            await ReadComparisonAsync(token);
        }
        finally { _loading = false; }
    });

    private async Task ReadComparisonAsync(CancellationToken token)
    {
        _comparison = await Repository.GetComparisonAsync(_mergeBase.IsChecked == true && _fixedMergeBase is not null ? new(_fixedMergeBase) : _base.Read(), _compare.Read(), token);
        ApplyFileFilter();
        await ReadSelectedAsync(token);
        Status.Text = _comparison.Files.Count + " " + T("files") + (_comparison.Files.Count == 0 ? " — " + T("No differences") : "");
    }

    private void ApplyFileFilter()
    {
        string? selected = SelectedFile?.Path;
        string search = _filter.Text ?? "";
        _files.ItemsSource = _comparison?.Files.Where(file => file.Path.Contains(search, StringComparison.OrdinalIgnoreCase) || file.OriginalPath?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToArray() ?? [];
        _files.SelectedItem = _files.Items.OfType<GitRevisionFile>().FirstOrDefault(file => file.Path == selected) ?? _files.Items.OfType<GitRevisionFile>().FirstOrDefault();
        UpdateActions();
    }

    private Task LoadSelectionAsync() => RunAsync("Load comparison file", ReadSelectedAsync);
    private async Task ReadSelectedAsync(CancellationToken token)
    {
        GitRevisionFile? file = SelectedFile;
        TabItem[] tabs = _tabs.Items.OfType<TabItem>().ToArray();
        tabs[1].IsEnabled = file is not null && file.Status != 'A';
        tabs[2].IsEnabled = file is not null && !file.IsDeleted;
        if (_tabs.SelectedIndex == 1 && !tabs[1].IsEnabled || _tabs.SelectedIndex == 2 && !tabs[2].IsEnabled) { _tabs.SelectedIndex = 0; }
        _fileCaption.Text = file is null ? T("No differences") : ExportDialogControls.DisplayPath(file.OriginalPath is null ? file.Path : file.OriginalPath + " → " + file.Path);
        if (file is null || _comparison is null) { _diff.ShowDiff(T("No differences")); _baseView.ShowDiff(""); _compareView.ShowDiff(""); return; }
        _diff.TextView.DocumentIdentity = string.Join("\0", Repository.Path, file.Path, file.OriginalPath, _comparison.Base, _comparison.Compare, "diff");
        _baseView.TextView.DocumentIdentity = string.Join("\0", Repository.Path, file.OriginalPath ?? file.Path, _comparison.Base, "blob");
        _compareView.TextView.DocumentIdentity = string.Join("\0", Repository.Path, file.Path, _comparison.Compare, "blob");
        if (_tabs.SelectedIndex == 0)
        {
            _diff.TextView.BeginDocumentLoad();
            await _diff.TextView.ConfigureRepositoryAsync(Repository, token);
            _diff.ShowDiff(await Repository.GetComparisonFileDiffDocumentAsync(_comparison, file, ReadDiffOptions(), _diff.TextView.DisplayOptions, token));
        }
        else if (_tabs.SelectedIndex == 1) { _baseView.ShowFile(file.OriginalPath ?? file.Path, await Repository.GetComparisonFileBytesAsync(_comparison.Base, file.OriginalPath ?? file.Path, token), await Repository.GetFileEncodingNameAsync(token)); }
        else { _compareView.ShowFile(file.Path, await Repository.GetComparisonFileBytesAsync(_comparison.Compare, file.Path, token), await Repository.GetFileEncodingNameAsync(token)); }
    }
    public GitComparisonDiffOptions ReadDiffOptions() => new((IgnoreWhitespaceKind)Math.Max(0, _whitespace.SelectedIndex), (int)(_context.Value ?? 3), _entire.IsChecked == true, _asText.IsChecked == true);

    private void InputsEdited()
    {
        if (_loading || IsBusy) { return; }
        _comparison = null; _files.ItemsSource = null;
        _diff.ShowDiff(T("Choose revisions and press Compare.")); _baseView.ShowDiff(""); _compareView.ShowDiff("");
        Status.Text = T("Choose revisions and press Compare."); UpdateActions();
    }
    private async Task SwapAsync()
    {
        _loading = true;
        try { GitCompareRevision first = _base.Read(); _base.Set(_compare.Read()); _compare.Set(first); }
        finally { _loading = false; }
        await InitializeAsync();
    }
    private async Task ChooseBranchAsync(ComparisonRevisionSelector selector)
    {
        GitReference? chosen = await CompareBranchPicker.ShowAsync(this, Repository, selector.Read(), selector.Read().Revision);
        if (chosen is null) { return; }
        _loading = true; try { selector.Set(new(chosen.Name)); } finally { _loading = false; }
        await InitializeAsync();
    }
    private async Task ChooseCommitAsync(ComparisonRevisionSelector selector)
    {
        GitCommit? chosen = await ExportRevisionPicker.ShowAsync(this, Repository);
        if (chosen is null) { return; }
        _loading = true; try { selector.Set(new(chosen.Hash)); } finally { _loading = false; }
        await InitializeAsync();
    }
    private Task OpenToolAsync(bool directory) => RunAsync("Open with diff tool", async token =>
    {
        if (_comparison is null) { return; }
        try { await Repository.OpenComparisonDiffToolAsync(_comparison, directory ? null : SelectedFile, directory, token); }
        finally { MarkRepositoryChanged(); }
        _loading = true; try { await ReadComparisonAsync(token); } finally { _loading = false; }
    });
    public async Task SaveSelectedFileAsync(string destination, bool baseSide = false, CancellationToken cancellationToken = default)
    {
        GitRevisionFile file = SelectedFile ?? throw new InvalidOperationException(T("Select a file first."));
        GitComparison comparison = _comparison ?? throw new InvalidOperationException(T("Compare revisions first."));
        if (baseSide ? file.Status == 'A' : file.IsDeleted) { throw new InvalidOperationException(T("The file does not exist in this revision.")); }
        byte[] bytes = await Repository.GetComparisonFileBytesAsync(baseSide ? comparison.Base : comparison.Compare, baseSide ? file.OriginalPath ?? file.Path : file.Path, cancellationToken);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, cancellationToken);
    }
    private async Task SaveAsAsync(bool baseSide)
    {
        if (SelectedFile is null) { return; }
        IStorageFile? output = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("Save as"), ShowOverwritePrompt = false, SuggestedFileName = Path.GetFileName(baseSide ? SelectedFile.OriginalPath ?? SelectedFile.Path : SelectedFile.Path) });
        if (output?.TryGetLocalPath() is string path) { await RunAsync("Save as", token => SaveSelectedFileAsync(path, baseSide, token)); }
    }

    private FileHistoryContentView ActiveView => _tabs.SelectedIndex == 1 ? _baseView : _tabs.SelectedIndex == 2 ? _compareView : _diff;
    private void Find(bool forward)
    {
        DiffView view = ActiveView.TextView;
        string query = _find.Text ?? "";
        if (query.Length == 0 || ActiveView.IsImage) { return; }
        int start = forward ? Math.Min(view.Text.Length, view.SelectionStart + view.SelectionLength) : Math.Max(0, view.SelectionStart - 1);
        int found = forward ? view.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase) : view.Text.LastIndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (found < 0) { found = forward ? view.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) : view.Text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase); }
        if (found < 0) { Status.Text = T("Text not found"); return; }
        view.Select(found, query.Length); view.Editor.ScrollToLine(view.Editor.Document.GetLineByOffset(found).LineNumber);
    }
    private async Task MoveChangeAsync(bool forward)
    {
        if (_tabs.SelectedIndex != 0 || _comparison is null || SelectedFile is null) { return; }
        await RunAsync(forward ? "Next change" : "Previous change", async token =>
        {
            DiffView view = _diff.TextView;
            var hunks = view.Editor.Document.Lines.Where(line => view.Editor.Document.GetText(line).StartsWith("@@ ", StringComparison.Ordinal)).ToArray();
            AvaloniaEdit.Document.DocumentLine? next = forward ? hunks.FirstOrDefault(line => line.Offset > view.CaretOffset) : hunks.LastOrDefault(line => line.Offset < view.CaretOffset);
            if (next is null)
            {
                int index = _files.SelectedIndex + (forward ? 1 : -1);
                if (index < 0 || index >= _files.Items.Count) { return; }
                _loading = true; try { _files.SelectedIndex = index; } finally { _loading = false; }
                await ReadSelectedAsync(token);
                next = forward ? view.Editor.Document.Lines.FirstOrDefault(line => view.Editor.Document.GetText(line).StartsWith("@@ ", StringComparison.Ordinal))
                    : view.Editor.Document.Lines.LastOrDefault(line => view.Editor.Document.GetText(line).StartsWith("@@ ", StringComparison.Ordinal));
            }
            if (next is not null) { view.Select(next.Offset, 0); view.Editor.ScrollToLine(next.LineNumber); }
        });
        if (!IsBusy) { _diff.TextView.Editor.Focus(); }
    }
    private async Task HandleKeyAsync(KeyEventArgs args)
    {
        if (args.Handled || IsBusy) { return; }
        if (ActiveView.TextView.HandleReadingShortcut(args) || ActiveView.TextView.AppearanceMenu.HandleKeyDown(args)) { return; }
        if (args.Key == Key.F && args.KeyModifiers == KeyModifiers.Control) { _find.Focus(); _find.SelectAll(); args.Handled = true; }
        else if (args.Key == Key.F3) { Find(args.KeyModifiers != KeyModifiers.Shift); args.Handled = true; }
        else if (args.Key is Key.Up or Key.Down && args.KeyModifiers == KeyModifiers.Alt) { await MoveChangeAsync(args.Key == Key.Down); args.Handled = true; }
        else if (args.Key == Key.E && args.KeyModifiers == KeyModifiers.Control) { _entire.IsChecked = _entire.IsChecked != true; args.Handled = true; }
        else if (args.Key == Key.W && args.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) { _whitespace.SelectedIndex = _whitespace.SelectedIndex == 3 ? 0 : 3; args.Handled = true; }
    }
    protected override void UpdateActions()
    {
        _mergeBase.IsEnabled = !IsBusy && _fixedMergeBase is not null;
        _directoryTool.IsEnabled = !IsBusy && !_bare && _comparison is not null && _comparison.Base.Kind != GitCompareRevisionKind.WorkingTree && _comparison.Files.Count > 0;
        _fileTool.IsEnabled = !IsBusy && !_bare && SelectedFile is not null && _comparison is not null && !(_comparison.UntrackedPaths.Contains(SelectedFile.Path, StringComparer.Ordinal));
        _saveBase.IsEnabled = !IsBusy && _comparison is not null && SelectedFile is { Status: not 'A' };
        _saveCompare.IsEnabled = !IsBusy && _comparison is not null && SelectedFile is { IsDeleted: false };
        _context.IsEnabled = !IsBusy && _entire.IsChecked != true;
    }
    private Control FileRow(GitRevisionFile? file)
    {
        if (file is null) { return new TextBlock(); }
        if (!_fileIcons.TryGetValue(file.Status, out Bitmap? icon))
        {
            string name = file.Status switch { 'A' => "FileStatusAdded.png", 'D' => "FileStatusRemoved.png", 'R' => "FileStatusRenamed.png", 'C' => "FileStatusCopied.png", _ => "FileStatusModified.png" };
            using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + name));
            icon = new(stream); _icons.Add(icon); _fileIcons.Add(file.Status, icon);
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Height = 24, Children =
        { new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
          new TextBlock { Text = ExportDialogControls.DisplayPath(file.OriginalPath is null ? file.Path : file.OriginalPath + " → " + file.Path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } } };
    }
    private Button IconAction(string name, string file, string tip, Func<Task> action)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + file));
        Bitmap icon = new(stream); _icons.Add(icon);
        Button button = Action(name, "", action);
        button.Content = new Image { Source = icon, Width = 16, Height = 16 }; button.Width = button.Height = 24; button.Padding = new Thickness(2);
        ToolTip.SetTip(button, T(tip)); return button;
    }
}
