using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

/// <summary>The editable, lazily populated branch expression control from FilterToolBar.</summary>
internal sealed class BranchHistoryFilterComboBox : ComboBox
{
    private bool _setting;
    private int _suggestionRequest;
    private TextBox? _editor;
    private Popup? _popup;
    private TopLevel? _pointerOwner;
    internal Func<string, IReadOnlyList<string>> Suggestions { get; set; } = _ => [];
    internal event EventHandler? DraftChanged;
    internal event EventHandler? AcceptRequested;
    protected override Type StyleKeyOverride => typeof(ComboBox);

    internal BranchHistoryFilterComboBox()
    {
        IsEditable = true; IsTextSearchEnabled = false;
        Width = 100; Height = 23; MinHeight = 23; MaxHeight = 23;
        Padding = new Thickness(2, 0); MaxDropDownHeight = 300;
        Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>())
        { Setters = { new Setter(MinHeightProperty, 20d), new Setter(PaddingProperty, new Thickness(4, 1)),
            new Setter(ForegroundProperty, new DynamicResourceExtension("ClassicTextBrush")) } });
        Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        { Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("ClassicEditorBrush")),
            new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("ClassicTextBrush")) } });
        // Fluent's ComboBoxItemBackgroundSelected/PointerOver/Pressed resources
        // are independent of the older SystemControlHighlightList* brushes.
        // Override the surface which actually paints the popup row, including
        // its inherited text color when hosted by the toolbar's PopupRoot.
        foreach (string state in new[] { ":selected", ":pointerover", ":pressed" })
        {
            Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>().Class(state))
            { Setters = { new Setter(ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
            Styles.Add(new Style(selector => selector.OfType<ComboBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("ClassicHighlightBrush")),
                new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("ClassicHighlightTextBrush")) } });
        }
        DropDownOpened += (_, _) => RefreshSuggestions();
        SelectionChanged += (_, _) =>
        {
            if (SelectedItem is not null) { CancelSuggestionRefresh(); }
        };
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        AttachedToVisualTree += (_, _) =>
        {
            _pointerOwner = TopLevel.GetTopLevel(this);
            _pointerOwner?.AddHandler(PointerPressedEvent, OutsidePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            ConfigurePopup();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            IsDropDownOpen = false;
            _pointerOwner?.RemoveHandler(PointerPressedEvent, OutsidePointerPressed);
            _pointerOwner = null;
        };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.KeyModifiers != Avalonia.Input.KeyModifiers.None) { return; }
            if (args.Key == Avalonia.Input.Key.Enter)
            { args.Handled = true; CancelSuggestionRefresh(); IsDropDownOpen = false; AcceptRequested?.Invoke(this, EventArgs.Empty); }
            else if (args.Key == Avalonia.Input.Key.Escape && IsDropDownOpen)
            { args.Handled = true; CloseSuggestions(); }
            else if (args.Key is Avalonia.Input.Key.Down or Avalonia.Input.Key.Up)
            {
                args.Handled = true; CancelSuggestionRefresh();
                if (!IsDropDownOpen) { IsDropDownOpen = true; }
                else { ChooseSuggestion(args.Key == Avalonia.Input.Key.Down ? 1 : -1); }
            }
        }, RoutingStrategies.Tunnel);
        ApplyPalette();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
    {
        if (_editor is not null) { _editor.PointerReleased -= EditorClicked; }
        base.OnApplyTemplate(args);
        _editor = args.NameScope.Find<TextBox>("PART_EditableTextBox") ?? this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        _popup = args.NameScope.Find<Popup>("PART_Popup");
        if (_editor is not null)
        {
            _editor.MinHeight = 0; _editor.Height = double.NaN; _editor.Padding = new Thickness(2, 0);
            _editor.PointerReleased += EditorClicked;
            _editor.Text = Text ?? "";
        }
        ConfigurePopup();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property != TextProperty || _setting) { return; }
        DraftChanged?.Invoke(this, EventArgs.Empty);
        // A suggestion changes the draft, but neither selects a different filter
        // nor repopulates the list while the user is navigating that list.
        if (_editor?.IsFocused != true || SelectedItem is string chosen && chosen == Text) { return; }
        int request = ++_suggestionRequest;
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _suggestionRequest || _editor?.IsFocused != true || !IsEffectivelyEnabled || !IsEffectivelyVisible) { return; }
            RefreshSuggestions(); IsDropDownOpen = true;
        });
    }

    private void CancelSuggestionRefresh() => _suggestionRequest++;

    private void CloseSuggestions()
    {
        CancelSuggestionRefresh();
        string? text = Text;
        int start = _editor?.SelectionStart ?? 0, end = _editor?.SelectionEnd ?? 0;
        IsDropDownOpen = false;
        _editor?.Focus();
        RestoreCaret(text, start, end);
        // ComboBox returns focus from its popup after the current input event.
        // Restore the editor's existing caret after that focus transition too.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDropDownOpen && _editor?.IsFocused == true) { RestoreCaret(text, start, end); }
        });
    }

    private void RestoreCaret(string? text, int start, int end)
    {
        if (_editor is null || Text != text) { return; }
        _editor.SelectionStart = Math.Min(start, text?.Length ?? 0);
        _editor.SelectionEnd = Math.Min(end, text?.Length ?? 0);
    }

    private void ChooseSuggestion(int direction)
    {
        if (Items.Count == 0) { return; }
        int index = SelectedIndex < 0 ? direction > 0 ? 0 : Items.Count - 1 : Math.Clamp(SelectedIndex + direction, 0, Items.Count - 1);
        _setting = true;
        try
        {
            SelectedIndex = index;
            Text = Items[index] as string;
            if (_editor is not null)
            {
                _editor.Text = Text ?? ""; _editor.Focus();
                _editor.SelectionStart = _editor.SelectionEnd = Text?.Length ?? 0;
            }
        }
        finally { _setting = false; }
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditorClicked(object? sender, Avalonia.Input.PointerReleasedEventArgs args)
    {
        if (args.InitialPressMouseButton == Avalonia.Input.MouseButton.Left && !IsDropDownOpen) { IsDropDownOpen = true; }
    }

    private void OutsidePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (!IsDropDownOpen || _pointerOwner is null || args.Source is not Visual source || TopLevel.GetTopLevel(source) != _pointerOwner) { return; }
        PixelPoint screen = _pointerOwner.PointToScreen(args.GetPosition(_pointerOwner));
        if (ContainsScreenPoint(this, screen) || _popup?.Child is Visual child && ContainsScreenPoint(child, screen)) { return; }
        // Light-dismiss pass-through otherwise dispatches the button's press
        // first, then ComboBox restores its editor focus and cancels that press.
        // Close in the owner's tunnel before the real target receives its press.
        CancelSuggestionRefresh(); IsDropDownOpen = false;
    }

    private static bool ContainsScreenPoint(Visual visual, PixelPoint screen)
    {
        if (TopLevel.GetTopLevel(visual) is not { } owner || !double.IsFinite(visual.Bounds.Width) || !double.IsFinite(visual.Bounds.Height)
            || visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0) { return false; }
        PixelPoint origin = visual.PointToScreen(default);
        return screen.X >= origin.X && screen.Y >= origin.Y
            && screen.X < origin.X + visual.Bounds.Width * owner.RenderScaling
            && screen.Y < origin.Y + visual.Bounds.Height * owner.RenderScaling;
    }

    internal void SetDraft(string? text)
    {
        CancelSuggestionRefresh();
        bool same = (Text ?? "") == (text ?? "");
        int start = _editor?.SelectionStart ?? 0, end = _editor?.SelectionEnd ?? 0;
        _setting = true;
        try
        {
            SelectedIndex = -1; Text = text;
            if (_editor is not null)
            {
                _editor.Text = text ?? "";
                _editor.SelectionStart = same ? Math.Min(start, text?.Length ?? 0) : text?.Length ?? 0;
                _editor.SelectionEnd = same ? Math.Min(end, text?.Length ?? 0) : text?.Length ?? 0;
            }
        }
        finally { _setting = false; }
    }

    internal void InvalidateSuggestions(bool close = false)
    {
        bool wasOpen = IsDropDownOpen;
        ReplaceItems([]);
        if (close) { IsDropDownOpen = false; }
        else if (wasOpen) { RefreshSuggestions(); }
    }

    internal void FocusEditor(bool selectAll = false)
    {
        ApplyTemplate(); _editor?.Focus();
        if (selectAll) { _editor?.SelectAll(); }
    }

    internal (string? Text, int Start, int End) CaptureEditorSelection()
        => (Text, _editor?.SelectionStart ?? 0, _editor?.SelectionEnd ?? 0);

    internal void RestoreEditorSelection((string? Text, int Start, int End) bookmark)
    {
        if (Text != bookmark.Text) { return; }
        FocusEditor(); RestoreCaret(bookmark.Text, bookmark.Start, bookmark.End);
        Dispatcher.UIThread.Post(() =>
        {
            if (_editor?.IsFocused == true && IsEffectivelyVisible && IsEffectivelyEnabled)
            { RestoreCaret(bookmark.Text, bookmark.Start, bookmark.End); }
        });
    }

    private void RefreshSuggestions()
    {
        ReplaceItems(Suggestions(Text ?? "")); ConfigurePopup();
    }

    private void ReplaceItems(IReadOnlyList<string> items)
    {
        string? text = Text;
        int start = _editor?.SelectionStart ?? 0, end = _editor?.SelectionEnd ?? 0;
        _setting = true;
        try
        {
            ItemsSource = items; SelectedIndex = -1; Text = text;
            if (_editor is not null)
            { _editor.Text = text ?? ""; _editor.SelectionStart = start; _editor.SelectionEnd = end; }
        }
        finally { _setting = false; }
    }

    private void ConfigurePopup()
    {
        if (_popup is null) { return; }
        _popup.OverlayDismissEventPassThrough = true;
        _popup.OverlayInputPassThroughElement = _pointerOwner;
        double widest = Items.OfType<string>().Select(text => new FormattedText(text,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize,
            Foreground ?? Brushes.Black).Width + 24).DefaultIfEmpty(300).Max();
        _popup.MinWidth = Math.Clamp(widest, 300, 600); _popup.MaxWidth = 600;
    }

    private void ApplyPalette()
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(ActualThemeVariant == ThemeVariant.Dark);
        foreach ((string key, string color) in new Dictionary<string, string>
        {
            ["ClassicEditorBrush"] = "EditorBackground", ["ClassicTextBrush"] = "WindowText",
            ["ClassicHighlightBrush"] = "Highlight", ["ClassicHighlightTextBrush"] = ActualThemeVariant == ThemeVariant.Dark ? "WindowText" : "HighlightText",
            ["ComboBoxDropDownBackground"] = "EditorBackground", ["ComboBoxDropDownBorderBrush"] = "ActiveBorder",
            ["SystemControlHighlightListLowBrush"] = "Highlight", ["SystemControlHighlightListMediumBrush"] = "Highlight",
            ["SystemControlHighlightListAccentLowBrush"] = "Highlight", ["SystemControlHighlightListAccentMediumBrush"] = "Highlight",
            ["SystemControlHighlightListAccentHighBrush"] = "Highlight", ["SystemControlHighlightAltBaseHighBrush"] = "HighlightText",
            ["ComboBoxForegroundFocused"] = "WindowText", ["ComboBoxForegroundFocusedPressed"] = "WindowText",
            ["ComboBoxDropDownGlyphForeground"] = "WindowText", ["ComboBoxDropDownGlyphForegroundFocused"] = "WindowText",
            ["ComboBoxDropDownGlyphForegroundFocusedPressed"] = "WindowText"
        }) { Resources[key] = Brush.Parse(colors[color]); }
        Resources["OverlayCornerRadius"] = new CornerRadius(0);
    }
}
