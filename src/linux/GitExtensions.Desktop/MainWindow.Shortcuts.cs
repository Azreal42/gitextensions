using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // Defaults from HotkeySettingsManager/FormBrowse and RevisionGridControl.
    private async void HandleShortcuts(object? sender, KeyEventArgs e)
    {
        if (_busy || e.Handled) { return; }
        // Window tunnel handlers run before the focused viewer. Preserve the
        // original child-first command scope when its gesture is available.
        DiffView? focusedDiff = _commitContent?.TextView.IsKeyboardFocusWithin == true ? _commitContent.TextView
            : _changesDiffView?.IsKeyboardFocusWithin == true ? _changesDiffView
            : _fileContent?.TextView.IsKeyboardFocusWithin == true ? _fileContent.TextView : null;
        if (focusedDiff?.HandleReadingShortcut(e) == true) { return; }
        if (focusedDiff?.AppearanceMenu.HandleKeyDown(e) == true) { return; }
        Func<Task>? action = (e.Key, e.KeyModifiers) switch
        {
            (Key.F5, KeyModifiers.None) => RefreshAsync,
            (Key.O, KeyModifiers.Control) => ChooseRepositoryAsync,
            (Key.W, KeyModifiers.Control) => CloseRepositoryAsync,
            (Key.Space, KeyModifiers.Control) => () => ShowWorkflowAsync("Changes"),
            (Key.R, KeyModifiers.Control) => CompareAsync,
            (Key.B, KeyModifiers.Control) => CreateBranchAsync,
            (Key.OemPeriod, KeyModifiers.Control) => OpenBranchCheckoutAsync,
            (Key.T, KeyModifiers.Control) => CreateTagAsync,
            (Key.M, KeyModifiers.Control) => MergeAsync,
            (Key.E, KeyModifiers.Control | KeyModifiers.Shift) => RebaseAsync,
            (Key.Down, KeyModifiers.Control) => PullAsync,
            (Key.Up, KeyModifiers.Control) => PushAsync,
            (Key.Down, KeyModifiers.Control | KeyModifiers.Shift) => QuickFetchAsync,
            (Key.P, KeyModifiers.Control | KeyModifiers.Shift) => () => ExecutePullActionAsync(GitExtensions.Extensibility.Git.GitPullAction.Merge),
            (Key.F8, KeyModifiers.None) => DefaultPullAsync,
            (Key.Up, KeyModifiers.Control | KeyModifiers.Shift) => PushAsync,
            (Key.Up, KeyModifiers.Control | KeyModifiers.Alt) => QuickStashAsync,
            (Key.Down, KeyModifiers.Control | KeyModifiers.Alt) => QuickStashPopAsync,
            (Key.Up, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt) when _supportsStashStaged == true => QuickStashStagedAsync,
            (Key.G, KeyModifiers.Control) => OpenTerminalAsync,
            (Key.OemComma, KeyModifiers.Control) => () => ShowWorkflowAsync("Configuration"),
            (Key.C, KeyModifiers.Control | KeyModifiers.Alt) => ToggleNavigationAsync,
            (Key.W, KeyModifiers.Control | KeyModifiers.Alt) => () => ShowWorkflowAsync("Worktrees"),
            (Key.N, KeyModifiers.Control | KeyModifiers.Shift) => NotesAsync,
            (Key.Enter, KeyModifiers.Control) when (_tabs.SelectedItem as TabItem)?.Name == "ChangesTab" => CommitAsync,
            (Key.A, KeyModifiers.Control | KeyModifiers.Shift) => () => SetHistoryScopeAsync(true),
            (Key.U, KeyModifiers.Control | KeyModifiers.Shift) => () => SetHistoryScopeAsync(false),
            _ => null
        };
        if (action is not null)
        {
            e.Handled = true;
            try { await action(); }
            catch (Exception exception) { AppendOutput(exception.Message); _state.Text = exception.Message; }
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.D0: _navigation.Focus(); break;
                case Key.D1: _commits.Focus(); break;
                case Key.D2: FocusCommitInformation(); break;
                case Key.D3: FocusDetail("DiffTab"); break;
                case Key.D4: FocusDetail("FileTreeTab"); break;
                case Key.D5: FocusDetail("GpgTab"); break;
                case Key.D6: FocusDetail("ConsoleTab"); break;
                case Key.E: FocusBranchFilter(); break;
                case Key.Tab: CycleDetails(1); break;
                case Key.P: SelectRelativeCommit(parent: true); break;
                case Key.N: SelectRelativeCommit(parent: false); break;
                case Key.Left: SelectRelativeCommit(parent: true); break;
                case Key.Right: SelectRelativeCommit(parent: true, lastParent: true); break;
                default: e.Handled = false; break;
            }
        }
        else if (e.Key == Key.Tab && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            CycleDetails(-1);
        }
    }

    private void FocusDetail(string name)
    {
        SelectDetailsTab(name);
        _tabs.UpdateLayout();
        Control? content = (_tabs.SelectedItem as TabItem)?.Content as Control;
        if (name == "DiffTab" && _commitContent?.IsImage is false) { _commitContent.TextView.Editor.TextArea.Focus(); }
        else if (content is DiffView diff) { diff.Editor.TextArea.Focus(); }
        else if (name == "FileTreeTab") { _tree.Focus(); }
        else if (name == "GpgTab") { _gpgInfo.Focus(); }
        else if (name == "ConsoleTab") { _output.Focus(); }
        else { content?.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible)?.Focus(); }
    }

    private void CycleDetails(int direction)
    {
        TabItem[] tabs = _tabs.Items.OfType<TabItem>().ToArray();
        int index = Math.Max(0, Array.IndexOf(tabs, _tabs.SelectedItem));
        FocusDetail(tabs[(index + direction + tabs.Length) % tabs.Length].Name!);
    }

    private void SelectRelativeCommit(bool parent, bool lastParent = false)
    {
        if (_commits.SelectedItem is not GitCommit current) { return; }
        GitCommit? relative = parent
            ? _history.FirstOrDefault(commit => commit.Hash == (lastParent ? current.Parents.LastOrDefault() : current.Parents.FirstOrDefault()))
            : _history.FirstOrDefault(commit => commit.Parents.Contains(current.Hash));
        if (relative is not null) { _commits.SelectedItem = relative; _commits.ScrollIntoView(relative); }
    }

    private static KeyGesture? BrowserGesture(string label) => label switch
    {
        "Open repository…" => new(Key.O, KeyModifiers.Control),
        "Close" => new(Key.W, KeyModifiers.Control),
        "Commit…" => new(Key.Space, KeyModifiers.Control),
        "Create branch…" => new(Key.B, KeyModifiers.Control),
        "Checkout branch…" => new(Key.OemPeriod, KeyModifiers.Control),
        "Create tag…" => new(Key.T, KeyModifiers.Control),
        "Merge…" => new(Key.M, KeyModifiers.Control),
        "Rebase…" => new(Key.E, KeyModifiers.Control | KeyModifiers.Shift),
        "Pull…" => new(Key.Down, KeyModifiers.Control),
        "Push…" => new(Key.Up, KeyModifiers.Control),
        "Settings…" => new(Key.OemComma, KeyModifiers.Control),
        "Terminal" => new(Key.G, KeyModifiers.Control),
        "Show left panel" => new(Key.C, KeyModifiers.Control | KeyModifiers.Alt),
        "Worktrees…" => new(Key.W, KeyModifiers.Control | KeyModifiers.Alt),
        _ => null
    };
}
