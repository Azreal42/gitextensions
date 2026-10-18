using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

internal sealed record FileHistoryBlameNavigation(GitRepository Repository, string Hash, string Path, int Line, bool ShowChanges);

/// <summary>File-tree-only blame: reading never changes the revision grid or takes its input focus.</summary>
internal sealed class FileHistoryBlameController : UserControl
{
    private sealed record Request(GitRepository Repository, string Path, GitCompareRevision Source);
    private readonly FileContentView _normal;
    private readonly BlameView _blame;
    private readonly CheckBox _toggle = new() { Name = "FileHistoryInlineBlameToggle" };
    private readonly StackPanel _tools;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _presentation = new(1);
    private readonly HashSet<Task> _pending = [];
    private CancellationTokenSource? _read;
    private Request? _request, _displayed;
    private (GitRepository Repository, string Hash, string Path, int Line)? _position;
    private long _generation;
    private bool _available, _settingMode, _configuring, _closing;
    private Task? _close;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();

    internal event Func<Task>? ReloadRequested;
    internal event Func<FileHistoryBlameNavigation, Task>? NavigationRequested;
    internal event Action<string>? InteractionError;
    internal BlameView View => _blame;
    internal FileContentView NormalView => _normal;
    internal bool IsClosing => _closing;
    internal bool IsBlameActive => _available && _toggle.IsChecked == true && !_closing;
    internal Task Completion => Task.WhenAll(_pending.ToArray());

    internal FileHistoryBlameController(FileContentView normal)
    {
        _normal = normal;
        _blame = new() { Name = "FileHistoryInlineBlame" };
        _blame.AvatarReadStarted += Track;
        _toggle.Content = _translations.Translate("Blame");
        Button options = _blame.CreateOptionsButton(); options.Name = "FileHistoryInlineBlameOptions";
        _tools = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _toggle, options }, IsVisible = false };
        DockPanel panel = new(); DockPanel.SetDock(_tools, Dock.Top); panel.Children.Add(_tools);
        Grid content = new(); content.Children.Add(_normal); content.Children.Add(_blame); panel.Children.Add(content); Content = panel;
        _blame.IsVisible = false;
        _toggle.IsCheckedChanged += async (_, _) =>
        {
            if (_settingMode || _closing) { return; }
            Cancel(); UpdateVisibility();
            try { if (ReloadRequested is { } reload) { await reload(); } }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_closing) { InteractionError?.Invoke(error.Message); } }
        };
        BlamePreferences previous = _blame.Preferences;
        _blame.PreferencesChanged += preferences =>
        {
            bool reload = previous.GitOptions != preferences.GitOptions; previous = preferences;
            if (reload) { ReloadCurrent(); }
            if (_blame.PreferenceError is { } error && !_closing) { InteractionError?.Invoke(error); }
        };
        _blame.TextView.FileEncodingChanged += (_, _) => { if (!_configuring) { ReloadCurrent(); } };
        _blame.CommandRequested += (_, args) => Track(NavigateAsync(args));
        _blame.InteractionError += message => { if (!_closing) { InteractionError?.Invoke(message); } };
    }

    internal void SetAvailable(bool available)
    {
        if (_closing || _available == available) { return; }
        _available = available; _settingMode = true;
        try { _toggle.IsChecked = available; }
        finally { _settingMode = false; }
        Cancel(); _tools.IsVisible = available; UpdateVisibility();
    }

    internal Task<bool> TryLoadAsync(GitRepository repository, string path, GitCompareRevision source,
        CancellationToken token, bool isSubmodule = false, int? initialLine = null)
    {
        if (!IsBlameActive || isSubmodule)
        { Cancel(showNormal: true); return Task.FromResult(false); }
        _normal.IsVisible = false; _blame.IsVisible = true;
        Request request = new(repository, path, source); _request = request;
        // Retain the old document and its identity until the replacement is complete,
        // just as Browse's diff reader does. Pending navigation cannot use its rows.
        if (_position is { } position && (position.Repository != repository || position.Path != path
            || source.Kind != GitCompareRevisionKind.Commit || position.Hash != source.Revision)) { _position = null; }
        _read?.Cancel();
        CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token); _read = read;
        long generation = ++_generation;
        Task<bool> task = LoadAsync(request, generation, read, initialLine); Track(task); return task;
    }

    private async Task<bool> LoadAsync(Request request, long generation, CancellationTokenSource read, int? initialLine)
    {
        try
        {
            string? encoding = _blame.TextView.FileEncodingName;
            GitFileBlame data = await request.Repository.GetComparisonFileBlameAsync(request.Source, request.Path,
                _blame.Preferences.GitOptions, read.Token, encoding);
            read.Token.ThrowIfCancellationRequested();
            await _presentation.WaitAsync(read.Token);
            try
            {
                if (!Current(request, generation)) { return true; }
                _configuring = true;
                try
                {
                    await _blame.ConfigureRepositoryAsync(request.Repository, read.Token);
                    read.Token.ThrowIfCancellationRequested();
                    if (!Current(request, generation)) { return true; }
                    _blame.TextView.FileEncodingName = encoding;
                    _blame.CommitInformationProvider = (line, token) =>
                    {
                        Task<string> information = ReadCommitInformationAsync(request.Repository, line, token);
                        Track(information); return information;
                    };
                    int? position = _position is { } pending ? pending.Line : initialLine;
                    _blame.SetBlame(data, position);
                    _displayed = request; _position = null;
                }
                finally { _configuring = false; }
            }
            finally { _presentation.Release(); }
            return true;
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { return true; }
        catch (Exception) when (read.IsCancellationRequested || !Current(request, generation)) { return true; }
        catch
        {
            _blame.SetBlame(new(request.Path, request.Source.Kind == GitCompareRevisionKind.Commit ? request.Source.Revision : null, [], []) { Source = request.Source });
            _displayed = null;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_read, read)) { _read = null; }
            read.Dispose();
        }
    }

    private bool Current(Request request, long generation)
        => !_closing && IsBlameActive && generation == _generation && ReferenceEquals(_request, request);

    private async Task<string> ReadCommitInformationAsync(GitRepository repository, GitFileBlameLine line, CancellationToken token)
    {
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        return await BlameDialog.ReadCommitInformationAsync(repository, line, read.Token);
    }

    private void ReloadCurrent()
    {
        if (!IsBlameActive || _request is not { } request) { return; }
        Track(ReloadAsync(request));
    }
    private async Task ReloadAsync(Request request)
    {
        try { await TryLoadAsync(request.Repository, request.Path, request.Source, _lifetime.Token, initialLine: _blame.CurrentFileLine); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) { InteractionError?.Invoke(error.Message); } }
    }

    private async Task NavigateAsync(BlameCommandEventArgs args)
    {
        if (_closing || !IsBlameActive || _request is not { } request || _displayed != request) { return; }
        long generation = _generation;
        string hash = args.Line.Line.Hash, path = args.Line.OriginalPath; int line = args.Line.Line.OriginalLine;
        if (hash.All(character => character == '0')) { return; }
        try
        {
            if (args.Command == BlameCommand.PreviousRevision)
            {
                GitBlamePreviousRevision? previous = await request.Repository.GetBlamePreviousRevisionAsync(args.Line,
                    _blame.Preferences.GitOptions, _blame.Preferences.UseHistogramDiffAlgorithm, _lifetime.Token);
                if (previous is null) { return; }
                hash = previous.Revision; path = previous.Path; line = previous.Line;
            }
            if (Current(request, generation) && NavigationRequested is { } navigate)
            { await navigate(new(request.Repository, hash, path, line, args.Command == BlameCommand.ShowChanges)); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (Current(request, generation)) { InteractionError?.Invoke(error.Message); } }
    }

    internal void SetPosition(GitRepository repository, string hash, string path, int line)
        => _position = (repository, hash, path, line);

    internal void Cancel(bool showNormal = false)
    {
        _generation++; _read?.Cancel(); _blame.CancelAvatarLoading();
        if (showNormal)
        {
            _request = null; _displayed = null; _position = null;
            _normal.IsVisible = true; _blame.IsVisible = false;
            if (!_closing) { _blame.SetBlame(new("", null, [], [])); }
        }
    }

    private void UpdateVisibility()
    { _normal.IsVisible = !IsBlameActive; _blame.IsVisible = IsBlameActive; }

    private void Track(Task task)
    {
        _pending.Add(task); _ = ForgetAsync(task);
        async Task ForgetAsync(Task pending)
        {
            try { await pending; }
            catch { /* The caller or interaction handler reports its own error; tracking is only a drainage barrier. */ }
            finally { _pending.Remove(pending); }
        }
    }

    internal Task CloseAsync()
    {
        if (_close is not null) { return _close; }
        _closing = true; IsEnabled = false; _lifetime.Cancel(); Cancel();
        return _close = DrainAndDisposeAsync();
    }
    private async Task DrainAndDisposeAsync()
    {
        try { await Completion; }
        catch (OperationCanceledException) { }
        catch { /* The initiating reader owns its error. Closing must still drain and dispose. */ }
        Task avatars = _blame.BeginAvatarClose(), tooltip = _blame.TooltipLoading;
        _blame.Dispose();
        try { await Task.WhenAll(avatars, tooltip); }
        catch (OperationCanceledException) { }
        finally { _lifetime.Dispose(); _presentation.Dispose(); }
    }
}
