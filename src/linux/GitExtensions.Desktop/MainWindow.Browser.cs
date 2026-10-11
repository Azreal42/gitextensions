using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

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

    private string T(string source) => _translations.Translate(source);

    private Control BuildLayout()
    {
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
            ItemsSource = new[] { MenuAction("Checkout revision", CheckoutCommitAsync), MenuAction("Cherry-pick", CherryPickAsync), MenuAction("Revert commit", RevertAsync), MenuAction("Reset branch…", ResetAsync), MenuAction("Compare…", CompareAsync) }
        };
        _navigation.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _busy || _navigation.SelectedItem is not TreeViewItem item) { return; }
            if (item.Tag is GitReference reference)
            {
                _references.SelectedItem = reference;
                _historyRevision.Text = reference.Name;
                await RefreshAsync();
            }
            else if (item.Tag is string workflow) { await ShowWorkflowAsync(workflow); }
        };

        DockPanel root = new() { LastChildFill = true };
        StackPanel top = new() { Spacing = 0 };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(BuildMenu());
        _repositoryPath.MinWidth = 240;
        _repositoryPath.Width = 300;
        _repositoryPath.Height = 24;
        _recent.Height = 24;
        top.Children.Add(new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brush.Parse("#B4B4B4"), Padding = new Thickness(4, 2),
            Child = Row(IconButton("Refresh", "ReloadRevisions.png", RefreshAsync, false),
                IconButton("Toggle left panel", "LayoutSidebarLeft.png", ToggleNavigationAsync, false),
                IconButton("Open repository", "FolderClosed.png", ChooseRepositoryAsync, false), _repositoryPath,
                IconButton("Pull", "Pull.png", PullAsync), IconButton("Push", "Push.png", PushAsync),
                IconButton("Commit", "RepoStateClean.png", () => ShowWorkflowAsync("Changes")),
                IconButton("Stash", "stash.png", StashAsync), IconButton("Console", "Console.png", OpenTerminalAsync, false),
                IconButton("Settings", "Settings.png", () => ShowWorkflowAsync("Configuration"), false), _recent)
        });
        _historyRevision.Height = 24;
        top.Children.Add(Row(new TextBlock { Text = T("Branches:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) },
            _historyRevision, Button("Filter", RefreshAsync), Button("All branches", () => { _historyRevision.Text = null; return RefreshAsync(); }),
            Button("Fetch", () => RunAsync("Fetch", r => r.FetchAsync(cancellationToken: Token))), Button("Cancel operation", () => { _operation?.Cancel(); return Task.CompletedTask; })));
        root.Children.Add(top);

        Border status = new() { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush.Parse("#B4B4B4"), Padding = new Thickness(6, 3), Child = _state };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        _browserBody = new Grid { ColumnDefinitions = new ColumnDefinitions("220,4,*") };
        _browserBody.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = Brush.Parse("#B4B4B4"), Child = _navigation });
        GridSplitter navigationSplitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(navigationSplitter, 1);
        _browserBody.Children.Add(navigationSplitter);

        Grid right = new() { RowDefinitions = new RowDefinitions("2*,4,3*") };
        Grid revisions = new() { ColumnDefinitions = new ColumnDefinitions("3*,4,1*") };
        revisions.Children.Add(BuildRevisionTable());
        GridSplitter infoSplitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(infoSplitter, 1);
        revisions.Children.Add(infoSplitter);
        Grid.SetColumn(_commitInfo, 2);
        revisions.Children.Add(_commitInfo);
        right.Children.Add(revisions);
        GridSplitter detailsSplitter = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(detailsSplitter, 1);
        right.Children.Add(detailsSplitter);

        _tabs.Name = "DetailsTabs";
        _tabs.ItemsSource = new[]
        {
            new TabItem { Name = "DiffTab", Header = IconLabel("Diff", "Diff.png"), Content = new DiffView(_diff) { Name = "CommitDiff" } },
            new TabItem { Name = "FileTreeTab", Header = IconLabel("File tree", "FileTree.png"), Content = _workflowViews["Files"] },
            new TabItem { Name = "GpgTab", Header = T("GPG"), Content = _gpgInfo },
            new TabItem { Name = "ConsoleTab", Header = IconLabel("Console", "Console.png"), Content = Panel(_output, Row(Button("Open terminal", OpenTerminalAsync))) },
            new TabItem { Name = "ChangesTab", Header = IconLabel("Working directory", "WorkingDirChanges.png"), Content = _workflowViews["Changes"] }
        };
        _tabs.SelectionChanged += async (_, args) =>
        {
            if (args.Source == _tabs && (_tabs.SelectedItem as TabItem)?.Name == "GpgTab")
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
        Grid.SetColumn(right, 2);
        _browserBody.Children.Add(right);
        root.Children.Add(_browserBody);
        return root;
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
        Border header = new() { Name = "RevisionHeader", Background = Brush.Parse("#F0F0F0"), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brush.Parse("#B4B4B4"), Child = headers };
        header.ActualThemeVariantChanged += (_, _) => header.Background = Brush.Parse(header.ActualThemeVariant == ThemeVariant.Dark ? "#373737" : "#F0F0F0");
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
            MenuGroup("File", MenuAction("Open repository…", ChooseRepositoryAsync), MenuAction("Clone repository…", CloneAsync), MenuAction("Create new repository…", InitializeAsync), MenuAction("Close", () => { Close(); return Task.CompletedTask; })),
            MenuGroup("Dashboard", MenuAction("Recent repositories", () => { _recent.IsDropDownOpen = true; return Task.CompletedTask; })),
            MenuGroup("Repository", MenuAction("Commit…", () => ShowWorkflowAsync("Changes")), MenuAction("Stash…", () => ShowWorkflowAsync("Stashes")), MenuAction("Worktrees…", () => ShowWorkflowAsync("Worktrees")), MenuAction("Submodules…", () => ShowWorkflowAsync("Submodules")), MenuAction("Maintenance…", () => ShowWorkflowAsync("Repository"))),
            MenuGroup("Commands", MenuAction("Branches and tags…", () => ShowWorkflowAsync("Branches & tags")), MenuAction("Merge…", MergeAsync), MenuAction("Rebase…", RebaseAsync), MenuAction("Interactive rebase…", InteractiveRebaseAsync), MenuAction("Pull…", PullAsync), MenuAction("Push…", PushAsync), MenuAction("Manage remotes…", () => ShowWorkflowAsync("Remotes")), MenuAction("Apply patch…", ApplyPatchAsync), MenuAction("Create patches…", FormatPatchAsync)),
            MenuGroup("Git hosts", MenuAction("Browse remote repository", BrowseRemoteAsync)),
            MenuGroup("Plugins", new MenuItem { Header = T("Plugins unavailable in this preview"), IsEnabled = false }),
            MenuGroup("Tools", MenuAction("Settings…", () => ShowWorkflowAsync("Configuration")), MenuAction("Terminal", OpenTerminalAsync), MenuAction("Theme", ChangeThemeAsync), MenuAction("Show left panel", ToggleNavigationAsync)),
            MenuGroup("Help", MenuAction("Online manual", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { "https://git-extensions-documentation.readthedocs.io/" } }); return Task.CompletedTask; }))
        }
    };

    private MenuItem MenuGroup(string header, params MenuItem[] items) => new() { Header = T(header), ItemsSource = items };
    private MenuItem MenuAction(string header, Func<Task> action)
    {
        MenuItem item = new() { Header = T(header) };
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
        if (name == "Changes") { SelectDetailsTab("ChangesTab"); _commitMessage.Focus(); return; }
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
        TreeViewItem Group(string name, IEnumerable<TreeViewItem> children) => new() { Header = T(name), IsExpanded = name != "Tags", ItemsSource = children.ToArray() };
        TreeViewItem Ref(GitReference reference) => new() { Header = IconLabel(reference.ShortName, reference.IsTag ? "Tag.png" : reference.IsRemote ? "BranchRemote.png" : "BranchLocal.png"), Tag = reference };
        _navigation.ItemsSource = new[]
        {
            Group("Branches", references.Where(r => r.IsBranch).Select(Ref)),
            Group("Remotes", references.Where(r => r.IsRemote && r.SymbolicTarget is null).Select(Ref)),
            Group("Tags", references.Where(r => r.IsTag).Select(Ref)),
            Group("Stashes", stashes.Select(stash => new TreeViewItem { Header = stash.Subject, Tag = "Stashes" })),
            Group("Submodules", submodules.Select(module => new TreeViewItem { Header = module.Path, Tag = "Submodules" })),
            Group("Worktrees", worktrees.Select(worktree => new TreeViewItem { Header = worktree.Path, Tag = "Worktrees" }))
        };
    }
}
