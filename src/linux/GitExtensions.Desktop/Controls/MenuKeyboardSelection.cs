using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Keep a keyboard-opened menu selected while it maps beneath a stationary pointer.</summary>
internal sealed class MenuKeyboardSelection : IDisposable
{
    private readonly Window _owner;
    private readonly MenuItem _root;
    private PixelPoint? _lastPointerScreen, _pointerAtOpen;
    private MenuItem? _pendingItem;
    private RoutedEvent? _pendingEvent;
    private bool _disposed;
    internal bool IsActive { get; private set; }

    internal MenuKeyboardSelection(Window owner, MenuItem root)
    {
        _owner = owner; _root = root;
        owner.AddHandler(InputElement.PointerMovedEvent, ObservePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, ObservePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(MenuItem.PointerEnteredItemEvent, SuspendHover);
        root.AddHandler(MenuItem.PointerExitedItemEvent, SuspendHover);
        root.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.PropertyChanged += RootPropertyChanged; owner.Closed += OwnerClosed;
    }
    internal void Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsActive = true; _pointerAtOpen = _lastPointerScreen; _pendingItem = null; _pendingEvent = null;
    }
    private void SuspendHover(object? sender, RoutedEventArgs args)
    {
        if (!IsActive || args.Source is not MenuItem item) { return; }
        _pendingItem = item; _pendingEvent = args.RoutedEvent; args.Handled = true;
    }
    private void ObservePointer(object? sender, PointerEventArgs args)
    {
        if (args.Source is not Visual source || TopLevel.GetTopLevel(source) is null) { return; }
        PixelPoint point = source.PointToScreen(args.GetPosition(source)); _lastPointerScreen = point;
        if (!IsActive) { return; }
        _pointerAtOpen ??= point;
        if (point == _pointerAtOpen) { return; }
        MenuItem? item = _pendingItem; RoutedEvent? hover = _pendingEvent;
        End();
        // As in CompactBrowserToolbar: PointerOverPreProcessor can precede PointerMoved.
        // Release the one deferred hover event on the first real move, never a command or key.
        if (item is not null && hover is not null) { item.RaiseEvent(new RoutedEventArgs(hover) { Source = item }); }
    }
    private void PointerPressed(object? sender, PointerPressedEventArgs args) => End();
    private void KeyDown(object? sender, KeyEventArgs args) { if (args.Key == Key.Escape) { End(); } }
    private void RootPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    { if (args.Property == MenuItem.IsSubMenuOpenProperty && !_root.IsSubMenuOpen) { End(); } }
    private void OwnerClosed(object? sender, EventArgs args) => Dispose();
    private void End() { IsActive = false; _pointerAtOpen = null; _pendingItem = null; _pendingEvent = null; }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; End();
        _owner.RemoveHandler(InputElement.PointerMovedEvent, ObservePointer);
        _owner.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
        _root.RemoveHandler(InputElement.PointerMovedEvent, ObservePointer);
        _root.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
        _root.RemoveHandler(MenuItem.PointerEnteredItemEvent, SuspendHover);
        _root.RemoveHandler(MenuItem.PointerExitedItemEvent, SuspendHover);
        _root.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
        _root.PropertyChanged -= RootPropertyChanged; _owner.Closed -= OwnerClosed;
    }
}
