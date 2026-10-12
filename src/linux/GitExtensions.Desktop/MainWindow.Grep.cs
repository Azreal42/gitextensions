using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBox _fileGrepExpression = new() { Name = "FileGrepExpression", Height = 24, Watermark = "git-grep regular expression…" };
    private readonly List<string> _fileGrepHistory = [];
    private FileGrepSettings _fileGrepSettings = new(ShowSearchBox: false);
    private Control? _fileGrepBar;
    private string[] _allFileTreePaths = [];
    private GitRepository? _fileTreeRepository;
    private string? _fileTreeRevision;
    private GitGrepResult? _fileGrepResult;
    private CancellationTokenSource? _fileGrepRead, _fileGrepNavigation;
    private long _fileGrepVersion;
    private bool _updatingFileGrep, _settingGrepTree;
    private int _renderingGrepFiles;
    internal bool IsFileGrepBusy { get; private set; }
    internal GitGrepResult? FileGrepResult => _fileGrepResult;

    private Control BuildFilesPanel()
    {
        _tree.Name = "CommitFileTree";
        Button search = Button("Search…", SearchAsync); search.Name = "FindInCommitFiles";
        Button clear = new() { Name = "ClearFileGrep", Content = "×", Height = 24, Width = 24, MinWidth = 0, Padding = default };
        clear.Click += (_, _) => UpdateFileGrepSettings(_fileGrepSettings with { Expression = "" });
        Grid filter = new() { Name = "FileGrepBar", ColumnDefinitions = new("*,Auto"), IsVisible = false };
        filter.Children.Add(_fileGrepExpression); Grid.SetColumn(clear, 1); filter.Children.Add(clear); _fileGrepBar = filter;
        _fileGrepExpression.PropertyChanged += (_, e) =>
        {
            if (!_updatingFileGrep && e.Property == TextBox.TextProperty)
            {
                UpdateFileGrepSettings(_fileGrepSettings with { Expression = _fileGrepExpression.Text ?? "" });
                if (_fileGrepSettings.Expression.Length > 0) { _ = SearchFileGrepAsync(delay: true); }
            }
        };
        Closed += (_, _) => { CancelFileGrep(); _fileGrepNavigation?.Cancel(); };
        return Panel(_tree, Row(_filePath, Button("Blame", BlameAsync), Button("File history", FileHistoryAsync), search), filter);
    }

    private Control BuildGrepFileContent()
    {
        Control content = BuildFileContent(); _fileContent!.Name = "CommitFileContent";
        _fileContent.TextView.FileEncodingChanged += (_, _) =>
        { if (_renderingGrepFiles == 0 && _fileGrepSettings.ShowSearchBox && _fileGrepSettings.Expression.Length > 0) { _ = SearchFileGrepAsync(); } };
        return content;
    }

    private void CancelFileGrepForCommit(string revision)
    {
        if (_fileTreeRevision != revision)
        {
            CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
            ClearFileGrepViewer();
        }
    }

    private async Task ShowFileGrepDialogAsync()
    {
        if (_repository is null || _fileTreeRevision is null) { return; }
        UpdateFileGrepSettings(_fileGrepSettings with { ShowSearchBox = true });
        FindInCommitFilesDialog dialog = new(_fileGrepSettings, _fileGrepHistory, UpdateFileGrepSettings, () => SearchFileGrepAsync()) { RequestedThemeVariant = WorkflowOwner.ActualThemeVariant };
        await dialog.ShowDialog(WorkflowOwner);
        if (!_fileGrepSettings.ShowSearchBox || _fileGrepSettings.Expression.Length == 0) { ClearFileGrep(); }
        else if (_fileGrepResult is not null && _fileContent?.TextView.IsDocumentReady == true) { _fileContent.TextView.Editor.TextArea.Focus(); }
    }

    private void UpdateFileGrepSettings(FileGrepSettings settings)
    {
        _fileGrepSettings = settings; _updatingFileGrep = true;
        try { _fileGrepExpression.Text = settings.Expression; if (_fileGrepBar is not null) { _fileGrepBar.IsVisible = settings.ShowSearchBox; } }
        finally { _updatingFileGrep = false; }
        if (!settings.ShowSearchBox || settings.Expression.Length == 0) { ClearFileGrep(); }
    }

    private void SetFileTreePaths(GitRepository repository, string revision, string[] paths)
    {
        bool changed = _fileTreeRepository?.Path != repository.Path || _fileTreeRevision != revision;
        if (changed) { CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null; }
        _fileTreeRepository = repository; _fileTreeRevision = revision; _allFileTreePaths = paths;
        _settingGrepTree = true;
        try { _tree.ItemsSource = paths; }
        finally { _settingGrepTree = false; }
        if (_fileGrepSettings.ShowSearchBox && _fileGrepSettings.Expression.Length > 0)
        { Dispatcher.UIThread.Post(async () => await SearchFileGrepAsync()); }
    }

    private void CancelFileGrep()
    {
        _fileGrepVersion++; _fileGrepRead?.Cancel(); _fileGrepRead?.Dispose(); _fileGrepRead = null; IsFileGrepBusy = false;
    }

    private void ClearFileGrep()
    {
        CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
        _settingGrepTree = true;
        try { _tree.ItemsSource = _allFileTreePaths; }
        finally { _settingGrepTree = false; }
    }

    private void ClearFileGrepViewer()
    {
        _fileContent?.ClearImage(); _fileView.Text = null;
        if (_fileContent is not null)
        {
            _fileContent.TextView.DocumentIdentity = null; _fileContent.TextView.SyntaxPath = null;
            // Clearing the TextBox can raise SourceChanged and mark a document ready.
            // Suspend reading after that notification: there is no source file to navigate.
            _fileContent.TextView.BeginDocumentLoad();
        }
    }

    private async Task SearchFileGrepAsync(bool delay = false)
    {
        if (_lifetime.IsCancellationRequested || _repository is not GitRepository repository || _fileTreeRevision is not string revision) { return; }
        FileGrepSettings settings = _fileGrepSettings;
        if (!settings.ShowSearchBox || settings.Expression.Length == 0) { ClearFileGrep(); return; }
        CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
        long version = _fileGrepVersion;
        LastException = null;
        CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _fileGrepRead = read;
        CancellationToken token = read.Token; IsFileGrepBusy = true;
        try
        {
            if (delay) { await Task.Delay(200, token); }
            GitGrepResult result = await repository.GrepAsync(new(settings.Expression, Revision: revision, IgnoreCase: !settings.MatchCase,
                MatchWholeWord: settings.MatchWholeWord, UserArguments: settings.UserArguments, EncodingName: _fileContent?.TextView.FileEncodingName), token);
            token.ThrowIfCancellationRequested();
            if (version != _fileGrepVersion || _repository != repository || _fileTreeRevision != revision) { return; }
            _fileGrepResult = result;
            _fileGrepHistory.RemoveAll(item => item == settings.Expression); _fileGrepHistory.Insert(0, settings.Expression);
            if (_fileGrepHistory.Count > 30) { _fileGrepHistory.RemoveRange(30, _fileGrepHistory.Count - 30); }
            string? previous = _tree.SelectedItem as string;
            string[] paths = result.Matches.Where(match => !match.IsContext && match.HasLosslessPath)
                .Select(match => match.Path).Concat(result.BinaryMatches.Where(match => match.HasLosslessPath).Select(match => match.Path))
                .Distinct(StringComparer.Ordinal).ToArray();
            _settingGrepTree = true;
            try { _tree.ItemsSource = paths; _tree.SelectedItem = paths.FirstOrDefault(path => path == previous) ?? paths.FirstOrDefault(); }
            finally { _settingGrepTree = false; }
            int blocked = result.Matches.Count(match => !match.IsContext && !match.HasLosslessPath) + result.BinaryMatches.Count(match => !match.HasLosslessPath);
            _state.Text = result.Matches.Count(match => !match.IsContext) + " source-line matches in " + paths.Length + " files"
                + (result.BinaryMatches.Count == 0 ? "" : "; " + result.BinaryMatches.Count + " binary file matches (no line or column)")
                + (blocked == 0 ? "" : "; non-UTF8 pathnames cannot be navigated") + (result.StandardError.Length == 0 ? "" : "; " + result.StandardError.Trim());
            if (_tree.SelectedItem is string selected) { _filePath.Text = selected; await ShowGrepFileAsync(selected); }
            else { _filePath.Text = null; ClearFileGrepViewer(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _fileGrepVersion && _repository == repository)
            {
                LastException = ex; _state.Text = ex.Message; AppendOutput(ex.Message);
                _settingGrepTree = true; try { _tree.ItemsSource = _allFileTreePaths; } finally { _settingGrepTree = false; }
            }
        }
        finally { if (version == _fileGrepVersion) { IsFileGrepBusy = false; } }
    }

    private Task ShowSelectedTreeFileAsync(string path)
        => _fileGrepResult is null ? ShowFileAsync(path) : ShowGrepFileAsync(path);

    private async Task ShowGrepFileAsync(string path)
    {
        if (_repository is not GitRepository repository || _fileGrepResult is not GitGrepResult result || _fileContent is null) { return; }
        GitGrepMatch? hit = result.Matches.FirstOrDefault(match => !match.IsContext && match.Path == path);
        GitGrepBinaryMatch? binary = result.BinaryMatches.FirstOrDefault(match => match.Path == path);
        if (hit is null && binary is null || hit?.HasLosslessPath == false || binary?.HasLosslessPath == false)
        { _state.Text = "This grep pathname cannot be navigated without changing its bytes."; return; }
        _fileGrepNavigation?.Cancel(); _fileGrepNavigation?.Dispose();
        CancellationTokenSource navigation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _fileGrepNavigation = navigation;
        long version = _fileGrepVersion; CancellationToken token = navigation.Token;
        try
        {
            DiffView viewer = _fileContent.TextView; _renderingGrepFiles++;
            viewer.BeginDocumentLoad(); viewer.DocumentIdentity = string.Join('\0', repository.Path, path, result.Revision ?? (result.Cached ? "index" : "worktree"), "blob");
            await viewer.ConfigureRepositoryAsync(repository, token);
            byte[] bytes = result.Revision is string hash ? await repository.GetFileBytesAsync(path, hash, token)
                : await repository.GetComparisonFileBytesAsync(new GitCompareRevision(Kind: result.Cached ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree), path, token);
            token.ThrowIfCancellationRequested();
            if (version != _fileGrepVersion || _repository != repository || _fileGrepResult != result || _tree.SelectedItem as string != path) { return; }
            _fileContent.ShowFile(path, bytes, result.EncodingName);
            GitDecodedFile decoded = GitFileEncodingCodec.DecodeFile(bytes, viewer.FileEncodingName ?? result.EncodingName);
            if (decoded.IsLossless && hit?.TextColumn is int column && viewer.GoToFileLine(hit.LineNumber))
            {
                var line = viewer.Editor.Document.GetLineByNumber(hit.LineNumber);
                viewer.CaretOffset = line.Offset + Math.Min(column - 1, line.Length); viewer.Editor.ScrollTo(hit.LineNumber, column);
            }
            else if (decoded.IsBinary && !_fileContent.IsImage)
            { _state.Text += "; this source cannot be read as text in " + decoded.EncodingName + "; binary preview shown without a source position"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _fileGrepVersion) { LastException = ex; _state.Text = ex.Message; AppendOutput(ex.Message); } }
        finally { _renderingGrepFiles--; }
    }
}
