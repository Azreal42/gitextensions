using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly BrowserRead _referenceSortingRead = new();
    private void ApplyReferenceSorting(GitRepository repository) => repository.ReferenceSorting = _preferences.ReferenceSorting;

    private void ConfigureReferenceSortingMenu()
    {
        ContextMenu menu = new() { Name = "RepositoryNavigationContextMenu" };
        MenuItem filter = new()
        {
            Name = "filterForSelectedRefsMenuItem",
            Header = _translations.TranslateAccessKeys("RepoObjectsTree", "filterForSelectedRefsMenuItem", "Text", "&Filter for selected"),
            Icon = OriginalIconImage("ShowThisBranchOnly.png")
        };
        ToolTip.SetTip(filter, _translations.Translate("RepoObjectsTree", "filterForSelectedRefsMenuItem", "ToolTipText",
            "Filter the revision grid to show selected (underlined) refs (branches and tags) only.\n"
            + "Hold CTRL while clicking to de/select multiple and include descendant tree nodes by additionally holding SHIFT.\n"
            + "Reset the filter via View > Show all branches."));
        MenuItem by = new() { Name = "GitRefsSortByContextMenuItem", Header = T("Sort by"), Icon = OriginalIconImage("SortBy.png") };
        MenuItem order = new() { Name = "GitRefsSortOrderContextMenuItem", Header = T("Sort order"), Icon = OriginalIconImage("SortBy.png") };
        menu.ItemsSource = new object[] { filter, new Separator(), by, order };
        string? originKey = null;
        GitRepository? originRepository = null;
        long originVersion = 0;
        string[] originReferences = [];
        filter.Click += async (_, _) =>
        {
            try { await FilterMarkedNavigationReferencesAsync(originRepository, originVersion, originReferences); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
        };
        foreach ((GitRefsSortBy value, string label) in DesktopSettingsDialog.ReferenceSortChoices)
        {
            MenuItem item = new() { Name = "NavigationSortBy" + value, Header = label, Tag = value, ToggleType = MenuItemToggleType.CheckBox };
            item.Click += async (_, _) => await ChangeAsync(_preferences.ReferenceSorting with { By = value }); by.Items.Add(item);
        }
        foreach (GitRefsSortOrder value in Enum.GetValues<GitRefsSortOrder>())
        {
            MenuItem item = new() { Name = "NavigationSortOrder" + value, Header = DesktopSettingsDialog.ReferenceOrderLabel(value), Tag = value, ToggleType = MenuItemToggleType.CheckBox };
            item.Click += async (_, _) => await ChangeAsync(_preferences.ReferenceSorting with { Order = value }); order.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            originRepository = _repository; originVersion = _navigationMarkedVersion;
            originReferences = MarkedNavigationReferences();
            bool available = originRepository is not null && !_busy && !_lifetime.IsCancellationRequested && !IsBranchFilterOwnerClosing;
            bool leaf = MarkedNavigationNodeCount == 1 && _navigation.SelectedItem is TreeViewItem { Tag: GitReference } && available;
            filter.IsEnabled = available && originReferences.Length > 0;
            by.IsEnabled = leaf; order.IsEnabled = leaf && _preferences.ReferenceSorting.By != GitRefsSortBy.GitDefault;
            originKey = _navigation.SelectedItem is TreeViewItem node && _navigationKeys.TryGetValue(node, out string? key) ? key : null;
            foreach (MenuItem item in by.Items.OfType<MenuItem>()) { item.IsChecked = Equals(item.Tag, _preferences.ReferenceSorting.By); }
            foreach (MenuItem item in order.Items.OfType<MenuItem>()) { item.IsChecked = Equals(item.Tag, _preferences.ReferenceSorting.Order); }
            object[] scripts = _navigation.SelectedItem is TreeViewItem { Tag: GitReference { IsBranch: true } }
                ? ScriptMenuItems.Build(_scripts, script => script.AddToRevisionGridContextMenu,
                    script => () => _repository == originRepository && _navigationMarkedVersion == originVersion && MarkedNavigationNodeCount == 1
                        ? RunScriptAsync(script) : Task.CompletedTask,
                    leaf, _translations, "RepoObjectsTree") : [];
            menu.ItemsSource = new object[] { filter, new Separator(), by, order }.Concat(scripts).ToArray();
        };
        _navigation.ContextMenu = menu;
        async Task ChangeAsync(GitReferenceSorting sorting)
        {
            if (_busy || _repository is null || _repository != originRepository || _navigationMarkedVersion != originVersion
                || MarkedNavigationNodeCount != 1 || _navigation.SelectedItem is not TreeViewItem { Tag: GitReference }) { return; }
            string? key = originKey;
            DesktopPreferences latest = _preferencesStore.Load();
            _preferencesStore.Save(latest with { ReferenceSorting = sorting });
            _preferences = _preferences with { ReferenceSorting = sorting };
            await ApplyReferenceSortingPreferencesAsync(key, menu);
        }
    }

    internal Task ApplyReferenceSortingPreferencesAsync() => ApplyReferenceSortingPreferencesAsync(null, null);

    private async Task ApplyReferenceSortingPreferencesAsync(string? focusedKey, ContextMenu? originMenu)
    {
        if (_repository is not GitRepository repository || _lifetime.IsCancellationRequested) { return; }
        ApplyReferenceSorting(repository);
        if (_busy) { return; }
        focusedKey ??= FocusManager?.GetFocusedElement() is Control focused
            ? focused.GetSelfAndVisualAncestors().OfType<TreeViewItem>().Where(_navigationKeys.ContainsKey).Select(node => _navigationKeys[node]).FirstOrDefault() : null;
        GitReferenceSorting sorting = _preferences.ReferenceSorting;
        string branches = _preferences.PrioritizedBranchNames, remotes = _preferences.PrioritizedRemoteNames;
        await ReadBrowserAsync(_referenceSortingRead, "Sort references", async (current, token) =>
        {
            IReadOnlyList<GitReference> references = await current.GetReferencesAsync(token);
            token.ThrowIfCancellationRequested();
            if (_repository != current || sorting != _preferences.ReferenceSorting || branches != _preferences.PrioritizedBranchNames || remotes != _preferences.PrioritizedRemoteNames) { return; }
            string? selection = (_references.SelectedItem as GitReference)?.Name;
            _references.ItemsSource = references; _references.SelectedItem = references.FirstOrDefault(reference => reference.Name == selection);
            UpdateBranchFilterReferences(references, opening: false);
            _navigationReferences = references; RebuildNavigation();
            _historyGraph.References = HistoryViewReferences(references); UpdateHistoryPresentation(); RefreshBranchMenu();
            string? key = focusedKey;
            Dispatcher.UIThread.Post(() =>
            {
                if (key is null || token.IsCancellationRequested || _repository != current || _busy || _lifetime.IsCancellationRequested
                    || !IsVisible || !IsEffectivelyEnabled || OwnedWindows.Any(window => window.IsVisible) || originMenu?.IsOpen == true
                    || sorting != _preferences.ReferenceSorting || branches != _preferences.PrioritizedBranchNames || remotes != _preferences.PrioritizedRemoteNames) { return; }
                Control? focused = FocusManager?.GetFocusedElement() as Control;
                if (focused is not null && focused is not Window && focused.IsEffectivelyVisible && focused.IsEffectivelyEnabled
                    && TopLevel.GetTopLevel(focused) is { IsVisible: true } && !_navigation.IsKeyboardFocusWithin
                    && (originMenu is null || !originMenu.GetLogicalDescendants().Contains(focused))) { return; }
                TreeViewItem? target = NavigationItems().FirstOrDefault(node => _navigationKeys.GetValueOrDefault(node) == key);
                if (target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) { target.Focus(); }
            }, DispatcherPriority.Loaded);
        });
    }

    private IEnumerable<TreeViewItem> ReferenceRemoteNodes(IEnumerable<GitReference> references,
        Func<string, IEnumerable<GitReference>, string, IEnumerable<TreeViewItem>> createChildren,
        Func<string, string, string, object?, IEnumerable<TreeViewItem>?, bool, TreeViewItem> node)
    {
        string[] remoteNames = _remotes.Items.OfType<GitRemote>().Select(remote => remote.Name).OrderByDescending(name => name.Length).ToArray();
        string RemoteName(GitReference reference) => remoteNames.FirstOrDefault(name => reference.ShortName.StartsWith(name + "/", StringComparison.Ordinal)) ?? reference.ShortName.Split('/')[0];
        var groups = references.GroupBy(RemoteName, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.CurrentCulture);
        foreach (var group in GitReferencePriorities.OrderByPriority(groups, group => group.Key, _preferences.PrioritizedRemoteNames))
        {
            string prefix = group.Key + "/";
            IReadOnlyList<GitReference> branches = GitReferencePriorities.OrderByPriority(group, reference => reference.ShortName[prefix.Length..], _preferences.PrioritizedBranchNames);
            yield return node("folder:Remotes:" + prefix, group.Key, "Remote.png", new NavigationFolder(prefix), createChildren("Remotes", branches, prefix), true);
        }
    }
}
