using Avalonia.Controls;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Owns only the standalone HEAD read; matching and acceptance remain in the shared SearchWindow.</summary>
internal sealed class SearchFileSession
{
    private readonly CancellationTokenSource _read = new();
    private bool _started, _closing, _allowClose;
    internal GitRepository Repository { get; }
    internal FileListFindDialog Dialog { get; }
    internal Task LoadTask { get; private set; } = Task.CompletedTask;
    internal Exception? LastException { get; private set; }

    internal SearchFileSession(GitRepository repository, TranslationCatalog? translations = null)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        Dialog = new([], repository.Path, translations ?? TranslationCatalog.LoadDefault())
        { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        Dialog.Opened += (_, _) =>
        {
            if (_started) { return; }
            _started = true;
            LoadTask = LoadAsync();
        };
        Dialog.Closing += (_, args) =>
        {
            if (_allowClose || LoadTask.IsCompleted) { return; }
            // Closing the application must not strand an owned Git reader or publish a partial selection.
            args.Cancel = true;
            if (_closing) { return; }
            _closing = true; _read.Cancel();
            _ = CloseAfterDrainAsync();
        };
        Dialog.Closed += (_, _) => _read.Dispose();
    }

    private async Task LoadAsync()
    {
        try
        {
            // RunSearchFileCommand.FindFileMatches reads GetFullTree("HEAD"), never the index or live files.
            IReadOnlyList<GitTreeEntry> tree = await Repository.GetTreeAsync("HEAD", cancellationToken: _read.Token);
            _read.Token.ThrowIfCancellationRequested();
            if (!_closing && Dialog.IsVisible)
            { Dialog.SetCandidates(tree.Select(entry => new FileStatusListEntry(entry, entry.Path, Status: ' ')).ToArray()); }
        }
        catch (OperationCanceledException) when (_read.IsCancellationRequested) { }
        catch (Exception error)
        {
            LastException = error;
            // GitRunner has already observed and drained its process before an unsuccessful read completes.
            _allowClose = true;
            if (Dialog.IsVisible) { Dialog.Close(); }
        }
    }

    private async Task CloseAfterDrainAsync()
    {
        await LoadTask;
        _allowClose = true;
        Dialog.Close();
    }
}
