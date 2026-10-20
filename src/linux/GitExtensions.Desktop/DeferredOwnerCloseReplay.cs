using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>Retains a real owner-close request while this window's own handlers defer its closure.</summary>
internal sealed class DeferredOwnerCloseReplay
{
    private Window? _owner;

    internal DeferredOwnerCloseReplay(Window window)
    {
        // The window's delegates own this passive instance until Closed. No global registration.
        window.Closing += Closing;
        window.Closed += Closed;
    }

    private void Closing(object? sender, WindowClosingEventArgs args)
    {
        // A derived asynchronous flush can veto even when the base is not busy.
        if (args.CloseReason == WindowCloseReason.OwnerWindowClosing && sender is Window window)
        { _owner ??= window.Owner as Window; }
    }

    private void Closed(object? sender, EventArgs args)
    {
        if (sender is Window window)
        { window.Closing -= Closing; window.Closed -= Closed; }
        Window? owner = _owner; _owner = null;
        // Only a completed close replays once. The base/derived owner still owns
        // cancellation, drainage and any further veto; this never forces closure.
        if (owner is not null)
        { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
    }
}
