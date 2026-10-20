using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>The six lost-object workflows from FormVerify, separate from statistics and garbage collection.</summary>
public sealed class RecoverLostObjectsDialog : RepositoryDialog
{
    private readonly CheckBox _commits = new() { Name = "RecoveryShowCommits", IsChecked = true };
    private readonly CheckBox _other = new() { Name = "RecoveryShowOther" };
    private readonly CheckBox _noReflogs = new() { Name = "RecoveryNoReflogs", IsChecked = true };
    private readonly CheckBox _unreachable = new() { Name = "RecoveryUnreachable" };
    private readonly CheckBox _full = new() { Name = "RecoveryFullCheck" };
    private readonly CheckBox _all = new() { Name = "RecoveryCheckAll", IsThreeState = true, Padding = new Thickness(0) };
    private readonly ListBox _objects = new() { Name = "RecoveryObjects", SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly FileHistoryContentView _preview = new() { Name = "RecoveryPreview", ClipToBounds = true };
    private readonly TextBlock _previewError = new() { Name = "RecoveryPreviewError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox _report = new() { Name = "RecoveryReport", IsReadOnly = true, AcceptsReturn = true, Height = 110 };
    private readonly Grid _header = new() { Name = "RecoveryHeader", Height = 30 };
    private readonly Grid _table = new() { RowDefinitions = new("Auto,*"), MinWidth = 700, ClipToBounds = true };
    private readonly Button _recover, _delete, _prune, _saveObjects;
    private readonly MenuItem _view, _tag, _branch, _copy, _copyParent, _save;
    private readonly List<Bitmap> _icons = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _annotationCancellation;
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private List<RecoveryRow> _rows = [], _displayed = [];
    private string _columns = "24,90,160,*,150,60,60";
    private int _sortColumn = 1;
    private bool _descending = true, _filtering, _presentingRows, _initialized, _closing;
    private long _previewVersion;
    private long _scanVersion;
    public GitObjectRecoveryReport? LastScan { get; private set; }
    public GitObjectRecoveryResult? LastRecovery { get; private set; }
    public GitObjectPreview? Preview { get; private set; }
    public GitLostObject? SelectedObject => (_objects.SelectedItem as RecoveryRow)?.Object;
    public IReadOnlyList<GitLostObject> VisibleObjects => _displayed.Select(row => row.Object).ToArray();
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SaveFilePicker { get; set; }

    public RecoverLostObjectsDialog(GitRepository repository, bool autoNormalize = true, string replacement = "_") : base(repository, "Verify database")
    {
        DesktopFonts.Apply(_report, DesktopFontRole.Code);
        Name = "RecoverLostObjectsDialog"; Title = Text("$this", "Verify database"); Width = 859; Height = 575; MinWidth = 760; MinHeight = 420;
        _autoNormalize = autoNormalize; _replacement = replacement; ConstrainStatusHeight("RecoveryStatus", 54);
        _commits.Content = Caption("ShowCommitsAndTags", "Show commits and annotated tags"); _other.Content = Caption("ShowOtherObjects", "Show blobs and trees");
        _noReflogs.Content = Caption("NoReflogs", "Do not consider commits that are referenced only by an entry in a \r\nreflog to be reachable.");
        _unreachable.Content = Caption("Unreachable", "Print out objects that exist but that aren't readable from any of the reference \r\nnodes.\r\n");
        _full.Content = Caption("FullCheck", "Check not just objects in GIT_OBJECT_DIRECTORY ($GIT_DIR/objects), \r\nbut also the ones found in alternate object pools.\r\n");
        foreach (CheckBox box in new[] { _commits, _other, _noReflogs, _unreachable, _full }) { box.Padding = new Thickness(0); box.MinHeight = 24; }
        ToolTip.SetTip(_commits, Text("ShowCommitsAndTags", "To recover unreachable commits or annotated tags", "toolTip"));
        ToolTip.SetTip(_other, Text("ShowOtherObjects", "To recover contents of files once staged but mistakenly deleted", "toolTip"));
        Grid instructions = new() { Name = "RecoveryInstructions", ColumnDefinitions = new("414,*"), Margin = new Thickness(8, 4) };
        instructions.Children.Add(new StackPanel { Spacing = 5, Children =
        {
            new TextBlock { Name = "RecoveryHelp", Text = Text("label2", "By default only unreferenced objects that are older than \n2 weeks are removed when cleaning up the database. All\nother object are only deleted when you run \"Remove all\ndangling objects\"\n\nCheck commits you want to recover and press Recover button\nContext menu for additional operations"), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Name = "RecoveryQuickViewHelp", Text = Text("label1", "Double-click on a row for quick view"), TextWrapping = TextWrapping.Wrap }
        } });
        StackPanel options = new() { Spacing = 2, Margin = new Thickness(8, 0, 0, 0), Children = { new WrapPanel { Orientation = Orientation.Horizontal, Children = { _commits, _other } }, _noReflogs, _unreachable, _full } };
        _commits.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(options, 1); instructions.Children.Add(options);
        DockPanel.SetDock(instructions, Dock.Top); Root.Children.Add(instructions);

        _prune = Action("RecoveryRemoveDangling", "Remove all dangling objects", PruneAsync); _prune.Content = Caption("Remove", "Remove all dangling objects");
        _delete = Action("RecoveryDeleteTags", "Delete all LOST_AND_FOUND tags", DeleteTagsAsync); _delete.Content = Caption("DeleteAllLostAndFoundTags", "Delete all LOST_AND_FOUND tags");
        _recover = Action("RecoveryRecoverSelected", "Recover selected objects", RecoverAsync); _recover.Content = Caption("btnRestoreSelectedObjects", "Recover selected objects");
        _saveObjects = Action("RecoverySaveLostFound", "Save objects to .git/lost-found", SaveLostFoundAsync); _saveObjects.Content = Caption("SaveObjects", "Save objects to .git/lost-found");
        Button cancel = new() { Name = "CancelRecovery", Content = Caption("btnCloseDialog", "Cancel"), IsCancel = true, Height = 25, MinHeight = 0 }; cancel.Click += (_, _) => Close();
        Grid actions = new() { Name = "RecoveryActions", ColumnDefinitions = new("252,12,*,12,208"), RowDefinitions = new("25,2,25"), Margin = new Thickness(3, 6) };
        actions.Children.Add(_prune); Grid.SetRow(_delete, 2); actions.Children.Add(_delete);
        Grid.SetColumn(_recover, 2); Grid.SetRowSpan(_recover, 3); _recover.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(_recover);
        Grid.SetColumn(_saveObjects, 4); actions.Children.Add(_saveObjects); Grid.SetColumn(cancel, 4); Grid.SetRow(cancel, 2); actions.Children.Add(cancel);
        foreach (Button button in new[] { _prune, _delete, _recover, _saveObjects }) { button.Height = 25; button.MinHeight = 0; button.HorizontalAlignment = HorizontalAlignment.Stretch; }
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        Expander report = new() { Name = "RecoveryReportExpander", Header = T("Command output"), Content = _report, HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(report, Dock.Bottom); Root.Children.Add(report);

        _all.MinWidth = 20; _all.MinHeight = 24; _all.HorizontalAlignment = HorizontalAlignment.Center;
        _all.Click += (_, _) => { bool check = _displayed.Any(row => !row.Checked); foreach (RecoveryRow row in _displayed) { row.Checked = check; } PresentRows(); };
        _header.Children.Add(_all);
        string[] names = ["Date", "Type", "Subject", "Author", "Hash", "Parent"], labels = ["Date", "Type", "Subject", "Author", "Hash", "Parent(s) hashs"];
        for (int index = 0; index < names.Length; index++)
        {
            int column = index + 1;
            Button heading = new() { Name = "RecoveryHeader" + names[index], Content = Text("column" + names[index], labels[index], "HeaderText"), Height = 30, MinHeight = 0,
                Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            heading.Click += (_, _) => { if (IsBusy) { return; } _descending = _sortColumn == column && !_descending; _sortColumn = column; PresentRows(); };
            Grid.SetColumn(heading, column); _header.Children.Add(heading);
        }
        _table.Children.Add(_header); Grid.SetRow(_objects, 1); _table.Children.Add(_objects);
        _objects.BorderThickness = new Thickness(0); _objects.ItemTemplate = new FuncDataTemplate<RecoveryRow>((row, _) => Row(row));
        _objects.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(0)), new Setter(Layoutable.MinHeightProperty, 24d) } });
        ScrollViewer.SetHorizontalScrollBarVisibility(_objects, ScrollBarVisibility.Disabled);
        ScrollViewer gridViewport = new() { Name = "RecoveryGridViewport", Content = _table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Border table = ClassicChrome.Border(gridViewport, new Thickness(1)); table.Name = "RecoveryGrid"; table.ClipToBounds = true;
        Grid split = new() { Name = "RecoverySplit", ColumnDefinitions = new("700,4,*"), Margin = new Thickness(0, 0, 0, 3), ClipToBounds = true };
        split.Children.Add(table); GridSplitter divider = new() { Name = "RecoverySplitter", Width = 4, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(divider, 1); split.Children.Add(divider);
        ScrollViewer previewErrors = new() { Name = "RecoveryPreviewDiagnostics", Content = _previewError, Margin = new Thickness(6), IsVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        previewErrors.Bind(IsVisibleProperty, _previewError.GetObservable(IsVisibleProperty));
        Grid preview = new() { Children = { _preview, previewErrors } };
        Grid.SetColumn(preview, 2); split.Children.Add(preview); Root.Children.Add(split);

        _view = Menu("RecoveryView", "mnuLostObjectView", "View", "ViewFile", ViewAsync);
        _tag = Menu("RecoveryCreateTag", "mnuLostObjectsCreateTag", "Create tag", "TagCreate", CreateTagAsync, new KeyGesture(Key.T, KeyModifiers.Control));
        _branch = Menu("RecoveryCreateBranch", "mnuLostObjectsCreateBranch", "Create branch", "BranchCreate", CreateBranchAsync, new KeyGesture(Key.B, KeyModifiers.Control));
        _copy = Menu("RecoveryCopyHash", "copyHashToolStripMenuItem", "Copy object hash", "CopyToClipboard", () => CopyAsync(false));
        _copyParent = Menu("RecoveryCopyParent", "copyParentHashToolStripMenuItem", "Copy parent hash", "CopyToClipboard", () => CopyAsync(true));
        _save = Menu("RecoverySaveBlob", "saveAsToolStripMenuItem", "Save as...", "SaveAs", SaveBlobAsync, new KeyGesture(Key.S, KeyModifiers.Control));
        _objects.ContextMenu = new ContextMenu { ItemsSource = new[] { _view, _tag, _branch, _copy, _copyParent, _save } };
        _objects.ContextMenu.Opened += (_, _) => UpdateActions();
        _objects.ContextMenu.AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.KeyModifiers != KeyModifiers.Control) { return; }
            Func<Task>? action = args.Key switch { Key.B => CreateBranchAsync, Key.T => CreateTagAsync, Key.S => SaveBlobAsync, _ => null };
            if (action is null) { return; }
            args.Handled = true; _objects.ContextMenu?.Close(); await action();
        }, RoutingStrategies.Tunnel);
        _objects.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (IsBusy || args.Source is not Visual source || !args.GetCurrentPoint(_objects).Properties.IsRightButtonPressed) { return; }
            ListBoxItem? item = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault(); if (item?.DataContext is RecoveryRow row) { _objects.SelectedItem = row; }
        }, RoutingStrategies.Tunnel);
        _objects.DoubleTapped += async (_, args) =>
        {
            if (args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<CheckBox>().Any()) { return; }
            await ViewAsync(); args.Handled = true;
        };
        _objects.AddHandler(KeyDownEvent, async (_, args) =>
        { if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None) { args.Handled = true; await ViewAsync(); } }, RoutingStrategies.Tunnel);
        _objects.SelectionChanged += async (_, _) => { if (!_presentingRows) { await LoadPreviewAsync(); } };
        _preview.TextView.ComparisonOptionsChanged += async (_, _) => await LoadPreviewAsync();
        _preview.TextView.DisplayOptionsChanged += async (_, _) => await LoadPreviewAsync();
        KeyDown += async (_, args) =>
        {
            if (args.KeyModifiers != KeyModifiers.Control) { return; }
            if (args.Key == Key.B) { args.Handled = true; await CreateBranchAsync(); }
            else if (args.Key == Key.T) { args.Handled = true; await CreateTagAsync(); }
            else if (args.Key == Key.S) { args.Handled = true; await SaveBlobAsync(); }
        };
        _commits.IsCheckedChanged += (_, _) => FilterChanged(_commits, _other); _other.IsCheckedChanged += (_, _) => FilterChanged(_other, _commits);
        foreach (CheckBox box in new[] { _noReflogs, _unreachable, _full }) { box.IsCheckedChanged += async (_, _) => { if (_initialized && !IsBusy && !_closing) { await ScanAsync(); } }; }
        Track(_commits, _other, _noReflogs, _unreachable, _full, _objects, _all);
        Closing += (_, _) => { _closing = true; _lifetime.Cancel(); _annotationCancellation?.Cancel(); CancelPreview(); };
        Closed += (_, _) => { _objects.ContextMenu?.Close(); _preview.Dispose(); foreach (Bitmap icon in _icons) { icon.Dispose(); } };
        Opened += (_, _) => { UpdateColumns(); UpdateActions(); }; UpdateColumns(); UpdateActions();
    }
    private string Text(string item, string source, string property = "Text") => Translations.Translate("FormVerify", item, property, source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormVerify", item, "Text", source), TextWrapping = TextWrapping.Wrap };
    private MenuItem Menu(string name, string key, string caption, string icon, Func<Task> action, KeyGesture? gesture = null)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon + ".png")); Bitmap bitmap = new(stream); _icons.Add(bitmap);
        MenuItem item = new() { Name = name, Header = Caption(key, caption), InputGesture = gesture, Icon = new Image { Source = bitmap, Width = 16, Height = 16, Tag = icon + ".png" } };
        item.Click += async (_, _) => await action(); return item;
    }
    private Grid Row(RecoveryRow? row)
    {
        Grid grid = new() { ColumnDefinitions = new(_columns), MinHeight = 24, Tag = row?.Object.Hash };
        CheckBox check = new() { Name = "RecoveryObjectChecked", IsChecked = row?.Checked == true, Padding = new Thickness(0), MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Center };
        if (row is not null) { check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(RecoveryRow.Checked)) { Source = row, Mode = BindingMode.TwoWay }); }
        check.IsCheckedChanged += (_, _) => { if (row is not null) { row.Checked = check.IsChecked == true; UpdateCheckAll(); } }; grid.Children.Add(check);
        string[] cells = [row?.Object.Date?.LocalDateTime.ToString("g") ?? "", row?.RawType ?? "", row?.Object.Subject ?? "", row?.Object.Author ?? "", row?.Object.Hash ?? "", row?.Object.Parent ?? ""];
        for (int index = 0; index < cells.Length; index++)
        {
            TextBlock text = new() { Text = cells[index], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center };
            if (index is 2 or 3 or 5) { text.IsVisible = _commits.IsChecked == true; }
            if (index == 1 && row is not null) { text.Bind(TextBlock.TextProperty, new Binding(nameof(RecoveryRow.RawType)) { Source = row }); }
            Grid.SetColumn(text, index + 1); grid.Children.Add(text); ToolTip.SetTip(text, cells[index]);
        }
        if (!string.IsNullOrWhiteSpace(row?.Object.ReadError)) { ToolTip.SetTip(grid, row.Object.ReadError); }
        return grid;
    }
    public GitObjectRecoveryOptions ReadOptions() => new(_full.IsChecked == true, _unreachable.IsChecked == true, _noReflogs.IsChecked == true);
    public override Task InitializeAsync() => ScanAsync();
    private Task ScanAsync() => RunAsync("Verify database", ReadScanAsync, cancellationToken: _lifetime.Token);
    private async Task ReadScanAsync(CancellationToken token)
    {
        CancelPreview(); _annotationCancellation?.Cancel(); long version = ++_scanVersion;
        GitObjectRecoveryReport scan = await Repository.GetLostObjectsAsync(ReadOptions(), token); token.ThrowIfCancellationRequested();
        if (_closing) { return; }
        LastScan = scan; _report.Text = scan.StandardOutput + scan.StandardError;
        _rows = scan.Objects.Select(item => new RecoveryRow(item)).ToList(); _initialized = true;
        PresentRows(); Status.Text = scan.ExitCode == 0 ? "" : T("Verification failed") + " (" + scan.ExitCode + ")";
        if (_other.IsChecked == true) { _ = AnnotateBlobsAsync(version); }
    }
    private void FilterChanged(CheckBox changed, CheckBox alternate)
    {
        if (_filtering || !_initialized || IsBusy || _closing) { return; }
        _filtering = true;
        try { if (changed.IsChecked != true && alternate.IsChecked != true) { alternate.IsChecked = true; } PresentRows(); }
        finally { _filtering = false; }
        if (_other.IsChecked == true) { _ = AnnotateBlobsAsync(_scanVersion); }
    }
    private void UpdateColumns()
    {
        double Measure(string value) { TextBlock text = new() { Text = value, FontFamily = FontFamily, FontSize = FontSize, FontWeight = FontWeight, FontStyle = FontStyle }; text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return text.DesiredSize.Width + 12; }
        IEnumerable<RecoveryRow> visible = _rows.Where(row => MatchesFilter(row.Object));
        double date = Math.Max(56, visible.Select(row => Measure(row.Object.Date?.LocalDateTime.ToString("g") ?? "")).DefaultIfEmpty(56).Max());
        double type = Math.Max(58, visible.Select(row => Measure(row.RawType)).DefaultIfEmpty(58).Max());
        string fixedColumns = "24," + date.ToString(CultureInfo.InvariantCulture) + "," + type.ToString(CultureInfo.InvariantCulture);
        _columns = _commits.IsChecked == true ? fixedColumns + ",*,150,60,60" : fixedColumns + ",0,0,60,0";
        _table.MinWidth = _commits.IsChecked == true ? Math.Max(700, 24 + date + type + 120 + 150 + 120) : 24 + date + type + 60;
        _header.ColumnDefinitions = new(_columns);
        foreach (int index in new[] { 3, 4, 6 }) { _header.Children[index].IsVisible = _commits.IsChecked == true; }
        foreach (Grid row in _objects.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Tag is string))
        {
            row.ColumnDefinitions = new(_columns);
            foreach (TextBlock text in row.Children.OfType<TextBlock>()) { text.IsVisible = Grid.GetColumn(text) is not (3 or 4 or 6) || _commits.IsChecked == true; }
        }
    }
    private bool MatchesFilter(GitLostObject item) => item.Type is GitLostObjectType.Commit or GitLostObjectType.Tag ? _commits.IsChecked == true : _other.IsChecked == true;
    private void PresentRows()
    {
        string? selected = SelectedObject?.Hash; CancelPreview(); UpdateColumns();
        IEnumerable<RecoveryRow> rows = _rows.Where(row => MatchesFilter(row.Object));
        if (_sortColumn == 1) { rows = _descending ? rows.OrderByDescending(row => row.Object.Date) : rows.OrderBy(row => row.Object.Date); }
        else
        {
            string Key(RecoveryRow row) => _sortColumn switch { 2 => row.RawType, 3 => row.Object.Subject ?? "", 4 => row.Object.Author ?? "", 5 => row.Object.Hash, 6 => row.Object.Parent ?? "", _ => "" };
            rows = _descending ? rows.OrderByDescending(Key, StringComparer.CurrentCulture) : rows.OrderBy(Key, StringComparer.CurrentCulture);
        }
        _displayed = rows.ToList(); _presentingRows = true;
        try { _objects.ItemsSource = _displayed; _objects.SelectedItem = _displayed.FirstOrDefault(row => row.Object.Hash == selected) ?? _displayed.FirstOrDefault(); }
        finally { _presentingRows = false; }
        UpdateCheckAll(); UpdateActions();
        _ = LoadPreviewAsync();
    }
    private void UpdateCheckAll()
    {
        int count = _displayed.Count(row => row.Checked); _all.IsChecked = count == 0 ? false : count == _displayed.Count ? true : null;
    }
    private void CancelPreview()
    { _previewVersion++; _previewCancellation?.Cancel(); Preview = null; _preview.TextView.DocumentIdentity = null; _preview.ShowDiff(""); _previewError.Text = ""; _previewError.IsVisible = false; }
    private async Task LoadPreviewAsync()
    {
        CancelPreview(); UpdateActions(); GitLostObject? item = SelectedObject; if (item is null || _closing) { return; }
        string hash = item.Hash; long version = _previewVersion;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _previewCancellation = cancellation;
        try
        {
            GitObjectPreview preview = await Repository.GetRecoveryObjectPreviewAsync(hash, cancellation.Token, _preview.TextView.ComparisonMenu.Options, _preview.TextView.DisplayOptions);
            if (_closing || cancellation.IsCancellationRequested || version != _previewVersion || SelectedObject?.Hash != hash) { return; }
            if (preview.ReadError is not null)
            { Preview = preview; _previewError.Text = Text("_lostObjectUnreadable", "Object could not be read. It may be corrupted or no longer available.") + "\n\n" + preview.ReadError; _previewError.IsVisible = true; }
            else
            {
                await _preview.TextView.ConfigureRepositoryAsync(Repository, cancellation.Token);
                if (_closing || cancellation.IsCancellationRequested || version != _previewVersion || SelectedObject?.Hash != hash) { return; }
                _preview.TextView.DocumentIdentity = Repository.Path + "\nobject:" + hash;
                if (preview.DiffDocument is not null) { _preview.ShowDiff(preview.DiffDocument, preview.SuggestedFileName); }
                else if (preview.Object?.Type == GitLostObjectType.Blob) { _preview.ShowFile(preview.SuggestedFileName, preview.Object.Bytes, preview.FileEncodingName); }
                else { _preview.ShowDiff(preview.Text ?? "", preview.SuggestedFileName); _preview.TextView.Mode = DiffViewMode.PlainText; }
                Preview = preview;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing && version == _previewVersion) { _previewError.Text = error.Message; _previewError.IsVisible = true; } }
        finally { if (ReferenceEquals(_previewCancellation, cancellation)) { _previewCancellation = null; } }
    }
    private async Task AnnotateBlobsAsync(long version)
    {
        _annotationCancellation?.Cancel();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _annotationCancellation = cancellation;
        try
        {
            foreach (RecoveryRow row in _rows.Where(row => row.Object.Type == GitLostObjectType.Blob && !row.Annotated).ToArray())
            {
                GitObjectPreview data = await Repository.GetRecoveryObjectPreviewAsync(row.Object.Hash, cancellation.Token);
                if (_closing || cancellation.IsCancellationRequested || version != _scanVersion) { return; }
                row.Annotated = true;
                if (data.ReadError is null) { row.RawType = row.Object.RawType + " (" + Text("_seemingly", "seemingly") + ": " + Path.GetExtension(data.SuggestedFileName).TrimStart('.') + ")"; UpdateColumns(); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { /* Per-object diagnostics and raw fsck output remain available; a failed type hint cannot remove rows. */ }
        finally { if (ReferenceEquals(_annotationCancellation, cancellation)) { _annotationCancellation = null; } }
    }
    private bool CanAct => _initialized && !IsBusy && !_closing && IsVisible;
    private Task CreateBranchAsync()
    {
        if (!CanAct || SelectedObject is not { Type: GitLostObjectType.Commit } item) { return Task.CompletedTask; } string hash = item.Hash;
        return RunAsync("Create branch", async token => { await ChildAsync(new CreateBranchDialog(Repository, hash, autoNormalize: _autoNormalize, replacement: _replacement), token); if (!_closing) { await ReadScanAsync(token); } }, cancellationToken: _lifetime.Token);
    }
    private Task CreateTagAsync()
    {
        if (!CanAct || SelectedObject is not { Type: GitLostObjectType.Commit } item) { return Task.CompletedTask; } string hash = item.Hash;
        return RunAsync("Create tag", async token => { await ChildAsync(new CreateTagDialog(Repository, hash), token); if (!_closing) { await ReadScanAsync(token); } }, cancellationToken: _lifetime.Token);
    }
    private async Task ChildAsync(RepositoryDialog child, CancellationToken token)
    {
        child.RequestedThemeVariant = ActualThemeVariant;
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (child.IsVisible) { child.Close(); } }));
        try { await child.InitializeAsync(); token.ThrowIfCancellationRequested(); if (!_closing && IsVisible) { await child.ShowDialog(this); } }
        finally { child.Close(); if (child.HasRepositoryChanges) { MarkRepositoryChanged(); } }
    }
    private Task CopyAsync(bool parent)
    {
        if (!CanAct || SelectedObject is not { } item || Clipboard is not { } clipboard || parent && (item.Type != GitLostObjectType.Commit || string.IsNullOrEmpty(item.Parent))) { return Task.CompletedTask; }
        string hash = parent ? item.Parent! : item.Hash;
        return RunAsync("Copy object hash", async _ => await ClipboardExtensions.SetTextAsync(clipboard, hash), cancellationToken: _lifetime.Token);
    }
    private Task ViewAsync()
    {
        if (!CanAct || SelectedObject is not { } item) { return Task.CompletedTask; } string hash = item.Hash;
        return RunAsync("View", async token =>
        {
            GitObjectPreview data = await Repository.GetRecoveryObjectPreviewAsync(hash, token, _preview.TextView.ComparisonMenu.Options, _preview.TextView.DisplayOptions); token.ThrowIfCancellationRequested();
            if (_closing || !IsVisible) { return; }
            using TextEditDialog child = new("", data.SuggestedFileName, Translations, "RecoveryQuickViewer")
                { Name = "RecoveryQuickView", IsReadOnly = true, RequestedThemeVariant = ActualThemeVariant };
            FileHistoryContentView viewer = child.Viewer;
            using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(child.Close));
            try
            {
                if (data.ReadError is null) { await viewer.TextView.ConfigureRepositoryAsync(Repository, token); }
                token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
                viewer.TextView.DocumentIdentity = Repository.Path + "\nobject:" + hash;
                if (data.ReadError is not null) { viewer.ShowDiff(Text("_lostObjectUnreadable", "Object could not be read. It may be corrupted or no longer available.") + "\n\n" + data.ReadError); viewer.TextView.Mode = DiffViewMode.PlainText; }
                else if (data.DiffDocument is not null) { viewer.ShowDiff(data.DiffDocument, data.SuggestedFileName); }
                else if (data.Object?.Type == GitLostObjectType.Blob) { viewer.ShowFile(data.SuggestedFileName, data.Object.Bytes, data.FileEncodingName); }
                else { viewer.ShowDiff(data.Text ?? "", data.SuggestedFileName); viewer.TextView.Mode = DiffViewMode.PlainText; }
                await child.ShowDialog(this);
            }
            finally { child.Close(); }
        }, cancellationToken: _lifetime.Token);
    }
    private Task SaveBlobAsync()
    {
        if (!CanAct || SelectedObject is not { Type: GitLostObjectType.Blob } item) { return Task.CompletedTask; } string hash = item.Hash;
        return RunAsync("Save as…", async token =>
        {
            GitObjectPreview data = await Repository.GetRecoveryObjectPreviewAsync(hash, token); token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
            string extension = Path.GetExtension(data.SuggestedFileName).TrimStart('.');
            FilePickerSaveOptions options = new() { Title = Text("saveAsToolStripMenuItem", "Save as..."), SuggestedFileName = data.SuggestedFileName, DefaultExtension = extension,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(Repository.Path)), FileTypeChoices = FileTypes(extension), ShowOverwritePrompt = true };
            token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
            IStorageFile? chosen = SaveFilePicker is { } picker ? await picker(options) : await StorageProvider.SaveFilePickerAsync(options);
            if (chosen is null) { return; }
            using (chosen)
            {
                token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
                string path = chosen.TryGetLocalPath() ?? throw new InvalidOperationException(T("Select a local file to save the recovered blob."));
                GitRepositoryInfo info = await Repository.GetInfoAsync(token); token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
                string repositoryRoot = ResolveRecoveryDirectory(info.RootPath);
                bool inRepository = IsInsideRecoveryDirectory(Path.GetFullPath(path), repositoryRoot) || IsInsideRecoveryDirectory(Path.GetFullPath(path), Repository.Path);
                GitObjectExportResult output;
                try { output = await Repository.ExportBlobAsync(hash, path, overwrite: true, cancellationToken: token); }
                finally { if (inRepository) { MarkRepositoryChanged(); } }
                // The picker may use an outside symbolic-link alias. Core reports the actual destination it wrote.
                if (IsInsideRecoveryDirectory(output.DestinationPath, repositoryRoot)) { MarkRepositoryChanged(); }
                Status.Text = output.DestinationPath;
            }
        }, cancellationToken: _lifetime.Token);
    }
    private static bool IsInsideRecoveryDirectory(string file, string directory)
        => file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static string ResolveRecoveryDirectory(string directory)
    {
        string full = Path.GetFullPath(directory), resolved = Path.GetPathRoot(full)!;
        foreach (string component in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(resolved, component); DirectoryInfo info = new(next);
            resolved = info.LinkTarget is null ? next : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("The repository directory symbolic link cannot be resolved.");
        }
        return resolved;
    }
    private static IReadOnlyList<FilePickerFileType> FileTypes(string extension)
    {
        Dictionary<string, string[]> equivalents = new(StringComparer.OrdinalIgnoreCase)
        { ["js"] = ["ts", "jsx", "tsx"], ["html"] = ["php", "cshtml"], ["cpp"] = ["c"], ["xml"] = ["config", "settings", "csproj", "xlf", "props"],
            ["zip"] = ["docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "jar", "msix"], ["exe"] = ["dll"], ["doc"] = ["xls", "ppt", "msi"], ["md"] = ["sh", "yml"], ["txt"] = ["csv", "css", "md", "yml"] };
        IEnumerable<string> types = new[] { extension }.Concat(equivalents.GetValueOrDefault(extension) ?? []);
        return types.Select(type => new FilePickerFileType(type + " Files (*." + type + ")") { Patterns = ["*." + type] }).Append(FilePickerFileTypes.All).ToArray();
    }
    private Task RecoverAsync()
    {
        if (!CanAct) { return Task.CompletedTask; } string[] selected = _displayed.Where(row => row.Checked).Select(row => row.Object.Hash).ToArray(); int count = _displayed.Count;
        return RunAsync("Restore lost objects", async token =>
        {
            if (selected.Length == 0) { await MessageAsync("RecoveryEmptySelection", Text("_selectLostObjectsToRestoreCaption", "Restore lost objects"), Text("_selectLostObjectsToRestoreMessage", "Select objects to restore."), "Warning", token); return; }
            LastRecovery = await Repository.RecoverObjectsAsync(selected, token); if (LastRecovery.HasRepositoryChanges) { MarkRepositoryChanged(); }
            string message = LastRecovery.EffectsVerified
                ? string.Format(CultureInfo.CurrentCulture, Text("_xTagsCreated", "{0} Tags created.\n\nDo not forget to delete these tags when finished."), LastRecovery.CreatedTags.Count)
                : T("The recovery effects could not be confirmed. Reload the repository to inspect its references.");
            string errors = string.Join('\n', LastRecovery.Errors.Select(error => error.Operation + ": " + error.ObjectId + "\n" + error.Message));
            if (LastRecovery.Cancelled) { errors = T("Operation cancelled") + "\n" + errors; }
            if (errors.Length != 0) { message += "\n\n" + errors; } Status.Text = message;
            if (_closing || !IsVisible) { return; }
            await MessageAsync("RecoveryTagsCreated", T("Tags created"), message, errors.Length == 0 ? "Information" : "Warning", token);
            if (_closing) { return; }
            if (LastRecovery.EffectsVerified && LastRecovery.Success && LastRecovery.CreatedTags.Count == count) { Close(true); }
            else { await ReadScanAsync(token); Status.Text = message; }
        }, cancellationToken: _lifetime.Token);
    }
    private Task DeleteTagsAsync()
    {
        if (!CanAct) { return Task.CompletedTask; }
        return RunAsync("Delete all LOST_AND_FOUND tags", async token =>
        {
            LastRecovery = await Repository.DeleteLostFoundTagsAsync(token); if (LastRecovery.HasRepositoryChanges) { MarkRepositoryChanged(); }
            string errors = string.Join('\n', LastRecovery.Errors.Select(error => error.Operation + ": " + error.Message));
            if (!LastRecovery.EffectsVerified) { errors = T("The recovery effects could not be confirmed. Reload the repository to inspect its references.") + "\n" + errors; }
            if (LastRecovery.Cancelled) { errors = T("Operation cancelled") + "\n" + errors; }
            if (!_closing) { await ReadScanAsync(token); Status.Text = errors; }
        }, cancellationToken: _lifetime.Token);
    }
    private Task SaveLostFoundAsync()
    {
        if (!CanAct) { return Task.CompletedTask; } GitObjectRecoveryOptions options = ReadOptions();
        return RunAsync("Save objects to .git/lost-found", async token =>
        {
            GitObjectRecoveryReport result;
            try { result = await Repository.SaveLostFoundAsync(options, token); } finally { MarkRepositoryChanged(); }
            if (!_closing) { await ReadScanAsync(token); _report.Text = result.StandardOutput + result.StandardError + "\n" + _report.Text; Status.Text = result.LostFoundDirectory; }
        }, cancellationToken: _lifetime.Token);
    }
    private Task PruneAsync()
    {
        if (!CanAct) { return Task.CompletedTask; }
        return RunAsync("Remove", async token =>
        {
            if (!await ConfirmPruneAsync(token)) { return; } token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
            GitResult result;
            try { result = await Repository.PruneLostObjectsAsync(token); } finally { MarkRepositoryChanged(); }
            if (!_closing) { await ReadScanAsync(token); _report.Text = result.StandardOutput + result.StandardError + "\n" + _report.Text; }
        }, cancellationToken: _lifetime.Token);
    }
    private async Task<bool> ConfirmPruneAsync(CancellationToken token)
    {
        Window warning = MessageWindow("RecoveryPruneWarning", Text("_removeDanglingObjectsCaption", "Remove"), Text("_removeDanglingObjectsQuestion", "Are you sure you want to delete all dangling objects?"), "Question");
        Button yes = new() { Name = "ConfirmRecoveryPrune", Content = T("Yes"), IsDefault = true, MinWidth = 75, Height = 25 }, no = new() { Name = "CancelRecoveryPrune", Content = T("No"), IsCancel = true, MinWidth = 75, Height = 25 };
        yes.Click += (_, _) => warning.Close(true); no.Click += (_, _) => warning.Close(false); ((StackPanel)warning.Content!).Children.Add(ExportDialogControls.Buttons(yes, no));
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => warning.Close(false)));
        bool confirmed = await warning.ShowDialog<bool>(this); token.ThrowIfCancellationRequested(); return confirmed;
    }
    private async Task MessageAsync(string name, string title, string message, string icon, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
        Window child = MessageWindow(name, title, message, icon); Button ok = new() { Name = "Close" + name, Content = T("OK"), IsDefault = true, IsCancel = true, Height = 25, MinWidth = 75 };
        ok.Click += (_, _) => child.Close(); ((StackPanel)child.Content!).Children.Add(ExportDialogControls.Buttons(ok));
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(child.Close));
        try { await child.ShowDialog(this); } finally { child.Close(); }
    }
    private Window MessageWindow(string name, string title, string message, string icon)
        => new() { Name = name, Title = title, Width = 540, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant,
            Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children = { new Grid { ColumnDefinitions = new("26,*"), Children =
            { MessageIcon(icon), MessageContent(message) } } } } };
    private static Control MessageIcon(string icon)
        => icon == "Warning" ? ResetDialogControls.Icon("Warning") : new Border
        { Name = "RecoveryMessageIcon", Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.Parse("#0078d7")), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = icon == "Question" ? "?" : "i", Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    private static ScrollViewer MessageContent(string message)
    { ScrollViewer content = new() { MaxHeight = 260, Content = new TextBlock { Name = "RecoveryMessage", Text = message, TextWrapping = TextWrapping.Wrap }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(content, 1); return content; }
    protected override void UpdateActions()
    {
        if (_recover is null) { return; } bool ready = _initialized && !IsBusy && !_closing;
        _recover.IsEnabled = _delete.IsEnabled = _prune.IsEnabled = _saveObjects.IsEnabled = ready;
        _all.IsEnabled = ready && _displayed.Count != 0;
        if (_view is null) { return; } GitLostObject? item = SelectedObject; bool selected = ready && item is not null;
        _view.IsEnabled = selected; _tag.IsEnabled = _branch.IsEnabled = selected && item!.Type == GitLostObjectType.Commit;
        _copy.IsEnabled = selected && Clipboard is not null; _copyParent.IsEnabled = selected && Clipboard is not null && item!.Type == GitLostObjectType.Commit && !string.IsNullOrEmpty(item.Parent);
        _save.IsEnabled = selected && item!.Type == GitLostObjectType.Blob;
    }
    private sealed class RecoveryRow(GitLostObject item) : INotifyPropertyChanged
    {
        private string _rawType = item.RawType;
        private bool _checked;
        public GitLostObject Object { get; } = item;
        public bool Checked { get => _checked; set { if (_checked == value) { return; } _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); } }
        public bool Annotated { get; set; }
        public string RawType { get => _rawType; set { _rawType = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawType))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
