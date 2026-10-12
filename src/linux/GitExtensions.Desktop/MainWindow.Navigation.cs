using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
        foreach ((string name, string icon) in new[] { ("Branches", "BranchLocal.png"), ("Remotes", "BranchRemote.png"),
            ("Tags", "TagHorizontal.png"), ("Submodules", "FolderSubmodule.png"), ("Stashes", "stash.png"), ("Worktrees", "WorkTree.png") })
        {
            Avalonia.Controls.Primitives.ToggleButton toggle = new()
            { Name = "NavigationShow" + name, Content = OriginalIconImage(icon), IsChecked = true, Width = 24, Height = 24,
                MinWidth = 0, MinHeight = 24, Padding = default };
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
        _navigation.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>())
        { Setters = { new Setter(MinHeightProperty, BrowseRowHeight), new Setter(PaddingProperty, new Thickness(0)) } });
        panel.Children.Add(_navigation);
        return panel;
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
                        group == "Remotes" && prefix.Length == 0 ? "BranchRemote.png" : "FolderClosed.png", new NavigationFolder(path), References(group, nested, path), true);
                }
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
                { groups.Add(Node("group:" + name, T(name), icon, new NavigationGroup(name), nodes, name != "Tags")); }
            }
            Group("Branches", "BranchLocal.png", References("Branches", _navigationReferences.Where(reference => reference.IsBranch && Matches(reference.ShortName))));
            Group("Remotes", "BranchRemote.png", References("Remotes", _navigationReferences.Where(reference => reference.IsRemote && reference.SymbolicTarget is null && Matches(reference.ShortName))));
            Group("Tags", "TagHorizontal.png", References("Tags", _navigationReferences.Where(reference => reference.IsTag && Matches(reference.ShortName))));
            Group("Submodules", "FolderSubmodule.png", _navigationSubmodules.Where(module => Matches(module.Path)).Select(module => Node("submodule:" + module.Path, module.Path, "FolderSubmodule.png", "Submodules")));
            Group("Stashes", "stash.png", _navigationStashes.Where(stash => Matches(stash.Subject)).Select(stash => Node("stash:" + stash.Reference, stash.Subject, "stash.png", "Stashes")));
            Group("Worktrees", "WorkTree.png", _navigationWorktrees.Where(worktree => Matches(worktree.Path)).Select(worktree => Node("worktree:" + worktree.Path, worktree.Path, "WorkTree.png", "Worktrees")));
            _navigationAppliedFilter = filter;
            _navigation.ItemsSource = groups;
            _navigation.SelectedItem = NavigationItems().FirstOrDefault(node => _navigationKeys.GetValueOrDefault(node) == _navigationSelectionKey);
        }
        finally { _updatingNavigation = false; }
    }
}
