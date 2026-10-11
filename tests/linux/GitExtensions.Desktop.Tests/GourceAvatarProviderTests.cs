using System.Collections.Concurrent;
using System.Net;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using SkiaSharp;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class GourceAvatarProviderTests
{
    [AvaloniaTest]
    public async Task JpegBecomesNativePngAndUsesOriginalMemoryAndThirtyDayFileCaches()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using AvatarHttpHandler handler = new(_ => ImageResponse(65, 45, jpeg: true));
        using HttpClient client = new(handler);
        string cache = Path.Combine(fixture.Root, "avatars");
        GourceAvatarProvider first = new(client, cache);
        byte[] png = (await first.GetAvatarPngAsync(" avatar@example.invalid ", "Avatar Tester", 90, default))!;
        AssertPng(png, 90); Assert.That(handler.Requests.Count, Is.EqualTo(1));
        png[0] = 0; // Callers must not be able to corrupt the memory cache.
        AssertPng((await first.GetAvatarPngAsync("avatar@example.invalid", "Avatar Tester", 90, default))!, 90);
        Assert.That(handler.Requests.Count, Is.EqualTo(1));
        GourceAvatarProvider disk = new(client, cache);
        AssertPng((await disk.GetAvatarPngAsync("avatar@example.invalid", "Avatar Tester", 90, default))!, 90);
        Assert.That(handler.Requests.Count, Is.EqualTo(1));
        string file = Directory.GetFiles(cache).Single();
        Assert.That(Path.GetFileName(file), Does.Not.Contain("avatar@example"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-31));
        GourceAvatarProvider expired = new(client, cache);
        AssertPng((await expired.GetAvatarPngAsync("avatar@example.invalid", "Avatar Tester", 90, default))!, 90);
        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(Directory.GetFiles(cache, "*.tmp"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task GitHubNoReplyAndBotProfileRequestsRetainTheOriginalProviderProtocol()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using AvatarHttpHandler handler = new(uri => uri.Host == "api.github.com"
            ? new(HttpStatusCode.OK) { Content = new StringContent("{\"avatar_url\":\"https://avatars.githubusercontent.com/bot.png?v=4\"}") }
            : ImageResponse(90, 90));
        using HttpClient client = new(handler);
        GourceAvatarProvider provider = new(client, Path.Combine(fixture.Root, "avatars"));
        AssertPng((await provider.GetAvatarPngAsync("10408412+Azreal42@users.noreply.github.com", "Az42", 90, default))!, 90);
        Assert.That(handler.Requests.First().AbsoluteUri, Is.EqualTo("https://avatars.githubusercontent.com/Azreal42?s=90"));
        AssertPng((await provider.GetAvatarPngAsync("123+dependabot[bot]@users.noreply.github.com", "Bot", 90, default))!, 90);
        Assert.That(handler.Requests.ElementAt(1).AbsoluteUri, Is.EqualTo("https://api.github.com/users/dependabot%5Bbot%5D"));
        Assert.That(handler.Requests.ElementAt(2).Query, Is.EqualTo("?v=4&s=90"));
        Assert.That(handler.AuthorizationHeaders, Is.All.Null);
        Assert.That(handler.UserAgents, Is.All.EqualTo("GitExtensions-Linux/8.0"));
    }

    [AvaloniaTest]
    public async Task GitHub420PixelIdenticonFallsThroughToCurrentOfficialGravatarHash()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using AvatarHttpHandler handler = new(uri => uri.Host.Contains("githubusercontent", StringComparison.Ordinal) ? ImageResponse(420, 420) : ImageResponse(30, 30));
        using HttpClient client = new(handler);
        GourceAvatarProvider provider = new(client, Path.Combine(fixture.Root, "avatars"));
        AssertPng((await provider.GetAvatarPngAsync("MyEmailAddress@example.com ", "Avatar", 90, default))!, 90);
        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(handler.Requests.Last().AbsoluteUri, Is.EqualTo("https://www.gravatar.com/avatar/84059b07d4be67b806386c0aad8070a23f18836bbaae342275dc0a83414c32ee?r=g&d=404&s=90"));
    }

    [AvaloniaTest]
    public async Task MissingImagesRenderOriginalDeterministicInitialsAndPaletteWithoutSavingRemoteFailures()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using AvatarHttpHandler handler = new(_ => new(HttpStatusCode.NotFound));
        using HttpClient client = new(handler);
        string cache = Path.Combine(fixture.Root, "avatars");
        GourceAvatarProvider provider = new(client, cache);
        byte[] png = (await provider.GetAvatarPngAsync("emile@example.invalid", "Émile Zola", 90, default))!;
        AssertPng(png, 90);
        using SKBitmap bitmap = SKBitmap.Decode(png);
        SKColor[] colors = [SKColors.SlateGray, SKColors.RoyalBlue, SKColors.Purple, SKColors.OrangeRed, SKColors.Teal, SKColors.OliveDrab];
        Assert.That(bitmap.GetPixel(0, 0), Is.EqualTo(colors[GourceAvatarProtocol.InitialsColorIndex("emile@example.invalid", colors.Length)]));
        Assert.That(bitmap.Pixels.Distinct().Count(), Is.GreaterThan(2), "Actual native initials glyphs must be rendered, not an empty placeholder.");
        Assert.That(GourceAvatarProtocol.Initials("emile.zola@example.invalid", null), Is.EqualTo("EZ"));
        Assert.That(GourceAvatarProtocol.Initials("unused", "GitExtensions"), Is.EqualTo("GE"));
        Assert.That(GourceAvatarProtocol.Initials("unused", "Émile Zola"), Is.EqualTo("ÉZ"));
        Assert.That(Directory.Exists(cache), Is.False);
    }

    [AvaloniaTest]
    public async Task HttpSizeAndImageDimensionsAreBoundedBeforeNativePixelDecoding()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using AvatarHttpHandler handler = new(uri =>
        {
            if (uri.Host == "www.gravatar.com") return new(HttpStatusCode.NotFound);
            if (uri.Query.Contains("oversized", StringComparison.Ordinal))
            {
                HttpResponseMessage response = ImageResponse(10, 10);
                response.Content.Headers.ContentLength = GourceAvatarProvider.MaximumResponseBytes + 1;
                return response;
            }
            return ImageResponse(GourceAvatarProvider.MaximumImageDimension + 1, 1);
        });
        using HttpClient client = new(handler);
        GourceAvatarProvider provider = new(client, Path.Combine(fixture.Root, "avatars"));
        AssertPng((await provider.GetAvatarPngAsync("oversized@example.invalid", "Oversized", 90, default))!, 90);
        AssertPng((await provider.GetAvatarPngAsync("dimensions@example.invalid", "Dimensions", 90, default))!, 90);
        Assert.That(handler.Requests.Count, Is.EqualTo(4));
    }

    [AvaloniaTest]
    public async Task CallerCancellationStopsHttpAndLeavesNoPartialCacheOrFallbackImage()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AvatarHttpHandler handler = new(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using HttpClient client = new(handler);
        string cache = Path.Combine(fixture.Root, "avatars");
        GourceAvatarProvider provider = new(client, cache);
        using CancellationTokenSource cancellation = new();
        Task<byte[]?> request = provider.GetAvatarPngAsync("cancel@example.invalid", "Cancel", 90, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        OperationCanceledException? cancelled = null;
        try { await request; } catch (OperationCanceledException exception) { cancelled = exception; }
        Assert.That(cancelled, Is.Not.Null);
        Assert.That(handler.Requests.Count, Is.EqualTo(1));
        Assert.That(Directory.Exists(cache), Is.False);
        cancelled = null;
        try { await provider.GetAvatarPngAsync("cancel@example.invalid", "Cancel", 90, cancellation.Token); }
        catch (OperationCanceledException exception) { cancelled = exception; }
        Assert.That(cancelled, Is.Not.Null);
        Assert.That(handler.Requests.Count, Is.EqualTo(1));
    }

    private static HttpResponseMessage ImageResponse(int width, int height, bool jpeg = false)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(AvatarHttpHandler.Image(width, height, jpeg)) };
    private static void AssertPng(byte[] png, int size)
    {
        Assert.That(png.Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.That((bitmap.Width, bitmap.Height), Is.EqualTo((size, size)));
    }
}

internal sealed class AvatarHttpHandler : HttpMessageHandler
{
    private readonly Func<Uri, CancellationToken, Task<HttpResponseMessage>> _response;
    internal ConcurrentQueue<Uri> Requests { get; } = new();
    internal ConcurrentQueue<string?> AuthorizationHeaders { get; } = new();
    internal ConcurrentQueue<string> UserAgents { get; } = new();
    internal AvatarHttpHandler(Func<Uri, HttpResponseMessage> response) : this((uri, _) => Task.FromResult(response(uri))) { }
    internal AvatarHttpHandler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> response) => _response = response;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Enqueue(request.RequestUri!); AuthorizationHeaders.Enqueue(request.Headers.Authorization?.ToString()); UserAgents.Enqueue(request.Headers.UserAgent.ToString());
        return _response(request.RequestUri!, token);
    }
    internal static byte[] Image(int width, int height, bool jpeg = false)
    {
        using SKBitmap bitmap = new(width, height); bitmap.Erase(SKColors.DarkGreen);
        using SKData data = bitmap.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
