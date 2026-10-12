using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

/// <summary>One original ToolStrip row; a contiguous suffix remains available in its overflow menu.</summary>
internal sealed class CompactBrowserToolbar : Border
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly List<Entry> _entries = [];
    private readonly ObservableCollection<Control> _popupItems = [];
    private readonly MenuFlyout _flyout = new();
    private bool _placing;
    private TopLevel? _pointerOwner;
    private MenuFlyoutPresenter? _pointerPresenter;
    private PixelPoint? _lastPointerScreen, _revealPointerScreen;
    private bool _keyboardReveal;
    private MenuItem? _pendingHoverItem;
    private RoutedEvent? _pendingHoverEvent;
    internal Button Overflow { get; }
    internal IReadOnlyList<Control> InlineItems => _entries.Where(entry => entry.Host.IsVisible).Select(entry => entry.Control).ToArray();
    internal MenuFlyout OverflowFlyout => _flyout;

    internal CompactBrowserToolbar()
    {
        Name = "RepositoryToolbar";
        Height = 26; MinHeight = 26; MaxHeight = 26; Padding = new Thickness(3, 1);
        _flyout.ItemsSource = _popupItems;
        DiffViewerPopupLayout.Configure(_flyout);
        Overflow = new Button
        {
            Name = "RepositoryToolbarOverflow", Content = "»", Width = 24, Height = 24,
            MinWidth = 0, MinHeight = 24, Padding = default, IsVisible = false, Flyout = _flyout
        };
        _row.Children.Add(Overflow); Child = _row;
        SizeChanged += (_, _) => { EndKeyboardReveal(); Place(); };
        _flyout.Opened += (_, _) => { ApplyPalette(); ObservePresenter(); };
        _flyout.Closed += (_, _) => { EndKeyboardReveal(); ObservePresenter(null); };
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        AttachedToVisualTree += (_, _) =>
        {
            _pointerOwner = TopLevel.GetTopLevel(this);
            _pointerOwner?.AddHandler(PointerMovedEvent, ObservePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            _pointerOwner?.AddHandler(PointerPressedEvent, HandlePointerPress, RoutingStrategies.Tunnel, handledEventsToo: true);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            EndKeyboardReveal(); _flyout.Hide(); ObservePresenter(null);
            _pointerOwner?.RemoveHandler(PointerMovedEvent, ObservePointer);
            _pointerOwner?.RemoveHandler(PointerPressedEvent, HandlePointerPress);
            _pointerOwner = null; _lastPointerScreen = null;
        };
        ApplyPalette();
    }

    internal void Add(Control control, Control popup)
    {
        Border host = new() { Child = control, Height = 24 };
        _entries.Add(new(control, host, popup));
        if (popup is MenuItem menu)
        {
            menu.AddHandler(MenuItem.PointerEnteredItemEvent, SuspendHover);
            menu.AddHandler(MenuItem.PointerExitedItemEvent, SuspendHover);
            menu.PointerEntered += ObservePointer;
            menu.PointerExited += ObservePointer;
        }
        _row.Children.Insert(_row.Children.Count - 1, host);
        control.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty || change.Property == WidthProperty) { Place(); }
        };
    }

    internal Control Reveal(Control control)
    {
        Entry? entry = _entries.FirstOrDefault(entry => ReferenceEquals(entry.Control, control) || control.GetVisualAncestors().Contains(entry.Control));
        if (entry is null || entry.Host.IsVisible) { return control; }
        if (_flyout.IsOpen) { _flyout.Hide(); }
        // Mapping beneath a stationary mouse must not let the menu's synthetic
        // hover select another item and steal the keyboard field's focus.
        _keyboardReveal = true; _revealPointerScreen = _lastPointerScreen;
        _flyout.ShowAt(Overflow);
        entry.Popup.UpdateLayout();
        if (entry.Popup is MenuItem { HasSubMenu: true } submenu)
        {
            submenu.IsSubMenuOpen = true; submenu.UpdateLayout();
            foreach (Control header in submenu.Items.OfType<MenuItem>().Select(item => item.Header).OfType<Control>())
            {
                Control? editor = (Control?)header.GetVisualDescendants().OfType<BranchHistoryFilterComboBox>().FirstOrDefault()
                    ?? header.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                if (editor is not null) { return editor; }
            }
        }
        return (Control?)entry.Popup.GetVisualDescendants().OfType<BranchHistoryFilterComboBox>().FirstOrDefault()
            ?? entry.Popup.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? control;
    }

    private void SuspendHover(object? sender, RoutedEventArgs args)
    {
        if (!_keyboardReveal || args.Source is not MenuItem item) { return; }
        _pendingHoverItem = item; _pendingHoverEvent = args.RoutedEvent;
        args.Handled = true;
    }

    private void ObservePointer(object? sender, PointerEventArgs args)
    {
        if (sender is not Visual visual || TopLevel.GetTopLevel(visual) is null) { return; }
        PixelPoint point = visual.PointToScreen(args.GetPosition(visual));
        _lastPointerScreen = point;
        if (!_keyboardReveal) { return; }
        _revealPointerScreen ??= point;
        if (point == _revealPointerScreen) { return; }
        MenuItem? item = _pendingHoverItem; RoutedEvent? hover = _pendingHoverEvent;
        EndKeyboardReveal();
        // PointerOverPreProcessor may precede PointerMoved, including with overlay
        // popups. Relay the suppressed event once so the first real move works.
        if (item is not null && hover is not null) { item.RaiseEvent(new RoutedEventArgs(hover) { Source = item }); }
    }

    private void HandlePointerPress(object? sender, PointerPressedEventArgs args)
    { EndKeyboardReveal(); ObservePointer(sender, args); }

    private void MenuKeyDown(object? sender, KeyEventArgs args)
    { if (args.Key is Key.Up or Key.Down or Key.Tab or Key.Escape) { EndKeyboardReveal(); } }

    private void EndKeyboardReveal()
    { _keyboardReveal = false; _revealPointerScreen = null; _pendingHoverItem = null; _pendingHoverEvent = null; }

    private void ObservePresenter()
    {
        ObservePresenter(_flyout.Items.OfType<Control>().SelectMany(item => item.GetVisualAncestors().OfType<MenuFlyoutPresenter>()).FirstOrDefault());
    }
    private void ObservePresenter(MenuFlyoutPresenter? presenter)
    {
        if (ReferenceEquals(_pointerPresenter, presenter)) { return; }
        _pointerPresenter?.RemoveHandler(PointerMovedEvent, ObservePointer);
        _pointerPresenter?.RemoveHandler(PointerPressedEvent, HandlePointerPress);
        _pointerPresenter?.RemoveHandler(KeyDownEvent, MenuKeyDown);
        _pointerPresenter = presenter;
        presenter?.AddHandler(PointerMovedEvent, ObservePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        presenter?.AddHandler(PointerPressedEvent, HandlePointerPress, RoutingStrategies.Tunnel, handledEventsToo: true);
        presenter?.AddHandler(KeyDownEvent, MenuKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    internal void Place()
    {
        if (_placing || Bounds.Width <= 0) { return; }
        _placing = true;
        try
        {
            List<Entry> available = _entries.Where(entry => entry.Control.IsVisible).ToList();
            foreach (Entry entry in available) { entry.Control.Measure(new Size(double.PositiveInfinity, 24)); }
            double width = Bounds.Width - Padding.Left - Padding.Right;
            bool overflow = available.Sum(entry => entry.Control.DesiredSize.Width + 1) > width;
            double used = 0;
            bool suffix = false;
            List<Control> popup = [];
            foreach (Entry entry in _entries)
            {
                if (!entry.Control.IsVisible) { entry.Host.IsVisible = false; continue; }
                double desired = entry.Control.DesiredSize.Width + 1;
                suffix |= used + desired > width - (overflow ? 25 : 0);
                entry.Host.IsVisible = !suffix;
                if (suffix) { popup.Add(entry.Popup); }
                else { used += desired; }
            }
            Overflow.IsVisible = popup.Count > 0;
            if (!_popupItems.SequenceEqual(popup))
            {
                _popupItems.Clear(); foreach (Control item in popup) { _popupItems.Add(item); }
            }
        }
        finally { _placing = false; }
    }

    private void ApplyPalette()
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(ActualThemeVariant == ThemeVariant.Dark);
        Background = Brush.Parse(colors["Control"]);
        MenuFlyoutPresenter? presenter = _flyout.Items.OfType<Control>()
            .SelectMany(item => item.GetVisualAncestors().OfType<MenuFlyoutPresenter>()).FirstOrDefault();
        if (presenter is null) { return; }
        presenter.Background = Brush.Parse(colors["Menu"]); presenter.Foreground = Brush.Parse(colors["MenuText"]);
        presenter.BorderBrush = Brush.Parse(colors["ActiveBorder"]);
        foreach ((string key, string color) in new Dictionary<string, string>
        {
            ["MenuFlyoutItemBackgroundPointerOver"] = "MenuHighlight", ["MenuFlyoutItemBackgroundPressed"] = "MenuHighlight",
            ["MenuFlyoutItemForegroundPointerOver"] = "HighlightText", ["MenuFlyoutItemForegroundPressed"] = "HighlightText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = "MenuText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = "HighlightText"
        }) { presenter.Resources[key] = Brush.Parse(colors[color]); }
    }
    private sealed record Entry(Control Control, Border Host, Control Popup);
}
