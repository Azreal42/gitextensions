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
        => _changes.SelectedItems?.Cast<GitChange>()
            .SelectMany(change => includeOriginalPath && change.OriginalPath is not null ? new[] { change.Path, change.OriginalPath } : new[] { change.Path })
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];

    private async Task CloneAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Clone", ("Repository URL or local path", ""), ("Destination directory", ""));
        if (values is null) { return; }
        GitRepository? cloned = null;
        await RunAsync("Clone", async _ => cloned = await GitRepository.CloneAsync(values[0], values[1], cancellationToken: Token), requireRepository: false, refresh: false);
        if (cloned is not null) { await OpenRepositoryAsync(cloned.Path); }
    }

    private async Task InitializeAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Initialize repository", ("Directory", _repositoryPath.Text ?? ""), ("Initial branch", "main"));
        if (values is null) { return; }
        GitRepository? initialized = null;
        await RunAsync("Initialize", async _ => initialized = await GitRepository.InitAsync(values[0], values[1], cancellationToken: Token), requireRepository: false, refresh: false);
        if (initialized is not null) { await OpenRepositoryAsync(initialized.Path); }
    }

    private Task StageAsync(bool all) => RunAsync("Stage", repository => all ? repository.StageAllAsync(Token) : repository.StageAsync(SelectedPaths(includeOriginalPath: false), Token));

    private Task UnstageAsync(bool all) => RunAsync("Unstage", repository => repository.UnstageAsync(all
        ? (_changes.ItemsSource?.Cast<GitChange>().Where(c => c.IsStaged).SelectMany(c => c.OriginalPath is null ? new[] { c.Path } : new[] { c.Path, c.OriginalPath }).Distinct(StringComparer.Ordinal) ?? [])
        : SelectedPaths(), Token));

    private Task LoadSelectedDiffAsync()
    {
        if (_refreshing || _changes.SelectedItem is not GitChange change) { return Task.CompletedTask; }
        return RunAsync("File diff", async repository =>
        {
            bool staged = change.IsStaged && (_stagedDiff.IsChecked == true || !change.IsModified);
            if (_stagedDiff.IsChecked != staged) { _stagedDiff.IsChecked = staged; }
            await LoadChangedFileContentAsync(repository, change, staged);
            _selectedPatch = GitPatch.Parse(_changesDiff.Text ?? "").FirstOrDefault();
            _hunks.ItemsSource = _selectedPatch?.Hunks;
        }, refresh: false);
    }

    private async Task ApplyHunksAsync(bool reverse)
    {
        if (_selectedPatch is null || _hunks.SelectedItems?.Count is not > 0) { _state.Text = "Select diff hunks first"; return; }
        if ((_stagedDiff.IsChecked == true) != reverse) { _state.Text = reverse ? "Show staged changes before unstaging hunks" : "Show unstaged changes before staging hunks"; return; }
        string patch = _selectedPatch.SelectHunks(_hunks.SelectedItems.Cast<GitPatchHunk>().Select(h => h.Index));
        await RunAsync(reverse ? "Unstage hunks" : "Stage hunks", r => r.ApplyPatchAsync(patch, staged: true, reverse: reverse, cancellationToken: Token));
        await LoadSelectedDiffAsync();
    }

    private async Task ApplyLinesAsync(bool reverse)
    {
        if (_changesDiffView is null || _changesDiffView.SelectionLength == 0 || _changes.SelectedItem is not GitChange change) { _state.Text = "Select changed lines in the diff first"; return; }
        if ((_stagedDiff.IsChecked == true) != reverse) { _state.Text = reverse ? "Show staged changes before unstaging lines" : "Show unstaged changes before staging lines"; return; }
        string? patch = GitPatch.SelectLines(_changesDiffView.Text, _changesDiffView.SelectionStart, _changesDiffView.SelectionLength,
            staged: reverse, reverse: reverse, isNewFile: change.IndexStatus == 'A', isRenamed: change.OriginalPath is not null);
        if (patch is null) { _state.Text = "The selected text contains no changed lines"; return; }
        await RunAsync(reverse ? "Unstage lines" : "Stage lines", repository => repository.ApplyPatchAsync(patch, staged: true, reverse: reverse, cancellationToken: Token));
        await LoadSelectedDiffAsync();
    }

    private static string DisplayBytes(byte[] bytes, bool truncated = false)
    {
        if (bytes.Take(8192).Contains((byte)0)) { return $"Binary file ({bytes.Length:N0} bytes{(truncated ? "+" : "")})\n{Convert.ToHexString(bytes.AsSpan(0, Math.Min(256, bytes.Length)))}"; }
        return System.Text.Encoding.UTF8.GetString(bytes) + (truncated ? "\n[Preview limited to 2 MB]" : "");
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
        string[] paths = _changes.SelectedItems?.Cast<GitChange>().Where(c => c.IsConflict).Select(c => c.Path).ToArray() ?? [];
        if (paths.Length == 0) { _state.Text = "Select conflicted files first"; return; }
        string[]? values = await Dialogs.PromptAsync(this, "Resolve conflicts", ("Keep version: ours or theirs (leave blank to stage your manual edits)", ""));
        if (values is null) { return; }
        if (values[0] is not ("" or "ours" or "theirs")) { throw new ArgumentException("Choose ours, theirs, or leave blank."); }
        if (values[0] != "" && !await Dialogs.ConfirmAsync(this, "Replace conflicted files", $"Replace {paths.Length} files with the {values[0]} version?")) { return; }
        await RunAsync("Resolve conflicts", async r =>
        {
            if (values[0] != "") { await ExecuteAsync(r, ["checkout", "--" + values[0], "--", .. paths]); }
            await r.StageAsync(paths, Token);
        });
    }

    private async Task CreateBranchAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Create branch", ("Name", ""), ("Start point (blank for HEAD)", ""));
        if (values is not null) { await RunAsync("Create branch", r => r.CreateBranchAsync(values[0], Optional(values[1]), cancellationToken: Token)); }
    }

    private async Task CheckoutAsync()
    {
        GitReference reference = SelectedReference();
        if (reference.IsRemote)
        {
            string[]? values = await Dialogs.PromptAsync(this, "Track remote branch", ("Local branch", reference.ShortName[(reference.ShortName.IndexOf('/') + 1)..]));
            if (values is not null) { await RunAsync("Track branch", r => ExecuteAsync(r, "switch", "-c", Value(values[0]), "--track", reference.ShortName)); }
            return;
        }

        await RunAsync("Checkout", r => r.CheckoutAsync(reference.IsTag ? reference.Hash : reference.ShortName, detached: reference.IsTag, cancellationToken: Token));
    }

    private async Task DeleteReferenceAsync()
    {
        GitReference reference = SelectedReference();
        if (reference.IsRemote) { _state.Text = "Remote deletion requires Push with an explicit refspec"; return; }
        if (!await Dialogs.ConfirmAsync(this, "Delete reference", $"Delete {reference.ShortName}? Unmerged local branches will be preserved.")) { return; }
        await RunAsync("Delete reference", r => reference.IsTag ? r.DeleteTagAsync(reference.ShortName, Token) : r.DeleteBranchAsync(reference.ShortName, cancellationToken: Token));
    }

    private async Task RenameBranchAsync()
    {
        GitReference reference = SelectedReference();
        if (!reference.IsBranch) { _state.Text = "Select a local branch"; return; }
        string[]? values = await Dialogs.PromptAsync(this, "Rename branch", ("New name", reference.ShortName));
        if (values is not null) { await RunAsync("Rename branch", r => r.RenameBranchAsync(reference.ShortName, values[0], Token)); }
    }

    private async Task CreateTagAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Create tag", ("Name", ""), ("Revision", "HEAD"), ("Annotation (blank for lightweight tag)", ""));
        if (values is not null) { await RunAsync("Create tag", r => r.CreateTagAsync(values[0], Optional(values[1]), Optional(values[2]), Token)); }
    }

    private async Task MergeAsync()
    {
        GitReference reference = SelectedReference();
        if (!await Dialogs.ConfirmAsync(this, "Merge", $"Merge {reference.ShortName} into the current branch?")) { return; }
        await RunAsync("Merge", r => r.MergeAsync(reference.Hash, cancellationToken: Token));
    }

    private async Task RebaseAsync()
    {
        GitReference reference = SelectedReference();
        if (!await Dialogs.ConfirmAsync(this, "Rebase", $"Replay the current branch onto {reference.ShortName}? Commit IDs will change.")) { return; }
        await RunAsync("Rebase", r => r.RebaseAsync(reference.Hash, Token));
    }

    private async Task InteractiveRebaseAsync()
    {
        if (_repository is null || _busy) { return; }
        string[]? values = await Dialogs.PromptAsync(this, "Interactive rebase", ("Upstream revision", (_references.SelectedItem as GitReference)?.ShortName ?? "HEAD~1"));
        if (values is null) { return; }
        GitRebasePlan? plan = null;
        await RunAsync("Prepare rebase", async repository => plan = await repository.GetRebasePlanAsync(values[0], Token), refresh: false);
        if (plan is null) { return; }
        GitRebasePlan? selected = await RebasePlanDialog.ShowAsync(this, plan);
        if (selected is null) { return; }
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "GitExtensions.exe" : "GitExtensions");
        await RunAsync("Interactive rebase", repository => repository.InteractiveRebaseAsync(selected, executable, Token));
    }

    private async Task ManageOperationAsync(GitOperationAction action)
    {
        string[]? values = await Dialogs.PromptAsync(this, action.ToString(), ("Operation: merge, rebase, cherry-pick or revert", "rebase"));
        if (values is not null) { await RunAsync(action.ToString(), r => r.ManageOperationAsync(values[0], action, Token)); }
    }

    private async Task CherryPickAsync()
    {
        GitCommit commit = SelectedCommit();
        if (await Dialogs.ConfirmAsync(this, "Cherry-pick", $"Apply commit {commit.Subject} to the current branch?")) { await RunAsync("Cherry-pick", r => r.CherryPickAsync(commit.Hash, Token)); }
    }

    private async Task RevertAsync()
    {
        GitCommit commit = SelectedCommit();
        if (await Dialogs.ConfirmAsync(this, "Revert commit", $"Create a commit that undoes {commit.Subject}?")) { await RunAsync("Revert", r => r.RevertAsync(commit.Hash, Token)); }
    }

    private async Task ResetAsync()
    {
        GitCommit commit = SelectedCommit();
        string[]? values = await Dialogs.PromptAsync(this, "Reset branch", ("Mode: soft, mixed or hard", "mixed"));
        if (values is null) { return; }
        if (values[0] is not ("soft" or "mixed" or "hard")) { throw new ArgumentException("Reset mode must be soft, mixed or hard."); }
        if (!await Dialogs.ConfirmAsync(this, "Reset branch", $"Move the current branch to {commit.Subject}?{(values[0] == "hard" ? " Staged and unstaged changes will be lost." : " The branch history will change.")}")) { return; }
        await RunAsync("Reset", r => r.ResetAsync(commit.Hash, Enum.Parse<GitResetMode>(values[0], ignoreCase: true), Token));
    }

    private async Task CompareAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Compare revisions", ("From", "HEAD~1"), ("To", "HEAD"));
        if (values is not null)
        {
            await RunAsync("Compare", async r =>
            {
                string from = await r.ResolveRevisionAsync(values[0], Token);
                string to = await r.ResolveRevisionAsync(values[1], Token);
                _revisionFiles.ItemsSource = null;
                _revisionChanges = null;
                _diffParent.IsVisible = false;
                _commitContent?.ClearImage();
                _diff.Text = (await r.RunAsync(["diff", "--no-ext-diff", "--no-textconv", from, to, "--"], Token)).StandardOutput;
            }, refresh: false);
        }
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

    private async Task PullAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Pull", ("Remote (blank for upstream)", (_remotes.SelectedItem as GitRemote)?.Name ?? ""), ("Branch (blank for default)", ""), ("Mode: ff-only or rebase", "ff-only"));
        if (values is null) { return; }
        if (values[2] is not ("ff-only" or "rebase")) { throw new ArgumentException("Pull mode must be ff-only or rebase."); }
        await RunAsync("Pull", r => r.PullAsync(Optional(values[0]), Optional(values[1]), values[2] == "rebase", Token));
    }

    private async Task PushAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Push", ("Remote (blank for upstream)", (_remotes.SelectedItem as GitRemote)?.Name ?? ""), ("Branch or refspec (blank for default)", ""), ("Set upstream: yes or no", "no"), ("Force with lease: yes or no", "no"));
        if (values is null) { return; }
        if (values[2] is not ("yes" or "no") || values[3] is not ("yes" or "no")) { throw new ArgumentException("Choose yes or no for push options."); }
        if ((values[3] == "yes" || values[1].StartsWith(':')) && !await Dialogs.ConfirmAsync(this, "Update remote history", "This push may replace remote history or delete a remote ref. Continue?")) { return; }
        await RunAsync("Push", r => r.PushAsync(Optional(values[0]), Optional(values[1]), values[2] == "yes", values[3] == "yes", Token));
    }

    private Task BrowseRemoteAsync()
    {
        Uri? uri = SelectedRemote().BrowseUri;
        if (uri is null) { throw new InvalidOperationException("This remote has no recognized web URL."); }
        Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { uri.AbsoluteUri }, UseShellExecute = false });
        return Task.CompletedTask;
    }

    private async Task StashAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Stash changes", ("Message", ""));
        if (values is not null) { await RunAsync("Stash", r => r.StashAsync(Optional(values[0]), cancellationToken: Token)); }
    }

    private Task ApplyStashAsync(bool pop)
    {
        GitStash stash = SelectedStash();
        return RunAsync(pop ? "Pop stash" : "Apply stash", r => r.ApplyStashAsync(stash.Reference, pop, cancellationToken: Token));
    }

    private async Task DropStashAsync()
    {
        GitStash stash = SelectedStash();
        if (await Dialogs.ConfirmAsync(this, "Delete stash", $"Permanently delete {stash.Subject}?")) { await RunAsync("Delete stash", r => r.DropStashAsync(stash.Reference, Token)); }
    }

    private Task ShowStashAsync()
    {
        GitStash stash = SelectedStash();
        return RunAsync("Stash diff", async r =>
        {
            _fileContent?.ClearImage();
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

    private Task ShowFileAsync(string path) => RunAsync("File contents", async r =>
    {
        _fileContent?.ClearImage();
        GitTreeEntry? entry = (await r.GetTreeAsync(CurrentRevision, path, Token)).FirstOrDefault(item => item.Path == path);
        if (entry?.IsSubmodule == true) { _fileView.Text = $"Submodule: {path}\nCommit: {entry.Hash}"; return; }
        byte[] bytes = await r.GetFileBytesAsync(path, CurrentRevision, Token);
        if (FileContentView.IsImagePath(path) && _fileContent is not null) { _fileContent.ShowImageOrHex(path, bytes); }
        else { _fileView.Text = DisplayBytes(bytes); }
    }, refresh: false);

    private Task BlameAsync() => RunAsync("Blame", async r =>
    {
        _fileContent?.ClearImage();
        IReadOnlyList<GitBlameLine> lines = await r.GetBlameAsync(_filePath.Text ?? "", CurrentRevision, Token);
        _fileView.Text = string.Join('\n', lines.Select(line => $"{line.Hash[..Math.Min(9, line.Hash.Length)]} {line.AuthorName,-18} {line.LineNumber,5}  {line.Text}"));
    }, refresh: false);

    private Task FileHistoryAsync() => RunAsync("File history", async r =>
    {
        _fileContent?.ClearImage();
        _fileView.Text = (await r.RunAsync(["log", "--follow", "--format=fuller", "--stat", CurrentRevision, "--", _filePath.Text ?? ""], Token)).StandardOutput;
    }, refresh: false);

    private async Task SearchAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, "Search repository", ("Pattern", ""));
        if (values is null) { return; }
        await RunAsync("Search", async r =>
        {
            _fileContent?.ClearImage();
            _fileView.Text = string.Join('\n', (await r.SearchAsync(values[0], revision: CurrentRevision, cancellationToken: Token)).Select(match => $"{match.Path}:{match.LineNumber}: {match.Text}"));
        }, refresh: false);
    }

    private async Task SetConfigurationAsync(GitConfigScope scope)
    {
        string selected = _configuration.SelectedItem as string ?? "";
        int separator = selected.IndexOf(" = ", StringComparison.Ordinal);
        string[]? values = await Dialogs.PromptAsync(this, $"Set {scope.ToString().ToLowerInvariant()} setting", ("Key", separator < 0 ? "user.name" : selected[..separator]), ("Value", separator < 0 ? "" : selected[(separator + 3)..]));
        if (values is not null) { await RunAsync("Configuration", r => r.SetConfigAsync(values[0], values[1], scope, Token)); }
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
        if (_repository is null) { return Task.CompletedTask; }
        string[] names = ["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"];
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        string? executable = names.SelectMany(name => directories.Select(directory => Path.Combine(directory, name))).FirstOrDefault(File.Exists);
        if (executable is null) { throw new InvalidOperationException("No terminal found on PATH."); }
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = _repository.Path, UseShellExecute = false });
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
        tools.Children.Add(Row(Button("Reflog", () => ShowCommandAsync("Reflog", "reflog", "--date=iso")), Button("Verify objects", VerifyObjectsAsync), Button("Repository statistics", StatisticsAsync)));
        tools.Children.Add(Row(Button("Export archive…", ExportArchiveAsync), Button("Create patches…", FormatPatchAsync), Button("Apply patch…", ApplyPatchAsync), Button("Notes…", NotesAsync)));
        tools.Children.Add(Row(Button("Bisect…", BisectAsync), Button("Sparse checkout…", SparseCheckoutAsync), Button("Garbage collection…", GarbageCollectAsync)));
        tools.Children.Add(new TextBlock { Text = "Output", FontSize = 16 });
        _repositoryOutput = ReadOnlyText();
        _repositoryOutput.MinHeight = 360;
        tools.Children.Add(_repositoryOutput);
        return new ScrollViewer { Content = tools };
    }

    private TextBox _repositoryOutput = ReadOnlyText();
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
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new BisectDialog(_repository));
    }

    private async Task SparseCheckoutAsync()
    {
        if (_repository is null || _busy) { return; }
        await ShowRepositoryDialogAsync(new SparseCheckoutDialog(_repository));
    }

    private Window WorkflowOwner => _workflowViews.Values.Select(TopLevel.GetTopLevel).OfType<Window>().FirstOrDefault(window => window != this && window.IsVisible) ?? this;

    private async Task ShowRepositoryDialogAsync(RepositoryDialog dialog)
    {
        await RunAsync(dialog.Title ?? "Open dialog", _ => dialog.InitializeAsync(), refresh: false);
        await dialog.ShowDialog(WorkflowOwner);
        if (dialog.HasRepositoryChanges) { await RefreshAsync(); }
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
