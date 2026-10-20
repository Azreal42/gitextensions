using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CompareRevisionsDialog
{
    private FileStatusListContextMenuController? _fileMenuController;
    private FileMenuOperations? _fileMenuOperations;
    private FileMenuAdditionalOperations? _fileMenuAdditionalOperations;
    private IReadOnlyDictionary<GitComparisonGroup, IReadOnlyList<GitComparisonFileMetadata>> _fileMenuMetadata
        = new Dictionary<GitComparisonGroup, IReadOnlyList<GitComparisonFileMetadata>>(ReferenceEqualityComparer.Instance);
    private sealed record ComparisonMenuIdentity(GitComparisonGroup[] Groups);
    internal Func<ProcessStartInfo, Process?> FileMenuExternalLauncher { get; set; } = Process.Start;

    private async Task<IReadOnlyDictionary<GitComparisonGroup, IReadOnlyList<GitComparisonFileMetadata>>> ReadFileMenuMetadataAsync(
        IReadOnlyList<GitComparisonGroup> groups, CancellationToken token)
    {
        if (_fileMenuAdditionalOperations is not null) { await _fileMenuAdditionalOperations.RefreshAsync(Repository, token); }
        Dictionary<GitComparisonGroup, IReadOnlyList<GitComparisonFileMetadata>> metadata = new(ReferenceEqualityComparer.Instance);
        foreach (GitComparisonGroup group in groups)
        {
            if (group.Kind is GitRevisionDiffGroupKind.RangeDiff or GitRevisionDiffGroupKind.Combined || group.Comparison.Files.Count == 0) { continue; }
            // FormDiff's artificial revisions have no hydrated parent. HEAD is not a substitute.
            metadata.Add(group, await Repository.GetComparisonFileMetadataAsync(group.Comparison, null, token));
        }
        token.ThrowIfCancellationRequested(); return metadata;
    }

    private FileMenuSelection? CaptureFileMenuSelection()
    {
        if (_comparisonGroups.Count == 0) { return null; }
        TreeViewItem[] nodes = _files.SelectedItems?.OfType<TreeViewItem>().ToArray() ?? [];
        ComparisonFileSelection[] files = SelectedFileSelections.ToArray();
        FileMenuLeaf[] leaves = files.Select(item =>
        {
            GitComparison comparison = item.Group.Comparison;
            GitComparisonFileMetadata? metadata = _fileMenuMetadata.GetValueOrDefault(item.Group)?.FirstOrDefault(value => value.File == item.File);
            return new FileMenuLeaf(item.File, comparison.Base, comparison.Compare, comparison,
                metadata?.Staged switch
                { GitFileStagedStatus.Index => FileMenuStage.Index, GitFileStagedStatus.WorkTree => FileMenuStage.WorkingTree, GitFileStagedStatus.Unknown => FileMenuStage.Unknown, _ => FileMenuStage.None },
                metadata?.IsTracked == true, metadata?.IsSubmodule == true, metadata?.IsSkipWorktree == true, metadata?.IsAssumeUnchanged == true)
                { SubmoduleSelection = metadata?.SubmoduleSelection, SubmoduleSelectionError = metadata?.SubmoduleSelectionError };
        }).ToArray();
        GitComparisonGroup[] groups = files.Select(file => file.Group).Concat(nodes.Select(node => node.Tag switch
        {
            GitComparisonGroup group => group,
            FileStatusListNodeTag tag => _groupsByKey.GetValueOrDefault(tag.GroupKey),
            _ => null
        }).OfType<GitComparisonGroup>()).Distinct<GitComparisonGroup>(ReferenceEqualityComparer.Instance).ToArray();
        string[] paths = nodes.Select(node => node.Tag switch
        {
            ComparisonFileSelection file => file.File.Path,
            FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } folder => folder.Key,
            _ => null
        }).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        string? folderPath = _filesView.SelectedFolderPath;
        if (nodes.Length == 1 && nodes[0].Items.Count > 0 && folderPath is null) { folderPath = ""; paths = ["."]; }
        if (paths.Length == 0) { paths = leaves.Select(leaf => leaf.File.Path).Distinct(StringComparer.Ordinal).ToArray(); }
        FileMenuLeaf? focused = _filesView.FocusedSelectedTag is ComparisonFileSelection focusedFile
            ? leaves.FirstOrDefault(leaf => ReferenceEquals(leaf.Comparison, focusedFile.Group.Comparison) && leaf.File == focusedFile.File) : leaves.FirstOrDefault();
        return new(Repository, leaves, paths, folderPath, IsDisplayOnly: groups.Any(group => group.Kind is GitRevisionDiffGroupKind.RangeDiff or GitRevisionDiffGroupKind.Combined),
            HostIdentity: new ComparisonMenuIdentity(groups), FocusedLeaf: focused);
    }

    private bool IsCurrentFileMenuSelection(FileMenuSelection selection) => !_selectionClosing && IsVisible
        && ReferenceEquals(selection.Repository, Repository) && selection.HostIdentity is ComparisonMenuIdentity identity
        && identity.Groups.All(group => _comparisonGroups.Any(current => ReferenceEquals(current, group)));

    private void InitializeFileMenus()
    {
        _fileMenuOperations = new(this, RunFileMenuOperationAsync, IsCurrentFileMenuSelection, info => FileMenuExternalLauncher(info), _selectionLifetime.Token, T)
        {
            ReadingPosition = leaf => !IsSelectionLoading && !ActiveView.IsImage && ActiveView.TextView.IsDocumentReady && _loadedSelection is { } loaded && loaded.File == leaf.File
                && ReferenceEquals(loaded.Comparison, leaf.Comparison) && loaded.Tab == _tabs.SelectedIndex
                ? (ActiveView.TextView.CurrentFileLine, ActiveView.TextView.FileEncodingName ?? ActiveView.TextView.DefaultFileEncodingName) : (1, null)
        };
        FileMenuAdditionalOperations additional = _fileMenuAdditionalOperations = new(this, RunFileMenuOperationAsync,
            IsCurrentFileMenuSelection, info => FileMenuExternalLauncher(info), _selectionLifetime.Token);
        DesktopScriptExecutor.SetFileProvider(this, CaptureFileMenuSelection);
        DesktopScriptExecutor.SetReadingPositionProvider(this, () => (ActiveView.TextView.CurrentFileLine, ActiveView.TextView.Editor.TextArea.Caret.Column));
        _fileMenuController = new(_filesView, Translations, new FileMenuHost
        {
            CaptureSelection = CaptureFileMenuSelection, IsCurrent = IsCurrentFileMenuSelection,
            RunScriptAsync = (script, selection) => RunFileMenuOperationAsync(script.GetDisplayName(),
                async token => { await DesktopScriptExecutor.ExecuteAsync(this, Repository, script, token, selection); },
                changes: !script.RunInBackground),
            IsBusy = () => IsBusy || _selectionClosing,
            ExecuteAsync = (command, selection) => FileMenuOperations.Handles(command)
                ? _fileMenuOperations.ExecuteAsync(command, selection) : additional.ExecuteAsync(command, selection),
            GetRemembered = () => additional.GetRemembered(Repository), GetCustomDiffTools = () => additional.GetCustomDiffTools(Repository),
            CreateAction = CreateFileMenuAction
        });
        ContextMenu menu = new() { Name = "ComparisonFilesContextMenu" }; _files.ContextMenu = menu;
        _files.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(_files).Properties.IsRightButtonPressed) { return; }
            TreeViewItem? item = (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (item is null) { return; }
            if (_files.SelectedItems?.Contains(item) != true) { _files.SelectedItem = item; }
            item.Focus();
        }, RoutingStrategies.Tunnel);
        menu.Opening += (_, _) => menu.ItemsSource = _fileMenuController.BuildItems(CaptureFileMenuSelection());
        _files.AddHandler(InputElement.DoubleTappedEvent, async (_, args) =>
        {
            if (CaptureFileMenuSelection() is not { Leaves.Count: 1, Folder: null } selection) { return; }
            if ((args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.Tag is not ComparisonFileSelection) { return; }
            args.Handled = true;
            try { await _fileMenuOperations.ExecuteAsync(FileMenuCommand.FileHistory, selection); }
            catch (Exception error) { ReportError(error); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private MenuItem CreateFileMenuAction(string category, string name, string label, string? icon, Func<Task> action, KeyGesture? gesture)
    {
        MenuItem item = OriginalMenuItems.Create(Translations, category, name, label, icon, gesture);
        item.Click += async (_, args) =>
        {
            if (!ReferenceEquals(args.Source, item)) { return; }
            args.Handled = true;
            Func<bool>? restoreFocus = _filesView.CaptureSelectedRowFocus();
            try { await action(); }
            catch (Exception error) { ReportError(error); }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || _selectionClosing || IsBusy) { return; }
                    IInputElement? focused = FocusManager?.GetFocusedElement();
                    if (focused is Visual focusedVisual && focused is not MenuItem && TopLevel.GetTopLevel(focusedVisual) == this && !_files.IsKeyboardFocusWithin) { return; }
                    if (restoreFocus?.Invoke() == true) { return; }
                    if (_files.SelectedItem is TreeViewItem selected) { selected.Focus(); } else { _files.Focus(); }
                }, DispatcherPriority.Input);
            }
        };
        return item;
    }

    private Task RunFileMenuOperationAsync(string label, Func<CancellationToken, Task> action, bool changes)
        => RunComparisonAsync(label, async token =>
        {
            Exception? actionError = null;
            try { await action(token); }
            catch (Exception error) { actionError = error; throw; }
            finally
            {
                if (changes)
                {
                    MarkRepositoryChanged();
                    if (!token.IsCancellationRequested && !_selectionClosing)
                    {
                        _loading = true;
                        try { await ReadComparisonAsync(token); }
                        catch (Exception refreshError) when (actionError is not null)
                        { actionError.Data["File menu refresh error"] = refreshError.Message; }
                        finally { _loading = false; }
                    }
                }
            }
        });
}
