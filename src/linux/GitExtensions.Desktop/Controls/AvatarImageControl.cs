using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Avatars;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

internal enum AvatarImageRole { Graph, CommitInfo, Blame }
internal enum AvatarPlaceholder { Initials, User80 }

/// <summary>One image presenter for graph, headers and blame. Render never initiates optional work.</summary>
internal sealed class AvatarImageControl : Control
{
    private readonly HashSet<Task> _reads = [];
    private readonly IGourceAvatarProvider? _injected;
    private readonly AvatarPlaceholder _placeholder;
    private AvatarWindowLifetime? _owner;
    private CancellationTokenSource? _request;
    private Bitmap? _image, _userImage;
    private string _email = "", _name = "", _identity = "", _requestKey = "";
    private Rect _viewport;
    private bool _hasViewport, _closing, _active = true;
    private long _version;

    internal AvatarImageRole Role { get; }
    internal Task Completion => Task.WhenAll(_reads.ToArray());
    internal event Action<Task>? ReadStarted;
    internal event Action? ImageChanged;
    internal event Action? AvatarMenuRequested;

    internal AvatarImageControl(string email = "", string? name = null, AvatarImageRole role = AvatarImageRole.Graph,
        AvatarPlaceholder placeholder = AvatarPlaceholder.Initials, IGourceAvatarProvider? injected = null)
    {
        Focusable = false;
        Role = role; _placeholder = placeholder; _injected = injected;
        SetIdentity(email, name);
        EnsurePlaceholder();
        AttachedToVisualTree += (_, _) => AttachOwner();
        DetachedFromVisualTree += (_, _) => DetachOwner();
        EffectiveViewportChanged += (_, args) => { _hasViewport = true; _viewport = args.EffectiveViewport; RefreshRequest(); };
        LayoutUpdated += (_, _) => RefreshRequest();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == BoundsProperty || change.Property == IsVisibleProperty) { RefreshRequest(); }
        };
        PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsRightButtonPressed && AvatarMenuRequested is { } showMenu)
            { args.Handled = true; showMenu(); }
        };
    }

    internal void SetIdentity(string? email, string? name, string? identity = null)
    {
        string nextEmail = email ?? "", nextName = name ?? "", nextIdentity = identity ?? nextEmail + "\0" + nextName;
        if (_email == nextEmail && _name == nextName && _identity == nextIdentity) { return; }
        _email = nextEmail; _name = nextName; _identity = nextIdentity;
        ToolTip.SetTip(this, _name + " <" + _email + ">");
        CancelRequest(clearImage: true); RefreshRequest(); InvalidateVisual();
    }

    internal void SetActive(bool active)
    {
        if (_active == active) { return; }
        _active = active;
        if (!active) { CancelRequest(clearImage: false); }
        RefreshRequest();
    }

    private void AttachOwner()
    {
        if (_closing || TopLevel.GetTopLevel(this) is not Window window) { return; }
        EnsurePlaceholder();
        _owner = AvatarWindowLifetime.For(window);
        _owner.Register(this); _owner.Changed += OwnerChanged;
        _hasViewport = false;
        OwnerChanged();
    }

    private void DetachOwner()
    {
        CancelRequest(clearImage: true); _hasViewport = false;
        if (_owner is not null)
        { _owner.Changed -= OwnerChanged; _owner.Unregister(this); _owner = null; }
        _userImage?.Dispose(); _userImage = null;
    }

    private void OwnerChanged()
    {
        if (_owner?.Scope is { } scope && Role == AvatarImageRole.CommitInfo)
        { IsVisible = scope.Service.Snapshot.Preferences.ShowCommitInfo; }
        RefreshRequest();
    }

    private bool IsDrawable => !_closing && _active && _owner?.IsMapped == true && IsEffectivelyVisible
        && Bounds.Width > 0 && Bounds.Height > 0 && _hasViewport
        && _viewport.Intersects(new Rect(Bounds.Size));

    private void RefreshRequest()
    {
        if (!IsDrawable || _owner?.Scope is not { } scope || !scope.IsActive)
        { CancelRequest(clearImage: false); return; }
        AvatarPolicySnapshot policy = scope.Service.Snapshot;
        if (!policy.IsReady || policy.IsStopping || Role == AvatarImageRole.CommitInfo && !policy.Preferences.ShowCommitInfo)
        { CancelRequest(clearImage: true); return; }
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int pixels = Math.Clamp((int)Math.Ceiling(Math.Min(Bounds.Width, Bounds.Height) * scaling), 1, 512);
        AvatarRenderIdentity render = AvatarRenderIdentity.Capture();
        string key = string.Join("\0", _identity, _email, _name, pixels, render, policy.Epoch);
        if (_requestKey == key) { return; }
        CancelRequest(clearImage: true); _requestKey = key;
        CancellationTokenSource request = new(); _request = request;
        long version = _version;
        Task read = ReadAsync(scope, render, pixels, policy.Epoch, version, request);
        _reads.Add(read); _owner.Track(read); ReadStarted?.Invoke(read);
        _ = RetireAsync(read);
        async Task RetireAsync(Task pending)
        { try { await pending; } catch { } finally { _reads.Remove(pending); } }
    }

    private async Task ReadAsync(AvatarOwnerScope scope, AvatarRenderIdentity render, int pixels, long epoch,
        long version, CancellationTokenSource request)
    {
        CancellationToken token = request.Token;
        Bitmap? decoded = null;
        try
        {
            AvatarPng? png;
            if (_injected is null)
            {
                AvatarReadResult result = await scope.ReadAsync(new(_email, _name, pixels, render), token);
                png = result.Outcome != AvatarReadOutcome.GenerationRetired && result.Epoch == epoch ? result.Image : null;
            }
            else
            {
                byte[]? bytes = await AvatarProviderBinding.ForBorrowed(scope, _injected)
                    .GetAvatarPngAsync(_email, _name, pixels, token);
                png = bytes is null ? null : new AvatarPng(bytes, epoch);
            }
            if (png is null || !Current()) { return; }
            using (Stream stream = png.OpenRead()) { decoded = new(stream); }
            if (!Current()) { return; }
            Bitmap? previous = _image; _image = decoded; decoded = null;
            previous?.Dispose(); InvalidateVisual(); ImageChanged?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || !scope.IsActive) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or HttpRequestException)
        { /* Optional avatars keep their local placeholder and never report a failed Git read. */ }
        finally
        {
            decoded?.Dispose();
            if (ReferenceEquals(_request, request)) { _request = null; }
            request.Dispose();
        }
        bool Current() => !token.IsCancellationRequested && !_closing && version == _version && IsDrawable
            && ReferenceEquals(_owner?.Scope, scope) && scope.IsActive && scope.Service.Snapshot.Epoch == epoch;
    }

    private void CancelRequest(bool clearImage)
    {
        if (_request is not null) { _request.Cancel(); _request = null; }
        _version++; _requestKey = "";
        if (clearImage) { _image?.Dispose(); _image = null; InvalidateVisual(); }
    }

    internal Task BeginClose()
    {
        if (!_closing) { _closing = true; DetachOwner(); }
        return Completion;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) { return; }
        if (_image is { } image) { context.DrawImage(image, new Rect(image.Size), new Rect(Bounds.Size)); return; }
        if (_placeholder == AvatarPlaceholder.User80)
        {
            if (_userImage is { } user) { context.DrawImage(user, new Rect(user.Size), new Rect(Bounds.Size)); }
            return;
        }
        string[] palette = ["SlateGray", "RoyalBlue", "Purple", "OrangeRed", "Teal", "OliveDrab"];
        string initials = GourceAvatarProtocol.Initials(_email, _name);
        int index = string.IsNullOrWhiteSpace(_email) && string.IsNullOrWhiteSpace(_name) ? 0 : GourceAvatarProtocol.InitialsColorIndex(_email, palette.Length);
        Color background = Color.Parse(palette[index]);
        IBrush foreground = Luminance(background) > .185 ? Brushes.Black : Brushes.White;
        context.FillRectangle(new SolidColorBrush(background), new Rect(Bounds.Size));
        Typeface face = new(DesktopFonts.For(DesktopFontRole.Application).Family);
        FormattedText estimate = new(initials, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 20, foreground);
        double ratio = Math.Min(Bounds.Width, Bounds.Height) / Math.Max(estimate.WidthIncludingTrailingWhitespace, estimate.Height);
        FormattedText text = new(initials, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 20 * ratio, foreground);
        context.DrawText(text, new Point(Math.Max(0, (Bounds.Width - text.WidthIncludingTrailingWhitespace) / 2), Math.Max(0, (Bounds.Height - text.Height) / 2)));
    }

    private void EnsurePlaceholder()
    {
        if (_placeholder != AvatarPlaceholder.User80 || _userImage is not null) { return; }
        Uri resource = new("avares://GitExtensions/Assets/Icons/User80.png");
        if (AssetLoader.Exists(resource)) { using Stream stream = AssetLoader.Open(resource); _userImage = new(stream); }
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
