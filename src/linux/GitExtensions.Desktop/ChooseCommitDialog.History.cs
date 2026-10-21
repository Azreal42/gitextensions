using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal partial class ChooseCommitDialog
{
    // A generation owns its native reader until real completion, even after replacement or window close.
    private sealed class HistoryRead(CancellationTokenSource cancellation, GitLogQuery query, int maximum,
        GitCompareRevision? target, long intent)
    {
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly GitLogQuery Query = query;
        internal readonly int Maximum = maximum;
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Progress = NewSignal();
        internal Task Work = Task.CompletedTask;
        internal GitCompareRevision? Target = target;
        internal readonly long Intent = intent;
        internal bool Completed;
        internal Exception? Error;
        internal void Pulse() { TaskCompletionSource previous = Progress; Progress = NewSignal(); previous.TrySetResult(); }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly CancellationTokenSource _historyLifetime;
    private readonly int _defaultMaximum, _batchSize;
    private readonly HashSet<Task> _historyLeases = [];
    private HistoryRead? _history;
    private Task _historyTail = Task.CompletedTask;
    private Task _observedWork = Task.CompletedTask, _completion = Task.CompletedTask;
    private int _historyVersion, _observedHistoryVersion = -1;
    private long _selectionVersion;
    private int? _maximumOverride;
    private bool _applyingHistory, _closeReady;
    private Task? _closeDrain;
    private ChooseCommitSelection? _pendingSelection;
    internal bool IsHistoryLoading => _history is { Completed: false };

    private Task ObserveCompletion()
    {
        if (_observedWork != _work || _observedHistoryVersion != _historyVersion)
        {
            _observedWork = _work; _observedHistoryVersion = _historyVersion;
            _completion = Task.WhenAll(_historyLeases.Append(_work));
        }
        return _completion;
    }

    private HistoryRead BeginHistoryRead()
    {
        bool initial = !_loaded;
        GitCompareRevision? target = initial ? _request.PreselectSelection : _revisions.Selection?.Revision;
        HistoryRead read = new(CancellationTokenSource.CreateLinkedTokenSource(_historyLifetime.Token), _query,
            _maximumOverride ?? _defaultMaximum, target, _selectionVersion);
        HistoryRead? previous = _history;
        Task previousWork = _historyTail;
        TaskCompletionSource lease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _historyLeases.Add(lease.Task); _historyVersion++;
        _history = read;
        if (previous is { Completed: false }) { previous.Cancellation.Cancel(); }
        read.Work = ReadHistoryAsync(read, previousWork, initial);
        _historyTail = read.Work;
        _ = RetireHistoryAsync(read, lease);
        return read;
    }

    private async Task RetireHistoryAsync(HistoryRead read, TaskCompletionSource lease)
    {
        // Work includes Git's pipe/callback/native drainage. No racing cancellation task can retire this lease.
        try { await read.Work; }
        finally { read.Cancellation.Dispose(); _historyLeases.Remove(lease.Task); lease.TrySetResult(); }
    }

    private bool CanPublish(HistoryRead read) => !_closing && _history == read && !read.Cancellation.IsCancellationRequested;

    private async Task ReadHistoryAsync(HistoryRead read, Task previousWork, bool initial)
    {
        CancellationToken token = read.Cancellation.Token;
        try
        {
            await previousWork;
            token.ThrowIfCancellationRequested();
            using IDisposable environment = Repository.BeginOperation();
            if (initial && read.Target is null && !string.IsNullOrWhiteSpace(_request.Preselect))
            {
                try { read.Target = new(await Repository.ResolveRevisionAsync(_request.Preselect, token)); }
                catch (Exception error) when (error is GitCommandException or ArgumentException) { /* Windows ignores an unresolved preselection. */ }
            }
            GitStatus status = await Repository.GetStatusAsync(token);
            IReadOnlyList<GitReference> references = await Repository.GetReferencesAsync(token);
            token.ThrowIfCancellationRequested();
            if (!CanPublish(read)) { return; }
            ApplyHistory(() => _revisions.BeginIncrementalData(references, status.Head, _showArtificial && !Repository.IsBare, read.Query.FirstParent));
            if (!(status.IsUnborn && read.Query.Revision == "HEAD"))
            {
                await Repository.StreamRevisionGraphAsync(read.Query, read.Maximum, _batchSize, async (batch, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!CanPublish(read)) { return; }
                        ApplyHistory(() => _revisions.AppendCommits(batch));
                        SelectPendingTarget(read);
                        _loaded = true;
                        read.Ready.TrySetResult(); read.Pulse();
                    });
                }, token);
            }
            token.ThrowIfCancellationRequested();
            if (!CanPublish(read)) { return; }
            ApplyHistory(() => _revisions.CompleteCommits());
            SelectPendingTarget(read);
            _loaded = true; read.Ready.TrySetResult(); read.Pulse();
            // Windows searches a bounded ancestry when the requested commit is excluded by the current filter/ceiling.
            if (read.Target?.Kind == GitCompareRevisionKind.Commit && read.Intent == _selectionVersion)
            {
                IReadOnlyList<GitCommit> ancestors = await Repository.GetLogAsync(new(Count: 50, All: false, Revision: read.Target.Revision), token);
                if (CanPublish(read) && read.Intent == _selectionVersion)
                {
                    foreach (GitCommit ancestor in ancestors) { if (ApplySelection(new(ancestor.Hash))) { break; } }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            read.Error = error;
            if (CanPublish(read) && !IsBusy) { ReportError(error); }
        }
        finally
        {
            read.Completed = true; read.Ready.TrySetResult(); read.Pulse();
        }
    }

    private void SelectPendingTarget(HistoryRead read)
    {
        if (read.Intent != _selectionVersion) { read.Target = null; return; }
        if (read.Target is not null && ApplySelection(read.Target)) { read.Target = null; }
        if (_revisions.Selection is null) { ApplyHistory(_revisions.SelectFirst); }
    }

    private void ApplyHistory(Action action)
    {
        bool previous = _applyingHistory; _applyingHistory = true;
        try { action(); }
        finally { _applyingHistory = previous; }
    }

    private bool ApplySelection(GitCompareRevision revision)
    {
        bool selected = false;
        ApplyHistory(() => selected = _revisions.Select(revision));
        return selected;
    }

    private long RecordSelectionIntent()
    {
        _selectionVersion++;
        if (_history is HistoryRead read) { read.Target = null; read.Pulse(); }
        return _selectionVersion;
    }

    private void RequestClose(ChooseCommitSelection? selection)
    {
        _pendingSelection = selection;
        Close(selection);
    }

    private void OnHistoryClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closeReady) { return; }
        if (_closing) { _pendingSelection = null; }
        _closing = true;
        _historyLifetime.Cancel();
        Task completion = Completion;
        if (completion.IsCompleted) { _closeReady = true; _historyLifetime.Dispose(); return; }
        args.Cancel = true;
        _closeDrain ??= CloseAfterHistoryAsync(completion);
    }

    private async Task CloseAfterHistoryAsync(Task completion)
    {
        await completion;
        _closeReady = true;
        _historyLifetime.Dispose();
        Close(_pendingSelection);
    }
}
