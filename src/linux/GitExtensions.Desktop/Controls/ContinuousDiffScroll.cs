using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

/// <summary>Opt-in FileViewer overscroll intent. The host still owns file selection and all Git reads.</summary>
internal sealed class ContinuousDiffScroll : IDisposable
{
    private readonly FileContentView _content;
    private readonly Func<bool> _available;
    private readonly Func<bool, Task> _navigate;
    private readonly Action<Exception> _report;
    private readonly DiffViewerPreferencesStore _preferences = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long? _last;
    private bool _disposed;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal ContinuousDiffScroll(FileContentView content, Func<bool> available, Func<bool, Task> navigate, Action<Exception> report)
    {
        _content = content; _available = available; _navigate = navigate; _report = report;
        content.AddHandler(InputElement.PointerWheelChangedEvent, Wheel, RoutingStrategies.Tunnel);
    }

    private void Wheel(object? sender, PointerWheelEventArgs args)
    {
        if (_disposed || args.Handled || args.Delta.Y == 0 || !Completion.IsCompleted || !_available()
            || (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0) { return; }
        Visual? source = args.Source as Visual;
        bool image = _content.IsImage && _content.Content is Image picture && source?.GetSelfAndVisualAncestors().Contains(picture) == true;
        if (!image && (source?.GetSelfAndVisualAncestors().Contains(_content.TextView.Editor) != true || !_content.TextView.IsDocumentReady)) { return; }
        DiffViewerPreferences preferences = _preferences.Load();
        if (args.KeyModifiers != KeyModifiers.Alt && !preferences.AutomaticContinuousScroll) { return; }
        bool forward = args.Delta.Y < 0;
        var editor = _content.TextView.Editor;
        bool boundary = image || (forward ? editor.VerticalOffset + editor.ViewportHeight >= editor.ExtentHeight - .5 : editor.VerticalOffset <= .5);
        long now = _clock.ElapsedMilliseconds;
        if (!boundary || _last is long last && now - last < preferences.AutomaticContinuousScrollDelay) { return; }
        _last = now; args.Handled = true; Completion = NavigateAsync(forward);
    }

    private async Task NavigateAsync(bool forward)
    { try { await _navigate(forward); } catch (OperationCanceledException) { } catch (Exception error) { if (!_disposed) { _report(error); } } }

    public void Dispose()
    { _disposed = true; _content.RemoveHandler(InputElement.PointerWheelChangedEvent, Wheel); }
}
