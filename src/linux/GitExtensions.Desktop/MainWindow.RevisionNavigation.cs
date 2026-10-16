using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly List<string> _revisionNavigation = [];
    private int _revisionNavigationIndex = -1;
    private GitRepository? _revisionNavigationRepository;
    private bool _movingRevisionNavigation;
    private readonly BrowserRead _goToCommitRead = new();

    private MenuItem BuildRevisionNavigateMenu() => BuildHistoryNavigateMenu(context: true);

    private bool HasRevisionParent() => _commits.SelectedItem is GitCommit { Parents.Count: > 0 };

    private void RememberRevisionNavigation()
    {
        if (_refreshing || _movingRevisionNavigation || _commits.SelectedItem is not GitCommit commit) { return; }
        if (_revisionNavigationRepository != _repository)
        { _revisionNavigation.Clear(); _revisionNavigationIndex = -1; _revisionNavigationRepository = _repository; }
        if (_revisionNavigationIndex >= 0 && _revisionNavigation[_revisionNavigationIndex] == commit.Hash) { return; }
        _revisionNavigation.RemoveRange(_revisionNavigationIndex + 1, _revisionNavigation.Count - _revisionNavigationIndex - 1);
        _revisionNavigation.Add(commit.Hash); _revisionNavigationIndex = _revisionNavigation.Count - 1;
    }

    private async Task GoToRevisionAsync()
    {
        if (_busy || _goToCommitRead.Current is not null || _repository is not GitRepository repository || _lifetime.IsCancellationRequested) { return; }
        LastException = null;
        GoToCommitDialog dialog = new(repository) { RequestedThemeVariant = ActualThemeVariant };
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _goToCommitRead.Current = read;
        dialog.Opened += async (_, _) => await dialog.InitializeAsync(read.Token);
        using CancellationTokenRegistration close = read.Token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try { await dialog.ShowDialog(WorkflowOwner); }
        finally
        {
            dialog.Close(); await dialog.Completion;
            if (_goToCommitRead.Current == read) { _goToCommitRead.Current = null; }
        }
        if (read.IsCancellationRequested || _repository != repository || _lifetime.IsCancellationRequested) { return; }
        if (!dialog.WasAccepted) { _commits.Focus(); return; }
        if (dialog.SelectedCommitHash is not string hash)
        {
            LastException = dialog.ResolutionError;
            await ShowGoToCommitErrorAsync("_noRevisionFoundError", "No revision found.");
        }
        else if (!_history.Any(commit => commit.Hash == hash)) { await ShowFilteredRevisionErrorAsync(hash); }
        else { SelectVisibleRevision(hash); }
    }

    private Task ShowFilteredRevisionErrorAsync(string hash)
    {
        string message = _translations.Translate("MessageBoxes", "_cannotFindRevisionFilter", "Text",
            "Revision \"{0}\" is not visible in the revision grid. Remove the revision filter.");
        return ShowGoToCommitErrorAsync(null, string.Format(System.Globalization.CultureInfo.CurrentCulture, message, hash[..Math.Min(7, hash.Length)]));
    }

    private async Task ShowGoToCommitErrorAsync(string? key, string source)
    {
        string message = key is null ? source : _translations.Translate("MessageBoxes", key, "Text", source);
        _state.Text = message;
        Window error = new() { Name = "GoToCommitError", Title = _translations.Translate("MessageBoxes", "_cannotFindRevisionCaption", "Text", "Cannot find revision"),
            Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button accept = new() { Name = "CloseGoToCommitError", Content = T("OK"), IsDefault = true, IsCancel = true, MinWidth = 75 };
        accept.Click += (_, _) => error.Close();
        error.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            new TextBlock { Name = "GoToCommitErrorMessage", Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { accept } }
        } };
        await error.ShowDialog(WorkflowOwner);
        if (!_lifetime.IsCancellationRequested) { _commits.Focus(); }
    }

    private async Task NavigateToRevisionAsync(string revision)
    {
        GitRepository? repository = _repository; string? hash = null;
        await RunAsync("Go to commit", async r => hash = await r.ResolveRevisionAsync(revision, Token), refresh: false);
        if (_repository != repository || hash is null) { return; }
        SelectVisibleRevision(hash);
    }

    private void SelectVisibleRevision(string hash, bool focus = true)
    {
        GitCommit? row = _history.FirstOrDefault(commit => commit.Hash == hash);
        if (row is null) { _state.Text = T("The revision is not visible with the current history filters.") + " " + hash; return; }
        _commits.SelectedItems?.Clear(); _commits.SelectedItem = row; _commits.ScrollIntoView(row);
        if (focus) { _commits.Focus(); }
    }

    private async Task NavigateToMergeBaseAsync()
    {
        GitCommit[] selected = SelectedComparisonCommits();
        if (selected.Length == 0) { return; }
        string? hash = null; GitRepository? repository = _repository;
        await RunAsync("Merge base", async r =>
        {
            string[] revisions = selected.Length == 1 ? [selected[0].Hash, "HEAD"] : selected.Select(commit => commit.Hash).ToArray();
            GitResult result = await r.RunAsync(new[] { "merge-base", "--octopus" }.Concat(revisions).ToArray(), Token);
            if (!result.Success) { throw new GitCommandException(["merge-base"], result); }
            hash = result.StandardOutput.Trim();
        }, refresh: false);
        if (_repository == repository && !string.IsNullOrEmpty(hash)) { SelectVisibleRevision(hash); }
    }

    private async Task NavigateRevisionHistoryAsync(int direction)
    {
        int next = _revisionNavigationIndex + direction;
        if (_revisionNavigationRepository != _repository || next < 0 || next >= _revisionNavigation.Count) { return; }
        _movingRevisionNavigation = true;
        try
        {
            _revisionNavigationIndex = next;
            string hash = _revisionNavigation[next];
            if (_history.Any(commit => commit.Hash == hash)) { SelectVisibleRevision(hash); }
            else { await ShowFilteredRevisionErrorAsync(hash); }
        }
        finally { _movingRevisionNavigation = false; RefreshRevisionContextMenu(); }
    }

    private Task SearchRevisionAsync(int direction)
    {
        string text = _quickHistoryFilter.Text ?? "";
        GitCommit[] rows = _history.ToArray();
        if (text.Length == 0 || rows.Length == 0) { FocusHistoryFilter(); return Task.CompletedTask; }
        int index = Array.FindIndex(rows, commit => commit.Hash == CurrentRevision);
        for (int step = 1; step <= rows.Length; step++)
        {
            GitCommit candidate = rows[((index + direction * step) % rows.Length + rows.Length) % rows.Length];
            if (candidate.Subject.Contains(text, StringComparison.CurrentCultureIgnoreCase) || candidate.Hash.Contains(text, StringComparison.OrdinalIgnoreCase)
                || candidate.AuthorName.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            { SelectVisibleRevision(candidate.Hash); break; }
        }
        return Task.CompletedTask;
    }

    private MenuItem BuildRevisionViewMenu() => BuildHistoryViewMenu(context: true);
}
