using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

internal static class RepositorySourceControls
{
    internal static ComboBox EditableChoice(string name) => new()
    {
        Name = name, IsEditable = true, IsTextSearchEnabled = false, Height = 24, MinWidth = 80, Padding = new Thickness(4, 2),
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    internal static string RepositoryName(string source)
    {
        string path = source.TrimEnd('/');
        if (!Path.IsPathFullyQualified(path) && Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "ssh" or "git" or "file" or "ftp" or "ftps")
        { path = System.Net.WebUtility.UrlDecode(uri.AbsolutePath).TrimEnd('/'); }
        else if (!Path.IsPathFullyQualified(path) && path.IndexOf(':') is int colon && colon > 0 && !path[..colon].Contains('/'))
        { path = System.Net.WebUtility.UrlDecode(path[(colon + 1)..]); }
        int split = path.LastIndexOf('/');
        string name = path[(split + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    internal static IDisposable CompleteHistory(ComboBox choice, Panel host, Action<string>? selectionCommitted = null,
        StringComparison comparison = StringComparison.Ordinal) => new HistoryCompletion(choice, host, selectionCommitted, comparison);

    /// <summary>SuggestAppend from the same MRU shown by the editable choice, preserving case-sensitive Linux paths.</summary>
    private sealed class HistoryCompletion : IDisposable
    {
        private readonly ComboBox _choice;
        private readonly Action<string>? _selectionCommitted;
        private readonly StringComparison _comparison;
        private readonly Panel _host;
        private readonly Popup _popup;
        private readonly ListBox _suggestions = new() { MaxHeight = 200, Padding = default, BorderThickness = default };
        private TextBox? _editor;
        private Window? _window;
        private bool _changing, _disposed, _retainingSelection, _clearSelectionOnLostFocus, _nativeDropDownQueued;
        private string? _typed, _pressedItem;

        internal HistoryCompletion(ComboBox choice, Panel host, Action<string>? selectionCommitted, StringComparison comparison)
        {
            _choice = choice; _host = host; _selectionCommitted = selectionCommitted; _comparison = comparison;
            _suggestions.Name = choice.Name + "HistorySuggestions";
            _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
            {
                new Setter(TemplatedControl.MinHeightProperty, 20d), new Setter(TemplatedControl.PaddingProperty, new Thickness(3, 1)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            } });
            foreach (string state in new[] { ":selected", ":pointerover", ":pressed" })
            {
                _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(state))
                { Setters = { new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
                _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                { Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("ClassicHighlightBrush")),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
            }
            ScrollViewer.SetHorizontalScrollBarVisibility(_suggestions, ScrollBarVisibility.Disabled);
            _suggestions.ItemTemplate = new FuncDataTemplate<string>((value, _) =>
            {
                TextBlock caption = new() { Text = ExportDialogControls.DisplayPath(value ?? ""), TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(caption, ExportDialogControls.DisplayPath(value ?? "")); return caption;
            });
            _popup = new() { Name = choice.Name + "HistoryCompletionPopup", PlacementTarget = choice, Placement = PlacementMode.Bottom,
                IsLightDismissEnabled = true, OverlayInputPassThroughElement = choice, OverlayDismissEventPassThrough = true, MaxWidth = selectionCommitted is null ? 600 : double.PositiveInfinity,
                Child = ClassicChrome.Border(_suggestions, new Thickness(1)) };
            _popup.Closed += PopupClosed;
            if (host is DockPanel) { DockPanel.SetDock(_popup, Dock.Bottom); }
            host.Children.Insert(0, _popup);
            choice.AddHandler(InputElement.TextInputEvent, Complete, RoutingStrategies.Bubble, handledEventsToo: true);
            choice.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
            choice.AddHandler(InputElement.PointerPressedEvent, ChoicePointerPressed, RoutingStrategies.Tunnel);
            choice.PropertyChanged += ChoiceChanged;
            choice.TemplateApplied += TemplateApplied;
            choice.AttachedToVisualTree += Attached;
            choice.DetachedFromVisualTree += Detached;
            choice.LostFocus += LostFocus; choice.DropDownOpened += NativeDropDownOpened;
            _suggestions.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
            _suggestions.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _suggestions.AddHandler(InputElement.PointerReleasedEvent, PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            _suggestions.LostFocus += LostFocus;
            AttachEditor(); AttachWindow();
        }

        private void Complete(object? sender, TextInputEventArgs args)
        {
            if (_disposed || _changing || string.IsNullOrEmpty(args.Text) || !_choice.IsEffectivelyEnabled || !_choice.IsKeyboardFocusWithin) { return; }
            AttachEditor(); if (_editor is not TextBox editor) { return; }
            string typed = editor.Text ?? "";
            Dismiss();
            if (typed.Length == 0 || editor.CaretIndex != typed.Length || editor.SelectionStart != editor.SelectionEnd) { return; }
            string[] matches = _choice.Items.OfType<string>().Where(item => item.StartsWith(typed, _comparison)).ToArray();
            if (matches.Length == 0) { return; }
            _typed = typed; _suggestions.ItemsSource = matches; _suggestions.SelectedIndex = 0;
            _choice.IsDropDownOpen = false;
            _popup.Width = CompletionWidth;
            if (!_retainingSelection)
            {
                _clearSelectionOnLostFocus = editor.ClearSelectionOnLostFocus; _retainingSelection = true;
                editor.SetCurrentValue(TextBox.ClearSelectionOnLostFocusProperty, false);
            }
            _popup.IsOpen = true;
            Preview(matches[0]);
        }

        private double CompletionWidth => _selectionCommitted is null ? Math.Min(600, Math.Max(100, _choice.Bounds.Width)) : _choice.Bounds.Width;

        private void NativeDropDownOpened(object? sender, EventArgs args)
        {
            // Finish ComboBox's own popup/binding transition before replacing it.
            // Closing and reopening synchronously inside PopupOpened reenters its template bindings.
            if (_selectionCommitted is null || _disposed || _nativeDropDownQueued || _popup.IsOpen) { return; }
            _nativeDropDownQueued = true; AttachEditor();
            string typed = _editor?.Text ?? _choice.Text ?? "";
            int caret = _editor?.CaretIndex ?? typed.Length, start = _editor?.SelectionStart ?? 0, end = _editor?.SelectionEnd ?? 0;
            if (_editor is TextBox editor && !_retainingSelection)
            {
                _clearSelectionOnLostFocus = editor.ClearSelectionOnLostFocus; _retainingSelection = true;
                editor.SetCurrentValue(TextBox.ClearSelectionOnLostFocusProperty, false);
            }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_disposed || !_choice.IsEffectivelyEnabled || _window?.IsVisible != true || !_choice.IsDropDownOpen) { return; }
                    _changing = true;
                    try
                    {
                        _choice.IsDropDownOpen = false;
                        _editor?.Focus(); _choice.Text = typed;
                        if (_editor is TextBox input)
                        {
                            input.Text = typed; input.CaretIndex = caret; input.SelectionStart = start; input.SelectionEnd = end;
                        }
                    }
                    finally { _changing = false; }
                    _typed = typed; _suggestions.ItemsSource = _choice.Items.OfType<string>().ToArray(); _suggestions.SelectedIndex = -1;
                    _popup.Width = CompletionWidth; _popup.IsOpen = true;
                }
                finally
                {
                    _nativeDropDownQueued = false;
                    if (!_popup.IsOpen) { PopupClosed(null, EventArgs.Empty); }
                }
            });
        }

        private void Preview(string value)
        {
            if (_editor is not TextBox editor) { return; }
            _changing = true;
            try
            {
                _choice.Text = value; editor.Text = value; editor.CaretIndex = value.Length;
                editor.SelectionStart = Math.Min(_typed?.Length ?? value.Length, value.Length); editor.SelectionEnd = value.Length;
            }
            finally { _changing = false; }
        }

        private void Accept(string? value = null, bool commit = false)
        {
            value ??= _suggestions.SelectedItem as string;
            if (value is not null) { Preview(value); }
            if (_editor is TextBox editor)
            {
                _changing = true;
                try
                {
                    // ComboBox selects its editable text on focus; finish acceptance after that transition.
                    editor.Focus();
                    editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = (editor.Text ?? "").Length;
                }
                finally { _changing = false; }
            }
            string accepted = value ?? _choice.Text ?? "";
            Dismiss();
            if (commit) { _selectionCommitted?.Invoke(accepted); }
        }

        private void KeyDown(object? sender, KeyEventArgs args)
        {
            if (_disposed) { return; }
            if (args.Key is Key.Back or Key.Delete) { Dismiss(); return; }
            if (!_popup.IsOpen || args.KeyModifiers != KeyModifiers.None && !(args.Key == Key.Tab && args.KeyModifiers == KeyModifiers.Shift)) { return; }
            if (args.Key == Key.Escape)
            {
                string? typed = _typed; Dismiss();
                if (typed is not null && _editor is TextBox editor)
                {
                    _changing = true;
                    try { _choice.Text = typed; editor.Text = typed; editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex = typed.Length; }
                    finally { _changing = false; }
                }
                _editor?.Focus(); args.Handled = true;
            }
            else if (args.Key is Key.Enter or Key.Tab) { Accept(commit: args.Key == Key.Enter); args.Handled = args.Key == Key.Enter; }
            else if (args.Key is Key.Down or Key.Up && _suggestions.ItemCount > 0)
            {
                int direction = args.Key == Key.Down ? 1 : -1;
                _suggestions.SelectedIndex = Math.Clamp(_suggestions.SelectedIndex + direction, 0, _suggestions.ItemCount - 1);
                if (_suggestions.SelectedItem is string value) { Preview(value); _suggestions.ScrollIntoView(value); }
                args.Handled = true;
            }
        }

        private void ChoiceChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (_disposed || _changing) { return; }
            if (args.Property == ComboBox.TextProperty || args.Property == ItemsControl.ItemsSourceProperty
                || args.Property == ComboBox.IsDropDownOpenProperty && _choice.IsDropDownOpen
                || args.Property == InputElement.IsEffectivelyEnabledProperty && !_choice.IsEffectivelyEnabled
                || args.Property == Visual.IsVisibleProperty && !_choice.IsVisible) { Dismiss(); }
        }

        private void EditorChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (!_disposed && !_changing && (args.Property == TextBox.TextProperty || args.Property == TextBox.CaretIndexProperty
                || args.Property == TextBox.SelectionStartProperty || args.Property == TextBox.SelectionEndProperty)) { Dismiss(); }
        }

        private void AttachEditor()
        {
            TextBox? editor = _choice.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (ReferenceEquals(editor, _editor)) { return; }
            Dismiss();
            if (_editor is not null) { _editor.PropertyChanged -= EditorChanged; }
            _editor = editor;
            if (_editor is not null) { _editor.PropertyChanged += EditorChanged; }
        }

        private void AttachWindow()
        {
            Window? window = TopLevel.GetTopLevel(_choice) as Window;
            if (ReferenceEquals(window, _window)) { return; }
            DetachWindow(); _window = window;
            if (_window is not null) { _window.Closed += WindowClosed; _window.Deactivated += WindowDeactivated; }
        }

        private void DetachWindow()
        {
            if (_window is not null) { _window.Closed -= WindowClosed; _window.Deactivated -= WindowDeactivated; _window = null; }
        }

        private void TemplateApplied(object? sender, TemplateAppliedEventArgs args) { AttachEditor(); AttachWindow(); }
        private void Attached(object? sender, VisualTreeAttachmentEventArgs args) { AttachEditor(); AttachWindow(); }
        private void Detached(object? sender, VisualTreeAttachmentEventArgs args) { Dismiss(); DetachWindow(); }
        private void WindowClosed(object? sender, EventArgs args) => Dispose();
        private void WindowDeactivated(object? sender, EventArgs args) => Dismiss();

        private void LostFocus(object? sender, RoutedEventArgs args)
        {
            // A pointer click may be moving focus into the suggestion before its release accepts it.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && !_choice.IsKeyboardFocusWithin && !_suggestions.IsKeyboardFocusWithin) { Dismiss(); }
            });
        }

        private ListBoxItem? Row(object? source)
            => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), _suggestions));
        private void ChoicePointerPressed(object? sender, PointerPressedEventArgs args) => _pressedItem = null;
        private void PointerPressed(object? sender, PointerPressedEventArgs args)
        {
            _pressedItem = args.GetCurrentPoint(_suggestions).Properties.IsLeftButtonPressed ? Row(args.Source)?.Content as string : null;
        }
        private void PointerReleased(object? sender, PointerReleasedEventArgs args)
        {
            ListBoxItem? row = Row(args.Source); string? pressed = _pressedItem; _pressedItem = null;
            if (args.InitialPressMouseButton == MouseButton.Left && row?.Content is string value && value == pressed
                && new Rect(row.Bounds.Size).Contains(args.GetPosition(row))) { Accept(value, commit: true); args.Handled = true; }
        }

        private void Dismiss() { _typed = null; _pressedItem = null; _popup.IsOpen = false; }
        private void PopupClosed(object? sender, EventArgs args)
        {
            _typed = null; _pressedItem = null;
            if (!_retainingSelection || _editor is not TextBox editor) { return; }
            _retainingSelection = false;
            editor.SetCurrentValue(TextBox.ClearSelectionOnLostFocusProperty, _clearSelectionOnLostFocus);
            if (_clearSelectionOnLostFocus && !editor.IsKeyboardFocusWithin)
            {
                _changing = true;
                try { editor.ClearSelection(); }
                finally { _changing = false; }
            }
        }

        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true; Dismiss(); PopupClosed(null, EventArgs.Empty); DetachWindow();
            _popup.Closed -= PopupClosed;
            _choice.RemoveHandler(InputElement.TextInputEvent, Complete); _choice.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
            _choice.RemoveHandler(InputElement.PointerPressedEvent, ChoicePointerPressed);
            _choice.PropertyChanged -= ChoiceChanged; _choice.TemplateApplied -= TemplateApplied;
            _choice.AttachedToVisualTree -= Attached; _choice.DetachedFromVisualTree -= Detached; _choice.LostFocus -= LostFocus; _choice.DropDownOpened -= NativeDropDownOpened;
            if (_editor is not null) { _editor.PropertyChanged -= EditorChanged; }
            _suggestions.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
            _suggestions.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed); _suggestions.RemoveHandler(InputElement.PointerReleasedEvent, PointerReleased);
            _suggestions.LostFocus -= LostFocus; _host.Children.Remove(_popup);
        }
    }
}
