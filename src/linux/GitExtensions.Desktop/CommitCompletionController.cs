using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;
using GitUI.AutoCompletion;
using GitUI.SpellChecker;

namespace GitExtensions.Linux.Desktop;

/// <summary>Git completion decorates the existing native editor; it never owns its text, focus or undo stack.</summary>
internal sealed class CommitCompletionController
{
    private readonly SpellingEditorView _view;
    private readonly TextBox _editor;
    private readonly WordAtCursorExtractor _extractor = new();
    private readonly Popup _popup;
    private readonly ListBox _suggestions = new()
    { Name = "CommitCompletionSuggestions", MaxHeight = 220, MinWidth = 160, Focusable = false, IsTabStop = false };
    private readonly HashSet<Task> _work = [];
    private readonly List<Exception> _failures = [];
    private Func<CancellationToken, Task<IEnumerable<AutoCompleteWord>>>? _provider;
    private IReadOnlyList<AutoCompleteWord> _words = Array.Empty<AutoCompleteWord>();
    private CancellationTokenSource? _load, _delay;
    private long _corpusVersion, _interactionVersion, _lastTextInputVersion = -1;
    private bool _enabled = true, _closed, _accepting, _userActivated, _invalidating;
    private Task? _close;
    private EditorState? _shownState;
    private string? _pressedWord;
    private long _pressedVersion;

    internal TextBlock Notice { get; } = new()
    { Name = "CommitCompletionNotice", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    internal event Action<IReadOnlyList<string>>? CorpusPublished;
    internal Task Completion => _close ?? Task.WhenAll(_work.ToArray());
    internal IReadOnlyList<Exception> Failures => _failures.AsReadOnly();
    internal bool IsOpen => _popup.IsOpen;
    internal bool IsCorpusReady => !_closed && _enabled && _provider is not null && _load is null;
    internal IReadOnlyList<string> Corpus => Array.AsReadOnly(_words.Select(word => word.Word).ToArray());
    internal IReadOnlyList<string> Suggestions => Array.AsReadOnly(_suggestions.Items.Cast<AutoCompleteWord>().Select(word => word.Word).ToArray());

    internal CommitCompletionController(SpellingEditorView view)
    {
        _view = view; _editor = view.Editor;
        _suggestions.ItemTemplate = new FuncDataTemplate<AutoCompleteWord>((word, _) => new TextBlock
        { Text = word?.Word, Margin = new Thickness(4, 1), TextTrimming = TextTrimming.CharacterEllipsis });
        _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(InputElement.FocusableProperty, false), new Setter(InputElement.IsTabStopProperty, false) } });
        foreach (string state in new[] { ":selected", ":pointerover", ":pressed" })
        {
            _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(state))
            { Setters = { new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
            _suggestions.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("ClassicHighlightBrush")),
                new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
        }
        ScrollViewer.SetHorizontalScrollBarVisibility(_suggestions, ScrollBarVisibility.Disabled);
        _popup = new()
        {
            Name = "CommitCompletionPopup", PlacementTarget = _editor, Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.BottomLeft, PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = true, OverlayInputPassThroughElement = _editor, OverlayDismissEventPassThrough = true,
            MaxWidth = 600, Child = _suggestions, Focusable = false, IsTabStop = false
        };
        _view.Children.Add(_popup);
        _popup.Closed += PopupClosed;
        _editor.PropertyChanged += EditorChanged;
        _editor.LostFocus += LostFocus;
        _editor.AddHandler(InputElement.TextInputEvent, TextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        _editor.AddHandler(InputElement.KeyDownEvent, EditorKeyDown, RoutingStrategies.Tunnel);
        _editor.AddHandler(InputElement.PointerPressedEvent, EditorPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _view.CompositionChanged += CompositionChanged;
        _view.LayoutUpdated += LayoutUpdated;
        _suggestions.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _suggestions.AddHandler(InputElement.PointerReleasedEvent, PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>One immutable corpus per Git snapshot, never a Git request per keystroke.</summary>
    internal void Refresh(Func<CancellationToken, Task<IEnumerable<AutoCompleteWord>>> provider)
    {
        if (_closed) { return; }
        _provider = provider;
        CancelLoad(); ClearInteraction(); _words = Array.Empty<AutoCompleteWord>();
        CorpusPublished?.Invoke(Array.Empty<string>());
        SetNotice(null);
        if (_enabled) { StartLoad(); }
    }

    internal void SetEnabled(bool enabled)
    {
        if (_closed || _enabled == enabled) { return; }
        _enabled = enabled; CancelLoad(); ClearInteraction(); SetNotice(null);
        if (!enabled)
        {
            _words = Array.Empty<AutoCompleteWord>(); CorpusPublished?.Invoke(Array.Empty<string>());
        }
        else if (_provider is not null) { StartLoad(); }
    }

    private void StartLoad()
    {
        if (_provider is not { } provider) { return; }
        CancellationTokenSource request = new(); _load = request;
        Track(LoadAsync(provider, _corpusVersion, request));
    }

    private async Task LoadAsync(Func<CancellationToken, Task<IEnumerable<AutoCompleteWord>>> provider, long version, CancellationTokenSource request)
    {
        try
        {
            // Own the actual provider and deferred enumeration even when it ignores cancellation.
            AutoCompleteWord[] words = await Task.Run(async () =>
            {
                IEnumerable<AutoCompleteWord> supplied = await provider(request.Token);
                return supplied.Where(word => word is not null && !string.IsNullOrEmpty(word.Word))
                    .Distinct().OrderBy(word => word.Word, StringComparer.CurrentCulture).ToArray();
            });
            if (_closed || !_enabled || version != _corpusVersion || request.IsCancellationRequested) { return; }
            _words = Array.AsReadOnly(words); SetNotice(null);
            CorpusPublished?.Invoke(Array.AsReadOnly(words.Select(word => word.Word).ToArray()));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            _failures.Add(error);
            if (!_closed && _enabled && version == _corpusVersion)
            { SetNotice("Git completion: " + error.Message); }
        }
        finally
        {
            if (ReferenceEquals(_load, request)) { _load = null; }
            request.Dispose();
        }
    }

    private void Track(Task task)
    {
        _work.Add(task);
        _ = ObserveAsync(task);
    }
    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        finally { _work.Remove(task); }
    }
    private void CancelLoad() { _corpusVersion++; _load?.Cancel(); }
    private void SetNotice(string? text) { Notice.Text = text; Notice.IsVisible = !string.IsNullOrEmpty(text); }

    private readonly record struct EditorState(string Text, int Caret, int Start, int End);
    private EditorState State => new(_editor.Text ?? "", _editor.CaretIndex, _editor.SelectionStart, _editor.SelectionEnd);
    private bool CanComplete => !_closed && _enabled && _editor.IsEffectivelyEnabled && _editor.IsKeyboardFocusWithin && !_view.IsComposing;
    private string Prefix(EditorState state) => _extractor.Extract(state.Text, Math.Min(state.Start, state.End) - 1);

    private void EditorChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (_closed || _accepting) { return; }
        if (args.Property == TextBox.TextProperty || args.Property == TextBox.CaretIndexProperty
            || args.Property == TextBox.SelectionStartProperty || args.Property == TextBox.SelectionEndProperty)
        {
            Invalidate();
            long version = _interactionVersion;
            // Native insertion changes Text, caret and selection before the bubbling TextInput event.
            // A caret/selection change without that input ends explicit Ctrl+Space activation.
            Dispatcher.UIThread.Post(() =>
            { if (!_closed && version == _interactionVersion && _lastTextInputVersion != version) { _userActivated = false; } });
        }
        else if (args.Property == InputElement.IsEnabledProperty && !_editor.IsEffectivelyEnabled) { ClearInteraction(); }
    }

    private void TextInput(object? sender, TextInputEventArgs args)
    {
        if (!CanComplete || string.IsNullOrEmpty(args.Text)) { return; }
        _lastTextInputVersion = _interactionVersion;
        if (args.Text[^1].IsSeparator()) { ClearInteraction(); return; }
        Schedule();
    }

    private void Schedule()
    {
        Invalidate();
        _lastTextInputVersion = _interactionVersion;
        if (!CanComplete) { return; }
        EditorState state = State;
        if (Prefix(state).Length < 2 && !_userActivated) { return; }
        CancellationTokenSource request = new(); _delay = request;
        Track(AfterDelayAsync(state, _interactionVersion, _corpusVersion, request));
    }

    private async Task AfterDelayAsync(EditorState state, long version, long corpusVersion, CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(200, request.Token);
            if (!request.IsCancellationRequested && version == _interactionVersion && corpusVersion == _corpusVersion && state == State)
            { Show(calledByUser: false); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_delay, request)) { _delay = null; }
            request.Dispose();
        }
    }

    private void Show(bool calledByUser)
    {
        if (!CanComplete) { return; }
        if (_load is not null)
        {
            if (calledByUser) { SetNotice("Git completion is still parsing the changed files."); }
            return;
        }
        EditorState state = State;
        string prefix = Prefix(state);
        if (prefix.Length < 2 && !calledByUser && !_userActivated) { ClearInteraction(); return; }
        AutoCompleteWord[] matches = _words.Where(word => word.Matches(prefix)).ToArray();
        if (matches.Length == 0) { ClearInteraction(); return; }
        if (calledByUser && matches.Length == 1) { Accept(matches[0], state); return; }
        if (calledByUser) { _userActivated = true; }
        _shownState = state;
        _suggestions.ItemsSource = matches; _suggestions.SelectedIndex = 0;
        if (!PositionPopup()) { ClearInteraction(); return; }
        _popup.IsOpen = true;
    }

    /// <summary>The owning window delegates here before its default/cancel shortcuts run.</summary>
    internal bool HandleKeyDown(KeyEventArgs args)
    {
        if (args.Handled || !CanComplete) { return false; }
        if (args.Key == Key.Space && args.KeyModifiers.HasFlag(KeyModifiers.Control) && !args.KeyModifiers.HasFlag(KeyModifiers.Alt))
        { Invalidate(); Show(calledByUser: true); args.Handled = true; return true; }
        if (_popup.IsOpen && args.KeyModifiers == KeyModifiers.None)
        {
            if (args.Key == Key.Escape) { ClearInteraction(); args.Handled = true; return true; }
            if (args.Key is Key.Enter or Key.Tab)
            {
                if (_suggestions.SelectedItem is AutoCompleteWord word && _shownState is EditorState shown) { Accept(word, shown); }
                args.Handled = true; return true;
            }
            int count = _suggestions.ItemCount, selected = _suggestions.SelectedIndex;
            int page = Math.Max(1, (int)(_suggestions.Bounds.Height / Math.Max(1, _editor.FontSize + 6)) - 1);
            int? next = args.Key switch
            {
                Key.Up => (selected + count - 1) % count,
                Key.Down => (selected + 1) % count,
                Key.PageUp => Math.Max(0, selected - page), Key.PageDown => Math.Min(count - 1, selected + page),
                Key.Home => 0, Key.End => count - 1, _ => null
            };
            if (next is int index)
            { _suggestions.SelectedIndex = index; _suggestions.ScrollIntoView(_suggestions.SelectedItem!); args.Handled = true; return true; }
        }
        if (args.Key is Key.Back or Key.Delete && args.KeyModifiers == KeyModifiers.None)
        {
            Invalidate();
            Dispatcher.UIThread.Post(() => { if (CanComplete) { Schedule(); } });
        }
        else if (args.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            || args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Alt))
        { ClearInteraction(); }
        return false;
    }
    private void EditorKeyDown(object? sender, KeyEventArgs args) => HandleKeyDown(args);

    private void Accept(AutoCompleteWord word, EditorState expected)
    {
        if (!CanComplete || expected != State) { ClearInteraction(); return; }
        string prefix = Prefix(expected);
        int start = Math.Min(expected.Start, expected.End);
        ClearInteraction(); _accepting = true;
        try
        {
            _editor.SelectionStart = start - prefix.Length; _editor.SelectionEnd = start;
            _editor.SelectedText = word.Word;
        }
        finally { _accepting = false; }
    }

    private bool PositionPopup()
    {
        if (_shownState is not EditorState shown || shown != State || _view.GetCaretRectangle(Math.Min(shown.Start, shown.End)) is not Rect caret)
        { return false; }
        _popup.PlacementRect = caret;
        _popup.MaxWidth = Math.Max(160, Math.Min(600, TopLevel.GetTopLevel(_editor)?.ClientSize.Width ?? 600));
        return true;
    }
    private void LayoutUpdated(object? sender, EventArgs args)
    { if (_popup.IsOpen && (!CanComplete || !PositionPopup())) { ClearInteraction(); } }
    private void CompositionChanged(object? sender, EventArgs args) { if (_view.IsComposing) { ClearInteraction(); } }
    private void EditorPointerPressed(object? sender, PointerPressedEventArgs args) => ClearInteraction();
    private void LostFocus(object? sender, RoutedEventArgs args) => ClearInteraction();
    private ListBoxItem? Row(object? source) => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
        .FirstOrDefault(item => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), _suggestions));
    private void PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _pressedWord = null;
        if (args.GetCurrentPoint(_suggestions).Properties.IsLeftButtonPressed && Row(args.Source) is { Content: AutoCompleteWord word })
        {
            _pressedWord = word.Word; _pressedVersion = _interactionVersion; _suggestions.SelectedItem = word;
            // Consume before ListBoxItem can transfer focus or alter the editor's native selection.
            args.Handled = true;
        }
    }
    private void PointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        string? pressed = _pressedWord; _pressedWord = null;
        if (args.InitialPressMouseButton == MouseButton.Left && _popup.IsOpen && _pressedVersion == _interactionVersion
            && Row(args.Source) is { Content: AutoCompleteWord word } row && word.Word == pressed
            && new Rect(row.Bounds.Size).Contains(args.GetPosition(row)) && _shownState is EditorState shown)
        { Accept(word, shown); args.Handled = true; }
    }
    private void PopupClosed(object? sender, EventArgs args)
    { _shownState = null; _pressedWord = null; if (!_invalidating) { _userActivated = false; } }
    private void Invalidate()
    {
        _interactionVersion++; _delay?.Cancel(); _pressedWord = null;
        _invalidating = true;
        try { _popup.IsOpen = false; }
        finally { _invalidating = false; }
        _shownState = null;
    }
    private void ClearInteraction() { Invalidate(); _userActivated = false; }

    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closed = true; CancelLoad(); ClearInteraction(); SetNotice(null);
        _editor.PropertyChanged -= EditorChanged; _editor.LostFocus -= LostFocus;
        _editor.RemoveHandler(InputElement.TextInputEvent, TextInput); _editor.RemoveHandler(InputElement.KeyDownEvent, EditorKeyDown);
        _editor.RemoveHandler(InputElement.PointerPressedEvent, EditorPointerPressed);
        _view.CompositionChanged -= CompositionChanged; _view.LayoutUpdated -= LayoutUpdated;
        _popup.Closed -= PopupClosed;
        _suggestions.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
        _suggestions.RemoveHandler(InputElement.PointerReleasedEvent, PointerReleased);
        _close = Task.WhenAll(_work.ToArray());
        return _close;
    }
}
