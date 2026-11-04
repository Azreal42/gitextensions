using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal static class ModalWindowActivation
{
    private static IDisposable? _openingSubscription;

    internal static void Initialize()
    {
        _openingSubscription ??= Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (!window.IsDialog) { return; }
            // The routed event precedes Owner assignment; Opened runs after it.
            window.Opened -= OnModalOpened;
            window.Opened += OnModalOpened;
        });
    }

    private static void OnModalOpened(object? sender, EventArgs args)
    {
        Window window = (Window)sender!;
        window.Opened -= OnModalOpened;
        if (!window.IsDialog || window.Owner is not Window owner) { return; }
        bool canMinimize = window.CanMinimize;
        window.CanMinimize = false;
        KeepVisibleWithOwner(window, owner, canMinimize);
    }

    private static void KeepVisibleWithOwner(Window window, Window owner, bool canMinimize)
    {
        List<Window> owners = [];
        for (Window? ancestor = owner; ancestor is not null; ancestor = ancestor.Owner as Window) { owners.Add(ancestor); }
        WindowState restoreState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        bool detached = false;
        void RestoreIfNeeded()
        {
            if (window.WindowState != WindowState.Minimized) { restoreState = window.WindowState; return; }
            // Some X11 window managers ignore CanMinimize. Do not leave a visible,
            // disabled owner behind; minimizing the whole application is allowed.
            Dispatcher.UIThread.Post(() =>
            {
                if (!detached && window.IsVisible && window.IsDialog && ReferenceEquals(window.Owner, owner)
                    && owners.All(ancestor => ancestor.IsVisible && ancestor.WindowState != WindowState.Minimized)
                    && window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = restoreState;
                    Window target = window;
                    while (target.OwnedWindows.LastOrDefault(child => child.IsDialog && child.IsVisible) is Window child) { target = child; }
                    if (target.WindowState != WindowState.Minimized) { target.Activate(); }
                }
            }, DispatcherPriority.Loaded);
        }
        void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (ReferenceEquals(sender, window) && args.Property == Window.IsVisibleProperty && !window.IsVisible) { Detach(); }
            else if (args.Property == Window.WindowStateProperty) { RestoreIfNeeded(); }
        }
        void OnClosed(object? sender, EventArgs args) => Detach();
        void Detach()
        {
            if (detached) { return; }
            detached = true;
            window.PropertyChanged -= OnPropertyChanged;
            foreach (Window ancestor in owners) { ancestor.PropertyChanged -= OnPropertyChanged; }
            window.Closed -= OnClosed;
            window.CanMinimize = canMinimize;
        }
        window.PropertyChanged += OnPropertyChanged;
        foreach (Window ancestor in owners) { ancestor.PropertyChanged += OnPropertyChanged; }
        window.Closed += OnClosed;
        RestoreIfNeeded();
    }

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
