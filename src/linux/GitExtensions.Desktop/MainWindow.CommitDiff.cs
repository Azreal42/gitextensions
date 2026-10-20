using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly ResolvedCommitDiffSource? _commitDiffSource;
    private GitCommit? _displayedCommit;
    private readonly BrowserRead _commitDiffOpenRead = new();
    private bool _commitDiffHasRepositoryChanges;
    private readonly HashSet<MainWindow> _modelessCommitDiffs = [];
    internal IReadOnlyCollection<MainWindow> ModelessCommitDiffWindows => _modelessCommitDiffs.ToArray();
    // An explicit lifetime is useful for embedders/headless hosts; production uses the real App lifetime.
    internal IClassicDesktopStyleApplicationLifetime? ModelessApplicationLifetime { get; init; }
    internal bool CommitDiffHasRepositoryChanges => _commitDiffHasRepositoryChanges;
    internal string? CommitDiffRevision => _commitDiffSource?.Commit.Hash;
    private bool IsCommitDiff => WindowKind == MainWindowKind.CommitDiff;
    private bool IsStandaloneDiff => IsRevisionLog || IsCommitDiff;
    private GitCommit? CurrentDisplayedCommit => IsCommitDiff ? _displayedCommit : _commits.SelectedItem as GitCommit;
    private GitCommit? CommitForDiff(string hash) => IsCommitDiff && _displayedCommit?.Hash == hash ? _displayedCommit : _history.FirstOrDefault(commit => commit.Hash == hash);

    private Control BuildCommitDiffLayout()
    {
        _commitInfo.ShowRevisionLinks = false; _commitInfo.ShowBranchesAsLinks = false;
        Grid files = (Grid)BuildCommitFilesView(); files.ColumnDefinitions[0].Width = new GridLength(260);
        UseStandaloneDiffTab(files);
        Grid layout = new() { Name = "CommitDiffLayout", RowDefinitions = new("200,4,*") };
        layout.Children.Add(_commitInfo);
        GridSplitter split = new() { Name = "CommitDiffInformationSplitter", Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 1); layout.Children.Add(split); Grid.SetRow(_tabs, 2); layout.Children.Add(_tabs);
        InitializeContinuousScroll();
        Opened += (_, _) => ModalWindowActivation.AfterMapping(this, () => !_lifetime.IsCancellationRequested);
        return WithStandaloneDiffStatus(layout);
    }

    private async Task InitializeCommitDiffAsync()
    {
        if (_commitDiffSource is null || _busy || _lifetime.IsCancellationRequested) { return; }
        Func<bool>? restore = _revisionFilesView?.CaptureSelectedRowFocus();
        _repository = _commitDiffSource.Repository;
        await RunAsync("Commit diff", repository => ReloadCommitDiffDataAsync(repository, Token), refresh: false);
        if (_lifetime.IsCancellationRequested || !IsVisible || !IsEffectivelyEnabled || OwnedWindows.Any(child => child.IsVisible)) { return; }
        if (restore?.Invoke() == true) { return; }
        if (IsActive && (FocusManager?.GetFocusedElement() is not Control current || current is Window
            || current == _revisionFiles || TopLevel.GetTopLevel(current) != this)) { FocusCommitDiffFiles(); }
    }

    private async Task ReloadCommitDiffDataAsync(GitRepository repository, CancellationToken token)
    {
        if (_commitDiffSource is null || !ReferenceEquals(repository, _commitDiffSource.Repository)) { return; }
        GitCommitDetails details = await repository.GetCommitDetailsAsync(_commitDiffSource.Commit.Hash, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested) { return; }
        _displayedCommit = CommitDiffWindow.ToCommit(details); UpdateCommitInfo(_displayedCommit);
        Title = T("Diff") + " - " + details.Hash[..Math.Min(9, details.Hash.Length)] + " - " + details.Author.Date.LocalDateTime.ToString("g")
            + " - " + details.Author.Name + " - " + repository.Path;
        _repositorySummary = details.Subject;
        await LoadCommitFilesAsync(repository, details.Hash, cancellationToken: token);
    }

    private void FocusCommitDiffFiles()
    {
        _revisionFiles.UpdateLayout();
        if (_revisionFiles.SelectedItem is TreeViewItem row) { row.BringIntoView(); row.Focus(); }
        else { _revisionFiles.Focus(); }
    }

    internal Task ShowCommitDiffAsync(GitCommit commit) => ReadBrowserAsync(_commitDiffOpenRead, "Commit diff", async (repository, token) =>
    {
        // Modeless windows belong to the application root lifetime, not the launcher or its native Owner chain.
        if ((ModelessApplicationLifetime ?? Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not MainWindow root
            || root._lifetime.IsCancellationRequested || !root.IsVisible)
        { throw new InvalidOperationException("An application root is required to open an independent commit diff."); }
        MainWindow child = await CommitDiffWindow.CreateAsync(repository, commit.Hash, token);
        if (token.IsCancellationRequested || root._lifetime.IsCancellationRequested || !IsVisible)
        { child.Close(); token.ThrowIfCancellationRequested(); return; }
        child.RequestedThemeVariant = ActualThemeVariant;
        root.RegisterModelessCommitDiff(child, this);
    });

    private void RegisterModelessCommitDiff(MainWindow child, MainWindow launcher)
    {
        WindowWork work = TrackWindowWork(); _modelessCommitDiffs.Add(child);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WeakReference<MainWindow> origin = new(launcher);
        OperationFocus? captured = launcher.CaptureOperationFocus();
        GitRepository? focusRepository = captured?.Repository; string? focusCommit = captured?.CommitHash;
        WeakReference<Control>? focusInput = captured?.Input is Control capturedInput ? new(capturedInput) : null;
        bool hasFocusOrigin = captured is not null, restoreFocus = false;
        child.Closing += (_, _) => { restoreFocus = child.IsActive; };
        child.Closed += (_, _) => closed.TrySetResult();
        CancellationTokenRegistration close = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(child.Close));
        _ = RetireModelessCommitDiffAsync();
        try { child.ShowInTaskbar = true; child.Show(); }
        catch { child.Close(); closed.TrySetResult(); throw; }
        async Task RetireModelessCommitDiffAsync()
        {
            try
            {
                await closed.Task; await child.WindowWorkCompletion;
                if (restoreFocus && !_lifetime.IsCancellationRequested && origin.TryGetTarget(out MainWindow? window)
                    && window.IsVisible && window.IsEffectivelyEnabled && !window.OwnedWindows.Any(owned => owned.IsVisible))
                {
                    Control? restoredInput = null;
                    if (focusInput is not null && focusInput.TryGetTarget(out Control? target)) { restoredInput = target; }
                    if (hasFocusOrigin) { window.RestoreOperationFocus(new(focusRepository, restoredInput, focusCommit)); }
                }
            }
            catch (Exception error) { AppendOutput("Closing commit diff: " + error.Message); }
            finally { close.Dispose(); _modelessCommitDiffs.Remove(child); work.Dispose(); }
        }
    }
}
