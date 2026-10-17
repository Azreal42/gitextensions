using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GitExtensions.Linux.Plugins;
using SkiaSharp;

namespace GitExtensions.Linux.Desktop;

/// <summary>Original GitHub → Gravatar → author-initials chain, using the existing native image codec.</summary>
public sealed class GourceAvatarProvider(HttpClient? client = null, string? cacheDirectory = null) : IGourceAvatarProvider
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Downloads = new(10);
    private readonly HttpClient _client = client ?? DefaultClient;
    private readonly string _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
    private readonly Dictionary<string, (byte[] Png, DateTime Expires)> _memory = [];
    private readonly LinkedList<string> _recent = [];
    private readonly object _gate = new();
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int MaximumImageDimension = 2048;

    public async Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DesktopFontSession fonts = DesktopFonts.Active;
        string key = GourceAvatarProtocol.CacheKey(email, authorName, imageSize);
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var image) && image.Expires > DateTime.UtcNow)
            { _recent.Remove(key); _recent.AddFirst(key); return image.Png.ToArray(); }
        }
        string path = Path.Combine(_cacheDirectory, key + ".png");
        byte[]? png = await ReadCacheAsync(path, imageSize, cancellationToken).ConfigureAwait(false);
        bool downloaded = png is null;
        if (png is null)
        {
            await Downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Uri github = GourceAvatarProtocol.GitHubAvatar(email, imageSize);
                string? username = GourceAvatarProtocol.GitHubUsername(email);
                if (username?.Contains('[') == true)
                {
                    byte[]? profile = await DownloadAsync(GourceAvatarProtocol.GitHubProfile(username), 64 * 1024, cancellationToken).ConfigureAwait(false);
                    github = ProfileAvatar(profile, imageSize) ?? github;
                }
                png = await DownloadPngAsync(github, imageSize, rejectGitHubIdenticon: true, cancellationToken).ConfigureAwait(false)
                    ?? await DownloadPngAsync(GourceAvatarProtocol.Gravatar(email, imageSize), imageSize, rejectGitHubIdenticon: false, cancellationToken).ConfigureAwait(false);
            }
            finally { Downloads.Release(); }
        }
        bool remoteImage = png is not null;
        png ??= await Dispatcher.UIThread.InvokeAsync(() => InitialsPng(email, authorName, imageSize, cancellationToken, fonts));
        cancellationToken.ThrowIfCancellationRequested();
        if (downloaded && remoteImage) await WriteCacheAsync(path, png, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _memory[key] = (png.ToArray(), DateTime.UtcNow.AddDays(30));
            _recent.Remove(key); _recent.AddFirst(key);
            while (_recent.Count > 30) { string oldest = _recent.Last!.Value; _recent.RemoveLast(); _memory.Remove(oldest); }
        }
        return png;
    }

    private async Task<byte[]?> DownloadPngAsync(Uri uri, int size, bool rejectGitHubIdenticon, CancellationToken token)
    {
        byte[]? content = await DownloadAsync(uri, MaximumResponseBytes, token).ConfigureAwait(false);
        return content is null ? null : ConvertPng(content, size, rejectGitHubIdenticon, token);
    }

    private async Task<byte[]?> DownloadAsync(Uri uri, int limit, CancellationToken token)
    {
        if (uri.Scheme is not ("http" or "https")) return null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("GitExtensions-Linux/8.0");
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit) return null;
            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using MemoryStream output = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > limit) return null;
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { return null; }
    }

    private static byte[]? ConvertPng(byte[] content, int size, bool rejectGitHubIdenticon, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using SKData data = SKData.CreateCopy(content);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width is < 1 or > MaximumImageDimension || codec.Info.Height is < 1 or > MaximumImageDimension
                || (rejectGitHubIdenticon && size != 420 && codec.Info.Width == 420)) return null;
            using MemoryStream input = new(content, writable: false);
            using Bitmap bitmap = new(input);
            token.ThrowIfCancellationRequested();
            using Bitmap scaled = bitmap.CreateScaledBitmap(new PixelSize(size, size), BitmapInterpolationMode.HighQuality);
            using MemoryStream output = new();
            scaled.Save(output);
            token.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException) { return null; }
    }

    private static Uri? ProfileAvatar(byte[]? content, int size)
    {
        if (content is null) return null;
        try
        {
            using JsonDocument profile = JsonDocument.Parse(content);
            if (profile.RootElement.TryGetProperty("avatar_url", out JsonElement property)
                && property.ValueKind == JsonValueKind.String && Uri.TryCreate(property.GetString(), UriKind.Absolute, out Uri? uri)
                && uri.Scheme is "http" or "https")
            {
                UriBuilder builder = new(uri);
                builder.Query = builder.Query.TrimStart('?') + (builder.Query.Length > 0 ? "&" : "") + "s=" + size.ToString(CultureInfo.InvariantCulture);
                return builder.Uri;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static byte[] InitialsPng(string email, string name, int size, CancellationToken token, DesktopFontSession fonts)
    {
        token.ThrowIfCancellationRequested();
        string[] palette = ["SlateGray", "RoyalBlue", "Purple", "OrangeRed", "Teal", "OliveDrab"];
        Color background = Color.Parse(palette[GourceAvatarProtocol.InitialsColorIndex(email, palette.Length)]);
        static double Linear(byte value) { double c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        IBrush foreground = .2126 * Linear(background.R) + .7152 * Linear(background.G) + .0722 * Linear(background.B) > .185 ? Brushes.Black : Brushes.White;
        string initials = GourceAvatarProtocol.Initials(email, name);
        Typeface face = new(fonts.For(DesktopFontRole.Application).Family);
        FormattedText estimate = new(initials, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 20, foreground);
        double ratio = size / Math.Max(estimate.WidthIncludingTrailingWhitespace, estimate.Height);
        FormattedText text = new(initials, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 20 * ratio, foreground);
        using RenderTargetBitmap bitmap = new(new PixelSize(size, size), new Vector(96, 96));
        using (DrawingContext drawing = bitmap.CreateDrawingContext())
        {
            drawing.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, size, size));
            drawing.DrawText(text, new Point(Math.Max(0, (size - text.WidthIncludingTrailingWhitespace) / 2), Math.Max(0, (size - text.Height) / 2)));
        }
        using MemoryStream output = new();
        bitmap.Save(output); token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static async Task<byte[]?> ReadCacheAsync(string path, int size, CancellationToken token)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > MaximumResponseBytes || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30)) return null;
            return ConvertPng(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), size, false, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteCacheAsync(string path, byte[] png, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, png, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    private static string DefaultCacheDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string root = Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(root, "gitextensions", "avatars");
    }
}
