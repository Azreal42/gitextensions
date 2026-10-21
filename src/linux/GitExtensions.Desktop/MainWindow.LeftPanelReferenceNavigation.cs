using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private KeyModifiers _leftPanelReferenceModifiers;

    private void InitializeLeftPanelReferenceNavigation()
    {
        // The owner access-key preview has already seen Alt before the tree
        // tunnel. Dispatch configured history chords here: TreeView otherwise
        // consumes Alt+Left/Right as ordinary collapse/expand navigation.
        _navigation.AddHandler(KeyDownEvent, HistoryAltKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _leftPanelReferenceModifiers = KeyModifiers.None,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, args) =>
        {
            _leftPanelReferenceModifiers = _navigation.IsKeyboardFocusWithin ? args.KeyModifiers : KeyModifiers.None;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, _) => _leftPanelReferenceModifiers = KeyModifiers.None,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _navigation.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _updatingNavigation || _busy || _lifetime.IsCancellationRequested
                || _navigation.SelectedItem is not TreeViewItem item) { return; }
            bool toggleSelection = _leftPanelReferenceModifiers.HasFlag(KeyModifiers.Control);
            _goToCommitRead.Cancel();
            if (item.Tag is GitReference reference)
            {
                _references.SelectedItem = reference;
                // No global refresh: the native second click remains available.
                await ReadBrowserAsync(_goToCommitRead, "Go to commit", async (repository, token) =>
                {
                    object? selected = _commits.SelectedItem;
                    string selection = RevisionSelectionIdentity;
                    string hash = await repository.ResolveRevisionAsync(reference.Name, token);
                    token.ThrowIfCancellationRequested();
                    if (_repository != repository || _navigation.SelectedItem != item || _busy
                        || !ReferenceEquals(_commits.SelectedItem, selected) || RevisionSelectionIdentity != selection
                        || _refreshing || _updatingNavigation || IsBranchFilterOwnerClosing) { return; }
                    await SelectLeftPanelReferenceAsync(hash, toggleSelection);
                });
            }
            else if (item.Tag is GitWorktree { Head: { } head }) { await SelectLeftPanelReferenceAsync(head, toggleSelection); }
            else if (item.Tag is string workflow) { await ShowWorkflowAsync(workflow); }
        };
    }

    private Task SelectLeftPanelReferenceAsync(string hash, bool toggleSelection)
    {
        // Ordinary Browse represents these endpoints outside the real-row ListBox.
        // Preserve the entire comparison until the shared typed-selection contract
        // can represent its Ctrl extension; never silently discard an endpoint.
        if (toggleSelection && (UsesArtificialInitialComparison || _selectedArtificialHistoryRevision is not null
            || _quickSearchArtificialSelection is not null && QuickSearchArtificialCount > 0))
        {
            _state.Text = T("The working directory or index selection cannot be extended here.");
            return Task.CompletedTask;
        }
        GitCommit? target = _commits.Items.OfType<GitCommit>().FirstOrDefault(commit => commit.Hash == hash);
        if (target is null)
        {
            _state.Text = T("The revision is not visible with the current history filters.") + " " + hash;
            return Task.CompletedTask;
        }
        Dictionary<string, GitCommit> selected = (_commits.SelectedItems?.OfType<GitCommit>() ?? [])
            .ToDictionary(commit => commit.Hash, StringComparer.Ordinal);
        // DataGridView.SelectedRows enumerates the selected-band linked list,
        // most recently added first; ListBox.SelectedItem alone is not that order.
        // Startup requests explicitly select First then Selected on Windows;
        // SelectedRows therefore exposes [Selected, First], independently of
        // the retained Linux ListBox order left by RefreshAsync.
        string[] order = _browseInitialComparison is { } pair
            ? new[] { pair.Selected.Revision, pair.First.Revision }.Distinct(StringComparer.Ordinal).ToArray()
            : _historySelectionOrder.AsEnumerable().Reverse().ToArray();
        GitCommit[] before = order.Where(selected.ContainsKey).Select(key => selected[key])
            .Concat(selected.Values.Where(commit => !order.Contains(commit.Hash, StringComparer.Ordinal))).ToArray();
        GitCommit[] after = !toggleSelection ? [target]
            : !selected.ContainsKey(hash) ? [target, .. before]
            : before.Length > 1 ? before.Where(commit => commit.Hash != hash).ToArray() : before;
        bool changed = !before.Select(commit => commit.Hash).SequenceEqual(after.Select(commit => commit.Hash), StringComparer.Ordinal);
        if (changed)
        {
            CancelQuickSearchClipboard(); CancelRevisionReads();
            int anchor = _commits.Selection.AnchorIndex;
            bool refreshing = _refreshing;
            _refreshing = true;
            try
            {
                _browseInitialComparison = null; _selectedArtificialHistoryRevision = null; _quickSearchArtificialSelection = null;
                // The public setter owns its internal batch and suppresses the
                // writable/model feedback while selecting this exact item order.
                // Do not wrap it in an outer batch or Clear/Add the old writable.
                _commits.SelectedItems = new Avalonia.Collections.AvaloniaList<object?>(after.Cast<object?>());
                _commits.Selection.AnchorIndex = after.Length == 1 ? _commits.Items.IndexOf(after[0]) : anchor;
                _historySelectionOrder.Clear(); _historySelectionOrder.AddRange(after.Reverse().Select(commit => commit.Hash));
                _quickSearchLastCommit = after[0].Hash; _parentChildLatestCommit = after[0].Hash;
                _quickSearchReturnTab = null;
            }
            finally { _refreshing = refreshing; }
        }
        // Windows also pushes a target that Ctrl has just deselected. Selection
        // events alone would instead push the survivor or retain the old primary.
        PushRevisionNavigation(hash);
        _commits.ScrollIntoView(after[0]);
        RefreshRebaseMenu(); RefreshRevisionContextMenu(); RefreshHistoryMenuStates();
        OnGitHubActionsSelectionChanged(); UpdateHistoryPresentation();
        // Keep the native tree's focus/caret, including the next keyboard arrow.
        return changed ? LoadSelectedRevisionDetailsAsync(after[0]) : Task.CompletedTask;
    }
}
