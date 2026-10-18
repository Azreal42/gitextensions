using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GitExtensions.Linux.Plugins;
using SkiaSharp;

namespace GitExtensions.Linux.Desktop.Avatars;

internal interface IAvatarImageCodec
{
    AvatarPng? Decode(byte[] content, int pixels, bool rejectGitHubIdenticon, CancellationToken cancellationToken);
}

internal sealed class AvatarImageCodec : IAvatarImageCodec
{
    internal const int MaximumImageDimension = 2048;
    public AvatarPng? Decode(byte[] content, int pixels, bool rejectGitHubIdenticon, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Length > AvatarImageTransport.MaximumResponseBytes) { return null; }
        try
        {
            using SKData data = SKData.CreateCopy(content);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width is < 1 or > MaximumImageDimension
                || codec.Info.Height is < 1 or > MaximumImageDimension
                || (rejectGitHubIdenticon && pixels != 420 && codec.Info.Width == 420)) { return null; }
            using MemoryStream input = new(content, writable: false);
            using Bitmap bitmap = new(input);
            cancellationToken.ThrowIfCancellationRequested();
            using Bitmap scaled = bitmap.CreateScaledBitmap(new PixelSize(pixels, pixels), BitmapInterpolationMode.HighQuality);
            using MemoryStream output = new(); scaled.Save(output);
            cancellationToken.ThrowIfCancellationRequested(); return new(output.ToArray());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { return null; }
    }

    internal static async Task<AvatarPng?> RenderInitialsAsync(AvatarRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Cancellation belongs to the actual queued dispatcher operation as well as its callback.
        return await Dispatcher.UIThread.InvokeAsync(() => RenderInitials(request, cancellationToken),
            DispatcherPriority.Background, cancellationToken);
    }

    internal static Task<AvatarPng?> RenderUserFallbackAsync(AvatarRequest request, IAvatarImageCodec codec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Stream input = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/User80.png"));
        using MemoryStream output = new(); input.CopyTo(output);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(codec.Decode(output.ToArray(), request.PixelSize, false, token));
    }

    private static AvatarPng RenderInitials(AvatarRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CultureInfo culture = CultureInfo.GetCultureInfo(request.RenderIdentity.CultureName);
        string[] palette = ["SlateGray", "RoyalBlue", "Purple", "OrangeRed", "Teal", "OliveDrab"];
        Color color = Color.Parse(palette[GourceAvatarProtocol.InitialsColorIndex(request.Email, palette.Length)]);
        static double Linear(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        IBrush foreground = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B) > .185 ? Brushes.Black : Brushes.White;
        string initials = AvatarProviderProtocol.Initials(request.Email, request.Name, culture);
        Typeface face = new(new FontFamily(request.RenderIdentity.FontFamily), request.RenderIdentity.Style, request.RenderIdentity.Weight);
        FormattedText estimate = new(initials, culture, FlowDirection.LeftToRight, face, 20, foreground);
        double ratio = request.PixelSize / Math.Max(estimate.WidthIncludingTrailingWhitespace, estimate.Height);
        FormattedText text = new(initials, culture, FlowDirection.LeftToRight, face, 20 * ratio, foreground);
        using RenderTargetBitmap bitmap = new(new PixelSize(request.PixelSize, request.PixelSize), new Vector(96, 96));
        using (DrawingContext drawing = bitmap.CreateDrawingContext())
        {
            drawing.FillRectangle(new SolidColorBrush(color), new Rect(0, 0, request.PixelSize, request.PixelSize));
            drawing.DrawText(text, new Point(Math.Max(0, (request.PixelSize - text.WidthIncludingTrailingWhitespace) / 2),
                Math.Max(0, (request.PixelSize - text.Height) / 2)));
        }
        using MemoryStream output = new(); bitmap.Save(output); token.ThrowIfCancellationRequested();
        return new(output.ToArray());
    }
}
