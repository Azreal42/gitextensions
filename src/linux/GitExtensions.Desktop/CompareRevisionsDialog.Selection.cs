using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CompareRevisionsDialog
{
    private sealed record SelectionRead(GitComparison Comparison, GitRevisionFile File, int Tab);
    private readonly CancellationTokenSource _selectionLifetime = new();
    private readonly HashSet<Task> _selectionTasks = [];
    private CancellationTokenSource? _selectionRead;
    private SelectionRead? _requestedSelection, _loadedSelection;
    private Task? _selectionTask;
    private bool _applyingFileFilter, _selectionClosing, _selectionClosed;
    private Window? _closingSelectionOwner;
    internal Task SelectionCompletion => Task.WhenAll(_selectionTasks.ToArray());
    internal bool IsSelectionLoading => _selectionTasks.Any(task => !task.IsCompleted);

    private void InitializeSelectionLifetime()
    {
        Closing += (_, args) =>
        {
            if (_selectionClosed) { return; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingSelectionOwner = Owner as Window; }
            _selectionClosing = true; _selectionLifetime.Cancel(); CancelSelectionRead();
            Task pending = SelectionCompletion;
            if (!pending.IsCompleted) { args.Cancel = true; _ = CloseAfterSelectionAsync(pending); }
        };
        Closed += (_, _) =>
        {
            _selectionClosed = true;
            _selectionLifetime.Dispose();
            Window? owner = _closingSelectionOwner; _closingSelectionOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }

    private async Task CloseAfterSelectionAsync(Task pending)
    {
        await pending;
        Dispatcher.UIThread.Post(() => { if (IsVisible && !IsSelectionLoading) { Close(); } });
    }

    private void CancelSelectionRead()
    {
        _selectionRead?.Cancel(); _selectionRead = null; _requestedSelection = null; _selectionTask = null;
    }

    private Task RunComparisonAsync(string label, Func<CancellationToken, Task> action)
    {
        if (_selectionClosing) { return Task.CompletedTask; }
        return RunAsync(label, async token =>
        { CancelSelectionRead(); await SelectionCompletion; token.ThrowIfCancellationRequested(); await action(token); token.ThrowIfCancellationRequested(); });
    }

    private Task LoadSelectionAsync() => ReadSelectedAsync();
    private Task ReloadSelectionAsync() => ReadSelectedAsync(force: true);

    private Task ReadSelectedAsync(CancellationToken parent = default, bool force = false)
    {
        if (_selectionClosing) { return Task.CompletedTask; }
        GitRevisionFile? file = SelectedFile;
        TabItem[] tabs = _tabs.Items.OfType<TabItem>().ToArray();
        tabs[1].IsEnabled = file is not null && file.Status != 'A';
        tabs[2].IsEnabled = file is not null && !file.IsDeleted;
        if (_tabs.SelectedIndex == 1 && !tabs[1].IsEnabled || _tabs.SelectedIndex == 2 && !tabs[2].IsEnabled)
        { _tabs.SelectedIndex = 0; }
        _fileCaption.Text = file is null ? T("No differences") : ExportDialogControls.DisplayPath(file.OriginalPath is null ? file.Path : file.OriginalPath + " → " + file.Path);
        UpdateActions();
        if (file is null || _comparison is null)
        {
            CancelSelectionRead(); _loadedSelection = null;
            _diff.ShowDiff(T("No differences")); _baseView.ShowDiff(""); _compareView.ShowDiff(""); return Task.CompletedTask;
        }
        SelectionRead selection = new(_comparison, file, _tabs.SelectedIndex);
        if (!force && SameSelection(selection, _requestedSelection) && _selectionTask is { IsCompleted: false } current) { return current; }
        if (!force && SameSelection(selection, _loadedSelection)) { return Task.CompletedTask; }
        CancelSelectionRead(); _loadedSelection = null;
        ClearError(); Status.Text = selection.Comparison.Files.Count + " " + T("files");
        CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_selectionLifetime.Token, parent);
        _selectionRead = read; _requestedSelection = selection;
        Task task = ReadSelectionCoreAsync(selection, read);
        _selectionTask = task; _selectionTasks.Add(task); _ = ObserveSelectionAsync(task);
        return task;
    }

    private async Task ObserveSelectionAsync(Task task)
    { try { await task; } finally { _selectionTasks.Remove(task); } }

    private static bool SameSelection(SelectionRead current, SelectionRead? other)
        => other is not null && ReferenceEquals(current.Comparison, other.Comparison) && current.File == other.File && current.Tab == other.Tab;

    private bool IsCurrentSelection(SelectionRead selection, CancellationTokenSource read)
        => !_selectionClosing && !read.IsCancellationRequested && ReferenceEquals(_selectionRead, read)
            && ReferenceEquals(_comparison, selection.Comparison) && SelectedFile == selection.File && _tabs.SelectedIndex == selection.Tab;

    private async Task ReadSelectionCoreAsync(SelectionRead selection, CancellationTokenSource read)
    {
        CancellationToken token = read.Token;
        GitComparison comparison = selection.Comparison; GitRevisionFile file = selection.File;
        try
        {
            _diff.TextView.DocumentIdentity = string.Join("\0", Repository.Path, file.Path, file.OriginalPath, comparison.Base, comparison.Compare, "diff");
            _baseView.TextView.DocumentIdentity = string.Join("\0", Repository.Path, file.OriginalPath ?? file.Path, comparison.Base, "blob");
            _compareView.TextView.DocumentIdentity = string.Join("\0", Repository.Path, file.Path, comparison.Compare, "blob");
            if (selection.Tab == 0)
            {
                _diff.TextView.BeginDocumentLoad();
                await _diff.TextView.ConfigureRepositoryAsync(Repository, token);
                if (!IsCurrentSelection(selection, read)) { return; }
                var document = await Repository.GetComparisonFileDiffDocumentAsync(comparison, file, ReadDiffOptions(), _diff.TextView.DisplayOptions, token);
                if (!IsCurrentSelection(selection, read)) { return; }
                _diff.ShowDiff(document, file.Path);
            }
            else
            {
                string path = selection.Tab == 1 ? file.OriginalPath ?? file.Path : file.Path;
                byte[] bytes = await Repository.GetComparisonFileBytesAsync(selection.Tab == 1 ? comparison.Base : comparison.Compare, path, token);
                string encoding = await Repository.GetFileEncodingNameAsync(token);
                if (!IsCurrentSelection(selection, read)) { return; }
                (selection.Tab == 1 ? _baseView : _compareView).ShowFile(path, bytes, encoding);
            }
            _loadedSelection = selection;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (IsCurrentSelection(selection, read)) { ReportError(exception); } }
        finally
        {
            if (ReferenceEquals(_selectionRead, read)) { _selectionRead = null; _requestedSelection = null; _selectionTask = null; }
            read.Dispose();
        }
    }
}
