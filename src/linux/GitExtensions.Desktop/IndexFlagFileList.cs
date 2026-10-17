using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Windows' session-only visibility choices and index-flag context actions.</summary>
internal sealed class IndexFlagFileList
{
    private readonly FileStatusListView _view;
    private readonly Func<bool> _available;
    private readonly MenuItem _showSkip, _showAssume;
    private IReadOnlyDictionary<string, GitIndexFileFlags> _flags = new Dictionary<string, GitIndexFileFlags>();
    private IReadOnlySet<string> _submodules = new HashSet<string>();
    private InputOrigin? _input;
    private TreeViewItem? _contextPointerRow;
    private Action _updateContext = () => { };
    private sealed record InputOrigin(Window Owner, string Filter, Func<bool>? Row, TextBox? Editor, int Caret, int Start, int End);

    internal IndexFlagFileList(FileStatusListView view, Func<bool> available, Func<Task> refresh,
        Func<string[], bool, bool, Task> setFlag)
    {
        _view = view; _available = available;
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        MenuItem Item(string name, string label)
        {
            MenuItem item = OriginalMenuItems.Create(translations, "FileStatusList", name, label);
            item.ToggleType = MenuItemToggleType.CheckBox; return item;
        }
        _showSkip = Item("tsmiShowSkipWorktreeFiles", "Show s&kip-worktree files");
        _showAssume = Item("tsmiShowAssumeUnchangedFiles", "Show &assumed-unchanged files");
        view.SettingsMenu.Items.Insert(0, _showSkip); view.SettingsMenu.Items.Insert(1, _showAssume);
        view.SettingsMenu.Items.Insert(2, new Separator());
        Button trigger = view.Toolbar.Children.OfType<Button>().Single(button => button.Name == view.Tree.Name + "Settings");
        trigger.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        { if (args.GetCurrentPoint(trigger).Properties.IsLeftButtonPressed) { CaptureInput(); } }, RoutingStrategies.Tunnel);
        trigger.AddHandler(InputElement.KeyDownEvent, (_, _) => _input = null, RoutingStrategies.Tunnel);
        view.SettingsMenu.Opening += (_, _) => UpdateAvailability();
        view.SettingsMenu.Closed += (_, _) => ForgetInput();
        _showSkip.Click += async (_, _) => await RefreshVisibleAsync();
        _showAssume.Click += async (_, _) => await RefreshVisibleAsync();

        ContextMenu menu = view.Tree.ContextMenu ??= new();
        MenuItem skip = Item("tsmiSkipWorktree", "S&kip worktree");
        MenuItem assume = Item("tsmiAssumeUnchanged", "Assu&me unchanged");
        Separator separator = new();
        menu.ItemsSource = menu.Items.Cast<object>().Concat(new object[] { separator, skip, assume }).ToArray();
        string[] paths = [];
        void UpdateContext()
        {
            FileStatusListEntry[] selected = view.SelectedEntries.ToArray();
            paths = selected.Where(entry => entry.Tag is GitChange { IsUntracked: false, IsSubmodule: false } && _flags.ContainsKey(entry.Path))
                .Select(entry => entry.Path).Distinct(StringComparer.Ordinal).ToArray();
            bool shown = paths.Length > 0 && !(selected.Length == 1 && selected[0].Tag is GitChange { IsSubmodule: true });
            separator.IsVisible = skip.IsVisible = assume.IsVisible = shown;
            skip.IsEnabled = assume.IsEnabled = shown && available();
            skip.IsChecked = paths.Any(path => _flags[path].IsSkipWorktree);
            assume.IsChecked = paths.Any(path => _flags[path].IsAssumeUnchanged);
        }
        _updateContext = UpdateContext;
        view.Tree.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            _contextPointerRow = args.GetCurrentPoint(view.Tree).Properties.IsRightButtonPressed
                ? (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() : null;
            _input = null;
        }, RoutingStrategies.Tunnel);
        view.Tree.AddHandler(InputElement.KeyDownEvent, (_, _) => { _contextPointerRow = null; _input = null; }, RoutingStrategies.Tunnel);
        menu.Opening += (_, _) =>
        {
            // Selection follows PointerPressed tunneling. Validate the actual clicked
            // row here, once selection has run, without moving focus to open the menu.
            UpdateContext(); CaptureInput(_contextPointerRow); _contextPointerRow = null;
        };
        menu.Closed += (_, _) => ForgetInput();
        skip.Click += async (_, _) => await ToggleAsync(true);
        assume.Click += async (_, _) => await ToggleAsync(false);

        async Task RefreshVisibleAsync()
        {
            if (!available()) { return; }
            InputOrigin? input = _input; _input = null;
            try { await refresh(); }
            finally { RestoreInput(input); }
        }
        async Task ToggleAsync(bool isSkip)
        {
            if (!available() || paths.Length == 0) { return; }
            string[] selected = paths; InputOrigin? input = _input; _input = null;
            bool enabled = !selected.Any(path => isSkip ? _flags[path].IsSkipWorktree : _flags[path].IsAssumeUnchanged);
            try { await setFlag(selected, isSkip, enabled); }
            finally { RestoreInput(input); }
        }
    }

    internal void UpdateAvailability()
    { _showSkip.IsEnabled = _showAssume.IsEnabled = _available(); _updateContext(); }

    private void ForgetInput()
    {
        InputOrigin? origin = _input;
        // Dismissal and Click can occur in either order in a routed popup event.
        // Only the action on that stack may consume the bookmark; a later opening cannot.
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_input, origin)) { _input = null; } });
    }

    internal async Task RefreshAsync(GitRepository repository, CancellationToken token)
    {
        var flags = (await repository.GetIndexFlagsAsync(token)).ToDictionary(file => file.Path, StringComparer.Ordinal);
        GitRevisionFile[] visible = flags.Values.Where(Visible).Select(file => new GitRevisionFile(file.Path, ' ')).ToArray();
        IReadOnlyList<GitComparisonFileMetadata> metadata = visible.Length == 0 ? []
            : await repository.GetComparisonFileMetadataAsync(new(new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree), visible, []), null, token);
        token.ThrowIfCancellationRequested();
        _flags = flags; _submodules = metadata.Where(file => file.IsSubmodule).Select(file => file.File.Path).ToHashSet(StringComparer.Ordinal);
    }

    // Keep the real status separate: revealing a flag is neither a content change
    // nor a staged entry. A path carrying both bits appears once, with both bits.
    internal IEnumerable<GitChange> WithVisibleFlags(IEnumerable<GitChange> worktree)
    {
        GitChange[] changes = worktree.ToArray();
        HashSet<string> existing = changes.Select(change => change.Path).ToHashSet(StringComparer.Ordinal);
        return changes.Concat(_flags.Values.Where(file => Visible(file) && !existing.Contains(file.Path))
            .Select(file => new GitChange(file.Path, null, '.', '.', false, _submodules.Contains(file.Path))));
    }
    private bool Visible(GitIndexFileFlags file) => _showSkip.IsChecked && file.IsSkipWorktree || _showAssume.IsChecked && file.IsAssumeUnchanged;

    private void CaptureInput(TreeViewItem? pointerRow = null)
    {
        _input = null;
        if (TopLevel.GetTopLevel(_view) is not Window owner) { return; }
        if (pointerRow is not null && _view.CaptureSelectedRowFocus(pointerRow) is { } pointerOrigin)
        { _input = new(owner, _view.FilterText, pointerOrigin, null, 0, 0, 0); return; }
        if (owner.FocusManager?.GetFocusedElement() is not Control focused) { return; }
        if (focused is TextBox editor && editor.GetVisualAncestors().Contains(_view))
        { _input = new(owner, _view.FilterText, null, editor, editor.CaretIndex, editor.SelectionStart, editor.SelectionEnd); }
        else if (focused.GetSelfAndVisualAncestors().Contains(_view.Tree) && _view.CaptureSelectedRowFocus() is { } row)
        { _input = new(owner, _view.FilterText, row, null, 0, 0, 0); }
    }
    private void RestoreInput(InputOrigin? origin)
    {
        if (origin is null) { return; }
        Dispatcher.UIThread.Post(() =>
        {
            Window owner = origin.Owner;
            if (!_available() || !owner.IsVisible || !owner.IsActive || !owner.IsEffectivelyEnabled || owner.OwnedWindows.Any(child => child.IsVisible)
                || !_view.IsEffectivelyVisible || !_view.IsEffectivelyEnabled || TopLevel.GetTopLevel(_view) != owner || _view.FilterText != origin.Filter
                || _view.SettingsMenu.IsOpen || _view.Tree.ContextMenu?.IsOpen == true) { return; }
            Control? current = owner.FocusManager?.GetFocusedElement() as Control;
            if (current is not null && current != owner && current != origin.Editor && !current.GetSelfAndVisualAncestors().Contains(_view)
                && current is not MenuItem && current.GetVisualAncestors().All(parent => parent is not MenuItem)) { return; }
            if (origin.Editor is TextBox editor && editor.IsEffectivelyVisible && editor.IsEffectivelyEnabled && TopLevel.GetTopLevel(editor) == owner)
            { editor.Focus(); editor.CaretIndex = origin.Caret; editor.SelectionStart = origin.Start; editor.SelectionEnd = origin.End; }
            else if (origin.Row?.Invoke() != true && _view.Tree.SelectedItem is TreeViewItem { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } selected)
            { selected.Focus(); selected.BringIntoView(); }
        }, DispatcherPriority.Input);
    }
}
