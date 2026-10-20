using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

internal sealed record LinuxUpdateRelease(LinuxReleaseVersion Version, Uri ReleaseUri, Uri DownloadUri, string FileName, long Size, string? Sha256, bool IsPrerelease = false);
internal sealed record LinuxUpdateSearch(LinuxUpdateRelease? Release, bool HasCompatiblePublications);

internal interface ILinuxUpdateClient : IDisposable
{
    Task<LinuxUpdateSearch> FindAsync(LinuxReleaseVersion current, bool includePrereleases, CancellationToken token);
    Task<string> DownloadAsync(LinuxUpdateRelease release, IProgress<int>? progress, CancellationToken token);
}

/// <summary>Public fork releases only. No hosting credentials, repository commands, or installer privileges.</summary>
internal sealed class LinuxUpdateClient : ILinuxUpdateClient
{
    internal const string RepositoryUrl = "https://github.com/Azreal42/gitextensions";
    internal const long MaxPackageBytes = 512L * 1024 * 1024;
    private const int MaxFeedBytes = 2 * 1024 * 1024;
    private const int MaxPages = 5;
    private readonly HttpClient _http;
    private readonly string? _architecture;
    private readonly TimeSpan _requestTimeout;
    private readonly string _cacheDirectory;

    internal LinuxUpdateClient(HttpMessageHandler? handler = null, string? architecture = null, string? cacheDirectory = null, TimeSpan? requestTimeout = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip })
        { Timeout = Timeout.InfiniteTimeSpan };
        _architecture = architecture ?? RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => null };
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache && Path.IsPathFullyQualified(cache)
            ? cache : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "gitextensions", "updates");
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(5)) { throw new ArgumentOutOfRangeException(nameof(requestTimeout)); }
    }

    public async Task<LinuxUpdateSearch> FindAsync(LinuxReleaseVersion current, bool includePrereleases, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_architecture is not ("amd64" or "arm64")) { throw new NotSupportedException("No Linux package is available for this architecture."); }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            LinuxUpdateRelease? best = null; bool compatible = false;
            for (int page = 1; page <= MaxPages; page++)
            {
                using HttpRequestMessage request = Request(new Uri("https://api.github.com/repos/Azreal42/gitextensions/releases?per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture)), json: true);
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                EnsureSuccess(response);
                byte[] bytes = await ReadBoundedAsync(response.Content, MaxFeedBytes, deadline.Token).ConfigureAwait(false);
                using JsonDocument json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Array) { throw new IOException("GitHub returned an invalid release list."); }
                foreach (JsonElement item in json.RootElement.EnumerateArray())
                {
                    LinuxUpdateRelease? release = ParseRelease(item, _architecture, includePrereleases);
                    if (release is null) { continue; }
                    compatible = true;
                    if (release.Version.CompareTo(current) > 0 && (best is null || release.Version.CompareTo(best.Version) > 0)) { best = release; }
                }
                bool next = response.Headers.TryGetValues("Link", out IEnumerable<string>? links) && links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
                if (!next) { deadline.Token.ThrowIfCancellationRequested(); return new(best, compatible); }
            }
            throw new IOException("The release list is too long. Open the fork releases to check manually.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new IOException("The update check timed out."); }
        catch (JsonException) { throw new IOException("GitHub returned an invalid release list."); }
        catch (HttpRequestException) { throw new IOException("Could not contact GitHub. Check the network connection and try again."); }
    }

    private static LinuxUpdateRelease? ParseRelease(JsonElement item, string architecture, bool includePrereleases)
    {
        if (item.ValueKind != JsonValueKind.Object || Bool(item, "draft") != false || Bool(item, "prerelease") is not { } prerelease) { return null; }
        string? tag = Text(item, "tag_name"); LinuxReleaseVersion? version = LinuxReleaseVersion.Parse(tag);
        if (version is null || (!includePrereleases && (prerelease || version.IsPrerelease))) { return null; }
        string releaseUrl = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag!);
        if (!ExactUri(Text(item, "html_url"), releaseUrl, out Uri? releaseUri)
            || !item.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) { return null; }
        string fileName = "gitextensions_" + version.DebianVersion + "_" + architecture + ".deb";
        string downloadUrl = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag!) + "/" + Uri.EscapeDataString(fileName);
        LinuxUpdateRelease? found = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != fileName || Text(asset, "state") != "uploaded"
                || !asset.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long length) || length <= 0 || length > MaxPackageBytes
                || !ExactUri(Text(asset, "browser_download_url"), downloadUrl, out Uri? download)) { continue; }
            string? digest = Text(asset, "digest");
            string? hash = digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
                && digest.AsSpan(7).ContainsAnyExcept("0123456789abcdefABCDEF") == false ? digest[7..].ToLowerInvariant() : null;
            // Duplicate package identities are ambiguous; never pick an arbitrary asset.
            if (found is not null) { return null; }
            found = new(version, releaseUri!, download!, fileName, length, hash, prerelease || version.IsPrerelease);
        }
        return found;
    }

    public async Task<string> DownloadAsync(LinuxUpdateRelease release, IProgress<int>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (release.Sha256 is not { Length: 64 } || release.Sha256.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF")
            || release.Size <= 0 || release.Size > MaxPackageBytes || Path.GetFileName(release.FileName) != release.FileName
            || !AllowedDownloadUri(release.DownloadUri) || release.DownloadUri.Host != "github.com"
            || !release.DownloadUri.AbsolutePath.StartsWith("/Azreal42/gitextensions/releases/download/", StringComparison.Ordinal))
        { throw new IOException("The update package does not have valid verification information. Use Direct Download."); }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        string? directory = null; bool completed = false;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            directory = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
            else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            string temporary = Path.Combine(directory, "package.partial"), destination = Path.Combine(directory, release.FileName);
            Uri uri = release.DownloadUri;
            for (int redirects = 0; ; redirects++)
            {
                if (!AllowedDownloadUri(uri)) { throw new IOException("GitHub redirected the download to an unsupported location."); }
                using HttpRequestMessage request = Request(uri, json: false);
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location) { throw new IOException("GitHub redirected the download repeatedly."); }
                    uri = location.IsAbsoluteUri ? location : new(uri, location); continue;
                }
                EnsureSuccess(response);
                if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Size) { throw new IOException("The update package size does not match the release."); }
                await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    byte[] buffer = new byte[81920]; long total = 0; int read; progress?.Report(0);
                    while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                    {
                        total += read;
                        if (total > release.Size) { throw new IOException("The update package exceeds the release size."); }
                        hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                        progress?.Report((int)(total * 100 / release.Size));
                    }
                    if (total != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    { throw new IOException("The downloaded package does not match the release checksum."); }
                    await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                deadline.Token.ThrowIfCancellationRequested();
                File.Move(temporary, destination); completed = true; return destination;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new IOException("The update download timed out."); }
        catch (HttpRequestException) { throw new IOException("Could not download the update from GitHub. Try again later."); }
        finally
        {
            if (!completed && directory is not null && Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    private static HttpRequestMessage Request(Uri uri, bool json)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha");
        request.Headers.Accept.Add(new(json ? "application/vnd.github+json" : "application/octet-stream"));
        if (json) { request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); }
        return request;
    }
    private static bool AllowedDownloadUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private static bool ExactUri(string? value, string expected, out Uri? uri)
    { return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.AbsoluteUri == new Uri(expected).AbsoluteUri && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0; }
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Bool(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) { throw new IOException((int)response.StatusCode is 403 or 429
            ? "GitHub denied the update request or its rate limit was reached. Try again later."
            : "GitHub returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "."); }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) { throw new IOException("The release list exceeds the supported size."); }
        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream output = new(); byte[] buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { if (output.Length + read > limit) { throw new IOException("The release list exceeds the supported size."); } output.Write(buffer, 0, read); }
        token.ThrowIfCancellationRequested(); return output.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
