using GitExtensions.Linux.Scripts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly PullPreferencesStore _pullPreferencesStore = new();
    private PullPreferences _pullPreferences = new();
    private readonly List<(MenuItem Item, GitPullAction Action, bool Default)> _pullMenuItems = [];
    // The original split menu begins at the primary icon's left edge. Its
    // target is the ten-pixel arrow immediately after the 24-pixel button.
    private readonly MenuFlyout _pullFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, HorizontalOffset = -24 };
    private Button? _pullButton, _pullDropdown;
    private GitPullAction? _shownPullAction;

    private Control BuildPullButton()
    {
        _pullPreferences = _pullPreferencesStore.Load();
        _pullButton = ToolbarButton("BrowsePull", "Pull", "PullMerge.png", DefaultPullAsync);
        _pullDropdown = Button("Pull/Fetch…", () => Task.CompletedTask);
        _pullDropdown.Name = "BrowsePullDropdown";
        _pullDropdown.Content = ToolbarDropdownArrow(_pullDropdown, "BrowsePullDropdownArrow");
        _pullDropdown.Width = 10; _pullDropdown.Height = _pullDropdown.MinHeight = 24;
        _pullDropdown.MinWidth = 0; _pullDropdown.Padding = default;
        _pullDropdown.BorderThickness = default; _pullDropdown.Background = Brushes.Transparent;
        ToolTip.SetTip(_pullDropdown, T("Pull/Fetch…"));
        _pullFlyout.ItemsSource = CreatePullMenu("PullMenu");
        _pullDropdown.Flyout = _pullFlyout;
        ConfigureBrowserFlyout(_pullFlyout, _pullDropdown);
        _pullFlyout.Opened += (_, _) => RefreshPullMenu();
        Grid split = new() { Name = "BrowsePullControl", ColumnDefinitions = new("24,10"), Width = 34, Height = 24 };
        split.Children.Add(_pullButton); Grid.SetColumn(_pullDropdown, 1); split.Children.Add(_pullDropdown);
        RefreshPullMenu(); return split;
    }

    private Control[] CreatePullMenu(string prefix)
    {
        GitPullAction[] actions = [GitPullAction.None, GitPullAction.Merge, GitPullAction.Rebase, GitPullAction.Fetch, GitPullAction.FetchAll, GitPullAction.FetchPruneAll];
        MenuItem Item(GitPullAction action, bool asDefault)
        {
            (string key, string source, string icon) = PullDescription(action);
            MenuItem item = new()
            {
                Name = prefix + (asDefault ? "Default" : "") + action,
                Header = new AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", key, "Text", source) },
                Icon = OriginalIconImage(icon),
                Tag = !asDefault && action is GitPullAction.Merge or GitPullAction.Rebase,
                StaysOpenOnClick = asDefault,
                ToggleType = asDefault ? MenuItemToggleType.CheckBox : MenuItemToggleType.None
            };
            if (!asDefault && action == GitPullAction.None) { DesktopHotkeys.Bind(item, "Browse", "PullOrFetch"); }
            _actionMenus.Add(item); _pullMenuItems.Add((item, action, asDefault));
            // Popup roots are outside the Window's input route. Preserve the
            // original global pull gestures while this menu has keyboard focus.
            item.AddHandler(KeyDownEvent, HandleShortcuts, RoutingStrategies.Tunnel);
            item.Click += async (_, _) =>
            {
                try
                {
                    if (asDefault)
                    {
                        PullPreferences next = _pullPreferencesStore.Load() with { DefaultAction = action };
                        _pullPreferencesStore.Save(next); _pullPreferences = next; RefreshPullMenu();
                    }
                    else
                    {
                        _pullFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide();
                        await ExecutePullActionAsync(action);
                    }
                }
                catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; RefreshPullMenu(); }
            };
            return item;
        }
        MenuItem defaults = new()
        {
            Name = prefix + "SetDefault",
            Header = new AccessText { Text = _translations.TranslateAccessKeys("FormBrowse", "setDefaultPullButtonActionToolStripMenuItem", "Text", "Set &default Pull button action") },
            ItemsSource = actions.Select(action => Item(action, true)).ToArray()
        };
        defaults.AddHandler(KeyDownEvent, HandleShortcuts, RoutingStrategies.Tunnel);
        return [Item(GitPullAction.None, false), new Separator(), ..actions.Skip(1).Select(action => Item(action, false)), new Separator(), defaults];
    }

    private static (string Key, string Source, string Icon) PullDescription(GitPullAction action) => action switch
    {
        GitPullAction.Merge => ("mergeToolStripMenuItem", "Pull - &merge", "PullMerge.png"),
        GitPullAction.Rebase => ("rebaseToolStripMenuItem1", "Pull - &rebase", "PullRebase.png"),
        GitPullAction.Fetch => ("fetchToolStripMenuItem", "&Fetch", "PullFetch.png"),
        GitPullAction.FetchAll => ("fetchAllToolStripMenuItem", "Fetch &all", "PullFetchAll.png"),
        GitPullAction.FetchPruneAll => ("fetchPruneAllToolStripMenuItem", "Fetch and prune all", "PullFetchPruneAll.png"),
        _ => ("pullToolStripMenuItem1", "Open &pull dialog…", "Pull.png")
    };

    private void RefreshPullMenu()
    {
        if (_pullButton is null || _pullDropdown is null) { return; }
        bool available = !_busy && _repository is not null;
        bool needsWorktree = _pullPreferences.DefaultAction is GitPullAction.Merge or GitPullAction.Rebase;
        _pullButton.Tag = needsWorktree;
        _pullButton.IsEnabled = available && (!needsWorktree || !_repository!.IsBare);
        _pullDropdown.IsEnabled = available;
        if (_shownPullAction != _pullPreferences.DefaultAction)
        {
            (string key, string label, string icon) = PullDescription(_pullPreferences.DefaultAction);
            _pullButton.Content = OriginalIconImage(icon);
            DesktopHotkeys.BindToolTip(_pullButton, "Browse", "QuickPullOrFetch", _translations.Translate("FormBrowse", key, "Text", label).Replace("&", "", StringComparison.Ordinal));
            _shownPullAction = _pullPreferences.DefaultAction;
        }
        int remotes = _remotes.Items.OfType<GitRemote>().Count();
        foreach ((MenuItem item, GitPullAction action, bool asDefault) in _pullMenuItems)
        {
            item.IsVisible = action != GitPullAction.FetchAll || remotes > 1;
            item.IsEnabled = !_busy && (asDefault || available && (action is not (GitPullAction.Merge or GitPullAction.Rebase) || !_repository!.IsBare));
            if (asDefault) { item.IsChecked = action == _pullPreferences.DefaultAction; }
        }
    }

    private Task PullAsync() => ExecutePullActionAsync(GitPullAction.None);

    private Task DefaultPullAsync()
    {
        _pullPreferences = _pullPreferencesStore.Load();
        RefreshPullMenu(); return ExecutePullActionAsync(_pullPreferences.DefaultAction);
    }

    private async Task ExecutePullActionAsync(GitPullAction action)
    {
        if (_repository is null || _busy) { return; }
        if (_repository.IsBare && action is GitPullAction.Merge or GitPullAction.Rebase)
        { _state.Text = T("This operation requires a working tree."); return; }
        _pullFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide();
        _pullPreferences = _pullPreferencesStore.Load();
        GitRepository repository = _repository;
        PullDialog? dialog = null;
        dialog = new(repository, action == GitPullAction.None ? _pullPreferences.DialogAction : action,
            afterSubmoduleUpdateAsync: token => AfterSubmoduleUpdateAsync(repository, dialog!.IsVisible ? dialog : WorkflowOwner, token),
            resolveConflictsAsync: ResolveWorkflowConflictsAsync)
            { RequestedThemeVariant = WorkflowOwner.ActualThemeVariant };
        try
        {
            await RunAsync("Pull/Fetch", async _ =>
            {
                CancellationToken commandToken = Token;
                await dialog.InitializeAsync(commandToken);
                commandToken.ThrowIfCancellationRequested();
                if (action != GitPullAction.None && dialog.IsReady)
                { await dialog.ExecuteAsync(WorkflowOwner, action == GitPullAction.FetchPruneAll, commandToken); }
                commandToken.ThrowIfCancellationRequested();
                if (action == GitPullAction.None || !dialog.Succeeded && !dialog.Cancelled)
                {
                    using CancellationTokenRegistration close = commandToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
                    await dialog.ShowDialog(WorkflowOwner);
                    commandToken.ThrowIfCancellationRequested();
                }
            }, refresh: false);
        }
        finally
        {
            dialog.Close(); if (dialog.Attempted || dialog.ScriptMayHaveChangedRepository) { AcceptStandaloneRepositoryMutationAttempt(repository); }
            if ((dialog.Attempted || dialog.ScriptMayHaveChangedRepository) && _repository is not null && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
            _pullPreferences = _pullPreferencesStore.Load(); RefreshPullMenu();
        }
    }

    private Task FetchAllWithScriptEventsAsync() => RunAsync("Fetch all", async repository =>
    {
        using ScriptWorkflowScope scripts = new(WorkflowOwner, repository, Token);
        if (!await scripts.RunAsync(ScriptEvent.BeforeFetch)) { return; }
        scripts.ThrowIfStopped();
        GitResult result = await repository.FetchAllAsync(scripts.Token);
        AppendOutput(result.StandardOutput + result.StandardError);
        scripts.ThrowIfStopped();
        await scripts.RunAsync(ScriptEvent.AfterFetch);
    });

    private Task QuickFetchAsync() => RunAsync("Fetch", async repository =>
    {
        bool attempted = false;
        Exception? operationError = null;
        Window owner = WorkflowOwner;
        using ScriptWorkflowScope scripts = new(owner, repository, Token);
        try
        {
            if (!await scripts.RunAsync(ScriptEvent.BeforeFetch)) { return; }
            scripts.ThrowIfStopped();
            System.Collections.Concurrent.ConcurrentQueue<GitProcessOutput> chunks = new();
            GitProcessDialogResult process = await GitProcessDialog.ShowAsync(owner, repository.Path, "git fetch --progress", true,
                (token, output) =>
                {
                    token.ThrowIfCancellationRequested(); attempted = true;
                    return repository.FetchConfiguredRemoteAsync(token, chunk => { chunks.Enqueue(chunk); output(chunk); });
                }, Token);
            if (process.Result is GitResult result) { AppendOutput(result.StandardOutput + result.StandardError); }
            else
            {
                GitProcessTextDecoder stdout = new(), stderr = new(); System.Text.StringBuilder text = new();
                while (chunks.TryDequeue(out GitProcessOutput? chunk))
                { text.Append((chunk.Stream == GitProcessOutputStream.StandardOutput ? stdout : stderr).Append(chunk.Bytes.Span)); }
                text.Append(stdout.Append([], flush: true)).Append(stderr.Append([], flush: true));
                if (text.Length > 0) { AppendOutput(text.ToString()); }
            }
            Token.ThrowIfCancellationRequested();
            if (process.WasCancelled || process.OwnerCloseRequested || !owner.IsVisible) { throw new OperationCanceledException(Token); }
            if (process.Error is Exception error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
            scripts.ThrowIfStopped();
            if (process.Success) { await scripts.RunAsync(ScriptEvent.AfterFetch); }
        }
        catch (Exception error) { operationError = error; throw; }
        finally
        {
            // Refresh partial ref updates inside the original operation, before its error/status is reported.
            if ((attempted || scripts.MayHaveRunScripts) && owner.IsVisible && !_lifetime.IsCancellationRequested)
            {
                try { await RefreshDataAsync(repository); }
                catch when (operationError is not null) { /* Preserve the operation's native failure or interruption. */ }
            }
        }
    }, refresh: false);
}
