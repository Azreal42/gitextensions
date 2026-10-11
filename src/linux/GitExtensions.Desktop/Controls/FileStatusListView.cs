using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

public sealed record FileStatusListGroup(object Tag, string Key, Control Header, IReadOnlyList<FileStatusListEntry> Entries,
    bool AlwaysVisible = false, bool IsBaseComparison = false, string? Name = null)
{
    public Func<int, int, Control>? HeaderFactory { get; init; }
    public bool ExpandSmallDiff { get; init; } = true;
}

/// <summary>Shared native FileStatusList presentation; Git commands remain the responsibility of its host.</summary>
public sealed class FileStatusListView : UserControl, IDisposable
{
    private readonly FileStatusListPreferencesStore _store;
    private readonly Func<FileStatusListEntry, Control> _fileHeader;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Height = 24 };
    private readonly ComboBox _filter = new() { IsEditable = true, Height = 24, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 0), FontWeight = FontWeight.Bold };
    private readonly Button _clearFilter;
    private readonly SplitButton _viewMode;
    private readonly ToggleButton _byPath, _byExtension, _byStatus, _unequal, _onlyB, _onlyA, _same;
    private readonly MenuItem[] _sortMenus;
    private readonly MenuItem _dense, _flatGroups, _toolbarMenu;
    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Image, Bitmap> _images = new();
    private readonly Dictionary<TreeViewItem, FileStatusListNode> _models = [];
    private readonly Dictionary<TreeViewItem, TreeViewItem?> _parents = [];
    private readonly Dictionary<(string Group, FileStatusListNodeKind Kind, string Key), bool> _expansion = [];
    private readonly List<string> _history = ["^(?!.*NotThisWord)", @"^(?!.*\bg?tests?/)"];
    private readonly Dictionary<Control, string> _toolbarRoles = [];
    private readonly TextBlock _noFiles = new() { FontStyle = FontStyle.Italic, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 0) };
    private readonly Grid _filterRow = new() { ColumnDefinitions = new ColumnDefinitions("*,20"), Height = 24 };
    private IReadOnlyList<FileStatusListGroup> _groups = [];
    private FileStatusListOptions _options;
    private FileStatusListDiffFilter _diffFilter = new();
    private Regex? _predicate;
    private CancellationTokenSource? _pendingFilter;
    private IDisposable? _filterBackgroundBinding;
    private bool _updatingFilter, _updatingToolbar, _disposed;

    public FileStatusListView(TreeView tree, Func<FileStatusListEntry, Control>? fileHeaderFactory = null,
        FileStatusListPreferencesStore? preferencesStore = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        Tree = tree;
        Name = (tree.Name ?? "FileStatus") + "View";
        _store = preferencesStore ?? new(); _options = _store.Load();
        _fileHeader = fileHeaderFactory ?? DefaultFileHeader;
        _filter.Name = Prefix("Filter"); _filter.ItemsSource = _history.ToArray();
        _filter.PlaceholderText = T("Filter files using a regular expression...");
        ToolTip.SetTip(_filter, T("Filter files using a regular expression..."));
        _filter.Styles.Add(new Style(selector => selector.OfType<TextBox>())
        { Setters = { new Setter(TextBox.BackgroundProperty, new Binding(nameof(Background)) { Source = _filter }) } });
        _filter.PropertyChanged += FilterPropertyChanged;
        _filter.SelectionChanged += FilterSelectionChanged;
        _clearFilter = IconButton("ClearFilter", "Clear filter", "DeleteText.png", ClearFilter);
        _clearFilter.IsVisible = false;

        Button collapse = IconButton("Collapse", "Collapse all groups, otherwise expand the selected group", "CollapseAll.png", CollapseGroups);
        _toolbar.Children.Add(collapse); _toolbarRoles[collapse] = "Collapse";
        _sortMenus = Enum.GetValues<FileStatusListSortMode>().Select(mode =>
        {
            MenuItem item = new() { Name = Prefix("Sort_" + mode), Header = new AccessText { Text = _translations.TranslateAccessKeys("FileStatusList", SortItemName(mode), "Text", SortCaption(mode)) }, ToggleType = MenuItemToggleType.CheckBox };
            item.Click += (_, _) => SortMode = mode;
            return item;
        }).ToArray();
        _dense = new() { Name = Prefix("DenseTree"), Header = T("Dense tree (merge single item with its folder node)"), ToggleType = MenuItemToggleType.CheckBox };
        _dense.Click += (_, _) => DenseTree = _dense.IsChecked;
        _flatGroups = new() { Name = Prefix("ShowGroupNodesInFlatList"), Header = T("Show group nodes in flat list (if multiple)"), ToggleType = MenuItemToggleType.CheckBox };
        _flatGroups.Click += (_, _) => ShowGroupNodesInFlatList = _flatGroups.IsChecked;
        _viewMode = new() { Name = Prefix("ViewMode"), Height = 24, MinHeight = 24, Width = 32, Padding = new Thickness(2, 0) };
        _viewMode.Resources["ButtonMinHeight"] = 24d;
        _viewMode.Flyout = new MenuFlyout { ItemsSource = _sortMenus.Cast<object>().Concat(new object[] { new Separator(), _dense, _flatGroups }).ToArray() };
        _viewMode.Click += (_, _) => SortMode = IsFlat ? (FileStatusListSortMode)((int)SortMode - 1) : (FileStatusListSortMode)((int)SortMode + 1);
        ToolTip.SetTip(_viewMode, T("Toggle flat list / tree"));
        _toolbar.Children.Add(_viewMode); _toolbarRoles[_viewMode] = "ViewMode";
        _byPath = SortButton("ByPath", "Group by file path", "FolderClosed.png", FileStatusListSortMode.FilePath);
        _byExtension = SortButton("ByExtension", "Group by file type (extension)", "File.png", FileStatusListSortMode.FileExtension);
        _byStatus = SortButton("ByStatus", "Group by diff status", "FileStatusModified.png", FileStatusListSortMode.FileStatus);
        _unequal = DiffButton("Unequal", "!", "Show files with different changes", "#ff0000", value => _diffFilter = _diffFilter with { Unequal = value });
        _onlyB = DiffButton("OnlyB", "B", "Show files changed in B only", "#bd7cff", value => _diffFilter = _diffFilter with { OnlyB = value });
        _onlyA = DiffButton("OnlyA", "A", "Show files changed in A only", "#a8a800", value => _diffFilter = _diffFilter with { OnlyA = value });
        _same = DiffButton("Same", "=", "Show files changed equally", "#00a800", value => _diffFilter = _diffFilter with { Same = value });
        SettingsMenu = new();
        _toolbarMenu = new() { Header = T("Toolbar"), Name = Prefix("ToolbarOptions") };
        SettingsMenu.Items.Add(_toolbarMenu);
        SettingsMenu.Opening += (_, _) => RefreshToolbarMenu();
        Button settings = IconButton("Settings", "Settings", "Settings.png", () => { });
        settings.Flyout = SettingsMenu; _toolbar.Children.Add(settings); _toolbarRoles[settings] = "Settings";

        _filterRow.Children.Add(_filter); Grid.SetColumn(_clearFilter, 1); _filterRow.Children.Add(_clearFilter);
        DockPanel layout = new();
        Border bar = ClassicChrome.Border(_toolbar, new Thickness(0, 0, 0, 1)); DockPanel.SetDock(bar, Dock.Top); layout.Children.Add(bar);
        DockPanel.SetDock(_filterRow, Dock.Top); layout.Children.Add(_filterRow);
        _noFiles.Text = T("No changes"); _noFiles.Name = Prefix("NoChanges");
        Grid treeHost = new(); treeHost.Children.Add(Tree); treeHost.Children.Add(_noFiles); layout.Children.Add(treeHost);
        Content = layout;
        Tree.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>())
        { Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 24d) } });
        _store.Changed += PreferencesChanged;
        ActualThemeVariantChanged += ThemeChanged;
        UpdateToolbar(); ApplyFilterPalette();
    }

    public TreeView Tree { get; }
    public Panel Toolbar => _toolbar;
    public MenuFlyout SettingsMenu { get; }
    public bool IsUpdating { get; private set; }
    public string FilterText { get => _filter.Text ?? ""; set => _filter.Text = value; }
    public string? FilterError { get; private set; }
    public bool IsFilterActive => FilterText.Length != 0;
    public IReadOnlyList<string> FilterHistory => _history.ToArray();
    public IReadOnlyList<FileStatusListEntry> Entries => _groups.SelectMany(group => group.Entries).ToArray();
    public IReadOnlyList<FileStatusListEntry> FilteredEntries => _models.Values.Where(node => node.Entry is not null).Select(node => node.Entry!).ToArray();
    public IReadOnlyList<FileStatusListEntry> SelectedEntries
    {
        get
        {
            HashSet<TreeViewItem> selected = [.. Tree.SelectedItems?.OfType<TreeViewItem>() ?? []];
            return _models.Where(pair => pair.Value.Entry is not null && IsSelected(pair.Key)).Select(pair => pair.Value.Entry!)
                .DistinctBy(entry => entry.Tag).ToArray();
            bool IsSelected(TreeViewItem node)
            {
                TreeViewItem? current = node;
                while (current is not null) { if (selected.Contains(current)) { return true; } current = _parents[current]; }
                return false;
            }
        }
    }
    public IReadOnlyList<object> SelectedTags => SelectedEntries.Select(entry => entry.Tag).ToArray();
    public FileStatusListSortMode SortMode { get => _options.SortMode; set => SetOptions(_options with { SortMode = value }); }
    public bool DenseTree { get => _options.DenseTree; set => SetOptions(_options with { DenseTree = value }); }
    public bool ShowGroupNodesInFlatList { get => _options.ShowGroupNodesInFlatList; set => SetOptions(_options with { ShowGroupNodesInFlatList = value }); }
    public bool FilterFileNameOnly { get => _options.FilterFileNameOnly; set => SetOptions(_options with { FilterFileNameOnly = value }); }
    public event EventHandler? DisplayChanged;
    public static string IconName(FileStatusListEntry entry) => FileStatusListBuilder.IconName(entry);

    public void SetGroups(IReadOnlyList<FileStatusListGroup> groups, bool preserveSelection = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(groups);
        _groups = groups; Rebuild(preserveSelection, selectFirst: true);
    }
    public TreeViewItem? FindItemByTag(object tag)
        => _models.Keys.FirstOrDefault(item => ReferenceEquals(item.Tag, tag) || Equals(item.Tag, tag));
    public void SelectTags(IEnumerable<object> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        HashSet<object> wanted = [.. tags];
        TreeViewItem[] selected = _models.Keys.Where(item => item.Tag is not null && wanted.Contains(item.Tag)).ToArray();
        IsUpdating = true;
        try { Tree.UnselectAll(); foreach (TreeViewItem item in selected) { ExpandAncestors(item); Tree.SelectedItems!.Add(item); } }
        finally { IsUpdating = false; }
        // Hosts may update a diff once for this logical selection, rather than for each intermediate item.
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ClearSelection() => Tree.UnselectAll();
    public void ClearFilter()
    {
        CancelPendingFilter(); _updatingFilter = true;
        try { _filter.Text = ""; } finally { _updatingFilter = false; }
        ApplyFilter("");
    }

    private string Prefix(string suffix) => (Tree.Name ?? "FileStatus") + suffix;
    private string T(string source) => _translations.Translate(source);
    private bool IsFlat => SortMode is FileStatusListSortMode.FilePathFlat or FileStatusListSortMode.FileExtensionFlat or FileStatusListSortMode.FileStatusFlat;
    private void SetOptions(FileStatusListOptions options)
    {
        if (_disposed || options == _options) { return; }
        if (!Enum.IsDefined(options.SortMode)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        _options = options;
        if (!_store.Save(options)) { Rebuild(); }
    }
    private void PreferencesChanged(object? sender, EventArgs args)
    {
        if (_disposed) { return; }
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => PreferencesChanged(sender, args)); return; }
        _options = _store.Load(); Rebuild();
    }
    private void FilterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != ComboBox.TextProperty || _updatingFilter || _disposed) { return; }
        _clearFilter.IsVisible = FilterText.Length > 0;
        CancelPendingFilter(); CancellationTokenSource operation = new(); _pendingFilter = operation;
        _ = DebounceFilterAsync(FilterText, operation);
    }
    private async Task DebounceFilterAsync(string text, CancellationTokenSource operation)
    {
        try
        {
            await Task.Delay(250, operation.Token);
            if (!_disposed && !operation.IsCancellationRequested) { ApplyFilter(text); }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_pendingFilter, operation)) { _pendingFilter = null; } operation.Dispose(); }
    }
    private void FilterSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_updatingFilter || _disposed || _filter.SelectedItem is not string text) { return; }
        CancelPendingFilter(); _updatingFilter = true;
        try { _filter.Text = text; } finally { _updatingFilter = false; }
        ApplyFilter(text);
    }
    private void CancelPendingFilter() { _pendingFilter?.Cancel(); _pendingFilter = null; }
    private void ApplyFilter(string text)
    {
        try
        {
            Regex? predicate = text.Length == 0 ? null : new(text, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            // Validate before replacing the last good predicate or the displayed tree.
            foreach (FileStatusListEntry entry in Entries) { _ = MatchesName(entry, predicate); }
            _predicate = predicate; FilterError = null; Rebuild();
            if (text.Length > 0 && FilteredEntries.Count > 0 && !_history.Contains(text, StringComparer.Ordinal))
            {
                if (_history.Count == 10) { _history.RemoveAt(9); }
                _history.Insert(0, text); _updatingFilter = true;
                try { _filter.ItemsSource = _history.ToArray(); _filter.Text = text; } finally { _updatingFilter = false; }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        { FilterError = exception.Message; DisplayChanged?.Invoke(this, EventArgs.Empty); }
        _clearFilter.IsVisible = text.Length > 0; ApplyFilterPalette();
    }
    private bool MatchesName(FileStatusListEntry entry, Regex? predicate)
    {
        if (predicate is null) { return true; }
        string path = entry.Path.TrimEnd('/'); string? previous = entry.OriginalPath;
        if (FilterFileNameOnly) { path = Path.GetFileName(path); previous = previous is null ? null : Path.GetFileName(previous); }
        return predicate.IsMatch(path) || previous is not null && predicate.IsMatch(previous);
    }
    private void Rebuild(bool preserveSelection = true, bool selectFirst = false)
    {
        if (_disposed) { return; }
        HashSet<(string Group, FileStatusListNodeKind Kind, string Key)> selected = preserveSelection
            ? [.. Tree.SelectedItems?.OfType<TreeViewItem>().Where(_models.ContainsKey).Select(item => Identity(_models[item])) ?? []] : [];
        foreach ((TreeViewItem item, FileStatusListNode node) in _models) { _expansion[Identity(node)] = item.IsExpanded; }
        FileStatusListModelGroup[] groups = _groups.Select(group => new FileStatusListModelGroup(group.Tag, group.Key, group.Entries, group.AlwaysVisible, group.IsBaseComparison)).ToArray();
        IReadOnlyList<FileStatusListNode> nodes = FileStatusListBuilder.Build(groups, _options, entry => _diffFilter.Matches(entry.DiffStatus) && MatchesName(entry, _predicate));
        IsUpdating = true;
        try
        {
            Tree.UnselectAll();
            foreach (TreeViewItem old in _models.Keys) { old.Header = null; }
            Tree.ItemsSource = null; _models.Clear(); _parents.Clear();
            TreeViewItem[] roots = nodes.Select(node => CreateItem(node, null)).ToArray(); Tree.ItemsSource = roots;
            TreeViewItem[] restored = _models.Where(pair => selected.Contains(Identity(pair.Value))).Select(pair => pair.Key).ToArray();
            if (restored.Length == 0 && selectFirst) { restored = [_models.FirstOrDefault(pair => pair.Value.Entry is not null).Key ?? roots.FirstOrDefault()!]; }
            foreach (TreeViewItem? item in restored)
            {
                if (item is null) { continue; }
                ExpandAncestors(item); Tree.SelectedItems!.Add(item);
            }
            UpdateToolbar();
        }
        finally { IsUpdating = false; }
        DisplayChanged?.Invoke(this, EventArgs.Empty);

        TreeViewItem CreateItem(FileStatusListNode node, TreeViewItem? parent)
        {
            FileStatusListGroup group = _groups.Single(group => group.Key == node.GroupKey);
            int shown = FileStatusListBuilder.Descendants(node.Children).Count(child => child.Entry is not null);
            Control header = node.Entry is not null ? _fileHeader(node.Entry with { DisplayPath = node.Label })
                : node.Kind == FileStatusListNodeKind.Revision ? group.HeaderFactory?.Invoke(shown, group.Entries.Count) ?? group.Header
                : IconHeader(node.Kind == FileStatusListNodeKind.Placeholder ? "- " + T("No changes") + " -" : node.Label, node.Icon);
            TreeViewItem item = new() { Tag = node.Tag, Header = header };
            if (node.Kind == FileStatusListNodeKind.Revision) { item.Name = group.Name; }
            _models.Add(item, node); _parents.Add(item, parent);
            item.ItemsSource = node.Children.Select(child => CreateItem(child, item)).ToArray();
            item.IsExpanded = _expansion.TryGetValue(Identity(node), out bool expanded) ? expanded
                : node.Kind != FileStatusListNodeKind.Revision || group.Entries.Count > 0
                    && (_groups.Count < 3 || group == _groups[0] || group.ExpandSmallDiff && group.Entries.Count <= 7);
            return item;
        }
    }
    private static (string, FileStatusListNodeKind, string) Identity(FileStatusListNode node) => (node.GroupKey, node.Kind, node.Key);
    private void ExpandAncestors(TreeViewItem item)
    { for (TreeViewItem? parent = _parents[item]; parent is not null; parent = _parents[parent]) { parent.IsExpanded = true; } }
    private void CollapseGroups()
    {
        TreeViewItem[] groups = _models.Keys.Where(item => item.Items.Count > 0).ToArray();
        if (groups.Any(item => item.IsExpanded))
        {
            foreach (TreeViewItem item in groups) { item.IsExpanded = false; }
            if (Tree.SelectedItem is TreeViewItem selected)
            {
                while (_parents[selected] is TreeViewItem parent) { selected = parent; }
                Tree.SelectedItem = selected;
            }
        }
        else if (Tree.SelectedItem is TreeViewItem selected) { selected.IsExpanded = true; }
    }
    private ToggleButton SortButton(string name, string tip, string icon, FileStatusListSortMode basis)
    {
        ToggleButton button = new() { Name = Prefix(name), Content = Icon(icon), Width = 24, Height = 24, MinHeight = 24,
            CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(1), Padding = new Thickness(2, 0) };
        ToolTip.SetTip(button, T(tip)); button.Click += (_, _) => { if (!_updatingToolbar) { SortMode = IsFlat ? (FileStatusListSortMode)((int)basis + 1) : basis; } };
        _toolbar.Children.Add(button); _toolbarRoles[button] = name; return button;
    }
    private ToggleButton DiffButton(string name, string caption, string tip, string color, Action<bool> update)
    {
        ToggleButton button = new() { Name = Prefix(name), Content = caption, Width = 24, Height = 24, MinHeight = 24, IsChecked = true,
            CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(1), Padding = new Thickness(2, 0), Tag = Color.Parse(color) };
        ToolTip.SetTip(button, T(tip)); button.Click += (_, _) => { if (!_updatingToolbar) { update(button.IsChecked == true); Rebuild(); } };
        _toolbar.Children.Add(button); _toolbarRoles[button] = name; return button;
    }
    private Button IconButton(string name, string tip, string icon, Action action)
    {
        Button button = new() { Name = Prefix(name), Content = Icon(icon), Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(2, 0) };
        ToolTip.SetTip(button, T(tip)); button.Click += (_, _) => action(); return button;
    }
    private Control DefaultFileHeader(FileStatusListEntry entry)
        => IconHeader(ExportDialogControls.DisplayPath(entry.DisplayPath ?? entry.Path)
            + (entry.OriginalPath is null ? "" : " (" + ExportDialogControls.DisplayPath(entry.OriginalPath) + ")"), IconName(entry));
    private Control IconHeader(string label, string icon)
    {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("16,*"), Height = 24, ClipToBounds = true };
        header.Children.Add(Icon(icon)); TextBlock text = new() { Text = label, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1); header.Children.Add(text); ToolTip.SetTip(header, label); return header;
    }
    private Image Icon(string name)
    {
        if (!_bitmaps.TryGetValue(name, out Bitmap? bitmap))
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + name)); bitmap = new(stream); _bitmaps[name] = bitmap;
        }
        Image image = new() { Source = bitmap, Width = 16, Height = 16, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true };
        _images.Add(image, bitmap); return image;
    }
    private void UpdateToolbar()
    {
        _updatingToolbar = true;
        try
        {
            _viewMode.Content = Icon(IsFlat ? "DocumentTree.png" : "FileTree.png");
            _byPath.IsChecked = SortMode is FileStatusListSortMode.FilePath or FileStatusListSortMode.FilePathFlat;
            _byExtension.IsChecked = SortMode is FileStatusListSortMode.FileExtension or FileStatusListSortMode.FileExtensionFlat;
            _byStatus.IsChecked = SortMode is FileStatusListSortMode.FileStatus or FileStatusListSortMode.FileStatusFlat;
            foreach (MenuItem menu in _sortMenus) { menu.IsChecked = menu.Name == Prefix("Sort_" + SortMode); }
            _dense.IsChecked = DenseTree; _dense.IsEnabled = !IsFlat;
            _flatGroups.IsChecked = ShowGroupNodesInFlatList; _flatGroups.IsEnabled = IsFlat && SortMode != FileStatusListSortMode.FilePathFlat;
            bool comparison = _groups.Any(group => group.IsBaseComparison);
            bool noFiles = Entries.Count == 0 && _groups.Count <= 1 && !_groups.Any(group => group.AlwaysVisible);
            _noFiles.IsVisible = noFiles; _filterRow.IsVisible = !noFiles;
            foreach (ToggleButton toggle in new[] { _unequal, _onlyB, _onlyA, _same }) { toggle.IsVisible = comparison; }
            foreach ((Control control, string role) in _toolbarRoles)
            {
                bool contextual = role is "Unequal" or "OnlyB" or "OnlyA" or "Same" ? comparison : true;
                control.IsVisible = contextual && !_options.HiddenToolbarItems.Contains(role, StringComparer.Ordinal);
            }
            ApplySortButtonPalette(); ApplyDiffButtonColors();
        }
        finally { _updatingToolbar = false; }
    }
    private void RefreshToolbarMenu()
    {
        _toolbarMenu.Items.Clear();
        foreach (Control control in _toolbar.Children)
        {
            if (!_toolbarRoles.TryGetValue(control, out string? role)) { role = control.Name ?? ""; }
            if (role.Length == 0) { continue; }
            string capturedRole = role;
            MenuItem item = new() { Header = ToolTip.GetTip(control)?.ToString() ?? role, ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = !_options.HiddenToolbarItems.Contains(role, StringComparer.Ordinal), IsEnabled = role != "Settings" };
            item.Click += (_, _) =>
            {
                List<string> hidden = _options.HiddenToolbarItems.Where(value => value != capturedRole).ToList();
                if (!item.IsChecked) { hidden.Add(capturedRole); }
                control.IsVisible = item.IsChecked; SetOptions(_options with { HiddenToolbarItems = hidden });
            };
            _toolbarMenu.Items.Add(item);
        }
    }
    private void ThemeChanged(object? sender, EventArgs args) { ApplyFilterPalette(); ApplySortButtonPalette(); ApplyDiffButtonColors(); }
    private void ApplySortButtonPalette()
    {
        // Fluent's checked styles draw on their ContentPresenter, bypassing Background setters.
        // Resolve every sort-button state from the original palette rather than the Linux accent.
        foreach (ToggleButton button in new[] { _byPath, _byExtension, _byStatus })
        {
            foreach ((string state, string background, string border, string foreground) in new[]
            {
                ("", "Button", "ButtonBorder", "ControlText"), ("PointerOver", "Hover", "ButtonBorder", "ControlText"),
                ("Pressed", "Pressed", "ButtonBorder", "ControlText"), ("Disabled", "Button", "ButtonBorder", "Disabled"),
                ("Checked", "Highlight", "Highlight", "HighlightText"), ("CheckedPointerOver", "Highlight", "Highlight", "HighlightText"),
                ("CheckedPressed", "Highlight", "Highlight", "HighlightText"), ("CheckedDisabled", "Button", "ButtonBorder", "Disabled")
            })
            {
                SetBrush("Background", background); SetBrush("BorderBrush", border); SetBrush("Foreground", foreground);
                void SetBrush(string role, string source)
                {
                    if (button.TryFindResource("Classic" + source + "Brush", button.ActualThemeVariant, out object? value))
                    { button.Resources["ToggleButton" + role + state] = value!; }
                }
            }
        }
    }
    private void ApplyFilterPalette()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        _filterBackgroundBinding?.Dispose(); _filterBackgroundBinding = null;
        if (FilterError is null && !IsFilterActive) { _filterBackgroundBinding = _filter.Bind(BackgroundProperty, new DynamicResourceExtension("ClassicEditorBrush")); }
        else { _filter.Background = Brush.Parse(FilterError is not null ? dark ? "#6e1919" : "#ffc8c8" : dark ? "#076641" : "#c8ffc8"); }
        ToolTip.SetTip(_filter, FilterError ?? T("Filter files using a regular expression..."));
    }
    private void ApplyDiffButtonColors()
    {
        foreach (ToggleButton button in new[] { _unequal, _onlyB, _onlyA, _same })
        {
            if (button.Tag is not Color original) { continue; }
            Color panel = button.TryFindResource("ClassicWindowBrush", button.ActualThemeVariant, out object? value) && value is ISolidColorBrush window ? window.Color : Colors.White;
            // A ToolStrip's semantic foreground stays colored when checked. A filled Fluent accent
            // makes those hues unreadable; use source neutral surfaces and a source highlight border.
            foreach ((string state, string source) in new[] { ("Checked", "Window"), ("PointerOver", "Hover"), ("Pressed", "Pressed"),
                ("CheckedPointerOver", "Hover"), ("CheckedPressed", "Pressed") })
            {
                if (button.TryFindResource("Classic" + source + "Brush", button.ActualThemeVariant, out value))
                { button.Resources["ToggleButtonBackground" + state] = value!; }
                if (state.StartsWith("Checked", StringComparison.Ordinal) && button.TryFindResource("ClassicHighlightBrush", button.ActualThemeVariant, out value))
                { button.Resources["ToggleButtonBorderBrush" + state] = value!; }
            }
            // Fluent sets these resources on the rendered presenter for checked/hover/pressed states.
            // Preserve the original status hue, adapting it using ColorHelper.EnsureContrast's rule.
            foreach (string state in new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" })
            {
                Color background = button.TryFindResource("ToggleButtonBackground" + state, button.ActualThemeVariant, out value) && value is ISolidColorBrush brush
                    ? Blend(brush.Color, brush.Opacity, panel) : panel;
                button.Resources["ToggleButtonForeground" + state] = new SolidColorBrush(FileStatusListColors.Contrasting(original, background));
            }
            button.Foreground = (IBrush)button.Resources["ToggleButtonForeground"]!;
        }

        static Color Blend(Color color, double opacity, Color background)
        {
            double alpha = color.A / 255d * opacity;
            byte Channel(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
            return Color.FromRgb(Channel(color.R, background.R), Channel(color.G, background.G), Channel(color.B, background.B));
        }
    }
    private static string SortCaption(FileStatusListSortMode mode) => mode switch
    {
        FileStatusListSortMode.FilePath => "Group by file &path - tree", FileStatusListSortMode.FilePathFlat => "Group by &file path - flat",
        FileStatusListSortMode.FileExtension => "Group by file &extension - tree", FileStatusListSortMode.FileExtensionFlat => "Group by file e&xtension - flat",
        FileStatusListSortMode.FileStatus => "Group by file &status - tree", _ => "Group by file s&tatus - flat"
    };
    private static string SortItemName(FileStatusListSortMode mode) => mode switch
    {
        FileStatusListSortMode.FilePath => "tsmiGroupByFilePathTree", FileStatusListSortMode.FilePathFlat => "tsmiGroupByFilePathFlat",
        FileStatusListSortMode.FileExtension => "tsmiGroupByFileExtensionTree", FileStatusListSortMode.FileExtensionFlat => "tsmiGroupByFileExtensionFlat",
        FileStatusListSortMode.FileStatus => "tsmiGroupByFileStatusTree", _ => "tsmiGroupByFileStatusFlat"
    };
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; CancelPendingFilter();
        _store.Changed -= PreferencesChanged; ActualThemeVariantChanged -= ThemeChanged;
        _filter.PropertyChanged -= FilterPropertyChanged; _filter.SelectionChanged -= FilterSelectionChanged;
        _filterBackgroundBinding?.Dispose(); _filter.IsDropDownOpen = false;
        _viewMode.Flyout?.Hide();
        foreach (Button button in _toolbar.Children.OfType<Button>()) { button.Flyout?.Hide(); }
        // Dispose is also safe while a host is detaching/replacing the view. Queued layout must never
        // encounter an Image whose Bitmap platform handle has already been released.
        foreach ((Image image, Bitmap _) in _images) { image.Source = null; }
        _images.Clear(); Content = null;
        foreach (Bitmap bitmap in _bitmaps.Values) { bitmap.Dispose(); } _bitmaps.Clear();
    }
}
