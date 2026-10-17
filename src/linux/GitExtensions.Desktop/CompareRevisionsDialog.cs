using GitExtensions.Linux.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
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

public sealed partial class CompareRevisionsDialog : RepositoryDialog
{
    private readonly ComparisonRevisionSelector _base;
    private readonly ComparisonRevisionSelector _compare;
    private readonly CheckBox _mergeBase = new() { Name = "CompareToMergeBase" };
    private readonly TextBlock _fileCaption = new() { Name = "ComparisonSelectedPath", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TreeView _files = new() { Name = "ComparisonFiles", SelectionMode = SelectionMode.Multiple };
    private readonly FileStatusListView _filesView;
    private readonly TabControl _tabs = new() { Name = "ComparisonTabs" };
    private readonly FileHistoryContentView _diff = new() { Name = "ComparisonDiff" };
    private readonly FileHistoryContentView _baseView = new() { Name = "ComparisonBaseView" };
    private readonly FileHistoryContentView _compareView = new() { Name = "ComparisonCompareView" };
    private readonly Button _directoryTool;
    private readonly Button _fileTool;
    private readonly Button _saveBase;
    private readonly Button _saveCompare;
    private readonly List<Bitmap> _icons = [];
    private GitComparison? _comparison;
    private string? _fixedMergeBase;
    private bool _mergeBaseLoaded;
    private bool _loading;
    private bool _bare;
    public GitComparison? Comparison => _comparison;
    public string? MergeBaseRevision => _fixedMergeBase;

    public CompareRevisionsDialog(GitRepository repository, GitCompareRevision baseRevision, GitCompareRevision compareRevision, FileStatusListPreferencesStore? fileListPreferences = null)
        : this(repository, baseRevision?.Revision ?? throw new ArgumentNullException(nameof(baseRevision)), compareRevision?.Revision ?? throw new ArgumentNullException(nameof(compareRevision)), fileListPreferences)
    {
        _loading = true;
        try { _base.Set(baseRevision); _compare.Set(compareRevision); }
        finally { _loading = false; }
    }

    public CompareRevisionsDialog(GitRepository repository, string baseRevision = "HEAD~1", string compareRevision = "HEAD", FileStatusListPreferencesStore? fileListPreferences = null) : base(repository, "Diff")
    {
        Width = 1100; Height = 720; MinWidth = 800; MinHeight = 440;
        _base = new("ComparisonBase", true, baseRevision, T);
        _compare = new("ComparisonCompare", false, compareRevision, T);
        Button swap = IconAction("ComparisonSwap", "Renamed.png", "Swap BASE and Compare commits", SwapAsync);
        Grid revisions = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(6, 4) };
        revisions.Children.Add(_base); swap.Margin = new Thickness(6, 14, 6, 0); Grid.SetColumn(swap, 1); revisions.Children.Add(swap); Grid.SetColumn(_compare, 2); revisions.Children.Add(_compare);
        DockPanel.SetDock(revisions, Dock.Top); Root.Children.Add(revisions);
        _mergeBase.Content = T("Compare to merge base");
        ReferenceDialogControls.Compact(_mergeBase);
        _directoryTool = Action("ComparisonDirectoryTool", "Open diff using directory diff tool", () => OpenToolAsync(true));
        Button load = Action("CompareRevisions", "Compare", InitializeAsync); load.IsDefault = true;
        StackPanel compareOptions = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(9, 0, 9, 4), Children = { _mergeBase, _directoryTool, load } };
        DockPanel.SetDock(compareOptions, Dock.Top); Root.Children.Add(compareOptions);
        _fileTool = Action("ComparisonFileTool", "Open with diff tool", () => OpenToolAsync(false));
        _saveBase = Action("ComparisonSaveBase", "Save BASE as…", () => SaveAsAsync(true));
        _saveCompare = Action("ComparisonSaveCompare", "Save Compare as…", () => SaveAsAsync(false));
        Button close = new() { Name = "CloseComparison", Content = T("Close"), IsCancel = true }; close.Click += (_, _) => Close();
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 4), Children = { _fileTool, _saveBase, _saveCompare, close } };
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        _filesView = new FileStatusListView(_files, preferencesStore: fileListPreferences) { Name = "ComparisonFileStatusList" };
        InitializeComparisonGroups();
        _tabs.ItemsSource = new[]
        {
            new TabItem { Name = "ComparisonDiffTab", Header = T("Diff"), Content = _diff },
            new TabItem { Name = "ComparisonBaseTab", Header = T("BASE"), Content = _baseView },
            new TabItem { Name = "ComparisonCompareTab", Header = T("Compare"), Content = _compareView }
        };
        _tabs.SelectedIndex = 0;
        DockPanel viewer = new();
        _diff.Toolbar.SetNames("Comparison");
        _diff.Toolbar.NextChangeRequested = () => MoveChangeAsync(true);
        _diff.Toolbar.PreviousChangeRequested = () => MoveChangeAsync(false);
        MenuItem asText = new() { Name = "ComparisonTreatAsText", Header = T("Treat all files as text"), ToggleType = MenuItemToggleType.CheckBox };
        asText.Click += (_, _) => _diff.TextView.ComparisonMenu.SetOptions(ReadDiffOptions() with { TreatAllFilesAsText = asText.IsChecked });
        _diff.TextView.ContextMenu = new ContextMenu { ItemsSource = new object[] { asText }.Concat(_diff.TextView.CreateContextMenuItems("Comparison")).ToArray() };
        _diff.TextView.ComparisonMenu.OptionsChanged += async (_, _) =>
        {
            asText.IsChecked = ReadDiffOptions().TreatAllFilesAsText;
            if (_tabs.SelectedIndex == 0) { await RequestDiffReloadAsync(ReloadSelectionAsync); }
        };
        _diff.TextView.Search.OpenDiffToolRequested = () => OpenToolAsync(false);
        _fileCaption.Margin = new Thickness(5, 2); DockPanel.SetDock(_fileCaption, Dock.Top); viewer.Children.Add(_fileCaption); viewer.Children.Add(_tabs);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("300,4,*"), Margin = new Thickness(6, 0, 6, 4) };
        body.Children.Add(ClassicChrome.Border(_filesView, new Thickness(1)));
        GridSplitter splitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        Grid.SetColumn(viewer, 2); body.Children.Add(viewer); Root.Children.Add(body);
        Track(_base, _compare, _mergeBase, _filesView, _files, _tabs);
        _base.Edited += (_, _) => InputsEdited(); _compare.Edited += (_, _) => InputsEdited();
        _base.BranchButton.Click += async (_, _) => await ChooseBranchAsync(_base);
        _compare.BranchButton.Click += async (_, _) => await ChooseBranchAsync(_compare);
        _base.CommitButton.Click += async (_, _) => await ChooseCommitAsync(_base);
        _compare.CommitButton.Click += async (_, _) => await ChooseCommitAsync(_compare);
        _mergeBase.IsCheckedChanged += async (_, _) => { if (!_loading && !IsBusy) { await InitializeAsync(); } };
        _files.SelectionChanged += async (_, args) => { if (args.Source == _files && !_loading && !_filesView.IsUpdating && !IsBusy) { await LoadSelectionAsync(); } };
        _filesView.DisplayChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadSelectionAsync(); } };
        _tabs.SelectionChanged += async (_, args) => { if (args.Source == _tabs && !_loading && !IsBusy) { await LoadSelectionAsync(); } };
        AddHandler(KeyDownEvent, async (_, args) => await HandleKeyAsync(args), RoutingStrategies.Tunnel);
        _diff.TextView.DisplayOptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 0) { await RequestDiffReloadAsync(ReloadSelectionAsync); }
        };
        InitializeSelectionLifetime();
        InitializeFileMenus();
        Closed += (_, _) => { _filesView.Dispose(); _base.Dispose(); _compare.Dispose(); _diff.Dispose(); _baseView.Dispose(); _compareView.Dispose(); foreach (Bitmap icon in _icons) { icon.Dispose(); } };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunComparisonAsync("Compare revisions", async token =>
    {
        _loading = true;
        try
        {
            _bare = (await Repository.GetInfoAsync(token)).IsBare;
            if (!_headCaptured) { _capturedHead = (await Repository.GetStatusAsync(token)).Head; _headCaptured = true; }
            _base.SetBare(_bare); _compare.SetBare(_bare);
            if (_base.Read().Kind == GitCompareRevisionKind.Commit) { _base.SetSummary(await ReferenceDialogControls.SummaryAsync(Repository, _base.Read().Revision, token)); }
            if (_compare.Read().Kind == GitCompareRevisionKind.Commit) { _compare.SetSummary(await ReferenceDialogControls.SummaryAsync(Repository, _compare.Read().Revision, token)); }
            if (!_mergeBaseLoaded)
            {
                GitCompareRevision? first = MergeRevision(_base.Read()), second = MergeRevision(_compare.Read());
                _fixedMergeBase = first is null || second is null ? null : await Repository.GetComparisonMergeBaseAsync(first, second, token);
                _mergeBaseLoaded = true;
                _mergeBase.Content = T("Compare to merge base") + (_fixedMergeBase is null ? "" : " (" + _fixedMergeBase[..Math.Min(9, _fixedMergeBase.Length)] + ")");
            }
            await ReadComparisonAsync(token);
        }
        finally { _loading = false; }
    });

    private async Task ReadComparisonAsync(CancellationToken token)
    {
        IReadOnlyList<GitComparisonGroup> groups = await Repository.GetComparisonGroupsAsync(
            _mergeBase.IsChecked == true && _fixedMergeBase is not null ? new(_fixedMergeBase) : _base.Read(), _compare.Read(),
            _capturedHead, _groupPreferences.Load().ShowDiffForAllParents, token);
        IReadOnlyDictionary<GitComparisonGroup, IReadOnlyList<GitComparisonFileMetadata>> metadata = await ReadFileMenuMetadataAsync(groups, token);
        token.ThrowIfCancellationRequested();
        _comparisonGroups = groups; _fileMenuMetadata = metadata;
        _comparison = _comparisonGroups.FirstOrDefault()?.Comparison;
        SetFileGroups();
        await ReadSelectedAsync(token);
        if (LastError is null) { int count = _comparison?.Files.Count ?? 0; Status.Text = count + " " + T("files") + (count == 0 ? " — " + T("No differences") : ""); }
    }

    public GitComparisonDiffOptions ReadDiffOptions() => _diff.TextView.ComparisonMenu.Options;

    private void InputsEdited()
    {
        if (_loading || IsBusy) { return; }
        CancelSelectionRead(); _loadedSelection = null;
        _comparison = null; _comparisonGroups = []; _groupsByKey.Clear(); _filesView.SetGroups([]);
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
    private Task OpenToolAsync(bool directory) => RunComparisonAsync("Open with diff tool", async token =>
    {
        GitComparison? comparison = directory ? _comparison : SelectedComparison;
        if (comparison is null || !directory && SelectedFile is null) { return; }
        try { await Repository.OpenComparisonDiffToolAsync(comparison, directory ? null : SelectedFile, directory, token); }
        finally { MarkRepositoryChanged(); }
        _loading = true; try { await ReadComparisonAsync(token); } finally { _loading = false; }
    });
    public async Task SaveSelectedFileAsync(string destination, bool baseSide = false, CancellationToken cancellationToken = default)
    {
        GitRevisionFile file = SelectedFile ?? throw new InvalidOperationException(T("Select a file first."));
        GitComparison comparison = SelectedComparison ?? throw new InvalidOperationException(T("Compare revisions first."));
        if (baseSide ? file.Status == 'A' : file.IsDeleted) { throw new InvalidOperationException(T("The file does not exist in this revision.")); }
        byte[] bytes = await Repository.GetComparisonFileBytesAsync(baseSide ? comparison.Base : comparison.Compare, baseSide ? file.OriginalPath ?? file.Path : file.Path, cancellationToken);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, cancellationToken);
    }
    private async Task SaveAsAsync(bool baseSide)
    {
        if (SelectedFile is null) { return; }
        IStorageFile? output = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("Save as"), ShowOverwritePrompt = false, SuggestedFileName = Path.GetFileName(baseSide ? SelectedFile.OriginalPath ?? SelectedFile.Path : SelectedFile.Path) });
        if (output?.TryGetLocalPath() is string path) { await RunComparisonAsync("Save as", token => SaveSelectedFileAsync(path, baseSide, token)); }
    }

    private FileHistoryContentView ActiveView => _tabs.SelectedIndex == 1 ? _baseView : _tabs.SelectedIndex == 2 ? _compareView : _diff;
    private async Task MoveChangeAsync(bool forward)
    {
        if (_tabs.SelectedIndex != 0 || _comparison is null || SelectedFile is null) { return; }
        await RunComparisonAsync(forward ? "Next change" : "Previous change", async token =>
        {
            await ReadSelectedAsync(token);
            DiffView view = _diff.TextView;
            var hunks = view.Editor.Document.Lines.Where(line => view.Editor.Document.GetText(line).StartsWith("@@ ", StringComparison.Ordinal)).ToArray();
            AvaloniaEdit.Document.DocumentLine? next = forward ? hunks.FirstOrDefault(line => line.Offset > view.CaretOffset) : hunks.LastOrDefault(line => line.Offset < view.CaretOffset);
            if (next is null)
            {
                TreeViewItem[] files = FileItems(_files.Items).ToArray();
                int index = Array.FindIndex(files, item => ReferenceEquals(item, _files.SelectedItem)) + (forward ? 1 : -1);
                if (index < 0 || index >= files.Length) { return; }
                _loading = true; try { _filesView.SelectTags([files[index].Tag!]); files[index].BringIntoView(); } finally { _loading = false; }
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
        if (_fileMenuController is not null && await _fileMenuController.HandleShortcutAsync(args)) { return; }
        if (ActiveView.TextView.HandleReadingShortcut(args)
            || ActiveView.TextView.AppearanceMenu.HandleKeyDown(args)) { return; }
        if (args.Key is Key.Up or Key.Down && args.KeyModifiers == KeyModifiers.Alt) { await MoveChangeAsync(args.Key == Key.Down); args.Handled = true; }
    }
    protected override void UpdateActions()
    {
        _allParents.IsEnabled = !IsBusy;
        _mergeBase.IsEnabled = !IsBusy && _fixedMergeBase is not null;
        _directoryTool.IsEnabled = !IsBusy && !_bare && _comparison is not null && _comparison.Base.Kind != GitCompareRevisionKind.WorkingTree && _comparison.Files.Count > 0;
        _fileTool.IsEnabled = !IsBusy && !_bare && SelectedFile is not null && SelectedComparison is not null && !(SelectedComparison.UntrackedPaths.Contains(SelectedFile.Path, StringComparer.Ordinal));
        _saveBase.IsEnabled = !IsBusy && SelectedComparison is not null && SelectedFile is { Status: not 'A' };
        _saveCompare.IsEnabled = !IsBusy && SelectedComparison is not null && SelectedFile is { IsDeleted: false };
        _diff.TextView.ComparisonMenu.SetHostBusy(IsBusy);
        _diff.TextView.RenderingMenu.SetHostBusy(IsBusy);
    }
    private static IEnumerable<TreeViewItem> FileItems(System.Collections.IEnumerable items)
    {
        foreach (TreeViewItem item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is ComparisonFileSelection) { yield return item; }
            foreach (TreeViewItem child in FileItems(item.Items)) { yield return child; }
        }
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
