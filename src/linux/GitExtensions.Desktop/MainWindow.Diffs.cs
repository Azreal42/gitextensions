using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitCommands.Settings;

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
    private MenuItem? _revisionWholeFile;
    private MenuItem? _revisionTreatAsText;
    private MenuItem[] _revisionWhitespaceOptions = [];
    private FileContentView? _commitContent;
    private FileContentView? _changesContent;
    private FileContentView? _fileContent;
    private bool _loadingCommitFiles;
    private bool _pendingDiffAppearance;

    private void RequestDiffAppearanceReload(object? sender, EventArgs args)
    {
        _pendingDiffAppearance = true;
        if (!_busy) { Dispatcher.UIThread.Post(async () => await ReloadDiffAppearancesAsync()); }
    }

    private async Task ReloadDiffAppearancesAsync()
    {
        if (!_pendingDiffAppearance || _busy || _lifetime.IsCancellationRequested) { return; }
        _pendingDiffAppearance = false;
        if (_revisionFiles.SelectedItem is TreeViewItem item)
        { await RunAsync("File diff", repository => ShowRevisionDiffItemAsync(repository, item), refresh: false); }
        if (SelectedWorkingChange is not null) { await LoadSelectedDiffAsync(); }
    }

    private Control BuildCommitFilesView()
    {
        _revisionDiffPreferences = _revisionDiffPreferencesStore.Load();
        _commitContent = new FileContentView(_diff, new DiffView(_diff) { Name = "CommitDiff" });
        _commitContent.TextView.DisplayOptionsChanged += RequestDiffAppearanceReload;
        _revisionFiles.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 24d) }
        });
        _revisionFiles.SelectionChanged += async (_, args) =>
        {
            if (args.Source == _revisionFiles && !_loadingCommitFiles && _revisionFilesView?.IsUpdating != true && !_refreshing && !_busy
                && _revisionFiles.SelectedItem is TreeViewItem item)
            {
                await RunAsync("File diff", repository => ShowRevisionDiffItemAsync(repository, item), refresh: false);
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
        _revisionFilesView = new FileStatusListView(_revisionFiles, RevisionDiffFileHeader) { Name = "RevisionFileStatusList" };
        _revisionFilesView.SettingsMenu.Items.Add(allParents);
        _revisionFilesView.SettingsMenu.Items.Add(omit);
        _revisionFilesView.Toolbar.Children.Add(RevisionDiffIconButton("RevisionDiffRefresh", "Refresh", "ReloadRevisions.png", () =>
            _commits.SelectedItem is GitCommit commit ? RunAsync("File diff", repository => LoadCommitFilesAsync(repository, commit.Hash), refresh: false) : Task.CompletedTask));
        _revisionFilesView.DisplayChanged += async (_, _) =>
        {
            if (_loadingCommitFiles || _refreshing || _busy || _repository is null) { return; }
            if (_revisionFiles.SelectedItem is TreeViewItem selected)
            { await RunAsync("File diff", repository => ShowRevisionDiffItemAsync(repository, selected), refresh: false); }
            else { _commitContent?.ClearImage(); _diff.Text = ""; }
        };
        Grid split = new() { Name = "RevisionDiffSplit", ColumnDefinitions = new ColumnDefinitions("300,7,*") };
        split.Children.Add(ClassicChrome.Border(_revisionFilesView, new Thickness(0, 0, 1, 0)));
        GridSplitter splitter = new() { Name = "RevisionDiffSplitter", Width = 7, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        split.Children.Add(splitter);
        DockPanel viewer = new();
        Control viewerToolbar = BuildRevisionViewerToolbar();
        DockPanel.SetDock(viewerToolbar, Dock.Top);
        viewer.Children.Add(viewerToolbar);
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

    private Control BuildRevisionViewerToolbar()
    {
        StackPanel toolbar = new() { Name = "RevisionViewerToolbar", Orientation = Orientation.Horizontal, Height = 24 };
        toolbar.Children.Add(RevisionDiffIconButton("RevisionDiffNextChange", "Next change", "ArrowDown.png", () => { _commitContent?.TextView.GoToNextChange(); return Task.CompletedTask; }));
        toolbar.Children.Add(RevisionDiffIconButton("RevisionDiffPreviousChange", "Previous change", "ArrowUp.png", () => { _commitContent?.TextView.GoToPreviousChange(); return Task.CompletedTask; }));
        toolbar.Children.Add(RevisionDiffIconButton("RevisionDiffIncreaseContext", "Increase the number of lines of context", "NumberOfLinesIncrease.png",
            () => ChangeRevisionViewerOptionsAsync(_revisionViewerOptions with { ContextLines = Math.Min(100000, _revisionViewerOptions.ContextLines + 1), ShowEntireFile = false })));
        toolbar.Children.Add(RevisionDiffIconButton("RevisionDiffDecreaseContext", "Decrease the number of lines of context", "NumberOfLinesDecrease.png",
            () => ChangeRevisionViewerOptionsAsync(_revisionViewerOptions with { ContextLines = Math.Max(0, _revisionViewerOptions.ContextLines - 1), ShowEntireFile = false })));
        toolbar.Children.Add(RevisionDiffIconButton("RevisionDiffEntireFile", "Show entire file", "ShowEntireFile.png",
            () => ChangeRevisionViewerOptionsAsync(_revisionViewerOptions with { ShowEntireFile = !_revisionViewerOptions.ShowEntireFile })));
        _revisionWholeFile = new MenuItem { Name = "RevisionDiffEntireFileMenu", Header = T("Show entire file"), ToggleType = MenuItemToggleType.CheckBox };
        _revisionWholeFile.Click += async (_, _) => await ChangeRevisionViewerOptionsAsync(_revisionViewerOptions with { ShowEntireFile = _revisionWholeFile.IsChecked });
        _revisionTreatAsText = new MenuItem { Name = "RevisionDiffTreatAsText", Header = T("Treat all files as text"), ToggleType = MenuItemToggleType.CheckBox };
        _revisionTreatAsText.Click += async (_, _) => await ChangeRevisionViewerOptionsAsync(_revisionViewerOptions with { TreatAllFilesAsText = _revisionTreatAsText.IsChecked });
        string[] whitespaceLabels = ["Do not ignore whitespace", "Ignore whitespace changes at end of line", "Ignore changes in amount of whitespace", "Ignore all whitespace changes"];
        IgnoreWhitespaceKind[] values = [IgnoreWhitespaceKind.None, IgnoreWhitespaceKind.Eol, IgnoreWhitespaceKind.Change, IgnoreWhitespaceKind.AllSpace];
        _revisionWhitespaceOptions = values.Select((value, index) =>
        {
            MenuItem item = new() { Name = "RevisionDiffWhitespace_" + value, Header = T(whitespaceLabels[index]), ToggleType = MenuItemToggleType.Radio,
                GroupName = "RevisionDiffWhitespace", IsChecked = value == _revisionViewerOptions.IgnoreWhitespace };
            item.Click += async (_, _) => await ChangeRevisionViewerOptionsAsync(_revisionViewerOptions with { IgnoreWhitespace = value });
            return item;
        }).ToArray();
        _commitContent!.TextView.ContextMenu = new ContextMenu { ItemsSource = new object[] { _revisionWholeFile, _revisionTreatAsText }
            .Concat(_revisionWhitespaceOptions).Concat(_commitContent.TextView.AppearanceMenu.CreateContextMenuItems()).ToArray() };
        return ClassicChrome.Border(toolbar, new Thickness(0, 0, 0, 1));
    }

    private async Task ChangeRevisionViewerOptionsAsync(GitComparisonDiffOptions options)
    {
        if (_busy) { return; }
        _revisionViewerOptions = options;
        if (_revisionWholeFile is not null) { _revisionWholeFile.IsChecked = options.ShowEntireFile; }
        if (_revisionTreatAsText is not null) { _revisionTreatAsText.IsChecked = options.TreatAllFilesAsText; }
        foreach (MenuItem item in _revisionWhitespaceOptions) { item.IsChecked = item.Name == "RevisionDiffWhitespace_" + options.IgnoreWhitespace; }
        if (_revisionFiles.SelectedItem is TreeViewItem selected)
        {
            await RunAsync("File diff", repository => ShowRevisionDiffItemAsync(repository, selected), refresh: false);
        }
    }

    private async Task ChangeRevisionDiffPreferencesAsync(RevisionDiffPreferences preferences)
    {
        if (_busy) { return; }
        _revisionDiffPreferences = preferences;
        _revisionDiffPreferencesStore.Save(preferences);
        if (_commits.SelectedItem is GitCommit commit)
        {
            await RunAsync("File diff", repository => LoadCommitFilesAsync(repository, commit.Hash), refresh: false);
        }
    }

    private void ClearRevisionDiff()
    {
        _revisionFilesView?.SetGroups([], preserveSelection: false);
        _revisionDiffGroups = [];
        if (_commitContent is not null) { _commitContent.TextView.Mode = DiffViewMode.Auto; }
    }

    private async Task LoadCommitFilesAsync(GitRepository repository, string revision, object? preservedSelection = null)
    {
        object? previous = (_revisionFiles.SelectedItem as TreeViewItem)?.Tag ?? preservedSelection;
        _loadingCommitFiles = true;
        try
        {
            GitCommit[] selectedCommits = SelectedRevisionDiffCommits();
            string[] revisions = selectedCommits.Length > 0 ? selectedCommits.Select(commit => commit.Hash).ToArray() : [revision];
            IReadOnlyList<GitRevisionDiffGroup> groups = await repository.GetRevisionDiffGroupsAsync(
                revisions, _revisionDiffPreferences.ShowDiffForAllParents, Token);
            Token.ThrowIfCancellationRequested();
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
            TreeViewItem? selected = previous switch
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
                await ShowRevisionDiffItemAsync(repository, selected);
            }
            else
            {
                _commitContent?.ClearImage();
                if (_commitContent is not null) { _commitContent.TextView.Mode = DiffViewMode.Auto; }
                _diff.Text = _revisionFilesView.FilterText.Length == 0
                    ? await repository.GetCommitDiffAsync(revision, cancellationToken: Token) : "";
            }
        }
        finally { _loadingCommitFiles = false; }
    }

    private Control RevisionDiffFileHeader(FileStatusListEntry entry)
    {
        string caption = ExportDialogControls.DisplayPath(entry.DisplayPath ?? entry.Path)
            + (entry.OriginalPath is null ? "" : " ← " + ExportDialogControls.DisplayPath(entry.OriginalPath));
        StackPanel source = (StackPanel)IconLabel(caption, FileStatusListView.IconName(entry));
        Image image = (Image)source.Children[0];
        source.Children.Remove(image);
        TextBlock label = new() { Text = caption, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("16,*"), Height = 24, ClipToBounds = true };
        header.Children.Add(image);
        Grid.SetColumn(label, 1); header.Children.Add(label);
        ToolTip.SetTip(header, caption);
        return header;
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
            text.Text = "(" + fraction + count + ") " + text.Text;
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

    private async Task ShowRevisionDiffItemAsync(GitRepository repository, TreeViewItem item)
    {
        if (_commitContent is null) { return; }
        _commitContent.ClearImage();
        await _commitContent.TextView.ConfigureRepositoryAsync(repository, Token);
        if (item.Tag is GitRevisionDiffGroup group)
        {
            if (group.Kind == GitRevisionDiffGroupKind.RangeDiff && group.RangeDiff is not null)
            {
                _commitContent.TextView.Mode = DiffViewMode.RangeDiff;
                _commitContent.TextView.ShowDocument(await repository.GetRangeDiffDocumentAsync(group.RangeDiff, new GitRangeDiffOptions
                {
                    ContextLines = _revisionViewerOptions.ContextLines,
                    ShowEntireFile = _revisionViewerOptions.ShowEntireFile,
                    IgnoreWhitespace = _revisionViewerOptions.IgnoreWhitespace,
                    TreatAllFilesAsText = _revisionViewerOptions.TreatAllFilesAsText
                }, _commitContent.TextView.DisplayOptions, Token));
            }
            else
            {
                _commitContent.TextView.Mode = DiffViewMode.Auto;
                _diff.Text = T("Select a file to inspect its diff.");
            }
            return;
        }
        if (item.Tag is not RevisionDiffFileSelection selection) { _commitContent.TextView.Mode = DiffViewMode.Auto; _diff.Text = T("Select a file to inspect its diff."); return; }
        if (selection.Group.Kind == GitRevisionDiffGroupKind.Combined && selection.Group.CombinedDiff is not null)
        {
            _commitContent.TextView.Mode = DiffViewMode.Combined;
            GitDiffDocument document = await repository.GetCombinedFileDiffDocumentAsync(selection.Group.CombinedDiff, selection.File,
                new GitCombinedDiffOptions { OmitUninterestingDiff = _revisionDiffPreferences.OmitUninterestingDiff, DiffOptions = _revisionViewerOptions },
                _commitContent.TextView.DisplayOptions, Token);
            if (string.IsNullOrWhiteSpace(document.Patch)) { _diff.Text = T("Uninteresting diff hunks are omitted."); }
            else { _commitContent.TextView.ShowDocument(document); }
            return;
        }
        _commitContent.TextView.Mode = DiffViewMode.Unified;
        GitRevisionChanges changes = new(selection.Group.Revision, selection.Group.BaseRevision, selection.Group.Files);
        GitRevisionFile file = selection.File;
        if (!_revisionViewerOptions.TreatAllFilesAsText && FileContentView.IsImagePath(file.Path))
        {
            string? imageRevision = file.IsDeleted ? changes.BaseRevision : changes.Revision;
            if (imageRevision is not null
                && (await repository.GetTreeAsync(imageRevision, file.Path, Token)).FirstOrDefault(entry => entry.Path == file.Path)?.IsSubmodule is not true)
            {
                _commitContent.ShowImageOrHex(file.Path, await repository.GetFileBytesAsync(file.Path, imageRevision, Token));
                return;
            }
        }
        _commitContent.TextView.ShowDocument(await repository.GetRevisionFileDiffDocumentAsync(changes, file, _revisionViewerOptions,
            _commitContent.TextView.DisplayOptions, Token));
    }

    private async Task LoadChangedFileContentAsync(GitRepository repository, GitChange change, bool staged)
    {
        _changesContent?.ClearImage();
        if (_changesContent is not null) { _changesContent.TextView.Mode = change.IsUntracked ? DiffViewMode.PlainText : DiffViewMode.Auto; }
        if (_changesContent is not null && await _changesContent.ShowChangedImageAsync(repository, change, staged, Token)) { return; }
        if (change.IsUntracked)
        {
            await using FileStream stream = File.OpenRead(Path.Combine(repository.Path, change.Path));
            byte[] bytes = new byte[(int)Math.Min(stream.Length, 2_000_000)];
            await stream.ReadExactlyAsync(bytes, Token);
            _changesDiff.Text = DisplayBytes(bytes, stream.Length > bytes.Length);
        }
        else if (_changesContent is not null)
        {
            await _changesContent.TextView.ConfigureRepositoryAsync(repository, Token);
            _changesContent.TextView.ShowDocument(await repository.GetDiffDocumentAsync(change.Path, staged,
                displayOptions: _changesContent.TextView.DisplayOptions, originalPath: change.OriginalPath, cancellationToken: Token));
        }
    }
}
