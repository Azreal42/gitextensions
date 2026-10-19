using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using GitExtensions.Linux;

internal static class GitHubActionsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("GitHub Actions Windows status and conclusion mappings retain unknown future states", StatusAsync),
        ("GitHub Actions groups sibling workflows per SHA and selects latest workflow run without losing failures", ShaAsync),
        ("GitHub Actions paginates 100 GET runs with documented headers and never follows a Link origin", PaginationAsync),
        ("GitHub Actions ETag conditional requests accept 304 without losing cached workflow payload", ETagAsync),
        ("GitHub Actions fresh cache avoids requests and explicit refresh bypasses only the time cache", CacheAsync),
        ("GitHub Actions cancellation during pagination does not publish a partial replacement", CancellationAsync),
        ("GitHub Actions bounded pagination reports truncation rather than pretending a complete history", TruncationAsync),
        ("GitHub Actions errors expose HTTP authentication and rate limit failures without token or payload", ErrorsAsync),
        ("GitHub Actions transport failures and invalid JSON never disclose transport exception contents", InvalidAsync),
        ("GitHub Actions HTTPS is mandatory for tokens and URL credentials are refused before network", TransportAsync),
        ("GitHub Actions redirects keep credentials within the same origin and reject cross-origin and downgrade", RedirectsAsync),
        ("GitHub Actions only exposes explicit same-site run pages for a browser action", LinksAsync),
        ("GitHub Actions Windows XML defaults stay disabled and remote detection never writes settings", SettingsAsync),
        ("GitHub Actions raw hierarchy preserves higher invalid scalars and rejects inherited token retargeting", ScopeAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static GitHubActionsSettings Settings(string token = "") => new()
    { Enabled = true, Provider = GitHubActionsSettings.ProviderName, Owner = "octo", Repository = "repo", Token = token };
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static Task StatusAsync()
    {
        foreach (string state in new[] { "in_progress", "queued", "requested", "waiting", "pending" })
        { Check(new GitHubActionsWorkflowRun { Status = state }.BuildStatus == GitHubActionsBuildStatus.InProgress, "Every running state is retained."); }
        foreach ((string conclusion, GitHubActionsBuildStatus status) in new[]
        { ("success", GitHubActionsBuildStatus.Success), ("neutral", GitHubActionsBuildStatus.Success), ("skipped", GitHubActionsBuildStatus.Success),
          ("failure", GitHubActionsBuildStatus.Failure), ("timed_out", GitHubActionsBuildStatus.Failure), ("cancelled", GitHubActionsBuildStatus.Stopped),
          ("action_required", GitHubActionsBuildStatus.Unstable), ("stale", GitHubActionsBuildStatus.Unstable), ("future", GitHubActionsBuildStatus.Unknown) })
        { Check(new GitHubActionsWorkflowRun { Status = "completed", Conclusion = conclusion }.BuildStatus == status, "Windows conclusions are preserved."); }
        Check(new GitHubActionsWorkflowRun { Status = "completed" }.BuildStatus == GitHubActionsBuildStatus.Unknown, "A missing conclusion is not success.");
        return Task.CompletedTask;
    }
    private static Task ShaAsync()
    {
        GitHubActionsRunSnapshot snapshot = new(new[]
        {
            new GitHubActionsWorkflowRun { Id = 1, WorkflowId = 10, HeadSha = Sha, Conclusion = "failure", CreatedAt = DateTimeOffset.UnixEpoch },
            new GitHubActionsWorkflowRun { Id = 2, WorkflowId = 10, HeadSha = Sha.ToUpperInvariant(), Conclusion = "success", CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) },
            new GitHubActionsWorkflowRun { Id = 3, WorkflowId = 20, HeadSha = Sha, Conclusion = "failure" },
            new GitHubActionsWorkflowRun { Id = 4, HeadSha = "not a SHA", Conclusion = "success" }
        }, DateTimeOffset.UtcNow);
        Check(snapshot.Commits.Count == 1 && snapshot.Commits[Sha].Runs.Count == 2 && snapshot.Commits[Sha].Status == GitHubActionsBuildStatus.Failure,
            "A newer successful workflow does not hide another workflow failure, or retain its own obsolete run.");
        Check(snapshot.Runs.Count == 3 && GitHubActionsWorkflowRun.IsCommitHash(new string('a', 64)), "Invalid SHAs are skipped and SHA256 commits are recognized.");
        return Task.CompletedTask;
    }
    private static async Task PaginationAsync()
    {
        using Handler handler = new((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "Public API reads are anonymous GETs.");
            Check(request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2022-11-28" && request.Headers.Accept.Any(v => v.MediaType == "application/vnd.github+json"), "Documented headers and Windows API version.");
            Check(request.RequestUri!.Query.Contains("per_page=100") && request.RequestUri.Host == "api.github.com", "Bounded numeric pages use the configured origin.");
            HttpResponseMessage response = request.RequestUri.Query.Contains("&page=1", StringComparison.Ordinal) ? Runs(100) : Runs(1, 101);
            if (request.RequestUri.Query.Contains("&page=1", StringComparison.Ordinal)) { response.Headers.Add("Link", "<https://evil.invalid/steal>; rel=\"next\""); }
            return Task.FromResult(response);
        });
        using GitHubActionsApiClient client = new(Settings(), handler);
        GitHubActionsRunSnapshot result = await client.ReadAsync();
        Check(handler.Calls == 2 && result.Runs.Count == 101 && !result.Truncated, "Both numeric pages are actually read; the Link URL is never used.");
    }
    private static async Task ETagAsync()
    {
        using Handler handler = new((request, _) =>
        {
            if (request.Headers.IfNoneMatch.Count > 0) { Check(request.Headers.IfNoneMatch.Single().Tag == "\"v1\"", "The original ETag is sent."); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            HttpResponseMessage response = Runs(1); response.Headers.ETag = new EntityTagHeaderValue("\"v1\""); return Task.FromResult(response);
        });
        using GitHubActionsApiClient client = new(Settings(), handler);
        GitHubActionsRunSnapshot first = await client.ReadAsync(); GitHubActionsRunSnapshot next = await client.ReadAsync(force: true);
        Check(handler.Calls == 2 && next.Runs.Count == first.Runs.Count && next.Runs[0].Id == first.Runs[0].Id, "304 preserves the parsed payload.");
    }
    private static async Task CacheAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Runs(1))); using GitHubActionsApiClient client = new(Settings(), handler);
        GitHubActionsRunSnapshot first = await client.ReadAsync(); Check(ReferenceEquals(first, await client.ReadAsync()) && handler.Calls == 1, "Fresh cached reads do not send HTTP.");
        await client.ReadAsync(force: true); Check(handler.Calls == 2, "An explicit refresh actually revalidates.");
    }
    private static async Task CancellationAsync()
    {
        using CancellationTokenSource cancel = new(); bool first = true;
        using Handler handler = new((request, token) =>
        {
            if (first) { first = false; return Task.FromResult(Runs(1)); }
            if (request.RequestUri!.Query.Contains("&page=1", StringComparison.Ordinal)) { return Task.FromResult(Runs(100)); }
            cancel.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException("Unreachable");
        });
        using GitHubActionsApiClient client = new(Settings(), handler); GitHubActionsRunSnapshot baseline = await client.ReadAsync();
        await ExpectAsync<OperationCanceledException>(() => client.ReadAsync(force: true, cancel.Token));
        Check(ReferenceEquals(baseline, await client.ReadAsync()), "Cancelled partial pages never replace the accepted cache.");
        using CancellationTokenSource already = new(); already.Cancel(); int calls = handler.Calls;
        await ExpectAsync<OperationCanceledException>(() => client.ReadAsync(cancellationToken: already.Token)); Check(handler.Calls == calls, "Pre-cancellation has no request.");
    }
    private static async Task TruncationAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Runs(100))); using GitHubActionsApiClient client = new(Settings(), handler) { MaxPages = 2 };
        Check((await client.ReadAsync()).Truncated && handler.Calls == 2, "A bounded partial history is explicitly marked.");
    }
    private static async Task ErrorsAsync()
    {
        foreach (int code in new[] { 401, 403, 404, 429, 500 })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("secret-token payload") }));
            using GitHubActionsApiClient client = new(Settings("secret-token"), handler);
            GitHubActionsException failure = await ExpectAsync<GitHubActionsException>(() => client.ReadAsync());
            Check(!failure.ToString().Contains("secret-token") && !failure.ToString().Contains("payload"), "Neither server body nor token is reported.");
            Check(failure.Message.Length > 20, "The HTTP failure remains visible and readable.");
        }
    }
    private static async Task InvalidAsync()
    {
        foreach (string body in new[] { "not json", "{}", "{\"workflow_runs\":null}" })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            using GitHubActionsApiClient client = new(Settings(), handler); await ExpectAsync<GitHubActionsException>(() => client.ReadAsync());
        }
        using Handler failed = new((_, _) => throw new HttpRequestException("secret-token")); using GitHubActionsApiClient transport = new(Settings("secret-token"), failed);
        Check(!(await ExpectAsync<GitHubActionsException>(() => transport.ReadAsync())).ToString().Contains("secret-token"), "Transport exception text is not propagated.");
    }
    private static Task TransportAsync()
    {
        foreach (string endpoint in new[] { "http://example.invalid", "https://user:password@example.invalid", "file:///tmp/api", "https://example.invalid/?token=value" })
        {
            bool failed = false;
            try { using GitHubActionsApiClient _ = new(new() { ApiUrl = endpoint, Owner = "o", Repository = "r", Token = "secret-token" }); }
            catch (ArgumentException error) { failed = true; Check(!error.ToString().Contains("secret-token") && !error.ToString().Contains("password"), "Configuration errors are redacted."); }
            Check(failed, "Unsafe token transport is rejected before creating a request.");
        }
        Check(Settings("secret-token").ToString().Contains("redacted") && !Settings("secret-token").ToString().Contains("secret-token"), "Configuration ToString never prints a token.");
        return Task.CompletedTask;
    }
    private static async Task RedirectsAsync()
    {
        foreach (string endpoint in new[] { "https://evil.invalid/steal", "http://api.github.com/steal" })
        {
            using Handler handler = new((_, _) => Task.FromResult(Redirect(endpoint))); using GitHubActionsApiClient client = new(Settings("secret-token"), handler);
            await ExpectAsync<GitHubActionsException>(() => client.ReadAsync()); Check(handler.Calls == 1, "No cross-origin or downgraded authenticated request is sent.");
        }
        int calls = 0; using Handler same = new((request, _) =>
        { Check(request.Headers.Authorization?.Parameter == "secret-token", "Same-origin explicit credentials are retained."); return Task.FromResult(++calls == 1 ? Redirect("/repos/octo/repo/actions/runs?moved=1") : Runs(1)); });
        using GitHubActionsApiClient safe = new(Settings("secret-token"), same); await safe.ReadAsync(); Check(calls == 2, "Same-origin redirects work.");
    }
    private static Task LinksAsync()
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No network expected")); using GitHubActionsApiClient client = new(Settings(), handler);
        foreach (string value in new[] { "file:///tmp/run", "https://evil.invalid/octo/repo/actions/runs/1", "https://u:p@github.com/octo/repo/actions/runs/1", "https://github.com/elsewhere/repo/actions/runs/1" })
        { Check(client.BuildPageUri(new() { HtmlUrl = value }) is null, "Only the configured repository run pages can be opened."); }
        Check(client.BuildPageUri(new() { HtmlUrl = "https://github.com/octo/repo/actions/runs/1" }) is not null && handler.Calls == 0, "Producing a URL is not opening it or sending HTTP."); return Task.CompletedTask;
    }
    private static async Task SettingsAsync()
    {
        using Files files = new(); GitXmlSettingsSnapshot global = await GitXmlSettingsStore.LoadAsync(files.Path("global"));
        GitHubActionsSettings settings = GitHubActionsSettings.Read(new(global), ["git@github.com:owner/project.git"]);
        Check(!settings.Active && settings.Owner == "owner" && settings.Repository == "project" && !File.Exists(global.Path), "Detection has no writes and CI defaults to off.");
        Check(GitHubActionsSettings.TokenKey == "BuildServer.GitHub Actions.GitHubActionsApiToken", "Exact Windows provider key.");
    }
    private static async Task ScopeAsync()
    {
        using Files files = new(); files.Write("global", (GitHubActionsSettings.EnableKey, "true"), (GitHubActionsSettings.ProviderKey, "GitHub Actions"), (GitHubActionsSettings.TokenKey, "secret-token"));
        files.Write("local", (GitHubActionsSettings.EnableKey, "future-bool")); files.Write("distributed", ("future.key", "kept"));
        GitDistributedSettingsSnapshot snapshot = new(await GitXmlSettingsStore.LoadAsync(files.Path("global")), await GitXmlSettingsStore.LoadAsync(files.Path("local")), await GitXmlSettingsStore.LoadAsync(files.Path("distributed")));
        Check(!GitHubActionsSettings.Read(snapshot).Enabled, "An invalid higher scalar masks a lower true rather than enabling requests.");
        files.Write("local", (GitHubActionsSettings.EnableKey, "true")); files.Write("distributed", (GitHubActionsSettings.ApiUrlKey, "https://evil.invalid/api"));
        snapshot = new(await GitXmlSettingsStore.LoadAsync(files.Path("global")), await GitXmlSettingsStore.LoadAsync(files.Path("local")), await GitXmlSettingsStore.LoadAsync(files.Path("distributed")));
        bool refused = false; try { _ = GitHubActionsSettings.Read(snapshot); } catch (ArgumentException) { refused = true; }
        Check(refused, "A distributed endpoint cannot retarget an inherited global token.");
    }
    private static HttpResponseMessage Runs(int count, int start = 1) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
    { total_count = count, workflow_runs = Enumerable.Range(start, count).Select(n => new { id = n, workflow_id = n, head_sha = Sha, name = "Build", status = "completed", conclusion = "success" }) })) };
    private static HttpResponseMessage Redirect(string uri) { HttpResponseMessage result = new(HttpStatusCode.Redirect); result.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute); return result; }
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); } }
    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-ci-test-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name + ".settings");
        public void Write(string name, params (string Key, string Value)[] values) => File.WriteAllText(Path(name), new XElement("dictionary", values.Select(v =>
            new XElement("item", new XElement("key", new XElement("string", v.Key)), new XElement("value", new XElement("string", v.Value))))).ToString());
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
