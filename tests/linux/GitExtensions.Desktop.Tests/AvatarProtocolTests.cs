using System.Globalization;
using System.Net;
using System.Text;
using GitCommands;
using GitExtensions.Linux.Desktop.Avatars;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class AvatarProtocolTests
{
    [TestCase(AvatarFallbackType.AuthorInitials, "404")]
    [TestCase(AvatarFallbackType.MonsterId, "monsterid")]
    [TestCase(AvatarFallbackType.Wavatar, "wavatar")]
    [TestCase(AvatarFallbackType.Identicon, "identicon")]
    [TestCase(AvatarFallbackType.Retro, "retro")]
    [TestCase(AvatarFallbackType.Robohash, "robohash")]
    public void GravatarUsesTheWindowsMd5AndExactFallbackQuery(AvatarFallbackType fallback, string expected)
    {
        Uri uri = AvatarProviderProtocol.Gravatar("  TEST@Example.com \t", 90, fallback);
        Assert.That(uri.AbsoluteUri, Is.EqualTo("https://www.gravatar.com/avatar/55502f40dc8b7c769880b10874abc9d0?r=g&d=" + expected + "&s=90"));
    }

    [Test]
    public void ForcedFallbackAndUnknownFallbackKeepTheWindowsQueryContract()
    {
        Assert.That(AvatarProviderProtocol.Gravatar("test@example.com", 64, AvatarFallbackType.Retro, true).Query,
            Is.EqualTo("?r=g&d=retro&f=y&s=64"));
        Assert.That(AvatarProviderProtocol.Gravatar("test@example.com", 64, (AvatarFallbackType)999).Query,
            Is.EqualTo("?r=g&d=404&s=64"));
        Assert.That(AvatarProviderProtocol.Gravatar("test@example.com", 64, null).Query, Is.EqualTo("?r=g&d=404&s=64"));
    }

    [TestCase("octocat@users.noreply.github.com", "octocat", "https://avatars.githubusercontent.com/octocat?s=90")]
    [TestCase("12345+octocat@users.noreply.github.com", "octocat", "https://avatars.githubusercontent.com/octocat?s=90")]
    [TestCase("test@example.com", null, "https://avatars.githubusercontent.com/u/e?email=test%40example.com&s=90")]
    [TestCase(" octocat@users.noreply.github.com ", null, "https://avatars.githubusercontent.com/u/e?email=+octocat%40users.noreply.github.com+&s=90")]
    public void GitHubPreservesLiteralEmailsAndOnlyRecognizesExactNoreplyAddresses(string email, string? username, string expected)
    {
        Assert.That(AvatarProviderProtocol.GitHubUsername(email), Is.EqualTo(username));
        Assert.That(AvatarProviderProtocol.GitHubAvatar(email, 90).AbsoluteUri, Is.EqualTo(expected));
    }

    [TestCase("{\"avatar_url\":\"https://cdn.invalid/avatar.png\"}", "https://cdn.invalid/avatar.png?s=90")]
    [TestCase("{\"avatar_url\":\"https://cdn.invalid/avatar.png?token=literal\"}", "https://cdn.invalid/avatar.png?token=literal&s=90")]
    [TestCase("{\"avatar_url\":\"file:///tmp/avatar.png\"}", null)]
    [TestCase("{\"avatar_url\":12}", null)]
    [TestCase("not json", null)]
    public void ProfileAvatarAcceptsOnlyHttpImagesAndPreservesExistingQueries(string json, string? expected)
        => Assert.That(AvatarProviderProtocol.ProfileAvatar(Encoding.UTF8.GetBytes(json), 90)?.AbsoluteUri, Is.EqualTo(expected));

    [Test]
    public void CustomTemplateUsesLiteralWindowsHashVectorsAndUrlEncoding()
    {
        AvatarRequest request = OfflineAvatars.Request(" TEST@Example.com ", "A+B Ω");
        string result = AvatarProviderProtocol.CompileTemplate("{name}|{EMAIL}|{ Md5 }|{sha1}|{sha256}|{imagesize}|{unknown}")(new(request));
        Assert.That(result, Is.EqualTo("A%2bB+%ce%a9|test%40example.com|55502f40dc8b7c769880b10874abc9d0|"
            + "567159d622ffbb50b11b0efd307be358624a26ee|973dfe463ec85785f5f95af5ba3906eedb2d931c24e69824a89ea65dba4e813b|90|"));
    }

    [Test]
    public void LinkedWindowsTemplateFormatterRetainsIncompleteAndRepeatedVariableBehavior()
    {
        Func<AvatarProviderProtocol.TemplateData, string> incomplete = AvatarProviderProtocol.CompileTemplate("hello {name} then {email.");
        Func<AvatarProviderProtocol.TemplateData, string> repeated = AvatarProviderProtocol.CompileTemplate("{name}-{name}/{unknown}/{email}");
        Assert.That(incomplete(new(OfflineAvatars.Request(name: "Literal"))), Is.EqualTo("hello Literal then "));
        Assert.That(repeated(new(OfflineAvatars.Request(name: "One"))), Is.EqualTo("One-One//test%40example.com"));
        Assert.That(repeated(new(OfflineAvatars.Request("next@example.com", "Two"))), Is.EqualTo("Two-Two//next%40example.com"));
    }

    // Literal vectors selected from the Windows InitialsAvatarGeneratorTests, plus explicit Turkish culture.
    [TestCase("albert.einstein@noreply.com", "albert z middlename einstein (k)", "en-US", "AE")]
    [TestCase("albert.einstein@noreply.com", "EINSTEIN, Albert middlename (Nobel_price_winner)", "en-US", "EM")]
    [TestCase("albert_z_einstein@noreply.com", null, "en-US", "AE")]
    [TestCase("", "AlbertEinstein", "en-US", "AE")]
    [TestCase("", "albert", "en-US", "Al")]
    [TestCase("1-pass@noreply.com", "", "en-US", "1P")]
    [TestCase("/*+=/°¨", "'µ *", "en-US", "'*")]
    [TestCase("", "", "en-US", "?")]
    [TestCase("", "ismail yılmaz", "tr-TR", "İY")]
    public void InitialsRetainTheWindowsNameEmailAndCultureRules(string email, string? name, string culture, string expected)
        => Assert.That(AvatarProviderProtocol.Initials(email, name, CultureInfo.GetCultureInfo(culture)), Is.EqualTo(expected));

    [Test]
    public async Task DefaultTriesGitHubThenGravatarAndRejectsGitHubIdenticonsOnly()
    {
        using OfflineAvatarFixture f = new(); f.Handler.Respond = (_, _) => Task.FromResult(OfflineAvatarHttp.Image());
        f.Codec.DecodeImage = (_, _, rejectIdenticon) => rejectIdenticon ? null : OfflineAvatars.Image();
        using AvatarImageTransport transport = new(f.Client);
        AvatarProviderChain chain = new(new() { Provider = AvatarProvider.Default, Fallback = AvatarFallbackType.Retro }, transport, f.Codec,
            (_, _) => throw new AssertionException("A successful Gravatar must stop the chain before initials."));
        Assert.That(await chain.ReadAsync(OfflineAvatars.Request(), default), Is.Not.Null);
        Assert.That(f.Handler.Requests.Select(uri => uri.AbsoluteUri), Is.EqualTo(new[]
        {
            "https://avatars.githubusercontent.com/u/e?email=test%40example.com&s=90",
            "https://www.gravatar.com/avatar/55502f40dc8b7c769880b10874abc9d0?r=g&d=retro&s=90"
        }));
        Assert.That(f.Codec.Calls.Select(call => (call.Pixels, call.RejectIdenticon)), Is.EqualTo(new[] { (90, true), (90, false) }));
    }

    [Test]
    public async Task BotNoreplyFetchesTheProfileThenItsAvatarInsteadOfTheEmailEndpoint()
    {
        using OfflineAvatarFixture f = new(); f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"avatar_url\":\"https://bot-cdn.invalid/icon?token=x\"}") }
            : OfflineAvatarHttp.Image());
        using AvatarImageTransport transport = new(f.Client);
        AvatarProviderChain chain = new(new() { Provider = AvatarProvider.Default }, transport, f.Codec,
            (_, _) => throw new AssertionException("The bot image is available."));
        Assert.That(await chain.ReadAsync(OfflineAvatars.Request("49699333+dependabot[bot]@users.noreply.github.com"), default), Is.Not.Null);
        Assert.That(f.Handler.Requests.Select(uri => uri.AbsoluteUri), Is.EqualTo(new[]
        {
            "https://api.github.com/users/dependabot%5Bbot%5D", "https://bot-cdn.invalid/icon?token=x&s=90"
        }));
        Assert.That(f.Codec.Calls.Single().RejectIdenticon, Is.True);
    }

    [Test]
    public async Task OrderedCustomUrlsSkipTagsTheyCannotUseAndStopAtTheFirstDecodedImage()
    {
        using OfflineAvatarFixture f = new(); f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "second.invalid"
            ? OfflineAvatarHttp.Image() : new HttpResponseMessage(HttpStatusCode.NotFound));
        using AvatarImageTransport transport = new(f.Client);
        AvatarProviderChain chain = new(new() { Provider = AvatarProvider.Custom,
            CustomTemplate = " <Custom>; <Unknown>; https://first.invalid/{email}; https://second.invalid/{imagesize}; https://never.invalid/ " },
            transport, f.Codec, (_, _) => throw new AssertionException("The second URL succeeds before initials."));
        Assert.That(await chain.ReadAsync(OfflineAvatars.Request(), default), Is.Not.Null);
        Assert.That(f.Handler.Requests.Select(uri => uri.AbsoluteUri), Is.EqualTo(new[] { "https://first.invalid/test%40example.com", "https://second.invalid/90" }));
        Assert.That(f.Codec.Calls.Single().RejectIdenticon, Is.False);
    }

    [TestCase("<None>", 0)]
    [TestCase("<dEfAuLt>", 2)]
    public async Task WindowsCustomTagsChooseInitialsBeforeLaterSegmentsOrForcedRemoteFallback(string tag, int expectedRequests)
    {
        using OfflineAvatarFixture f = new(); int initials = 0; using AvatarImageTransport transport = new(f.Client);
        AvatarProviderChain chain = new(new() { Provider = AvatarProvider.Custom, Fallback = AvatarFallbackType.Retro,
            CustomTemplate = tag + ";https://never.invalid/image" }, transport, f.Codec,
            (_, _) => { initials++; return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); });
        Assert.That(await chain.ReadAsync(OfflineAvatars.Request(), default), Is.Not.Null);
        Assert.That(initials, Is.EqualTo(1)); Assert.That(f.Handler.Requests.Count, Is.EqualTo(expectedRequests));
        if (expectedRequests > 0)
        {
            Assert.That(f.Handler.Requests.Last().AbsoluteUri,
                Is.EqualTo("https://www.gravatar.com/avatar/55502f40dc8b7c769880b10874abc9d0?r=g&d=404&s=90"));
        }
    }

    [TestCase(AvatarFallbackType.AuthorInitials, false)]
    [TestCase(AvatarFallbackType.Retro, true)]
    public async Task NoneProviderCanStillRequestAnExplicitRemoteFallback(AvatarFallbackType fallback, bool remote)
    {
        using OfflineAvatarFixture f = new(); int initials = 0; f.Handler.Respond = (_, _) => Task.FromResult(OfflineAvatarHttp.Image());
        using AvatarImageTransport transport = new(f.Client);
        AvatarProviderChain chain = new(new() { Provider = AvatarProvider.None, Fallback = fallback }, transport, f.Codec,
            (_, _) => { initials++; return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); });
        Assert.That(chain.PerformsIo, Is.EqualTo(remote)); Assert.That(await chain.ReadAsync(OfflineAvatars.Request(), default), Is.Not.Null);
        Assert.That(initials, Is.EqualTo(remote ? 0 : 1)); Assert.That(f.Handler.Requests.Count, Is.EqualTo(remote ? 1 : 0));
        if (remote) { Assert.That(f.Handler.Requests.Single().Query, Is.EqualTo("?r=g&d=retro&f=y&s=90")); }
    }

    [TestCase("file:///tmp/avatar.png")]
    [TestCase("ftp://offline.invalid/avatar")]
    [TestCase("relative/image.png")]
    public async Task TransportRejectsNonHttpTargetsWithoutSendingAnything(string target)
    {
        using OfflineAvatarFixture f = new(); using AvatarImageTransport transport = new(f.Client);
        Assert.That(await transport.DownloadAsync(new Uri(target, UriKind.RelativeOrAbsolute), 1024, default), Is.Null);
        Assert.That(await transport.DownloadAsync(null, 1024, default), Is.Null); Assert.That(f.Handler.Requests, Is.Empty);
    }

    [Test]
    public async Task TransportEnforcesTheStreamLimitEvenWithoutADeclaredLengthAndDisposesTheResponse()
    {
        using OfflineAvatarFixture f = new(); TrackingAvatarStream stream = new(new byte[8193]);
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using AvatarImageTransport transport = new(f.Client);
        Assert.That(await transport.DownloadAsync(new Uri("https://offline.invalid/large"), 8192, default), Is.Null);
        Assert.That(stream.Disposed, Is.True); Assert.That(stream.BytesRead, Is.EqualTo(8193)); Assert.That(f.Codec.Calls, Is.Empty);
    }

    [Test]
    public async Task TransportHasTenActualSendsAndACanceledQueuedWaitDoesNotSendAnEleventhRequest()
    {
        using OfflineAvatarFixture f = new(); TaskCompletionSource tenEntered = OfflineAvatars.Signal(), release = OfflineAvatars.Signal(); int entered = 0;
        f.Handler.Respond = async (_, _) => { if (Interlocked.Increment(ref entered) == 10) { tenEntered.TrySetResult(); } await release.Task; return OfflineAvatarHttp.Image(); };
        using AvatarImageTransport transport = new(f.Client);
        Task<byte[]?>[] reads = Enumerable.Range(0, 10).Select(i => transport.DownloadAsync(new Uri("https://offline.invalid/" + i), 1024, default)).ToArray();
        using CancellationTokenSource queued = new();
        try
        {
            await tenEntered.Task.WaitAsync(OfflineAvatars.Limit);
            Task<byte[]?> eleventh = transport.DownloadAsync(new Uri("https://offline.invalid/eleventh"), 1024, queued.Token);
            Assert.That(eleventh.IsCompleted, Is.False); queued.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await eleventh.WaitAsync(OfflineAvatars.Limit));
            Assert.That(f.Handler.Requests.Count, Is.EqualTo(10)); release.TrySetResult();
            Assert.That((await Task.WhenAll(reads).WaitAsync(OfflineAvatars.Limit)).All(bytes => bytes!.SequenceEqual(OfflineAvatars.Bytes)), Is.True);
        }
        finally { release.TrySetResult(); await Task.WhenAll(reads).WaitAsync(OfflineAvatars.Limit); }
    }

    private sealed class TrackingAvatarStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal bool Disposed { get; private set; } internal int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { int read = await base.ReadAsync(buffer, cancellationToken); BytesRead += read; return read; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
