using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly MenuFlyout _stashFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, HorizontalOffset = -24 };
    private readonly List<MenuItem> _stashMenuItems = [];
    private Button? _stashButton, _stashDropdown;
    private bool? _supportsStashStaged;

    private Control BuildStashButton()
    {
        _stashButton = ToolbarButton("BrowseStash", "Manage stashes…", "stash.png", () => ShowStashDialogAsync());
        _stashButton.Tag = true;
        _stashDropdown = Button("Stash", () => Task.CompletedTask);
        _stashDropdown.Name = "BrowseStashDropdown"; _stashDropdown.Content = ToolbarDropdownArrow(_stashDropdown, "BrowseStashDropdownArrow");
        _stashDropdown.Width = 10; _stashDropdown.Height = _stashDropdown.MinHeight = 24; _stashDropdown.MinWidth = 0;
        _stashDropdown.Padding = default; _stashDropdown.BorderThickness = default; _stashDropdown.Background = Brushes.Transparent;
        ToolTip.SetTip(_stashDropdown, T("Manage stashes…"));
        _stashFlyout.ItemsSource = CreateStashMenu("StashMenu"); _stashDropdown.Flyout = _stashFlyout;
        ConfigureBrowserFlyout(_stashFlyout, _stashDropdown); _stashFlyout.Opened += (_, _) => RefreshStashMenu();
        Grid split = new() { Name = "BrowseStashControl", ColumnDefinitions = new("24,10"), Width = 34, Height = 24 };
        split.Children.Add(_stashButton); Grid.SetColumn(_stashDropdown, 1); split.Children.Add(_stashDropdown); return split;
    }

    private Control[] CreateStashMenu(string prefix)
    {
        MenuItem Item(string suffix, string key, string text, Func<Task> action, KeyGesture? gesture = null)
        {
            MenuItem item = new() { Name = prefix + suffix, Tag = true,
                Header = new AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", key, "Text", text) }, InputGesture = gesture };
            _actionMenus.Add(item); _stashMenuItems.Add(item);
            item.AddHandler(KeyDownEvent, HandleShortcuts, RoutingStrategies.Tunnel);
            item.Click += async (_, _) =>
            {
                _stashFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide();
                try { await action(); } catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
            };
            return item;
        }
        return [
            Item("Save", "stashChangesToolStripMenuItem", "&Stash", QuickStashAsync, new(Key.Up, KeyModifiers.Control | KeyModifiers.Alt)),
            Item("Staged", "stashStagedToolStripMenuItem", "S&tash staged", QuickStashStagedAsync, new(Key.Up, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)),
            Item("Pop", "stashPopToolStripMenuItem", "Stash &pop", QuickStashPopAsync, new(Key.Down, KeyModifiers.Control | KeyModifiers.Alt)),
            new Separator(),
            Item("Manage", "manageStashesToolStripMenuItem", "&Manage stashes…", () => ShowStashDialogAsync()),
            Item("Create", "createAStashToolStripMenuItem", "&Create a stash…", () => ShowStashDialogAsync(false))
        ];
    }

    private void RefreshStashMenu()
    {
        bool available = !_busy && _repository is { IsBare: false };
        if (_stashButton is not null) { _stashButton.IsEnabled = available; }
        if (_stashDropdown is not null) { _stashDropdown.IsEnabled = available; }
        foreach (MenuItem item in _stashMenuItems)
        {
            item.IsEnabled = available;
            if (item.Name?.EndsWith("Staged", StringComparison.Ordinal) == true) { item.IsVisible = _supportsStashStaged == true; }
        }
    }

    private Task StashAsync() => ShowStashDialogAsync(false);
    private Task ShowStashDialogAsync(bool manage = true, GitStash? initialStash = null)
        => _repository is null || _busy || _repository.IsBare ? Task.CompletedTask : ShowRepositoryDialogAsync(new StashDialog(_repository, manage, initialStash));

    private Task QuickStashAsync() => RunQuickStashAsync("Stash", r => r.StashAsync(new GitStashSaveOptions(IncludeUntracked: new StashPreferencesStore().Load().IncludeUntracked), Token));
    private Task QuickStashStagedAsync() => RunQuickStashAsync("Stash staged", r => r.StashAsync(new GitStashSaveOptions(StagedOnly: true), Token));
    private Task QuickStashPopAsync() => RunQuickStashAsync("Stash pop", r => r.PopLatestStashAsync(Token));

    private async Task RunQuickStashAsync(string label, Func<GitRepository, Task<GitResult>> action)
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        _stashFlyout.Hide(); _pullFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide();
        await RunAsync(label, async repository =>
        {
            GitResult result = await action(repository); AppendOutput(result.StandardOutput + result.StandardError);
        }, refresh: false);
        Exception? error = LastException;
        await RefreshAsync();
        if (error is not null)
        {
            if (_repository is not null && (await _repository.GetStatusAsync(_lifetime.Token)).Changes.Any(change => change.IsConflict))
            { await ShowStashDialogAsync(false); }
            else { await Dialogs.ShowErrorAsync(WorkflowOwner, error.Message); }
        }
    }
}
