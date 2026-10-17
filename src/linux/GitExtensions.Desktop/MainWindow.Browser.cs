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
    private readonly CommitInformationView _commitInfo = new() { Name = "CommitInformation", ShowBranchesAsLinks = true };
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
        _commits.Focusable = true;
        InitializeCommitInformation();
        _commits.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) =>
        {
            Control row = RevisionHistoryRow.Create(commit, _historyGraph.References);
            row.Tag = commit;
            UpdateHistoryRow(row);
            return row;
        });
        _historyGraph.LineHeight = BrowseRowHeight;
        _historyGraph.LaneWidth = 12;
        _historyGraph.ShowReferenceLabels = false;
        _historyGraph.DrawAlternateBackground = true;
        _commits.GotFocus += (_, _) => UpdateHistoryPresentation();
        _commits.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateHistoryPresentation);
        _commits.ActualThemeVariantChanged += (_, _) => UpdateHistoryPresentation();
        _commits.ContextMenu = BuildRevisionContextMenu();
        _navigation.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _updatingNavigation || _busy || _navigation.SelectedItem is not TreeViewItem item) { return; }
            if (item.Tag is GitReference reference)
            {
                _references.SelectedItem = reference;
                SetNavigationBranchFilter(reference.Name);
                await ReloadHistoryAsync();
            }
            else if (item.Tag is GitWorktree { Head: { } head }) { SelectVisibleRevision(head, focus: false); }
            else if (item.Tag is string workflow) { await ShowWorkflowAsync(workflow); }
        };

        DockPanel root = new() { LastChildFill = true };
        StackPanel top = new() { Spacing = 0 };
        DockPanel.SetDock(top, Dock.Top);
        Control toolbar = BuildRepositoryToolbar();
        top.Children.Add(BuildMenu());
        _repositoryToolbars = new StackPanel { Spacing = 0 };
        _repositoryToolbars.Children.Add(toolbar);
        top.Children.Add(_repositoryToolbars);
        root.Children.Add(top);

        Border status = ClassicChrome.Border(_state, new Thickness(0, 1, 0, 0), new Thickness(6, 3));
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        _browserBody = new Grid { ColumnDefinitions = new ColumnDefinitions($"{NavigationWidth},4,*") };
        _browserBody.Children.Add(ClassicChrome.Border(BuildNavigationPanel(), new Thickness(0, 0, 1, 0)));
        GridSplitter navigationSplitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(navigationSplitter, 1);
        _browserBody.Children.Add(navigationSplitter);

        Grid right = new() { RowDefinitions = new RowDefinitions("53*,4,47*") };
        _revisionPanels = new Grid { Name = "RevisionPanels" };
        _revisionPanels.SizeChanged += (_, _) => LimitCommitInfoWidth();
        _revisionTable = WithQuickSearchNotice(BuildRevisionTable());
        _commitInfoSplitter = new GridSplitter { Name = "CommitInfoSplitter", Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        _revisionPanels.Children.Add(_revisionTable);
        _revisionPanels.Children.Add(_commitInfoSplitter);
        right.Children.Add(_revisionPanels);
        GridSplitter detailsSplitter = new() { Name = "DetailsSplitter", Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(detailsSplitter, 1);
        right.Children.Add(detailsSplitter);

        _tabs.Name = "DetailsTabs";
        _commitInfoTab = new TabItem { Name = "CommitInfoTab", Header = IconLabel("Commit", "RepoStateClean.png") };
        _browserDetailTabs = new[]
        {
            new TabItem { Name = "DiffTab", Header = IconLabel("Diff", "Diff.png"), Content = BuildCommitFilesView() },
            new TabItem { Name = "FileTreeTab", Header = IconLabel("File tree", "FileTree.png"), Content = _workflowViews["Files"] },
            new TabItem { Name = "GpgTab", Header = IconLabel("GPG", "Key.png"), Content = _gpgInfo },
            new TabItem { Name = "ConsoleTab", Header = IconLabel("Console", "Console.png"), Content = Panel(_output, Row(Button("Open terminal", OpenTerminalAsync))) },
            new TabItem { Name = "ChangesTab", Header = IconLabel("Working directory", "WorkingDirChanges.png"), Content = _workflowViews["Changes"] }
        };
        _tabs.SelectionChanged += async (_, args) =>
        {
            if (!_layingOutCommitInfo && args.Source == _tabs && !_busy)
            { await LoadVisibleRevisionPanelAsync(); }
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
            Flyout = new MenuFlyout { ItemsSource = _commitInfoPositionItems }
        };
        dropdown.Content = ToolbarDropdownArrow(dropdown, "CommitInfoPositionDropdownArrow");
        ConfigureBrowserFlyout((MenuFlyout)dropdown.Flyout!, dropdown);
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
        => OriginalIconImage(CommitInfoLayoutDescription(position).Icon);

    private static Image OriginalIconImage(string icon)
    {
        return Controls.OriginalMenuItems.Icon(icon);
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
        if (!_busy) { _ = LoadVisibleRevisionPanelAsync(); }
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
        _commitInfo.FocusContent();
    }

    private Control BuildRevisionTable()
    {
        DockPanel panel = new() { Name = "RevisionTable" };
        _virtualRevisions = BuildVirtualRevisionRows();
        DockPanel.SetDock(_virtualRevisions, Dock.Top);
        panel.Children.Add(_virtualRevisions);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
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
            MenuGroup("Start", MenuAction("Open repository…", ChooseRepositoryAsync), MenuAction("Clone repository…", CloneAsync), MenuAction("Create new repository…", InitializeAsync), _favoritesMenu, _recentMenu, MenuAction("Dashboard", CloseRepositoryAsync), MenuAction("Refresh dashboard", RefreshDashboardAsync), MenuAction("Recent repositories settings", RecentRepositorySettingsAsync), MenuAction("Close", CloseRepositoryAsync)),
            BuildRepositoryMenu(),
            BuildHistoryNavigateMenu(),
            BuildHistoryViewMenu(),
            BuildCommandsMenu(),
            MenuGroup("Git hosts", MenuAction("Browse remote repository", BrowseRemoteAsync)),
            MenuGroup("Plugins", NativePluginCatalog.BuiltIn.Concat(LifecycleSettingsPlugins()).Select(PluginMenuItem).ToArray()),
            BuildToolsMenu(),
            BuildHelpMenu()
        }
    };

    private MenuItem MenuGroup(string header, params MenuItem[] items) => new() { Header = T(header), ItemsSource = items };
    private MenuItem BuildCommandsMenu()
    {
        MenuItem commit = MenuAction("Commit…", () => ShowWorkflowAsync("Changes"), "RepoStateClean.png"); commit.Name = "BrowseCommit"; commit.Tag = true;
        commit.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "commitToolStripMenuItem", "Text", "&Commit...") };
        if (commit.Icon is Image commitIcon) { commitIcon.Tag = "RepoStateClean.png"; }
        MenuItem undo = MenuAction("Undo last commit…", UndoLastCommitAsync, "ResetFileTo.png"); undo.Name = "BrowseUndoLastCommit"; undo.Tag = true;
        undo.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "undoLastCommitToolStripMenuItem", "Text", "&Undo last commit...") };
        if (undo.Icon is Image undoIcon) { undoIcon.Tag = "ResetFileTo.png"; }
        MenuItem bisect = MenuAction("Bisect…", BisectAsync, "Bisect.png"); bisect.Name = "BrowseBisect"; bisect.Tag = true;
        bisect.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "bisectToolStripMenuItem", "Text", "B&isect...") };
        if (bisect.Icon is Image bisectIcon) { bisectIcon.Tag = "Bisect.png"; }
        MenuItem commands = MenuGroup("Commands", commit, undo, MenuAction("Create branch…", CreateBranchAsync, "BranchCreate.png"), MenuAction("Delete branch…", DeleteBranchesAsync, "BranchDelete.png"), MenuAction("Checkout branch…", CheckoutAsync, "BranchCheckout.png"), MenuAction("Merge…", MergeAsync), MenuAction("Rebase…", RebaseAsync), MenuAction("Interactive rebase…", InteractiveRebaseAsync), MenuAction("Create tag…", CreateTagAsync, "TagCreate.png"), MenuAction("Delete tag…", DeleteTagsAsync, "TagDelete.png"), MenuAction("Branches and tags…", () => ShowWorkflowAsync("Branches & tags")), MenuAction("Fetch", QuickFetchAsync, "PullFetch.png"), MenuAction("Pull…", PullAsync), MenuAction("Push…", PushAsync), MenuAction("Manage remotes…", () => ManageRemotesAsync(), "Remotes.png"), bisect, BuildReflogMenu(), BuildCleanupMenu(), MenuAction("Apply patch…", ApplyPatchAsync), MenuAction("Create patches…", FormatPatchAsync));
        commands.SubmenuOpened += (_, _) =>
        {
            bool available = !_busy && _repository is { IsBare: false };
            commit.IsEnabled = available;
            undo.IsEnabled = available;
            // FormBrowse updates this exact guard when Commands opens; its Maintenance action can also pass multiple selections.
            bisect.IsEnabled = available && (_commits.SelectedItems?.OfType<GitCommit>().Count() ?? 0) == 1;
        };
        return commands;
    }
    private MenuItem BuildGitMaintenanceMenu()
    {
        MenuItem recover = MenuAction("Recover lost objects…", RecoverLostObjectsAsync, "RecoverLostObjects.png"); recover.Name = "BrowseRecoverLostObjects";
        recover.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "recoverLostObjectsToolStripMenuItem", "Text", "&Recover lost objects...") };
        if (recover.Icon is Image icon) { icon.Tag = "RecoverLostObjects.png"; }
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/Maintenance.png"));
        return new MenuItem { Name = "BrowseGitMaintenance", Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "gitMaintenanceToolStripMenuItem", "Text", "&Git maintenance") },
            Icon = new Image { Source = new Bitmap(stream), Width = 16, Height = 16, Tag = "Maintenance.png" }, ItemsSource = new[] { recover } };
    }
    private MenuItem BuildReflogMenu()
    {
        MenuItem item = MenuAction("Show reflog…", ReflogAsync, "Book.png");
        item.Name = "BrowseShowReflog";
        if (item.Icon is Image icon) { icon.Tag = "Book.png"; }
        item.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "toolStripMenuItemReflog", "Text", "Show reflo&g...") };
        return item;
    }
    private MenuItem BuildCleanupMenu()
    {
        MenuItem item = MenuAction("Clean working directory…", CleanupRepositoryAsync, "CleanupRepo.png"); item.Name = "BrowseCleanupRepository"; item.Tag = true;
        item.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "cleanupToolStripMenuItem", "Text", "Clean &working directory...") };
        if (item.Icon is Image icon) { icon.Tag = "CleanupRepo.png"; } return item;
    }
    private MenuItem SaveViewSettingsMenu()
    {
        MenuItem item = MenuAction("Save current view settings as default", SaveCurrentViewSettingsAsync);
        item.Name = "SaveCurrentViewSettingsAsDefault";
        return item;
    }
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
            item.Icon = OriginalIconImage(icon);
        }
        _actionMenus.Add(item);
        item.Click += async (_, _) => { try { await WithMenuActionFocusAsync(item, action); } catch (Exception exception) { AppendOutput(exception.Message); } };
        return item;
    }

    private Control IconLabel(string label, string name, bool translate = true)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Uri uri = new($"avares://GitExtensions/Assets/Icons/{name}");
        if (AssetLoader.Exists(uri)) { panel.Children.Add(OriginalIconImage(name)); }
        panel.Children.Add(new TextBlock { Text = translate ? T(label) : label, VerticalAlignment = VerticalAlignment.Center });
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
            SetNavigationVisible(!visible);
        }
        return Task.CompletedTask;
    }

    private void SelectDetailsTab(string name) => _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().First(item => item.Name == name);

    private async Task ShowWorkflowAsync(string name)
    {
        if (name == "Stashes") { await ShowStashDialogAsync(); return; }
        if (name == "Worktrees") { await ManageWorktreesAsync(); return; }
        if (name == "Submodules") { await ManageSubmodulesAsync(); return; }
        if (name == "Changes")
        {
            if (_repository is null || _busy) { return; }
            if (_repository.IsBare) { _state.Text = T("Open a worktree to commit changes in a bare repository."); return; }
            GitRepository repository = _repository;
            CommitDialog commit = new(repository, afterSubmoduleUpdateAsync: (owner, ct) => AfterSubmoduleUpdateAsync(repository, owner, ct)) { RequestedThemeVariant = ActualThemeVariant };
            await RunAsync("Open commit dialog", _ => commit.InitializeAsync(), refresh: false);
            await commit.ShowDialog(this);
            if (commit.HasRepositoryChanges) { await RefreshAsync(); }
            return;
        }
        Control content = _workflowViews[name];
        if (content.Parent is not null) { return; }
        Window dialog = new() { Title = T(name) + " — Git Extensions", Width = 960, Height = 600, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        dialog.Closed += (_, _) => dialog.Content = null;
        await dialog.ShowDialog(this);
    }

    private Task CheckoutCommitAsync() => RunAsync("Checkout revision", r => r.CheckoutAsync(SelectedCommit().Hash, detached: true, cancellationToken: Token));

    private void UpdateCommitInfo(GitCommit commit)
    {
        _commitInfoLoadTask = null;
        _commitInfo.SetRevision(_repository, commit, _history);
    }

}
