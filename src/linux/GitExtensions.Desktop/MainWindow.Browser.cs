using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;
using GitCommands;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly TreeView _navigation = new() { Name = "RepositoryNavigation" };
    private readonly TextBox _commitInfo = ReadOnlyText();
    private readonly TextBox _gpgInfo = ReadOnlyText();
    private readonly Dictionary<string, Control> _workflowViews = new(StringComparer.Ordinal);
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<MenuItem> _actionMenus = [];
    private Grid? _browserBody;
    private Button? _historyMore;
    private readonly BrowserLayoutPreferencesStore _browserLayoutPreferences = new();
    private CommitInfoPosition _commitInfoPosition;
    private Grid? _revisionPanels;
    private Control? _revisionTable;
    private GridSplitter? _commitInfoSplitter;
    private TabItem? _commitInfoTab;
    private IReadOnlyList<TabItem> _browserDetailTabs = [];
    private Button? _commitInfoPositionButton;
    private MenuItem[] _commitInfoPositionItems = [];
    private double _commitInfoWidth = 490;
    private bool _layingOutCommitInfo;

    private string T(string source) => _translations.Translate(source);

    private Control BuildLayout()
    {
        _commitInfoPosition = _browserLayoutPreferences.Load().CommitInfoPosition;
        // Reuse the same workflow controls in dialogs; keep the Browse layout from FormBrowse.
        IReadOnlyList<TabItem> views = BuildTabs();
        Grid previousHistory = (Grid)views[0].Content!;
        DockPanel previousHistoryPanel = (DockPanel)previousHistory.Children[0];
        Grid previousHistoryBody = (Grid)previousHistoryPanel.Children[^1];
        previousHistoryBody.Children.Remove(_commits);
        previousHistoryBody.Children.Remove(_historyGraph);
        previousHistory.Children.Remove(_diff);
        if (_historyRevision.Parent is Panel previousFilter) { previousFilter.Children.Remove(_historyRevision); }
        string[] names = ["Changes", "Branches & tags", "Remotes", "Stashes", "Worktrees", "Submodules", "Files", "Configuration", "Repository"];
        for (int i = 1; i < views.Count; i++)
        {
            _workflowViews[names[i - 1]] = (Control)views[i].Content!;
            views[i].Content = null;
        }

        _commits.Name = "RevisionHistory";
        _commitInfo.TextWrapping = TextWrapping.Wrap;
        _commitInfo.FontFamily = FontFamily.Default;
        _commitInfo.Name = "CommitInformation";
        _commits.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) =>
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,120,130,75"), Height = CommitGraph.RowHeight - 2 };
            string[] values = [commit?.Subject ?? "", commit?.AuthorName ?? "", commit?.AuthoredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "", commit?.Hash[..Math.Min(9, commit.Hash.Length)] ?? ""];
            for (int i = 0; i < values.Length; i++)
            {
                TextBlock cell = new() { Text = values[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0) };
                Grid.SetColumn(cell, i);
                row.Children.Add(cell);
            }
            return row;
        });
        _commits.ContextMenu = new ContextMenu
        {
            ItemsSource = new[] { MenuAction("Checkout revision", CheckoutCommitAsync), MenuAction("Cherry-pick", CherryPickAsync), MenuAction("Revert commit", RevertAsync), MenuAction("Reset branch…", ResetAsync), BuildComparisonMenu() }
        };
        _navigation.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _busy || _navigation.SelectedItem is not TreeViewItem item) { return; }
            if (item.Tag is GitReference reference)
            {
                _references.SelectedItem = reference;
                _navigationRevisionFilter = reference.Name;
                _historyRevision.Text = reference.Name;
                await ReloadHistoryAsync();
            }
            else if (item.Tag is string workflow) { await ShowWorkflowAsync(workflow); }
        };

        DockPanel root = new() { LastChildFill = true };
        StackPanel top = new() { Spacing = 0 };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(BuildMenu());
        _repositoryToolbars = new StackPanel { Spacing = 0 };
        top.Children.Add(_repositoryToolbars);
        _repositoryPath.MinWidth = 240;
        _repositoryPath.Width = 300;
        _repositoryPath.Height = 24;
        _recent.Height = 24;
        Border toolbar = new()
        {
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(4, 2),
            Child = Row(IconButton("Refresh", "ReloadRevisions.png", RefreshAsync, false),
                IconButton("Toggle left panel", "LayoutSidebarLeft.png", ToggleNavigationAsync, false),
                BuildCommitInfoPositionButton(),
                IconButton("Open repository", "FolderClosed.png", ChooseRepositoryAsync, false), _repositoryPath,
                IconButton("Pull", "Pull.png", PullAsync), IconButton("Push", "Push.png", PushAsync),
                IconButton("Commit", "RepoStateClean.png", () => ShowWorkflowAsync("Changes")),
                IconButton("Stash", "stash.png", StashAsync), IconButton("Console", "Console.png", OpenTerminalAsync, false),
                IconButton("Settings", "Settings.png", () => ShowWorkflowAsync("Configuration"), false), _recent)
        };
        toolbar.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ClassicBorderBrush"));
        _repositoryToolbars.Children.Add(toolbar);
        _historyRevision.Height = 24;
        _historyRevision.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && _historyRevision.Text != _navigationRevisionFilter) { _navigationRevisionFilter = null; }
        };
        _historyMore = Button("Load more", LoadMoreHistoryAsync);
        _historyMore.IsVisible = false;
        _repositoryToolbars.Children.Add(Row(new TextBlock { Text = T("Branches:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) },
            _historyRevision, Button("Filter", FilterHistoryAsync), Button("All branches", () => SetHistoryScopeAsync(true)), _historyMore,
            Button("Fetch", () => RunAsync("Fetch", r => r.FetchAsync(cancellationToken: Token))), Button("Cancel operation", () => { _operation?.Cancel(); return Task.CompletedTask; })));
        root.Children.Add(top);

        Border status = ClassicChrome.Border(_state, new Thickness(0, 1, 0, 0), new Thickness(6, 3));
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        _browserBody = new Grid { ColumnDefinitions = new ColumnDefinitions("220,4,*") };
        _browserBody.Children.Add(ClassicChrome.Border(_navigation, new Thickness(0, 0, 1, 0)));
        GridSplitter navigationSplitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(navigationSplitter, 1);
        _browserBody.Children.Add(navigationSplitter);

        Grid right = new() { RowDefinitions = new RowDefinitions("2*,4,3*") };
        _revisionPanels = new Grid { Name = "RevisionPanels" };
        _revisionPanels.SizeChanged += (_, _) => LimitCommitInfoWidth();
        _revisionTable = BuildRevisionTable();
        _commitInfoSplitter = new GridSplitter { Name = "CommitInfoSplitter", Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        _revisionPanels.Children.Add(_revisionTable);
        _revisionPanels.Children.Add(_commitInfoSplitter);
        right.Children.Add(_revisionPanels);
        GridSplitter detailsSplitter = new() { Name = "DetailsSplitter", Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(detailsSplitter, 1);
        right.Children.Add(detailsSplitter);

        _tabs.Name = "DetailsTabs";
        _commitInfoTab = new TabItem { Name = "CommitInfoTab", Header = T("Commit") };
        _browserDetailTabs = new[]
        {
            new TabItem { Name = "DiffTab", Header = IconLabel("Diff", "Diff.png"), Content = BuildCommitFilesView() },
            new TabItem { Name = "FileTreeTab", Header = IconLabel("File tree", "FileTree.png"), Content = _workflowViews["Files"] },
            new TabItem { Name = "GpgTab", Header = T("GPG"), Content = _gpgInfo },
            new TabItem { Name = "ConsoleTab", Header = IconLabel("Console", "Console.png"), Content = Panel(_output, Row(Button("Open terminal", OpenTerminalAsync))) },
            new TabItem { Name = "ChangesTab", Header = IconLabel("Working directory", "WorkingDirChanges.png"), Content = _workflowViews["Changes"] }
        };
        _tabs.SelectionChanged += async (_, args) =>
        {
            if (!_layingOutCommitInfo && args.Source == _tabs && (_tabs.SelectedItem as TabItem)?.Name == "GpgTab")
            {
                await RunAsync("Commit signature", async repository =>
                {
                    GitResult result = await repository.RunAsync(["show", "--no-patch", "--format=Signature status: %G?%nSigner: %GS%nKey: %GK%nFingerprint: %GF", CurrentRevision], Token);
                    _gpgInfo.Text = result.StandardOutput + result.StandardError;
                }, refresh: false);
            }
        };
        Grid.SetRow(_tabs, 2);
        right.Children.Add(_tabs);
        ApplyCommitInfoLayout(_commitInfoPosition, persist: false);
        Grid.SetColumn(right, 2);
        _browserBody.Children.Add(right);
        _dashboard = new DashboardControl(this, () => _repositoryHistory, SaveRepositoryHistory, OpenRepositoryAsync, InitializeAsync, ChooseRepositoryAsync, CloneAsync);
        Grid workspace = new(); workspace.Children.Add(_browserBody); workspace.Children.Add(_dashboard);
        root.Children.Add(workspace);
        UpdateRepositoryMenus();
        SetDashboardVisible(_repository is null);
        return root;
    }

    private Control BuildCommitInfoPositionButton()
    {
        _commitInfoPositionItems = Enum.GetValues<CommitInfoPosition>().Select(position =>
        {
            MenuItem item = new()
            {
                Name = "CommitInfoPosition_" + position, Header = T(CommitInfoLayoutDescription(position).Label),
                Icon = CommitInfoLayoutImage(position), Tag = position, ToggleType = MenuItemToggleType.Radio,
                GroupName = "CommitInfoPosition"
            };
            item.Click += (_, _) => ApplyCommitInfoLayout(position, persist: true);
            return item;
        }).ToArray();
        _commitInfoPositionButton = new Button
        {
            Name = "CommitInfoPosition", Width = 22, Height = 24, MinWidth = 0, MinHeight = 24, Padding = new Thickness(0)
        };
        _commitInfoPositionButton.Click += (_, _) => ApplyCommitInfoLayout(
            (CommitInfoPosition)(((int)_commitInfoPosition + 1) % Enum.GetValues<CommitInfoPosition>().Length), persist: true);
        Button dropdown = new()
        {
            Name = "CommitInfoPositionMenu", Width = 10, Height = 24, MinWidth = 0, MinHeight = 24, Padding = new Thickness(0),
            Content = new TextBlock { Text = "▾", FontSize = 10 }, Flyout = new MenuFlyout { ItemsSource = _commitInfoPositionItems }
        };
        ToolTip.SetTip(dropdown, T("Commit info position"));
        Grid splitButton = new() { Name = "CommitInfoPositionControl", ColumnDefinitions = new ColumnDefinitions("22,10"), Height = 24 };
        splitButton.Children.Add(_commitInfoPositionButton);
        Grid.SetColumn(dropdown, 1);
        splitButton.Children.Add(dropdown);
        UpdateCommitInfoPositionButton();
        return splitButton;
    }

    private static (string Label, string Icon) CommitInfoLayoutDescription(CommitInfoPosition position) => position switch
    {
        CommitInfoPosition.BelowList => ("Commit info below graph", "LayoutFooterTab.png"),
        CommitInfoPosition.LeftwardFromList => ("Commit info left of graph", "LayoutSidebarTopLeft.png"),
        CommitInfoPosition.RightwardFromList => ("Commit info right of graph", "LayoutSidebarTopRight.png"),
        _ => throw new ArgumentOutOfRangeException(nameof(position))
    };

    private static Image CommitInfoLayoutImage(CommitInfoPosition position)
    {
        string icon = CommitInfoLayoutDescription(position).Icon;
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon));
        return new Image { Source = new Bitmap(stream), Width = 16, Height = 16, Tag = icon };
    }

    private void UpdateCommitInfoPositionButton()
    {
        if (_commitInfoPositionButton is not null)
        {
            _commitInfoPositionButton.Content = CommitInfoLayoutImage(_commitInfoPosition);
            ToolTip.SetTip(_commitInfoPositionButton, T(CommitInfoLayoutDescription(_commitInfoPosition).Label));
        }
        foreach (MenuItem item in _commitInfoPositionItems) { item.IsChecked = Equals(item.Tag, _commitInfoPosition); }
    }

    private void ApplyCommitInfoLayout(CommitInfoPosition position, bool persist)
    {
        if (_revisionPanels is null || _revisionTable is null || _commitInfoSplitter is null || _commitInfoTab is null) { return; }
        if (!Enum.IsDefined(position)) { throw new ArgumentOutOfRangeException(nameof(position)); }
        TabItem? selected = _tabs.SelectedItem as TabItem;
        if (_commitInfo.Parent == _revisionPanels && _commitInfo.Bounds.Width > 0) { _commitInfoWidth = _commitInfo.Bounds.Width; }
        _layingOutCommitInfo = true;
        try
        {
            // Reparent the existing information control, just as FormBrowse.LayoutRevisionInfo does.
            _commitInfoTab.Content = null;
            _revisionPanels.Children.Remove(_commitInfo);
            _commitInfoPosition = position;
            _commitInfoSplitter.IsVisible = position != CommitInfoPosition.BelowList;
            if (position == CommitInfoPosition.BelowList)
            {
                _revisionPanels.ColumnDefinitions = new ColumnDefinitions("*");
                Grid.SetColumn(_revisionTable, 0);
                _commitInfoTab.Content = _commitInfo;
                _tabs.ItemsSource = new[] { _commitInfoTab }.Concat(_browserDetailTabs).ToArray();
                _tabs.SelectedItem = _commitInfoTab;
            }
            else
            {
                bool left = position == CommitInfoPosition.LeftwardFromList;
                _revisionPanels.ColumnDefinitions = new ColumnDefinitions(left ? "Auto,4,*" : "*,4,Auto");
                int infoColumn = left ? 0 : 2;
                _revisionPanels.ColumnDefinitions[infoColumn].Width = new GridLength(_commitInfoWidth);
                Grid.SetColumn(_revisionTable, left ? 2 : 0);
                Grid.SetColumn(_commitInfoSplitter, 1);
                Grid.SetColumn(_commitInfo, infoColumn);
                _revisionPanels.Children.Add(_commitInfo);
                _tabs.ItemsSource = _browserDetailTabs.ToArray();
                _tabs.SelectedItem = selected is not null && selected != _commitInfoTab ? selected : _browserDetailTabs[0];
                LimitCommitInfoWidth();
            }
            UpdateCommitInfoPositionButton();
        }
        finally { _layingOutCommitInfo = false; }
        if (persist) { _browserLayoutPreferences.Save(new(position)); }
    }

    private void LimitCommitInfoWidth()
    {
        if (_revisionPanels is null || _commitInfoPosition == CommitInfoPosition.BelowList || _revisionPanels.Bounds.Width <= 0) { return; }
        // Keep the information panel fixed on resize, while leaving space for the graph and history at narrow widths.
        _revisionPanels.ColumnDefinitions[_commitInfoPosition == CommitInfoPosition.LeftwardFromList ? 0 : 2].MaxWidth = Math.Max(120, _revisionPanels.Bounds.Width - 300);
    }

    private void FocusCommitInformation()
    {
        if (_commitInfoPosition == CommitInfoPosition.BelowList) { SelectDetailsTab("CommitInfoTab"); }
        _tabs.UpdateLayout();
        _commitInfo.Focus();
    }

    private Control BuildRevisionTable()
    {
        DockPanel panel = new();
        Grid headers = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 24 };
        TextBlock graphHeader = new() { Text = T("Graph"), Width = 180, Margin = new Thickness(4, 2) };
        headers.Children.Add(graphHeader);
        Grid columns = new() { ColumnDefinitions = new ColumnDefinitions("*,120,130,75") };
        string[] labels = ["Message", "Author", "Date", "Commit"];
        for (int i = 0; i < labels.Length; i++)
        {
            TextBlock label = new() { Text = T(labels[i]), Margin = new Thickness(4, 2) };
            Grid.SetColumn(label, i);
            columns.Children.Add(label);
        }
        Grid.SetColumn(columns, 1);
        headers.Children.Add(columns);
        DockPanel.SetDock(headers, Dock.Top);
        Border header = ClassicChrome.Border(headers, new Thickness(0, 0, 0, 1));
        header.Name = "RevisionHeader";
        header.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        panel.Children.Add(header);
        DockPanel.SetDock(panel.Children[0], Dock.Top);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("180,*") };
        _historyGraph.Width = 180;
        body.Children.Add(_historyGraph);
        Grid.SetColumn(_commits, 1);
        body.Children.Add(_commits);
        panel.Children.Add(body);
        return panel;
    }

    private Menu BuildMenu() => new()
    {
        ItemsSource = new[]
        {
            MenuGroup("File", MenuAction("Open repository…", ChooseRepositoryAsync), MenuAction("Clone repository…", CloneAsync), MenuAction("Create new repository…", InitializeAsync), _favoritesMenu, _recentMenu, MenuAction("Close", CloseRepositoryAsync)),
            MenuGroup("Dashboard", MenuAction("Recent repositories", CloseRepositoryAsync), MenuAction("Refresh", RefreshDashboardAsync), MenuAction("Recent repositories settings", RecentRepositorySettingsAsync)),
            MenuGroup("Repository", MenuAction("Commit…", () => ShowWorkflowAsync("Changes")), MenuAction("Stash…", () => ShowWorkflowAsync("Stashes")), MenuAction("Worktrees…", () => ShowWorkflowAsync("Worktrees")), MenuAction("Submodules…", () => ShowWorkflowAsync("Submodules")), MenuAction("Maintenance…", () => ShowWorkflowAsync("Repository"))),
            MenuGroup("Commands", MenuAction("Create branch…", CreateBranchAsync, "BranchCreate.png"), MenuAction("Delete branch…", DeleteBranchesAsync, "BranchDelete.png"), MenuAction("Checkout branch…", CheckoutAsync, "BranchCheckout.png"), MenuAction("Merge…", MergeAsync), MenuAction("Rebase…", RebaseAsync), MenuAction("Interactive rebase…", InteractiveRebaseAsync), MenuAction("Create tag…", CreateTagAsync, "TagCreate.png"), MenuAction("Delete tag…", DeleteTagsAsync, "TagDelete.png"), MenuAction("Branches and tags…", () => ShowWorkflowAsync("Branches & tags")), MenuAction("Pull…", PullAsync), MenuAction("Push…", PushAsync), MenuAction("Manage remotes…", () => ShowWorkflowAsync("Remotes")), MenuAction("Apply patch…", ApplyPatchAsync), MenuAction("Create patches…", FormatPatchAsync)),
            MenuGroup("Git hosts", MenuAction("Browse remote repository", BrowseRemoteAsync)),
            MenuGroup("Plugins", NativePluginCatalog.BuiltIn.Concat(LifecycleSettingsPlugins()).Select(PluginMenuItem).ToArray()),
            MenuGroup("Tools", MenuAction("Settings…", () => ShowWorkflowAsync("Configuration")), MenuAction("Terminal", OpenTerminalAsync), MenuAction("Theme", ChangeThemeAsync), MenuAction("Show left panel", ToggleNavigationAsync)),
            MenuGroup("Help", MenuAction("Online manual", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { "https://git-extensions-documentation.readthedocs.io/" } }); return Task.CompletedTask; }))
        }
    };

    private MenuItem MenuGroup(string header, params MenuItem[] items) => new() { Header = T(header), ItemsSource = items };
    private MenuItem PluginMenuItem(INativePlugin plugin)
    {
        MenuItem item = MenuAction(plugin.Descriptor.Name, () => ShowPluginAsync(plugin));
        Uri uri = new($"avares://GitExtensions/Assets/PluginIcons/{plugin.Descriptor.IconResource}.png");
        if (AssetLoader.Exists(uri)) { using Stream stream = AssetLoader.Open(uri); item.Icon = new Image { Source = new Bitmap(stream), Width = 16, Height = 16 }; }
        return item;
    }
    private MenuItem MenuAction(string header, Func<Task> action, string? icon = null)
    {
        MenuItem item = new() { Header = T(header), InputGesture = BrowserGesture(header), Tag = RequiresWorktree(header) };
        if (icon is not null)
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon));
            item.Icon = new Image { Source = new Bitmap(stream), Width = 16, Height = 16 };
        }
        _actionMenus.Add(item);
        item.Click += async (_, _) => { try { await action(); } catch (Exception exception) { AppendOutput(exception.Message); } };
        return item;
    }

    private Control IconLabel(string label, string name)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Uri uri = new($"avares://GitExtensions/Assets/Icons/{name}");
        if (AssetLoader.Exists(uri)) { using Stream stream = AssetLoader.Open(uri); panel.Children.Add(new Image { Source = new Bitmap(stream), Width = 16, Height = 16 }); }
        panel.Children.Add(new TextBlock { Text = T(label), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private Button IconButton(string label, string icon, Func<Task> action, bool text = true)
    {
        Button button = Button(label, action);
        Control content = IconLabel(label, icon);
        if (!text && content is StackPanel panel) { panel.Children.RemoveAt(panel.Children.Count - 1); }
        button.Content = content;
        ToolTip.SetTip(button, T(label));
        return button;
    }

    private Task ToggleNavigationAsync()
    {
        if (_browserBody is not null)
        {
            bool visible = _browserBody.ColumnDefinitions[0].Width.Value > 0;
            _browserBody.ColumnDefinitions[0].Width = visible ? new GridLength(0) : new GridLength(220);
            _navigation.IsVisible = !visible;
        }
        return Task.CompletedTask;
    }

    private void SelectDetailsTab(string name) => _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().First(item => item.Name == name);

    private async Task ShowWorkflowAsync(string name)
    {
        if (name == "Changes")
        {
            if (_repository is null || _busy) { return; }
            if (_repository.IsBare) { _state.Text = T("Open a worktree to commit changes in a bare repository."); return; }
            CommitDialog commit = new(_repository);
            await RunAsync("Open commit dialog", _ => commit.InitializeAsync(), refresh: false);
            await commit.ShowDialog(this);
            if (commit.HasRepositoryChanges) { await RefreshAsync(); }
            return;
        }
        Control content = _workflowViews[name];
        if (content.Parent is not null) { return; }
        Window dialog = new() { Title = T(name) + " — Git Extensions", Width = 960, Height = 600, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Closed += (_, _) => dialog.Content = null;
        await dialog.ShowDialog(this);
    }

    private Task CheckoutCommitAsync() => RunAsync("Checkout revision", r => r.CheckoutAsync(SelectedCommit().Hash, detached: true, cancellationToken: Token));

    private void UpdateCommitInfo(GitCommit commit)
    {
        _commitInfo.Text = $"{T("Author")}: {commit.AuthorName}\n{commit.AuthorEmail}\n\n{T("Date")}: {commit.AuthoredAt.LocalDateTime:g}\n\n{T("Commit")}: {commit.Hash}\n\n{T("Parents")}: {string.Join("\n", commit.Parents)}\n\n{commit.Subject}\n\n{commit.Body}";
    }

    private void UpdateBrowserNavigation(IReadOnlyList<GitReference> references, IReadOnlyList<GitStash> stashes, IReadOnlyList<GitSubmodule> submodules, IReadOnlyList<GitWorktree> worktrees)
    {
        TreeViewItem? selected = _navigation.SelectedItem as TreeViewItem;
        string? selectedReference = (selected?.Tag as GitReference)?.Name;
        string? selectedWorkflow = selected?.Tag as string;
        string? selectedGroup = selected?.Tag is null ? selected?.Header as string : null;
        Dictionary<string, bool> expanded = _navigation.Items.OfType<TreeViewItem>().Where(item => item.Header is string).ToDictionary(item => (string)item.Header!, item => item.IsExpanded, StringComparer.Ordinal);
        TreeViewItem Group(string name, IEnumerable<TreeViewItem> children) => new() { Header = T(name), IsExpanded = expanded.TryGetValue(T(name), out bool wasExpanded) ? wasExpanded : name != "Tags", ItemsSource = children.ToArray() };
        TreeViewItem Ref(GitReference reference) => new() { Header = IconLabel(reference.ShortName, reference.IsTag ? "Tag.png" : reference.IsRemote ? "BranchRemote.png" : "BranchLocal.png"), Tag = reference };
        _navigation.SelectedItem = null;
        _navigation.ItemsSource = new[]
        {
            Group("Branches", references.Where(r => r.IsBranch).Select(Ref)),
            Group("Remotes", references.Where(r => r.IsRemote && r.SymbolicTarget is null).Select(Ref)),
            Group("Tags", references.Where(r => r.IsTag).Select(Ref)),
            Group("Stashes", stashes.Select(stash => new TreeViewItem { Header = stash.Subject, Tag = "Stashes" })),
            Group("Submodules", submodules.Select(module => new TreeViewItem { Header = module.Path, Tag = "Submodules" })),
            Group("Worktrees", worktrees.Select(worktree => new TreeViewItem { Header = worktree.Path, Tag = "Worktrees" }))
        };
        IEnumerable<TreeViewItem> groups = _navigation.Items.OfType<TreeViewItem>();
        _navigation.SelectedItem = selectedReference is not null
            ? groups.SelectMany(group => group.Items.OfType<TreeViewItem>()).FirstOrDefault(item => item.Tag is GitReference reference && reference.Name == selectedReference)
            : selectedWorkflow is not null
                ? groups.SelectMany(group => group.Items.OfType<TreeViewItem>()).FirstOrDefault(item => item.Tag as string == selectedWorkflow && item.Header as string == selected?.Header as string)
                : groups.FirstOrDefault(item => item.Header as string == selectedGroup);
    }
}
