using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original four-revision assistant for resolving a single unmerged gitlink.</summary>
public sealed partial class SubmoduleConflictDialog : RepositoryDialog
{
    private readonly string _filename;
    private readonly Func<string, CancellationToken, Task> _openBrowseAsync;
    private GitSubmoduleConflictSnapshot? _snapshot;
    private Window? _closingOwner;
    private bool _invalidated;

    public GitSubmoduleConflictSnapshot? Snapshot => _snapshot;
    public GitSubmoduleConflictStageResult? LastStageResult { get; private set; }
    public bool Resolved { get; private set; }

    public SubmoduleConflictDialog(GitRepository repository, string path,
        Func<string, CancellationToken, Task>? openBrowseAsync = null) : base(repository, "Submodule conflict")
    {
        ArgumentNullException.ThrowIfNull(path);
        _filename = path; _openBrowseAsync = openBrowseAsync ?? BrowseApplicationLauncher.OpenAsync;
        Name = "SubmoduleConflictDialog"; Title = Original("$this", "Submodule conflict");
        Width = MinWidth = MaxWidth = 595; Height = MinHeight = MaxHeight = 254; CanResize = false;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _open = OriginalButton("btOpenSubmodule", "Open submodule", OpenAsync, "FolderSubmodule");
        _open.IsDefault = true; _open.TabIndex = 12;
        _checkout = OriginalButton("btCheckoutBranch", "Checkout Branch", CheckoutAsync); _checkout.TabIndex = 17;
        _stage = OriginalButton("btStageCurrent", "Stage Current", StageAsync); _stage.TabIndex = 16;
        _refresh = OriginalButton("btRefresh", "", RefreshAsync); _refresh.Content = ResetDialogControls.Icon("ReloadRevisions"); _refresh.TabIndex = 13;
        ToolTip.SetTip(_refresh, T("Refresh")); _path.Text = path; ToolTip.SetTip(_path, path);
        BuildOriginalLayout(); UpdateActions();
        Opened += (_, _) => { if (_snapshot is not null && !IsBusy) { _open.Focus(); } };
        Closing += (_, args) => { if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; } };
        Closed += (_, _) => { if (_closingOwner?.IsVisible == true) { Dispatcher.UIThread.Post(_closingOwner.Close); } };
    }

    public override Task InitializeAsync() => InitializeAsync(CancellationToken.None);

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunAsync("Submodule conflict", async token =>
        {
            GitSubmoduleConflictSnapshot snapshot = _snapshot is null
                ? await Repository.GetSubmoduleConflictAsync(_filename, token)
                : await Repository.RefreshSubmoduleConflictAsync(_snapshot, token);
            SetSnapshot(snapshot);
        }, cancellationToken: cancellationToken);
        if (IsVisible && !IsBusy) { _open.Focus(); }
    }

    private void SetSnapshot(GitSubmoduleConflictSnapshot snapshot)
    {
        _snapshot = snapshot; _invalidated = false;
        string deleted = Original("_deleted", "deleted");
        _base.Text = snapshot.Base ?? deleted; _local.Text = snapshot.Local ?? deleted;
        _remote.Text = snapshot.Remote ?? deleted; _current.Text = snapshot.Current ?? "";
        if (snapshot.ValidationError is string error) { ReportError(new InvalidOperationException(error)); }
        else { Status.Text = ""; }
    }

    private async Task RefreshCurrentAsync(GitSubmoduleConflictSnapshot expected, CancellationToken token)
    {
        try { SetSnapshot(await Repository.RefreshSubmoduleConflictAsync(expected, token)); }
        catch { _invalidated = true; throw; }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy || _snapshot is not { } expected) { return; }
        await RunAsync("Refresh", token => RefreshCurrentAsync(expected, token));
        if (IsVisible && !IsBusy) { _refresh.Focus(); }
    }

    private async Task OpenAsync()
    {
        if (IsBusy || _invalidated || _snapshot is not { CanOpen: true } expected) { return; }
        await RunAsync("Open submodule", async token =>
        {
            GitRepository module = await Repository.OpenSubmoduleConflictRepositoryAsync(expected, token);
            token.ThrowIfCancellationRequested(); await _openBrowseAsync(module.Path, token); Status.Text = "";
        });
        if (IsVisible && !IsBusy) { _open.Focus(); }
    }

    private async Task StageAsync()
    {
        if (IsBusy || _invalidated || _snapshot is not { CanStageCurrent: true }) { return; }
        await RunAsync(string.Format(System.Globalization.CultureInfo.CurrentCulture, Original("_stageFilename", "Stage {0}"), _filename), StageCurrentAsync);
        if (IsVisible && !IsBusy) { _stage.Focus(); }
    }

    private async Task StageCurrentAsync(CancellationToken token)
    {
        GitSubmoduleConflictSnapshot expected = _snapshot ?? throw new InvalidOperationException("Load the submodule conflict first.");
        token.ThrowIfCancellationRequested(); MarkRepositoryChanged();
        LastStageResult = await Repository.StageSubmoduleConflictCurrentAsync(expected, token);
        if (!LastStageResult.Result.Success) { throw new GitCommandException(["add", "--", _filename], LastStageResult.Result); }
        if (!LastStageResult.IsResolved) { throw new InvalidOperationException(LastStageResult.ValidationError ?? "Git did not resolve the selected submodule conflict."); }
        token.ThrowIfCancellationRequested(); Resolved = true; Status.Text = ""; Close();
    }

    private async Task CheckoutAsync()
    {
        if (IsBusy || _invalidated || _snapshot is not { CanCheckoutBranch: true } expected) { return; }
        await RunAsync("Checkout Branch", async token =>
        {
            GitRepository module = await Repository.OpenSubmoduleConflictRepositoryAsync(expected, token);
            CheckoutBranchConstraint constraint = new(
                ct => Repository.GetSubmoduleConflictBranchesAsync(expected, ct),
                (branch, options, ct) => Repository.CheckoutSubmoduleConflictBranchAsync(expected, branch, options, ct));
            CheckoutBranchDialog dialog = new(module, constraint: constraint) { RequestedThemeVariant = ActualThemeVariant };
            dialog.Opened += async (_, _) => await dialog.InitializeAsync(token);
            using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            try { await dialog.ShowDialog(this); }
            finally
            {
                dialog.Close(); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
                await RefreshCurrentAsync(expected, token);
            }
            token.ThrowIfCancellationRequested();
            if (dialog.Succeeded && IsVisible) { await StageCurrentAsync(token); }
            else if (dialog.LastException is Exception failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        });
        if (IsVisible && !IsBusy) { _checkout.Focus(); }
    }

    protected override void UpdateActions()
    {
        bool enabled = !IsBusy && !_invalidated;
        _open.IsEnabled = enabled && _snapshot?.CanOpen == true;
        _checkout.IsEnabled = enabled && _snapshot?.CanCheckoutBranch == true;
        _stage.IsEnabled = enabled && _snapshot?.CanStageCurrent == true;
        _refresh.IsEnabled = !IsBusy && _snapshot is not null;
    }
}
