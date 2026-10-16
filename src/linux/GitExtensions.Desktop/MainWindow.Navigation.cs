using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private Control? _navigationPanel;
    private readonly TextBox _navigationSearch = new() { Name = "RepositoryNavigationSearch", Height = 22, MinHeight = 22, Padding = new Thickness(3, 1) };
    private readonly Dictionary<string, bool> _navigationGroups = new(StringComparer.Ordinal)
    { ["Branches"] = true, ["Remotes"] = true, ["Tags"] = true, ["Submodules"] = true, ["Stashes"] = true, ["Worktrees"] = true };
    private readonly Dictionary<string, bool> _navigationExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<TreeViewItem, string> _navigationKeys = [];
    private string? _navigationSelectionKey;
    private string _navigationAppliedFilter = "";
    private bool _updatingNavigation;
    private IReadOnlyList<GitReference> _navigationReferences = [];
    private IReadOnlyList<GitStash> _navigationStashes = [];
    private IReadOnlyList<GitSubmodule> _navigationSubmodules = [];
    private IReadOnlyList<GitWorktree> _navigationWorktrees = [];
    private sealed record NavigationFolder(string Path);
    private sealed record NavigationGroup(string Name);

    private Control BuildNavigationPanel()
    {
        DockPanel panel = new() { Name = "RepositoryNavigationPanel", LastChildFill = true };
        _navigationPanel = panel;
        StackPanel toolbar = new() { Name = "RepositoryNavigationToolbar", Orientation = Orientation.Horizontal, Spacing = 0, Height = 24 };
        Button collapse = ToolbarButton("NavigationCollapseAll", "Collapse all", "CollapseAll.png", () =>
        { foreach (TreeViewItem item in NavigationItems()) { item.IsExpanded = false; } return Task.CompletedTask; });
        toolbar.Children.Add(collapse);
        foreach ((string name, string icon) in new[] { ("Branches", "LocalBranchRoot.png"), ("Remotes", "RemoteBranchRoot.png"),
            ("Tags", "TagHorizontal.png"), ("Submodules", "FolderSubmodule.png"), ("Stashes", "stash.png"), ("Worktrees", "WorkTree.png") })
        {
            Avalonia.Controls.Primitives.ToggleButton toggle = new()
            { Name = "NavigationShow" + name, Content = OriginalIconImage(icon), IsChecked = true, Width = 24, Height = 24,
                MinWidth = 0, MinHeight = 24, Padding = default, CornerRadius = default,
                Resources = new ResourceDictionary { ThemeDictionaries =
                { [ThemeVariant.Light] = NavigationToolbarResources(false), [ThemeVariant.Dark] = NavigationToolbarResources(true) } } };
            ToolTip.SetTip(toggle, T("Show " + name.ToLowerInvariant()));
            toggle.IsCheckedChanged += (_, _) => { _navigationGroups[name] = toggle.IsChecked == true; RebuildNavigation(); };
            toolbar.Children.Add(toggle);
        }
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        Grid search = new() { ColumnDefinitions = new ColumnDefinitions("*,20"), Height = 24, Margin = new Thickness(3, 0, 3, 1) };
        search.Children.Add(_navigationSearch);
        TextBlock magnifier = new() { Text = "⌕", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 17 };
        Grid.SetColumn(magnifier, 1); search.Children.Add(magnifier);
        ToolTip.SetTip(_navigationSearch, T("Filter references"));
        _navigationSearch.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { RebuildNavigation(); } };
        _navigationSearch.KeyDown += (_, args) =>
        { if (args.Key == Avalonia.Input.Key.Escape) { _navigationSearch.Text = ""; args.Handled = true; } };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        _navigation.KeyDown += async (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Enter || args.KeyModifiers != Avalonia.Input.KeyModifiers.None || _busy
                || _navigation.SelectedItem is not TreeViewItem { Tag: GitWorktree worktree }) { return; }
            args.Handled = true;
            if (IsCurrentWorktreePath(worktree.Path) || !Directory.Exists(worktree.Path)) { return; }
            try { await OpenWorktreePathAsync(worktree.Path, confirm: true); }
            catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
        };
        panel.Children.Add(Controls.ClassicTreeViewAppearance.Wrap(_navigation));
        return panel;
    }

    private static ResourceDictionary NavigationToolbarResources(bool dark)
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark);
        ResourceDictionary resources = new();
        // The photo's professional ToolStrip uses a pale blue checked surface.
        // Set the template resources too: Fluent otherwise uses the platform accent.
        IBrush checkedFill = Brush.Parse(dark ? colors["Highlight"] : "#c2e0ff");
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled" })
        {
            bool disabled = state.EndsWith("Disabled", StringComparison.Ordinal);
            bool selected = state.StartsWith("Checked", StringComparison.Ordinal) && !disabled;
            string neutral = state.Contains("Pressed", StringComparison.Ordinal) ? "ControlDark"
                : state.Contains("PointerOver", StringComparison.Ordinal) ? "ControlLight" : "Control";
            resources["ToggleButtonBackground" + state] = selected ? checkedFill : Brush.Parse(colors[neutral]);
            resources["ToggleButtonBorderBrush" + state] = selected ? Brush.Parse(colors["Highlight"]) : Brushes.Transparent;
            resources["ToggleButtonForeground" + state] = Brush.Parse(colors[disabled ? "GrayText" : "ControlText"]);
        }
        return resources;
    }

    private IEnumerable<TreeViewItem> NavigationItems()
    {
        static IEnumerable<TreeViewItem> Descendants(IEnumerable<TreeViewItem> items)
        { foreach (TreeViewItem item in items) { yield return item; foreach (TreeViewItem child in Descendants(item.Items.OfType<TreeViewItem>())) { yield return child; } } }
        return Descendants(_navigation.Items.OfType<TreeViewItem>());
    }

    private void UpdateBrowserNavigation(IReadOnlyList<GitReference> references, IReadOnlyList<GitStash> stashes,
        IReadOnlyList<GitSubmodule> submodules, IReadOnlyList<GitWorktree> worktrees)
    {
        _navigationReferences = references; _navigationStashes = stashes; _navigationSubmodules = submodules; _navigationWorktrees = worktrees;
        RebuildNavigation();
        RefreshWorktreeMenu();
    }

    private void RebuildNavigation()
    {
        if (_updatingNavigation) { return; }
        if (_navigationAppliedFilter.Length == 0)
        { foreach ((TreeViewItem node, string key) in _navigationKeys) { _navigationExpansion[key] = node.IsExpanded; } }
        if (_navigation.SelectedItem is TreeViewItem selected)
        {
            if (selected.Tag is GitReference reference) { _navigationSelectionKey = "ref:" + reference.Name; }
            else if (_navigationKeys.TryGetValue(selected, out string? selection)) { _navigationSelectionKey = selection; }
        }
        string filter = _navigationSearch.Text ?? "";
        bool Matches(string caption) => caption.Contains(filter, StringComparison.OrdinalIgnoreCase);
        TreeViewItem Node(string key, string caption, string icon, object? tag, IEnumerable<TreeViewItem>? children = null, bool expanded = false)
        {
            TreeViewItem node = new() { Header = IconLabel(caption, icon, translate: false), Tag = tag,
                IsExpanded = filter.Length > 0 || (_navigationExpansion.TryGetValue(key, out bool previous) ? previous : expanded) };
            if (children is not null) { node.ItemsSource = children.ToArray(); }
            if (tag is GitReference { IsCurrent: true } && node.Header is StackPanel label)
            { label.Children.OfType<TextBlock>().Single().FontWeight = FontWeight.Bold; }
            ToolTip.SetTip(node, caption); _navigationKeys[node] = key; return node;
        }
        IEnumerable<TreeViewItem> References(string group, IEnumerable<GitReference> refs, string prefix = "")
        {
            GitReference[] entries = refs.OrderBy(reference => reference.ShortName, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (IGrouping<string, GitReference> segment in entries.GroupBy(reference => reference.ShortName[prefix.Length..].Split('/')[0], StringComparer.Ordinal))
            {
                GitReference[] direct = segment.Where(reference => reference.ShortName[prefix.Length..] == segment.Key).ToArray();
                foreach (GitReference reference in direct)
                { yield return Node("ref:" + reference.Name, segment.Key, reference.IsTag ? "TagHorizontal.png" : reference.IsRemote ? "BranchRemote.png" : "BranchLocal.png", reference); }
                GitReference[] nested = segment.Except(direct).ToArray();
                if (nested.Length > 0)
                {
                    string path = prefix + segment.Key + "/";
                    yield return Node("folder:" + group + ":" + path, segment.Key,
                        group == "Remotes" && prefix.Length == 0 ? "Remote.png" : "BranchFolder.png", new NavigationFolder(path), References(group, nested, path), true);
                }
            }
        }
        IEnumerable<TreeViewItem> Worktrees()
        {
            string main = _navigationWorktrees.FirstOrDefault()?.Path ?? _repository?.Path ?? "";
            string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(main)) ?? main;
            string[] paths = _navigationWorktrees.Select(worktree => Path.GetRelativePath(parent, worktree.Path)).ToArray();
            string prefix = CommonWorktreePathPrefix(paths.Skip(1).ToArray());
            for (int i = 0; i < _navigationWorktrees.Count; i++)
            {
                GitWorktree worktree = _navigationWorktrees[i];
                string path = i != 0 && prefix.Length != 0 ? paths[i][prefix.Length..] : paths[i];
                string caption = WorktreeDisplayName(worktree, path);
                if (!Matches(caption) && !Matches(worktree.Path)) { continue; }
                TreeViewItem node = Node("worktree:" + worktree.Path, caption, "WorkTree.png", worktree);
                node.ContextMenu = BuildWorktreeNodeMenu(worktree);
                bool current = IsCurrentWorktreePath(worktree.Path), exists = Directory.Exists(worktree.Path);
                if (node.Header is StackPanel label)
                {
                    TextBlock text = label.Children.OfType<TextBlock>().Single();
                    if (current) { text.FontWeight = FontWeight.Bold; }
                    if (!exists) { text.Opacity = .6; }
                }
                ToolTip.SetTip(node, worktree.Path + (current ? " (" + T("current") + ")" : !exists ? " (" + T("deleted") + ")" : "")
                    + "\n" + WorktreeDisplayName(worktree, T("Branch") + ":") + (worktree.Head is null ? "" : "\nHEAD: " + worktree.Head));
                node.DoubleTapped += async (_, args) =>
                {
                    args.Handled = true;
                    if (_busy || IsCurrentWorktreePath(worktree.Path) || !Directory.Exists(worktree.Path)) { return; }
                    try { await OpenWorktreePathAsync(worktree.Path, confirm: true); }
                    catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
                };
                yield return node;
            }
        }
        _updatingNavigation = true;
        try
        {
            _navigation.SelectedItem = null; _navigationKeys.Clear();
            List<TreeViewItem> groups = [];
            void Group(string name, string icon, IEnumerable<TreeViewItem> children)
            {
                TreeViewItem[] nodes = children.ToArray();
                if (_navigationGroups[name] && (filter.Length == 0 || nodes.Length > 0))
                {
                    TreeViewItem root = Node("group:" + name, T(name), icon, new NavigationGroup(name), nodes, name != "Tags");
                    if (name == "Worktrees") { root.ContextMenu = BuildWorktreeRootMenu(); }
                    groups.Add(root);
                }
            }
            Group("Branches", "LocalBranchRoot.png", References("Branches", _navigationReferences.Where(reference => reference.IsBranch && Matches(reference.ShortName))));
            Group("Remotes", "RemoteBranchRoot.png", References("Remotes", _navigationReferences.Where(reference => reference.IsRemote && reference.SymbolicTarget is null && Matches(reference.ShortName))));
            Group("Tags", "TagHorizontal.png", References("Tags", _navigationReferences.Where(reference => reference.IsTag && Matches(reference.ShortName))));
            Group("Submodules", "FolderSubmodule.png", _navigationSubmodules.Where(module => Matches(module.Path)).Select(module => Node("submodule:" + module.Path, module.Path, "FolderSubmodule.png", "Submodules")));
            Group("Stashes", "stash.png", _navigationStashes.Where(stash => Matches(stash.Subject)).Select(stash => Node("stash:" + stash.Reference, stash.Subject, "stash.png", "Stashes")));
            Group("Worktrees", "WorkTree.png", Worktrees());
            _navigationAppliedFilter = filter;
            _navigation.ItemsSource = groups;
            _navigation.SelectedItem = NavigationItems().FirstOrDefault(node => _navigationKeys.GetValueOrDefault(node) == _navigationSelectionKey);
        }
        finally { _updatingNavigation = false; }
    }
}
