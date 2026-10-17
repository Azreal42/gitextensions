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
    private IReadOnlyDictionary<string, GitTreeEntry> _fileTreeEntries = new Dictionary<string, GitTreeEntry>();
    private GitRepository? _fileTreeRepository;
    private string? _fileTreeRevision;
    private GitGrepResult? _fileGrepResult;
    private CancellationTokenSource? _fileGrepRead, _fileGrepNavigation;
    private long _fileGrepVersion;
    private bool _updatingFileGrep, _settingGrepTree;
    private int _renderingGrepFiles;
    internal bool IsFileGrepBusy { get; private set; }
    internal GitGrepResult? FileGrepResult => _fileGrepResult;

    private FileStatusListView? _fileTreeView;
    private string? SelectedTreePath => (_tree.SelectedItem as TreeViewItem)?.Tag as string;

    private Control BuildFileTreePane()
    {
        Grid pane = new() { Name = "FileTreeSplit", ColumnDefinitions = new("240,4,*") };
        pane.Children.Add(ClassicChrome.Border(BuildFilesPanel(), new Thickness(0, 0, 1, 0)));
        GridSplitter splitter = new() { Width = 4, MinWidth = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); pane.Children.Add(splitter);
        Control content = BuildGrepFileContent(); Grid.SetColumn(content, 2); pane.Children.Add(content);
        return pane;
    }

    private Control BuildFilesPanel()
    {
        _tree.Name = "CommitFileTree";
        _fileTreeView = new FileStatusListView(_tree, FileTreeEntryHeader, fileTreeMode: true) { Name = "CommitFileStatusList" };
        ConfigureFileContextMenu(_tree, isFileTree: true);
        _fileTreeView.DisplayChanged += async (_, _) =>
        {
            if (!_refreshing && !_settingGrepTree && !_busy)
            {
                if (SelectedTreePath is string path) { _filePath.Text = path; await ShowSelectedTreeFileAsync(path); }
                else { ShowSelectedTreeFolder(); }
            }
        };
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
        DockPanel panel = new(); DockPanel.SetDock(filter, Dock.Top); panel.Children.Add(filter); panel.Children.Add(_fileTreeView);
        return panel;
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

    private void SetFileTreePaths(GitRepository repository, string revision, IReadOnlyList<GitTreeEntry> entries)
    {
        _fileTreeEntries = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        string[] paths = entries.Select(entry => entry.Path).ToArray();
        bool changed = _fileTreeRepository?.Path != repository.Path || _fileTreeRevision != revision;
        if (changed) { CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null; }
        _fileTreeRepository = repository; _fileTreeRevision = revision; _allFileTreePaths = paths;
        _settingGrepTree = true;
        try { UpdateFileTreeEntries(paths); }
        finally { _settingGrepTree = false; }
        if (_fileGrepSettings.ShowSearchBox && _fileGrepSettings.Expression.Length > 0)
        { Dispatcher.UIThread.Post(async () => await SearchFileGrepAsync()); }
    }

    private Control FileTreeEntryHeader(FileStatusListEntry entry)
        => _fileTreeView!.CreateFileHeader(entry, _fileTreeEntries.GetValueOrDefault(entry.Path)?.IsSubmodule == true ? "FolderSubmodule.png" : "File.png");

    private void ShowSelectedTreeFolder()
    {
        _treeFileRead.Cancel();
        _filePath.Text = null; ClearFileGrepViewer();
        if (_fileTreeView?.SelectedFolderDescription is not string text || _fileContent is null) { return; }
        _fileContent.TextView.Mode = DiffViewMode.PlainText; _fileView.Text = text;
        _fileContent.TextView.CompletePlainTextDocumentLoad();
    }

    private void UpdateFileTreeEntries(string[] paths)
    {
        _fileTreeView!.SetGroups([new FileStatusListGroup("tree", "tree", new TextBlock(),
            paths.Select(path => new FileStatusListEntry(path, path, Status: ' ')).ToArray())]);
    }

    private void CancelFileGrep()
    {
        _fileGrepVersion++; _fileGrepRead?.Cancel(); _fileGrepRead?.Dispose(); _fileGrepRead = null; IsFileGrepBusy = false;
    }

    private void ClearFileGrep()
    {
        CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
        _settingGrepTree = true;
        try { UpdateFileTreeEntries(_allFileTreePaths); }
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
            string? previous = SelectedTreePath;
            string[] paths = result.Matches.Where(match => !match.IsContext && match.HasLosslessPath)
                .Select(match => match.Path).Concat(result.BinaryMatches.Where(match => match.HasLosslessPath).Select(match => match.Path))
                .Distinct(StringComparer.Ordinal).ToArray();
            _settingGrepTree = true;
            try
            {
                UpdateFileTreeEntries(paths);
                if ((paths.FirstOrDefault(path => path == previous) ?? paths.FirstOrDefault()) is string selectedPath)
                { _fileTreeView!.SelectTags([selectedPath]); }
            }
            finally { _settingGrepTree = false; }
            int blocked = result.Matches.Count(match => !match.IsContext && !match.HasLosslessPath) + result.BinaryMatches.Count(match => !match.HasLosslessPath);
            _state.Text = result.Matches.Count(match => !match.IsContext) + " source-line matches in " + paths.Length + " files"
                + (result.BinaryMatches.Count == 0 ? "" : "; " + result.BinaryMatches.Count + " binary file matches (no line or column)")
                + (blocked == 0 ? "" : "; non-UTF8 pathnames cannot be navigated") + (result.StandardError.Length == 0 ? "" : "; " + result.StandardError.Trim());
            if (SelectedTreePath is string selected) { _filePath.Text = selected; await ShowGrepFileAsync(selected); }
            else { _filePath.Text = null; ClearFileGrepViewer(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _fileGrepVersion && _repository == repository)
            {
                LastException = ex; _state.Text = ex.Message; AppendOutput(ex.Message);
                _settingGrepTree = true; try { UpdateFileTreeEntries(_allFileTreePaths); } finally { _settingGrepTree = false; }
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
            if (version != _fileGrepVersion || _repository != repository || _fileGrepResult != result || SelectedTreePath != path) { return; }
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
