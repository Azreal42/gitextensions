using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public sealed class DashboardControl : UserControl, IDisposable
{
    private readonly Func<RepositoryHistoryState> _history;
    private readonly Action<RepositoryHistoryState> _save;
    private readonly Func<string, Task> _open;
    private readonly Window _owner;
    private readonly RepositoryBranchMetadataService _repositoryBranches;
    private readonly bool _ownsRepositoryBranches;
    private readonly Func<bool> _showRepositoryBranch;
    private readonly Func<GitExecutionEnvironment> _branchExecution;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly TextBox _search = new() { Name = "DashboardSearch", Height = 24, MinWidth = 160 };
    private readonly StackPanel _groups = new() { Spacing = 8, Margin = new Thickness(12, 8) };
    private readonly Border _logo = new() { Height = 68, Padding = new Thickness(14, 12) };
    private readonly Border _start = new() { Padding = new Thickness(20), VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Border _contribute = new() { Padding = new Thickness(20, 20, 20, 30) };
    private readonly Border _header = new() { Height = 68, Padding = new Thickness(12, 6) };
    private readonly List<ListBox> _lists = [];
    private readonly List<Bitmap> _bitmaps = [];
    private readonly Dictionary<string, (bool Valid, string Branch)> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(Image Icon, TextBlock Branch)>> _rows = new(StringComparer.Ordinal);
    private readonly List<(Image Original, Border Dark)> _adaptedIcons = [];
    private readonly List<MenuItem> _removeMissingMenus = [];
    private CancellationTokenSource? _refresh;
    private bool _disposed;
    private double _tileWidth = 300;

    public DashboardControl(Window owner, Func<RepositoryHistoryState> history, Action<RepositoryHistoryState> save, Func<string, Task> open, Func<Task> create, Func<Task> choose, Func<Task> clone)
    {
        Name = "RepositoryDashboard";
        _owner = owner; _history = history; _save = save; _open = open;
        MainWindow? main = owner as MainWindow;
        _repositoryBranches = main?.RepositoryBranches ?? new();
        _ownsRepositoryBranches = main is null;
        _showRepositoryBranch = main is null ? () => new PreferencesStore().Load().ShowRepoCurrentBranch : () => main.ShowRepositoryCurrentBranch;
        _branchExecution = main is null ? () => GitEnvironment.Current : () => main.RepositoryBranchExecution;
        owner.Closed += (_, _) => Dispose();
        _search.Watermark = T("Search repositories...");
        _search.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { RenderGroups(); } };
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && _lists.SelectMany(list => list.Items.OfType<DashboardRepository>()).FirstOrDefault() is { } entry)
            { e.Handled = true; await OpenAsync(entry.Path); }
        };
        Grid header = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = T("Recent repositories"), FontSize = DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 5.5 * 96 / 72, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(_search, 1); header.Children.Add(_search); _header.Child = header;
        _logo.Child = new Image { Source = LoadBitmap("GitExtensionsLogoWide.png"), Stretch = Stretch.Uniform };
        _start.Child = new StackPanel { Spacing = 8, Children = { Link(T("Create new repository"), "RepoCreate.png", create), Link(T("Open repository"), "RepoOpen.png", choose), Link(T("Clone repository"), "CloneRepoGit.png", clone) } };
        _contribute.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = T("Contribute"), FontSize = DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 5.5 * 96 / 72, Margin = new Thickness(0, 0, 0, 8) },
                Link(T("Develop"), "Develop.png", () => OpenUrlAsync("https://github.com/gitextensions/gitextensions")),
                Link(T("Donate"), "DollarSign.png", () => OpenUrlAsync("https://opencollective.com/gitextensions")),
                Link(T("Translate"), "Translate.png", () => OpenUrlAsync("https://github.com/gitextensions/gitextensions/wiki/Translations")),
                Link(T("Issues"), "BugReport.png", () => OpenUrlAsync("https://github.com/gitextensions/gitextensions/issues"))
            }
        };
        Grid left = new() { RowDefinitions = new RowDefinitions("68,*,Auto") };
        left.Children.Add(_logo); Grid.SetRow(_start, 1); left.Children.Add(_start); Grid.SetRow(_contribute, 2); left.Children.Add(_contribute);
        DockPanel right = new(); right.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ClassicWindowBrush")); DockPanel.SetDock(_header, Dock.Top); right.Children.Add(_header);
        right.Children.Add(new ScrollViewer { Content = _groups, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid columns = new() { ColumnDefinitions = new ColumnDefinitions("*,213,12*,*") };
        Grid.SetColumn(left, 1); columns.Children.Add(left); Grid.SetColumn(right, 2); columns.Children.Add(right);
        Content = columns;
        AttachedToVisualTree += (_, _) => ApplyPalette();
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
    }

    public Task MetadataLoading { get; private set; } = Task.CompletedTask;
    public RepositoryHistoryState History => _history();

    public void Refresh(bool reload = true)
    {
        if (_disposed) { return; }
        if (reload) { _metadata.Clear(); _repositoryBranches.Invalidate(); }
        if (reload) { _search.Text = ""; }
        RenderGroups();
        RefreshMetadata();
        Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible) { _search.Focus(); } });
    }

    internal Task RefreshMetadata()
    {
        if (_disposed) { return MetadataLoading; }
        _refresh?.Cancel();
        CancellationTokenSource request = new(); _refresh = request;
        bool includeBranch = _showRepositoryBranch();
        foreach (var rows in _rows.Values)
        {
            foreach ((Image _, TextBlock branch) in rows)
            { branch.IsVisible = includeBranch; if (!includeBranch) { branch.Text = ""; } }
        }
        Task previous = MetadataLoading;
        Task read = LoadMetadataAsync(request, includeBranch, _branchExecution());
        MetadataLoading = previous.IsCompletedSuccessfully ? read : Task.WhenAll(previous, read);
        return MetadataLoading;
    }

    public void SetFavorite(string path, bool enabled) { Save(_history().Favorite(path, enabled)); }
    public void AssignCategory(string path, string? category) { Save(_history().AssignCategory(path, category)); }
    public void RemoveRepository(string path, bool favorite) { Save(_history().Remove(path, favorite)); }

    private void Save(RepositoryHistoryState history) { _save(history); Refresh(reload: false); }

    private void RenderGroups()
    {
        if (_disposed) { return; }
        _groups.Children.Clear(); _lists.Clear(); _rows.Clear(); _removeMissingMenus.Clear();
        RepositoryHistoryState history = _history();
        IEnumerable<RepositoryHistoryRow> recent = Filter(Select(history));
        IEnumerable<RepositoryHistoryRow> favorites = Filter(Select(history with { Recent = history.Favorites, SortRecent = history.SortFavorites || history.SortRecent }));
        double preferred = history.RecentReposComboMinWidth > 0 ? history.RecentReposComboMinWidth : recent.Concat(favorites).Select(row => MeasureCaption(row.Caption) + 32).DefaultIfEmpty(250).Max();
        _tileWidth = preferred + 50;
        AddGroup(T("Recent repositories"), recent, false, null, favorites.Where(item => string.IsNullOrWhiteSpace(item.Entry.Category)));
        foreach (IGrouping<string?, RepositoryHistoryRow> group in favorites.Where(item => !string.IsNullOrWhiteSpace(item.Entry.Category)).GroupBy(item => item.Entry.Category).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        { AddGroup(group.Key!, group, true, group.Key); }

        IEnumerable<RepositoryHistoryRow> Filter(IEnumerable<RepositoryHistoryRow> entries)
            => entries.Where(item => item.Path.Contains(_search.Text ?? "", StringComparison.CurrentCultureIgnoreCase));
        IEnumerable<RepositoryHistoryRow> Select(RepositoryHistoryState state)
        {
            RepositoryHistorySelection selection = RepositoryHistoryPresentation.Select(state, MeasureCaption);
            return selection.Top.Concat(selection.Recent).DistinctBy(row => row.Path, StringComparer.Ordinal);
        }
    }

    private void AddGroup(string title, IEnumerable<RepositoryHistoryRow> entries, bool favorite, string? category, IEnumerable<RepositoryHistoryRow>? uncategorizedFavorites = null)
    {
        DashboardRepository[] repositories = entries.Select(item => Row(item, favorite)).Concat((uncategorizedFavorites ?? []).Select(item => Row(item, true))).ToArray();
        static DashboardRepository Row(RepositoryHistoryRow item, bool favorite) => new(item.Path, favorite, item.Entry.Category, item.IsAnchored) { Caption = item.Caption };
        if (repositories.Length == 0) { return; }
        TextBlock heading = new() { Text = title, FontSize = DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 2, Margin = new Thickness(0, 4, 0, 2) };
        List<MenuItem> actions = [];
        if (!favorite) { actions.Add(Menu(T("Clear all recent repositories"), async () =>
        {
            if (await Dialogs.ConfirmAsync(_owner, T("Clear recent repositories"), T("Do you want to clear the list of recent repositories?\n\nThe action cannot be undone.")))
            { Save(_history() with { Recent = [] }); }
        })); }
        else if (category is not null)
        {
            actions.Add(Menu(T("Rename category"), async () =>
            {
                string? result = await PromptCategoryAsync(category);
                if (result is not null) { Save(_history().RenameCategory(category, result)); }
            }));
            actions.Add(Menu(T("Delete category"), async () =>
            {
                if (await Dialogs.ConfirmAsync(_owner, T("Delete Category"), string.Format(T("Do you want to delete category \"{0}\" with {1} repositories?\n\nThe action cannot be undone."), category, repositories.Length)))
                { Save(_history().RenameCategory(category, null)); }
            }));
        }
        Grid headingRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headingRow.Children.Add(heading);
        if (actions.Count > 0)
        {
            ContextMenu groupMenu = new() { ItemsSource = actions }; heading.ContextMenu = groupMenu;
            Button taskLink = new() { Content = T("Actions"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 0), Foreground = Brush.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#9393FF" : "#0000FF") };
            taskLink.Click += (_, _) => groupMenu.Open(taskLink);
            Grid.SetColumn(taskLink, 1); headingRow.Children.Add(taskLink);
        }
        _groups.Children.Add(headingRow);
        ListBox list = new()
        {
            Name = favorite ? "DashboardFavoriteRepositories" : "DashboardRecentRepositories",
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemsSource = repositories,
            ItemTemplate = new FuncDataTemplate<DashboardRepository>((entry, _) => entry is null ? new TextBlock() : Tile(entry))
        };
        list.DoubleTapped += async (_, _) => { if (list.SelectedItem is DashboardRepository entry) { await OpenAsync(entry.Path); } };
        list.KeyDown += async (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is DashboardRepository entry) { e.Handled = true; await OpenAsync(entry.Path); } };
        _lists.Add(list); _groups.Children.Add(list);
    }

    private Control Tile(DashboardRepository entry)
    {
        Grid tile = new() { Width = _tileWidth, MinHeight = 54, ColumnDefinitions = new ColumnDefinitions("40,*"), Margin = new Thickness(0, 3, 8, 3), Tag = entry };
        Image icon = new() { Width = 32, Height = 32, Source = LoadBitmap("Icons/DashboardFolderGit.png"), VerticalAlignment = VerticalAlignment.Center };
        tile.Children.Add(icon);
        StackPanel text = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        TextBlock path = new() { Text = entry.Caption ?? entry.Path, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = entry.Pinned ? FontWeight.Bold : FontWeight.Normal };
        if (entry.IsFavorite) { path.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Color.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#DAA520" : "#B8860B"))); }
        TextBlock branch = new() { Name = "DashboardBranch", FontSize = Math.Max(1, DesktopFonts.For(DesktopFontRole.Application).SizeInDips - 96d / 72), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Color.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#87CEFA" : "#647FD2"))) };
        text.Children.Add(path); text.Children.Add(branch); Grid.SetColumn(text, 1); tile.Children.Add(text); ToolTip.SetTip(tile, entry.Path);
        if (!_rows.TryGetValue(entry.Path, out List<(Image, TextBlock)>? rows)) { _rows[entry.Path] = rows = []; }
        rows.Add((icon, branch));
        if (_metadata.TryGetValue(entry.Path, out var metadata)) { ApplyMetadata(icon, branch, metadata); }
        List<MenuItem> menu =
        [
            Menu(T("Open repository"), () => OpenAsync(entry.Path)),
            Menu(T("Show in folder"), () => OpenUrlAsync(entry.Path)),
            Menu(T(entry.IsFavorite ? "Remove from favorites" : "Add to favorites"), () => { SetFavorite(entry.Path, !entry.IsFavorite); return Task.CompletedTask; }),
            Menu(T("Remove project from the list"), () => { RemoveRepository(entry.Path, entry.IsFavorite); return Task.CompletedTask; })
        ];
        if (entry.IsFavorite)
        {
            List<MenuItem> categories = [Menu(T("(none)"), () => { AssignCategory(entry.Path, null); return Task.CompletedTask; })];
            categories.AddRange(_history().Favorites.Select(item => item.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct().OrderBy(category => category).Select(category => Menu(category!, () => { AssignCategory(entry.Path, category); return Task.CompletedTask; })));
            categories.Add(Menu(T("Add new..."), async () =>
            {
                string? result = await PromptCategoryAsync();
                if (result is not null) { AssignCategory(entry.Path, result); }
            }));
            menu.Add(new MenuItem { Header = T("Categories"), ItemsSource = categories });
        }
        MenuItem removeMissing = Menu(T("Remove missing projects from the list"), async () =>
        {
            await MetadataLoading;
            HashSet<string> invalid = _metadata.Where(item => !item.Value.Valid).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            RepositoryHistoryState history = _history();
            Save(history with { Recent = history.Recent.Where(item => !invalid.Contains(item.Path)).ToList(), Favorites = history.Favorites.Where(item => !invalid.Contains(item.Path)).ToList() });
        });
        removeMissing.IsVisible = HasInvalidEntries(); _removeMissingMenus.Add(removeMissing); menu.Add(removeMissing);
        tile.ContextMenu = new ContextMenu { ItemsSource = menu };
        return tile;
    }

    private async Task LoadMetadataAsync(CancellationTokenSource request, bool includeBranch, GitExecutionEnvironment execution)
    {
        CancellationToken token = request.Token;
        try
        {
            string[] paths = _history().Recent.Concat(_history().Favorites).Select(item => item.Path).Distinct(StringComparer.Ordinal).ToArray();
            await Task.WhenAll(paths.Select(async path =>
            {
                RepositoryBranchMetadata result = await _repositoryBranches.ReadAsync(path, includeBranch, execution, token);
                token.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _disposed || !ReferenceEquals(_refresh, request)) { return; }
                    string name = includeBranch && _showRepositoryBranch() ? result.IsDetached ? T("Detached HEAD") : result.Branch : "";
                    var metadata = (result.Valid, name);
                    _metadata[path] = metadata;
                    if (_rows.TryGetValue(path, out var rows)) { foreach ((Image icon, TextBlock branch) in rows) { ApplyMetadata(icon, branch, metadata); } }
                    foreach (MenuItem menu in _removeMissingMenus) { menu.IsVisible = HasInvalidEntries(); }
                });
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_refresh, request)) { _refresh = null; }
            request.Dispose();
        }
    }

    private void ApplyMetadata(Image icon, TextBlock branch, (bool Valid, string Branch) metadata)
    {
        icon.Source = LoadBitmap(metadata.Valid ? "Icons/DashboardFolderGit.png" : "Icons/DashboardFolderError.png");
        Avalonia.Automation.AutomationProperties.SetName(icon, T(metadata.Valid ? "Git repository" : "Invalid repository"));
        branch.IsVisible = _showRepositoryBranch();
        branch.Text = branch.IsVisible ? metadata.Branch : "";
    }

    private void ApplyPalette()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        Background = new ImageBrush(LoadBitmap(dark ? "Backgrounds/DashboardBackgroundGrey.png" : "Backgrounds/DashboardBackgroundBlue.png")) { Stretch = Stretch.UniformToFill };
        _logo.Background = Brush.Parse(dark ? "#5A5A5A" : "#137AD4");
        _start.Background = Brush.Parse(dark ? "#202020" : "#DBEBF8");
        _contribute.Background = Brush.Parse(dark ? "#2E2E2E" : "#E6F1FA");
        _header.Background = Brush.Parse(dark ? "#4A4A4A" : "#ACD0EF");
        _search.Background = Brush.Parse(dark ? "#202020" : "#F8F8FF");
        foreach (var icon in _adaptedIcons) { icon.Original.IsVisible = !dark; icon.Dark.IsVisible = dark; }
        RenderGroups();
    }

    private Button Link(string title, string icon, Func<Task> action)
    {
        Button button = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        Bitmap bitmap = LoadBitmap("Icons/" + icon);
        Control glyph = new Image { Source = bitmap, Width = 16, Height = 16 };
        if (icon is "Develop.png" or "Translate.png")
        {
            // The original dashboard adapts these monochrome glyphs to the theme.
            Image original = (Image)glyph;
            Border dark = new() { Width = 16, Height = 16, Background = Brush.Parse("#F0F0F0"), OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform } };
            Grid adapted = new() { Width = 16, Height = 16 }; adapted.Children.Add(original); adapted.Children.Add(dark);
            _adaptedIcons.Add((original, dark)); glyph = adapted;
        }
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { glyph, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center } } };
        button.Click += async (_, _) => { try { await action(); } catch (Exception exception) { await Dialogs.ShowErrorAsync(_owner, exception.Message); } };
        return button;
    }

    private MenuItem Menu(string title, Func<Task> action)
    {
        MenuItem menu = new() { Header = title };
        menu.Click += async (_, _) => { try { await action(); } catch (Exception exception) { await Dialogs.ShowErrorAsync(_owner, exception.Message); } };
        return menu;
    }

    private async Task OpenAsync(string path) { await _open(path); }

    private Task<string?> PromptCategoryAsync(string? original = null)
        => new DashboardCategoryDialog(_history().Favorites.Select(item => item.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Cast<string>(), original).ShowDialog<string?>(_owner);

    private bool HasInvalidEntries() => _history().Recent.Concat(_history().Favorites).Any(entry => _metadata.TryGetValue(entry.Path, out var value) && !value.Valid);

    private static Task OpenUrlAsync(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { url } });
        return Task.CompletedTask;
    }

    private Bitmap LoadBitmap(string path)
    {
        // Reuse the upstream bitmap resources; cache them for the dashboard lifetime.
        if (_bitmapCache.TryGetValue(path, out Bitmap? bitmap)) { return bitmap; }
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/" + path));
        bitmap = new Bitmap(stream); _bitmaps.Add(bitmap); _bitmapCache[path] = bitmap; return bitmap;
    }
    private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.Ordinal);
    private string T(string source) => _translations.Translate(source);
    private double MeasureCaption(string text) => new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Black).Width;

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _refresh?.Cancel();
        if (_ownsRepositoryBranches) { MetadataLoading = FinishOwnedReadsAsync(MetadataLoading); }
        foreach (Bitmap bitmap in _bitmaps) { bitmap.Dispose(); }
    }

    private async Task FinishOwnedReadsAsync(Task pending)
    {
        await _repositoryBranches.DisposeAsync();
        await pending;
    }
}

public sealed record DashboardRepository(string Path, bool IsFavorite, string? Category, bool Pinned)
{
    public string? Caption { get; init; }
}
