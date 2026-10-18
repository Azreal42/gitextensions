using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop.Controls;

internal sealed record SettingsNavigationPage(string Id, string Title, Control Content, string? Icon = null,
    bool IsInstantSave = false, IReadOnlyList<string>? SearchKeywords = null,
    IReadOnlyList<SettingsNavigationPage>? Children = null, string? NavigationTitle = null);

/// <summary>Settings pages keep their controls and drafts while the tree changes only the visible page.</summary>
internal sealed class SettingsNavigationView : UserControl
{
    private readonly TreeView _tree = new() { Name = "SettingsNavigationTree" };
    private readonly TextBox _find = new() { Name = "SettingsFind", Margin = new Thickness(4), MinHeight = 24 };
    private readonly ContentControl _pageContent = new()
    {
        Name = "SettingsPageContent", HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top
    };
    private readonly Dictionary<string, TreeViewItem> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<TreeViewItem, TreeViewItem?> _parents = [];
    private readonly Dictionary<TreeViewItem, (Border Surface, TextBlock Text)> _headers = [];
    private readonly Dictionary<TreeViewItem, IReadOnlyList<string>> _searchKeywords = [];
    private readonly List<TreeViewItem> _matches = [];
    private long _selectionVersion;
    private long? _pendingContentFocus;
    private TopLevel? _host;
    private bool _selecting;

    internal SettingsNavigationView(IReadOnlyList<SettingsNavigationPage> roots, TranslationCatalog translations)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(translations);
        if (roots.Count == 0) { throw new ArgumentException("Settings needs at least one page.", nameof(roots)); }
        Name = "SettingsNavigation";
        _find.Watermark = translations.Translate("TranslatedStrings", "_settingsTypeToFind", "Text", "Type to find");
        _tree.ItemsSource = roots.Select(page => CreateNode(page, null)).ToArray();
        _tree.SelectionChanged += (_, _) =>
        {
            if (!_selecting && _tree.SelectedItem is TreeViewItem { Tag: SettingsNavigationPage page } node)
            { ShowPage(node, page, focusContent: false); }
        };
        _find.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { HighlightMatches(); } };
        // Consume Enter before the Window's default OK button can publish the form.
        _find.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Enter) { return; }
            args.Handled = true;
            if (_matches.Count == 0) { return; }
            int index = _tree.SelectedItem is TreeViewItem selected ? _matches.IndexOf(selected) : -1;
            SelectNode(_matches[(index + 1) % _matches.Count], focusContent: false);
        }, RoutingStrategies.Tunnel);

        Styles.Add(new Style(selector => selector.OfType<Border>().Class("settings-search-match"))
        { Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ClassicHighlightBrush")) } });
        Styles.Add(new Style(selector => selector.OfType<TextBlock>().Class("settings-search-match"))
        { Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
        ScrollViewer contentScroll = new()
        {
            Content = _pageContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        // An unconstrained horizontal measure would stretch '*' columns to the
        // longest script argument. Keep normal pages at the viewport width and
        // scroll only when a page's actual minimum cannot fit a narrow window.
        contentScroll.PropertyChanged += (_, args) => { if (args.Property == ScrollViewer.ViewportProperty) { UpdateContentWidth(); } };
        _pageContent.PropertyChanged += (_, args) => { if (args.Property == ContentControl.ContentProperty) { UpdateContentWidth(); } };
        void UpdateContentWidth()
        {
            double minimum = _pageContent.Content is Control content ? content.MinWidth + content.Margin.Left + content.Margin.Right : 0;
            _pageContent.Width = Math.Max(contentScroll.Viewport.Width, minimum);
        }
        Grid navigation = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        navigation.Children.Add(_find);
        Control treeSurface = ClassicTreeViewAppearance.Wrap(_tree, inset: 3, showRootLines: false);
        Grid.SetRow(treeSurface, 1); navigation.Children.Add(treeSurface);
        Border sidebar = new() { Child = navigation, BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebar.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ClassicBorderBrush"));
        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("200,*") };
        layout.Children.Add(sidebar); Grid.SetColumn(contentScroll, 1); layout.Children.Add(contentScroll); Content = layout;

        AttachedToVisualTree += (_, _) =>
        {
            _host = TopLevel.GetTopLevel(this);
            if (_host is not null)
            {
                _host.AddHandler(PointerPressedEvent, CancelPendingFocus, RoutingStrategies.Tunnel, handledEventsToo: true);
                _host.AddHandler(KeyDownEvent, CancelPendingFocus, RoutingStrategies.Tunnel, handledEventsToo: true);
            }
            if (_pendingContentFocus is long request) { QueueContentFocus(request); }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_host is not null)
            {
                _host.RemoveHandler(PointerPressedEvent, CancelPendingFocus);
                _host.RemoveHandler(KeyDownEvent, CancelPendingFocus);
            }
            _host = null; _pendingContentFocus = null;
        };
        SelectedPage = roots[0];
        SelectNode(_nodes[roots[0].Id], focusContent: false);
    }

    internal SettingsNavigationPage SelectedPage { get; private set; }
    internal event Action<SettingsNavigationPage, bool>? PageSelected;

    internal bool SelectPage(string id, bool focusContent = false)
    {
        if (!_nodes.TryGetValue(id, out TreeViewItem? node)) { return false; }
        if (node.Items.Count != 0) { node.IsExpanded = true; }
        SelectNode(node, focusContent);
        return true;
    }

    private TreeViewItem CreateNode(SettingsNavigationPage page, TreeViewItem? parent)
    {
        ArgumentNullException.ThrowIfNull(page.Content);
        TextBlock text = new() { Text = page.NavigationTitle ?? page.Title, VerticalAlignment = VerticalAlignment.Center };
        Grid label = new() { ColumnDefinitions = new ColumnDefinitions("16,*"), ColumnSpacing = 3, MinHeight = ClassicTreeViewAppearance.RowHeight };
        if (page.Icon is not null) { label.Children.Add(OriginalMenuItems.Icon(page.Icon)); }
        Grid.SetColumn(text, 1); label.Children.Add(text);
        Border surface = new() { Child = label, Padding = new Thickness(1, 0), IsHitTestVisible = false };
        TreeViewItem node = new() { Name = "Settings" + page.Id + "Node", Tag = page, DataContext = page, Header = surface };
        // Native TreeViewItem owns focus and arrow navigation; its decorative layout must not take it.
        node.TemplateApplied += (_, args) =>
        { if (args.NameScope.Find<Border>("PART_LayoutRoot") is Border itemSurface) { itemSurface.Focusable = false; } };
        _nodes.Add(page.Id, node); _parents.Add(node, parent); _headers.Add(node, (surface, text));
        if (page.Children is not null) { node.ItemsSource = page.Children.Select(child => CreateNode(child, node)).ToArray(); }
        return node;
    }

    private void SelectNode(TreeViewItem node, bool focusContent)
    {
        _selecting = true;
        try { ExpandAncestors(node); if (focusContent && node.Items.Count != 0) { node.IsExpanded = true; } _tree.SelectedItem = node; }
        finally { _selecting = false; }
        ShowPage(node, (SettingsNavigationPage)node.Tag!, focusContent);
    }

    private void ShowPage(TreeViewItem node, SettingsNavigationPage page, bool focusContent)
    {
        long request = ++_selectionVersion;
        _pendingContentFocus = null;
        SelectedPage = page;
        if (!ReferenceEquals(_pageContent.Content, page.Content)) { _pageContent.Content = page.Content; }
        ExpandAncestors(node); node.BringIntoView();
        PageSelected?.Invoke(page, focusContent);
        if (!focusContent || request != _selectionVersion) { return; }
        _pendingContentFocus = request;
        if (TopLevel.GetTopLevel(this) is not null) { QueueContentFocus(request); }
    }

    private void ExpandAncestors(TreeViewItem node)
    {
        for (TreeViewItem? parent = _parents[node]; parent is not null; parent = _parents[parent]) { parent.IsExpanded = true; }
    }

    private void HighlightMatches()
    {
        _matches.Clear();
        string query = _find.Text ?? "";
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (TreeViewItem node in _nodes.Values)
        {
            SettingsNavigationPage page = (SettingsNavigationPage)node.Tag!;
            bool match = !string.IsNullOrWhiteSpace(query)
                && (page.Title.Contains(query, StringComparison.InvariantCultureIgnoreCase)
                    || words.All(word => GetSearchKeywords(node).Any(keyword => keyword.Contains(word, StringComparison.InvariantCultureIgnoreCase))));
            _headers[node].Surface.Classes.Set("settings-search-match", match);
            _headers[node].Text.Classes.Set("settings-search-match", match);
            if (match) { _matches.Add(node); ExpandAncestors(node); }
        }
        if (_matches.Count != 0) { _tree.UpdateLayout(); _matches[^1].BringIntoView(); }
    }

    private IReadOnlyList<string> GetSearchKeywords(TreeViewItem node)
    {
        SettingsNavigationPage page = (SettingsNavigationPage)node.Tag!;
        if (page.SearchKeywords is not null) { return page.SearchKeywords; }
        if (_searchKeywords.TryGetValue(node, out IReadOnlyList<string>? cached)) { return cached; }
        return _searchKeywords[node] = GetChildrenText(page.Content);
    }

    private static IReadOnlyList<string> GetChildrenText(Control root)
    {
        // Windows caches page text on the first search, including drafts in text boxes.
        // Logical children also cover pages which have never been shown by the content host.
        List<string> texts = [];
        Queue<(Control Control, bool AncestorsEnabled)> queue = new();
        HashSet<Control> visited = [];
        queue.Enqueue((root, true));
        while (queue.TryDequeue(out var entry))
        {
            Control control = entry.Control;
            if (!visited.Add(control) || !control.IsVisible || control is NumericUpDown) { continue; }
            bool enabled = entry.AncestorsEnabled && control.IsEnabled;
            if (control is ComboBox combo)
            {
                // Its logical items are the complete drop-down list. Index only the displayed selection.
                if (enabled)
                {
                    if (combo.IsEditable) { Add(combo.Text); }
                    else if (combo.SelectedItem is { } selected)
                    {
                        var template = combo.SelectionBoxItemTemplate ?? combo.ItemTemplate;
                        if (template is not null)
                        {
                            if (template.Build(selected) is Control label) { queue.Enqueue((label, true)); }
                        }
                        else if (selected is Control label) { queue.Enqueue((label, true)); }
                        else { Add(selected.ToString()); }
                    }
                }
                continue;
            }
            if (enabled)
            {
                Add(control switch
                {
                    TextBox input => input.Text,
                    TextBlock label => label.Text,
                    ContentControl { Content: string content } => content,
                    _ => null
                });
            }
            foreach (Control child in control.GetLogicalChildren().OfType<Control>()) { queue.Enqueue((child, enabled)); }
        }
        return texts;

        void Add(string? text) { if (!string.IsNullOrWhiteSpace(text)) { texts.Add(text.Trim()); } }
    }

    private void CancelPendingFocus(object? sender, RoutedEventArgs args) => _pendingContentFocus = null;

    private void QueueContentFocus(long request)
    {
        TopLevel? host = TopLevel.GetTopLevel(this);
        if (host is null) { return; }
        IInputElement? origin = host.FocusManager?.GetFocusedElement();
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingContentFocus != request) { return; }
            _pendingContentFocus = null;
            if (_selectionVersion != request || !IsEffectivelyVisible || !IsEffectivelyEnabled
                || TopLevel.GetTopLevel(this) != host || !ReferenceEquals(host.FocusManager?.GetFocusedElement(), origin)) { return; }
            // One bounded layout callback only; a later user choice or selection is never overridden.
            Control? input = new[] { SelectedPage.Content }.Concat(SelectedPage.Content.GetVisualDescendants().OfType<Control>())
                .FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
            input?.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }
}
