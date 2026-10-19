using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // RevisionGridMenuCommands feeds both FormBrowseMenus and the grid context menu.
    // Keep a single command definition, including state and gestures, for all hosts.
    private sealed record HistoryCommand(string Name, string Text, Func<Task>? Execute = null, string? Icon = null,
        KeyGesture? Gesture = null, Func<bool>? Checked = null, Func<bool>? Available = null, bool Caption = false, string? UnavailableReason = null);
    private readonly List<(MenuItem Item, HistoryCommand Command)> _historyMenuBindings = [];
    private readonly HistoryViewPreferencesStore _historyViewStore = new();
    private HistoryViewPreferences _historyView = new();
    private int _artificialNavigation = 2;

    private HistoryCommand[] NavigateHistoryCommands() =>
    [
        new("ToggleBetweenArtificialAndHeadCommits", "&Toggle between artificial and HEAD commits", ToggleArtificialRevisionAsync, "WorkingDirChanges.png", new(Key.OemBackslash, KeyModifiers.Control), Available: () => _repository?.IsBare == false && _historyView.ShowArtificialCommits),
        new("GotoCurrentRevision", "Go to c&urrent revision", () => NavigateToRevisionAsync("HEAD"), "GotoCurrentRevision.png", new(Key.C, KeyModifiers.Control | KeyModifiers.Shift)),
        new("GotoCommit", "Go to &commit...", GoToRevisionAsync, "GotoCommit.png", new(Key.G, KeyModifiers.Control | KeyModifiers.Shift)),
        new("|", ""),
        new("GotoChildCommit", "Go to c&hild commit", () => MoveHistoryRelativeAsync(RelativeRevisionCommand.Child), "GoToChildCommit.png", new(Key.N, KeyModifiers.Control), Available: HasRevisionChild),
        new("GotoParentCommit", "Go to &parent commit", () => MoveHistoryRelativeAsync(RelativeRevisionCommand.Parent), "GoToParentCommit.png", new(Key.P, KeyModifiers.Control), Available: HasRevisionParent),
        new("GotoFirstParentCommit", "Go to f&irst parent commit", () => MoveHistoryRelativeAsync(RelativeRevisionCommand.FirstParent), "GoToFirstParentCommit.png", new(Key.Left, KeyModifiers.Control), Available: HasDirectRevisionParent),
        new("GotoLastParentCommit", "Go to &last parent commit", () => MoveHistoryRelativeAsync(RelativeRevisionCommand.LastParent), "GoToLastParentCommit.png", new(Key.Right, KeyModifiers.Control), Available: HasDirectRevisionParent),
        new("GotoMergeBaseCommit", "Go to common &ancestor (merge base)", NavigateToMergeBaseAsync, "GoToMergeBaseCommit.png", new(Key.K, KeyModifiers.Control | KeyModifiers.Shift), Available: () => SelectedComparisonCommits().Length > 0),
        new("|", ""),
        new("NavigateBackward", "Navigate &backward", () => NavigateRevisionHistoryAsync(-1), "NavigateBackward.png", new(Key.Left, KeyModifiers.Alt), Available: () => _revisionNavigationIndex > 0),
        new("NavigateForward", "Navigate &forward", () => NavigateRevisionHistoryAsync(1), "NavigateForward.png", new(Key.Right, KeyModifiers.Alt), Available: () => _revisionNavigationIndex >= 0 && _revisionNavigationIndex + 1 < _revisionNavigation.Count),
        new("|", ""),
        new("QuickSearch", "&Quick search", ShowQuickSearchHelpAsync),
        new("PrevQuickSearch", "Quick search p&revious", () => SearchRevisionAsync(-1), Gesture: new(Key.Up, KeyModifiers.Alt)),
        new("NextQuickSearch", "Quick search &next", () => SearchRevisionAsync(1), Gesture: new(Key.Down, KeyModifiers.Alt))
    ];

    private HistoryCommand[] ViewHistoryCommands()
    {
        HistoryCommand Toggle(string name, string label, Func<HistoryViewPreferences, bool> read, Func<HistoryViewPreferences, HistoryViewPreferences> change,
            bool reload = false, KeyGesture? gesture = null) => new(name, label, () => ChangeHistoryViewAsync(change, reload), Gesture: gesture, Checked: () => read(_historyView));
        HistoryCommand Missing(string name, string text, string reason) => new(name, text, Available: () => false, UnavailableReason: reason);
        return
        [
            new("BranchesToolStripMenuItem", "Branches", Caption: true),
            new("ShowAllBranches", "Show &all branches", () => SetHistoryBranchScopeAsync(HistoryBranchScope.All), "BranchLocal.png", new(Key.A, KeyModifiers.Control | KeyModifiers.Shift), () => _historyBranchScope == HistoryBranchScope.All),
            new("ShowCurrentBranchOnly", "Show &current branch only", () => SetHistoryBranchScopeAsync(HistoryBranchScope.Current), "BranchFilter.png", new(Key.U, KeyModifiers.Control | KeyModifiers.Shift), () => _historyBranchScope == HistoryBranchScope.Current),
            new("ShowFilteredBranches", "Show &filtered branches", () => SetHistoryBranchScopeAsync(HistoryBranchScope.Filtered), "BranchFilter.png", new(Key.T, KeyModifiers.Control | KeyModifiers.Shift), () => _historyBranchScope == HistoryBranchScope.Filtered),
            new("ShowReflogReferences", "Show &reflog references", () => ChangeHistoryViewAsync(p => p with { ShowReflog = !p.ShowReflog }, true), "Book.png", new(Key.L, KeyModifiers.Control | KeyModifiers.Shift), () => _historyView.ShowReflog),
            new("|", ""), new("filterToolStripMenuItem", "Advanced filter...", FilterHistoryAsync, "EditFilter.png", new(Key.I, KeyModifiers.Control)), new("|", ""),
            Toggle("drawNonrelativesGrayToolStripMenuItem", "Draw non relatives gra&y", p => p.DrawNonRelativesGray, p => p with { DrawNonRelativesGray = !p.DrawNonRelativesGray }),
            new("HighlightSelectedBranch", "Highlight selected branch (until refresh)", HighlightHistoryBranchAsync, Gesture: new(Key.B, KeyModifiers.Control | KeyModifiers.Shift), Available: () => _commits.SelectedItem is GitCommit),
            new("|", ""), new("CommitsToolStripMenuItem", "Commits", Caption: true),
            Toggle("ShowArtificialCommits", "Show artificial commits", p => p.ShowArtificialCommits, p => p with { ShowArtificialCommits = !p.ShowArtificialCommits }),
            Toggle("ShowStashes", "Show stashes", p => p.ShowStashes, p => p with { ShowStashes = !p.ShowStashes }, true),
            Toggle("showGitNotesToolStripMenuItem", "Show git &notes", p => p.ShowNotes, p => p with { ShowNotes = !p.ShowNotes }, true),
            Toggle("ShowSessionCheckpoints", "Show session checkpoints", p => p.ShowSessionCheckpoints, p => p with { ShowSessionCheckpoints = !p.ShowSessionCheckpoints }, true),
            new("|", ""), new("Grid_labelsToolStripMenuItem", "Grid labels", Caption: true),
            Toggle("ShowRemoteBranches", "Show remote &branches", p => p.ShowRemoteBranches, p => p with { ShowRemoteBranches = !p.ShowRemoteBranches }, gesture: new(Key.R, KeyModifiers.Control | KeyModifiers.Shift)),
            Toggle("showTagsToolStripMenuItem", "Show &tags", p => p.ShowTags, p => p with { ShowTags = !p.ShowTags }, gesture: new(Key.T, KeyModifiers.Control | KeyModifiers.Alt)),
            Missing("ShowSuperprojectTags", "Show su&perproject tags", "Superproject labels are not available in this alpha."),
            Missing("ShowSuperprojectRemoteBranches", "Show superpro&ject remote branches", "Superproject labels are not available in this alpha."),
            Missing("ShowSuperprojectBranches", "Show sup&erproject branches", "Superproject labels are not available in this alpha."),
            new("|", ""), new("Grid_infoToolStripMenuItem", "Grid info", Caption: true),
            Toggle("showBuildStatusIconToolStripMenuItem", "Show build status &icon", p => p.ShowBuildStatusIcon, p => p with { ShowBuildStatusIcon = !p.ShowBuildStatusIcon }),
            Toggle("showBuildStatusTextToolStripMenuItem", "Show build status te&xt", p => p.ShowBuildStatusText, p => p with { ShowBuildStatusText = !p.ShowBuildStatusText }),
            Toggle("showCommitMessageBodyToolStripMenuItem", "Show commit message body", p => p.ShowBody, p => p with { ShowBody = !p.ShowBody }),
            Toggle("showAuthorDateToolStripMenuItem", "Sho&w author date", p => p.ShowAuthorDate, p => p with { ShowAuthorDate = !p.ShowAuthorDate }, true),
            Toggle("showRelativeDateToolStripMenuItem", "Show relati&ve date", p => p.RelativeDate, p => p with { RelativeDate = !p.RelativeDate }),
            new("|", ""), new("ColumnsToolStripMenuItem", "Columns", Caption: true),
            Toggle("showRevisionGraphColumnToolStripMenuItem", "Show revision &graph column", p => p.ShowGraph, p => p with { ShowGraph = !p.ShowGraph }),
            Toggle("showGitNotesColumnToolStripMenuItem", "Show Git &notes column", p => p.ShowNotesColumn, p => p with { ShowNotesColumn = !p.ShowNotesColumn }, true),
            Toggle("showAuthorAvatarColumnToolStripMenuItem", "Show aut&hor avatar column", p => p.ShowAvatar, p => p with { ShowAvatar = !p.ShowAvatar }),
            Toggle("showAuthorNameColumnToolStripMenuItem", "Show a&uthor name column", p => p.ShowAuthor, p => p with { ShowAuthor = !p.ShowAuthor }),
            Toggle("showDateColumnToolStripMenuItem", "Show &date column", p => p.ShowDate, p => p with { ShowDate = !p.ShowDate }),
            Toggle("showIdColumnToolStripMenuItem", "Show SHA&-1 column", p => p.ShowId, p => p with { ShowId = !p.ShowId }),
            new("|", ""), new("SortingToolStripMenuItem", "Sorting", Caption: true),
            Toggle("AuthorDateSort", "&Sort commits by author date", p => p.Sort == GitLogSort.AuthorDate, p => p with { Sort = p.Sort == GitLogSort.AuthorDate ? GitLogSort.GitDefault : GitLogSort.AuthorDate }, true),
            Toggle("TopoOrder", "Arrange c&ommits by topo order (ancestor order)", p => p.Sort == GitLogSort.Topology, p => p with { Sort = p.Sort == GitLogSort.Topology ? GitLogSort.GitDefault : GitLogSort.Topology }, true),
            new("|", ""), new("Settings_persistenceToolStripMenuItem", "Settings persistence", Caption: true),
            new("SaveAsDefault", "Save current view settings as default", SaveCurrentViewSettingsAsync)
        ];
    }

    private object[] CreateHistoryMenuItems(IEnumerable<HistoryCommand> commands, string prefix)
        => commands.Select(command =>
        {
            if (command.Name == "|") { return (object)new Separator(); }
            MenuItem item = OriginalAction("RevisionGrid", prefix + command.Name, command.Text, command.Icon, async () =>
            {
                if (_busy || _repository is null || command.Available?.Invoke() == false || command.Execute is null) { return; }
                try { await command.Execute(); }
                finally { RefreshHistoryMenuStates(); }
            }, HistoryHotkeyNames.TryGetValue(command.Name, out string? hotkey) ? DesktopHotkeys.Get("RevisionGrid", hotkey) : command.Gesture);
            if (hotkey is not null) { DesktopHotkeys.Bind(item, "RevisionGrid", hotkey); }
            // The XLF key is the upstream command name; the host prefix only names controls.
            item.Header = new Avalonia.Controls.Primitives.AccessText { Text = _translations.TranslateAccessKeys("RevisionGrid", command.Name, "Text", command.Text) };
            if (prefix == "RevisionView" && command.Name == "SaveAsDefault") { item.Name = "SaveCurrentViewSettingsAsDefault"; }
            if (command.Checked is not null) { item.ToggleType = MenuItemToggleType.CheckBox; }
            if (command.Caption) { item.FontWeight = FontWeight.Bold; }
            if (command.Name == "QuickSearch") { ToolTip.SetTip(item, QuickSearchHelpText); }
            if (command.UnavailableReason is string reason) { ToolTip.SetTip(item, T(reason)); }
            _historyMenuBindings.Add((item, command));
            return item;
        }).ToArray();

    private MenuItem BuildHistoryNavigateMenu(bool context = false)
    {
        MenuItem menu = new() { Name = context ? "RevisionNavigate" : "NavigateMenu", Header = T("Navigate"),
            Icon = context ? OriginalIconImage("GotoCommit.png") : null, ItemsSource = CreateHistoryMenuItems(NavigateHistoryCommands(), context ? "RevisionNavigate" : "Navigate") };
        menu.SubmenuOpened += (_, args) => { if (args.Source == menu) { RefreshHistoryMenuStates(); } };
        menu.AddHandler(KeyDownEvent, HistoryMenuKeyDown, RoutingStrategies.Tunnel);
        RefreshHistoryMenuStates(); return menu;
    }

    private MenuItem BuildHistoryViewMenu(bool context = false)
    {
        object[] commands = CreateHistoryMenuItems(ViewHistoryCommands(), context ? "RevisionView" : "View");
        MenuItem menu = new() { Name = context ? "RevisionView" : "ViewMenu", Header = T("View"),
            Icon = context ? OriginalIconImage("AdvancedSettings.png") : null,
            ItemsSource = context ? commands : [.. commands, new Separator(), BuildHistoryToolbarsMenu()] };
        menu.SubmenuOpened += (_, args) => { if (args.Source == menu) { RefreshHistoryMenuStates(); } };
        menu.AddHandler(KeyDownEvent, HistoryMenuKeyDown, RoutingStrategies.Tunnel);
        RefreshHistoryMenuStates(); return menu;
    }

    private void RefreshHistoryMenuStates()
    {
        foreach ((MenuItem item, HistoryCommand command) in _historyMenuBindings)
        {
            item.IsEnabled = !command.Caption && !_busy && _repository is not null && command.Execute is not null && command.Available?.Invoke() != false;
            if (command.Checked is not null) { item.IsChecked = command.Checked(); }
        }
    }

    private async Task<bool> HandleHistoryMenuShortcutAsync(KeyEventArgs args, Action? beforeExecute = null)
    {
        return await ExecuteConfiguredHotkeyAsync("RevisionGrid", args, beforeExecute);
    }

    private async void HistoryMenuKeyDown(object? sender, KeyEventArgs args)
    {
        if (_busy || args.Handled || _repository is null) { return; }
        async Task ExecuteAsync() { await HandleConfiguredShortcutsAsync(sender, args, allowAlt: true); }
        if (sender is MenuItem source) { await WithMenuActionFocusAsync(source, ExecuteAsync); }
        else { await ExecuteAsync(); }
    }

    private async void HistoryAltKeyDown(object? sender, KeyEventArgs args)
    {
        // Let the owner's AccessKeyHandler observe the chord in its preview
        // handler first. Handling it at the window tunnel suppresses that
        // bookkeeping and Alt-up subsequently opens the main menu.
        if (!_busy && !args.Handled && args.KeyModifiers == KeyModifiers.Alt)
        { await HandleConfiguredShortcutsAsync(sender, args, allowAlt: true); }
    }

    private Task ToggleArtificialRevisionAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _artificialNavigation = (_artificialNavigation + 1) % 3;
            Button? button = _artificialNavigation == 0 ? _workingRevision : _indexRevision;
            if (_artificialNavigation == 2)
            {
                if (_historyGraph.CurrentHead is string head) { SelectVisibleRevision(head); _commits.Focus(); }
                return Task.CompletedTask;
            }
            if (button is not null && (_artificialNavigation == 0 ? _workingRevisionState?.Text : _indexRevisionState?.Text) != "✓")
            { button.Focus(); button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); return Task.CompletedTask; }
        }
        return Task.CompletedTask;
    }
}
