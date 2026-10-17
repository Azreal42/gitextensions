using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private const double BrowseRowHeight = 20;
    private const double NavigationWidth = 195;
    private CompactBrowserToolbar? _browserToolbar;
    private Button? _currentBranchButton, _commitToolbarButton, _scopeButton, _cancelToolbarButton;
    private MenuItem? _scopeOverflow;
    private MenuItem? _recentToolbarMenu;
    private readonly TextBox _quickHistoryFilter = new() { Name = "RevisionQuickFilter", Width = 100, Height = 24, MinHeight = 24 };
    private string _quickHistoryFilterKind = "Message";
    private readonly List<MenuItem> _quickFilterChoices = [];

    private Control BuildRepositoryToolbar()
    {
        CompactBrowserToolbar toolbar = new(); _browserToolbar = toolbar;
        void Command(string name, string label, string icon, Func<Task> action, bool text = false)
        {
            Button button = ToolbarButton(name, label, icon, action, text);
            toolbar.Add(button, MenuAction(label, action, icon));
        }
        Command("BrowseRefresh", "Refresh", "ReloadRevisions.png", RefreshAsync);
        _cancelToolbarButton = ToolbarButton("BrowseCancelOperation", "Cancel operation", "BisectStop.png",
            () => { _operation?.Cancel(); return Task.CompletedTask; });
        _cancelToolbarButton.IsVisible = false;
        MenuItem cancelOverflow = new() { Header = T("Cancel operation") };
        cancelOverflow.Click += (_, _) => _operation?.Cancel();
        toolbar.Add(_cancelToolbarButton, cancelOverflow);
        Command("BrowseToggleSidebar", "Toggle left panel", "LayoutSidebarLeft.png", ToggleNavigationAsync);
        Control position = BuildCommitInfoPositionButton();
        MenuItem positions = new() { Header = T("Commit info position"), ItemsSource = _commitInfoPositionItems.Select(original =>
        {
            MenuItem copy = new() { Header = original.Header, Icon = CommitInfoLayoutImage((GitCommands.CommitInfoPosition)original.Tag!),
                ToggleType = MenuItemToggleType.Radio, GroupName = "CommitInfoOverflow" };
            copy.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(MenuItem.IsChecked)) { Source = original });
            copy.Click += (_, _) => ApplyCommitInfoLayout((GitCommands.CommitInfoPosition)original.Tag!, persist: true);
            return copy;
        }).ToArray() };
        toolbar.Add(position, positions);
        Command("BrowseOpen", "Open repository", "FolderClosed.png", ChooseRepositoryAsync);
        _repositoryPath.Name = "RepositoryPath"; _repositoryPath.MinWidth = 120; _repositoryPath.Width = 220;
        _repositoryPath.Height = 24; _repositoryPath.Padding = new Thickness(3, 2);
        toolbar.Add(_repositoryPath, ToolbarInput("RepositoryPathOverflow", "Repository", _repositoryPath,
            () => OpenRepositoryAsync(_repositoryPath.Text ?? "")));
        _recent.Width = 24; _recent.Height = 24; _recent.MinWidth = 0; _recent.Padding = default;
        _recent.Name = "RecentRepositoriesDropdown"; _recent.PlaceholderText = ""; _recent.SelectionBoxItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((_, _) => new TextBlock());
        _recent.Classes.Add("browse-toolbar-dropdown");
        _recent.Styles.Add(new Style(selector => selector.OfType<ComboBox>().Class("browse-toolbar-dropdown").Template().OfType<PathIcon>().Name("DropDownGlyph"))
        {
            Setters =
            {
                new Setter(PathIcon.DataProperty, ToolbarDropdownGeometry),
                new Setter(WidthProperty, 6d), new Setter(HeightProperty, 3d),
                new Setter(MarginProperty, new Thickness(0)),
                // Fluent's ComboBox content grid places its glyph one pixel
                // below the centered 24-pixel toolbar buttons.
                new Setter(RenderTransformProperty, new TranslateTransform(0, -1)),
                new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center),
                new Setter(VerticalAlignmentProperty, VerticalAlignment.Center)
            }
        });
        ToolTip.SetTip(_recent, T("Recent repositories"));
        _recentToolbarMenu = new MenuItem { Header = T("Recent repositories") };
        _recent.TemplateApplied += (_, args) =>
        {
            if (args.NameScope.Find<Avalonia.Controls.Primitives.Popup>("PART_Popup") is { } popup) { popup.MinWidth = RecentRepositoriesPopupWidth; }
            if (args.NameScope.Find<PathIcon>("DropDownGlyph") is { } arrow)
            { arrow.Bind(PathIcon.ForegroundProperty, new Binding(nameof(TemplatedControl.Foreground)) { Source = _recent }); }
        };
        toolbar.Add(_recent, _recentToolbarMenu);
        toolbar.Add(BuildWorktreeButton(), BuildWorktreeOverflow());
        _currentBranchButton = BuildCurrentBranchButton();
        toolbar.Add(_currentBranchButton, _branchOverflow!);
        toolbar.Add(BuildPullButton(), new MenuItem { Name = "PullOverflow", Header = T("Pull/Fetch…"), Icon = OriginalIconImage("Pull.png"), ItemsSource = CreatePullMenu("PullOverflow") });
        bool quickPush = false;
        async Task PushFromToolbarAsync()
        {
            bool quick = quickPush; quickPush = false;
            await ShowPushAsync(quick);
        }
        Button pushButton = ToolbarButton("BrowsePush", "Push", "Push.png", PushFromToolbarAsync);
        MenuItem pushOverflow = MenuAction("Push", PushFromToolbarAsync, "Push.png");
        foreach (Control control in new Control[] { pushButton, pushOverflow })
        {
            control.AddHandler(PointerPressedEvent, (_, args) => quickPush = args.KeyModifiers.HasFlag(KeyModifiers.Shift),
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(KeyDownEvent, (_, args) =>
            {
                if (args.Key is Key.Enter or Key.Space) { quickPush = args.KeyModifiers.HasFlag(KeyModifiers.Shift); }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        toolbar.Add(pushButton, pushOverflow);
        _commitToolbarButton = ToolbarButton("BrowseCommit", "Commit", "RepoStateClean.png", () => ShowWorkflowAsync("Changes"), text: true);
        toolbar.Add(_commitToolbarButton, MenuAction("Commit…", () => ShowWorkflowAsync("Changes"), "RepoStateClean.png"));
        toolbar.Add(BuildStashButton(), new MenuItem { Name = "StashOverflow", Header = T("Stash"), Icon = OriginalIconImage("stash.png"), ItemsSource = CreateStashMenu("StashOverflow") });
        Command("BrowseFileExplorer", "Open repository in file manager", "BrowseFileExplorer.png", OpenRepositoryFolderAsync);
        Command("BrowseConsole", "Console", "Console.png", OpenTerminalAsync);
        Command("BrowseSettings", "Settings", "Settings.png", () => ShowWorkflowAsync("Configuration"));
        Command("BrowseAdvancedFilter", "Filter revisions", "FunnelPencil.png", FilterHistoryAsync);
        _scopeOverflow = new MenuItem { Header = T("All branches"), ItemsSource = HistoryScopeChoices("Overflow") };
        // The scope button opens choices; it must not also change the query on its first click.
        _scopeButton = new Button { Name = "BrowseHistoryScope", MinWidth = 110, Height = 24, MinHeight = 24,
            Padding = new Thickness(2, 0), Content = IconLabel("All branches", "BranchLocal.png"), Flyout = new MenuFlyout { ItemsSource = HistoryScopeChoices("") } };
        ConfigureBrowserFlyout((MenuFlyout)_scopeButton.Flyout!, _scopeButton);
        _actionButtons.Add(_scopeButton);
        toolbar.Add(_scopeButton, _scopeOverflow);
        ConfigureBranchFilter(_historyRevision, "BranchHistoryFilter");
        Grid branchFilter = ToolbarField(T("Branches:"), _historyRevision); branchFilter.Name = "HistoryToolbarBranches";
        toolbar.Add(branchFilter, BranchFilterOverflow());
        Control suggestions = BranchSuggestionButton();
        toolbar.Add(suggestions, BranchSuggestionOverflow());
        _quickHistoryFilter.KeyDown += async (_, args) =>
        { if (args.Key == Key.Enter && !_busy) { args.Handled = true; await ApplyQuickHistoryFilterAsync(); } };
        Button type = new() { Name = "RevisionQuickFilterType", Content = OriginalIconImage("FunnelPencil.png"),
            Width = 24, Height = 24, MinWidth = 0, MinHeight = 24, Padding = default };
        foreach (string kind in new[] { "Message", "Author", "Committer", "DiffContent" })
        {
            string label = kind switch { "Message" => "Commit message", "DiffContent" => "Diff contains (SLOW)", _ => kind };
            MenuItem choice = new() { Name = "RevisionQuickFilterType_" + kind, Header = T(label), Tag = kind,
                ToggleType = MenuItemToggleType.Radio, GroupName = "QuickHistoryFilter", IsChecked = kind == _quickHistoryFilterKind };
            choice.Click += (_, _) =>
            {
                _quickHistoryFilterKind = kind;
                foreach (MenuItem option in _quickFilterChoices) { option.IsChecked = option.Tag as string == kind; }
                _quickHistoryFilter.Text = QuickFilterValue();
                ToolTip.SetTip(_quickHistoryFilter, T(label));
            };
            _quickFilterChoices.Add(choice);
        }
        ToolTip.SetTip(type, T("Filter type"));
        ToolTip.SetTip(_quickHistoryFilter, T("Commit message"));
        type.Flyout = new MenuFlyout { ItemsSource = _quickFilterChoices };
        ConfigureBrowserFlyout((MenuFlyout)type.Flyout, type);
        _actionButtons.Add(type);
        MenuItem overflowTypes = new() { Header = T("Filter type"), ItemsSource = _quickFilterChoices.Select(original =>
        {
            MenuItem copy = new() { Header = original.Header, ToggleType = MenuItemToggleType.Radio, GroupName = "QuickHistoryFilterOverflow" };
            copy.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(MenuItem.IsChecked)) { Source = original });
            copy.Click += (_, _) => original.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            return copy;
        }).ToArray() };

        Grid quick = ToolbarField(T("Filter:"), _quickHistoryFilter, type); quick.Name = "HistoryToolbarQuickFilter";
        MenuItem quickOverflow = new() { Header = T("Filter:"), ItemsSource = new Control[]
        { ToolbarInput("RevisionQuickFilterOverflow", "Filter:", _quickHistoryFilter, ApplyQuickHistoryFilterAsync), overflowTypes } };
        toolbar.Add(quick, quickOverflow);
        _historyMore = ToolbarButton("BrowseLoadMore", "Load more", "ArrowDown.png", LoadMoreHistoryAsync);
        _historyMore.IsVisible = false; toolbar.Add(_historyMore, MenuAction("Load more", LoadMoreHistoryAsync));
        SizeChanged += (_, _) =>
        { _repositoryPath.Width = Math.Clamp(Bounds.Width * .17, 120, 260); toolbar.Place(); };
        return toolbar;
    }

    private static void ConfigureBrowserFlyout(MenuFlyout flyout, Control owner)
    {
        void Palette()
        {
            MenuFlyoutPresenter? presenter = flyout.Items.OfType<Control>()
                .SelectMany(item => item.GetVisualAncestors().OfType<MenuFlyoutPresenter>()).FirstOrDefault();
            if (presenter is null) { return; }
            IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(owner.ActualThemeVariant == ThemeVariant.Dark);
            presenter.Background = Brush.Parse(colors["Menu"]); presenter.Foreground = Brush.Parse(colors["MenuText"]);
            presenter.BorderBrush = Brush.Parse(colors["ActiveBorder"]);
            foreach (string state in new[] { "PointerOver", "Pressed" })
            {
                presenter.Resources["MenuFlyoutItemBackground" + state] = Brush.Parse(colors["MenuHighlight"]);
                presenter.Resources["MenuFlyoutItemForeground" + state] = Brush.Parse(colors["HighlightText"]);
            }
            presenter.Resources["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = Brush.Parse(colors["MenuText"]);
            presenter.Resources["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = Brush.Parse(colors["HighlightText"]);
        }
        flyout.Opened += (_, _) => Palette();
        owner.ActualThemeVariantChanged += (_, _) => Palette();
        owner.DetachedFromVisualTree += (_, _) => flyout.Hide();
    }

    private Button ToolbarButton(string name, string label, string icon, Func<Task> action, bool text = false)
    {
        Button button = IconButton(label, icon, action, text);
        button.Name = name; button.Height = 24; button.MinHeight = 24; button.MinWidth = 0;
        button.Padding = new Thickness(text ? 3 : 0, 0); button.BorderThickness = default;
        button.Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<Button>())
        { Setters = { new Avalonia.Styling.Setter(BackgroundProperty, Brushes.Transparent) } });
        if (!text) { button.Width = 24; }
        return button;
    }
    private static readonly Geometry ToolbarDropdownGeometry = ClassicToolbarAppearance.DropdownGeometry;
    private static PathIcon ToolbarDropdownArrow(TemplatedControl owner, string name)
        => ClassicToolbarAppearance.DropdownArrow(owner, name);
    private static Grid ToolbarField(string label, Control input, Control? trailing = null)
    {
        Grid field = new() { ColumnDefinitions = new ColumnDefinitions(trailing is null ? "Auto,Auto" : "Auto,Auto,Auto"), Height = 24 };
        field.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
        Grid.SetColumn(input, 1); field.Children.Add(input);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); field.Children.Add(trailing); }
        return field;
    }
    private MenuItem ToolbarInput(string name, string label, TextBox original, Func<Task> accept)
    {
        TextBox input = new() { Name = name, Width = 190, Height = 24, MinHeight = 24 };
        if (name == "RevisionQuickFilterOverflow")
        {
            _quickFilterOverflowInput = input;
            input.AddHandler(KeyDownEvent, RelayBranchFilterFocusShortcut, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
        input.Bind(TextBox.TextProperty, new Binding(nameof(TextBox.Text)) { Source = original, Mode = BindingMode.TwoWay });
        input.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter || _busy) { return; }
            args.Handled = true; _browserToolbar?.OverflowFlyout.Hide(); await accept();
        };
        MenuItem item = new() { Header = ToolbarField(T(label), input), StaysOpenOnClick = true };
        _actionMenus.Add(item); return item;
    }
    private string? QuickFilterValue() => _quickHistoryFilterKind switch
    { "Author" => _historyQuery.Author, "Committer" => _historyQuery.Committer, "DiffContent" => _historyQuery.DiffContent, _ => _historyQuery.Message };
    private Task ApplyQuickHistoryFilterAsync()
    {
        if (_repository is null || _busy) { return Task.CompletedTask; }
        string? value = string.IsNullOrEmpty(_quickHistoryFilter.Text) ? null : _quickHistoryFilter.Text;
        _historyQuery = _quickHistoryFilterKind switch
        {
            "Author" => _historyQuery with { Author = value }, "Committer" => _historyQuery with { Committer = value },
            "DiffContent" => _historyQuery with { DiffContent = value }, _ => _historyQuery with { Message = value }
        };
        return ReloadHistoryAsync();
    }
    private Task OpenRepositoryFolderAsync()
    {
        if (_repository is not null)
        { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { _repository.Path } }); }
        return Task.CompletedTask;
    }
    private void UpdateBrowserToolbar(GitStatus status)
    {
        if (_currentBranchButton is not null)
        {
            string branch = status.Branch ?? T("detached");
            _currentBranchButton.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children =
            { OriginalIconImage("BranchLocal.png"), new TextBlock { Text = branch, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) }, ToolbarDropdownArrow(_currentBranchButton, "BrowseCurrentBranchDropdownArrow") } };
            Grid.SetColumn(((Grid)_currentBranchButton.Content).Children[1], 1); Grid.SetColumn(((Grid)_currentBranchButton.Content).Children[2], 2);
            ToolTip.SetTip(_currentBranchButton, branch + " — " + T("Checkout branch"));
            if (_branchOverflow is not null) { _branchOverflow.Header = branch; }
        }
        if (_commitToolbarButton is not null)
        {
            int count = status.Changes.Count;
            string icon = count == 0 ? "RepoStateClean.png" : status.Changes.Any(change => change.IsStaged) ? "RepoStateStaged.png" : "RepoStateDirty.png";
            _commitToolbarButton.Content = IconLabel(T("Commit") + " (" + count + ")", icon);
        }
        UpdateHistoryScopePresentation();
        _quickHistoryFilter.Text = QuickFilterValue();
        RefreshWorktreeMenu();
        _browserToolbar?.Place();
        if (_repository is not null) { Title = Path.GetFileName(_repository.Path.TrimEnd(Path.DirectorySeparatorChar)) + " (" + status.Branch + ") — Git Extensions"; }
    }
}
