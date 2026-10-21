using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private ContextMenu? _revisionContext;
    private MenuItem? _revisionCheckoutBranches, _revisionRenameBranches, _revisionDeleteBranches, _revisionCopy;
    private readonly List<(MenuItem Item, bool Worktree, Func<bool>? Available)> _revisionActions = [];
    private bool _selectingRevisionForContextMenu, _contextRevisionDiffPending;
    private MenuItem? _revisionResetChanges, _revisionCommit;

    private object? RevisionContextItem => _parentChildLatestCommit is { } key && HistoryItem(key) is { } item
        && _commits.SelectedItems?.Contains(item) == true ? item : PrimaryHistoryItem;

    private AccessText RevisionHeader(string key, string fallback) => new()
    { Text = _translations.TranslateAccessKeys("RevisionGridControl", key, "Text", fallback) };

    private MenuItem RevisionAction(string name, string key, string label, Func<Task> action,
        string? icon = null, bool worktree = false, Func<bool>? available = null)
    {
        MenuItem item = new() { Name = name, Header = RevisionHeader(key, label), Icon = icon is null ? null : OriginalIconImage(icon) };
        item.Click += async (_, args) =>
        {
            if (args.Source != item || !item.IsEnabled || _busy || _repository is null || (worktree && _repository.IsBare)) { return; }
            try { await WithRevisionActionFocusAsync(action); }
            catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
        };
        _revisionActions.Add((item, worktree, available));
        return item;
    }

    private ContextMenu BuildRevisionContextMenu()
    {
        _revisionResetChanges = ArtificialRevisionAction("RevisionResetChanges", "resetChangesToolStripMenuItem", "&Reset changes", ResetArtificialRevisionAsync, "ResetWorkingDirChanges.png");
        _revisionCommit = ArtificialRevisionAction("RevisionCommit", "commitToolStripMenuItem", "&Commit", () => ShowWorkflowAsync("Changes"), "RepoStateDirty.png");
        DesktopHotkeys.Bind(_revisionCommit, "Browse", "Commit");
        _revisionCopy = new() { Name = "RevisionCopy", Header = RevisionHeader("copyToClipboardToolStripMenuItem", "&Copy to clipboard"), Icon = OriginalIconImage("CopyToClipboard.png") };
        _revisionCopy.SubmenuOpened += async (_, args) => { if (args.Source == _revisionCopy) { await RefreshRevisionCopyDatesAsync(); } };
        _revisionCheckoutBranches = new() { Name = "RevisionCheckoutBranch", Header = RevisionHeader("checkoutBranchToolStripMenuItem", "Chec&kout branch..."), Icon = OriginalIconImage("BranchCheckout.png") };
        _revisionRenameBranches = new() { Name = "RevisionRenameBranch", Header = RevisionHeader("renameBranchToolStripMenuItem", "R&ename branch..."), Icon = OriginalIconImage("Renamed.png") };
        _revisionDeleteBranches = new() { Name = "RevisionDeleteBranch", Header = RevisionHeader("deleteBranchToolStripMenuItem", "&Delete branch..."), Icon = OriginalIconImage("BranchDelete.png") };
        MenuItem createBranch = RevisionAction("RevisionCreateBranch", "createNewBranchToolStripMenuItem", "Create new &branch here...", CreateBranchAsync, "BranchCreate.png");
        DesktopHotkeys.Bind(createBranch, "Browse", "CreateBranch");
        MenuItem createTag = RevisionAction("RevisionCreateTag", "createTagToolStripMenuItem", "Create new ta&g here...", CreateTagAsync, "TagCreate.png");
        DesktopHotkeys.Bind(createTag, "Browse", "CreateTag");
        DesktopHotkeys.Bind(_revisionRenameBranches, "RevisionGrid", "RenameRef");
        DesktopHotkeys.Bind(_revisionDeleteBranches, "RevisionGrid", "DeleteRef");
        _revisionPushBranches = new() { Name = "RevisionPushBranch", Header = RevisionHeader("tsmiPushBranch", "Pus&h branch..."), Icon = OriginalIconImage("Push.png") };
        _revisionMergeBranches = new() { Name = "RevisionMergeBranch", Header = RevisionHeader("mergeBranchToolStripMenuItem", "&Merge into current branch..."), Icon = OriginalIconImage("Merge.png") };
        _revisionDeleteTags = new() { Name = "RevisionDeleteTag", Header = RevisionHeader("deleteTagToolStripMenuItem", "&Delete tag..."), Icon = OriginalIconImage("TagDelete.png") };
        _revisionSelectInLeftPanel = new() { Name = "RevisionSelectInLeftPanel", Header = RevisionHeader("tsmiSelectInLeftPanel", "Se&lect in left panel"), Icon = OriginalIconImage("FileTree.png") };
        _revisionSelectInLeftPanel.Click += async (_, args) =>
        {
            if (args.Source == _revisionSelectInLeftPanel && _revisionSelectInLeftPanel.IsEnabled && _selectRevisionInLeftPanel is { } select)
            { try { await select(); } catch (Exception error) { AppendOutput(error.Message); _state.Text = error.Message; } }
        };
        object[] commands =
            [
                _revisionResetChanges, _revisionCommit, new Separator(), .. BuildRevisionBisectMenuItems(), _revisionCopy, new Separator(), _revisionCheckoutBranches, _revisionPushBranches, _revisionMergeBranches, BuildRebaseMenu(),
                RevisionAction("RevisionResetCurrent", "resetCurrentBranchToHereToolStripMenuItem", "Reset c&urrent branch to here...", ResetAsync, "ResetCurrentBranchToHere.png", true),
                new Separator(), _revisionSelectInLeftPanel, createBranch,
                RevisionAction("RevisionResetAnother", "resetAnotherBranchToHereToolStripMenuItem", "Reset an&other branch to here...", ResetAnotherBranchAsync, "ResetAnotherBranchToHere.png", true),
                _revisionRenameBranches, _revisionDeleteBranches,
                new Separator(), createTag, _revisionDeleteTags, new Separator { Name = "RevisionCommitSeparator" },
                RevisionAction("RevisionCheckout", "checkoutRevisionToolStripMenuItem", "Checkout this commit...", CheckoutRevisionWithConfirmationAsync, "checkout.png", true),
                RevisionAction("RevisionRevert", "revertCommitToolStripMenuItem", "Re&vert this commit...", RevertAsync, "RevertCommit.png", true),
                RevisionAction("RevisionCherryPick", "cherryPickCommitToolStripMenuItem", "Cherr&y pick this commit...", CherryPickAsync, "CherryPick.png", true),
                RevisionAction("RevisionArchive", "archiveRevisionToolStripMenuItem", "Arch&ive this commit...", ExportArchiveAsync, "ArchiveRevision.png"),
                BuildRevisionAdvancedMenu(), new Separator(), BuildComparisonMenu(), new Separator { Name = "RevisionNavigateSeparator" }, BuildRevisionNavigateMenu(), BuildRevisionViewMenu(),
                BuildGitHubActionsShowBuildPageMenu(), BuildServerPullRequestPageMenu()
            ];
        _revisionContext = new ContextMenu { Name = "RevisionContextMenu", ItemsSource = commands };
        InitializeFocusedRevisionMenu(commands);
        _revisionContext.Opening += (_, _) => { BeginRevisionContextMenu(); CaptureRevisionBisectTarget(); PopulateRevisionReferenceMenus(); PopulateRevisionCopyMenu(); RefreshRevisionContextMenu(); ComposeRevisionContextMenu(_focusedRevisionMenu); RefreshRevisionScriptMenu(); };
        _revisionContext.Opened += (_, _) => { PopulateRevisionReferenceMenus(); PopulateRevisionCopyMenu(); RefreshRevisionContextMenu(); ComposeRevisionContextMenu(_focusedRevisionMenu); RefreshRevisionScriptMenu(); };
        _revisionContext.Closed += (_, _) => EndRevisionContextMenu();
        _revisionContext.AddHandler(KeyDownEvent, async (_, args) =>
            await WithRevisionActionFocusAsync(() => HandleShortcutsAsync(_revisionContext, args)), RoutingStrategies.Tunnel);
        _commits.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(_commits).Properties.IsRightButtonPressed || args.Source is not Control source) { return; }
            ListBoxItem? row = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            object? item = row?.DataContext;
            GitCommit? commit = item as GitCommit;
            if (item is GitCommit or ArtificialHistoryRevision)
            {
                string key = HistoryRevisionItems.Key(item);
                bool preserve = _parentChildLatestCommit == key && _commits.SelectedItems?.Contains(item) == true;
                _selectingRevisionForContextMenu = true;
                try
                {
                    if (!preserve) { _commits.SelectedItems = new Avalonia.Collections.AvaloniaList<object?>([item]); }
                    else
                    {
                        // Native context target/caret is distinct from SelectedRows order.
                        // Keep comparison order and pending reads when the latest row remains selected.
                        bool refreshing = _refreshing; _refreshing = true;
                        try
                        {
                            object[] others = _commits.SelectedItems!.Cast<object>().Where(other => !ReferenceEquals(other, item)).ToArray();
                            _commits.SelectedItems = new Avalonia.Collections.AvaloniaList<object?>(new[] { item }.Concat(others));
                        }
                        finally { _refreshing = refreshing; }
                    }
                }
                finally { _selectingRevisionForContextMenu = false; }
                _parentChildLatestCommit = key;
                row!.Focus();
            }
            CaptureRevisionPointerContext(args, commit, source);
        }, RoutingStrategies.Tunnel);
        PopulateRevisionReferenceMenus(); PopulateRevisionCopyMenu(); RefreshRevisionContextMenu(); RefreshRevisionScriptMenu();
        return _revisionContext;
    }

    private Task LoadSelectedRevisionDetailsAsync(GitCommit commit)
    {
        CancelFileGrepForCommit(commit.Hash);
        UpdateCommitInfo(commit);
        _gpgInfo.Text = null;
        return LoadVisibleRevisionPanelAsync();
    }

    private void QueuePendingRevisionDetails()
    {
        if (!_contextRevisionDiffPending) { return; }
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (!_contextRevisionDiffPending || _busy || _revisionContext?.IsOpen == true || _lifetime.IsCancellationRequested
                || _repository is null || PrimaryHistoryItem is null) { return; }
            // Keep the request while a menu action owns a dialog. Returning to
            // idle retries it, including when that dialog was simply cancelled.
            _contextRevisionDiffPending = false;
            await LoadVisibleRevisionPanelAsync();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RefreshRevisionContextMenu()
    {
        EnsureRevisionContextRepository();
        RefreshHistoryMenuStates();
        RefreshRebaseMenu();
        if (_revisionContext is null) { return; }
        bool selected = !_busy && _repository is not null && SelectedComparisonCommits().Length > 0;
        bool artificial = !_busy && _repository is { IsBare: false } && RevisionContextItem is ArtificialHistoryRevision;
        foreach (MenuItem? item in new[] { _revisionResetChanges, _revisionCommit })
        { if (item is not null) { item.IsVisible = item.IsEnabled = artificial; } }
        _revisionContext.IsEnabled = !_busy && _repository is not null && RevisionContextItem is not null;
        foreach ((MenuItem item, bool worktree, Func<bool>? available) in _revisionActions)
        { item.IsVisible = item.IsEnabled = selected && (!worktree || !_repository!.IsBare) && (available?.Invoke() ?? true); }
        RefreshRevisionBisectMenu();
        if (_revisionCopy is not null) { _revisionCopy.IsVisible = _revisionCopy.IsEnabled = selected; }
        GitReference? clicked = CurrentRevisionContextReference();
        bool includesCurrentBranch = clicked is not null ? clicked.IsBranch && clicked.IsCurrent
            : _navigationReferences.Any(reference => reference.IsBranch && reference.IsCurrent
                && reference.RevisionHash == (RevisionContextItem as GitCommit)?.Hash);
        foreach (MenuItem? group in new[] { _revisionCheckoutBranches, _revisionRenameBranches, _revisionDeleteBranches })
        {
            if (group is null) { continue; }
            group.IsEnabled = selected && (group == _revisionRenameBranches || !_repository!.IsBare) && group.Items.OfType<MenuItem>().Any();
            group.IsVisible = group.IsEnabled || group == _revisionDeleteBranches && selected && includesCurrentBranch;
        }
        if (_revisionPushBranches is not null) { _revisionPushBranches.IsVisible = _revisionPushBranches.IsEnabled = selected && !_repository!.IsBare && _revisionPushBranches.Items.OfType<MenuItem>().Any(); }
        if (_revisionMergeBranches is not null) { _revisionMergeBranches.IsVisible = _revisionMergeBranches.IsEnabled = selected && !_repository!.IsBare && _revisionMergeBranches.Items.OfType<MenuItem>().Any(); }
        if (_revisionDeleteTags is not null) { _revisionDeleteTags.IsVisible = _revisionDeleteTags.IsEnabled = selected && _revisionDeleteTags.Items.OfType<MenuItem>().Any(); }
        if (_revisionSelectInLeftPanel is not null) { _revisionSelectInLeftPanel.IsVisible = _revisionSelectInLeftPanel.IsEnabled = !IsStandaloneDiff && selected && (_selectRevisionInLeftPanel is not null || _revisionSelectInLeftPanel.Items.OfType<MenuItem>().Any()); }
        RefreshRevisionAdvancedMenu();
    }

    private void PopulateRevisionReferenceMenus()
    {
        if (_revisionCheckoutBranches is null || _revisionRenameBranches is null || _revisionDeleteBranches is null) { return; }
        GitRepository? repository = _repository;
        string? hash = (RevisionContextItem as GitCommit)?.Hash;
        GitReference? clicked = CurrentRevisionContextReference();
        GitReference[] refs = _navigationReferences.Where(reference => reference.RevisionHash == hash && reference.SymbolicTarget is null
            && (clicked is null || reference.Name == clicked.Name))
            .OrderBy(reference => reference.IsRemote).ThenBy(reference => reference.ShortName, StringComparer.OrdinalIgnoreCase).ToArray();
        MenuItem Entry(GitReference reference, Func<Task> action, bool restoreGridFocus)
        {
            MenuItem item = new() { Header = new TextBlock { Text = reference.ShortName }, Tag = reference,
                Icon = OriginalIconImage(reference.IsTag ? "Tag.png" : reference.IsRemote ? "BranchRemote.png" : "BranchLocal.png") };
            item.Click += async (_, args) =>
            {
                if (args.Source != item || _busy || repository is null || repository != _repository
                    || (RevisionContextItem as GitCommit)?.Hash != hash) { return; }
                try { if (restoreGridFocus) { await WithRevisionActionFocusAsync(action); } else { await action(); } }
                catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
            };
            return item;
        }
        object[] Entries(IEnumerable<GitReference> choices, Func<GitReference, Task> action, bool restoreGridFocus = true)
        {
            List<object> items = []; bool local = false;
            foreach (GitReference reference in choices)
            {
                if (reference.IsRemote && local) { items.Add(new Separator()); local = false; }
                if (reference.IsBranch) { local = true; }
                items.Add(Entry(reference, () => action(reference), restoreGridFocus));
            }
            return items.ToArray();
        }
        _revisionCheckoutBranches.ItemsSource = Entries(refs.Where(reference => (reference.IsBranch || reference.IsRemote) && !reference.IsCurrent),
            reference => CheckoutSelectedBranchAsync(repository!, reference));
        _revisionRenameBranches.ItemsSource = Entries(refs.Where(reference => reference.IsBranch),
            reference => RenameRevisionBranchAsync(repository!, reference));
        _revisionDeleteBranches.ItemsSource = Entries(refs.Where(reference => (reference.IsBranch || reference.IsRemote) && !reference.IsCurrent),
            reference => DeleteRevisionBranchAsync(repository!, reference));
        if (_revisionPushBranches is not null) { _revisionPushBranches.ItemsSource = Entries(refs.Where(reference => reference.IsBranch), reference => PushRevisionReferenceAsync(repository!, reference)); }
        if (_revisionMergeBranches is not null)
        {
            _revisionMergeBranches.ItemsSource = Entries(refs.Where(reference => reference.IsTag || (reference.IsBranch || reference.IsRemote) && !reference.IsCurrent),
                reference => MergeRevisionReferenceAsync(repository!, reference.Name));
            // Windows still offers the selected commit when there is no eligible named ref.
            if (_revisionMergeBranches.Items.Count == 0 && hash is not null && !refs.Any(reference => reference.IsCurrent))
            {
                MenuItem commit = new() { Header = new TextBlock { Text = hash }, Tag = hash, Icon = OriginalIconImage("CommitId.png") };
                commit.Click += async (_, args) =>
                {
                    if (args.Source == commit && !_busy && repository is not null && repository == _repository
                        && (RevisionContextItem as GitCommit)?.Hash == hash)
                    { await WithRevisionActionFocusAsync(() => MergeRevisionReferenceAsync(repository, hash)); }
                };
                _revisionMergeBranches.ItemsSource = new[] { commit };
            }
        }
        if (_revisionDeleteTags is not null) { _revisionDeleteTags.ItemsSource = Entries(refs.Where(reference => reference.IsTag), reference => DeleteRevisionBranchAsync(repository!, reference)); }
        if (_revisionSelectInLeftPanel is not null)
        {
            GitReference[] targets = refs.Where(reference => reference.IsBranch || reference.IsRemote || reference.IsTag).ToArray();
            GitReference? single = targets.Length == 1 ? targets[0] : null;
            _selectRevisionInLeftPanel = single is null ? null : () => repository == _repository
                && (RevisionContextItem as GitCommit)?.Hash == hash
                ? SelectRevisionReferenceInLeftPanelAsync(single) : Task.CompletedTask;
            _revisionSelectInLeftPanel.Tag = single;
            _revisionSelectInLeftPanel.ItemsSource = single is null ? Entries(targets, SelectRevisionReferenceInLeftPanelAsync, restoreGridFocus: false) : Array.Empty<object>();
        }
    }

    private async Task RenameRevisionBranchAsync(GitRepository repository, GitReference reference)
    {
        RenameBranchDialog dialog = new(repository, reference.ShortName, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol);
        dialog.Closed += (_, _) =>
        {
            if (dialog.RenamedBranch is not string name) { return; }
            _referenceSelectionOverride = "refs/heads/" + name;
            RenameNavigationBranchFilter(reference.Name, _referenceSelectionOverride);
        };
        await ShowRepositoryDialogAsync(dialog);
    }

    private async Task DeleteRevisionBranchAsync(GitRepository repository, GitReference reference)
    {
        if (reference.IsTag) { await ShowRepositoryDialogAsync(new DeleteTagDialog(repository, reference.ShortName)); return; }
        if (!reference.IsRemote) { await ShowRepositoryDialogAsync(new DeleteBranchDialog(repository, [reference.ShortName])); return; }
        await ShowRepositoryDialogAsync(new DeleteRemoteBranchDialog(repository, reference.Name));
    }

    private async Task CheckoutRevisionWithConfirmationAsync()
    {
        if (_repository is null || _repository.IsBare || RevisionContextItem is not GitCommit selected) { return; }
        GitRepository repository = _repository;
        if (_busy) { return; }
        await ShowRepositoryDialogAsync(new CheckoutRevisionDialog(repository,
            (owner, request, token) => CheckoutRevisionWithScriptEventsAsync(repository, request.Revision, token, request.Force, owner), selected.Hash));
    }

    private void PopulateRevisionCopyMenu(IReadOnlyDictionary<string, DateTimeOffset>? committedDates = null)
    {
        if (_revisionCopy is null) { return; }
        GitCommit[] selected = SelectedComparisonCommits();
        GitReference? clicked = CurrentRevisionContextReference();
        List<object> items = [];
        MenuItem Copy(string label, string value, string icon, Func<Task<string>>? readValue = null)
        {
            string preview = value.Split('\n')[0].TrimEnd('\r');
            if (preview.Length > 40) { preview = preview[..37] + "..."; }
            MenuItem item = new() { Header = new TextBlock { Text = label + ":   " + preview }, Icon = OriginalIconImage(icon), Tag = value };
            item.Click += async (_, args) =>
            {
                if (args.Source != item || Clipboard is not { } clipboard) { return; }
                try { await clipboard.SetTextAsync(readValue is null ? value : await readValue()); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
            };
            return item;
        }
        foreach ((string label, bool tags, string icon) in new[] { ("Branches", false, "Branch.png"), ("Tags", true, "Tag.png") })
        {
            string[] names = _navigationReferences.Where(reference => selected.Any(commit => commit.Hash == reference.RevisionHash)
                && (tags ? reference.IsTag : reference.IsBranch || reference.IsRemote) && reference.SymbolicTarget is null
                && (clicked is null || reference.Name == clicked.Name))
                .Select(reference => reference.ShortName).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0) { continue; }
            items.Add(new MenuItem { Header = T(label), IsEnabled = false });
            items.AddRange(names.Select(name => Copy(name, name, icon))); items.Add(new Separator());
        }
        string Values(Func<GitCommit, string> get) => string.Join('\n', selected.Select(get).Distinct(StringComparer.Ordinal));
        items.Add(Copy(T(selected.Length > 1 ? "Commit hashes" : "Commit hash"), Values(commit => commit.Hash), "CommitId.png"));
        GitRepository? repository = _repository;
        items.Add(Copy(T(selected.Length > 1 ? "Messages" : "Message"), Values(commit => commit.Subject), "Message.png",
            () => ReadRevisionMessagesAsync(repository ?? throw new InvalidOperationException(T("Open a repository first.")), selected)));
        items.Add(Copy(T(selected.Length > 1 ? "Authors" : "Author"), Values(commit => commit.AuthorName + " <" + commit.AuthorEmail + ">"), "Author.png"));
        bool sameDate = selected.Length == 1 && committedDates?.TryGetValue(selected[0].Hash, out DateTimeOffset date) == true && date == selected[0].AuthoredAt;
        items.Add(Copy(T(sameDate ? "Date" : "Author date"), Values(commit => commit.AuthoredAt.ToString()), "Date.png"));
        if (!sameDate && committedDates is not null)
        { items.Add(Copy(T("Commit date"), Values(commit => committedDates[commit.Hash].ToString()), "Date.png")); }
        _revisionCopy.ItemsSource = items;
    }

    private async Task<string> ReadRevisionMessagesAsync(GitRepository repository, GitCommit[] selected)
    {
        GitResult result = await repository.RunAsync(new[] { "show", "--no-patch", "-z", "--encoding=UTF-8", "--format=%B" }
            .Concat(selected.Select(commit => commit.Hash)).ToArray(), _lifetime.Token);
        if (!result.Success) { throw new GitCommandException(["show"], result); }
        string[] messages = result.StandardOutput.Split('\0');
        if (messages.LastOrDefault() == "") { messages = messages[..^1]; }
        if (messages.Length != selected.Length) { throw new InvalidOperationException(T("Unable to read the selected commit messages.")); }
        return string.Join('\n', messages.Distinct(StringComparer.Ordinal));
    }

    private async Task RefreshRevisionCopyDatesAsync()
    {
        if (_repository is not GitRepository repository) { return; }
        GitCommit[] selected = SelectedComparisonCommits();
        if (selected.Length == 0) { return; }
        try
        {
            GitResult result = await repository.RunAsync(new[] { "show", "--no-patch", "--format=%H%x00%cI" }.Concat(selected.Select(commit => commit.Hash)).ToArray(), _lifetime.Token);
            if (!result.Success) { throw new GitCommandException(["show"], result); }
            Dictionary<string, DateTimeOffset> dates = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\0')).ToDictionary(parts => parts[0], parts => DateTimeOffset.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
            if (_repository == repository && selected.Select(commit => commit.Hash).SequenceEqual(SelectedComparisonCommits().Select(commit => commit.Hash)))
            { PopulateRevisionCopyMenu(dates); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppendOutput(exception.Message); }
    }
}
