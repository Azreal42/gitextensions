using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GitExtensions.Linux;

/// <summary>GET-only AppVeyor reader. Explicit token, fixed service, bounded responses, and owned cancellable operations.</summary>
public sealed class AppVeyorApiClient : IAppVeyorClient
{
    public static Uri WebsiteUri { get; } = new("https://ci.appveyor.com/");
    public const int HistorySize = 25;
    private readonly AppVeyorSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Func<string, bool> _admitCommit;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<Task> _active = [];
    private Dictionary<Uri, ResponseData> _responses = [];
    private AppVeyorSnapshot? _snapshot;
    private bool _disposed;
    private Task? _drain;
    private sealed record ResponseData(byte[] Bytes, string? ETag);
    private sealed class RequestBoundException : Exception { }
    private sealed class ReadContext(Dictionary<Uri, ResponseData> responses)
    {
        internal Dictionary<Uri, ResponseData> Responses { get; } = responses;
        internal int Requests { get; set; }
    }
    public string UniqueKey => WebsiteUri.Host;
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxProjects { get; init; } = 100;
    public int MaxItems { get; init; } = 2500;
    public int MaxRequests { get; init; } = 256;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    public AppVeyorApiClient(AppVeyorSettings settings, HttpMessageHandler? handler = null, TimeProvider? clock = null,
        Func<string, bool>? admitCommit = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        if (settings.ProjectName.Any(c => c is '{' or '}'))
        { throw new ArgumentException("Resolve AppVeyor project variables from the repository before creating its API client."); }
        _settings = settings; _clock = clock ?? TimeProvider.System; _admitCommit = admitCommit ?? (_ => true);
        HttpMessageHandler transport = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        for (HttpMessageHandler? current = transport; current is not null; current = current is DelegatingHandler d ? d.InnerHandler : null)
        {
            if (current is HttpClientHandler http) { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseDefaultCredentials = false; http.Credentials = null; http.DefaultProxyCredentials = null; }
            if (current is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; sockets.DefaultProxyCredentials = null; }
        }
        _http = new(transport) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<AppVeyorSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<AppVeyorSnapshot> task = RunAsync(force, cancellationToken); _active.Add(task); _ = RetireAsync(task); return task;
        }
    }
    private async Task RetireAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception) { /* The caller owns the error. */ } finally { lock (_sync) { _active.Remove(task); } } }

    private async Task<AppVeyorSnapshot> RunAsync(bool force, CancellationToken caller)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        CancellationToken token = linked.Token; await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ValidateBounds(); token.ThrowIfCancellationRequested();
            ReadContext context = new(new(_responses));
            AppVeyorSnapshot result = await ReadCoreAsync(context, force, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); _responses = context.Responses; _snapshot = result; return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AppVeyorException("The AppVeyor request timed out."); }
        catch (Exception error) when ((error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            && error is not AppVeyorException)
        { throw new AppVeyorException("AppVeyor returned an invalid build response or configuration."); }
        catch (HttpRequestException) { throw new AppVeyorException("The AppVeyor service could not be reached."); }
        catch (IOException) { throw new AppVeyorException("The AppVeyor response could not be read."); }
        finally { _gate.Release(); }
    }
    private void ValidateBounds()
    {
        if (MaxProjects is < 1 or > 10000 || MaxItems is < 1 or > 100000 || MaxRequests is < 1 or > 20000
            || MaxResponseBytes is < 1024 or > 64 * 1024 * 1024 || CacheDuration < TimeSpan.Zero
            || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
        { throw new AppVeyorException("The AppVeyor reader bounds are invalid."); }
    }

    private async Task<AppVeyorSnapshot> ReadCoreAsync(ReadContext context, bool force, CancellationToken token)
    {
        if (!_settings.Active) { return new([], _clock.GetUtcNow()); }
        if (!force && _snapshot is not null)
        {
            // Windows discovers history at initialization/explicit refresh, then only follows already-running builds.
            TimeSpan runningCache = CacheDuration <= TimeSpan.FromSeconds(10) ? CacheDuration : TimeSpan.FromSeconds(10);
            if (!_snapshot.Builds.Any(b => b.IsRunning) || _clock.GetUtcNow() - _snapshot.ReadAt < runningCache) { return _snapshot; }
            List<AppVeyorBuild> pending = _snapshot.Builds.ToList();
            List<string> pendingDiagnostics = _snapshot.Diagnostics.ToList(); bool incomplete = _snapshot.Truncated;
            try
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (pending[i].IsRunning) { pending[i] = await ReadDetailsAsync(context, pending[i], token).ConfigureAwait(false); }
                }
            }
            catch (RequestBoundException)
            { incomplete = true; pendingDiagnostics.Add("The AppVeyor request bound was reached; running build details are incomplete."); }
            return new(pending, _clock.GetUtcNow(), incomplete, pendingDiagnostics);
        }
        List<string> diagnostics = []; bool truncated = false;
        List<string> projects = AppVeyorProjects.Resolve(_settings.ProjectName, _settings.AccountName).ToList();
        List<AppVeyorBuild> builds = [];
        try
        {
            if (projects.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(_settings.AccountName) || string.IsNullOrWhiteSpace(_settings.AccountToken))
                { throw new AppVeyorException("Configure an AppVeyor account/project, or an account name and API token for project discovery."); }
                string path = _settings.AccountToken.StartsWith("v2.", StringComparison.Ordinal)
                    ? "api/account/" + Uri.EscapeDataString(_settings.AccountName) + "/projects/" : "api/projects/";
                using JsonDocument list = await JsonAsync(context, new(WebsiteUri, path), token).ConfigureAwait(false);
                if (list.RootElement.ValueKind != JsonValueKind.Array) { throw InvalidResponse(); }
                foreach (JsonElement item in list.RootElement.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested(); string slug = Text(item, "slug") ?? throw InvalidResponse();
                    string id = _settings.AccountName + "/" + slug; AppVeyorProjects.ValidateId(id);
                    if (!projects.Contains(id, StringComparer.Ordinal)) { projects.Add(id); }
                    if (projects.Count > MaxProjects) { break; }
                }
            }
            if (projects.Count > MaxProjects) { projects = projects.Take(MaxProjects).ToList(); truncated = true; diagnostics.Add("The AppVeyor project bound was reached; history is incomplete."); }
            foreach (string project in projects)
            {
                token.ThrowIfCancellationRequested();
                using JsonDocument history = await JsonAsync(context, HistoryUri(project), token).ConfigureAwait(false);
                JsonElement root = history.RootElement;
                if (!root.TryGetProperty("builds", out JsonElement values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > HistorySize) { throw InvalidResponse(); }
                if (values.GetArrayLength() == HistorySize) { truncated = true; diagnostics.Add("AppVeyor history is limited to the latest 25 builds per project."); }
                string? repositoryName = root.TryGetProperty("project", out JsonElement metadata) ? Text(metadata, "repositoryName") : null;
                string? repositoryType = metadata.ValueKind == JsonValueKind.Object ? Text(metadata, "repositoryType") : null;
                foreach (JsonElement item in values.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    AppVeyorBuild? build = ParseBuild(item, project, repositoryType, repositoryName, diagnostics);
                    if (build is null) { continue; }
                    if (builds.Count >= MaxItems) { truncated = true; diagnostics.Add("The AppVeyor item bound was reached; history is incomplete."); break; }
                    builds.Add(build);
                }
                if (builds.Count >= MaxItems) { if (projects.IndexOf(project) + 1 < projects.Count) { truncated = true; diagnostics.Add("The AppVeyor item bound was reached; history is incomplete."); } break; }
            }
            // A running build must still finish even if it has moved outside the latest history window.
            foreach (AppVeyorBuild previous in _snapshot?.Builds.Where(b => b.IsRunning) ?? [])
            {
                if (builds.Any(b => b.ProjectId == previous.ProjectId && b.BuildId == previous.BuildId) || !_admitCommit(previous.CommitHash)) { continue; }
                if (builds.Count >= MaxItems) { truncated = true; diagnostics.Add("The AppVeyor item bound was reached; history is incomplete."); break; }
                builds.Add(previous);
            }
            List<AppVeyorBuild> selected = new AppVeyorSnapshot(builds, _clock.GetUtcNow()).Builds.ToList();
            builds = selected; // Preserve already completed detail updates if the explicit request bound is reached.
            for (int i = 0; i < selected.Count; i++)
            {
                AppVeyorBuild build = selected[i];
                AppVeyorBuild? previous = _snapshot?.Builds.FirstOrDefault(b => b.ProjectId == build.ProjectId && b.BuildId == build.BuildId);
                if (previous is not null) { build = build with { ProgressCounter = previous.ProgressCounter, Tests = previous.Tests }; }
                if (build.IsRunning || previous?.IsRunning == true || _settings.LoadTestsResults && (build.Status is AppVeyorBuildStatus.Success or AppVeyorBuildStatus.Failure))
                { build = await ReadDetailsAsync(context, build, token).ConfigureAwait(false); }
                selected[i] = build;
            }
        }
        catch (RequestBoundException)
        { truncated = true; diagnostics.Add("The AppVeyor request bound was reached; history or build details are incomplete."); }
        return new(builds, _clock.GetUtcNow(), truncated, diagnostics);
    }

    private AppVeyorBuild? ParseBuild(JsonElement item, string project, string? repositoryType, string? repositoryName, List<string> diagnostics)
    {
        string? sha = Text(item, "pullRequestHeadCommitId") ?? Text(item, "commitId");
        if (!AppVeyorBuild.IsCommitHash(sha)) { diagnostics.Add("An AppVeyor build with an invalid or missing commit was ignored."); return null; }
        if (!_admitCommit(sha!)) { return null; }
        long id = PositiveId(item, "buildId"); string version = Version(item); string rawStatus = Text(item, "status") ?? "";
        AppVeyorBuildStatus status = AppVeyorBuild.ParseStatus(rawStatus);
        DateTimeOffset? started = Date(item, "started"), created = Date(item, "created"), updated = Date(item, "updated");
        string? pr = Text(item, "pullRequestId");
        if (pr is not null && (!long.TryParse(pr, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n <= 0)) { throw InvalidResponse(); }
        return new()
        {
            BuildId = id, Version = version, ProjectId = project, CommitHash = sha!, Branch = Text(item, "branch") ?? "",
            RawStatus = rawStatus, Status = status, StartedAt = started, CreatedAt = created, UpdatedAt = updated,
            DurationMilliseconds = status is AppVeyorBuildStatus.Success or AppVeyorBuildStatus.Failure ? Duration(started, created, updated) : null,
            PullRequestText = pr is null ? "" : "PR#" + pr, PullRequestTitle = Text(item, "pullRequestName") ?? "",
            PullRequestUri = PullRequestUri(repositoryType, repositoryName, pr), PageUri = Page(project, version)
        };
    }
    private async Task<AppVeyorBuild> ReadDetailsAsync(ReadContext context, AppVeyorBuild build, CancellationToken token)
    {
        JsonDocument detail;
        try { detail = await JsonAsync(context, DetailUri(build.ProjectId, build.Version), token).ConfigureAwait(false); }
        catch (AppVeyorException error) when (error.StatusCode == 404)
        {
            if (build.BuildId == long.MaxValue) { throw new AppVeyorException("The AppVeyor build identity cannot be used for version recovery."); }
            using JsonDocument history = await JsonAsync(context, HistoryUri(build.ProjectId, build.BuildId + 1), token).ConfigureAwait(false);
            if (!history.RootElement.TryGetProperty("builds", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() != 1 || PositiveId(entries[0], "buildId") != build.BuildId)
            { throw new AppVeyorException("The AppVeyor version recovery returned another build identity."); }
            string recovered = Version(entries[0]);
            build = build with { Version = recovered, PageUri = Page(build.ProjectId, recovered) };
            detail = await JsonAsync(context, DetailUri(build.ProjectId, recovered), token).ConfigureAwait(false);
        }
        using (detail)
        {
            if (!detail.RootElement.TryGetProperty("build", out JsonElement value) || PositiveId(value, "buildId") != build.BuildId
                || Version(value) != build.Version || !value.TryGetProperty("jobs", out JsonElement jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0)
            { throw new AppVeyorException("AppVeyor returned invalid or mismatched build details."); }
            string? detailSha = Text(value, "pullRequestHeadCommitId") ?? Text(value, "commitId");
            if (detailSha is not null && !detailSha.Equals(build.CommitHash, StringComparison.OrdinalIgnoreCase))
            { throw new AppVeyorException("AppVeyor build details changed the commit identity."); }
            JsonElement job = jobs[jobs.GetArrayLength() - 1]; string rawStatus = Text(job, "status") ?? "";
            AppVeyorBuildStatus status = AppVeyorBuild.ParseStatus(rawStatus);
            int count = Count(job, "testsCount");
            int passed = count == 0 && Text(job, "passedTestsCount") is null ? 0 : Count(job, "passedTestsCount");
            int failed = count == 0 && Text(job, "failedTestsCount") is null ? 0 : Count(job, "failedTestsCount");
            if (passed > count || failed > count || (long)passed + failed > count) { throw new AppVeyorException("AppVeyor returned inconsistent test counters."); }
            DateTimeOffset? started = Date(value, "started"), created = Date(value, "created"), updated = Date(value, "updated");
            return build with
            {
                RawStatus = rawStatus, Status = status, StartedAt = started ?? build.StartedAt, CreatedAt = created ?? build.CreatedAt, UpdatedAt = updated ?? build.UpdatedAt,
                DurationMilliseconds = status == AppVeyorBuildStatus.InProgress ? build.DurationMilliseconds : Duration(started, created, updated),
                Tests = count == 0 ? null : new(count, passed, failed), ProgressCounter = (build.ProgressCounter % 3) + 1
            };
        }
    }

    private static Uri HistoryUri(string project, long? startBuildId = null) => new(WebsiteUri, "api/projects/" + AppVeyorProjects.Escaped(project)
        + "/history?recordsNumber=" + (startBuildId.HasValue ? "1&startBuildId=" + startBuildId.Value.ToString(CultureInfo.InvariantCulture) : "25"));
    private static Uri DetailUri(string project, string version) => new(WebsiteUri, "api/projects/" + AppVeyorProjects.Escaped(project) + "/build/" + Uri.EscapeDataString(version));
    private static Uri Page(string project, string version) => new(WebsiteUri, "project/" + AppVeyorProjects.Escaped(project) + "/build/" + Uri.EscapeDataString(version));
    public Uri? BuildPageUri(AppVeyorBuild build)
    {
        try { return build.BuildId > 0 && ValidVersion(build.Version) ? Page(build.ProjectId, build.Version) : null; }
        catch (ArgumentException) { return null; }
    }
    private static Uri? PullRequestUri(string? type, string? repository, string? pr)
    {
        if (repository is null || pr is null) { return null; }
        string[] parts = repository.Split('/');
        if (parts.Length < 2 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.Any(c => char.IsControl(c) || "\\?#%:@".Contains(c)))) { return null; }
        string escaped = string.Join('/', parts.Select(Uri.EscapeDataString));
        string? path = type?.ToLowerInvariant() switch
        { "github" => "https://github.com/" + escaped + "/pull/" + pr,
          "bitbucket" => "https://bitbucket.org/" + escaped + "/pull-requests/" + pr,
          "gitlab" => "https://gitlab.com/" + escaped + "/merge_requests/" + pr, _ => null };
        return path is null ? null : new Uri(path);
    }
    private static bool ValidVersion(string version) => !string.IsNullOrWhiteSpace(version) && version.Length <= 512 && version is not ("." or "..") && !version.Any(char.IsControl);
    private static string Version(JsonElement value)
    { string? version = Text(value, "version"); return version is not null && ValidVersion(version) ? version : throw InvalidResponse(); }
    private static string? Text(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement field) || field.ValueKind == JsonValueKind.Null) { return null; }
        return field.ValueKind switch { JsonValueKind.String => field.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => field.ToString(), _ => throw InvalidResponse() };
    }
    private static long PositiveId(JsonElement value, string name)
    { return long.TryParse(Text(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out long result) && result > 0 ? result : throw InvalidResponse(); }
    private static int Count(JsonElement value, string name)
    { return int.TryParse(Text(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result >= 0 ? result : throw new AppVeyorException("AppVeyor returned invalid test counters."); }
    private static DateTimeOffset? Date(JsonElement value, string name)
    {
        string? raw = Text(value, name); if (raw is null) { return null; }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result) ? result : throw InvalidResponse();
    }
    private static long Duration(DateTimeOffset? started, DateTimeOffset? created, DateTimeOffset? updated)
    {
        DateTimeOffset? begin = started ?? created;
        if (!begin.HasValue || !updated.HasValue) { return 0; }
        long milliseconds = (long)(updated.Value - begin.Value).TotalMilliseconds;
        return milliseconds >= 0 ? milliseconds : throw new AppVeyorException("AppVeyor returned an invalid build duration.");
    }
    private static AppVeyorException InvalidResponse() => new("AppVeyor returned an invalid build response.");

    private async Task<JsonDocument> JsonAsync(ReadContext context, Uri uri, CancellationToken token)
    { ResponseData data = await ResponseAsync(context, uri, token).ConfigureAwait(false); return JsonDocument.Parse(data.Bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
    private static bool InApiScope(Uri uri)
    {
        if (uri.Scheme != "https" || uri.IdnHost != WebsiteUri.IdnHost || uri.Port != WebsiteUri.Port || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) { return false; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.StartsWith("/api/", StringComparison.Ordinal) && !path.Contains('\\') && !path.Split('/').Any(p => p is "." or "..");
    }
    private async Task<ResponseData> ResponseAsync(ReadContext context, Uri original, CancellationToken token)
    {
        Uri uri = original;
        // One deadline includes redirects, response headers and every response-body read.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout); CancellationToken requestToken = deadline.Token;
        for (int redirects = 0; ; redirects++)
        {
            requestToken.ThrowIfCancellationRequested();
            if (context.Requests >= MaxRequests) { throw new RequestBoundException(); } context.Requests++;
            if (!InApiScope(uri)) { throw new AppVeyorException("The request is outside the AppVeyor API endpoint."); }
            context.Responses.TryGetValue(uri, out ResponseData? cached);
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_settings.AccountToken.Length > 0) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccountToken); }
            if (cached?.ETag is string tag && EntityTagHeaderValue.TryParse(tag, out EntityTagHeaderValue? etag)) { request.Headers.IfNoneMatch.Add(etag); }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            requestToken.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is null || !Uri.TryCreate(uri, response.Headers.Location, out Uri? next) || !InApiScope(next))
                { throw new AppVeyorException("AppVeyor redirected outside its API endpoint or too many times; the token was not forwarded."); }
                // A redirect cannot silently retarget another account/project or query.
                if (next.AbsolutePath != original.AbsolutePath || next.Query != original.Query)
                { throw new AppVeyorException("AppVeyor redirected to another API resource; the token was not forwarded."); }
                uri = next; continue;
            }
            if (response.StatusCode == HttpStatusCode.NotModified) { return cached ?? throw new AppVeyorException("AppVeyor returned 304 without a cached response."); }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new AppVeyorException("AppVeyor authentication failed. Check the configured API token.", 401); }
            if (response.StatusCode == HttpStatusCode.Forbidden) { throw new AppVeyorException("AppVeyor denied access. Check project and token permissions.", 403); }
            if (response.StatusCode == HttpStatusCode.NotFound) { throw new AppVeyorException("The AppVeyor project or build was not found or is private.", 404); }
            if ((int)response.StatusCode == 429) { throw new AppVeyorException("AppVeyor rate-limited the request. Try again later.", 429); }
            if (!response.IsSuccessStatusCode) { throw new AppVeyorException("AppVeyor returned HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode); }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) { throw new AppVeyorException("The AppVeyor response exceeded the byte bound."); }
            await using Stream stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using MemoryStream bytes = new(); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaxResponseBytes) { throw new AppVeyorException("The AppVeyor response exceeded the byte bound."); }
                bytes.Write(buffer, 0, read);
            }
            requestToken.ThrowIfCancellationRequested(); ResponseData result = new(bytes.ToArray(), response.Headers.ETag?.ToString());
            context.Responses.Remove(uri);
            if (result.ETag is not null)
            {
                context.Responses[uri] = result;
                while (context.Responses.Count > 256 || context.Responses.Values.Sum(r => (long)r.Bytes.Length) > 16 * 1024 * 1024)
                { context.Responses.Remove(context.Responses.Keys.First()); }
            }
            return result;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_drain is not null) { return new(_drain); }
            _disposed = true; _lifetime.Cancel(); _drain = DrainAsync(_active.ToArray()); return new(_drain);
        }
    }
    private async Task DrainAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception) { /* Actual callers retain errors and cancellation. */ }
        _http.Dispose(); _gate.Dispose(); _lifetime.Dispose();
    }
}
