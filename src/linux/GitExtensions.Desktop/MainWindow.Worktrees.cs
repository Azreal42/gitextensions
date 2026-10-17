using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly MenuFlyout _worktreeFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, HorizontalOffset = -24 };
    private readonly BrowserRead _worktreeMenuRead = new();
    private Button? _worktreeButton, _worktreeDropdown;
    private Grid? _worktreeControl;
    private MenuItem? _worktreeOverflow, _manageWorktreesMenu;
    private IReadOnlyList<GitWorktree> _worktreeFlyoutValues = [], _worktreeOverflowValues = [];
    private GitRepository? _worktreeFlyoutRepository, _worktreeOverflowRepository;

    private async Task ManageWorktreesAsync(string? selectedPath = null)
    {
        if (_repository is not GitRepository repository || _busy) { return; }
        CloseWorktreeMenus();
        WorktreeDialog dialog = new(repository, selectedPath, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol);
        await ShowRepositoryDialogAsync(dialog);
        if (dialog.RequestedWorktreePath is string path && _repository == repository && !_lifetime.IsCancellationRequested && IsVisible)
        { await OpenRepositoryAsync(path); }
    }

    private async Task CreateWorktreeAsync()
    {
        if (_repository is not GitRepository repository || _busy) { return; }
        CloseWorktreeMenus();
        string? mainPath = _navigationWorktrees.FirstOrDefault()?.Path;
        CreateWorktreeDialog dialog = new(repository, mainPath, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol);
        await ShowRepositoryDialogAsync(dialog);
        if (dialog.CreatedWorktreePath is string path && _repository == repository && !_lifetime.IsCancellationRequested && IsVisible)
        { await OpenWorktreePathAsync(path, confirm: true); }
    }

    private async Task OpenWorktreePathAsync(string path, bool confirm)
    {
        if (_repository is not GitRepository repository || _busy) { return; }
        CloseWorktreeMenus();
        string? destination = null;
        await RunAsync("Open worktree", async _ =>
        {
            if (confirm)
            {
                if (await WorktreeDialogControls.ConfirmOpenAsync(WorkflowOwner, repository, path, Token)) { destination = path; }
            }
            else
            {
                // The toolbar switches directly, while retaining the same native identity checks.
                var expected = (await repository.GetWorktreeSnapshotsAsync(Token)).SingleOrDefault(item => SameWorktreePath(item.Path, path))
                    ?? throw new InvalidOperationException(T("The selected worktree is no longer registered.") + " " + path);
                destination = await repository.ValidateWorktreeOpenAsync(expected, Token);
            }
            Token.ThrowIfCancellationRequested();
        }, refresh: false);
        // ShowRepositoryDialogAsync and confirmations hold MainWindow's busy scope. Switch only after it ends.
        if (destination is not null && _repository == repository && !_lifetime.IsCancellationRequested && IsVisible)
        { await OpenRepositoryAsync(destination); }
    }

    private Task DeleteWorktreePathAsync(string path)
        => RunWorktreeActionAsync("Delete worktree", (owner, repository, token) => WorktreeDialogControls.DeleteAsync(owner, repository, path, token));

    private Task PruneWorktreesAsync()
        => RunWorktreeActionAsync("Prune worktrees", WorktreeDialogControls.PruneAsync);

    private async Task RunWorktreeActionAsync(string label, Func<Window, GitRepository, CancellationToken, Task<bool>> action)
    {
        if (_repository is not GitRepository repository || _busy) { return; }
        CloseWorktreeMenus(); bool attempted = false;
        await RunAsync(label, async _ =>
        {
            attempted = await action(WorkflowOwner, repository, Token);
            Token.ThrowIfCancellationRequested();
        }, refresh: false);
        if (attempted && _repository == repository && !_lifetime.IsCancellationRequested && IsVisible)
        {
            Exception? failure = LastException; string? status = _state.Text;
            await RefreshAsync();
            if (failure is not null) { LastException = failure; _state.Text = status; }
        }
    }

    private Control BuildWorktreeButton()
    {
        _worktreeButton = ToolbarButton("BrowseWorktrees", "Manage worktrees...", "WorkTree.png", () => ManageWorktreesAsync());
        _worktreeDropdown = Button("Worktrees", () => Task.CompletedTask);
        _worktreeDropdown.Name = "BrowseWorktreesDropdown";
        _worktreeDropdown.Content = ToolbarDropdownArrow(_worktreeDropdown, "BrowseWorktreesDropdownArrow");
        _worktreeDropdown.Width = 10; _worktreeDropdown.Height = _worktreeDropdown.MinHeight = DesktopFontMetrics.ToolbarHeight; _worktreeDropdown.MinWidth = 0;
        _worktreeDropdown.Padding = default; _worktreeDropdown.BorderThickness = default; _worktreeDropdown.Background = Brushes.Transparent;
        ToolTip.SetTip(_worktreeDropdown, T("Worktrees"));
        _worktreeFlyout.ItemsSource = CreateWorktreeMenu("WorktreeMenu", _navigationWorktrees);
        _worktreeDropdown.Flyout = _worktreeFlyout; ConfigureBrowserFlyout(_worktreeFlyout, _worktreeDropdown);
        _worktreeFlyout.Opened += async (_, _) => await ReadWorktreeMenuAsync("WorktreeMenu", items => _worktreeFlyout.ItemsSource = items, () => _worktreeFlyout.IsOpen);
        _worktreeFlyout.Closed += (_, _) => _worktreeMenuRead.Cancel();
        Grid split = new() { Name = "BrowseWorktreesControl", ColumnDefinitions = new("24,10"), Width = 34, Height = DesktopFontMetrics.ToolbarHeight };
        _worktreeControl = split;
        split.Children.Add(_worktreeButton); Grid.SetColumn(_worktreeDropdown, 1); split.Children.Add(_worktreeDropdown);
        RefreshWorktreeMenu(); return split;
    }

    private MenuItem BuildWorktreeOverflow()
    {
        MenuItem overflow = new() { Name = "WorktreeOverflow", Header = T("Worktrees"), Icon = OriginalIconImage("WorkTree.png"),
            ItemsSource = CreateWorktreeMenu("WorktreeOverflow", _navigationWorktrees) };
        _worktreeOverflow = overflow;
        overflow.SubmenuOpened += async (_, args) =>
        {
            if (args.Source == overflow)
            { await ReadWorktreeMenuAsync("WorktreeOverflow", items => overflow.ItemsSource = items, () => overflow.IsSubMenuOpen); }
        };
        overflow.PropertyChanged += (_, change) =>
        { if (change.Property == MenuItem.IsSubMenuOpenProperty && !overflow.IsSubMenuOpen) { _worktreeMenuRead.Cancel(); } };
        return overflow;
    }

    private Control[] CreateWorktreeMenu(string prefix, IReadOnlyList<GitWorktree> worktrees)
    {
        List<Control> items = [];
        for (int i = 0; i < worktrees.Count; i++)
        {
            GitWorktree worktree = worktrees[i];
            MenuItem item = WorktreeMenuAction(prefix + "Open" + i, "", WorktreeDisplayName(worktree,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(worktree.Path))), () => OpenWorktreePathAsync(worktree.Path, confirm: false), "WorkTree.png", literal: true);
            item.Tag = worktree.Path; item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = IsCurrentWorktreePath(worktree.Path);
            item.IsEnabled = !_busy && _repository is not null && !item.IsChecked && Directory.Exists(worktree.Path);
            if (!Directory.Exists(worktree.Path)) { item.Opacity = .6; }
            ToolTip.SetTip(item, worktree.Path); items.Add(item);
        }
        if (items.Count != 0) { items.Add(new Separator()); }
        items.Add(WorktreeMenuAction(prefix + "Create", "_createWorktree", "Create worktree...", CreateWorktreeAsync, "WorkTree.png", "TranslatedStrings"));
        items.Add(WorktreeMenuAction(prefix + "Prune", "_pruneWorktrees", "Prune worktrees", PruneWorktreesAsync, category: "TranslatedStrings"));
        items.Add(WorktreeMenuAction(prefix + "Manage", "_manageWorktrees", "Manage worktrees...", () => ManageWorktreesAsync(), category: "TranslatedStrings"));
        return items.ToArray();
    }

    private Task ReadWorktreeMenuAsync(string prefix, Action<Control[]> publish, Func<bool> isOpen)
        => ReadBrowserAsync(_worktreeMenuRead, "Worktrees", async (repository, token) =>
        {
            IReadOnlyList<GitWorktree> values = await repository.GetWorktreesAsync(token);
            token.ThrowIfCancellationRequested();
            if (_repository == repository && isOpen()) { PublishWorktreeMenu(prefix, values, publish); }
        });

    private void PublishWorktreeMenu(string prefix, IReadOnlyList<GitWorktree> values, Action<Control[]> publish)
    {
        bool overflow = prefix == "WorktreeOverflow";
        IReadOnlyList<GitWorktree> previous = overflow ? _worktreeOverflowValues : _worktreeFlyoutValues;
        GitRepository? previousRepository = overflow ? _worktreeOverflowRepository : _worktreeFlyoutRepository;
        // Do not replace focused menu items when the native read confirms the displayed list.
        if (previousRepository == _repository && previous.SequenceEqual(values)) { return; }
        IEnumerable<MenuItem> previousItems = overflow ? _worktreeOverflow?.Items.OfType<MenuItem>() ?? [] : _worktreeFlyout.Items.OfType<MenuItem>();
        MenuItem? focused = previousItems.FirstOrDefault(item => item.IsKeyboardFocusWithin);
        if (overflow) { _worktreeOverflowValues = values.ToArray(); _worktreeOverflowRepository = _repository; }
        else { _worktreeFlyoutValues = values.ToArray(); _worktreeFlyoutRepository = _repository; }
        Control[] items = CreateWorktreeMenu(prefix, values); publish(items);
        if (focused is not null)
        {
            MenuItem? replacement = items.OfType<MenuItem>().FirstOrDefault(item => focused.Tag is string path ? item.Tag as string == path : item.Name == focused.Name);
            Dispatcher.UIThread.Post(() =>
            {
                if ((overflow ? _worktreeOverflow?.IsSubMenuOpen == true : _worktreeFlyout.IsOpen) && replacement?.IsEnabled == true) { replacement.Focus(); }
            }, DispatcherPriority.Background);
        }
    }

    private void RefreshWorktreeMenu()
    {
        bool available = !_busy && _repository is not null;
        if (_worktreeControl is not null) { _worktreeControl.IsVisible = _repository is not null && _navigationWorktrees.Count > 1; }
        if (_worktreeButton is not null) { _worktreeButton.IsEnabled = available; }
        if (_worktreeDropdown is not null) { _worktreeDropdown.IsEnabled = available; }
        if (_worktreeOverflow is not null) { _worktreeOverflow.IsEnabled = available; }
        if (_manageWorktreesMenu is not null) { _manageWorktreesMenu.IsEnabled = available; }
        if (!available) { CloseWorktreeMenus(); }
        else
        {
            PublishWorktreeMenu("WorktreeMenu", _navigationWorktrees, items => _worktreeFlyout.ItemsSource = items);
            if (_worktreeOverflow is MenuItem overflow) { PublishWorktreeMenu("WorktreeOverflow", _navigationWorktrees, items => overflow.ItemsSource = items); }
        }
    }

    private void CloseWorktreeMenus()
    {
        _worktreeFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide(); _worktreeMenuRead.Cancel();
        foreach (TreeViewItem node in NavigationItems()) { node.ContextMenu?.Close(); }
    }

    private MenuItem WorktreeMenuAction(string name, string key, string caption, Func<Task> action, string? icon = null,
        string category = "RepoObjectsTree", bool literal = false)
    {
        MenuItem item = new() { Name = name, Header = literal ? caption : new AccessText
            { Text = _translations.TranslateAccessKeys(category, key, "Text", caption) },
            Icon = icon is null ? null : OriginalIconImage(icon), IsEnabled = !_busy && _repository is not null };
        if (key is "_manageWorktrees" or "mnubtnManageWorktreesFromRootNode")
        { DesktopHotkeys.Bind(item, "Browse", "ManageWorkTrees"); }
        item.AddHandler(KeyDownEvent, HandleShortcuts, RoutingStrategies.Tunnel);
        item.Click += async (_, args) =>
        {
            if (args.Source != item || !item.IsEnabled || _busy || _repository is null) { return; }
            try { await action(); }
            catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
        };
        return item;
    }

    private ContextMenu BuildWorktreeRootMenu()
    {
        ContextMenu menu = new() { Name = "WorktreeRootContextMenu", ItemsSource = new[]
        {
            WorktreeMenuAction("mnubtnCreateWorktreeFromRootNode", "mnubtnCreateWorktreeFromRootNode", "&Create worktree...", CreateWorktreeAsync),
            WorktreeMenuAction("mnubtnPruneWorktreesFromRootNode", "mnubtnPruneWorktreesFromRootNode", "&Prune worktrees", PruneWorktreesAsync),
            WorktreeMenuAction("mnubtnManageWorktreesFromRootNode", "mnubtnManageWorktreesFromRootNode", "&Manage worktrees...", () => ManageWorktreesAsync())
        } };
        void Availability()
        { foreach (MenuItem item in menu.Items.OfType<MenuItem>()) { item.IsEnabled = !_busy && _repository is not null; } }
        menu.Opening += (_, _) => Availability();
        // Avalonia's Open(control) bypasses Opening, but raises Opened synchronously.
        menu.Opened += (_, _) => Availability();
        Availability();
        return menu;
    }

    private ContextMenu BuildWorktreeNodeMenu(GitWorktree worktree)
    {
        MenuItem open = WorktreeMenuAction("mnubtnOpenWorktree", "mnubtnOpenWorktree", "&Open worktree", () => OpenWorktreePathAsync(worktree.Path, confirm: true), "FolderOpen.png");
        MenuItem delete = WorktreeMenuAction("mnubtnDeleteWorktree", "mnubtnDeleteWorktree", "&Delete worktree...", () => DeleteWorktreePathAsync(worktree.Path));
        ContextMenu menu = new() { Name = "WorktreeNodeContextMenu", ItemsSource = new[] { open, delete } };
        void Availability()
        {
            bool available = !_busy && _repository is not null && Directory.Exists(worktree.Path) && !IsCurrentWorktreePath(worktree.Path);
            open.IsEnabled = available;
            delete.IsEnabled = available && !SameWorktreePath(_navigationWorktrees.FirstOrDefault()?.Path, worktree.Path);
        }
        menu.Opening += (_, _) => Availability();
        menu.Opened += (_, _) => Availability();
        Availability();
        return menu;
    }

    private bool IsCurrentWorktreePath(string path) => SameWorktreePath(_repository?.Path, path);
    private static bool SameWorktreePath(string? left, string right)
        => left is not null && string.Equals(WorktreePathIdentity(left), WorktreePathIdentity(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string WorktreePathIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            string root = Path.GetPathRoot(fullPath)!;
            string current = root;
            foreach (string part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                DirectoryInfo directory = new(current);
                if (directory.LinkTarget is not null && directory.ResolveLinkTarget(returnFinalTarget: true) is FileSystemInfo target) { current = target.FullName; }
            }
            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Path.TrimEndingDirectorySeparator(fullPath); }
    }

    private static string WorktreeDisplayName(GitWorktree worktree, string name)
        => worktree.IsBare ? name + " (bare)" : worktree.IsDetached ? name + " (detached at " + (worktree.Head is { Length: >= 7 } head ? head[..7] : worktree.Head ?? "???") + ")"
            : worktree.Branch is string branch ? name + " (" + (branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch) + ")" : name;

    private static string CommonWorktreePathPrefix(string[] paths)
    {
        if (paths.Length < 2) { return ""; }
        string prefix = paths[0];
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string path in paths.Skip(1))
        {
            int length = Math.Min(prefix.Length, path.Length);
            while (length != 0 && !path.StartsWith(prefix[..length], comparison)) { length--; }
            prefix = prefix[..length];
        }
        int separator = prefix.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separator >= 0) { return prefix[..(separator + 1)]; }
        if (paths.Any(path => path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)) { return ""; }
        int boundary = prefix.LastIndexOfAny(['_', '-', '.', ' ']);
        return boundary < 0 ? "" : prefix[..(boundary + 1)];
    }
}
