using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal static class ModalWindowActivation
{
    internal static void AfterMapping(Window window, Func<bool> canActivate)
    {
        // On X11 the first key can otherwise reach the disabled owner and only
        // activate its modal child, losing the user's actual input.
        Dispatcher.UIThread.Post(() =>
        {
            if (canActivate() && window.IsVisible && window.IsEffectivelyEnabled
                && !window.OwnedWindows.Any(child => child.IsVisible)) { window.Activate(); }
        }, DispatcherPriority.Loaded);
    }
}
