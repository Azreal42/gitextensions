using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop.Avatars;

/// <summary>Window-local admission and real close drainage. Unbound windows remain entirely dormant.</summary>
internal sealed class AvatarWindowLifetime
{
    private static readonly ConditionalWeakTable<Window, AvatarWindowLifetime> Windows = new();
    private readonly Window _window;
    private readonly HashSet<AvatarImageControl> _presenters = [];
    private readonly HashSet<Task> _work = [];
    private AvatarApplicationLifetime? _application;
    private AvatarOwnerScope? _scope;
    private Task? _close;
    private bool _mapped, _closing, _ownerManagesClose, _closeScheduled, _closeReady;

    private AvatarWindowLifetime(Window window)
    {
        _window = window;
        _mapped = false;
        if (window.IsVisible)
        { Dispatcher.UIThread.Post(() => { if (!_closing && window.IsVisible) { _mapped = true; NotifyChanged(); } }); }
        window.Opened += (_, _) => { _mapped = true; NotifyChanged(); };
        window.PropertyChanged += (_, change) =>
        {
            if (change.Property == Visual.IsVisibleProperty || change.Property == Window.WindowStateProperty)
            { NotifyChanged(); }
        };
        window.Closing += (_, args) =>
        {
            if (_ownerManagesClose || _closeReady) { return; }
            Task close = BeginClose();
            if (close.IsCompleted) { _closeReady = true; return; }
            args.Cancel = true;
            if (!_closeScheduled) { _closeScheduled = true; _ = CloseWhenReadyAsync(close); }
        };
        window.Closed += (_, _) => { _mapped = false; Unsubscribe(); };
    }

    internal static AvatarWindowLifetime For(Window window, bool ownerManagesClose = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        AvatarWindowLifetime result = Windows.GetValue(window, static owner => new(owner));
        result._ownerManagesClose |= ownerManagesClose;
        return result;
    }

    internal event Action? Changed;
    internal bool IsMapped => _mapped && _window.IsVisible && _window.WindowState != WindowState.Minimized && !_closing;
    internal bool IsClosing => _closing;
    private AvatarApplicationLifetime? BoundApplication => _application
        ?? (_window.Owner is Window owner ? For(owner).BoundApplication : null);
    internal bool HasBinding => BoundApplication is not null;
    internal Task Completion => Task.WhenAll(_work.ToArray());
    internal AvatarOwnerScope? Scope => TryGetScope();

    internal void Bind(AvatarApplicationLifetime application)
    {
        if (_closing) { throw new InvalidOperationException("The avatar window is closing."); }
        if (_application is not null && !ReferenceEquals(_application, application))
        { throw new InvalidOperationException("A window cannot change its application avatar runtime."); }
        if (_application is not null) { return; }
        _application = application;
        application.Service.Changed += PolicyChanged;
        NotifyChanged();
    }

    private AvatarOwnerScope? TryGetScope()
    {
        if (!IsMapped) { return null; }
        AvatarWindowLifetime? parent = _window.Owner is Window owner ? For(owner) : null;
        if (parent?._closing == true) { return null; }
        if (_application is null && parent?.BoundApplication is { } inherited) { Bind(inherited); }
        if (_application is null) { return null; }
        if (_scope is not null) { return _scope.IsActive ? _scope : null; }
        AvatarOwnerScope? parentScope = null;
        if (parent is not null)
        {
            parent.Bind(_application);
            parentScope = parent.TryGetScope();
            if (parentScope is null) { return null; }
        }
        // Owner is known after mapping and fixed before the first request is admitted.
        _scope = _application.Service.CreateOwnerScope(_window.Name ?? _window.GetType().Name, parentScope);
        return _scope;
    }

    private void PolicyChanged(AvatarPolicySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => { if (!_closing) { NotifyChanged(); } });
    }

    private void NotifyChanged()
    {
        if (!_closing) { Changed?.Invoke(); }
    }

    internal void Register(AvatarImageControl presenter) => _presenters.Add(presenter);
    internal void Unregister(AvatarImageControl presenter) => _presenters.Remove(presenter);

    internal void Track(Task work)
    {
        _work.Add(work);
        _ = RetireAsync(work);
        async Task RetireAsync(Task pending)
        {
            try { await pending; }
            catch { /* The presenter or operation owns its error; this ledger owns drainage. */ }
            finally { _work.Remove(pending); }
        }
    }

    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closing = true;
        Unsubscribe();
        Task scope = _scope?.BeginClose(includeDescendants: true).Completion ?? Task.CompletedTask;
        Task[] presenters = _presenters.ToArray().Select(presenter => presenter.BeginClose()).ToArray();
        Task[] children = _window.OwnedWindows.OfType<Window>()
            .Select(child => Windows.TryGetValue(child, out AvatarWindowLifetime? lifetime) ? lifetime.BeginClose() : Task.CompletedTask).ToArray();
        return _close = Task.WhenAll(presenters.Concat(children).Append(scope).Append(Completion));
    }

    private void Unsubscribe()
    {
        if (_application is not null) { _application.Service.Changed -= PolicyChanged; }
    }

    private async Task CloseWhenReadyAsync(Task pending)
    {
        try { await pending; }
        catch { /* The existing owner reports its own errors. Never abandon the close barrier. */ }
        finally
        {
            _closeReady = true;
            Dispatcher.UIThread.Post(() => { if (_window.IsVisible) { _window.Close(); } });
        }
    }
}
