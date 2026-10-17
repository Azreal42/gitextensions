using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original two-list history editor; only OK returns an updated history.</summary>
public sealed class RecentRepositoriesSettingsDialog : Window
{
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private RepositoryHistoryState _draft;
    private RepositoryHistorySelection _presentation = new([], []);
    private readonly NumericUpDown _topCount = Number("_NO_TRANSLATE_maxRecentRepositories", 0, 1_000_000);
    private readonly NumericUpDown _historySize = Number("_NO_TRANSLATE_RecentRepositoriesHistorySize", 10, 999);
    private readonly NumericUpDown _minimumWidth = Number("comboMinWidthEdit", 0, 800);
    private readonly CheckBox _hideTop = new() { Name = "hideTopRepositoriesFromRecentList" };
    private readonly CheckBox _sortTop = new() { Name = "sortTopRepos" };
    private readonly CheckBox _sortRecent = new() { Name = "sortRecentRepos" };
    private readonly RadioButton _none = new() { Name = "dontShortenRB", GroupName = "Shortening" };
    private readonly RadioButton _significant = new() { Name = "mostSigDirRB", GroupName = "Shortening" };
    private readonly RadioButton _middle = new() { Name = "middleDotRB", GroupName = "Shortening" };
    private readonly ListBox _top = new() { Name = "TopLB", SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _recent = new() { Name = "AllRecentLB", SelectionMode = SelectionMode.Multiple };
    private decimal _previousWidth;
    private bool _adjustingWidth;
    private double _topColumnWidth, _recentColumnWidth;

    public RepositoryHistoryState? ReturnedHistory { get; private set; }
    public RepositoryHistoryState CurrentHistory => ReadHistory();
    public RepositoryHistorySelection Presentation => _presentation;

    public RecentRepositoriesSettingsDialog(RepositoryHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Fluent's glyph containers remain 32px tall even when the control itself is compact.
        Resources["CheckBoxMinHeight"] = 20d; Resources["RadioButtonMinHeight"] = 20d;
        _draft = state with { Recent = [.. state.Recent], Favorites = [.. state.Favorites] };
        Title = L("$this", "Recent repositories settings");
        Width = 684; Height = 361; MinWidth = 684; MinHeight = 361;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _topCount.Value = Math.Clamp(state.MaxTopRepositories, 0, 1_000_000);
        _historySize.Value = Math.Clamp(state.HistorySize, 10, 999);
        _minimumWidth.Value = state.RecentReposComboMinWidth <= 0 ? 0 : Math.Clamp(state.RecentReposComboMinWidth, 30, 800);
        _previousWidth = _minimumWidth.Value ?? 0;
        _hideTop.IsChecked = state.HideTopRepositoriesFromRecentList;
        _sortTop.IsChecked = state.SortTopRepos;
        _sortRecent.IsChecked = state.SortRecent;
        _none.IsChecked = state.ShorteningStrategy == RepositoryPathShorteningStrategy.None;
        _significant.IsChecked = state.ShorteningStrategy == RepositoryPathShorteningStrategy.MostSignDir;
        _middle.IsChecked = state.ShorteningStrategy == RepositoryPathShorteningStrategy.MiddleDots;
        if (_none.IsChecked != true && _significant.IsChecked != true && _middle.IsChecked != true) { _none.IsChecked = true; }
        Grid options = new() { ColumnDefinitions = new ColumnDefinitions("*,61"), Margin = new Thickness(8) };
        Field(options, L("maxRecentRepositories", "Maximum number of top repositories"), _topCount);
        Field(options, L("lblRecentRepositoriesHistorySize", "Maximum number of recent repositories"), _historySize);
        Option(options, _hideTop, L("hideTopRepositoriesFromRecentList", "Hide top repositories from recent repositories list"));
        Option(options, _sortTop, L("sortTopRepos", "Sort top repositories alphabetically"));
        Option(options, _sortRecent, L("sortRecentRepos", "Sort recent repositories alphabetically"));
        _none.Content = L("dontShortenRB", "Do not shorten  ");
        _middle.Content = L("middleDotRB", "Replace middle part with dots ");
        _significant.Content = L("mostSigDirRB", "The most significant directory ");
        foreach (RadioButton radio in new[] { _none, _middle, _significant })
        {
            radio.Height = 20; radio.MinHeight = 0; radio.Padding = new Thickness(4, 0, 0, 0); radio.Margin = new Thickness(0);
            radio.Resources["RadioButtonMinHeight"] = 20d;
            radio.VerticalContentAlignment = VerticalAlignment.Center;
        }
        StackPanel radios = new() { Spacing = 2, Children = { _none, _middle, _significant } };
        Border group = ClassicChrome.Border(new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = L("shorteningGB", "Shortening strategy") }, radios }
        }, new Thickness(1), new Thickness(6));
        FullRow(options, group, 4);
        Field(options, L("comboMinWidthLabel", "Combobox minimum width (0 = Autosize)"), _minimumWidth);
        FullRow(options, new TextBlock
        {
            Name = "comboMinWidthNote", Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = L("comboMinWidthNote", "NB: The width of the columns helps to visualise how the repository name will be shown in the combobox.")
        });

        PrepareList(_top, true); PrepareList(_recent, false);
        Grid preview = new() { RowDefinitions = new RowDefinitions("21,121,24,*"), Margin = new Thickness(0, 0, 8, 0) };
        Add(preview, new TextBlock { Name = "TopLabel", Text = L("TopLabel", "Top repositories"), Margin = new Thickness(3, 3, 0, 0) }, 0);
        Add(preview, _top, 1);
        Add(preview, new TextBlock { Name = "RecentLabel", Text = L("label1", "Recent repositories"), Margin = new Thickness(3, 5, 0, 0) }, 2);
        Add(preview, _recent, 3);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("350,*") };
        body.Children.Add(options); Grid.SetColumn(preview, 1); body.Children.Add(preview);
        Button ok = new() { Name = "Ok", Content = L("Ok", "OK"), IsDefault = true, Width = 75, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
        Button cancel = new() { Name = "Abort", Content = L("Abort", "Cancel"), IsCancel = true, Width = 75, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
        ok.Click += (_, _) => { ReturnedHistory = ReadHistory().Normalize(); Close(ReturnedHistory); };
        cancel.Click += (_, _) => Close(null);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(3), Children = { ok, cancel } };
        DockPanel root = new() { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(body); Content = root;
        _topCount.ValueChanged += (_, _) => RefreshPresentation();
        _hideTop.IsCheckedChanged += (_, _) => RefreshPresentation();
        _sortTop.IsCheckedChanged += (_, _) => RefreshPresentation();
        _sortRecent.IsCheckedChanged += (_, _) => RefreshPresentation();
        _none.IsCheckedChanged += (_, _) => RefreshPresentation();
        _middle.IsCheckedChanged += (_, _) => RefreshPresentation();
        _significant.IsCheckedChanged += (_, _) => RefreshPresentation();
        _minimumWidth.ValueChanged += (_, _) => MinimumWidthChanged();
        _recent.DoubleTapped += (_, _) => ChangeAnchor(_recent, RepositoryHistoryAnchor.Top);
        _top.DoubleTapped += (_, _) => ChangeAnchor(_top, RepositoryHistoryAnchor.Recent);
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(null); } };
        Opened += (_, _) => RefreshPresentation();
        ActualThemeVariantChanged += (_, _) => RefreshPresentation();
        RefreshPresentation();
    }

    private RepositoryHistoryState ReadHistory() => _draft with
    {
        MaxTopRepositories = (int)(_topCount.Value ?? 0), HistorySize = (int)(_historySize.Value ?? 30),
        HideTopRepositoriesFromRecentList = _hideTop.IsChecked == true,
        SortTopRepos = _sortTop.IsChecked == true, SortRecent = _sortRecent.IsChecked == true,
        RecentReposComboMinWidth = (int)(_minimumWidth.Value ?? 0),
        ShorteningStrategy = _significant.IsChecked == true ? RepositoryPathShorteningStrategy.MostSignDir
            : _middle.IsChecked == true ? RepositoryPathShorteningStrategy.MiddleDots : RepositoryPathShorteningStrategy.None
    };

    private void RefreshPresentation()
    {
        string[] topSelection = SelectedPaths(_top), recentSelection = SelectedPaths(_recent);
        _presentation = RepositoryHistoryPresentation.Select(ReadHistory(), MeasureCaption);
        int width = ReadHistory().RecentReposComboMinWidth;
        _topColumnWidth = ColumnWidth(_presentation.Top, width, RepositoryHistoryAnchor.Top);
        _recentColumnWidth = ColumnWidth(_presentation.Recent, width, RepositoryHistoryAnchor.Recent);
        _top.ItemsSource = _presentation.Top; _recent.ItemsSource = _presentation.Recent;
        Restore(_top, topSelection); Restore(_recent, recentSelection);
        static void Restore(ListBox list, string[] paths)
        {
            foreach (RepositoryHistoryRow row in list.Items.OfType<RepositoryHistoryRow>().Where(row => paths.Contains(row.Path, StringComparer.Ordinal)))
            { if (list.SelectedItems?.Contains(row) != true) { list.SelectedItems?.Add(row); } }
        }
    }

    private double ColumnWidth(IReadOnlyList<RepositoryHistoryRow> entries, int width, RepositoryHistoryAnchor boldAnchor)
        => width > 0 ? Math.Max(30, width) : Math.Max(30, entries.Select(entry => new FormattedText(
            ExportDialogControls.DisplayPath(entry.Caption), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, entry.Entry.EffectiveAnchor == boldAnchor ? FontWeight.Bold : FontWeight, FontStretch),
            FontSize, Brushes.Black).WidthIncludingTrailingWhitespace + 10).DefaultIfEmpty(30).Max());

    private double MeasureCaption(string text)
        => new FormattedText(ExportDialogControls.DisplayPath(text), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;

    private void MinimumWidthChanged()
    {
        if (_adjustingWidth) { return; }
        decimal value = _minimumWidth.Value ?? 0;
        if (value < _previousWidth && value < 30) { value = 0; }
        else if (value > _previousWidth && value < 30) { value = 30; }
        _previousWidth = value;
        _adjustingWidth = true;
        try { _minimumWidth.Value = value; }
        finally { _adjustingWidth = false; }
        RefreshPresentation();
    }

    private void PrepareList(ListBox list, bool top)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        list.ItemTemplate = new FuncDataTemplate<RepositoryHistoryRow>((row, _) =>
        {
            if (row is null) { return new Border { Height = 20 }; }
            TextBlock label = new()
            {
                Text = ExportDialogControls.DisplayPath(row.Caption), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = row.Entry.EffectiveAnchor == (top ? RepositoryHistoryAnchor.Top : RepositoryHistoryAnchor.Recent) ? FontWeight.Bold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 0)
            };
            if (!Directory.Exists(row.Path)) { label.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Colors.Red)); }
            Border column = ClassicChrome.Border(label, new Thickness(0, 0, 1, 1));
            column.Width = top ? _topColumnWidth : _recentColumnWidth;
            column.HorizontalAlignment = HorizontalAlignment.Left;
            column.Height = 20; ToolTip.SetTip(column, row.Path); return column;
        });
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(HeightProperty, 20d), new Setter(MinHeightProperty, 20d), new Setter(PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0)), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Left) }
        });
        MenuItem anchorTop = Menu("anchorToTopReposToolStripMenuItem", "Anchor to top repositories", () => ChangeAnchor(list, RepositoryHistoryAnchor.Top));
        MenuItem anchorRecent = Menu("anchorToRecentReposToolStripMenuItem", "Anchor to recent repositories", () => ChangeAnchor(list, RepositoryHistoryAnchor.Recent));
        MenuItem unpin = Menu("removeAnchorToolStripMenuItem", "Remove anchor", () => ChangeAnchor(list, RepositoryHistoryAnchor.None));
        MenuItem remove = Menu("removeRecentToolStripMenuItem", "Remove from recent repositories", () =>
        {
            foreach (string path in SelectedPaths(list)) { _draft = _draft.Remove(path, favorite: false); }
            RefreshPresentation();
        });
        ContextMenu context = new() { ItemsSource = new[] { anchorTop, anchorRecent, unpin, remove } };
        context.Opened += (_, _) =>
        {
            RepositoryHistoryRow[] selected = list.SelectedItems?.OfType<RepositoryHistoryRow>().ToArray() ?? [];
            anchorTop.IsEnabled = selected.Any(row => row.Entry.EffectiveAnchor != RepositoryHistoryAnchor.Top);
            anchorRecent.IsEnabled = selected.Any(row => row.Entry.EffectiveAnchor != RepositoryHistoryAnchor.Recent);
            unpin.IsEnabled = selected.Any(row => row.Entry.EffectiveAnchor != RepositoryHistoryAnchor.None);
            remove.IsEnabled = selected.Length > 0;
        };
        list.ContextMenu = context;
    }

    private void ChangeAnchor(ListBox list, RepositoryHistoryAnchor anchor)
    {
        foreach (string path in SelectedPaths(list)) { _draft = _draft.Anchor(path, anchor); }
        RefreshPresentation();
    }

    private static string[] SelectedPaths(ListBox list)
        => list.SelectedItems?.OfType<RepositoryHistoryRow>().Select(row => row.Path).ToArray() ?? [];

    private MenuItem Menu(string name, string source, Action action)
    {
        MenuItem item = new() { Name = name, Header = L(name, source) };
        item.Click += (_, _) => action(); return item;
    }

    private string L(string item, string source) => _translations.Translate("FormRecentReposSettings", item, "Text", source);
    private static NumericUpDown Number(string name, int minimum, int maximum)
        => new()
        {
            Name = name, Minimum = minimum, Maximum = maximum, Increment = 1, Width = 61, Height = 24, MinHeight = 24,
            FormatString = "0", ClipValueToMinMax = true,
            Template = new FuncControlTemplate<NumericUpDown>((owner, scope) =>
            {
                TextBox text = new() { Name = "PART_TextBox", Height = 22, MinHeight = 0, MinWidth = 0, Padding = new Thickness(2, 0), BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
                ButtonSpinner spinner = new()
                {
                    Name = "PART_Spinner", Content = text, AllowSpin = true, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0),
                    Template = new FuncControlTemplate<ButtonSpinner>((host, spinnerScope) =>
                    {
                        RepeatButton increase = Arrow("PART_IncreaseButton", "▴"), decrease = Arrow("PART_DecreaseButton", "▾");
                        spinnerScope.Register(increase.Name!, increase); spinnerScope.Register(decrease.Name!, decrease);
                        StackPanel arrows = new() { Width = 14, Children = { increase, decrease } };
                        Grid panel = new() { ColumnDefinitions = new ColumnDefinitions("*,14") };
                        panel.Children.Add(new ContentPresenter { Content = host.Content, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                        Grid.SetColumn(arrows, 1); panel.Children.Add(arrows); return panel;
                    })
                };
                scope.Register(text.Name!, text); scope.Register(spinner.Name!, spinner);
                Border border = ClassicChrome.Border(spinner, new Thickness(1));
                border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ClassicWindowBrush"));
                return border;
                static RepeatButton Arrow(string part, string glyph) => new()
                {
                    Name = part, Content = glyph, Height = 11, MinHeight = 0, MinWidth = 0, FontSize = 8,
                    Padding = new Thickness(0), Margin = new Thickness(0), Focusable = false,
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
                };
            })
        };
    private static void Add(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    private static void Field(Grid grid, string caption, Control field)
    {
        int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TextBlock label = new() { Text = caption, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        label.Margin = new Thickness(0, 2, 5, 2); field.Margin = new Thickness(0, 2);
        Add(grid, label, row); Grid.SetColumn(field, 1); Add(grid, field, row);
    }
    private static void Option(Grid grid, CheckBox box, string caption)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,20"), MinHeight = 23 };
        row.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        box.Height = 20; box.MinHeight = 0; box.Padding = new Thickness(0); box.Margin = new Thickness(0);
        box.Resources["CheckBoxMinHeight"] = 20d;
        box.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(box, 1); row.Children.Add(box); FullRow(grid, row);
    }
    private static void FullRow(Grid grid, Control control, double margin = 0)
    {
        int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumnSpan(control, 2); if (margin > 0) { control.Margin = new Thickness(0, margin); } Add(grid, control, row);
    }
}
