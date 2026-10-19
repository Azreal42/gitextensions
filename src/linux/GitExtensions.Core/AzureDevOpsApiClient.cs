using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

/// <summary>GET-only original Build REST operations. PAT Basic or anonymous; never GCM, OS credentials or cookies.</summary>
public sealed class AzureDevOpsApiClient : IAzureDevOpsClient
{
    private const string Properties = "sourceVersion,status,buildNumber,result,definition,_links,startTime,finishTime";
    private readonly AzureDevOpsSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _active = [];
    private Dictionary<Uri, ResponseData> _responses = [];
    private Dictionary<long, AzureDevOpsBuild> _finished = [];
    private long[]? _definitions;
    private DateTimeOffset? _watermark;
    private AzureDevOpsSnapshot? _snapshot;
    private bool _disposed;
    private Task? _drain;
    private sealed class BoundException : Exception { }
    private sealed record ResponseData(byte[] Bytes, string? ETag, string? Continuation);
    private sealed class Context(Dictionary<Uri, ResponseData> responses, Dictionary<long, AzureDevOpsBuild> finished, long[]? definitions, DateTimeOffset? watermark)
    {
        internal Dictionary<Uri, ResponseData> Responses { get; } = responses;
        internal Dictionary<long, AzureDevOpsBuild> Finished { get; set; } = finished;
        internal long[]? Definitions { get; set; } = definitions;
        internal DateTimeOffset? Watermark { get; set; } = watermark;
        internal int Requests { get; set; }
        internal bool Truncated { get; set; }
        internal List<string> Diagnostics { get; } = [];
    }
    public Uri ProjectUri { get; }
    public Uri ApiUri { get; }
    public string UniqueKey => ProjectUri.AbsoluteUri;
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleCacheDuration { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxPages { get; init; } = 100;
    public int MaxItems { get; init; } = 10000;
    public int MaxDefinitions { get; init; } = 1000;
    public int MaxRequests { get; init; } = 256;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    public AzureDevOpsApiClient(AzureDevOpsSettings settings, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); ProjectUri = settings.ProjectUri; ApiUri = new(ProjectUri, "_apis/");
        _settings = settings; _clock = clock ?? TimeProvider.System;
        HttpMessageHandler transport = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        for (HttpMessageHandler? current = transport; current is not null; current = current is DelegatingHandler d ? d.InnerHandler : null)
        {
            if (current is HttpClientHandler http) { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseDefaultCredentials = false; http.Credentials = null; http.DefaultProxyCredentials = null; }
            if (current is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; sockets.DefaultProxyCredentials = null; }
        }
        _http = new(transport) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<AzureDevOpsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
        => Owned((context, token) => ReadCoreAsync(context, force, token), cancellationToken, (accepted, context) =>
        { _finished = context.Finished; _definitions = context.Definitions; _watermark = context.Watermark; _snapshot = accepted; });
    public Task<string?> GetBuildDefinitionNameFromIdAsync(long buildId, CancellationToken cancellationToken = default)
    {
        if (buildId <= 0) { throw new ArgumentException("Enter a positive Azure DevOps build ID."); }
        return Owned<string?>(async (context, token) =>
        {
            using JsonDocument document = await JsonAsync(context, Endpoint("build/builds/" + buildId.ToString(CultureInfo.InvariantCulture), ("api-version", "2.0")), token).ConfigureAwait(false);
            JsonElement value = document.RootElement;
            if (Long(value, "id") != buildId) { throw Invalid("build identity"); }
            return value.TryGetProperty("definition", out JsonElement definition) ? Text(definition, "name") : null;
        }, cancellationToken);
    }
    private async Task<AzureDevOpsSnapshot> ReadCoreAsync(Context context, bool force, CancellationToken token)
    {
        if (!_settings.Active) { return new([], _clock.GetUtcNow()); }
        TimeSpan interval = _snapshot?.Builds.Any(b => b.IsRunning) == true ? CacheDuration : IdleCacheDuration;
        if (!force && _snapshot is not null && _clock.GetUtcNow() - _snapshot.ReadAt < interval) { return _snapshot; }
        if (!force && _snapshot?.Truncated == true) { context.Truncated = true; context.Diagnostics.AddRange(_snapshot.Diagnostics); }
        if (force) { context.Definitions = null; context.Finished = []; context.Watermark = null; }
        context.Definitions ??= await DefinitionsAsync(context, token).ConfigureAwait(false);
        if (context.Definitions.Length == 0)
        { return new([], _clock.GetUtcNow(), context.Truncated, context.Diagnostics.Append("No matching Azure DevOps build definition was found.")); }
        string ids = string.Join(',', context.Definitions.Select(n => n.ToString(CultureInfo.InvariantCulture)));
        List<AzureDevOpsBuild> running = [];
        try
        {
            List<JsonElement> runningValues = await ListAsync(context, Endpoint("build/builds", ("properties", Properties), ("definitions", ids),
                ("statusFilter", "cancelling,inProgress,none,notStarted,postponed"), ("api-version", "2.0")), MaxItems, token).ConfigureAwait(false);
            foreach (JsonElement value in runningValues)
            { token.ThrowIfCancellationRequested(); AzureDevOpsBuild? build = ParseBuild(value, context); if (build is not null) { running.Add(build); } }
            Uri finished = context.Watermark.HasValue
                ? Endpoint("build/builds", ("properties", Properties), ("definitions", ids), ("statusFilter", "completed"),
                    ("minTime", (context.Watermark.Value > DateTimeOffset.MinValue.AddSeconds(1) ? context.Watermark.Value.AddSeconds(-1) : DateTimeOffset.MinValue).UtcDateTime.ToString("s", CultureInfo.InvariantCulture)), ("api-version", "4.1"))
                : Endpoint("build/builds", ("properties", Properties), ("definitions", ids), ("statusFilter", "completed"), ("api-version", "2.0"));
            foreach (JsonElement value in await ListAsync(context, finished, Math.Max(0, MaxItems - runningValues.Count), token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                AzureDevOpsBuild? build = ParseBuild(value, context); if (build is null) { continue; }
                if (build.IsRunning) { throw Invalid("completed build status"); }
                context.Finished[build.BuildId] = build;
                if (build.FinishedAt.HasValue && (!context.Watermark.HasValue || build.FinishedAt > context.Watermark)) { context.Watermark = build.FinishedAt; }
            }
        }
        catch (BoundException) { context.Truncated = true; context.Diagnostics.Add("The Azure DevOps request bound was reached; build history is incomplete."); }
        if (context.Finished.Count + running.Count > MaxItems)
        {
            context.Truncated = true; context.Diagnostics.Add("The Azure DevOps retained history bound was reached; only recent results are retained.");
            context.Finished = context.Finished.Values.OrderByDescending(b => b.FinishedAt).Take(Math.Max(0, MaxItems - running.Count)).ToDictionary(b => b.BuildId);
        }
        return new(running.Concat(context.Finished.Values), _clock.GetUtcNow(), context.Truncated, context.Diagnostics);
    }
    private async Task<long[]> DefinitionsAsync(Context context, CancellationToken token)
    {
        List<(string, string)> query = [("api-version", "6.0")];
        if (!string.IsNullOrWhiteSpace(_settings.RepositoryName))
        {
            // A failed repository lookup must never silently remove the repository filter.
            using JsonDocument repository = await JsonAsync(context, Endpoint("git/repositories/" + Uri.EscapeDataString(_settings.RepositoryName), ("api-version", "6.0")), token).ConfigureAwait(false);
            string? id = Text(repository.RootElement, "id"); if (!Guid.TryParse(id, out Guid guid)) { throw Invalid("repository identity"); }
            query.Add(("repositoryId", guid.ToString())); query.Add(("repositoryType", "TfsGit"));
        }
        bool filtered = !string.IsNullOrWhiteSpace(_settings.BuildDefinitionFilter);
        List<(string, string)> first = [.. query]; if (filtered) { first.Add(("name", _settings.BuildDefinitionFilter)); }
        List<JsonElement> definitions = await ListAsync(context, Endpoint("build/definitions", first.ToArray()), MaxDefinitions, token).ConfigureAwait(false);
        bool regexFallback = filtered && definitions.Count == 0;
        if (regexFallback) { definitions = await ListAsync(context, Endpoint("build/definitions", query.ToArray()), MaxDefinitions, token).ConfigureAwait(false); }
        Regex regex = _settings.CreateBuildDefinitionRegex(); List<long> ids = [];
        foreach (JsonElement item in definitions)
        {
            token.ThrowIfCancellationRequested();
            long id = Long(item, "id") ?? 0; string? name = Text(item, "name"); if (id <= 0 || name is null) { throw Invalid("build definition"); }
            if (!regexFallback || regex.IsMatch(name)) { if (!ids.Contains(id)) { ids.Add(id); } }
        }
        return ids.ToArray();
    }
    private async Task<List<JsonElement>> ListAsync(Context context, Uri original, int limit, CancellationToken token)
    {
        List<JsonElement> items = []; HashSet<string> tokens = []; Uri uri = original;
        for (int page = 1; ; page++)
        {
            if (items.Count >= limit || page > MaxPages)
            { context.Truncated = true; context.Diagnostics.Add("The Azure DevOps page or item bound was reached; results are incomplete."); break; }
            ResponseData response;
            try { response = await ResponseAsync(context, uri, token).ConfigureAwait(false); }
            catch (BoundException) { context.Truncated = true; context.Diagnostics.Add("The Azure DevOps request bound was reached; results are incomplete."); break; }
            using JsonDocument document = Parse(response.Bytes); JsonElement root = document.RootElement;
            if (!root.TryGetProperty("value", out JsonElement values) || values.ValueKind != JsonValueKind.Array) { throw Invalid("result list"); }
            if (Long(root, "count") is long count && count != values.GetArrayLength()) { throw Invalid("result count"); }
            foreach (JsonElement value in values.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= limit) { context.Truncated = true; context.Diagnostics.Add("The Azure DevOps item bound was reached; results are incomplete."); break; }
                items.Add(value.Clone());
            }
            if (string.IsNullOrEmpty(response.Continuation)) { break; }
            if (!tokens.Add(response.Continuation)) { throw Invalid("cyclic continuation token"); }
            uri = new(original.AbsoluteUri + "&continuationToken=" + Uri.EscapeDataString(response.Continuation));
        }
        return items;
    }

    private AzureDevOpsBuild? ParseBuild(JsonElement value, Context context)
    {
        string raw = Text(value, "sourceVersion") ?? "";
        if (!AzureDevOpsBuild.IsCommitHash(raw)) { context.Diagnostics.Add("An Azure DevOps build with an invalid commit was ignored."); return null; }
        string? stateValue = Text(value, "status");
        string state = stateValue ?? "", result = Text(value, "result") ?? "", reason = Text(value, "reason") ?? "";
        string sha = raw, pr = "";
        if (reason == "validateShelveset" && Text(value, "parameters") is string parameters)
        {
            using JsonDocument parsed = JsonDocument.Parse(parameters, new JsonDocumentOptions { MaxDepth = 16 });
            string? head = Text(parsed.RootElement, "system.pullRequest.sourceCommitId");
            if (!string.IsNullOrEmpty(head)) { if (!AzureDevOpsBuild.IsCommitHash(head)) { throw Invalid("pull request commit"); } sha = head; }
            pr = Text(parsed.RootElement, "system.pullRequest.pullRequestId") ?? "";
            if (pr.Length > 0 && (!long.TryParse(pr, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n <= 0)) { throw Invalid("pull request identity"); }
        }
        long id = Long(value, "id") ?? 0;
        Uri? page = null;
        if (value.TryGetProperty("_links", out JsonElement links) && links.TryGetProperty("web", out JsonElement web)
            && AzureDevOpsProjectUrls.TryParseBuildUrl(Text(web, "href"), out AzureDevOpsBuildUrl? parsedPage)
            && AzureDevOpsSettings.SameProject(ProjectUri, parsedPage.ProjectUri))
        {
            if (id == 0) { id = parsedPage.BuildId; }
            if (id == parsedPage.BuildId) { page = new Uri(Text(web, "href")!); }
        }
        if (id <= 0) { throw Invalid("build identity"); }
        if (!value.TryGetProperty("definition", out JsonElement definition)) { throw Invalid("build definition"); }
        long definitionId = Long(definition, "id") ?? 0; if (definitionId <= 0) { throw Invalid("build definition identity"); }
        DateTimeOffset? started = Date(value, "startTime"), finished = Date(value, "finishTime"); long? duration = null; string durationText = "";
        if (stateValue is not null && state is not ("none" or "notStarted" or "postponed") && started.HasValue)
        {
            DateTimeOffset? end = state == "inProgress" ? _clock.GetUtcNow() : finished;
            if (end.HasValue)
            {
                duration = (long)(end.Value - started.Value).TotalMilliseconds;
                if (duration < 0) { throw Invalid("build duration"); } durationText = AzureDevOpsBuild.FormatDuration(duration.Value);
            }
            else { durationText = "???"; }
        }
        Uri? prPage = null;
        if (pr.Length > 0 && value.TryGetProperty("repository", out JsonElement repository) && Text(repository, "url") is string repoUrl
            && SafeProjectWebUri(repoUrl, out Uri? repoUri)) { prPage = new(repoUri.AbsoluteUri.TrimEnd('/') + "/pullrequest/" + pr); }
        return new()
        {
            BuildId = id, BuildNumber = Text(value, "buildNumber") ?? "", DefinitionId = definitionId, DefinitionName = Text(definition, "name") ?? "",
            SourceVersion = raw, CommitHash = sha, RawStatus = state, Result = result, Reason = reason, Status = AzureDevOpsBuild.Map(state, result),
            StartedAt = started, FinishedAt = finished, DurationMilliseconds = duration, DurationText = durationText, PageUri = page, PullRequestId = pr, PullRequestUri = prPage
        };
    }
    public Uri? BuildPageUri(AzureDevOpsBuild build)
        => build.PageUri is { IsAbsoluteUri: true } && AzureDevOpsProjectUrls.TryParseBuildUrl(build.PageUri.AbsoluteUri, out AzureDevOpsBuildUrl? parsed)
            && parsed.BuildId == build.BuildId && AzureDevOpsSettings.SameProject(ProjectUri, parsed.ProjectUri) ? build.PageUri : null;
    private bool SafeProjectWebUri(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Scheme != ProjectUri.Scheme || uri.IdnHost != ProjectUri.IdnHost || uri.Port != ProjectUri.Port) { return false; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath), prefix = Uri.UnescapeDataString(ProjectUri.AbsolutePath);
        return path.StartsWith(prefix, StringComparison.Ordinal) && !path.Contains('\\') && !path.Split('/').Any(p => p is "." or "..");
    }
    private Uri Endpoint(string path, params (string Key, string Value)[] query)
        => new(ApiUri, path + "?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    private static string? Text(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out JsonElement item) || item.ValueKind == JsonValueKind.Null) { return null; }
        return item.ValueKind switch { JsonValueKind.String => item.GetString(), JsonValueKind.Number => item.ToString(), _ => throw Invalid("scalar") };
    }
    private static long? Long(JsonElement value, string key)
    { string? text = Text(value, key); return text is null ? null : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : throw Invalid("numerical identity"); }
    private static DateTimeOffset? Date(JsonElement value, string key)
    { string? text = Text(value, key); return text is null ? null : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? date : throw Invalid("date"); }
    private static AzureDevOpsException Invalid(string kind) => new("Azure DevOps returned an invalid " + kind + ".");
    private static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    private async Task<JsonDocument> JsonAsync(Context context, Uri uri, CancellationToken token)
        => Parse((await ResponseAsync(context, uri, token).ConfigureAwait(false)).Bytes);
    private bool InApiScope(Uri uri) => SafeProjectWebUri(uri.GetLeftPart(UriPartial.Path), out Uri? _) && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && Uri.UnescapeDataString(uri.AbsolutePath).StartsWith(Uri.UnescapeDataString(ApiUri.AbsolutePath), StringComparison.Ordinal);

    private async Task<ResponseData> ResponseAsync(Context context, Uri original, CancellationToken token)
    {
        Uri uri = original;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(RequestTimeout); CancellationToken requestToken = deadline.Token;
        for (int redirects = 0; ; redirects++)
        {
            requestToken.ThrowIfCancellationRequested(); if (++context.Requests > MaxRequests) { throw new BoundException(); }
            if (!InApiScope(uri)) { throw new AzureDevOpsException("The request is outside the configured Azure DevOps project API."); }
            context.Responses.TryGetValue(uri, out ResponseData? cached);
            using HttpRequestMessage request = new(HttpMethod.Get, uri); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha");
            if (_settings.ApiToken.Length > 0) { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + _settings.ApiToken))); }
            if (cached?.ETag is string tag && EntityTagHeaderValue.TryParse(tag, out EntityTagHeaderValue? etag)) { request.Headers.IfNoneMatch.Add(etag); }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false); requestToken.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is null || !Uri.TryCreate(uri, response.Headers.Location, out Uri? next) || !InApiScope(next)
                    || next.AbsolutePath != original.AbsolutePath || next.Query != original.Query)
                { throw new AzureDevOpsException("Azure DevOps redirected outside the requested project resource or too many times; credentials were not forwarded."); }
                uri = next; continue;
            }
            if (response.StatusCode == HttpStatusCode.NotModified) { return cached ?? throw Invalid("304 response without a cache entry"); }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new AzureDevOpsException("Azure DevOps authentication failed. Configure a valid PAT in Build server integration; GCM and Windows SSO are not used.", 401); }
            if (response.StatusCode == HttpStatusCode.Forbidden) { throw new AzureDevOpsException("Azure DevOps denied access. Check the configured PAT and its build/project permissions.", 403); }
            if (response.StatusCode == HttpStatusCode.NotFound) { throw new AzureDevOpsException("The configured Azure DevOps project, repository or build was not found or is private.", 404); }
            if ((int)response.StatusCode == 429) { throw new AzureDevOpsException("Azure DevOps rate-limited the request. Try again later.", 429); }
            if (!response.IsSuccessStatusCode) { throw new AzureDevOpsException("Azure DevOps returned HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode); }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) { throw new AzureDevOpsException("The Azure DevOps response exceeded the byte bound."); }
            await using Stream stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false); using MemoryStream bytes = new(); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
            { if (bytes.Length + read > MaxResponseBytes) { throw new AzureDevOpsException("The Azure DevOps response exceeded the byte bound."); } bytes.Write(buffer, 0, read); }
            string? continuation = null;
            if (response.Headers.TryGetValues("x-ms-continuationtoken", out IEnumerable<string>? values))
            {
                string[] tokens = values.ToArray(); if (tokens.Length != 1 || tokens[0].Length > 2048 || tokens[0].Any(char.IsControl)) { throw Invalid("continuation header"); } continuation = tokens[0];
            }
            requestToken.ThrowIfCancellationRequested(); ResponseData result = new(bytes.ToArray(), response.Headers.ETag?.ToString(), continuation);
            context.Responses.Remove(uri);
            if (result.ETag is not null)
            {
                context.Responses[uri] = result;
                while (context.Responses.Count > 256 || context.Responses.Values.Sum(r => (long)r.Bytes.Length) > 16 * 1024 * 1024) { context.Responses.Remove(context.Responses.Keys.First()); }
            }
            return result;
        }
    }
    private Task<T> Owned<T>(Func<Context, CancellationToken, Task<T>> body, CancellationToken caller, Action<T, Context>? publish = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Task<T> task = RunAsync(body, caller, publish); _active.Add(task); _ = RetireAsync(task); return task;
        }
    }
    private async Task RetireAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception) { } finally { lock (_sync) { _active.Remove(task); } } }
    private async Task<T> RunAsync<T>(Func<Context, CancellationToken, Task<T>> body, CancellationToken caller, Action<T, Context>? publish)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token); CancellationToken token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (MaxPages is < 1 or > 10000 || MaxItems is < 1 or > 100000 || MaxDefinitions is < 1 or > 10000 || MaxRequests is < 1 or > 20000
                || MaxResponseBytes is < 1024 or > 64 * 1024 * 1024 || CacheDuration < TimeSpan.Zero || IdleCacheDuration < TimeSpan.Zero
                || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10)) { throw new AzureDevOpsException("The Azure DevOps reader bounds are invalid."); }
            Context context = new(new(_responses), new(_finished), _definitions, _watermark);
            T result = await body(context, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); _responses = context.Responses; publish?.Invoke(result, context); return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AzureDevOpsException("The Azure DevOps request timed out."); }
        catch (BoundException) { throw new AzureDevOpsException("The Azure DevOps request bound was reached."); }
        catch (RegexMatchTimeoutException) { throw new AzureDevOpsException("The Azure DevOps build definition filter exceeded its time bound."); }
        catch (Exception error) when ((error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException) && error is not AzureDevOpsException)
        { throw new AzureDevOpsException("Azure DevOps returned an invalid response or configuration."); }
        catch (HttpRequestException) { throw new AzureDevOpsException("The Azure DevOps service could not be reached."); }
        catch (IOException) { throw new AzureDevOpsException("The Azure DevOps response could not be read."); }
        finally { _gate.Release(); }
    }
    public ValueTask DisposeAsync()
    { lock (_sync) { if (_drain is not null) { return new(_drain); } _disposed = true; _lifetime.Cancel(); _drain = DrainAsync(_active.ToArray()); return new(_drain); } }
    private async Task DrainAsync(Task[] tasks)
    { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { } _http.Dispose(); _gate.Dispose(); _lifetime.Dispose(); }
}
