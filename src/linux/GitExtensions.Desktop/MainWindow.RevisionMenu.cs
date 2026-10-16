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

    private AccessText RevisionHeader(string key, string fallback) => new()
    { Text = _translations.TranslateAccessKeys("RevisionGridControl", key, "Text", fallback) };

    private MenuItem RevisionAction(string name, string key, string label, Func<Task> action,
        string? icon = null, bool worktree = false, Func<bool>? available = null)
    {
        MenuItem item = new() { Name = name, Header = RevisionHeader(key, label), Icon = icon is null ? null : OriginalIconImage(icon) };
        item.Click += async (_, args) =>
        {
            if (args.Source != item || !item.IsEnabled || _busy || _repository is null || (worktree && _repository.IsBare)) { return; }
            try { await action(); }
            catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
        };
        _revisionActions.Add((item, worktree, available));
        return item;
    }

    private ContextMenu BuildRevisionContextMenu()
    {
        _revisionCopy = new() { Name = "RevisionCopy", Header = RevisionHeader("copyToClipboardToolStripMenuItem", "&Copy to clipboard"), Icon = OriginalIconImage("CopyToClipboard.png") };
        _revisionCopy.SubmenuOpened += async (_, args) => { if (args.Source == _revisionCopy) { await RefreshRevisionCopyDatesAsync(); } };
        _revisionCheckoutBranches = new() { Name = "RevisionCheckoutBranch", Header = RevisionHeader("checkoutBranchToolStripMenuItem", "Chec&kout branch..."), Icon = OriginalIconImage("BranchCheckout.png") };
        _revisionRenameBranches = new() { Name = "RevisionRenameBranch", Header = RevisionHeader("renameBranchToolStripMenuItem", "R&ename branch..."), Icon = OriginalIconImage("Renamed.png") };
        _revisionDeleteBranches = new() { Name = "RevisionDeleteBranch", Header = RevisionHeader("deleteBranchToolStripMenuItem", "&Delete branch..."), Icon = OriginalIconImage("BranchDelete.png") };
        MenuItem createBranch = RevisionAction("RevisionCreateBranch", "createNewBranchToolStripMenuItem", "Create new &branch here...", CreateBranchAsync, "BranchCreate.png");
        createBranch.InputGesture = new(Key.B, KeyModifiers.Control);
        MenuItem createTag = RevisionAction("RevisionCreateTag", "createTagToolStripMenuItem", "Create new ta&g here...", CreateTagAsync, "TagCreate.png");
        createTag.InputGesture = new(Key.T, KeyModifiers.Control);
        _revisionContext = new ContextMenu
        {
            Name = "RevisionContextMenu", ItemsSource = new object[]
            {
                _revisionCopy, new Separator(), _revisionCheckoutBranches, BuildRebaseMenu(),
                RevisionAction("RevisionResetCurrent", "resetCurrentBranchToHereToolStripMenuItem", "Reset c&urrent branch to here...", ResetAsync, "ResetCurrentBranchToHere.png", true),
                new Separator(), createBranch, _revisionRenameBranches, _revisionDeleteBranches,
                RevisionAction("RevisionResetAnother", "resetAnotherBranchToHereToolStripMenuItem", "Reset an&other branch to here...", ResetAnotherBranchAsync, "ResetAnotherBranchToHere.png", true),
                new Separator(), createTag, new Separator(),
                RevisionAction("RevisionCheckout", "checkoutRevisionToolStripMenuItem", "Checkout this commit...", CheckoutRevisionWithConfirmationAsync, "checkout.png", true),
                RevisionAction("RevisionRevert", "revertCommitToolStripMenuItem", "Re&vert this commit...", RevertAsync, "RevertCommit.png", true),
                RevisionAction("RevisionCherryPick", "cherryPickCommitToolStripMenuItem", "Cherr&y pick this commit...", CherryPickAsync, "CherryPick.png", true),
                RevisionAction("RevisionArchive", "archiveRevisionToolStripMenuItem", "Arch&ive this commit...", ExportArchiveAsync, "ArchiveRevision.png"),
                BuildRevisionAdvancedMenu(), new Separator(), BuildComparisonMenu(), new Separator(), BuildRevisionNavigateMenu(), BuildRevisionViewMenu()
            }
        };
        _revisionContext.Opening += (_, _) => { PopulateRevisionReferenceMenus(); PopulateRevisionCopyMenu(); RefreshRevisionContextMenu(); };
        _revisionContext.Opened += (_, _) => { PopulateRevisionReferenceMenus(); PopulateRevisionCopyMenu(); RefreshRevisionContextMenu(); };
        _revisionContext.Closed += (_, _) => QueuePendingRevisionDetails();
        _revisionContext.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Control && args.Key is Key.B or Key.T) { _revisionContext.Close(); }
            HandleShortcuts(this, args);
        }, RoutingStrategies.Tunnel);
        _commits.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(_commits).Properties.IsRightButtonPressed || args.Source is not Control source) { return; }
            GitCommit? commit = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as GitCommit;
            if (commit is not null && !ReferenceEquals(_commits.SelectedItem, commit))
            {
                GitCommit[] preserved = _commits.SelectedItems?.Contains(commit) == true
                    ? _commits.SelectedItems.OfType<GitCommit>().ToArray() : [];
                _selectingRevisionForContextMenu = true;
                try
                {
                    _commits.SelectedItems?.Clear(); _commits.SelectedItem = commit;
                    foreach (GitCommit other in preserved.Where(selected => selected.Hash != commit.Hash)) { _commits.SelectedItems?.Add(other); }
                }
                finally { _selectingRevisionForContextMenu = false; }
            }
        }, RoutingStrategies.Tunnel);
        PopulateRevisionReferenceMenus(); PopulateRevisionCopyMenu(); RefreshRevisionContextMenu();
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
                || _repository is null || _commits.SelectedItem is not GitCommit commit) { return; }
            // Keep the request while a menu action owns a dialog. Returning to
            // idle retries it, including when that dialog was simply cancelled.
            _contextRevisionDiffPending = false;
            await LoadSelectedRevisionDetailsAsync(commit);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RefreshRevisionContextMenu()
    {
        RefreshHistoryMenuStates();
        if (_revisionContext is null) { return; }
        bool selected = !_busy && _repository is not null && SelectedComparisonCommits().Length > 0;
        _revisionContext.IsEnabled = selected;
        foreach ((MenuItem item, bool worktree, Func<bool>? available) in _revisionActions)
        { item.IsEnabled = selected && (!worktree || !_repository!.IsBare) && (available?.Invoke() ?? true); }
        if (_revisionCopy is not null) { _revisionCopy.IsEnabled = selected; }
        foreach (MenuItem? group in new[] { _revisionCheckoutBranches, _revisionRenameBranches, _revisionDeleteBranches })
        { if (group is not null) { group.IsEnabled = selected && (group == _revisionRenameBranches || !_repository!.IsBare) && group.Items.OfType<MenuItem>().Any(); } }
        RefreshRevisionAdvancedMenu();
    }

    private void PopulateRevisionReferenceMenus()
    {
        if (_revisionCheckoutBranches is null || _revisionRenameBranches is null || _revisionDeleteBranches is null) { return; }
        GitRepository? repository = _repository;
        string? hash = (_commits.SelectedItem as GitCommit)?.Hash;
        GitReference[] refs = _navigationReferences.Where(reference => reference.Hash == hash && reference.SymbolicTarget is null)
            .OrderBy(reference => reference.IsRemote).ThenBy(reference => reference.ShortName, StringComparer.OrdinalIgnoreCase).ToArray();
        MenuItem Entry(GitReference reference, Func<Task> action)
        {
            MenuItem item = new() { Header = new TextBlock { Text = reference.ShortName }, Tag = reference,
                Icon = OriginalIconImage(reference.IsRemote ? "BranchRemote.png" : "BranchLocal.png") };
            item.Click += async (_, args) =>
            {
                if (args.Source != item || _busy || repository is null || repository != _repository) { return; }
                try { await action(); } catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
            };
            return item;
        }
        object[] Entries(IEnumerable<GitReference> choices, Func<GitReference, Task> action)
        {
            List<object> items = []; bool local = false;
            foreach (GitReference reference in choices)
            {
                if (reference.IsRemote && local) { items.Add(new Separator()); local = false; }
                if (reference.IsBranch) { local = true; }
                items.Add(Entry(reference, () => action(reference)));
            }
            return items.ToArray();
        }
        _revisionCheckoutBranches.ItemsSource = Entries(refs.Where(reference => (reference.IsBranch || reference.IsRemote) && !reference.IsCurrent),
            reference => CheckoutSelectedBranchAsync(repository!, reference));
        _revisionRenameBranches.ItemsSource = Entries(refs.Where(reference => reference.IsBranch),
            reference => RenameRevisionBranchAsync(repository!, reference));
        _revisionDeleteBranches.ItemsSource = Entries(refs.Where(reference => (reference.IsBranch || reference.IsRemote) && !reference.IsCurrent),
            reference => DeleteRevisionBranchAsync(repository!, reference));
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
        if (!reference.IsRemote) { await ShowRepositoryDialogAsync(new DeleteBranchDialog(repository, [reference.ShortName])); return; }
        await ShowRepositoryDialogAsync(new DeleteRemoteBranchDialog(repository, reference.Name));
    }

    private async Task CheckoutRevisionWithConfirmationAsync()
    {
        if (_repository is null || _repository.IsBare || _commits.SelectedItem is not GitCommit selected) { return; }
        GitRepository repository = _repository;
        if (!await Dialogs.ConfirmAsync(WorkflowOwner, T("Checkout this commit"), T("Checkout this commit with detached HEAD?") + "\n\n" + selected.Hash + "\n" + selected.Subject)) { return; }
        if (_repository != repository || _busy) { return; }
        await RunAsync("Checkout revision", r => r.CheckoutAsync(selected.Hash, detached: true, cancellationToken: Token));
    }

    private void PopulateRevisionCopyMenu(IReadOnlyDictionary<string, DateTimeOffset>? committedDates = null)
    {
        if (_revisionCopy is null) { return; }
        GitCommit[] selected = SelectedComparisonCommits();
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
            string[] names = _navigationReferences.Where(reference => selected.Any(commit => commit.Hash == reference.Hash)
                && (tags ? reference.IsTag : reference.IsBranch || reference.IsRemote) && reference.SymbolicTarget is null)
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
