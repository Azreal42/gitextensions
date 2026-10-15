using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using GitExtensions.Desktop.Controls;
using Avalonia.Layout;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Value(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith('-') || value.Contains('\0')) { throw new ArgumentException("A name or revision cannot begin with '-' or contain NUL."); }
        return value;
    }

    private GitReference SelectedReference() => _references.SelectedItem as GitReference ?? throw new InvalidOperationException("Select a branch or tag first.");
    private GitCommit SelectedCommit() => _commits.SelectedItem as GitCommit ?? throw new InvalidOperationException("Select a commit in History first.");
    private GitRemote SelectedRemote() => _remotes.SelectedItem as GitRemote ?? throw new InvalidOperationException("Select a remote first.");
    private GitStash SelectedStash() => _stashes.SelectedItem as GitStash ?? throw new InvalidOperationException("Select a stash first.");
    private string[] SelectedPaths(bool includeOriginalPath = true)
        => SelectedWorkingChanges
            .SelectMany(change => includeOriginalPath && change.OriginalPath is not null ? new[] { change.Path, change.OriginalPath } : new[] { change.Path })
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];

    private async Task CloneAsync()
    {
        if (_busy) { return; }
        CloneDialog dialog = new(repository: _repository);
        await RunAsync("Clone", async _ => await dialog.ShowDialog(WorkflowOwner), requireRepository: false, refresh: false);
        if (dialog.CreatedRepository is GitRepository cloned)
        {
            LoadRepositoryHistory(); UpdateRepositoryMenus();
            if (_dashboard?.IsVisible == true) { _dashboard.Refresh(); }
            if (await Dialogs.YesNoAsync(this, T("Open"), string.Format(System.Globalization.CultureInfo.CurrentCulture, T("The repository has been cloned successfully.\nDo you want to open the new repository \"{0}\" now?"), cloned.Path)))
            { await OpenRepositoryAsync(cloned.Path); }
        }
    }

    private async Task InitializeAsync()
    {
        if (_busy) { return; }
        InitDialog dialog = new(string.IsNullOrEmpty(_repositoryPath.Text) ? new ClonePreferencesStore().Load().Destination : _repositoryPath.Text);
        await RunAsync("Create new repository", async _ => await dialog.ShowDialog(WorkflowOwner), requireRepository: false, refresh: false);
        if (dialog.CreatedRepository is GitRepository initialized) { await OpenRepositoryAsync(initialized.Path); }
    }

    private Task StageAsync(bool all) => RunAsync("Stage", async repository =>
    {
        IEnumerable<GitChange> changes = all ? FilteredWorkingChanges.Where(change => change.IsModified || change.IsConflict) : SelectedWorkingChanges;
        HashSet<string> excluded = (await repository.GetIndexFlagsAsync(Token)).Where(file => file.IsAssumeUnchanged || file.IsSkipWorktree)
            .Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        string[] paths = changes.Where(change => !excluded.Contains(change.Path)).Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 0) { await repository.StageAsync(paths, Token); }
        if (all) { _changesFilesView?.ClearFilter(); }
    });

    private Task UnstageAsync(bool all) => RunAsync("Unstage", async repository =>
    {
        IEnumerable<GitChange> changes = all ? FilteredWorkingChanges.Where(change => change.IsStaged) : SelectedWorkingChanges;
        string[] paths = changes.SelectMany(change => change.OriginalPath is null ? new[] { change.Path } : new[] { change.Path, change.OriginalPath })
            .Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 0) { await repository.UnstageAsync(paths, Token); }
        if (all) { _changesFilesView?.ClearFilter(); }
    });

    private bool _settingWorkingDiffSide;

    private Task LoadSelectedDiffAsync()
    {
        if (_refreshing || _settingWorkingDiffSide || SelectedWorkingChange is not GitChange change) { return Task.CompletedTask; }
        bool staged = change.IsStaged && (_stagedDiff.IsChecked == true || !change.IsModified);
        _settingWorkingDiffSide = true;
        try { _stagedDiff.IsChecked = staged; }
        finally { _settingWorkingDiffSide = false; }
        return ReadBrowserAsync(_workingDiffRead, "File diff", async (repository, token) =>
        {
            _selectedPatch = null; _hunks.ItemsSource = null; UpdatePartialStageActions();
            await LoadChangedFileContentAsync(repository, change, staged, token);
            token.ThrowIfCancellationRequested();
            _selectedPatch = _changesDiffView?.CanStagePatch == true ? GitPatch.Parse(_changesDiff.Text ?? "").FirstOrDefault() : null;
            _hunks.ItemsSource = _selectedPatch?.Hunks;
        });
    }

    private async Task ApplyHunksAsync(bool reverse)
    {
        if (_workingDiffRead.Current is not null || SelectedWorkingChange is null || _changesDiffView?.CanStagePatch != true || _selectedPatch is null || _hunks.SelectedItems?.Count is not > 0) { _state.Text = "Select diff hunks first"; return; }
        if ((_stagedDiff.IsChecked == true) != reverse) { _state.Text = reverse ? "Show staged changes before unstaging hunks" : "Show unstaged changes before staging hunks"; return; }
        byte[] patch = GitEncodedPatch.SelectHunks(_changesDiffView.Document!, _hunks.SelectedItems.Cast<GitPatchHunk>().Select(h => h.Index));
        await RunAsync(reverse ? "Unstage hunks" : "Stage hunks", r => r.ApplyPatchAsync(patch, staged: true, reverse: reverse, cancellationToken: Token));
        await LoadSelectedDiffAsync();
    }

    private async Task ApplyLinesAsync(bool reverse)
    {
        if (_workingDiffRead.Current is not null || _selectedPatch is null || _changesDiffView is null || !_changesDiffView.CanStagePatch || _changesDiffView.SelectionLength == 0 || SelectedWorkingChange is not GitChange change) { _state.Text = "Select changed lines in the diff first"; return; }
        if ((_stagedDiff.IsChecked == true) != reverse) { _state.Text = reverse ? "Show staged changes before unstaging lines" : "Show unstaged changes before staging lines"; return; }
        byte[]? patch = GitEncodedPatch.SelectLines(_changesDiffView.Document!, _changesDiffView.SelectionStart, _changesDiffView.SelectionLength,
            staged: reverse, reverse: reverse, isNewFile: change.IndexStatus == 'A', isRenamed: change.OriginalPath is not null);
        if (patch is null) { _state.Text = "The selected text contains no changed lines"; return; }
        await RunAsync(reverse ? "Unstage lines" : "Stage lines", repository => repository.ApplyPatchAsync(patch, staged: true, reverse: reverse, cancellationToken: Token));
        await LoadSelectedDiffAsync();
    }

    private async Task CommitAsync()
    {
        string message = _commitMessage.Text ?? "";
        if (string.IsNullOrWhiteSpace(message)) { _state.Text = "Enter a commit message"; _commitMessage.Focus(); return; }
        if (_amend.IsChecked == true && !await Dialogs.ConfirmAsync(this, "Amend commit", "Replace the latest commit with the staged changes and this message?")) { return; }
        await RunAsync("Commit", async repository =>
        {
            GitResult result = await repository.CommitAsync(message, _amend.IsChecked == true, Token);
            AppendOutput(result.StandardOutput + result.StandardError);
            _commitMessage.Text = "";
            _amend.IsChecked = false;
        });
    }

    private async Task DiscardAsync()
    {
        string[] paths = SelectedPaths();
        if (paths.Length == 0) { _state.Text = "Select changed files first"; return; }
        if (!await Dialogs.ConfirmAsync(this, "Discard changes", $"Discard unstaged changes in {paths.Length} selected paths? Untracked files are preserved.")) { return; }
        await RunAsync("Discard", r => ExecuteAsync(r, ["restore", "--worktree", "--", .. paths]));
    }

    private async Task ResolveConflictAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        await ShowRepositoryDialogAsync(new ResolveConflictsDialog(_repository));
    }

    private async Task ResolveWorkflowConflictsAsync(Window owner, GitRepository repository, bool offerCommit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return; }
        if ((await repository.GetConflictsAsync(token)).Count > 0)
        {
            Task<bool> confirmation = Dialogs.ConfirmAsync(owner, T("Resolve conflicts"), T("There are unresolved conflicts. Resolve them now?"));
            using CancellationTokenRegistration cancelConfirmation = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } }));
            if (!await confirmation) { return; }
            token.ThrowIfCancellationRequested();
            if (!owner.IsVisible || !await ResolveConflictsDialog.ShowAsync(owner, repository, token)) { return; }
        }
        token.ThrowIfCancellationRequested();
        if (!offerCommit || !owner.IsVisible || !(await repository.GetStatusAsync(token)).Changes.Any(change => change.IsStaged)) { return; }
        CommitDialog commit = new(repository, afterSubmoduleUpdateAsync: (window, ct) => AfterSubmoduleUpdateAsync(repository, window, ct)) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancelCommit = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(commit.Close));
        try { await commit.ShowDialog(owner); token.ThrowIfCancellationRequested(); }
        finally { commit.Close(); }
    }

    private async Task CreateBranchAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new CreateBranchDialog(_repository, CurrentRevision, autoNormalize: _preferences.AutoNormaliseBranchName, replacement: _preferences.AutoNormaliseSymbol));
    }

    private async Task CheckoutAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        CheckoutBranchDialog? dialog = null;
        dialog = new CheckoutBranchDialog(repository, _references.SelectedItem as GitReference, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol,
            token => AfterSubmoduleUpdateAsync(repository, dialog!, token));
        await ShowRepositoryDialogAsync(dialog);
    }

    private async Task DeleteReferenceAsync()
    {
        if (_repository is null || _busy) { return; }
        GitReference reference = SelectedReference();
        if (reference.IsRemote) { _state.Text = "Remote deletion requires Push with an explicit refspec"; return; }
        await ShowRepositoryDialogAsync(reference.IsTag ? new DeleteTagDialog(_repository, reference.ShortName) : new DeleteBranchDialog(_repository, [reference.ShortName]));
    }

    private async Task DeleteBranchesAsync()
    {
        if (_repository is null || _busy) { return; }
        string[] selected = _references.SelectedItem is GitReference { IsBranch: true } reference ? [reference.ShortName] : [];
        await ShowRepositoryDialogAsync(new DeleteBranchDialog(_repository, selected));
    }

    private async Task DeleteTagsAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new DeleteTagDialog(_repository, (_references.SelectedItem as GitReference) is { IsTag: true } reference ? reference.ShortName : null));
    }

    private async Task RenameBranchAsync()
    {
        if (_repository is null || _busy) { return; }
        GitReference? reference = _references.SelectedItem as GitReference;
        if (reference is not null && !reference.IsBranch) { _state.Text = "Select a local branch"; return; }
        string? branch = reference?.ShortName;
        if (branch is null)
        {
            GitStatus? status = null;
            await RunAsync("Select branch", async repository => status = await repository.GetStatusAsync(Token), refresh: false);
            branch = status?.Branch;
        }
        if (branch is null or "(detached)") { _state.Text = T("Select a local branch"); return; }
        string oldReference = "refs/heads/" + branch;
        RenameBranchDialog dialog = new(_repository, branch, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol);
        dialog.Closed += (_, _) =>
        {
            if (dialog.RenamedBranch is not string renamed) { return; }
            _referenceSelectionOverride = "refs/heads/" + renamed;
            if (_navigation.SelectedItem is TreeViewItem { Tag: GitReference navigationReference } selected && navigationReference.Name == oldReference)
            { selected.Tag = navigationReference with { Name = _referenceSelectionOverride }; }
            RenameNavigationBranchFilter(oldReference, _referenceSelectionOverride);
        };
        await ShowRepositoryDialogAsync(dialog);
    }

    private async Task CreateTagAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new CreateTagDialog(_repository, CurrentRevision));
    }

    private async Task MergeAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        GitRepository repository = _repository;
        await ShowRepositoryDialogAsync(new MergeBranchDialog(repository, (_references.SelectedItem as GitReference)?.Name,
            ResolveWorkflowConflictsAsync, (owner, ct) => AfterSubmoduleUpdateAsync(repository, owner, ct)));
    }

    private async Task ManageOperationAsync(GitOperationAction action)
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        GitRepository repository = _repository;
        bool pendingRebase = (await repository.GetRebaseStateAsync(_lifetime.Token)).IsActive;
        if (_repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
        if (pendingRebase)
        {
            await ShowRebaseDialogAsync(recoveryAction: action);
            return;
        }
        string[]? values = await Dialogs.PromptAsync(this, action.ToString(), ("Operation: merge, rebase, cherry-pick or revert", "rebase"));
        if (values is null || _repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
        if (values[0] == "rebase") { await ShowRebaseDialogAsync(recoveryAction: action); }
        else { await RunAsync(action.ToString(), r => r.ManageOperationAsync(values[0], action, Token)); }
    }

    private async Task CherryPickAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        string[] revisions = SelectedComparisonCommits().Reverse().Select(commit => commit.Hash).ToArray();
        if (revisions.Length == 0) { _state.Text = T("Select a commit in History first."); return; }
        GitRepository repository = _repository;
        try
        {
            await RunAsync("Cherry-pick", async _ =>
                await CherryPickSequence.ShowAsync(WorkflowOwner, repository, revisions, Token,
                    (owner, token) => AfterSubmoduleUpdateAsync(repository, owner, token)), refresh: false);
        }
        finally
        {
            // A cancelled sequence can have applied earlier commits, or leave
            // a conflict that Git needs the user to resolve.
            if (_repository == repository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
    }

    private async Task RevertAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        string[] revisions = SelectedComparisonCommits().Reverse().Select(commit => commit.Hash).ToArray();
        if (revisions.Length == 0) { _state.Text = T("Select a commit in History first."); return; }
        GitRepository repository = _repository;
        try { await RunAsync("Revert", async _ => await RevertSequence.ShowAsync(WorkflowOwner, repository, revisions, Token, ResolveWorkflowConflictsAsync), refresh: false); }
        finally { if (_repository == repository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); } }
    }

    private async Task ResetAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        await ShowRepositoryDialogAsync(new ResetCurrentBranchDialog(repository, SelectedCommit().Hash,
            afterSubmoduleUpdate: (owner, ct) => AfterSubmoduleUpdateAsync(repository, owner, ct)));
    }

    private async Task ResetAnotherBranchAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        await ShowRepositoryDialogAsync(new ResetAnotherBranchDialog(repository, SelectedCommit().Hash,
            afterSubmoduleUpdate: (owner, ct) => AfterSubmoduleUpdateAsync(repository, owner, ct)));
    }

    private async Task CompareAsync()
    {
        if (_repository is null || _busy) { return; }
        GitCommit[] selected = SelectedComparisonCommits();
        string compare = CurrentRevision;
        string from = selected.Length > 1 ? selected[^1].Hash : (_commits.SelectedItem as GitCommit)?.Parents.FirstOrDefault() ?? compare;
        if (selected.Length > 1) { compare = selected[0].Hash; }
        await ShowRepositoryDialogAsync(new CompareRevisionsDialog(_repository, from, compare));
    }

    private async Task AddRemoteAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Add remote", ("Name", "origin"), ("URL", ""));
        if (values is not null) { await RunAsync("Add remote", r => r.AddRemoteAsync(values[0], values[1], Token)); }
    }

    private async Task EditRemoteAsync()
    {
        GitRemote remote = SelectedRemote();
        string[]? values = await Dialogs.PromptAsync(this, "Edit remote URL", ("URL", remote.FetchUrls.FirstOrDefault() ?? ""));
        if (values is not null) { await RunAsync("Edit remote", r => r.SetRemoteUrlAsync(remote.Name, values[0], cancellationToken: Token)); }
    }

    private async Task RemoveRemoteAsync()
    {
        GitRemote remote = SelectedRemote();
        if (await Dialogs.ConfirmAsync(this, "Remove remote", $"Remove {remote.Name} and its tracking refs?")) { await RunAsync("Remove remote", r => r.RemoveRemoteAsync(remote.Name, Token)); }
    }

    private Task PushAsync() => ShowPushAsync(quick: false);
    private Task QuickPushAsync() => ShowPushAsync(quick: true);

    private async Task ShowPushAsync(bool quick)
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        PushDialog dialog = new(repository, (owner, token) => AfterSubmoduleUpdateAsync(repository, owner, token))
            { RequestedThemeVariant = WorkflowOwner.ActualThemeVariant };
        try
        {
            await RunAsync("Push", async _ =>
            {
                CancellationToken commandToken = Token;
                using CancellationTokenRegistration registration = commandToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
                if (quick) { await dialog.PushAndShowWhenFailedAsync(WorkflowOwner, commandToken); }
                else
                {
                    dialog.Opened += async (_, _) => await dialog.InitializeAsync(commandToken);
                    await dialog.ShowDialog(WorkflowOwner);
                }
                commandToken.ThrowIfCancellationRequested();
            }, refresh: false);
        }
        finally
        {
            dialog.Close();
            if (dialog.HasRepositoryChanges && _repository is not null && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
    }

    private Task BrowseRemoteAsync()
    {
        Uri? uri = SelectedRemote().BrowseUri;
        if (uri is null) { throw new InvalidOperationException("This remote has no recognized web URL."); }
        Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { uri.AbsoluteUri }, UseShellExecute = false });
        return Task.CompletedTask;
    }

    private Task ApplyStashAsync(bool pop)
    {
        GitStash stash = SelectedStash();
        return RunQuickStashAsync(pop ? "Pop stash" : "Apply stash", r => r.ApplyStashAsync(stash, pop, cancellationToken: Token));
    }

    private async Task DropStashAsync()
    {
        GitStash stash = SelectedStash();
        if (await Dialogs.ConfirmAsync(this, "Delete stash", $"Permanently delete {stash.Subject}?")) { await RunAsync("Delete stash", r => r.DropStashAsync(stash, Token)); }
    }

    private Task ShowStashAsync()
    {
        GitStash stash = SelectedStash();
        return RunAsync("Stash diff", async r =>
        {
            _fileContent?.ClearImage();
            if (_fileContent is not null) { _fileContent.TextView.Mode = DiffViewMode.Auto; }
            _fileView.Text = (await r.RunAsync(["stash", "show", "-p", "--include-untracked", stash.Reference], Token)).StandardOutput;
            SelectDetailsTab("FileTreeTab");
        }, refresh: false);
    }

    private async Task AddWorktreeAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Add worktree", ("Directory", ""), ("Revision", "HEAD"), ("New branch (blank for existing revision)", ""));
        if (values is not null) { await RunAsync("Add worktree", r => r.AddWorktreeAsync(values[0], values[1], Optional(values[2]), Token)); }
    }

    private Task OpenWorktreeAsync() => _worktrees.SelectedItem is GitWorktree worktree ? OpenRepositoryAsync(worktree.Path) : Task.CompletedTask;

    private async Task RemoveWorktreeAsync()
    {
        if (_worktrees.SelectedItem is not GitWorktree worktree) { return; }
        if (await Dialogs.ConfirmAsync(this, "Remove worktree", $"Remove {worktree.Path}? Git will refuse dirty or locked worktrees.")) { await RunAsync("Remove worktree", r => r.RemoveWorktreeAsync(worktree.Path, cancellationToken: Token)); }
    }

    private async Task AddSubmoduleAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Add submodule", ("URL", ""), ("Repository-relative path", ""));
        if (values is not null) { await RunAsync("Add submodule", r => r.AddSubmoduleAsync(values[0], values[1], Token)); }
    }

    private Task OpenSubmoduleAsync() => _submodules.SelectedItem is GitSubmodule module && _repository is not null ? OpenRepositoryAsync(Path.Combine(_repository.Path, module.Path)) : Task.CompletedTask;

    private Task ShowFileAsync(string path, CancellationToken parent = default)
    {
        string? identity = _repository is null ? null : string.Join('\0', _repository.Path, path, _fileTreeRevision ?? CurrentRevision, "blob");
        if (identity is not null && _fileContent?.TextView is { IsDocumentReady: true } viewer && viewer.DocumentIdentity == identity)
        { _treeFileRead.Cancel(); return Task.CompletedTask; }
        return ReadBrowserAsync(_treeFileRead, "File contents", (repository, token) => LoadTreeFileAsync(repository, path, token), parent);
    }

    private async Task LoadTreeFileAsync(GitRepository repository, string path, CancellationToken? cancellationToken = null)
    {
        CancellationToken token = cancellationToken ?? Token;
        string revision = _fileTreeRevision ?? CurrentRevision;
        GitTreeEntry? entry = _fileTreeEntries.GetValueOrDefault(path)
            ?? (await repository.GetTreeAsync(revision, path, token)).FirstOrDefault(item => item.Path == path);
        byte[]? bytes = entry?.IsSubmodule == true ? null : await repository.GetFileBytesAsync(path, revision, token);
        string encoding = await repository.GetFileEncodingNameAsync(token);
        token.ThrowIfCancellationRequested();
        if (_fileContent is null) { return; }
        await _fileContent.TextView.ConfigureRepositoryAsync(repository, token);
        token.ThrowIfCancellationRequested();
        _fileContent.TextView.BeginDocumentLoad(); _fileContent.ClearImage();
        _fileContent.TextView.DocumentIdentity = string.Join('\0', repository.Path, path, revision, "blob");
        _fileContent.TextView.Mode = DiffViewMode.PlainText;
        if (entry?.IsSubmodule == true)
        {
            _fileView.Text = $"Submodule: {path}\nCommit: {entry.Hash}";
            _fileContent.TextView.CompletePlainTextDocumentLoad();
        }
        else { _fileContent.ShowFile(path, bytes!, encoding); }
    }

    private Task BlameAsync() => RunAsync("Blame", async r =>
    {
        string path = _filePath.Text ?? "";
        string identity = string.Join('\0', r.Path, path, CurrentRevision, "blob");
        bool sameFile = _fileContent is { } content && content.TextView.IsDocumentReady && content.TextView.DocumentIdentity == identity;
        int line = sameFile ? _fileContent!.TextView.CurrentFileLine : 1;
        BlameDialog dialog = new(r, path, CurrentRevision, line, fileEncodingName: sameFile ? _fileContent!.TextView.FileEncodingName : null);
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await dialog.ShowDialog(WorkflowOwner);
    }, refresh: false);

    private Task FileHistoryAsync() => RunAsync("File history", async r =>
    {
        FileHistoryDialog dialog = new(r, _filePath.Text ?? "", CurrentRevision);
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await dialog.ShowDialog(WorkflowOwner);
    }, refresh: false);

    private Task SearchAsync() => ShowFileGrepDialogAsync();

    private async Task SetConfigurationAsync(GitConfigScope scope)
    {
        string selected = _configuration.SelectedItem as string ?? "";
        int separator = selected.IndexOf(" = ", StringComparison.Ordinal);
        string[]? values = await Dialogs.PromptAsync(this, $"Set {scope.ToString().ToLowerInvariant()} setting", ("Key", separator < 0 ? "user.name" : selected[..separator]), ("Value", separator < 0 ? "" : selected[(separator + 3)..]));
        if (values is not null) { await RunAsync("Configuration", r => r.SetConfigAsync(values[0], values[1], scope, Token)); }
    }

    private async Task DiffViewerSettingsAsync()
        => await NativeDiffViewerSettingsDialog.ShowAsync(WorkflowOwner);

    private Task SaveCurrentViewSettingsAsync()
    {
        if (!new DiffViewerPreferencesStore().SaveCurrentViewSettingsAsDefault())
        {
            string message = T("Unable to save settings.");
            AppendOutput(message); _state.Text = message;
        }
        return Task.CompletedTask;
    }

    private async Task UnsetConfigurationAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Unset local setting", ("Key", (_configuration.SelectedItem as string)?.Split(" = ")[0] ?? ""));
        if (values is not null && await Dialogs.ConfirmAsync(this, "Unset local setting", $"Remove local {values[0]}?")) { await RunAsync("Unset setting", r => r.UnsetConfigAsync(values[0], GitConfigScope.Local, Token)); }
    }

    private async Task DiffToolAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Configure diff tool", ("Git tool name (for example meld, vimdiff, kdiff3)", "meld"));
        if (values is not null) { await RunAsync("Configure diff tool", r => r.SetConfigAsync("diff.tool", values[0], GitConfigScope.Global, Token)); }
    }

    private Task OpenTerminalAsync()
    {
        if (_busy || _repository is null) { return Task.CompletedTask; }
        string[] names = ["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"];
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        string? executable = names.SelectMany(name => directories.Select(directory => Path.Combine(directory, name))).FirstOrDefault(File.Exists);
        if (executable is null) { throw new InvalidOperationException("No terminal found on PATH."); }
        ProcessStartInfo info = new(executable) { WorkingDirectory = _repository.Path, UseShellExecute = false };
        // Ctrl+G is Git bash in the original, so explicitly request Bash instead
        // of depending on a terminal profile's configured interactive shell.
        info.ArgumentList.Add(Path.GetFileName(executable) == "gnome-terminal" ? "--" : "-e");
        info.ArgumentList.Add("bash");
        ExternalLauncher(info);
        return Task.CompletedTask;
    }

    private Task ChangeThemeAsync()
    {
        string theme = _preferences.Theme switch { "System" => "Dark", "Dark" => "Light", _ => "System" };
        _preferences = _preferences with { Theme = theme };
        ApplyTheme(theme);
        _preferencesStore.Save(_preferences);
        return Task.CompletedTask;
    }

    private static void ApplyTheme(string theme)
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = theme switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        }
    }

    private Control BuildRepositoryTools()
    {
        StackPanel tools = new() { Spacing = 10, Margin = new Thickness(16) };
        tools.Children.Add(new TextBlock { Text = "History and repository maintenance", FontSize = 20 });
        Button reflog = Button("Reflog", ReflogAsync); reflog.Name = "RepositoryShowReflog";
        tools.Children.Add(Row(reflog, Button("Verify objects", VerifyObjectsAsync), Button("Repository statistics", StatisticsAsync)));
        tools.Children.Add(Row(Button("Export archive…", ExportArchiveAsync), Button("Create patches…", FormatPatchAsync), Button("Apply patch…", ApplyPatchAsync), Button("Notes…", NotesAsync)));
        tools.Children.Add(Row(Button("Bisect…", BisectAsync), Button("Sparse checkout…", SparseCheckoutAsync), Button("Garbage collection…", GarbageCollectAsync)));
        tools.Children.Add(new TextBlock { Text = "Output", FontSize = 16 });
        _repositoryOutput = ReadOnlyText();
        _repositoryOutput.MinHeight = 360;
        tools.Children.Add(_repositoryOutput);
        return new ScrollViewer { Content = tools };
    }

    private TextBox _repositoryOutput = ReadOnlyText();
    private async Task ReflogAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        await ShowRepositoryDialogAsync(new ReflogDialog(repository, autoNormalize: _preferences.AutoNormaliseBranchName,
            replacement: _preferences.AutoNormaliseSymbol, afterSubmoduleUpdate: (owner, ct) => AfterSubmoduleUpdateAsync(repository, owner, ct)));
    }
    private Task ShowCommandAsync(string label, params string[] arguments) => RunAsync(label, async r =>
    {
        GitResult result = await r.RunAsync(arguments, Token);
        _repositoryOutput.Text = result.StandardOutput + result.StandardError;
    }, refresh: false);

    private async Task ExportArchiveAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new ArchiveDialog(_repository, CurrentRevision));
    }

    private async Task FormatPatchAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new FormatPatchDialog(_repository, CurrentRevision));
    }

    private async Task ApplyPatchAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new ApplyPatchDialog(_repository));
    }

    private async Task NotesAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new NoteDialog(_repository, CurrentRevision));
    }

    private async Task BisectAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        // GetSelectedRevisions() has no sort unless its caller requests one. Keep the selection collection's order.
        string[] selected = (_commits.SelectedItems?.OfType<GitCommit>() ?? []).Select(commit => commit.Hash).ToArray();
        await ShowRepositoryDialogAsync(new BisectDialog(_repository, selected));
    }

    private async Task UndoLastCommitAsync()
    {
        if (_repository is null || _busy || _repository.IsBare) { return; }
        GitRepository repository = _repository; Window owner = WorkflowOwner;
        bool resetStarted = false, resetCompleted = false;
        try
        {
            await RunAsync(T("Undo last commit"), async current =>
            {
                if (current != repository || !await UndoLastCommitConfirmationDialog.ShowAsync(owner, new BrowseCommandPreferencesStore(), Token)) { return; }
                Token.ThrowIfCancellationRequested();
                if (!owner.IsVisible || _repository != repository) { return; }
                resetStarted = true;
                GitResult result = await repository.UndoLastCommitAsync(Token);
                resetCompleted = true;
                AppendOutput(result.StandardOutput + result.StandardError);
            }, refresh: false);
        }
        finally
        {
            // Cancellation or a native failure can follow a real ref update; reread Git rather than simulate index/worktree effects.
            if (resetStarted && _repository == repository && !_lifetime.IsCancellationRequested)
            {
                Exception? failure = LastException; string? failureStatus = _state.Text;
                await RefreshAsync();
                if (!resetCompleted) { LastException = failure; _state.Text = failureStatus; }
            }
        }
    }

    private async Task SparseCheckoutAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new SparseCheckoutDialog(_repository));
    }

    private Window WorkflowOwner => _workflowViews.Values.Select(TopLevel.GetTopLevel).OfType<Window>().FirstOrDefault(window => window != this && window.IsVisible) ?? this;

    private async Task ShowRepositoryDialogAsync(RepositoryDialog dialog)
    {
        Window owner = WorkflowOwner;
        dialog.RequestedThemeVariant ??= owner.ActualThemeVariant;
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        try
        {
            await RunAsync(dialog.Title ?? "Open dialog", async _ =>
            {
                CancellationToken commandToken = Token;
                using CancellationTokenRegistration registration = commandToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
                commandToken.ThrowIfCancellationRequested();
                await dialog.ShowDialog(owner);
                commandToken.ThrowIfCancellationRequested();
            }, refresh: false);
        }
        finally
        {
            dialog.Close();
            if (dialog.HasRepositoryChanges && _repository is not null && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
    }

    private async Task GarbageCollectAsync()
    {
        if (await Dialogs.ConfirmAsync(this, "Optimize repository", "Run Git garbage collection with its normal retention policy?"))
        {
            await RunAsync("Garbage collection", async repository =>
            {
                GitResult result = await repository.GarbageCollectAsync(Token);
                AppendOutput(result.StandardOutput + result.StandardError);
            });
        }
    }
}
