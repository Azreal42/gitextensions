using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class LinuxUpdateClientTests
{
    [TestCase("7.2.1-linux-alpha.2", "7.2.1-linux-alpha.10", -1)]
    [TestCase("7.2.1-linux-alpha+aaaa", "7.2.1-linux-alpha+zzzz", 0)]
    [TestCase("7.2.1-linux-alpha", "7.2.1-linux-alpha.1", -1)]
    [TestCase("7.2.1-linux-alpha.9", "7.2.1", -1)]
    [TestCase("7.2.2-linux-alpha", "7.2.1", 1)]
    [TestCase("7.2.1-2", "7.2.1-a", -1)]
    [TestCase("7.2.1-123456789012345678901", "7.2.1-12345678901234567890", 1)]
    public void ReleaseOrderingUsesSemanticVersionsAndNeverBuildHashes(string a, string b, int sign)
    { Assert.That(Math.Sign(Version(a).CompareTo(Version(b))), Is.EqualTo(sign)); }

    [TestCase("7.2")] [TestCase("v07.2.1")] [TestCase("7.2.1-alpha.01")] [TestCase("7.2.1/../../x")]
    public void InvalidReleaseTagsAreNotVersionNumbers(string text) => Assert.That(LinuxReleaseVersion.Parse(text), Is.Null);

    [Test]
    public async Task SelectsHighestCompatibleReleaseAcrossPagesAndIgnoresDraftsWindowsAndOtherArchitectures()
    {
        List<string> urls = [];
        using LinuxUpdateClient client = new(new Handler((r, t) =>
        {
            urls.Add(r.RequestUri!.AbsoluteUri);
            Assert.That(r.Headers.Authorization, Is.Null); Assert.That(r.Headers.UserAgent.ToString(), Does.Contain("GitExtensions/"));
            HttpResponseMessage response = Feed(urls.Count == 1
                ? [Release("v7.2.1-linux-alpha.2"), Release("v99.0.0", draft: true), Release("v99.0.0", architecture: "arm64"), Release("v88.0.0", windows: true)]
                : [Release("v7.2.1-linux-alpha.10"), Release("v7.2.1-linux-alpha.3")]);
            if (urls.Count == 1) { response.Headers.Add("Link", "<https://api.github.com/repos/Azreal42/gitextensions/releases?per_page=100&page=2>; rel=\"next\""); }
            return Task.FromResult(response);
        }), "amd64");
        LinuxUpdateSearch result = await client.FindAsync(Version("7.2.1-linux-alpha+abc"), true, default);
        Assert.That(result.Release!.Version.Text, Is.EqualTo("7.2.1-linux-alpha.10")); Assert.That(result.HasCompatiblePublications, Is.True);
        Assert.That(result.Release.FileName, Is.EqualTo("gitextensions_7.2.1~linux-alpha.10_amd64.deb"));
        Assert.That(urls.Count, Is.EqualTo(2)); Assert.That(urls[1], Does.EndWith("page=2"));
    }

    [Test]
    public async Task StableChannelSkipsPrereleasesAndEqualMetadataDoesNotBecomeANewVersion()
    {
        using LinuxUpdateClient stable = Client(Release("v7.2.2-linux-alpha"), Release("v7.2.0", prerelease: false));
        Assert.That((await stable.FindAsync(Version("7.2.1"), false, default)).Release, Is.Null);
        using LinuxUpdateClient equal = Client(Release("v7.2.1-linux-alpha+different"));
        LinuxUpdateSearch result = await equal.FindAsync(Version("7.2.1-linux-alpha+installed"), true, default);
        Assert.That(result.HasCompatiblePublications, Is.True); Assert.That(result.Release, Is.Null);
    }

    [Test]
    public async Task DistinguishesAnEmptyLinuxPublicationListFromNoNewerVersion()
    {
        using LinuxUpdateClient empty = Client(); Assert.That((await empty.FindAsync(Version("7.2.1"), true, default)).HasCompatiblePublications, Is.False);
        using LinuxUpdateClient older = Client(Release("v7.2.0", prerelease: false));
        LinuxUpdateSearch result = await older.FindAsync(Version("7.2.1"), true, default);
        Assert.That(result.Release, Is.Null); Assert.That(result.HasCompatiblePublications, Is.True);
    }

    [TestCase("url")] [TestCase("name")] [TestCase("size")] [TestCase("duplicate")]
    public async Task RejectsIncoherentOrAmbiguousPackageIdentities(string invalid)
    {
        Dictionary<string, object?> release = Release("v7.2.2"); var assets = (Dictionary<string, object?>[])release["assets"]!;
        switch (invalid)
        {
            case "url": assets[0]["browser_download_url"] = "https://example.com/gitextensions.deb"; break;
            case "name": assets[0]["name"] = "gitextensions_7.2.1_amd64.deb"; break;
            case "size": assets[0]["size"] = "wrong JSON type"; break;
            case "duplicate": release["assets"] = new[] { assets[0], assets[0] }; break;
        }
        using LinuxUpdateClient client = Client(release);
        Assert.That((await client.FindAsync(Version("7.2.1"), true, default)).HasCompatiblePublications, Is.False);
    }

    [Test]
    public async Task OldAssetsWithoutDigestCanOnlyBeDownloadedDirectlyInTheBrowser()
    {
        Dictionary<string, object?> release = Release("v7.2.2"); ((Dictionary<string, object?>[])release["assets"]!)[0].Remove("digest");
        using LinuxUpdateClient client = Client(release); LinuxUpdateRelease found = (await client.FindAsync(Version("7.2.1"), true, default)).Release!;
        Assert.That(found.Sha256, Is.Null);
        Assert.ThrowsAsync<IOException>(async () => await client.DownloadAsync(found, null, default));
    }

    [TestCase(403)] [TestCase(429)] [TestCase(500)]
    public void HttpErrorsAreNotReportedAsNoUpdates(int code)
    {
        using LinuxUpdateClient client = new(new Handler((r, t) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code))), "amd64");
        Assert.ThrowsAsync<IOException>(async () => await client.FindAsync(Version("7.2.1"), true, default));
    }

    [TestCase(false)] [TestCase(true)]
    public void FeedIsBoundedEvenWithoutAContentLength(bool knownLength)
    {
        byte[] bytes = new byte[2 * 1024 * 1024 + 1];
        using LinuxUpdateClient client = new(new Handler((r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekStream(new MemoryStream(bytes))) })), "amd64");
        Assert.ThrowsAsync<IOException>(async () => await client.FindAsync(Version("7.2.1"), true, default));
    }

    [Test]
    public void PaginationLimitIsAnErrorRatherThanAnIncompleteNoUpdatesResult()
    {
        int requests = 0;
        using LinuxUpdateClient client = new(new Handler((r, t) =>
        { requests++; HttpResponseMessage response = Feed([]); response.Headers.Add("Link", "<https://example.com/>; rel=\"next\""); return Task.FromResult(response); }), "amd64");
        Assert.ThrowsAsync<IOException>(async () => await client.FindAsync(Version("7.2.1"), true, default)); Assert.That(requests, Is.EqualTo(5));
    }

    [TestCase(false)] [TestCase(true)]
    public void DeadlineAndCancellationCoverAStalledResponseBody(bool cancel)
    {
        using CancellationTokenSource token = new();
        using LinuxUpdateClient client = new(new Handler((r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) })),
            "amd64", requestTimeout: TimeSpan.FromMilliseconds(cancel ? 1000 : 40));
        if (cancel) { token.CancelAfter(40); }
        if (cancel) { Assert.CatchAsync<OperationCanceledException>(async () => await client.FindAsync(Version("7.2.1"), true, token.Token)); }
        else { Assert.ThrowsAsync<IOException>(async () => await client.FindAsync(Version("7.2.1"), true, token.Token)); }
    }

    [Test]
    public async Task DownloadFollowsOnlyGitHubAssetRedirectsVerifiesBytesAndPublishesOnlyACompleteFile()
    {
        string cache = Path.Combine(Path.GetTempPath(), "updates-" + Guid.NewGuid().ToString("N")); byte[] bytes = Encoding.UTF8.GetBytes("test package bytes");
        LinuxUpdateRelease release = Package(bytes); int requests = 0; List<int> progress = [];
        using LinuxUpdateClient client = new(new Handler((r, t) =>
        {
            requests++; Assert.That(r.Headers.Authorization, Is.Null);
            if (requests == 1) { HttpResponseMessage redirect = new(HttpStatusCode.Found); redirect.Headers.Location = new("https://release-assets.githubusercontent.com/github-production-release-asset/test?signature=private"); return Task.FromResult(redirect); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }), "amd64", cache);
        try
        {
            string path = await client.DownloadAsync(release, new Values(progress), default);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes)); Assert.That(Path.GetFileName(path), Is.EqualTo(release.FileName));
            Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { path })); Assert.That(progress.Last(), Is.EqualTo(100));
            if (!OperatingSystem.IsWindows()) { Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(path)!) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead), Is.EqualTo((UnixFileMode)0)); }
        }
        finally { if (Directory.Exists(cache)) { Directory.Delete(cache, true); } }
    }

    [TestCase("hash")] [TestCase("short")] [TestCase("long")] [TestCase("redirect")] [TestCase("cancel")]
    public void FailedOrCancelledDownloadsNeverLeaveAPackageOrPartialFile(string failure)
    {
        string cache = Path.Combine(Path.GetTempPath(), "updates-" + Guid.NewGuid().ToString("N")); byte[] bytes = [1, 2, 3]; LinuxUpdateRelease release = Package(bytes);
        using CancellationTokenSource cancellation = new();
        using LinuxUpdateClient client = new(new Handler((r, t) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK);
            if (failure == "redirect") { response.StatusCode = HttpStatusCode.Found; response.Headers.Location = new("http://github.com/untrusted"); }
            else if (failure == "cancel") { response.Content = new StreamContent(new WaitingStream()); cancellation.CancelAfter(40); }
            else { response.Content = new StreamContent(new NonSeekStream(new MemoryStream(failure switch { "hash" => [3, 2, 1], "short" => [1, 2], _ => [1, 2, 3, 4] }))); }
            return Task.FromResult(response);
        }), "amd64", cache);
        try
        {
            if (failure == "cancel") { Assert.CatchAsync<OperationCanceledException>(async () => await client.DownloadAsync(release, null, cancellation.Token)); }
            else { Assert.ThrowsAsync<IOException>(async () => await client.DownloadAsync(release, null, cancellation.Token)); }
            Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories), Is.Empty);
            Assert.That(Directory.GetDirectories(cache), Is.Empty);
        }
        finally { if (Directory.Exists(cache)) { Directory.Delete(cache, true); } }
    }

    private static LinuxReleaseVersion Version(string value) => LinuxReleaseVersion.Parse(value)!;
    private static LinuxUpdateClient Client(params Dictionary<string, object?>[] releases) => new(new Handler((r, t) => Task.FromResult(Feed(releases))), "amd64");
    private static HttpResponseMessage Feed(Dictionary<string, object?>[] releases) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) };
    private static Dictionary<string, object?> Release(string tag, bool draft = false, string architecture = "amd64", bool windows = false, bool prerelease = true)
    {
        string name = windows ? "GitExtensions.msi" : "gitextensions_" + Version(tag).DebianVersion + "_" + architecture + ".deb";
        return new() { ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = prerelease,
            ["html_url"] = LinuxUpdateClient.RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag),
            ["assets"] = new[] { new Dictionary<string, object?> { ["name"] = name, ["state"] = "uploaded", ["size"] = 42L,
                ["browser_download_url"] = LinuxUpdateClient.RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name), ["digest"] = "sha256:" + new string('a', 64) } } };
    }
    private static LinuxUpdateRelease Package(byte[] bytes) => new(Version("7.2.2"), new(LinuxUpdateClient.RepositoryUrl + "/releases/tag/v7.2.2"),
        new(LinuxUpdateClient.RepositoryUrl + "/releases/download/v7.2.2/gitextensions_7.2.2_amd64.deb"), "gitextensions_7.2.2_amd64.deb", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    private sealed class Values(List<int> values) : IProgress<int> { public void Report(int value) => values.Add(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private class NonSeekStream(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class WaitingStream() : NonSeekStream(Stream.Null)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
}
