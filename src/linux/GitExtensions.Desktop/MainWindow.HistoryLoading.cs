using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private sealed record HistoryOperationFailure(Exception? Error, string? State);

    private sealed class BrowseHistoryLoad(GitRepository repository, GitRepository? origin, CancellationTokenSource cancellation,
        GitLogQuery query, HistoryBookmark bookmark, long intent, bool opening, bool preserveSelection)
    {
        internal readonly GitRepository Repository = repository;
        internal readonly GitRepository? Origin = origin;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Progress = NewHistoryProgress();
        internal Task Work = Task.CompletedTask, Inventory = Task.CompletedTask;
        internal GitLogQuery Query = query;
        internal readonly HistoryBookmark Bookmark = bookmark;
        internal readonly long Intent = intent;
        internal readonly bool Opening = opening, ChangedRepository = opening && origin?.Path != repository.Path;
        internal readonly bool PreserveSelection = preserveSelection;
        internal readonly List<GitCommit> Received = [];
        internal readonly HashSet<string> ReceivedKeys = new(StringComparer.Ordinal);
        internal GitStatus Status = null!;
        internal IReadOnlyList<GitReference> References = [];
        internal GitFileHistoryGraphContext? FileContext;
        internal FileHistoryPreferences FilePreferences = new();
        internal HistoryViewPreferences View = new();
        internal GitCompareRevision? Requested, First;
        internal BrowseInitialComparison? Comparison;
        internal string[] PendingSelection = [];
        internal string? WorkingPath, ReferenceName;
        internal int Maximum, BatchSize;
        internal bool Published, Completed, EndOfHistory, MissingNavigationReference, SelectionPending, PendingPanels;
        internal Exception? Error;
        internal HistoryOperationFailure? RetainedFailure;
        internal IReadOnlyList<string>? Ancestors;
        internal void Pulse() { TaskCompletionSource old = Progress; Progress = NewHistoryProgress(); old.TrySetResult(); }
    }

    private BrowseHistoryLoad? _historyLoad, _interruptedHistory;
    private Task _historyLoadingTail = Task.CompletedTask;
    private readonly HashSet<Task> _historyLoadingLeases = [];
    private long _historyIntentVersion;
    private int? _historyMaximumOverride;
    private int _historyMutationScopes;
    internal Exception? HistoryLoadingException => _historyLoad?.Error;
    internal bool IsHistoryLoading => _historyLoad is { Completed: false };
    internal Task HistoryLoadingCompletion => Task.WhenAll(_historyLoadingLeases.ToArray());
    private static TaskCompletionSource NewHistoryProgress() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool CanPublishHistory(BrowseHistoryLoad load) => ReferenceEquals(_historyLoad, load)
        && !load.Cancellation.IsCancellationRequested && !_commitInfoClosing && !_lifetime.IsCancellationRequested
        && ReferenceEquals(_repository, load.Published ? load.Repository : load.Origin);

    private void CancelHistoryLoading()
    {
        if (_historyLoad is { Completed: false } load) { load.Cancellation.Cancel(); load.Pulse(); }
    }

    private async Task CancelAndDrainHistoryLoadingAsync()
    {
        CancelHistoryLoading();
        // This tail excludes the operation/notification/modeless leases which may be awaiting us.
        await _historyLoadingTail;
    }

    private async Task StartHistoryLoadingAsync(GitRepository repository, bool opening, CancellationToken admission, BrowseHistoryLoad? resumed = null)
    {
        BrowseHistoryLoad? previous = _historyLoad;
        Task previousWork = _historyLoadingTail;
        CancelHistoryLoading();
        GitLogQuery query = opening ? new GitLogQuery
        { Count = Math.Clamp(_preferences.HistoryLimit, 1, 5000), Reflog = _historyView.ShowReflog, Path = _browseRequest.PathFilter?.Path } : resumed?.Query ?? _historyQuery;
        BrowseHistoryLoad load = new(repository, _repository, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, admission),
            query, CaptureHistoryBookmark(), _historyIntentVersion, opening,
            _operationHistoryFocus is { PreserveHistorySelection: true } focus && ReferenceEquals(focus.Repository, _repository))
        {
            Requested = opening && !_browseSelectionApplied ? _browseRequest.SelectedRevision : null,
            First = opening && !_browseSelectionApplied ? _browseRequest.FirstRevision : null,
            Comparison = opening && _repository?.Path != repository.Path ? null : _browseInitialComparison,
            WorkingPath = SelectedWorkingChange?.Path,
            ReferenceName = opening ? null : _referenceSelectionOverride ?? (_references.SelectedItem as GitReference)?.Name,
            Maximum = resumed?.Maximum ?? _historyMaximumOverride ?? _preferences.MaxRevisionGraphCommits,
            BatchSize = resumed?.BatchSize ?? Math.Clamp(_preferences.HistoryLimit, 1, 5000), View = _historyView, RetainedFailure = resumed?.RetainedFailure
        };
        _historyLoad = load;
        if (!ReferenceEquals(_interruptedHistory, previous)) { _interruptedHistory = null; }
        WindowWork lease = TrackWindowWork();
        _historyLoadingLeases.Add(lease.Completion);
        load.Work = ReadProgressiveHistoryAsync(load, previousWork);
        _historyLoadingTail = load.Work;
        _ = RetireHistoryLoadingAsync(load, lease);
        try
        {
            await load.Ready.Task.WaitAsync(admission);
            if (load.Error is not null && !load.Published) { throw load.Error; }
            admission.ThrowIfCancellationRequested();
        }
        catch
        {
            // An initial failure must retain both the last good rows and their repository.
            // If that reader was interrupted before EOF, its accepted query can resume after the operation.
            if (!load.Published && ReferenceEquals(_historyLoad, load))
            {
                _historyLoad = previous;
                if (previous is { Published: true, EndOfHistory: false }) { _interruptedHistory = previous; }
            }
            throw;
        }
    }

    private async Task RetireHistoryLoadingAsync(BrowseHistoryLoad load, WindowWork lease)
    {
        try { await load.Work; }
        finally
        {
            load.Cancellation.Dispose();
            _historyLoadingLeases.Remove(lease.Completion); lease.Dispose();
        }
    }

    private async Task ReadProgressiveHistoryAsync(BrowseHistoryLoad load, Task previousWork)
    {
        CancellationToken token = load.Cancellation.Token;
        try
        {
            await previousWork;
            token.ThrowIfCancellationRequested();
            using IDisposable environment = load.Repository.BeginOperation();
            ApplyReferenceSorting(load.Repository);
            if (load.Requested?.Kind == GitCompareRevisionKind.Commit)
            { load.Requested = load.Requested with { Revision = await load.Repository.ResolveRevisionAsync(load.Requested.Revision, token) }; }
            if (load.First?.Kind == GitCompareRevisionKind.Commit)
            { load.First = load.First with { Revision = await load.Repository.ResolveRevisionAsync(load.First.Revision, token) }; }
            load.Status = await load.Repository.GetStatusAsync(token);
            load.References = await load.Repository.GetReferencesAsync(token);
            token.ThrowIfCancellationRequested();
            if (!CanPublishHistory(load)) { return; }
            string? revision = load.Query.BranchFilter ?? load.Query.Revision;
            load.MissingNavigationReference = !load.Opening && _navigationRevisionFilter is string navigation && revision == navigation
                && !load.References.Any(reference => reference.Name == navigation);
            if (load.MissingNavigationReference) { load.Query = load.Query with { All = true, Revision = null, BranchFilter = null }; }
            load.Query = ConfigureHistoryViewQuery(load.Query) with { Skip = 0 };
            load.FilePreferences = _fileHistoryPreferences;
            SetProgressiveSelectionTarget(load);
            bool emptyCurrent = load.Status.IsUnborn && (load.Query.Revision == "HEAD" || load.Query.BranchFilter?.Trim() == "HEAD");
            if (!emptyCurrent)
            {
                if (string.IsNullOrEmpty(load.Query.Path))
                {
                    await load.Repository.StreamRevisionGraphAsync(load.Query, load.Maximum, load.BatchSize,
                        (batch, cancellation) => PublishHistoryBatchAsync(load, null, batch, cancellation), token);
                }
                else
                {
                    bool directory = _browseRequest.PathFilter is { } filter && filter.Path == load.Query.Path
                        ? filter.Kind == BrowsePathKind.Directory
                        : load.Query.Path.EndsWith('/') || Directory.Exists(Path.Combine(load.Repository.Path, load.Query.Path));
                    GitFileHistoryGraphStreamResult result = await load.Repository.StreamFileHistoryGraphAsync(load.Query, load.Maximum, load.BatchSize,
                        (context, batch, cancellation) => PublishHistoryBatchAsync(load, context, batch, cancellation),
                        new(load.FilePreferences.FollowRenames, load.FilePreferences.ExactRenames, directory), token);
                    load.FileContext = result.Context;
                }
            }
            token.ThrowIfCancellationRequested();
            if (!CanPublishHistory(load)) { return; }
            if (load.Status.Head is not null && !load.Received.Any(commit => commit.Hash == load.Status.Head) && !load.Repository.IsBare)
            {
                try { load.Ancestors = await load.Repository.ReadRevisionAncestryAsync(load.Status.Head, token); }
                catch (GitCommandException) { load.Ancestors = []; }
            }
            token.ThrowIfCancellationRequested();
            if (!CanPublishHistory(load)) { return; }
            load.EndOfHistory = true;
            await Dispatcher.UIThread.InvokeAsync(() => PublishProgressiveHistory(load, [], final: true));
            await load.Inventory;
            await LoadProgressiveMetadataAsync(load);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            load.Error = error;
            if (CanPublishHistory(load) && load.Published) { ReportHistoryLoadingError(load, error); }
        }
        finally
        {
            // Any inventory already admitted belongs to this generation even after the graph failed/cancelled.
            try { await load.Inventory; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                load.Error ??= error;
                if (CanPublishHistory(load) && load.Published) { ReportHistoryLoadingError(load, error); }
            }
            load.Completed = true; load.Ready.TrySetResult(); load.Pulse();
        }
    }

    private async ValueTask PublishHistoryBatchAsync(BrowseHistoryLoad load, GitFileHistoryGraphContext? context,
        IReadOnlyList<GitCommit> batch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!CanPublishHistory(load)) { return Task.CompletedTask; }
            load.FileContext = context;
            return PublishProgressiveHistory(load, batch, final: false);
        });
    }

    private void ReportHistoryLoadingError(BrowseHistoryLoad load, Exception error)
    {
        if (!CanPublishHistory(load)) { return; }
        if (load.RetainedFailure is not null)
        {
            // The resumed reader has its own Error/output. Do not invalidate the
            // mutation's original report by temporarily replacing LastException.
            AppendOutput("Load revisions: " + ExceptionReportSnapshot.MessageOf(error));
            RestoreRetainedHistoryFailure(load);
            return;
        }
        LastException = error;
        ExceptionReportSnapshot? captured = !Program.SmokeTest && !_lifetime.IsCancellationRequested && IsVisible
            ? ExceptionReportSnapshot.Capture(error) : null;
        _state.Text = "Load revisions failed — see command output";
        AppendOutput("Load revisions: " + (captured?.Root.Message ?? ExceptionReportSnapshot.MessageOf(error)));
        if (!Program.SmokeTest && !_lifetime.IsCancellationRequested && IsVisible)
        {
            _errorSnapshot = captured;
            _errorContext = ExceptionReportContext.Capture("Load revisions", load.Repository.Path, DesktopVersion, error);
            _errorDetails.IsVisible = true; _errorDetails.IsEnabled = _errorReportCompletion.IsCompleted;
        }
    }

    private void CaptureInterruptedHistoryFailure()
    {
        if (_interruptedHistory is { } interrupted)
        { interrupted.RetainedFailure = new(LastException, _state.Text); }
    }

    private void RestoreRetainedHistoryFailure(BrowseHistoryLoad load)
    {
        if (ReferenceEquals(_historyLoad, load) && load.RetainedFailure is { } failure)
        { LastException = failure.Error; _state.Text = failure.State; }
    }

    private long RecordHistorySelectionIntent()
    {
        if (_refreshing) { return _historyIntentVersion; }
        _historyIntentVersion++;
        if (_historyLoad is { } load) { load.PendingSelection = []; load.SelectionPending = false; load.Pulse(); }
        return _historyIntentVersion;
    }

    private async Task<bool> WaitForHistoryRevisionAsync(GitCompareRevision revision, long intent, CancellationToken token = default)
    {
        BrowseHistoryLoad? load = _historyLoad;
        while (HistoryItem(revision) is null && load is { Completed: false } && ReferenceEquals(_historyLoad, load)
            && intent == _historyIntentVersion && !load.Cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            Task progress = load.Progress.Task;
            await progress.WaitAsync(token);
        }
        return intent == _historyIntentVersion && !_lifetime.IsCancellationRequested
            && (load is null || ReferenceEquals(_historyLoad, load)) && HistoryItem(revision) is not null;
    }

    private bool IsHistoryRequestCurrent(BrowseHistoryLoad? request, GitRepository repository, long intent)
        => ReferenceEquals(request, _historyLoad) && ReferenceEquals(_repository, repository)
            && intent == _historyIntentVersion && !_commitInfoClosing && !_lifetime.IsCancellationRequested;

    private bool IsHistoryAbsenceProven(BrowseHistoryLoad? request, GitRepository repository, long intent)
        => IsHistoryRequestCurrent(request, repository, intent)
            && (request is null || request.EndOfHistory && request.Error is null && !request.Cancellation.IsCancellationRequested);

    private Task RunHistoryMutationAsync(string label, Func<GitRepository, Task> action, bool refresh = false, bool requireRepository = true)
        => RunWithOperationFocusAsync(label, action, refresh, requireRepository, historyMutation: true);

    private IAsyncDisposable BeginHistoryMutationScope()
    { _historyMutationScopes++; return new HistoryMutationScope(this); }

    private sealed class HistoryMutationScope(MainWindow owner) : IAsyncDisposable
    {
        private bool _disposed;
        public async ValueTask DisposeAsync()
        {
            if (_disposed) { return; } _disposed = true;
            owner._historyMutationScopes--;
            await owner.ResumeInterruptedHistoryAsync(allowBusy: false);
        }
    }

    private async Task ResumeInterruptedHistoryAsync(bool allowBusy)
    {
        BrowseHistoryLoad? interrupted = _interruptedHistory;
        if (_historyMutationScopes != 0 || interrupted is null || _busy && !allowBusy) { return; }
        _interruptedHistory = null;
        if (!ReferenceEquals(_historyLoad, interrupted) || !ReferenceEquals(_repository, interrupted.Repository)
            || _lifetime.IsCancellationRequested || _commitInfoClosing || !IsVisible) { return; }
        try { await StartHistoryLoadingAsync(interrupted.Repository, opening: false, _lifetime.Token, resumed: interrupted); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppendOutput("Resume revisions: " + ExceptionReportSnapshot.MessageOf(error));
            if (interrupted.RetainedFailure is { } failure) { LastException = failure.Error; _state.Text = failure.State; }
            else { LastException = error; _state.Text = error.Message; }
        }
    }
}
