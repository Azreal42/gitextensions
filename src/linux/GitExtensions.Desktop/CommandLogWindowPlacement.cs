using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace GitExtensions.Linux.Desktop;

/// <summary>Tracks only normal journal geometry; no journal controls or preferences are persisted here.</summary>
internal sealed class CommandLogWindowPlacement
{
    private readonly Window _window;
    private readonly CommandLogWindowPlacementState _state = CommandLogWindowPlacementState.Open();
    private CommandLogWindowPlacementValue? _normal;
    private bool _restored, _opened, _closed;

    internal CommandLogWindowPlacement(Window window)
    {
        _window = window;
        // Avalonia raises this direct event before initial layout and native Show.
        // Program has already loaded state on a worker; there is no I/O here.
        window.AddHandler(Window.WindowOpenedEvent, OnOpening);
        window.Opened += OnOpened;
        window.PositionChanged += OnPositionChanged;
        window.Resized += OnResized;
        window.ScalingChanged += OnScalingChanged;
    }

    private void OnOpening(object? sender, RoutedEventArgs args)
    {
        if (_restored) { return; }
        _restored = true;
        if (_state.Value is not { } saved) { return; }
        Screen[] screens = _window.Screens.All.Where(screen => screen.WorkingArea.Width > 0 && screen.WorkingArea.Height > 0).ToArray();
        if (screens.Length == 0) { return; }
        // Select using the original physical rectangle, before applying a DPI change.
        Screen screen = screens.FirstOrDefault(candidate => Intersects(candidate.WorkingArea, saved))
            ?? screens.FirstOrDefault(candidate => candidate.IsPrimary) ?? screens[0];
        double scale = screen.Scaling;
        if (!double.IsFinite(scale) || scale is < 0.25 or > 8) { return; }
        PixelRect area = screen.WorkingArea;
        double width = Math.Max(_window.MinWidth, Math.Min(saved.Width, area.Width / scale));
        double height = Math.Max(_window.MinHeight, Math.Min(saved.Height, area.Height / scale));
        double ratio = scale / saved.Scaling;
        int x = checked((int)Math.Round(saved.X * ratio)), y = checked((int)Math.Round(saved.Y * ratio));
        double physicalWidth = width * scale, physicalHeight = height * scale;
        if (!screens.Any(candidate => Visible(candidate.WorkingArea, x, y, physicalWidth, physicalHeight)))
        { x = area.X; y = area.Y; }
        _normal = new(x, y, width, height, scale, Maximized: false);
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Width = width; _window.Height = height;
        _window.Position = new PixelPoint(x, y);
        _window.WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private static bool Intersects(PixelRect area, CommandLogWindowPlacementValue value)
        => area.X < value.X + value.Width * value.Scaling && area.Right > value.X
            && area.Y < value.Y + value.Height * value.Scaling && area.Bottom > value.Y;

    private static bool Visible(PixelRect area, int x, int y, double width, double height)
    {
        // Retain a partial location only if the top/title edge remains reachable
        // and at least the Windows 10%-of-screen minimum is visible on both axes.
        double visibleWidth = Math.Min(area.Right, x + width) - Math.Max(area.X, x);
        double visibleHeight = Math.Min(area.Bottom, y + height) - Math.Max(area.Y, y);
        return y >= area.Y && y < area.Bottom && visibleWidth >= Math.Min(width, area.Width * 0.1)
            && visibleHeight >= Math.Min(height, area.Height * 0.1);
    }

    private void OnOpened(object? sender, EventArgs args) { _opened = true; CaptureNormal(); }
    private void OnPositionChanged(object? sender, PixelPointEventArgs args) => CaptureNormal();
    private void OnResized(object? sender, WindowResizedEventArgs args) => CaptureNormal();
    private void OnScalingChanged(object? sender, EventArgs args) => CaptureNormal();

    private void CaptureNormal()
    {
        if (!_opened || _closed || !_window.IsVisible || _window.WindowState != WindowState.Normal) { return; }
        // Keep an immutable snapshot at the actual normal notification. A later
        // Max/Min in this UI turn must not discard an already-notified move/size.
        // Window publishes the platform state synchronously; X11 coalesces
        // geometry notifications. Ignore those while the public state is non-normal.
        // Do not sample merely on returning to Normal or at Closing: the platform
        // may still expose the preceding maximized client rectangle at that time.
        PixelPoint position = _window.Position;
        Size size = _window.ClientSize;
        CommandLogWindowPlacementValue value = new(position.X, position.Y, size.Width, size.Height, _window.DesktopScaling, Maximized: false);
        if (value.IsValid) { _normal = value; }
    }

    internal void Closed()
    {
        if (_closed) { return; }
        _closed = true;
        _window.RemoveHandler(Window.WindowOpenedEvent, OnOpening);
        _window.Opened -= OnOpened;
        _window.PositionChanged -= OnPositionChanged;
        _window.Resized -= OnResized;
        _window.ScalingChanged -= OnScalingChanged;
        // The platform is disposed at Closed. Use the last normal snapshot, captured
        // on its notifications, and only the managed state. Minimized/fullscreen normalize.
        if (_opened && _normal is { } normal)
        { _state.Save(normal with { Maximized = _window.WindowState == WindowState.Maximized }); }
    }
}
