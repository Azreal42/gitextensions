using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux;

/// <summary>Original full/timestamp polling, owned by the host. GET-only, explicit Guest/Basic, no OS credentials.</summary>
public sealed class JenkinsApiClient : IJenkinsClient
{
    private readonly JenkinsSettings _settings;
    private readonly IReadOnlyList<JenkinsProject> _projects;
    private readonly Regex? _ignore;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _active = [];
    private Dictionary<Uri, ResponseData> _responses = [];
    private Dictionary<string, ProjectState> _states = [];
    private JenkinsSnapshot? _snapshot;
    private bool _disposed;
    private Task? _drain;
    private sealed record ResponseData(byte[] Bytes, string? ETag, bool Missing = false);
    private sealed record ProjectState(long Timestamp, bool NeedsFull, IReadOnlyList<JenkinsBuild> Builds, bool Truncated, IReadOnlyList<string> Diagnostics);
    private sealed class ProjectDiagnostics
    {
        internal List<string> Messages { get; } = [];
        internal bool Truncated { get; set; }
    }
    private sealed class BoundException : Exception { }
    private sealed class Context(Dictionary<Uri, ResponseData> responses, Dictionary<string, ProjectState> states)
    {
        internal Dictionary<Uri, ResponseData> Responses { get; } = responses;
        internal Dictionary<string, ProjectState> States { get; } = states;
        internal List<string> Diagnostics { get; } = [];
        internal List<JenkinsBuild> Emissions { get; } = [];
        internal bool Truncated { get; set; }
        internal int Requests { get; set; }
        internal int Builds { get; set; }
    }
    public Uri ServerUri { get; }
    public string UniqueKey => ServerUri.Host;
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleCacheDuration { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxProjects { get; init; } = 50;
    public int MaxJobs { get; init; } = 500;
    public int MaxBuildsPerProject { get; init; } = 500;
    public int MaxBuilds { get; init; } = 2000;
    public int MaxRequests { get; init; } = 256;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    public JenkinsApiClient(JenkinsSettings settings, IBuildServerCredentials? credentials = null, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(requireProjects: false);
        _settings = settings; ServerUri = settings.ServerUri; _projects = JenkinsProjects.Resolve(settings.ProjectName, ServerUri);
        _ignore = settings.CreateIgnoreBuildBranchRegex(); _clock = clock ?? TimeProvider.System;
        BuildServerCredentialsType mode = credentials?.BuildServerCredentialsType ?? BuildServerCredentialsType.Guest;
        // Copy draft values now. Subsequent edits cannot retarget or silently change this instance's credentials.
        if (mode == BuildServerCredentialsType.UsernameAndPassword)
        {
            string user = credentials?.Username ?? "", password = credentials?.Password ?? "";
            if (user.Length == 0 || user.Contains(':') || user.Any(char.IsControl) || password.Any(char.IsControl))
            { throw new ArgumentException("Enter valid explicit Jenkins username and password credentials."); }
            _authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        }
        else if (mode != BuildServerCredentialsType.Guest)
        { throw new ArgumentException("Jenkins supports explicit Guest or username/password credentials; Bearer and Windows SSO are not implemented."); }
        HttpMessageHandler transport = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        for (HttpMessageHandler? h = transport; h is not null; h = h is DelegatingHandler d ? d.InnerHandler : null)
        {
            if (h is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseCookies = false; client.UseDefaultCredentials = false; client.Credentials = null; client.DefaultProxyCredentials = null; }
            if (h is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; sockets.DefaultProxyCredentials = null; }
        }
        _http = new(transport) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public Task<JenkinsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<JenkinsSnapshot> task = RunAsync(force, cancellationToken); _active.Add(task); _ = RetireAsync(task); return task;
        }
    }
    private async Task RetireAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception) { } finally { lock (_sync) { _active.Remove(task); } } }
    private async Task<JenkinsSnapshot> RunAsync(bool force, CancellationToken caller)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token); CancellationToken token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (MaxProjects is < 1 or > 1000 || MaxJobs is < 1 or > 10000 || MaxBuildsPerProject is < 1 or > 100000
                || MaxBuilds is < 1 or > 100000 || MaxRequests is < 1 or > 10000 || MaxResponseBytes is < 1024 or > 64 * 1024 * 1024
                || CacheDuration < TimeSpan.Zero || IdleCacheDuration < TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            { throw new JenkinsException("The Jenkins reader bounds are invalid."); }
            if (!_settings.Active) { return new([], _clock.GetUtcNow()); }
            if (_projects.Count == 0) { throw new JenkinsException("Configure at least one Jenkins project before enabling integration."); }
            TimeSpan interval = _snapshot?.Builds.Any(b => b.IsRunning) == true ? CacheDuration : IdleCacheDuration;
            if (!force && _snapshot is not null && _clock.GetUtcNow() - _snapshot.ReadAt < interval) { return _snapshot; }
            Context context = new(new(_responses), force ? [] : new(_states));
            if (_projects.Count > MaxProjects) { Truncate(context, "The Jenkins project bound was reached; results are incomplete."); }
            JenkinsProject[] selected = _projects.Take(MaxProjects).ToArray();
            // Windows emits already-required full reads before full reads discovered by timestamp probes.
            // Cached projects remain in history, but must not be replayed through this pass's status priority.
            bool NeedsFull(JenkinsProject project) => force || !context.States.TryGetValue(project.Id, out ProjectState? state)
                || state.NeedsFull || state.Timestamp <= 0;
            var polling = selected.Select(project => (Project: project, Full: NeedsFull(project)))
                .OrderByDescending(item => item.Full).ToArray();
            try
            {
                for (int index = 0; index < polling.Length; index++)
                {
                    (JenkinsProject project, bool full) = polling[index];
                    token.ThrowIfCancellationRequested(); context.States.TryGetValue(project.Id, out ProjectState? previous);
                    ResponseData response = await ResponseAsync(context, JenkinsProjects.RequestUri(project, full), token).ConfigureAwait(false);
                    if (response.Missing)
                    {
                        if (previous is null) { _states.TryGetValue(project.Id, out previous); }
                        string[] messages = [.. (previous?.Diagnostics ?? []), "A configured Jenkins job was not found; its previous results are retained and history is incomplete."];
                        context.States[project.Id] = new(previous?.Timestamp ?? 0, true, previous?.Builds ?? [], true,
                            Array.AsReadOnly(messages.Distinct(StringComparer.Ordinal).ToArray()));
                        continue;
                    }
                    using JsonDocument document = Parse(response.Bytes);
                    if (!full)
                    {
                        ProjectState prior = previous ?? throw new JenkinsException("The Jenkins project cache is unavailable.");
                        ProjectDiagnostics probe = new();
                        long timestamp = Timestamp(document.RootElement, project.IsMultiBranch, probe);
                        if (timestamp > prior.Timestamp)
                        {
                            response = await ResponseAsync(context, JenkinsProjects.RequestUri(project, true), token).ConfigureAwait(false);
                            if (response.Missing) { throw new JenkinsException("The Jenkins job disappeared during refresh; previous results were preserved."); }
                            using JsonDocument fullDocument = Parse(response.Bytes);
                            PublishProject(ReadProject(fullDocument.RootElement, project, context, token));
                        }
                        else
                        {
                            context.Builds += prior.Builds.Count;
                            if (probe.Truncated)
                            {
                                context.States[project.Id] = prior with { NeedsFull = true, Truncated = true,
                                    Diagnostics = Array.AsReadOnly(prior.Diagnostics.Concat(probe.Messages).Distinct(StringComparer.Ordinal).ToArray()) };
                            }
                        }
                    }
                    else { PublishProject(ReadProject(document.RootElement, project, context, token)); }
                    if (context.Builds >= MaxBuilds && index + 1 < polling.Length)
                    { Truncate(context, "The Jenkins retained build bound was reached; results are incomplete."); break; }
                    void PublishProject(ProjectState state)
                    { context.States[project.Id] = state; context.Emissions.AddRange(state.Builds); }
                }
            }
            catch (BoundException) { Truncate(context, "The Jenkins request bound was reached; results are incomplete."); }
            ProjectState[] retained = _projects.Where(p => context.States.ContainsKey(p.Id)).Select(p => context.States[p.Id]).ToArray();
            JenkinsBuild[] history = retained.SelectMany(state => state.Builds).ToArray();
            if (history.Length > MaxBuilds) { Truncate(context, "The Jenkins retained build bound was reached; results are incomplete."); }
            JenkinsSnapshot result = new(history.Take(MaxBuilds), _clock.GetUtcNow(), context.Truncated || retained.Any(state => state.Truncated),
                context.Diagnostics.Concat(retained.SelectMany(state => state.Diagnostics)), _snapshot?.Commits, context.Emissions);
            token.ThrowIfCancellationRequested(); _states = context.States; _responses = context.Responses; _snapshot = result; return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new JenkinsException("The Jenkins request timed out."); }
        catch (RegexMatchTimeoutException) { throw new JenkinsException("The Jenkins ignore-branch expression exceeded its time bound."); }
        catch (JenkinsException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { throw new JenkinsException("Jenkins returned an invalid response or configuration; previous results were preserved."); }
        catch (HttpRequestException) { throw new JenkinsException("The Jenkins service could not be reached."); }
        catch (IOException) { throw new JenkinsException("The Jenkins response could not be read."); }
        finally { _gate.Release(); }
    }
    private ProjectState ReadProject(JsonElement root, JenkinsProject project, Context context, CancellationToken token)
    {
        ProjectDiagnostics details = new();
        long timestamp = Timestamp(root, project.IsMultiBranch, details); List<JenkinsBuild> builds = []; int selected = 0;
        IEnumerable<JsonElement> jobs;
        if (root.TryGetProperty("builds", out _)) { jobs = [root]; }
        else if (root.TryGetProperty("jobs", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            if (children.GetArrayLength() > MaxJobs) { Truncate(details, "The Jenkins multibranch job bound was reached; results are incomplete."); }
            jobs = children.EnumerateArray().Take(MaxJobs);
        }
        else { throw Invalid("job build list (the custom tree must expose builds or jobs/builds)"); }
        foreach (JsonElement job in jobs)
        {
            if (!job.TryGetProperty("builds", out JsonElement values) || values.ValueKind != JsonValueKind.Array) { throw Invalid("job build list"); }
            foreach (JsonElement value in values.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (selected >= MaxBuildsPerProject || context.Builds >= MaxBuilds)
                { Truncate(details, "The Jenkins build bound was reached; results are incomplete."); return State(); }
                selected++; context.Builds++; JenkinsBuild? build = ParseBuild(value, project);
                if (build is not null)
                {
                    builds.Add(build);
                    if (build.CommitHashes.Count == 0) { details.Messages.Add("Some Jenkins builds provide no Git revisions and cannot be mapped to commits."); }
                }
            }
        }
        if (selected > 0 && timestamp <= 0) { throw Invalid("last-build timestamp"); }
        return State();
        ProjectState State() => new(timestamp, details.Truncated || builds.Any(b => b.IsRunning), builds.AsReadOnly(), details.Truncated,
            Array.AsReadOnly(details.Messages.Distinct(StringComparer.Ordinal).ToArray()));
    }
    private long Timestamp(JsonElement root, bool multi, ProjectDiagnostics details)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw Invalid("job object"); }
        long latest = Last(root);
        if (root.TryGetProperty("jobs", out JsonElement jobs))
        {
            if (jobs.ValueKind != JsonValueKind.Array) { throw Invalid("multibranch jobs"); }
            if (jobs.GetArrayLength() > MaxJobs) { Truncate(details, "The Jenkins multibranch job bound was reached; results are incomplete."); }
            foreach (JsonElement job in jobs.EnumerateArray().Take(MaxJobs)) { latest = Math.Max(latest, Last(job)); }
        }
        else if (multi && !root.TryGetProperty("builds", out _)) { throw Invalid("multibranch timestamp"); }
        return latest;
        static long Last(JsonElement job)
        {
            if (job.ValueKind != JsonValueKind.Object) { throw Invalid("job"); }
            if (!job.TryGetProperty("lastBuild", out JsonElement last) || last.ValueKind == JsonValueKind.Null) { return 0; }
            long stamp = Long(last, "timestamp") ?? 0; if (stamp < 0) { throw Invalid("timestamp"); } return stamp;
        }
    }
    private JenkinsBuild? ParseBuild(JsonElement value, JenkinsProject project)
    {
        long number = Long(value, "number") ?? 0, timestamp = Long(value, "timestamp") ?? -1;
        if (number <= 0 || timestamp < 0 || !value.TryGetProperty("building", out JsonElement active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw Invalid("build identity or state"); }
        bool running = active.GetBoolean(); long? duration = running ? null : Long(value, "duration");
        DateTimeOffset started = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        if (!running && (!duration.HasValue || duration < 0 || duration > (DateTimeOffset.MaxValue - started).TotalMilliseconds)) { throw Invalid("duration"); }
        List<string> hashes = []; JenkinsTestResults? tests = null;
        if (value.TryGetProperty("actions", out JsonElement actions) && actions.ValueKind != JsonValueKind.Null)
        {
            if (actions.ValueKind != JsonValueKind.Array) { throw Invalid("actions"); }
            foreach (JsonElement action in actions.EnumerateArray())
            {
                if (action.ValueKind == JsonValueKind.Null) { continue; }
                if (action.ValueKind != JsonValueKind.Object) { throw Invalid("action"); }
                if (action.TryGetProperty("lastBuiltRevision", out JsonElement revision) && revision.ValueKind != JsonValueKind.Null)
                {
                    string? hash = Text(revision, "SHA1"); if (!JenkinsBuild.IsCommitHash(hash)) { throw Invalid("Git revision"); }
                    if (!hashes.Contains(hash!, StringComparer.OrdinalIgnoreCase)) { hashes.Add(hash!); }
                    if (_ignore is not null && revision.TryGetProperty("branch", out JsonElement branches) && branches.ValueKind != JsonValueKind.Null)
                    {
                        if (branches.ValueKind != JsonValueKind.Array) { throw Invalid("Git branches"); }
                        foreach (JsonElement branch in branches.EnumerateArray())
                        { if (branch.ValueKind != JsonValueKind.Null && Text(branch, "name") is string name && !string.IsNullOrWhiteSpace(name) && _ignore.IsMatch(name)) { return null; } }
                    }
                }
                long? total = Long(action, "totalCount");
                if (total is < 0) { throw Invalid("test counters"); }
                if (total is > 0)
                {
                    long failed = Long(action, "failCount") ?? -1, skipped = Long(action, "skipCount") ?? -1;
                    if (failed < 0 || skipped < 0 || failed > total || skipped > total - failed) { throw Invalid("test counters"); }
                    tests = new(total.Value, failed, skipped);
                }
            }
        }
        Uri? page = SafePage(Text(value, "url"), project, number);
        if (project.IsMultiBranch && page is null) { throw Invalid("multibranch build URL"); }
        string id = page is null ? project.ProjectUri.AbsolutePath.TrimEnd('/') : page.AbsolutePath.TrimEnd('/')[..page.AbsolutePath.TrimEnd('/').LastIndexOf('/')];
        string result = Text(value, "result") ?? "";
        return new() { ProjectId = id, ProjectName = project.Id, Number = number, RawResult = result, IsRunning = running,
            Status = JenkinsBuild.Map(result, running), CommitHashes = hashes.AsReadOnly(), StartedAt = started,
            DurationMilliseconds = duration, PageUri = page, TestResults = tests };
    }
    private Uri? SafePage(string? address, JenkinsProject project, long number)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? page) || !JenkinsProjects.InServer(ServerUri, page)
            || page.Query.Length != 0 || !page.AbsolutePath.StartsWith(project.ProjectUri.AbsolutePath, StringComparison.Ordinal)
            || page.AbsolutePath.TrimEnd('/').Split('/').Last() != number.ToString(CultureInfo.InvariantCulture)) { return null; }
        return page;
    }
    public Uri? BuildPageUri(JenkinsBuild build)
        => build.PageUri is not null && _projects.Any(project => SafePage(build.PageUri.AbsoluteUri, project, build.Number) is not null) ? build.PageUri : null;
    private static void Truncate(Context context, string message) { context.Truncated = true; context.Diagnostics.Add(message); }
    private static void Truncate(ProjectDiagnostics details, string message) { details.Truncated = true; details.Messages.Add(message); }
    private static JenkinsException Invalid(string kind) => new("Jenkins returned an invalid " + kind + ".");
    private static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    private static string? Text(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out JsonElement item) || item.ValueKind == JsonValueKind.Null) { return null; }
        return item.ValueKind switch { JsonValueKind.String => item.GetString(), JsonValueKind.Number => item.ToString(), _ => throw Invalid("scalar") };
    }
    private static long? Long(JsonElement value, string key)
    { string? text = Text(value, key); return text is null ? null : long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : throw Invalid("number"); }
    private async Task<ResponseData> ResponseAsync(Context context, Uri original, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(RequestTimeout); CancellationToken requestToken = deadline.Token;
        Uri uri = original;
        for (int redirects = 0; ; redirects++)
        {
            requestToken.ThrowIfCancellationRequested(); if (++context.Requests > MaxRequests) { throw new BoundException(); }
            if (!JenkinsProjects.InServer(ServerUri, uri)) { throw new JenkinsException("The request is outside the configured Jenkins server base path."); }
            context.Responses.TryGetValue(uri, out ResponseData? cached);
            using HttpRequestMessage request = new(HttpMethod.Get, uri); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha");
            request.Headers.Authorization = _authorization;
            if (cached?.ETag is string tag && EntityTagHeaderValue.TryParse(tag, out EntityTagHeaderValue? etag)) { request.Headers.IfNoneMatch.Add(etag); }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is null || !Uri.TryCreate(uri, response.Headers.Location, out Uri? next)
                    || !JenkinsProjects.InServer(ServerUri, next) || next.AbsolutePath != original.AbsolutePath || next.Query != original.Query)
                { throw new JenkinsException("Jenkins redirected outside the requested server resource or too many times; credentials were not forwarded."); }
                uri = next; continue;
            }
            if (response.StatusCode == HttpStatusCode.NotModified) { return cached ?? throw Invalid("304 response without cached data"); }
            if (response.StatusCode == HttpStatusCode.NotFound) { return new([], null, true); }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            { throw new JenkinsException("Jenkins requires explicit Guest access or valid username/password credentials. Windows SSO and Bearer are not used.", response.StatusCode == HttpStatusCode.Forbidden ? 403 : 401); }
            if (!response.IsSuccessStatusCode) { throw new JenkinsException("Jenkins returned HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode); }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) { throw new JenkinsException("The Jenkins response exceeded the byte bound."); }
            await using Stream stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false); using MemoryStream bytes = new(); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
            { if (bytes.Length + read > MaxResponseBytes) { throw new JenkinsException("The Jenkins response exceeded the byte bound."); } bytes.Write(buffer, 0, read); }
            requestToken.ThrowIfCancellationRequested(); ResponseData result = new(bytes.ToArray(), response.Headers.ETag?.ToString());
            context.Responses.Remove(uri);
            if (result.ETag is not null)
            {
                context.Responses[uri] = result;
                while (context.Responses.Count > 128 || context.Responses.Values.Sum(v => (long)v.Bytes.Length) > 16 * 1024 * 1024) { context.Responses.Remove(context.Responses.Keys.First()); }
            }
            return result;
        }
    }
    public ValueTask DisposeAsync()
    { lock (_sync) { if (_drain is not null) { return new(_drain); } _disposed = true; _lifetime.Cancel(); _drain = DrainAsync(_active.ToArray()); return new(_drain); } }
    private async Task DrainAsync(Task[] tasks)
    { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { } _http.Dispose(); _gate.Dispose(); _lifetime.Dispose(); }
}
