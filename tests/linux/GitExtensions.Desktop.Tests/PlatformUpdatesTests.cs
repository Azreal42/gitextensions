using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform")]
public sealed class PlatformUpdatesTests
{
    private const string Version = "9.0.0-avalonia-alpha";
    private static readonly string[] Packages =
    [
        "gitextensions_9.0.0~avalonia-alpha_amd64.deb", "gitextensions_9.0.0~avalonia-alpha_arm64.deb",
        "gitextensions_9.0.0-avalonia-alpha_win-x64.zip", "gitextensions_9.0.0-avalonia-alpha_win-arm64.zip",
        "gitextensions_9.0.0-avalonia-alpha_osx-x64.zip", "gitextensions_9.0.0-avalonia-alpha_osx-arm64.zip",
    ];

    [TestCase(0, "amd64", 0)] [TestCase(0, "arm64", 1)]
    [TestCase(1, "amd64", 2)] [TestCase(1, "arm64", 3)]
    [TestCase(2, "amd64", 4)] [TestCase(2, "arm64", 5)]
    public async Task SelectsOnlyTheNativeAssetAndDownloadsTheVerifiedBytes(int platform, string architecture, int package)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Native update λ\n");
        string cache = Path.Combine(Path.GetTempPath(), "native updates λ " + Guid.NewGuid().ToString("N"));
        using LinuxUpdateClient client = new(new FeedHandler(bytes, Packages), architecture, cache, platform: (DesktopPlatform)platform);
        try
        {
            LinuxUpdateSearch result = await client.FindAsync(LinuxReleaseVersion.Parse("7.2.1-avalonia-alpha")!, true, default);
            Assert.That(result.HasCompatiblePublications, Is.True);
            LinuxUpdateRelease release = result.Release!;
            Assert.That(release.FileName, Is.EqualTo(Packages[package]));
            string path = await client.DownloadAsync(release, null, default);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(Path.GetFileName(path), Is.EqualTo(Packages[package]));
            Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { path }));
        }
        finally { if (Directory.Exists(cache)) { Directory.Delete(cache, true); } }
    }

    [TestCase(0, "amd64", 0)] [TestCase(0, "arm64", 1)]
    [TestCase(1, "amd64", 2)] [TestCase(1, "arm64", 3)]
    [TestCase(2, "amd64", 4)] [TestCase(2, "arm64", 5)]
    public async Task OtherSystemsAndArchitecturesAreNeverOfferedAsAnUpdate(int platform, string architecture, int package)
    {
        using LinuxUpdateClient client = new(new FeedHandler([1, 2, 3], Packages.Where((_, i) => i != package).ToArray()),
            architecture, platform: (DesktopPlatform)platform);
        LinuxUpdateSearch result = await client.FindAsync(LinuxReleaseVersion.Parse("7.2.1")!, true, default);
        Assert.That(result.HasCompatiblePublications, Is.False);
        Assert.That(result.Release, Is.Null);
    }

    [Test]
    public async Task ProductionDefaultSelectsTheCurrentOperatingSystem()
    {
        using LinuxUpdateClient client = new(new FeedHandler([1, 2, 3], Packages), "amd64");
        LinuxUpdateSearch result = await client.FindAsync(LinuxReleaseVersion.Parse("7.2.1")!, true, default);
        int package = OperatingSystem.IsWindows() ? 2 : OperatingSystem.IsMacOS() ? 4 : 0;
        Assert.That(result.Release!.FileName, Is.EqualTo(Packages[package]));
    }

    private sealed class FeedHandler(byte[] bytes, string[] packages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.Headers.Authorization, Is.Null);
            if (request.RequestUri!.Host != "api.github.com")
            {
                Assert.That(request.RequestUri.Host, Is.EqualTo("github.com"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            var release = new
            {
                draft = false, prerelease = true, tag_name = "v" + Version,
                html_url = LinuxUpdateClient.RepositoryUrl + "/releases/tag/v" + Version,
                assets = packages.Select(name => new
                {
                    name, state = "uploaded", size = bytes.Length,
                    digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    browser_download_url = LinuxUpdateClient.RepositoryUrl + "/releases/download/v" + Version + "/" + Uri.EscapeDataString(name),
                }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new[] { release }), Encoding.UTF8, "application/json") });
        }
    }
}
