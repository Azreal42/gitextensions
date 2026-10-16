using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitCommands.Settings;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

internal sealed record RevisionDiffFileSelection(GitRevisionDiffGroup Group, GitRevisionFile File);

public sealed partial class MainWindow
{
    private readonly TreeView _revisionFiles = new() { Name = "RevisionFiles" };
    private FileStatusListView? _revisionFilesView;
    private readonly RevisionDiffPreferencesStore _revisionDiffPreferencesStore = new();
    private RevisionDiffPreferences _revisionDiffPreferences = new();
    private IReadOnlyList<GitRevisionDiffGroup> _revisionDiffGroups = [];
    private GitComparisonDiffOptions _revisionViewerOptions = new();
    private MenuItem? _revisionTreatAsText;
    private FileContentView? _commitContent;
    private FileContentView? _changesContent;
    private FileContentView? _fileContent;
    private bool _loadingCommitFiles;
    private bool _pendingDiffAppearance;
    private Button? _stageLinesButton, _unstageLinesButton, _stageHunksButton, _unstageHunksButton;
    private GitComparisonDiffOptions _workingViewerOptions = new();

    private void UpdatePartialStageActions()
    {
        bool available = !_busy && _workingDiffRead.Current is null && _repository is { IsBare: false } && _changesDiffView?.CanStagePatch == true
            && _selectedPatch?.Hunks.Count is > 0;
        bool staged = _stagedDiff.IsChecked == true;
        if (_changesDiffView is not null)
        {
            _changesDiffView.ComparisonMenu.SetHostBusy(_busy);
            _changesDiffView.RenderingMenu.SetHostBusy(_busy);
        }
        if (_commitContent is not null)
        {
            _commitContent.TextView.ComparisonMenu.SetHostBusy(_busy);
            _commitContent.TextView.RenderingMenu.SetHostBusy(_busy);
        }
        if (_stageLinesButton is not null) { _stageLinesButton.IsEnabled = available && !staged; }
        if (_unstageLinesButton is not null) { _unstageLinesButton.IsEnabled = available && staged; }
        if (_stageHunksButton is not null) { _stageHunksButton.IsEnabled = available && !staged; }
        if (_unstageHunksButton is not null) { _unstageHunksButton.IsEnabled = available && staged; }
    }

    private Control BuildFileContent()
    {
        _fileContent = new FileContentView(_fileView);
        return _fileContent;
    }

    private void RequestDiffAppearanceReload(object? sender, EventArgs args)
    {
        _pendingDiffAppearance = true;
        UpdatePartialStageActions();
        if (!_busy) { Dispatcher.UIThread.Post(async () => await ReloadDiffAppearancesAsync()); }
    }

    private async Task ReloadDiffAppearancesAsync()
    {
        if (!_pendingDiffAppearance || _busy || _lifetime.IsCancellationRequested) { return; }
        _pendingDiffAppearance = false;
        if (_revisionFiles.SelectedItem is TreeViewItem item)
        { await ReadRevisionDiffAsync(item); }
        if (SelectedWorkingChange is not null) { await LoadSelectedDiffAsync(); }
    }

    private Control BuildCommitFilesView()
    {
        _revisionDiffPreferences = _revisionDiffPreferencesStore.Load();
        _commitContent = new FileContentView(_diff, new DiffView(_diff) { Name = "CommitDiff" });
        _revisionViewerOptions = _commitContent.TextView.ComparisonMenu.Options;
        _commitContent.TextView.ComparisonMenu.OptionsChanged += async (_, _) =>
            await ChangeRevisionViewerOptionsAsync(_commitContent.TextView.ComparisonMenu.Options);
        _commitContent.TextView.DisplayOptionsChanged += RequestDiffAppearanceReload;
        _revisionFiles.SelectionChanged += async (_, args) =>
        {
            if (args.Source == _revisionFiles && !_selectingFileContext && !_loadingCommitFiles && _revisionFilesView?.IsUpdating != true && !_refreshing && !_busy)
            {
                if (_revisionFiles.SelectedItem is TreeViewItem item) { await ReadRevisionDiffAsync(item); }
                else { ClearRevisionPreview(); }
            }
        };

        MenuItem allParents = new()
        {
            Name = "RevisionDiffAllParents", Header = T("Show file differences for all parents in browse dialog"),
            ToggleType = MenuItemToggleType.CheckBox, IsChecked = _revisionDiffPreferences.ShowDiffForAllParents
        };
        MenuItem omit = new()
        {
            Name = "RevisionDiffOmitUninteresting", Header = T("Omit uninteresting diff hunks"),
            ToggleType = MenuItemToggleType.CheckBox, IsChecked = _revisionDiffPreferences.OmitUninterestingDiff
        };
        allParents.Click += async (_, _) => await ChangeRevisionDiffPreferencesAsync(
            _revisionDiffPreferences with { ShowDiffForAllParents = allParents.IsChecked });
        omit.Click += async (_, _) => await ChangeRevisionDiffPreferencesAsync(
            _revisionDiffPreferences with { OmitUninterestingDiff = omit.IsChecked });
        _revisionFilesView = new FileStatusListView(_revisionFiles) { Name = "RevisionFileStatusList", GroupByRevision = true };
        ConfigureRulesFileList(_revisionFilesView, worktreeContext: false);
        ConfigureFileContextMenu(_revisionFiles, isFileTree: false);
        _revisionFilesView.SettingsMenu.Items.Add(allParents);
        _revisionFilesView.SettingsMenu.Items.Add(omit);
        _revisionFilesView.Toolbar.Children.Insert(1, RevisionDiffIconButton("RevisionDiffRefresh", "Refresh", "ReloadRevisions.png", () =>
            _commits.SelectedItem is GitCommit commit ? ReadCommitFilesAsync(commit.Hash) : Task.CompletedTask));
        _revisionFilesView.DisplayChanged += async (_, _) =>
        {
            if (_loadingCommitFiles || _refreshing || _busy || _repository is null) { return; }
            if (_revisionFiles.SelectedItem is TreeViewItem selected)
            { await ReadRevisionDiffAsync(selected); }
            else { ClearRevisionPreview(); }
        };
        _revisionFilesView.Toolbar.Children.Insert(_revisionFilesView.Toolbar.Children.Count - 1,
            RevisionDiffIconButton("RevisionFilesFindInCommitFiles", "Find in commit files using git-grep...", "ViewFile.png", OpenFileGrepFromDiffAsync));
        Grid split = new() { Name = "RevisionDiffSplit", ColumnDefinitions = new ColumnDefinitions("420,4,*") };
        split.Children.Add(ClassicChrome.Border(_revisionFilesView, new Thickness(0, 0, 1, 0)));
        GridSplitter splitter = new() { Name = "RevisionDiffSplitter", Width = 4, MinWidth = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        split.Children.Add(splitter);
        DockPanel viewer = new();
        ConfigureRevisionViewer();
        viewer.Children.Add(_commitContent);
        Grid.SetColumn(viewer, 2);
        split.Children.Add(viewer);
        return split;
    }

    private Button RevisionDiffIconButton(string name, string tooltip, string icon, Func<Task> action)
    {
        Button button = new() { Name = name, Width = 24, Height = 24, MinWidth = 0, Padding = new Thickness(0) };
        StackPanel content = (StackPanel)IconLabel(tooltip, icon);
        content.Children.RemoveAt(content.Children.Count - 1);
        button.Content = content;
        ToolTip.SetTip(button, T(tooltip));
        button.Click += async (_, _) =>
        {
            if (_busy) { return; }
            try { await action(); } catch (Exception exception) { AppendOutput(exception.Message); }
        };
        _actionButtons.Add(button);
        return button;
    }

    private void ConfigureRevisionViewer()
    {
        _commitContent!.TextView.Toolbar.SetNames("RevisionDiff", "RevisionViewerToolbar");
        DiffViewerComparisonMenu options = _commitContent!.TextView.ComparisonMenu;
        object[] viewerMenus = _commitContent.TextView.CreateContextMenuItems("RevisionDiff").ToArray();
        _revisionTreatAsText = new MenuItem { Name = "RevisionDiffTreatAsText", Header = T("Treat all files as text"), ToggleType = MenuItemToggleType.CheckBox };
        _revisionTreatAsText.Click += (_, _) => options.SetOptions(options.Options with { TreatAllFilesAsText = _revisionTreatAsText.IsChecked });
        _commitContent.TextView.ContextMenu = new ContextMenu { ItemsSource = new object[] { _revisionTreatAsText }.Concat(viewerMenus).ToArray() };
    }

    private async Task ChangeRevisionViewerOptionsAsync(GitComparisonDiffOptions options)
    {
        if (_busy) { return; }
        _revisionViewerOptions = options;
        if (_revisionTreatAsText is not null) { _revisionTreatAsText.IsChecked = options.TreatAllFilesAsText; }
        if (_revisionFiles.SelectedItem is TreeViewItem selected)
        {
            await ReadRevisionDiffAsync(selected);
        }
    }

    private async Task ChangeRevisionDiffPreferencesAsync(RevisionDiffPreferences preferences)
    {
        if (_busy) { return; }
        _revisionDiffPreferences = preferences;
        _revisionDiffPreferencesStore.Save(preferences);
        if (_commits.SelectedItem is GitCommit commit)
        {
            await ReadCommitFilesAsync(commit.Hash);
        }
    }

    private void ClearRevisionDiff()
    {
        _revisionFilesView?.SetGroups([], preserveSelection: false);
        _revisionDiffGroups = [];
        if (_commitContent is not null) { _commitContent.TextView.Mode = DiffViewMode.Auto; }
    }

    private void ClearRevisionPreview()
    {
        _revisionDiffRead.Cancel();
        _commitContent?.ClearImage(); _diff.Text = "";
        if (_commitContent is not null) { _commitContent.TextView.DocumentIdentity = null; }
    }

    private async Task LoadCommitFilesAsync(GitRepository repository, string revision, object? preservedSelection = null, CancellationToken? cancellationToken = null)
    {
        object? previous = (_revisionFiles.SelectedItem as TreeViewItem)?.Tag ?? preservedSelection;
        CancellationToken token = cancellationToken ?? Token;
        string selectionIdentity = RevisionSelectionIdentity;
        GitCommit[] selectedCommits = SelectedRevisionDiffCommits();
        string[] revisions = selectedCommits.Length > 0 ? selectedCommits.Select(commit => commit.Hash).ToArray() : [revision];
        IReadOnlyList<GitRevisionDiffGroup> groups = await repository.GetRevisionDiffGroupsAsync(
            revisions, _revisionDiffPreferences.ShowDiffForAllParents, token);
        token.ThrowIfCancellationRequested();
        _loadingCommitFiles = true;
        TreeViewItem? selected;
        try
        {
            _revisionDiffGroups = groups;
            _revisionFilesView!.SetGroups(groups.Select((group, index) => new FileStatusListGroup(group,
                $"{index}:{group.Kind}:{group.BaseRevision}:{group.Revision}", RevisionDiffGroupHeader(group),
                group.Files.Select(file => new FileStatusListEntry(new RevisionDiffFileSelection(group, file), file.Path,
                    file.OriginalPath, file.Status, file.SimilarityPercent, file.DiffStatus)).ToArray(),
                AlwaysVisible: group.Kind == GitRevisionDiffGroupKind.RangeDiff,
                IsBaseComparison: group.Kind is GitRevisionDiffGroupKind.BaseToFirst or GitRevisionDiffGroupKind.BaseToSecond,
                Name: "RevisionDiffGroup_" + group.Kind)
                {
                    HeaderFactory = (shown, total) => RevisionDiffGroupHeader(group, shown, total),
                    ExpandSmallDiff = group.Kind is GitRevisionDiffGroupKind.Parent or GitRevisionDiffGroupKind.Comparison
                }).ToArray(), preserveSelection: false);
            TreeViewItem[] roots = _revisionFiles.Items.OfType<TreeViewItem>().ToArray();
            TreeViewItem[] fileItems = FileStatusTreeItems(_revisionFiles).Where(item => item.Tag is RevisionDiffFileSelection).ToArray();
            selected = previous switch
            {
                RevisionDiffFileSelection old => fileItems.FirstOrDefault(item => item.Tag is RevisionDiffFileSelection candidate
                    && candidate.Group.Kind == old.Group.Kind && candidate.Group.BaseRevision == old.Group.BaseRevision
                    && candidate.File.Path == old.File.Path)
                    ?? fileItems.FirstOrDefault(item => item.Tag is RevisionDiffFileSelection candidate && candidate.File.Path == old.File.Path),
                GitRevisionDiffGroup old => roots.FirstOrDefault(item => item.Tag is GitRevisionDiffGroup candidate
                    && candidate.Kind == old.Kind && candidate.BaseRevision == old.BaseRevision),
                _ => null
            };
            selected ??= fileItems.FirstOrDefault() ?? roots.FirstOrDefault();
            if (selected is not null)
            {
                _revisionFilesView.SelectTags([selected.Tag!]);
            }
        }
        finally { _loadingCommitFiles = false; }
        _loadedDiffSelection = selectionIdentity;
        if (selected is not null)
        {
            if (_busy) { await ShowRevisionDiffItemAsync(repository, selected, token); }
            else { await ReadRevisionDiffAsync(selected, token); }
        }
        else
        {
            string patch = _revisionFilesView!.FilterText.Length == 0
                ? await repository.GetCommitDiffAsync(revision, cancellationToken: token) : "";
            token.ThrowIfCancellationRequested();
            _commitContent?.ClearImage();
            if (_commitContent is not null) { _commitContent.TextView.Mode = DiffViewMode.Auto; }
            _diff.Text = patch;
        }
    }

    private Control RevisionDiffGroupHeader(GitRevisionDiffGroup group, int? shown = null, int? total = null)
    {
        string shortRevision = group.BaseRevision is null ? group.Revision[..Math.Min(9, group.Revision.Length)]
            : group.BaseRevision[..Math.Min(9, group.BaseRevision.Length)];
        Control header = group.Kind switch
        {
            GitRevisionDiffGroupKind.Combined => IconLabel("Combined diff", "DiffC.png"),
            GitRevisionDiffGroupKind.RangeDiff => IconLabel(T("Range diff") + " " + group.RangeDiff!.First.CommitCount + "↓ "
                + group.RangeDiff.Second.CommitCount + "↑ BASE " + group.RangeDiff.CommonAncestor[..Math.Min(9, group.RangeDiff.CommonAncestor.Length)], "DiffR.png"),
            GitRevisionDiffGroupKind.BaseToSecond => IconLabel(T("Diff BASE with") + " B " + group.Revision[..Math.Min(9, group.Revision.Length)], "DiffB.png"),
            GitRevisionDiffGroupKind.BaseToFirst => IconLabel(T("Diff BASE with") + " A " + group.Revision[..Math.Min(9, group.Revision.Length)], "DiffA.png"),
            _ => IconLabel(T("Diff with A ") + shortRevision, "Diff.png")
        };
        if (group.Kind != GitRevisionDiffGroupKind.RangeDiff && header is StackPanel panel
            && panel.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock text)
        {
            int count = total ?? group.Files.Count;
            string fraction = shown is int visible && visible < count ? visible + "/" : "";
            string? subject = _history.FirstOrDefault(commit => commit.Hash == group.BaseRevision)?.Subject;
            text.Text = "(" + fraction + count + ") " + text.Text + (subject is null ? "" : ": " + subject);
            panel.Height = ClassicTreeViewAppearance.RowHeight;
        }
        return header;
    }

    private bool SelectRevisionDiffGroup(GitRevisionDiffGroupKind kind)
    {
        TreeViewItem? item = _revisionFiles.Items.OfType<TreeViewItem>().FirstOrDefault(root => root.Tag is GitRevisionDiffGroup group && group.Kind == kind);
        if (item is null) { return false; }
        _revisionFiles.SelectedItem = item;
        return true;
    }

    private async Task ShowRevisionDiffItemAsync(GitRepository repository, TreeViewItem item, CancellationToken? cancellationToken = null)
    {
        if (_commitContent is null) { return; }
        CancellationToken token = cancellationToken ?? Token;
        DiffView viewer = _commitContent.TextView;
        GitComparisonDiffOptions options = _revisionViewerOptions;
        GitDiffDisplayOptions display = viewer.DisplayOptions;
        string? folder = _revisionFilesView?.SelectedFolderDescription;
        Action publish;
        string? identity = null, path = null;
        DiffViewMode mode = DiffViewMode.Auto;
        if (item.Tag is GitRevisionDiffGroup group)
        {
            identity = string.Join("\0", repository.Path, "group", group.Kind, group.Revision, group.BaseRevision,
                group.RangeDiff?.First, group.RangeDiff?.Second);
            if (group.Kind == GitRevisionDiffGroupKind.RangeDiff && group.RangeDiff is not null)
            {
                mode = DiffViewMode.RangeDiff;
                GitDiffDocument document = await repository.GetRangeDiffDocumentAsync(group.RangeDiff, new GitRangeDiffOptions
                {
                    ContextLines = options.ContextLines, ShowEntireFile = options.ShowEntireFile,
                    IgnoreWhitespace = options.IgnoreWhitespace, TreatAllFilesAsText = options.TreatAllFilesAsText
                }, display, token);
                publish = () => viewer.ShowDocument(document);
            }
            else { publish = () => _diff.Text = T("Select a file to inspect its diff."); }
        }
        else if (item.Tag is RevisionDiffFileSelection selection)
        {
            identity = string.Join("\0", repository.Path, selection.File.Path, selection.Group.Kind,
                selection.Group.Revision, selection.Group.BaseRevision, selection.File.OriginalPath);
            path = selection.File.Path;
            if (selection.Group.Kind == GitRevisionDiffGroupKind.Combined && selection.Group.CombinedDiff is not null)
            {
                mode = DiffViewMode.Combined;
                GitDiffDocument document = await repository.GetCombinedFileDiffDocumentAsync(selection.Group.CombinedDiff, selection.File,
                    new GitCombinedDiffOptions { OmitUninterestingDiff = _revisionDiffPreferences.OmitUninterestingDiff, DiffOptions = options }, display, token);
                publish = string.IsNullOrWhiteSpace(document.Patch)
                    ? () => _diff.Text = T("Uninteresting diff hunks are omitted.") : () => viewer.ShowDocument(document);
            }
            else
            {
                mode = DiffViewMode.Unified;
                GitRevisionChanges changes = new(selection.Group.Revision, selection.Group.BaseRevision, selection.Group.Files);
                GitRevisionFile file = selection.File;
                string? imageRevision = file.IsDeleted ? changes.BaseRevision : changes.Revision;
                if (!options.TreatAllFilesAsText && FileContentView.IsImagePath(file.Path) && imageRevision is not null
                    && (await repository.GetTreeAsync(imageRevision, file.Path, token)).FirstOrDefault(entry => entry.Path == file.Path)?.IsSubmodule is not true)
                {
                    byte[] bytes = await repository.GetFileBytesAsync(file.Path, imageRevision, token);
                    publish = () => _commitContent.ShowImageOrHex(file.Path, bytes);
                }
                else
                {
                    GitDiffDocument document = await repository.GetRevisionFileDiffDocumentAsync(changes, file, options, display, token);
                    publish = () => viewer.ShowDocument(document);
                }
            }
        }
        else
        {
            mode = folder is null ? DiffViewMode.Auto : DiffViewMode.PlainText;
            publish = () => _diff.Text = folder ?? T("Select a file to inspect its diff.");
        }
        token.ThrowIfCancellationRequested();
        await viewer.ConfigureRepositoryAsync(repository, token);
        token.ThrowIfCancellationRequested();
        // Keep the previous document visible until its replacement is ready. No
        // window-wide enable/disable cycle, focus change or empty intermediate text.
        viewer.BeginDocumentLoad(); _commitContent.ClearImage();
        viewer.DocumentIdentity = identity; viewer.SyntaxPath = path; viewer.Mode = mode;
        publish();
    }

    private async Task LoadChangedFileContentAsync(GitRepository repository, GitChange change, bool staged, CancellationToken? cancellationToken = null)
    {
        if (_changesContent is null) { return; }
        CancellationToken token = cancellationToken ?? Token;
        DiffView viewer = _changesContent.TextView;
        // The previous pixels can stay visible, but their patch must stop being
        // actionable immediately, including when the replacement read fails.
        viewer.BeginDocumentLoad();
        viewer.DocumentIdentity = string.Join('\0', repository.Path, change.Path, change.OriginalPath, _historyGraph.CurrentHead, staged ? "index" : "worktree");
        GitComparisonDiffOptions options = _workingViewerOptions;
        Action publish;
        if (!change.IsSubmodule && !change.IsConflict && FileContentView.IsImagePath(change.Path))
        {
            byte[] bytes;
            if (staged)
            {
                bytes = change.IndexStatus == 'D'
                    ? await repository.GetFileBytesAsync(change.OriginalPath ?? change.Path, "HEAD", token)
                    : await repository.GetIndexFileBytesAsync(change.Path, token);
            }
            else
            {
                string path = Path.Combine(repository.Path, change.Path);
                bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : await repository.GetIndexFileBytesAsync(change.Path, token);
            }
            publish = () => _changesContent.ShowImageOrHex(change.Path, bytes);
        }
        else if (change.IsUntracked)
        {
            await using FileStream stream = File.OpenRead(Path.Combine(repository.Path, change.Path));
            byte[] bytes = new byte[(int)Math.Min(stream.Length, 2_000_000)];
            await stream.ReadExactlyAsync(bytes, token);
            string encoding = await repository.GetFileEncodingNameAsync(token);
            bool truncated = stream.Length > bytes.Length;
            publish = () => _changesContent.ShowFile(change.Path, bytes, encoding, truncated);
        }
        else
        {
            GitDiffDocument document = await repository.GetDiffDocumentAsync(change.Path, staged,
                options: options, displayOptions: viewer.DisplayOptions, originalPath: change.OriginalPath, cancellationToken: token);
            publish = () => viewer.ShowDocument(document);
        }
        token.ThrowIfCancellationRequested();
        await viewer.ConfigureRepositoryAsync(repository, token);
        token.ThrowIfCancellationRequested();
        _changesContent.ClearImage();
        viewer.Mode = change.IsUntracked ? DiffViewMode.PlainText : DiffViewMode.Auto;
        viewer.SyntaxPath = change.Path;
        publish();
    }
}
