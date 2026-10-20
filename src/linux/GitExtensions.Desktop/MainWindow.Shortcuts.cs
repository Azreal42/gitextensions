using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private KeyEventArgs? _contextGestureKeyDown;
    private Control? _contextGestureTarget;

    private void InitializeKeyboardContextGesture()
    {
        AddHandler(KeyDownEvent, (_, args) =>
        {
            _contextGestureKeyDown = null; _contextGestureTarget = null;
            if (args.Key == Key.F10 && args.KeyModifiers == KeyModifiers.Shift
                && FocusManager?.GetFocusedElement() is Control target && TopLevel.GetTopLevel(target) == this)
            { _contextGestureKeyDown = args; _contextGestureTarget = target; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Control's native ContextRequested handling runs on KeyUp. Fall back only
        // after it, so Win32/native mappings open once and X11/headless gain the
        // Windows gesture without changing application-wide platform settings.
        AddHandler(KeyUpEvent, (_, args) =>
        {
            if (args.Key != Key.F10) { return; }
            KeyEventArgs? down = _contextGestureKeyDown; Control? target = _contextGestureTarget;
            _contextGestureKeyDown = null; _contextGestureTarget = null;
            if (args.Handled || down?.Handled != false || args.KeyModifiers != KeyModifiers.Shift
                || _busy || _lifetime.IsCancellationRequested || target is null || !target.IsEffectivelyVisible
                || !target.IsEffectivelyEnabled || TopLevel.GetTopLevel(target) != this
                || !ReferenceEquals(FocusManager?.GetFocusedElement(), target)) { return; }
            // Retain configured KeyDown command precedence. Ask the actual input
            // control, so file trees and editors keep their own context menu.
            ContextRequestedEventArgs request = new(); target.RaiseEvent(request);
            args.Handled = request.Handled;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // Defaults from HotkeySettingsManager/FormBrowse and RevisionGridControl.
    private async void HandleShortcuts(object? sender, KeyEventArgs e) => await HandleShortcutsAsync(sender, e);

    private Task HandleShortcutsAsync(object? sender, KeyEventArgs e) => HandleConfiguredShortcutsAsync(sender, e, allowAlt: false);

    private async Task HandleConfiguredShortcutsAsync(object? sender, KeyEventArgs e, bool allowAlt)
    {
        if (IsStandaloneDiff && !e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && sender is not MenuItem and not Avalonia.Controls.ContextMenu
            && _revisionContext?.IsOpen != true && _revisionFiles.ContextMenu?.IsOpen != true)
        {
            e.Handled = true;
            if (_quickSearch?.Notice.IsVisible == true) { HideQuickSearch(); } else { Close(); }
            return;
        }
        if (_busy || e.Handled || _lifetime.IsCancellationRequested) { return; }
        // Alt goes through the owner access-key preview before dispatch, otherwise
        // releasing Alt can unexpectedly select the main menu.
        if (e.KeyModifiers == KeyModifiers.Alt && !allowAlt && sender is not MenuItem and not Avalonia.Controls.ContextMenu) { return; }
        DiffView? focusedDiff = _commitContent?.TextView.IsKeyboardFocusWithin == true ? _commitContent.TextView
            : _changesDiffView?.IsKeyboardFocusWithin == true ? _changesDiffView
            : _fileContent?.TextView.IsKeyboardFocusWithin == true ? _fileContent.TextView : null;
        if (focusedDiff?.HandleReadingShortcut(e) == true || focusedDiff?.AppearanceMenu.HandleKeyDown(e) == true) { return; }
        void ClosePopup()
        {
            if (sender is MenuItem menu) { menu.IsSubMenuOpen = false; }
            if (sender is Avalonia.Controls.ContextMenu popup) { popup.Close(); }
            if (sender is MenuItem or Avalonia.Controls.ContextMenu) { _revisionContext?.Close(); }
        }
        if (await HandleScriptHotkeyAsync(e, ClosePopup)) { return; }
        if (await HandleNavigationHotkeyAsync(e)) { return; }
        if (await HandleFilePaneShortcutAsync(e)) { return; }
        if (await ExecuteConfiguredHotkeyAsync("Browse", e, ClosePopup)) { return; }
        if (!IsFocusedTextEditingKey(e) && await ExecuteConfiguredHotkeyAsync("RevisionGrid", e, ClosePopup)) { return; }

        // These are framework/application commands outside the configurable
        // Windows contexts. Configured commands above have no legacy fallback.
        Func<Task>? action = (e.Key, e.KeyModifiers) switch
        {
            (Key.F5, KeyModifiers.None) => RefreshAsync,
            (Key.F12, KeyModifiers.None) => ShowCommandLogAsync,
            (Key.Enter, KeyModifiers.Control) when (_tabs.SelectedItem as TabItem)?.Name == "ChangesTab" => CommitAsync,
            _ => null
        };
        if (action is null) { return; }
        e.Handled = true;
        try { ClosePopup(); await action(); }
        catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
    }

    private bool IsFocusedTextEditingKey(KeyEventArgs args)
    {
        if (FocusManager?.GetFocusedElement() is not (TextBox or AvaloniaEdit.Editing.TextArea)) { return false; }
        KeyModifiers modifiers = args.KeyModifiers & ~KeyModifiers.Shift;
        if (modifiers == KeyModifiers.None && (args.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9
            or Key.Space or Key.Insert || args.Key.ToString().StartsWith("Oem", StringComparison.Ordinal))) { return true; }
        modifiers &= ~KeyModifiers.Control;
        return modifiers == KeyModifiers.None && args.Key is Key.A or Key.C or Key.V or Key.X or Key.Y or Key.Z
            or Key.Back or Key.Delete or Key.Left or Key.Right or Key.Home or Key.End or Key.Up or Key.Down or Key.PageUp or Key.PageDown;
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

    private static KeyGesture? BrowserGesture(string label)
        => BrowseHotkeyName(label) is { } command ? DesktopHotkeys.Get("Browse", command) : null;
}
