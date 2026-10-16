using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class PullDialog
{
    private void UpdateSourceUrl()
    {
        if (UseUrl) { return; }
        _url.Text = _remotes.FirstOrDefault(remote => remote.Name == _remote.Text)?.FetchUrls.FirstOrDefault() ?? "";
    }

    private async Task BrowseSourceAsync()
    {
        string? folder = await ExportDialogControls.FolderAsync(this, T("Please select a source directory"));
        if (folder is not null) { _url.Text = folder; }
    }

    private Task ManageRemotesAsync() => RunAsync("Manage remotes", async token =>
    {
        RemotesDialog dialog = new(Repository, preselectRemote: All ? null : _remote.Text);
        dialog.Opened += async (_, _) => await dialog.InitializeAsync(token);
        await ShowOwnedAsync<object?>(this, dialog, token);
        if (!dialog.HasRepositoryChanges) { return; }
        MarkRepositoryChanged();
        _initializing = true;
        try
        {
            string? previous = _remote.Text;
            _remotes = await Repository.GetRemotesAsync(token); _references = await Repository.GetReferencesAsync(token);
            _remote.ItemsSource = new[] { AllRemotes }.Concat(_remotes.Select(remote => remote.Name)).ToArray();
            SetInputText(_remote, previous == AllRemotes ? AllRemotes : _remotes.FirstOrDefault(remote => remote.Name == previous)?.Name ?? _remotes.FirstOrDefault()?.Name ?? AllRemotes);
            _configuredRemote = (await Repository.GetConfigAsync(cancellationToken: token)).LastOrDefault(entry => entry.Key == "branch." + _status?.Branch + ".remote").Value;
            UpdateSourceUrl(); UpdateBranchChoices();
        }
        finally { _initializing = false; }
    });

    private async Task<string?> LocalFetchRefspecAsync(Window owner, string? branch, CancellationToken token)
    {
        string destination = _localBranch.Text!;
        if (string.IsNullOrWhiteSpace(branch))
        {
            if (!await Dialogs.ConfirmAsync(owner, T("Remote branch not specified"), T("Fetch remote branch") + " " + destination + "?")) { return null; }
            branch = destination;
        }
        string source = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch;
        foreach (string name in new[] { source, destination })
        {
            GitResult valid = await Repository.RunAsync(["check-ref-format", "refs/heads/" + name], token);
            if (!valid.Success) { throw new ArgumentException(T("Select a valid branch name.") + " " + name); }
        }
        string reference = "refs/heads/" + destination;
        if ((await Repository.GetWorktreesAsync(token)).Any(worktree => worktree.Branch == reference))
        { throw new InvalidOperationException(T("Cannot fetch into a branch checked out in a worktree.")); }
        if ((await Repository.GetReferencesAsync(token)).Any(item => item.Name == reference)
            && !await Dialogs.ConfirmAsync(owner, T("Reset branch"), T("Reset branch") + " " + destination + " " + T("to") + " " + source + "?")) { return null; }
        // Native Git retains its checked-out branch guards and ref transaction.
        // Resetting an existing destination is explicitly confirmed above.
        return "+refs/heads/" + source + ":" + reference;
    }
}
