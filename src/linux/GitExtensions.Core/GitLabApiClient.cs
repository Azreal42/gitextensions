using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GitExtensions.Linux;

public sealed class GitLabException(string message, int? statusCode = null) : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>GET-only GitLab v4 reader. No OS credentials, implicit browser login, writes or automatic web opening.</summary>
public sealed class GitLabApiClient : IGitLabClient
{
    public const int PageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 32 };
    private readonly GitLabSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _active = [];
    private Dictionary<Uri, ResponseData> _responses = [];
    private GitLabSnapshot? _snapshot;
    private bool _disposed;
    private Task? _drain;
    private sealed class RequestBoundException : Exception { }
    private sealed record ResponseData(byte[] Bytes, string? ETag, IReadOnlyDictionary<string, string[]> Headers, Uri Uri);
    private sealed class ReadContext(Dictionary<Uri, ResponseData> cache)
    {
        internal Dictionary<Uri, ResponseData> Cache { get; } = cache;
        internal int Requests { get; set; }
    }
    public Uri InstanceUri { get; }
    public Uri ApiUri { get; }
    public string UniqueKey => InstanceUri.AbsoluteUri;
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxPages { get; init; } = 100;
    public int MaxItems { get; init; } = 10000;
    public int MaxRequests { get; init; } = 256;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    public GitLabApiClient(GitLabSettings settings, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(requireProject: false);
        _settings = settings; _clock = clock ?? TimeProvider.System; InstanceUri = settings.InstanceUri; ApiUri = new(InstanceUri, "api/v4/");
        HttpMessageHandler transport = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        for (HttpMessageHandler? current = transport; current is not null; current = current is DelegatingHandler d ? d.InnerHandler : null)
        {
            if (current is HttpClientHandler http) { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseDefaultCredentials = false; http.Credentials = null; http.DefaultProxyCredentials = null; }
            if (current is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; sockets.DefaultProxyCredentials = null; }
        }
        _http = new(transport) { Timeout = Timeout.InfiniteTimeSpan }; // The per-request deadline below also covers response-body reads.
    }

    public Task<GitLabProject> GetProjectAsync(string projectNamespace, string projectName, CancellationToken cancellationToken = default)
    {
        ValidatePath(projectNamespace, allowNested: true); ValidatePath(projectName, allowNested: false);
        return Owned<GitLabProject>(async (context, token) =>
        {
            string path = projectNamespace + "/" + projectName;
            ResponseData response = await GetResponseAsync(context, new(ApiUri, "projects/" + Uri.EscapeDataString(path)), token).ConfigureAwait(false);
            GitLabProject? project = JsonSerializer.Deserialize<GitLabProject>(response.Bytes, JsonOptions);
            if (project is null || project.Id <= 0) { throw new GitLabException("GitLab returned an invalid project response."); }
            if (project.WebUrl is not null && !SafeWebUri(project.WebUrl, out _)) { throw new GitLabException("GitLab returned a project URL outside the configured instance."); }
            return project;
        }, cancellationToken);
    }

    public Task<GitLabSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default) => Owned<GitLabSnapshot>(async (context, token) =>
    {
        if (!_settings.Active) { return new([], _clock.GetUtcNow()); }
        _settings.Validate();
        if (!force && _snapshot is not null && _clock.GetUtcNow() - _snapshot.ReadAt < CacheDuration) { return _snapshot; }
        List<GitLabPipeline> pipelines = []; List<string> diagnostics = []; bool truncated = false, stop = false;
        // Retain live statuses before a large finished history consumes the global item/request budget.
        foreach (string scope in new[] { "running", "finished" })
        {
            int page = 1, pagesRead = 0;
            while (!stop)
            {
                token.ThrowIfCancellationRequested();
                if (pagesRead >= MaxPages || _settings.PagesLimit > 0 && pagesRead >= _settings.PagesLimit)
                {
                    truncated = true;
                    diagnostics.Add(pagesRead >= MaxPages ? "The GitLab safety page bound was reached; history is incomplete." : "The configured GitLab pages limit was reached; history is incomplete.");
                    break;
                }
                Uri endpoint = PipelineUri(scope, page); ResponseData response;
                try { response = await GetResponseAsync(context, endpoint, token, pipelineScope: scope, pipelinePage: page).ConfigureAwait(false); }
                catch (RequestBoundException)
                { truncated = true; diagnostics.Add("The GitLab safety request bound was reached; history is incomplete."); stop = true; break; }
                List<GitLabPipeline?>? values = JsonSerializer.Deserialize<List<GitLabPipeline?>>(response.Bytes, JsonOptions);
                if (values is null) { throw new GitLabException("GitLab returned an invalid pipeline list."); }
                foreach (GitLabPipeline? pipeline in values)
                {
                    if (pipeline is null || pipeline.Id <= 0 || !GitLabPipeline.IsCommitHash(pipeline.Sha) || string.IsNullOrEmpty(pipeline.Status)
                        || pipeline.ProjectId < 0 || pipeline.ProjectId > 0 && pipeline.ProjectId != _settings.ProjectId)
                    { throw new GitLabException("GitLab returned an invalid pipeline identity, revision or status."); }
                    if (pipelines.Count >= MaxItems)
                    { truncated = true; diagnostics.Add("The GitLab safety item bound was reached; history is incomplete."); stop = true; break; }
                    pipelines.Add(pipeline.ProjectId == 0 ? pipeline with { ProjectId = _settings.ProjectId } : pipeline);
                }
                if (stop) { break; }
                pagesRead++;
                int? next = NextPage(response, scope, page, values.Count);
                if (next is null) { break; }
                page = next.Value;
            }
            if (stop) { break; }
        }
        return new(pipelines, _clock.GetUtcNow(), truncated, diagnostics.Distinct(StringComparer.Ordinal));
    }, cancellationToken, accepted => _snapshot = accepted);

    private Uri PipelineUri(string scope, int page) => new(ApiUri, "projects/" + _settings.ProjectId.ToString(CultureInfo.InvariantCulture)
        + "/pipelines?scope=" + scope + "&page=" + page.ToString(CultureInfo.InvariantCulture) + "&per_page=100");
    private bool InApiScope(Uri uri)
    {
        if (!GitLabSettings.SameOrigin(ApiUri, uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) { return false; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.StartsWith(Uri.UnescapeDataString(ApiUri.AbsolutePath), StringComparison.Ordinal) && !path.Contains('\\') && !path.Split('/').Any(x => x is "." or "..");
    }
    private Uri Follow(Uri current, string target, string? pipelineScope, int pipelinePage)
    {
        if (!Uri.TryCreate(current, target, out Uri? next) || !InApiScope(next))
        { throw new GitLabException("GitLab redirected outside the configured API origin/base path; the token was not forwarded."); }
        if (pipelineScope is not null) { ValidatePipelineUri(next, pipelineScope, pipelinePage); }
        return next;
    }
    private int ValidatePipelineUri(Uri uri, string scope, int? requiredPage = null)
    {
        Uri expected = PipelineUri(scope, 1);
        if (!InApiScope(uri) || !string.Equals(uri.AbsolutePath.TrimEnd('/'), expected.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal))
        { throw new GitLabException("GitLab pagination or redirect changed the configured project pipeline endpoint."); }
        Dictionary<string, string> query = [];
        foreach (string item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2); string key = Uri.UnescapeDataString(pair[0]);
            if (pair.Length != 2 || !query.TryAdd(key, Uri.UnescapeDataString(pair[1]))) { throw new GitLabException("GitLab returned invalid pagination parameters."); }
        }
        if (query.Count != 3 || !query.TryGetValue("scope", out string? actualScope) || actualScope != scope
            || !query.TryGetValue("per_page", out string? size) || size != "100" || !query.TryGetValue("page", out string? page)
            || !int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0 || requiredPage.HasValue && n != requiredPage)
        { throw new GitLabException("GitLab changed pagination scope, page or page size."); }
        return n;
    }
    private static string? Header(ResponseData data, string key, out bool exists)
    {
        exists = data.Headers.TryGetValue(key, out string[]? values);
        if (!exists) { return null; }
        if (values is null || values.Length != 1) { throw new GitLabException("GitLab returned ambiguous pagination headers."); }
        return values[0].Trim();
    }
    private static int? NumberHeader(ResponseData data, string key, out bool exists)
    {
        string? value = Header(data, key, out exists);
        if (string.IsNullOrEmpty(value)) { return null; }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0)
        { throw new GitLabException("GitLab returned invalid pagination headers."); }
        return n;
    }
    private int? NextPage(ResponseData data, string scope, int current, int count)
    {
        int? actual = NumberHeader(data, "X-Page", out _), perPage = NumberHeader(data, "X-Per-Page", out _);
        int? total = NumberHeader(data, "X-Total-Pages", out _), next = NumberHeader(data, "X-Next-Page", out bool hasNext);
        if (count > PageSize || actual.HasValue && actual != current || perPage.HasValue && (perPage is <= 0 or > PageSize || count > perPage)
            || total.HasValue && total < current && !(total == 0 && current == 1 && count == 0))
        { throw new GitLabException("GitLab returned inconsistent pagination metadata."); }
        int? linkPage = null;
        if (data.Headers.TryGetValue("Link", out string[]? links))
        {
            foreach (string part in links.SelectMany(x => x.Split(',')))
            {
                string[] sections = part.Split(';'); bool isNext = sections.Skip(1).Any(p => p.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase) || p.Trim().Equals("rel=next", StringComparison.OrdinalIgnoreCase));
                if (!isNext) { continue; }
                string address = sections[0].Trim();
                if (linkPage.HasValue || !address.StartsWith('<') || !address.EndsWith('>')) { throw new GitLabException("GitLab returned an invalid next-page link."); }
                Uri uri = Follow(data.Uri, address[1..^1], pipelineScope: null, pipelinePage: 0);
                linkPage = ValidatePipelineUri(uri, scope);
            }
        }
        int? candidate;
        if (hasNext) { candidate = next is > 0 ? next : null; }
        else if (linkPage.HasValue) { candidate = linkPage; }
        else if (total.HasValue) { candidate = current < total ? checked(current + 1) : null; }
        else { candidate = count >= (perPage ?? PageSize) ? checked(current + 1) : null; }
        if (linkPage.HasValue && candidate != linkPage || total.HasValue && (current < total && candidate is null || current >= total && candidate is not null)
            || candidate.HasValue && candidate != current + 1)
        { throw new GitLabException("GitLab pagination is contradictory, cyclic or skips a page."); }
        return candidate;
    }

    private async Task<ResponseData> GetResponseAsync(ReadContext context, Uri original, CancellationToken token, string? pipelineScope = null, int pipelinePage = 0)
    {
        Uri uri = original;
        for (int redirects = 0; ; redirects++)
        {
            token.ThrowIfCancellationRequested();
            if (context.Requests >= MaxRequests) { throw new RequestBoundException(); }
            context.Requests++;
            if (!InApiScope(uri)) { throw new GitLabException("The request is outside the configured GitLab API endpoint."); }
            context.Cache.TryGetValue(uri, out ResponseData? cached);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(RequestTimeout); CancellationToken requestToken = deadline.Token;
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_settings.ApiToken.Length > 0) { request.Headers.Add("PRIVATE-TOKEN", _settings.ApiToken); }
            if (cached?.ETag is string value && EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? etag)) { request.Headers.IfNoneMatch.Add(etag); }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            requestToken.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is null) { throw new GitLabException("GitLab redirected repeatedly. Configure its final instance URL."); }
                uri = Follow(uri, response.Headers.Location.OriginalString, pipelineScope, pipelinePage); continue;
            }
            if (response.StatusCode == HttpStatusCode.NotModified) { return cached ?? throw new GitLabException("GitLab returned 304 without a matching cached response."); }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new GitLabException("GitLab authentication failed. Check the configured API token.", 401); }
            if (response.StatusCode == HttpStatusCode.Forbidden) { throw new GitLabException("GitLab denied access. Check project access and API token permissions.", 403); }
            if (response.StatusCode == HttpStatusCode.NotFound) { throw new GitLabException("The GitLab project was not found or is private. Check its ID, namespace and token access.", 404); }
            if ((int)response.StatusCode == 429) { throw new GitLabException("GitLab rate-limited the request. Try again later.", 429); }
            if (!response.IsSuccessStatusCode) { throw new GitLabException("GitLab returned HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode); }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) { throw new GitLabException("The GitLab response exceeded the configured byte bound."); }
            await using Stream stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using MemoryStream bytes = new(); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaxResponseBytes) { throw new GitLabException("The GitLab response exceeded the configured byte bound."); }
                bytes.Write(buffer, 0, read);
            }
            Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "X-Page", "X-Per-Page", "X-Total-Pages", "X-Next-Page", "Link" })
            { if (response.Headers.TryGetValues(key, out IEnumerable<string>? values)) { headers[key] = values.ToArray(); } }
            ResponseData result = new(bytes.ToArray(), response.Headers.ETag?.ToString(), headers, uri);
            requestToken.ThrowIfCancellationRequested();
            context.Cache.Remove(uri);
            if (result.ETag is not null)
            {
                context.Cache[uri] = result;
                while (context.Cache.Count > 256 || context.Cache.Values.Sum(r => (long)r.Bytes.Length) > 16 * 1024 * 1024) { context.Cache.Remove(context.Cache.Keys.First()); }
            }
            return result;
        }
    }

    public Uri? BuildPageUri(GitLabPipeline pipeline)
    {
        if (pipeline.Id <= 0 || !SafeWebUri(pipeline.WebUrl, out Uri? uri)) { return null; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/'), id = pipeline.Id.ToString(CultureInfo.InvariantCulture);
        return path.EndsWith("/-/pipelines/" + id, StringComparison.Ordinal) || path.EndsWith("/pipelines/" + id, StringComparison.Ordinal) ? uri : null;
    }
    private bool SafeWebUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !GitLabSettings.SameOrigin(InstanceUri, uri) || uri.UserInfo.Length > 0) { return false; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.StartsWith(Uri.UnescapeDataString(InstanceUri.AbsolutePath), StringComparison.Ordinal) && !path.Contains('\\') && !path.Split('/').Any(p => p is "." or "..");
    }
    private static void ValidatePath(string value, bool allowNested)
    {
        if (string.IsNullOrWhiteSpace(value) || !allowNested && value.Contains('/')
            || value.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "\\?#".Contains(c))))
        { throw new ArgumentException("Enter a valid GitLab project namespace and name."); }
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
            if (MaxPages is < 1 or > 10000 || MaxItems is < 1 or > 100000 || MaxRequests is < 1 or > 20000 || MaxResponseBytes is < 1024 or > 64 * 1024 * 1024
                || CacheDuration < TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            { throw new ArgumentOutOfRangeException(nameof(MaxPages), "GitLab safety bounds are outside their supported ranges."); }
            ReadContext context = new(new(_responses)); T result = await body(context, token).ConfigureAwait(false);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
                _responses = context.Cache; publish?.Invoke(result);
            }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (token.IsCancellationRequested && error is HttpRequestException or IOException or ObjectDisposedException)
        { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) { throw new GitLabException("The GitLab request timed out."); }
        catch (RequestBoundException) { throw new GitLabException("The GitLab safety request bound was reached."); }
        catch (HttpRequestException) { throw new GitLabException("Could not connect to GitLab. Check the instance URL and network connection."); }
        catch (JsonException) { throw new GitLabException("GitLab returned invalid or unsafe JSON."); }
        catch (FormatException) { throw new GitLabException("GitLab returned invalid response metadata."); }
        catch (IOException) { throw new GitLabException("The GitLab response could not be read completely."); }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) { return; } _disposed = true; _lifetime.Cancel(); _http.Dispose(); }
    }
    public ValueTask DisposeAsync()
    {
        Dispose(); lock (_sync) { return new(_drain ??= DrainAsync(_active.ToArray())); }
    }
    private async Task DrainAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception) { /* Owned callers retain real errors; disposal waits for every operation without rethrowing their failures. */ }
        _lifetime.Dispose();
    }
}
