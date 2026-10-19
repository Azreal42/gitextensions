using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GitExtensions.Linux;

internal static class GitLabScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("GitLab Windows provider spelling keys defaults and pure remote prefill never enable or write CI", SettingsAsync),
        ("GitLab raw XML hierarchy masks lower invalid scalars and supports modern long project IDs", HierarchyAsync),
        ("GitLab inherited tokens cannot be retargeted to another origin or application base path", ProvenanceAsync),
        ("GitLab Windows HTTP HTTPS git ssh and scp remotes preserve nested namespaces and suffixes", RemotesAsync),
        ("GitLab configured application basepaths and custom SSH ports are resolved without guessing web ports", RemoteInstanceAsync),
        ("GitLab remote parser is anchored and refuses credentials query fragments and malformed paths", InvalidRemotesAsync),
        ("GitLab only original running success failed canceled statuses map while all future states stay unknown", StatusAsync),
        ("GitLab snapshots select latest UpdatedAt per SHA and deterministic IDs without sibling aggregation", SnapshotAsync),
        ("GitLab PRIVATE TOKEN v4 project lookup URL encodes the complete nested namespace as one segment", ProjectAsync),
        ("GitLab polling reads exact running and finished scopes with per page100 and no mutating requests", WireAsync),
        ("GitLab total pages and next page headers load all ordered pages and deduplicate repeated IDs", PaginationAsync),
        ("GitLab omitted total headers use X Next Page or validated Link without false completion", MissingHeadersAsync),
        ("GitLab configured PagesLimit zero stays unlimited while positive limits visibly truncate", ConfigLimitAsync),
        ("GitLab global page item and request bounds explicitly mark incomplete publications", GlobalBoundsAsync),
        ("GitLab bounded item history retains current running pipelines before finished history", RunningPriorityAsync),
        ("GitLab ETag304 preserves payload pagination metadata and missing cache304 fails", ETagAsync),
        ("GitLab time cache avoids HTTP while force refresh actually revalidates both scopes", CacheAsync),
        ("GitLab cancellation during pagination retains the last accepted atomic snapshot", CancellationAsync),
        ("GitLab disposal cancels and drains active requests and refuses new work", DrainAsync),
        ("GitLab request deadlines also cancel blocked response bodies and drain their streams", BodyTimeoutAsync),
        ("GitLab caller cancellation of a body stays cancellation rather than authentication or timeout", BodyCancellationAsync),
        ("GitLab same instance redirects preserve explicit HTTP token and application basepath", RedirectAsync),
        ("GitLab redirect and pagination origins basepaths project IDs and downgrades cannot steal tokens", UnsafeRedirectAsync),
        ("GitLab malformed conflicting cyclic pagination headers fail without hiding incomplete pages", InvalidPaginationAsync),
        ("GitLab401403404429server errors preserve useful status without secret response bodies", ErrorsAsync),
        ("GitLab malformed null missing identity revision and excessive JSON depth cannot publish partial success", JsonAsync),
        ("GitLab response byte bounds cover content length and chunked bodies", SizeAsync),
        ("GitLab transport errors and timeouts never expose inner exception token text", TransportAsync),
        ("GitLab explicit exact same instance pipeline pages reject unsafe origins paths and IDs", PagesAsync),
        ("GitLab polling is disabled by default and bad settings fail before any network", AdmissionAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Sha256 = new('a', 64);
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static GitLabSettings Settings(int pages = 0, string instance = "https://gitlab.invalid/base/", string token = "private-token") => new()
    { Enabled = true, Provider = "Gitlab", InstanceUrl = instance, ProjectId = 23, ApiToken = token, PagesLimit = pages };
    private static GitLabPipeline Pipeline(long id, string status = "success", string sha = Sha, int updated = 1) => new()
    { Id = id, ProjectId = 23, Sha = sha, Status = status, CreatedAt = Epoch, UpdatedAt = Epoch.AddSeconds(updated), WebUrl = "https://gitlab.invalid/base/group/repo/-/pipelines/" + id };
    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> Reply(HttpResponseMessage value) => Task.FromResult(value);
    private static HttpResponseMessage Page(IEnumerable<GitLabPipeline> values, int? total = 1, string? next = "", int? page = null)
    {
        HttpResponseMessage response = Json(values);
        if (total.HasValue) { response.Headers.Add("X-Total-Pages", total.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (next is not null) { response.Headers.TryAddWithoutValidation("X-Next-Page", next); }
        if (page.HasValue) { response.Headers.Add("X-Page", page.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        response.Headers.Add("X-Per-Page", "100"); return response;
    }
    private static HttpResponseMessage Redirect(string uri)
    { HttpResponseMessage response = new(HttpStatusCode.Redirect); response.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute); return response; }
    private static bool Running(HttpRequestMessage r) => r.RequestUri!.Query.Contains("scope=running", StringComparison.Ordinal);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private static async Task SettingsAsync()
    {
        using Files files = new(); GitXmlSettingsSnapshot global = await GitXmlSettingsStore.LoadAsync(files.Path("global"));
        GitLabSettings settings = GitLabSettings.Read(new(global), ["http://self.invalid:8080/group/sub/repo.git"]);
        Check(GitLabSettings.ProviderName == "Gitlab" && GitLabSettings.Prefix == "BuildServer.Gitlab." && GitLabSettings.ApiTokenKey == "BuildServer.Gitlab.ApiToken", "Original XML spelling is retained.");
        Check(!settings.Active && settings.ProjectId == 0 && settings.PagesLimit == 0 && settings.InstanceUrl == "http://self.invalid:8080" && !File.Exists(global.Path), "Prefill preserves explicit remote HTTP without GETs or writes.");
    }
    private static async Task HierarchyAsync()
    {
        using Files files = new(); files.Write("global", (GitLabSettings.EnableKey, "true"), (GitLabSettings.ProviderKey, "Gitlab"), (GitLabSettings.InstanceUrlKey, "http://ci.invalid/gitlab"), (GitLabSettings.ProjectIdKey, "3000000000"));
        files.Write("local", (GitLabSettings.EnableKey, "future-bool")); files.Write("distributed", (GitLabSettings.PagesLimitKey, "0"), ("future.key", "kept"));
        GitDistributedSettingsSnapshot snapshot = await files.Snapshot(); GitLabSettings settings = GitLabSettings.Read(snapshot);
        Check(!settings.Enabled && settings.ProjectId == 3000000000 && snapshot.GetValue("future.key") == "kept", "Invalid higher booleans mask lower true; long IDs extend old int safely.");
        files.Write("local", (GitLabSettings.PagesLimitKey, "not-an-int")); Check(GitLabSettings.Read(await files.Snapshot()).PagesLimit < 0, "Malformed limit never silently becomes unlimited.");
    }
    private static async Task ProvenanceAsync()
    {
        using Files files = new(); files.Write("global", (GitLabSettings.InstanceUrlKey, "https://gitlab.invalid/a"), (GitLabSettings.ApiTokenKey, "private-token"));
        foreach (string other in new[] { "https://evil.invalid/a", "http://gitlab.invalid/a", "https://gitlab.invalid/b", "https://gitlab.invalid:9443/a" })
        {
            files.Write("local", (GitLabSettings.InstanceUrlKey, other)); bool refused = false;
            try { _ = GitLabSettings.Read(await files.Snapshot()); } catch (ArgumentException e) { refused = true; Check(!e.ToString().Contains("private-token"), "Token is never printed."); }
            Check(refused, "No inherited token can retarget origin or basepath.");
        }
        files.Write("local", (GitLabSettings.InstanceUrlKey, "https://gitlab.invalid/a/")); Check(GitLabSettings.Read(await files.Snapshot()).ApiToken == "private-token", "Canonical same instance remains usable.");
    }
    private static Task RemotesAsync()
    {
        GitLabRemoteParser parser = new();
        foreach (string remote in new[] { "https://gitlab.com/group/sub/repo.git/", "http://gitlab.com/group/sub/repo/", "git@gitlab.com:group/sub/repo.git", "ssh://git@gitlab.com/group/sub/repo.git/", "git://gitlab.com/group/sub/repo.git" })
        { Check(parser.TryParse(remote, out GitLabRemote? data) && data.Namespace == "group/sub" && data.Name == "repo", "Every original transport supports nested namespaces."); }
        Check(parser.TryExtractGitlabDataFromRemoteUrl("https://self.invalid:5896/group/repo.git/", out string? host, out string? owner, out string? name)
            && host == "self.invalid:5896" && owner == "group" && name == "repo", "Legacy extraction returns host authority.");
        Check(parser.TryParse("git@gitlab.com:group/repo.git.git", out GitLabRemote? suffix) && suffix.Name == "repo.git", "Only the final .git suffix is stripped."); return Task.CompletedTask;
    }
    private static Task RemoteInstanceAsync()
    {
        GitLabRemoteParser parser = new(); Uri configured = new("https://self.invalid:9443/gitlab/");
        Check(parser.TryParse("https://self.invalid:9443/gitlab/group/sub/repo.git", out GitLabRemote? web, configured)
            && web.Namespace == "group/sub" && web.InstanceUri == configured, "Configured HTTP basepath is distinct from namespace.");
        Check(parser.TryParse("ssh://git@self.invalid:2222/group/sub/repo.git", out GitLabRemote? ssh, configured)
            && ssh.Namespace == "group/sub" && ssh.InstanceUri == configured, "SSH port is not guessed as a web port.");
        Check(!parser.TryParse("https://self.invalid:9443/other/group/repo", out _, configured), "Remote belongs to the configured web application."); return Task.CompletedTask;
    }
    private static Task InvalidRemotesAsync()
    {
        GitLabRemoteParser parser = new();
        foreach (string remote in new[] { "junk https://gitlab.com/group/repo.git", "https://user:private@gitlab.com/group/repo", "https://gitlab.com/group/repo?private_token=value", "https://gitlab.com/group/repo#fragment", "file:///group/repo.git", "git@host:", "git@host:group//repo", "git@host:group/../repo", "https://owner@dev.bad.com/owner/project/_git/repo" })
        { Check(!parser.TryParse(remote, out GitLabRemote? data) && data is null, "Malformed/credential-bearing URL is not partially matched."); }
        return Task.CompletedTask;
    }
    private static Task StatusAsync()
    {
        foreach ((string raw, GitLabBuildStatus expected) in new[] { ("running", GitLabBuildStatus.InProgress), ("success", GitLabBuildStatus.Success), ("failed", GitLabBuildStatus.Failure), ("canceled", GitLabBuildStatus.Stopped), ("pending", GitLabBuildStatus.Unknown), ("manual", GitLabBuildStatus.Unknown), ("skipped", GitLabBuildStatus.Unknown), ("future", GitLabBuildStatus.Unknown) })
        { GitLabPipeline p = Pipeline(1, raw); Check(p.BuildStatus == expected && !p.ShowInBuildReportTab, "Exact original status mapping and report opt-out."); }
        Check(Pipeline(1, updated: 60).Duration == TimeSpan.FromMinutes(1).Ticks, "Original duration is UpdatedAt minus CreatedAt in ticks."); return Task.CompletedTask;
    }
    private static Task SnapshotAsync()
    {
        GitLabSnapshot snapshot = new([Pipeline(100, "failed", updated: 1), Pipeline(2, "success", updated: 2), Pipeline(3, "running", Sha256, 3), Pipeline(3, "success", Sha256, 4), Pipeline(4, "failed", Sha256, 4)], Epoch);
        Check(snapshot.Commits[Sha].Pipeline.Id == 2 && snapshot.Commits[Sha].Status == GitLabBuildStatus.Success, "UpdatedAt, not largest ID or worst sibling status, controls each SHA.");
        Check(snapshot.Commits[Sha256].Pipeline.Id == 4 && snapshot.Commits[Sha256].Pipelines.Count == 1 && snapshot.Pipelines.Count == 4, "Equal timestamps use deterministic ID; duplicate pipeline updates merge and SHA256 remains valid."); return Task.CompletedTask;
    }
    private static async Task ProjectAsync()
    {
        using Handler handler = new((r, _) =>
        {
            Check(r.Method == HttpMethod.Get && r.Headers.GetValues("PRIVATE-TOKEN").Single() == "private-token", "Only explicit original token header is sent.");
            Check(r.RequestUri!.OriginalString.Contains("projects/group%2Fsub%2Frepo", StringComparison.OrdinalIgnoreCase), "Whole namespace/name is one escaped project identifier.");
            return Reply(Json(new { id = 3000000000L, path_with_namespace = "group/sub/repo", web_url = "http://self.invalid:8080/base/group/sub/repo" }));
        });
        await using GitLabApiClient client = new(Settings(instance: "http://self.invalid:8080/base"), handler);
        Check((await client.GetProjectAsync("group/sub", "repo")).Id == 3000000000L && handler.Calls == 1, "Explicit HTTP and long IDs remain usable.");
    }
    private static async Task WireAsync()
    {
        List<string> scopes = []; using Handler handler = new((r, _) =>
        {
            Check(r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/base/api/v4/projects/23/pipelines" && r.RequestUri.Query.Contains("per_page=100", StringComparison.Ordinal), "Exact original v4 project endpoint and page size.");
            Check(r.Headers.GetValues("PRIVATE-TOKEN").Single() == "private-token", "Original header, no token query."); scopes.Add(Running(r) ? "running" : "finished");
            return Reply(Page(Running(r) ? [Pipeline(1, "running")] : [Pipeline(2, "success", Sha256)]));
        });
        await using GitLabApiClient client = new(Settings(), handler); GitLabSnapshot s = await client.ReadAsync();
        Check(scopes.SequenceEqual(["running", "finished"]) && s.Commits.Count == 2 && !s.Truncated, "Both scopes are read atomically, current running first.");
    }
    private static async Task PaginationAsync()
    {
        using Handler handler = new((r, _) =>
        {
            if (Running(r)) { return Reply(Page([])); }
            bool second = r.RequestUri!.Query.Contains("page=2&", StringComparison.Ordinal);
            return Reply(second ? Page([Pipeline(2), Pipeline(1)], 2, "", 2) : Page([Pipeline(3), Pipeline(2)], 2, "2", 1));
        });
        await using GitLabApiClient client = new(Settings(), handler); GitLabSnapshot s = await client.ReadAsync();
        Check(handler.Calls == 3 && s.Pipelines.Count == 3 && !s.Truncated, "All pages are read and repeated IDs deduplicated.");
    }
    private static async Task MissingHeadersAsync()
    {
        foreach (bool links in new[] { false, true })
        {
            using Handler handler = new((r, _) =>
            {
                if (Running(r)) { return Reply(Page([])); }
                bool second = r.RequestUri!.Query.Contains("page=2&", StringComparison.Ordinal);
                HttpResponseMessage result = Page([Pipeline(second ? 2 : 1)], null, links ? null : second ? "" : "2");
                if (links && !second) { result.Headers.Add("Link", "<https://gitlab.invalid/base/api/v4/projects/23/pipelines?scope=finished&page=2&per_page=100>; rel=\"next\""); }
                return Reply(result);
            });
            await using GitLabApiClient client = new(Settings(), handler); Check((await client.ReadAsync()).Pipelines.Count == 2 && handler.Calls == 3, "Either header form supports complete traversal without totals.");
        }
    }
    private static async Task ConfigLimitAsync()
    {
        foreach (int limit in new[] { 0, 1 })
        {
            using Handler handler = new((r, _) => Reply(Running(r) ? Page([]) : r.RequestUri!.Query.Contains("page=2&", StringComparison.Ordinal) ? Page([Pipeline(2)], 2, "", 2) : Page([Pipeline(1)], 2, "2", 1)));
            await using GitLabApiClient client = new(Settings(limit), handler); GitLabSnapshot s = await client.ReadAsync();
            Check(s.Truncated == (limit == 1) && s.Pipelines.Count == (limit == 0 ? 2 : 1), "Zero does not become a one-page configured limit.");
        }
    }
    private static async Task GlobalBoundsAsync()
    {
        using Handler pages = new((r, _) => Reply(Running(r) ? Page([]) : Page([Pipeline(1)], 2, "2", 1)));
        await using GitLabApiClient pageBound = new(Settings(), pages) { MaxPages = 1 }; Check((await pageBound.ReadAsync()).Truncated, "Safety pages bound explicitly truncates even when configured unlimited.");
        using Handler requests = new((_, _) => Reply(Page([Pipeline(1, "running")])));
        await using GitLabApiClient requestBound = new(Settings(), requests) { MaxRequests = 1 }; GitLabSnapshot limited = await requestBound.ReadAsync();
        Check(limited.Truncated && limited.Pipelines.Count == 1 && requests.Calls == 1 && limited.Diagnostics.Count > 0, "Global request bound publishes only an explicitly incomplete snapshot.");
    }
    private static async Task RunningPriorityAsync()
    {
        using Handler handler = new((r, _) => Reply(Page(Running(r) ? [Pipeline(5, "running")] : [Pipeline(4, "failed")])));
        await using GitLabApiClient client = new(Settings(), handler) { MaxItems = 1 }; GitLabSnapshot s = await client.ReadAsync();
        Check(s.Truncated && s.Commits[Sha].Status == GitLabBuildStatus.InProgress && s.Pipelines.Single().Id == 5, "A large finished history cannot crowd out current running statuses.");
    }
    private static async Task ETagAsync()
    {
        using Handler handler = new((r, _) =>
        {
            if (r.Headers.IfNoneMatch.Count > 0) { return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage response = Page(Running(r) ? [Pipeline(1, "running")] : []); response.Headers.ETag = new EntityTagHeaderValue("\"v1\""); return Reply(response);
        });
        await using GitLabApiClient client = new(Settings(), handler); await client.ReadAsync(); Check((await client.ReadAsync(force: true)).Pipelines.Count == 1 && handler.Calls == 4, "304 preserves raw body and pagination metadata per scope.");
        using Handler missing = new((_, _) => Reply(new(HttpStatusCode.NotModified))); await using GitLabApiClient noCache = new(Settings(), missing); await Expect<GitLabException>(() => noCache.ReadAsync());
    }
    private static async Task CacheAsync()
    {
        using Handler handler = new((r, _) => Reply(Page(Running(r) ? [Pipeline(1)] : []))); await using GitLabApiClient client = new(Settings(), handler);
        GitLabSnapshot first = await client.ReadAsync(); Check(ReferenceEquals(first, await client.ReadAsync()) && handler.Calls == 2, "No HTTP for a fresh snapshot.");
        await client.ReadAsync(force: true); Check(handler.Calls == 4, "Force performs real revalidation.");
    }
    private static async Task CancellationAsync()
    {
        using CancellationTokenSource cancel = new(); bool abort = false;
        using Handler handler = new((r, token) =>
        {
            if (abort && !Running(r)) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            return Reply(Page(Running(r) ? [Pipeline(1, "running")] : []));
        });
        await using GitLabApiClient client = new(Settings(), handler); GitLabSnapshot baseline = await client.ReadAsync(); abort = true;
        await Expect<OperationCanceledException>(() => client.ReadAsync(force: true, cancel.Token)); Check(ReferenceEquals(baseline, await client.ReadAsync()), "Aborted pagination never replaces the accepted snapshot.");
        using CancellationTokenSource already = new(); already.Cancel(); int count = handler.Calls;
        await Expect<OperationCanceledException>(() => client.GetProjectAsync("g", "r", already.Token)); Check(handler.Calls == count, "Pre-cancellation sends no request.");
    }
    private static async Task DrainAsync()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Handler handler = new(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Page([]); });
        GitLabApiClient client = new(Settings(), handler); Task task = client.ReadAsync(); await entered.Task; await client.DisposeAsync();
        await Expect<OperationCanceledException>(() => task); await Expect<ObjectDisposedException>(() => client.ReadAsync()); Check(handler.Calls == 1, "Close waits for owned request cancellation and starts no new poll.");
    }
    private static async Task BodyTimeoutAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        await using GitLabApiClient client = new(Settings(), handler) { RequestTimeout = TimeSpan.FromMilliseconds(60) };
        GitLabException error = await Expect<GitLabException>(() => client.GetProjectAsync("g", "r"));
        Check(error.Message.Contains("timed out", StringComparison.Ordinal) && stream.ReadStarted.Task.IsCompleted && stream.Disposed, "Deadline covers a body blocked after successful headers, and disposes it.");
    }
    private static async Task BodyCancellationAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        await using GitLabApiClient client = new(Settings(), handler); using CancellationTokenSource cancel = new();
        Task request = client.GetProjectAsync("g", "r", cancel.Token); await stream.ReadStarted.Task; cancel.Cancel();
        await Expect<OperationCanceledException>(() => request); Check(stream.Disposed, "Caller cancellation remains cancellation and drains the body stream.");
    }
    private static async Task RedirectAsync()
    {
        int calls = 0; using Handler handler = new((r, _) =>
        { Check(r.Headers.GetValues("PRIVATE-TOKEN").Single() == "private-token", "Same configured instance only."); return Reply(++calls == 1 ? Redirect("/gitlab/api/v4/projects/123") : Json(new { id = 123 })); });
        await using GitLabApiClient client = new(Settings(instance: "http://self.invalid:8080/gitlab/"), handler);
        Check((await client.GetProjectAsync("old", "name")).Id == 123 && calls == 2, "Official moved-project GET redirect works within HTTP origin/basepath.");
    }
    private static async Task UnsafeRedirectAsync()
    {
        foreach (string target in new[] { "https://evil.invalid/base/api/v4/projects/23/pipelines?scope=running&page=2&per_page=100", "http://gitlab.invalid/base/api/v4/projects/23/pipelines?scope=running&page=2&per_page=100", "/other/api/v4/projects/23/pipelines?scope=running&page=2&per_page=100", "/base/api/v4/projects/24/pipelines?scope=running&page=2&per_page=100" })
        {
            foreach (bool link in new[] { false, true })
            {
                using Handler handler = new((_, _) =>
                {
                    if (!link) { return Reply(Redirect(target)); }
                    HttpResponseMessage response = Page([Pipeline(1)], null, null); response.Headers.Add("Link", "<" + target + ">; rel=\"next\""); return Reply(response);
                });
                await using GitLabApiClient client = new(Settings(), handler); await Expect<GitLabException>(() => client.ReadAsync()); Check(handler.Calls == 1, "Token is not sent to unsafe redirect/pagination destination.");
            }
        }
    }
    private static async Task InvalidPaginationAsync()
    {
        foreach ((string key, string value) in new[] { ("X-Next-Page", "private-payload"), ("X-Next-Page", "1"), ("X-Next-Page", "3"), ("X-Total-Pages", "private-payload"), ("X-Page", "2") })
        {
            using Handler handler = new((_, _) => { HttpResponseMessage r = Page([Pipeline(1)], null, null); r.Headers.TryAddWithoutValidation(key, value); return Reply(r); });
            await using GitLabApiClient client = new(Settings(), handler); Check(!(await Expect<GitLabException>(() => client.ReadAsync())).ToString().Contains("private-payload"), "Malformed/cyclic metadata is not exposed or followed.");
        }
    }
    private static async Task ErrorsAsync()
    {
        foreach (int code in new[] { 401, 403, 404, 429, 500 })
        {
            using Handler handler = new((_, _) => Reply(new((HttpStatusCode)code) { Content = new StringContent("private-token private-payload") }));
            await using GitLabApiClient client = new(Settings(), handler); GitLabException error = await Expect<GitLabException>(() => client.ReadAsync());
            Check(error.StatusCode == code && !error.ToString().Contains("private-") && handler.Calls == 1, "Status is readable, secrets/response/transport details are not logged or wrapped.");
        }
    }
    private static async Task JsonAsync()
    {
        foreach (string body in new[] { "private-token not json", "null", "{}", "[null]", "[{\"id\":1,\"sha\":\"invalid\",\"status\":\"success\"}]", "[{\"id\":0}]", new string('[', 40) + "0" + new string(']', 40) })
        {
            using Handler handler = new((_, _) => Reply(Text(body))); await using GitLabApiClient client = new(Settings(), handler);
            Check(!(await Expect<GitLabException>(() => client.ReadAsync())).ToString().Contains("private-token"), "Invalid list/identity/hash/depth never creates a partial successful snapshot.");
        }
    }
    private static async Task SizeAsync()
    {
        foreach (bool chunked in new[] { false, true })
        {
            byte[] bytes = Encoding.UTF8.GetBytes("[\"" + new string('x', 5000) + "\"]");
            using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = chunked ? new StreamContent(new NonSeekStream(bytes)) : new ByteArrayContent(bytes) }));
            await using GitLabApiClient client = new(Settings(), handler) { MaxResponseBytes = 1024 }; await Expect<GitLabException>(() => client.ReadAsync());
        }
    }
    private static async Task TransportAsync()
    {
        foreach (bool timeout in new[] { false, true })
        {
            using Handler handler = new((_, _) => timeout ? Task.FromException<HttpResponseMessage>(new OperationCanceledException("private-token")) : throw new HttpRequestException("private-token"));
            await using GitLabApiClient client = new(Settings(), handler); Check(!(await Expect<GitLabException>(() => client.ReadAsync())).ToString().Contains("private-token"), "Original exceptions never leak their content.");
        }
    }
    private static Task PagesAsync()
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No HTTP")); using GitLabApiClient client = new(Settings(), handler);
        GitLabPipeline p = Pipeline(1) with { WebUrl = "https://gitlab.invalid/base/g/sub/r/-/pipelines/1?view=summary#details" };
        Check(client.BuildPageUri(p)?.AbsoluteUri == p.WebUrl && handler.Calls == 0, "Only explicit valid same-instance page is returned exactly.");
        foreach (string url in new[] { "file:///tmp/pipeline", "https://evil.invalid/base/g/r/-/pipelines/1", "https://u:private@gitlab.invalid/base/g/r/-/pipelines/1", "https://gitlab.invalid/other/g/r/-/pipelines/1", "https://gitlab.invalid/base/g/r/-/pipelines/2" })
        { Check(client.BuildPageUri(p with { WebUrl = url }) is null, "Unsafe origin/basepath/identity cannot open a page."); }
        return Task.CompletedTask;
    }
    private static async Task AdmissionAsync()
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No network expected"));
        await using GitLabApiClient client = new(new() { InstanceUrl = "http://self.invalid/gitlab" }, handler);
        Check((await client.ReadAsync()).Pipelines.Count == 0 && handler.Calls == 0, "Polling is disabled by default.");
        foreach (string url in new[] { "file:///tmp/p", "https://user:private@host", "https://host?token=private", "https://host#fragment" })
        {
            bool refused = false; try { _ = GitLabSettings.ParseInstanceUri(url); } catch (ArgumentException e) { refused = true; Check(!e.ToString().Contains("private"), "Admission errors redact URL credentials."); }
            Check(refused, "Unsafe instance fails before a request.");
        }
        Check(!Settings().ToString().Contains("private-token") && !Pipeline(1).ToString().Contains("private-token"), "Debug strings redact sensitive configuration.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) { Calls++; return send(r, t); } }
    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { ReadStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-gitlab-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name + ".settings");
        public void Write(string name, params (string Key, string Value)[] values) => File.WriteAllText(Path(name), new XElement("dictionary", values.Select(v =>
            new XElement("item", new XElement("key", new XElement("string", v.Key)), new XElement("value", new XElement("string", v.Value))))).ToString());
        public async Task<GitDistributedSettingsSnapshot> Snapshot() => new(await GitXmlSettingsStore.LoadAsync(Path("global")), await GitXmlSettingsStore.LoadAsync(Path("local")), await GitXmlSettingsStore.LoadAsync(Path("distributed")));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
