using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux;

public sealed class TeamCityException(string message, int? statusCode = null) : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Explicit GET-only REST/XML reader. No NTLM, OS credentials, inherited cookies or browser login.</summary>
public sealed class TeamCityApiClient : ITeamCityClient
{
    public const int PageSize = 100;
    private readonly TeamCitySettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly Regex _filter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _active = [];
    private Dictionary<Uri, CachedXml> _documents = [];
    private TeamCityBuildSnapshot? _cached;
    private bool _disposed;
    private Task? _drain;
    private sealed record CachedXml(string ETag, XDocument Document, long Bytes);
    private sealed class ReadContext(Dictionary<Uri, CachedXml> cache)
    {
        internal Dictionary<Uri, CachedXml> Cache { get; } = cache;
        internal int Requests { get; set; }
    }
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPages { get; init; } = 10;
    public int MaxItems { get; init; } = 1000;
    public int MaxBuildDetails { get; init; } = 500;
    public int MaxRequests { get; init; } = 2048;
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    public Uri ServerUri { get; }
    public Uri ApiUri { get; }
    public string UniqueKey => ServerUri.Host;

    public TeamCityApiClient(TeamCitySettings settings, IBuildServerCredentials? credentials = null,
        HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(requireProjects: false);
        _settings = settings; ServerUri = settings.ServerUri; _filter = settings.CreateBuildIdRegex(); _clock = clock ?? TimeProvider.System;
        // Snapshot mutable interface drafts now; no subsequent caller mutation can change endpoint/auth mode.
        BuildServerCredentialsType mode = credentials?.BuildServerCredentialsType ?? BuildServerCredentialsType.Guest;
        string user = credentials?.Username ?? "", password = credentials?.Password ?? "", bearer = credentials?.BearerToken ?? "";
        string prefix;
        switch (mode)
        {
            case BuildServerCredentialsType.Guest: prefix = "guestAuth/"; break;
            case BuildServerCredentialsType.UsernameAndPassword:
                if (string.IsNullOrWhiteSpace(user) || password.Length == 0 || user.Contains(':') || user.Any(char.IsControl) || password.Any(char.IsControl))
                { throw new ArgumentException("Enter valid explicit TeamCity username and password credentials."); }
                prefix = "httpAuth/"; _authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password))); break;
            case BuildServerCredentialsType.BearerToken:
                if (bearer.Length == 0 || bearer.Any(c => c < 33 || c > 126)) { throw new ArgumentException("Enter a valid TeamCity bearer token."); }
                prefix = ""; _authorization = new("Bearer", bearer); break;
            default: throw new ArgumentException("The TeamCity credential mode is not supported.");
        }
        // Windows permits explicitly configured HTTP, including explicitly supplied credentials. Never upgrade/downgrade silently.
        ApiUri = new(ServerUri, prefix + "app/rest/");
        HttpMessageHandler transport = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        for (HttpMessageHandler? h = transport; h is not null; h = h is DelegatingHandler d ? d.InnerHandler : null)
        {
            if (h is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseCookies = false; client.UseDefaultCredentials = false; client.Credentials = null; client.DefaultProxyCredentials = null; }
            if (h is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; sockets.DefaultProxyCredentials = null; }
        }
        _http = new(transport) { Timeout = TimeSpan.FromSeconds(45) };
    }

    public Task<TeamCityPage<TeamCityProject>> GetProjectsAsync(CancellationToken cancellationToken = default) => Owned(
        (c, t) => CollectionAsync(c, Endpoint("projects?locator=count:100&fields=count,nextHref,project(id,name,parentProjectId,archived)"), "projects", "project", TeamCityXml.Project, t), cancellationToken);

    public Task<TeamCityPage<TeamCityBuildType>> GetProjectBuildTypesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        TeamCitySettings.ValidateId(projectId);
        return Owned((c, t) => ProjectBuildTypesAsync(c, projectId, t), cancellationToken);
    }
    private Task<TeamCityPage<TeamCityBuildType>> ProjectBuildTypesAsync(ReadContext c, string id, CancellationToken t) => CollectionAsync(c,
        Endpoint("projects/" + Uri.EscapeDataString(id) + "?fields=id,buildTypes(count,nextHref,buildType(id,name,projectId))"),
        "buildTypes", "buildType", TeamCityXml.BuildType, t, nestedProject: true);

    public Task<TeamCityBuildType> GetBuildTypeAsync(string buildTypeId, CancellationToken cancellationToken = default)
    {
        TeamCitySettings.ValidateId(buildTypeId);
        return Owned(async (c, t) =>
        {
            TeamCityBuildType result = TeamCityXml.BuildType(TeamCityXml.Root(await GetXmlAsync(c, Endpoint("buildTypes/id:" + Uri.EscapeDataString(buildTypeId) + "?fields=id,name,projectId"), t).ConfigureAwait(false), "buildType"));
            if (result.Id != buildTypeId) { throw new TeamCityException("TeamCity returned a different build configuration."); }
            return result;
        }, cancellationToken);
    }

    public Task<TeamCityBuildSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default) => Owned<TeamCityBuildSnapshot>(async (c, t) =>
    {
        if (!_settings.Active) { return new([], _clock.GetUtcNow()); }
        if (!force && _cached is not null && _clock.GetUtcNow() - _cached.ReadAt < CacheDuration) { return _cached; }
        if (_settings.ProjectIds.Count == 0) { throw new TeamCityException("Select at least one TeamCity project before enabling integration."); }
        bool truncated = false; List<string> diagnostics = []; Dictionary<string, string> ids = new(StringComparer.Ordinal);
        HashSet<string> types = new(StringComparer.Ordinal);
        foreach (string project in _settings.ProjectIds)
        {
            TeamCityPage<TeamCityBuildType> page = await ProjectBuildTypesAsync(c, project, t).ConfigureAwait(false);
            truncated |= page.Truncated; diagnostics.AddRange(page.Diagnostics);
            foreach (TeamCityBuildType type in page.Items)
            {
                if (!_filter.IsMatch(type.Id) || !types.Add(type.Id)) { continue; }
                foreach (bool running in new[] { false, true })
                {
                    string locator = "branch:(default:any),running:" + (running ? "true" : "false") + ",defaultFilter:false,count:100";
                    TeamCityPage<string> builds = await CollectionAsync(c, Endpoint("buildTypes/id:" + Uri.EscapeDataString(type.Id)
                        + "/builds/?locator=" + Uri.EscapeDataString(locator) + "&fields=count,nextHref,build(id)"), "builds", "build", e => TeamCityXml.Required(e, "id"), t).ConfigureAwait(false);
                    truncated |= builds.Truncated; diagnostics.AddRange(builds.Diagnostics);
                    foreach (string id in builds.Items)
                    {
                        if (!ulong.TryParse(id, out ulong n) || n == 0) { throw new TeamCityException("TeamCity returned an invalid build ID."); }
                        if (ids.TryGetValue(id, out string? previous) && previous != type.Id) { throw new TeamCityException("TeamCity returned conflicting build configuration IDs."); }
                        ids[id] = type.Id;
                    }
                }
            }
        }
        List<TeamCityBuild> accepted = [];
        if (ids.Count > MaxBuildDetails) { truncated = true; diagnostics.Add("The configured TeamCity build-detail limit was reached; history is incomplete."); }
        foreach ((string id, string expectedType) in ids.OrderByDescending(x => ulong.Parse(x.Key)).Take(MaxBuildDetails))
        {
            XDocument xml = await GetXmlAsync(c, Endpoint("builds/id:" + Uri.EscapeDataString(id)
                + "?fields=id,buildTypeId,buildType(id),number,status,state,running,webUrl,startDate,finishDate,statusText,running-info(currentStageText),canceledInfo,revisions(revision(version))"), t).ConfigureAwait(false);
            TeamCityBuild build = TeamCityXml.Build(xml);
            if (build.Id != id || build.BuildTypeId != expectedType) { throw new TeamCityException("TeamCity returned details for a different build."); }
            accepted.Add(build);
        }
        if (accepted.Any(b => b.CommitHashes.Count == 0)) { diagnostics.Add("Some TeamCity builds have no supported Git revision; no commit status was assigned to those builds."); }
        return new(accepted, _clock.GetUtcNow(), truncated, diagnostics.Distinct(StringComparer.Ordinal));
    }, cancellationToken, snapshot => _cached = snapshot);

    private Uri Endpoint(string relative) => new(ApiUri, relative);
    private bool InApiScope(Uri uri)
    {
        if (!TeamCitySettings.SameOrigin(ApiUri, uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) { return false; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.StartsWith(Uri.UnescapeDataString(ApiUri.AbsolutePath), StringComparison.Ordinal) && !path.Contains('\\')
            && !path.Split('/').Any(p => p is "." or "..");
    }
    private Uri Follow(Uri current, string href)
    {
        if (!Uri.TryCreate(current, href, out Uri? next) || !InApiScope(next))
        { throw new TeamCityException("TeamCity redirected or paginated outside the configured REST origin/base path; credentials were not forwarded."); }
        return next;
    }
    private async Task<TeamCityPage<T>> CollectionAsync<T>(ReadContext context, Uri first, string rootName, string itemName,
        Func<XElement, T> parse, CancellationToken token, bool nestedProject = false)
    {
        List<T> values = []; HashSet<Uri> visited = []; Uri? uri = first;
        for (int page = 0; page < MaxPages && uri is not null; page++)
        {
            if (!visited.Add(uri)) { throw new TeamCityException("TeamCity pagination contains a cycle."); }
            XDocument doc = await GetXmlAsync(context, uri, token).ConfigureAwait(false); XElement root;
            if (page == 0 && nestedProject)
            {
                root = TeamCityXml.Root(doc, "project").Element("buildTypes")
                    ?? throw new TeamCityException("The TeamCity project response has no build configuration list.");
            }
            else { root = TeamCityXml.Root(doc, rootName); }
            XElement[] elements = root.Elements(itemName).ToArray();
            string? count = (string?)root.Attribute("count");
            if (count is not null && (!int.TryParse(count, out int n) || n != elements.Length))
            { throw new TeamCityException("The TeamCity collection count does not match its XML entries."); }
            foreach (XElement element in elements)
            {
                if (values.Count == MaxItems) { return new(values, true, ["The TeamCity item limit was reached; this list is incomplete."]); }
                values.Add(parse(element));
            }
            string? next = (string?)root.Attribute("nextHref"); uri = string.IsNullOrWhiteSpace(next) ? null : Follow(uri, next);
        }
        return new(values, uri is not null, uri is null ? [] : ["The TeamCity page limit was reached; this list is incomplete."]);
    }

    private async Task<XDocument> GetXmlAsync(ReadContext context, Uri original, CancellationToken token)
    {
        Uri uri = original;
        for (int redirects = 0; ; redirects++)
        {
            token.ThrowIfCancellationRequested();
            if (++context.Requests > MaxRequests) { throw new TeamCityException("The TeamCity request limit was reached. Narrow the configured projects/build filter."); }
            if (!InApiScope(uri)) { throw new TeamCityException("The request is outside the configured TeamCity REST endpoint."); }
            context.Cache.TryGetValue(uri, out CachedXml? cached);
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha"); request.Headers.Authorization = _authorization;
            if (cached is not null && EntityTagHeaderValue.TryParse(cached.ETag, out EntityTagHeaderValue? etag)) { request.Headers.IfNoneMatch.Add(etag); }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is null) { throw new TeamCityException("TeamCity redirected repeatedly. Configure its final endpoint."); }
                uri = Follow(uri, response.Headers.Location.OriginalString); continue;
            }
            if (response.StatusCode == HttpStatusCode.NotModified)
            { return cached?.Document ?? throw new TeamCityException("TeamCity returned 304 without a matching cached XML document."); }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { throw new TeamCityException("TeamCity denied access. Configure guest access or explicit credentials; NTLM/OS sign-in is not supported.", (int)response.StatusCode); }
            if ((int)response.StatusCode == 429) { throw new TeamCityException("TeamCity rate-limited the request. Try again later.", 429); }
            if (!response.IsSuccessStatusCode) { throw new TeamCityException("TeamCity returned HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode); }
            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true)
            { throw new TeamCityException("TeamCity returned a login page. Configure guest access or explicit credentials; NTLM/OS sign-in is not supported."); }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) { throw new TeamCityException("The TeamCity XML response exceeded the configured size limit."); }
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream bytes = new(); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaxResponseBytes) { throw new TeamCityException("The TeamCity XML response exceeded the configured size limit."); }
                bytes.Write(buffer, 0, read);
            }
            bytes.Position = 0;
            using XmlReader reader = XmlReader.Create(bytes, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxResponseBytes });
            XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false);
            if (response.Headers.ETag is { } tag)
            {
                context.Cache.Remove(uri); context.Cache[uri] = new(tag.ToString(), document, bytes.Length);
                while (context.Cache.Count > 256 || context.Cache.Values.Sum(x => x.Bytes) > 16 * 1024 * 1024) { context.Cache.Remove(context.Cache.Keys.First()); }
            }
            else { context.Cache.Remove(uri); }
            return document;
        }
    }

    private Task<T> Owned<T>(Func<ReadContext, CancellationToken, Task<T>> body, CancellationToken caller, Action<T>? publish = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<T> work = ExecuteAsync(body, caller, publish); _active.Add(work);
            _ = work.ContinueWith(t => { lock (_sync) { _active.Remove(t); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return work;
        }
    }
    private async Task<T> ExecuteAsync<T>(Func<ReadContext, CancellationToken, Task<T>> body, CancellationToken caller, Action<T>? publish)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        CancellationToken token = linked.Token; await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (MaxPages is < 1 or > 100 || MaxItems is < 1 or > 10000 || MaxBuildDetails is < 1 or > 10000 || MaxRequests is < 1 or > 16384
                || MaxResponseBytes is < 1024 or > 64 * 1024 * 1024 || CacheDuration < TimeSpan.Zero)
            { throw new ArgumentOutOfRangeException(nameof(MaxPages), "TeamCity limits are outside their supported ranges."); }
            ReadContext context = new(new(_documents)); T result = await body(context, token).ConfigureAwait(false);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
                _documents = context.Cache; publish?.Invoke(result);
            }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new TeamCityException("The TeamCity request timed out."); }
        catch (HttpRequestException) { throw new TeamCityException("Could not connect to TeamCity. Check the server address and network connection."); }
        catch (XmlException) { throw new TeamCityException("TeamCity returned invalid or unsafe XML."); }
        catch (FormatException) { throw new TeamCityException("TeamCity returned invalid response metadata."); }
        catch (RegexMatchTimeoutException) { throw new TeamCityException("The TeamCity build ID regular expression exceeded its time limit."); }
        catch (IOException) { throw new TeamCityException("The TeamCity response could not be read completely."); }
        finally { _gate.Release(); }
    }

    public Uri? BuildPageUri(TeamCityBuild build)
    {
        // Reconstruct the canonical explicit build page: no server-supplied query or arbitrary URL is launched.
        if (!ulong.TryParse(build.Id, out ulong id) || id == 0) { return null; }
        if (build.WebUrl is not null && (!Uri.TryCreate(build.WebUrl, UriKind.Absolute, out Uri? web)
            || !TeamCitySettings.SameOrigin(ServerUri, web) || web.UserInfo.Length > 0
            || !Uri.UnescapeDataString(web.AbsolutePath).StartsWith(Uri.UnescapeDataString(ServerUri.AbsolutePath), StringComparison.Ordinal))) { return null; }
        return new(ServerUri, "viewLog.html?buildId=" + id + (_settings.LogAsGuest ? "&guest=1" : ""));
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; } _disposed = true; _lifetime.Cancel(); _http.Dispose();
        }
    }
    public ValueTask DisposeAsync()
    {
        Dispose(); lock (_sync) { return new(_drain ??= DrainAsync(_active.ToArray())); }
    }
    private async Task DrainAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception) { /* Owned operation callers retain their real failures; closing only drains them. */ }
        _lifetime.Dispose();
    }
}
