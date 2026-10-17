using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

internal sealed record ComparisonFileSelection(GitComparisonGroup Group, GitRevisionFile File);

public sealed partial class CompareRevisionsDialog
{
    private IReadOnlyList<GitComparisonGroup> _comparisonGroups = [];
    private readonly Dictionary<string, GitComparisonGroup> _groupsByKey = new(StringComparer.Ordinal);
    private readonly RevisionDiffPreferencesStore _groupPreferences = new();
    private readonly MenuItem _allParents = new() { Name = "ComparisonDiffAllParents", ToggleType = MenuItemToggleType.CheckBox };
    private string? _capturedHead;
    private bool _headCaptured;
    public IReadOnlyList<GitComparisonGroup> ComparisonGroups => _comparisonGroups;
    public GitComparisonGroup? SelectedGroup => _files.SelectedItems?.Count != 1 ? null : (_files.SelectedItem as TreeViewItem)?.Tag switch
    {
        ComparisonFileSelection selection => selection.Group,
        GitComparisonGroup group => group,
        FileStatusListNodeTag node when _groupsByKey.TryGetValue(node.GroupKey, out GitComparisonGroup? group) => group,
        _ => null
    };
    public GitComparison? SelectedComparison => SelectedGroup?.Comparison;
    public GitRevisionFile? SelectedFile => _files.SelectedItems?.Count == 1 && _files.SelectedItem is TreeViewItem { Tag: ComparisonFileSelection selection } ? selection.File : null;
    internal IReadOnlyList<ComparisonFileSelection> SelectedFileSelections => _files.SelectedItems?.Count == 1
        ? _filesView.SelectedTags.OfType<ComparisonFileSelection>().ToArray()
        : _files.SelectedItems?.OfType<TreeViewItem>().Select(item => item.Tag).OfType<ComparisonFileSelection>().ToArray() ?? [];
    public IReadOnlyList<GitRevisionFile> SelectedFiles => SelectedFileSelections.Select(selection => selection.File).ToArray();

    private void InitializeComparisonGroups()
    {
        _allParents.Header = T("Show file differences for all parents");
        _allParents.IsChecked = _groupPreferences.Load().ShowDiffForAllParents;
        _filesView.SettingsMenu.Items.Add(_allParents);
        InitializeSettingsInputFocus();
        _filesView.SettingsMenu.Opening += (_, _) =>
        {
            _allParents.IsChecked = _groupPreferences.Load().ShowDiffForAllParents;
            _settingsInput ??= CaptureSettingsInput();
        };
        _allParents.Click += async (_, _) =>
        {
            if (IsBusy || _selectionClosing) { return; }
            SettingsInput? origin = _settingsInput; _settingsInput = null;
            try
            {
                _groupPreferences.Save(_groupPreferences.Load() with { ShowDiffForAllParents = _allParents.IsChecked });
                await RunComparisonAsync("Compare revisions", async token =>
                { _loading = true; try { await ReadComparisonAsync(token); } finally { _loading = false; } });
            }
            finally { QueueSettingsInput(origin); }
        };
    }

    private GitCompareRevision? MergeRevision(GitCompareRevision revision)
        => revision.Kind == GitCompareRevisionKind.Commit ? revision : _capturedHead is null ? null : new(_capturedHead);

    private void SetFileGroups()
    {
        _groupsByKey.Clear();
        FileStatusListGroup[] groups = _comparisonGroups.Select((group, index) =>
        {
            GitComparison comparison = group.Comparison;
            string key = $"{index}:{group.Kind}:{comparison.Base.Kind}:{comparison.Base.Revision}:{comparison.Compare.Kind}:{comparison.Compare.Revision}";
            _groupsByKey[key] = group;
            FileStatusListEntry[] entries = comparison.Files.Select(file => new FileStatusListEntry(new ComparisonFileSelection(group, file), file.Path,
                file.OriginalPath, file.Status, file.SimilarityPercent, file.DiffStatus)).ToArray();
            return new FileStatusListGroup(group, key, ComparisonGroupHeader(group, entries.Length, entries.Length), entries,
                AlwaysVisible: group.Kind == GitRevisionDiffGroupKind.RangeDiff,
                IsBaseComparison: group.Kind is GitRevisionDiffGroupKind.BaseToFirst or GitRevisionDiffGroupKind.BaseToSecond,
                Name: index == 0 ? "ComparisonFileGroup" : "ComparisonFileGroup_" + group.Kind)
            {
                HeaderFactory = (shown, total) => ComparisonGroupHeader(group, shown, total),
                ExpandSmallDiff = group.Kind == GitRevisionDiffGroupKind.Comparison
            };
        }).ToArray();
        _filesView.SetGroups(groups);
    }

    private Control ComparisonGroupHeader(GitComparisonGroup group, int shown, int total)
    {
        GitComparison comparison = group.Comparison;
        string icon = "Diff.png", caption;
        if (group.Kind == GitRevisionDiffGroupKind.RangeDiff && group.RangeDiff is { } range)
        {
            icon = "DiffR.png";
            caption = T("Range diff") + " " + range.First.CommitCount + "↓ " + range.Second.CommitCount + "↑ BASE " + Short(range.CommonAncestor);
        }
        else
        {
            string count = shown < total ? shown + "/" + total : total.ToString();
            (string title, GitCompareRevision revision) = group.Kind switch
            {
                GitRevisionDiffGroupKind.BaseToSecond => (T("Diff BASE with") + " B ", comparison.Compare),
                GitRevisionDiffGroupKind.BaseToFirst => (T("Diff BASE with") + " A ", comparison.Compare),
                _ => (T("Diff with A "), comparison.Base)
            };
            icon = group.Kind == GitRevisionDiffGroupKind.BaseToSecond ? "DiffB.png" : group.Kind == GitRevisionDiffGroupKind.BaseToFirst ? "DiffA.png" : icon;
            caption = "(" + count + ") " + title + (revision.Kind switch
            { GitCompareRevisionKind.Index => T("Commit index"), GitCompareRevisionKind.WorkingTree => T("Working directory"), _ => Short(revision.Revision) });
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Height = ClassicTreeViewAppearance.RowHeight, Children =
        { _filesView.CreateIcon(icon), new TextBlock { Text = caption, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center } } };
    }
    private static string Short(string revision) => revision[..Math.Min(9, revision.Length)];
    private sealed record SettingsInput(string Filter, TextBox? Editor, int Caret, int Start, int End);
    private SettingsInput? _settingsInput;
    private Button? _settingsTrigger;
    private int _settingsInputVersion;

    private void InitializeSettingsInputFocus()
    {
        Button trigger = _filesView.Toolbar.Children.OfType<Button>().Single(button => button.Name == "ComparisonFilesSettings");
        _settingsTrigger = trigger;
        trigger.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(trigger).Properties.IsLeftButtonPressed) { return; }
            _settingsInputVersion++; _settingsInput = CaptureSettingsInput();
        }, RoutingStrategies.Tunnel);
        trigger.AddHandler(KeyDownEvent, (_, _) => { _settingsInputVersion++; _settingsInput = null; }, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!_filesView.SettingsMenu.IsOpen && args.Source is Visual source && !source.GetSelfAndVisualAncestors().Contains(trigger))
            { _settingsInputVersion++; _settingsInput = null; }
        }, RoutingStrategies.Tunnel);
        _filesView.SettingsMenu.Closed += (_, _) =>
        { SettingsInput? origin = _settingsInput; _settingsInput = null; QueueSettingsInput(origin); };
    }

    private SettingsInput? CaptureSettingsInput()
    {
        if (_selectionClosing || IsBusy || FocusManager?.GetFocusedElement() is not Control input) { return null; }
        if (input is TextBox editor && editor.GetVisualAncestors().Any(control => control is ComboBox { Name: "ComparisonFilesFilter" }))
        { return new(_filesView.FilterText, editor, editor.CaretIndex, editor.SelectionStart, editor.SelectionEnd); }
        return input.GetSelfAndVisualAncestors().Contains(_files) ? new(_filesView.FilterText, null, 0, 0, 0) : null;
    }

    private void QueueSettingsInput(SettingsInput? origin)
    {
        if (origin is null) { return; }
        int version = _settingsInputVersion; GitComparison? comparison = _comparison;
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectionClosing || IsBusy || version != _settingsInputVersion || !ReferenceEquals(_comparison, comparison)
                || !IsVisible || !IsEffectivelyEnabled || !_files.IsEffectivelyEnabled || !_filesView.IsEffectivelyVisible
                || !IsActive || OwnedWindows.Any(child => child.IsVisible) || _filesView.SettingsMenu.IsOpen
                || _filesView.FilterText != origin.Filter) { return; }
            Control? current = FocusManager?.GetFocusedElement() as Control;
            if (current is not null && current is not Window && current != origin.Editor && current != _settingsTrigger
                && !current.GetVisualAncestors().Contains(_settingsTrigger!) && !IsSettingsItem(current)) { return; }
            if (origin.Editor is TextBox editor)
            {
                if (!editor.IsEffectivelyEnabled || !editor.IsEffectivelyVisible || TopLevel.GetTopLevel(editor) != this || (editor.Text ?? "") != origin.Filter) { return; }
                editor.Focus(); editor.CaretIndex = origin.Caret; editor.SelectionStart = origin.Start; editor.SelectionEnd = origin.End;
            }
            else if (_files.SelectedItem is TreeViewItem selected && selected.IsEffectivelyEnabled && selected.IsEffectivelyVisible
                && selected.Focusable && TopLevel.GetTopLevel(selected) == this)
            { selected.BringIntoView(); selected.Focus(); }
        });
    }

    private bool IsSettingsItem(Control control)
    {
        bool Contains(IEnumerable<object> items)
            => items.OfType<MenuItem>().Any(item => ReferenceEquals(item, control) || Contains(item.Items.Cast<object>()));
        return Contains(_filesView.SettingsMenu.Items.Cast<object>());
    }

}
