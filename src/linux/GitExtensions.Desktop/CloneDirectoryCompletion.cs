using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

/// <summary>Local filesystem SuggestAppend, attached to the existing destination TextBox. MRU is independent.</summary>
internal sealed class CloneDirectoryCompletion : IDisposable
{
    private readonly TextBox _editor;
    private readonly Popup _popup;
    private readonly ListBox _suggestions = new() { Name = "CloneDirectorySuggestions", MaxHeight = 200 };
    private readonly TextBlock _error = new() { Name = "CloneDirectoryCompletionError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private CancellationTokenSource? _request;
    private long _version;
    private bool _changing, _disposed;
    private string? _typed;
    private readonly List<Exception> _failures = [];
    private readonly HashSet<Task> _operations = [];
    private bool _retainingSelection, _clearSelectionOnLostFocus;
    private string? _pressedPath;
    internal Func<string, IReadOnlyList<string>, Task>? BeforeApply { get; set; }

    internal CloneDirectoryCompletion(TextBox editor, Panel host, string controlNamePrefix = "Clone")
    {
        _editor = editor;
        _suggestions.Name = controlNamePrefix + "DirectorySuggestions";
        _error.Name = controlNamePrefix + "DirectoryCompletionError";
        foreach (string state in new[] { ":selected", ":pointerover", ":pressed" })
        {
            _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(state))
            { Setters = { new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
            _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("ClassicHighlightBrush")),
                new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
        }
        ScrollViewer.SetHorizontalScrollBarVisibility(_suggestions, ScrollBarVisibility.Disabled);
        _suggestions.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            TextBlock caption = new()
            {
                Text = ExportDialogControls.DisplayPath(Path.GetFileName(path ?? "")),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 1)
            };
            ToolTip.SetTip(caption, ExportDialogControls.DisplayPath(path ?? ""));
            return caption;
        });
        _popup = new() { Name = controlNamePrefix + "DirectoryCompletionPopup", PlacementTarget = editor, Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true, OverlayInputPassThroughElement = editor, OverlayDismissEventPassThrough = true, MaxWidth = 450,
            Child = new StackPanel { Children = { _suggestions, _error } } };
        _popup.Closed += PopupClosed;
        host.Children.Add(_popup);
        editor.AddHandler(InputElement.TextInputEvent, TextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        editor.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
        editor.PropertyChanged += Changed;
        editor.LostFocus += LostFocus;
        editor.AddHandler(InputElement.PointerPressedEvent, EditorPointerPressed, RoutingStrategies.Tunnel);
        _suggestions.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _suggestions.AddHandler(InputElement.PointerReleasedEvent, PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal IReadOnlyList<Exception> Failures => _failures.ToArray();
    internal Task Idle => Task.WhenAll(_operations.ToArray());
    internal bool IsPending => _operations.Any(operation => !operation.IsCompleted);

    private void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (!_changing && (args.Property == TextBox.TextProperty || args.Property == TextBox.CaretIndexProperty
            || args.Property == TextBox.SelectionStartProperty || args.Property == TextBox.SelectionEndProperty))
        {
            Invalidate(); _typed = null;
        }
        else if (args.Property == InputElement.IsEnabledProperty && !_editor.IsEffectivelyEnabled) { Invalidate(); }
    }

    private void TextInput(object? sender, TextInputEventArgs args)
    {
        if (_disposed || string.IsNullOrEmpty(args.Text) || !_editor.IsKeyboardFocusWithin || !_editor.IsEffectivelyEnabled) { return; }
        // TextBox has already inserted the real input; only a tail edit can append a suffix.
        string typed = _editor.Text ?? "";
        if (_editor.CaretIndex != typed.Length || _editor.SelectionStart != _editor.SelectionEnd) { return; }
        Invalidate(); _typed = typed;
        CancellationTokenSource request = new(); _request = request;
        _ = TrackCompletionAsync(typed, _version, request);
    }

    private async Task TrackCompletionAsync(string typed, long version, CancellationTokenSource request)
    {
        Task operation = CompleteAsync(typed, version, request);
        _operations.Add(operation);
        try { await operation; }
        finally { _operations.Remove(operation); }
    }

    private async Task CompleteAsync(string typed, long version, CancellationTokenSource request)
    {
        Func<string, IReadOnlyList<string>, Task>? beforeApply = BeforeApply;
        try
        {
            string[] paths = await Task.Run(() => ReadDirectories(typed, request.Token), request.Token);
            request.Token.ThrowIfCancellationRequested();
            // Tests can retain a response produced by the actual filesystem read.
            // Production leaves this hook null; it cannot replace any path.
            if (beforeApply is not null) { await beforeApply(typed, Array.AsReadOnly(paths)); }
            request.Token.ThrowIfCancellationRequested();
            if (_disposed || version != _version || !_editor.IsKeyboardFocusWithin || !_editor.IsEffectivelyEnabled
                || _editor.Text != typed || _editor.CaretIndex != typed.Length
                || _editor.SelectionStart != _editor.SelectionEnd) { return; }
            _suggestions.ItemsSource = paths; _suggestions.SelectedIndex = paths.Length == 0 ? -1 : 0;
            _error.IsVisible = false;
            SetPopupOpen(paths.Length > 0);
            int separator = typed.LastIndexOf(Path.DirectorySeparatorChar);
            if (paths.Length > 0 && separator < typed.Length - 1) { Preview(paths[0]); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            _failures.Add(error); // Retain errors even when a later edit makes their UI stale.
            if (!_disposed && version == _version && _editor.IsKeyboardFocusWithin && _editor.Text == typed)
            {
                _suggestions.ItemsSource = Array.Empty<string>(); _error.Text = error.Message; _error.IsVisible = true;
                SetPopupOpen(true);
            }
        }
        finally { if (ReferenceEquals(_request, request)) { _request = null; } request.Dispose(); }
    }

    private static string[] ReadDirectories(string typed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (typed.Length == 0) { return []; }
        int separator = typed.LastIndexOf(Path.DirectorySeparatorChar);
        string prefix = separator < 0 ? "" : typed[..(separator + 1)], fragment = typed[(separator + 1)..];
        string directory = prefix.Length == 0 ? "." : prefix;
        List<string> paths = [];
        foreach (string path in Directory.EnumerateDirectories(directory))
        {
            token.ThrowIfCancellationRequested(); string name = Path.GetFileName(path);
            if (name.StartsWith(fragment, StringComparison.Ordinal)) { paths.Add(prefix + name); }
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private void Preview(string path)
    {
        int start = _typed?.Length ?? path.Length;
        _changing = true;
        try
        {
            _editor.Text = path; _editor.CaretIndex = path.Length;
            _editor.SelectionStart = Math.Min(start, path.Length); _editor.SelectionEnd = path.Length;
        }
        finally { _changing = false; }
    }

    private void Accept(string? path = null)
    {
        path ??= _suggestions.SelectedItem as string;
        if (path is not null) { Preview(path); }
        _editor.SelectionStart = _editor.SelectionEnd = _editor.CaretIndex = (_editor.Text ?? "").Length;
        _typed = null; Invalidate(); _editor.Focus();
    }

    private void KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is Key.Back or Key.Delete) { Invalidate(); _typed = null; return; }
        if (!_popup.IsOpen) { return; }
        if (args.Key == Key.Escape)
        {
            string? typed = _typed; Invalidate(); _typed = null;
            if (typed is not null)
            {
                _changing = true;
                try { _editor.Text = typed; _editor.SelectionStart = _editor.SelectionEnd = _editor.CaretIndex = typed.Length; }
                finally { _changing = false; }
            }
            args.Handled = true;
        }
        else if (args.Key is Key.Enter or Key.Tab) { Accept(); args.Handled = args.Key == Key.Enter; }
        else if (args.Key is Key.Down or Key.Up && _suggestions.ItemCount > 0)
        {
            int direction = args.Key == Key.Down ? 1 : -1;
            _suggestions.SelectedIndex = Math.Clamp(_suggestions.SelectedIndex + direction, 0, _suggestions.ItemCount - 1);
            if (_suggestions.SelectedItem is string path) { Preview(path); } args.Handled = true;
        }
    }

    private ListBoxItem? Row(object? source)
        => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), _suggestions));
    private void EditorPointerPressed(object? sender, PointerPressedEventArgs args) => _pressedPath = null;
    private void PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _pressedPath = args.GetCurrentPoint(_suggestions).Properties.IsLeftButtonPressed
            ? Row(args.Source)?.Content as string : null;
    }
    private void PointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        ListBoxItem? row = Row(args.Source);
        string? pressed = _pressedPath; _pressedPath = null;
        if (args.InitialPressMouseButton == MouseButton.Left && row is not null && row.Content is string path && path == pressed
            && new Rect(row.Bounds.Size).Contains(args.GetPosition(row)))
        { Accept(path); args.Handled = true; }
    }
    private void SetPopupOpen(bool open)
    {
        if (open)
        {
            _popup.Width = Math.Min(450, Math.Max(100, _editor.Bounds.Width));
            if (!_retainingSelection)
            {
                _clearSelectionOnLostFocus = _editor.ClearSelectionOnLostFocus;
                _retainingSelection = true;
                _editor.SetCurrentValue(TextBox.ClearSelectionOnLostFocusProperty, false);
            }
        }
        _popup.IsOpen = open;
    }
    private void PopupClosed(object? sender, EventArgs args)
    {
        _pressedPath = null;
        if (!_retainingSelection) { return; }
        _retainingSelection = false;
        _editor.SetCurrentValue(TextBox.ClearSelectionOnLostFocusProperty, _clearSelectionOnLostFocus);
        if (_clearSelectionOnLostFocus && !_editor.IsKeyboardFocusWithin)
        {
            _changing = true;
            try { _editor.ClearSelection(); }
            finally { _changing = false; }
        }
    }
    private void LostFocus(object? sender, RoutedEventArgs args)
    {
        // Invalidate the lookup immediately even if focus returns before it completes.
        // Keep the existing popup until a pointer selection has had a chance to run.
        _version++; _request?.Cancel();
        Dispatcher.UIThread.Post(() => { if (!_disposed && !_editor.IsKeyboardFocusWithin && !_suggestions.IsKeyboardFocusWithin) { Invalidate(); _typed = null; } });
    }
    private void Invalidate()
    { _version++; _request?.Cancel(); _popup.IsOpen = false; }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; Invalidate();
        PopupClosed(null, EventArgs.Empty);
        _popup.Closed -= PopupClosed;
        _editor.RemoveHandler(InputElement.TextInputEvent, TextInput); _editor.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
        _editor.RemoveHandler(InputElement.PointerPressedEvent, EditorPointerPressed);
        _editor.PropertyChanged -= Changed; _editor.LostFocus -= LostFocus;
        _suggestions.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
        _suggestions.RemoveHandler(InputElement.PointerReleasedEvent, PointerReleased);
    }
}
