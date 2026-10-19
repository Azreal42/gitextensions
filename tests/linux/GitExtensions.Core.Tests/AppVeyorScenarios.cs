using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GitExtensions.Linux;

internal static class AppVeyorScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("AppVeyor original provider keys defaults and XML hierarchy retain unknown values without writes", SettingsAsync),
        ("AppVeyor known project variables remain valid drafts and require runtime expansion before HTTP", VariablesAsync),
        ("AppVeyor pipe projects resolve slugs upstream accounts and duplicate names without lossy remapping", ProjectsAsync),
        ("AppVeyor original master JSON and approved identity duration status report and URL are retained", MasterFixtureAsync),
        ("AppVeyor original PR JSON and approved head commit version duration and PR link are retained", PullRequestFixtureAsync),
        ("AppVeyor explicit public projects use anonymous GET history25 and original JSON accept header", GuestWireAsync),
        ("AppVeyor account discovery uses original bearer endpoint and expands returned slugs", DiscoveryAsync),
        ("AppVeyor v2 account enumeration uses its account prefix while project history keeps original route", V2Async),
        ("AppVeyor inactive settings make no requests and incomplete active configuration reports a fixed error", DisabledAsync),
        ("AppVeyor exact original status mapping retains queued running and future unknown states", StatusAsync),
        ("AppVeyor snapshots select one newest started build per SHA across projects with stable ties", SelectionAsync),
        ("AppVeyor visible SHA admission precedes detail requests and supports nonzero SHA256 commits", AdmissionAsync),
        ("AppVeyor success failure details require the checkbox while stopped and unknown do not request details", DetailOptionAsync),
        ("AppVeyor running details always load and preserve last job and Windows skipped formula", RunningDetailsAsync),
        ("AppVeyor running builds leaving the history window continue detail updates until their terminal status", RunningContinuationAsync),
        ("AppVeyor ordinary ticks poll only running details and never rediscover history or reload finished tests", PollingAsync),
        ("AppVeyor renamed versions recover only404 and verify the same stable numerical build identity", VersionRecoveryAsync),
        ("AppVeyor version recovery refuses another build and authentication errors never trigger recovery", BadRecoveryAsync),
        ("AppVeyor created fallback missing dates milliseconds and original text tooltip formatting stay explicit", DurationAsync),
        ("AppVeyor invalid counters dates and mismatched empty job details cannot publish false success", InvalidDetailsAsync),
        ("AppVeyor encoded project and version pages and fixed public PR links never carry API credentials", LinksAsync),
        ("AppVeyor project item request and25 history bounds publish visible incomplete diagnostics", BoundsAsync),
        ("AppVeyor ETag304 and force revalidate while ordinary time cache avoids unnecessary HTTP", CacheAsync),
        ("AppVeyor malformed refresh leaves both last accepted snapshot and conditional cache atomically unchanged", AtomicAsync),
        ("AppVeyor same resource redirects are bounded and preserve explicit authentication", RedirectAsync),
        ("AppVeyor redirect origins paths queries credentials fragments and HTTP cannot steal bearer tokens", UnsafeRedirectAsync),
        ("AppVeyor HTTP failures retain useful status without secret response or transport exception text", ErrorsAsync),
        ("AppVeyor malformed deep oversized and uncached304 responses are never silently empty histories", InvalidResponsesAsync),
        ("AppVeyor chunked response bounds apply without trusting a content length header", ChunkedAsync),
        ("AppVeyor caller cancellation before headers remains cancellation and retains the prior snapshot", CancellationAsync),
        ("AppVeyor request deadline covers blocked response bodies and disposes their streams", BodyTimeoutAsync),
        ("AppVeyor caller body cancellation and disposal drain all owned operations before refusing new work", DrainAsync),
        ("AppVeyor cancellation while waiting for the read gate never starts an unowned second request", GateAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Sha256 = new('a', 64);
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static AppVeyorSettings Settings(string project = "account/repo", string account = "", string token = "", bool load = false) => new()
    { Enabled = true, Provider = AppVeyorSettings.ProviderName, ProjectName = project, AccountName = account, AccountToken = token, LoadTestsResults = load };
    private static Dictionary<string, object?> Build(long id = 1, string status = "success", string sha = Sha, string version = "1.0", int started = 1) => new()
    { ["buildId"] = id, ["version"] = version, ["commitId"] = sha, ["status"] = status, ["branch"] = "main", ["started"] = Epoch.AddSeconds(started), ["created"] = Epoch, ["updated"] = Epoch.AddSeconds(started + 60) };
    private static object Job(string status = "success", int total = 10, int passed = 7, int failed = 2) => new { status, testsCount = total, passedTestsCount = passed, failedTestsCount = failed };
    private static HttpResponseMessage History(params Dictionary<string, object?>[] builds)
        => Json(new { project = new { repositoryType = "gitHub", repositoryName = "owner/repo" }, builds });
    private static HttpResponseMessage Detail(Dictionary<string, object?> build, params object[] jobs)
        => Json(new { build = new Dictionary<string, object?>(build) { ["jobs"] = jobs } });
    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> Reply(HttpResponseMessage value) => Task.FromResult(value);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> body) where T : Exception
    { try { await body(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static bool IsHistory(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/history", StringComparison.Ordinal);
    private static HttpResponseMessage Redirect(string uri)
    { HttpResponseMessage response = new(HttpStatusCode.Redirect); response.Headers.Location = new(uri, UriKind.RelativeOrAbsolute); return response; }

    private static async Task SettingsAsync()
    {
        using Files files = new(); GitDistributedSettingsSnapshot empty = await files.Snapshot();
        AppVeyorSettings defaults = AppVeyorSettings.Read(empty);
        Check(AppVeyorSettings.ProviderName == "AppVeyor" && AppVeyorSettings.AccountTokenKey == "BuildServer.AppVeyor.AppVeyorAccountToken"
            && AppVeyorSettings.LoadTestsResultsKey == "BuildServer.AppVeyor.AppVeyorLoadTestsResults" && AppVeyorSettings.Keys.Count == 7, "Exact original provider and XML keys.");
        Check(!defaults.Active && defaults.ProjectName == "" && !defaults.LoadTestsResults && !File.Exists(files.Path("global")), "Read has no runtime folder fallback or writes.");
        files.Write("global", (AppVeyorSettings.EnableKey, "true"), (AppVeyorSettings.ProviderKey, "AppVeyor"), (AppVeyorSettings.AccountNameKey, "global"), (AppVeyorSettings.ProjectNameKey, "repo"), ("future.key", "retained"));
        files.Write("local", (AppVeyorSettings.EnableKey, "future-value"), (AppVeyorSettings.AccountNameKey, "local"));
        GitDistributedSettingsSnapshot scoped = await files.Snapshot(); AppVeyorSettings s = AppVeyorSettings.Read(scoped);
        Check(!s.Enabled && s.AccountName == "local" && scoped.GetValue("future.key") == "retained" && s.ProjectName == "repo", "Higher raw values mask lower ones and unrelated keys survive.");
        Check(AppVeyorSettings.Read(empty, "visual-folder").ProjectName == "visual-folder", "Only an explicitly supplied default fills an absent key.");
    }
    private static Task VariablesAsync()
    {
        Settings("{cRepoProject}/{cRepoShortName}").Validate();
        Check(AppVeyorProjects.Resolve("{cRepoProject}/{cRepoShortName}", "", s => s.Replace("{cRepoProject}", "up").Replace("{cRepoShortName}", "repo")).Single() == "up/repo", "Known variables expand without rewriting the stored draft.");
        bool refused = false; try { _ = new AppVeyorApiClient(Settings("{cRepoProject}/{cRepoShortName}")); } catch (ArgumentException) { refused = true; }
        Check(refused, "Unresolved draft cannot reach HTTP."); refused = false;
        try { Settings("{unknown}/repo").Validate(); } catch (ArgumentException) { refused = true; }
        Check(refused, "Only original variable spellings are accepted."); return Task.CompletedTask;
    }
    private static Task ProjectsAsync()
    {
        Check(AppVeyorProjects.Resolve("repo|upstream/other||repo", "account").SequenceEqual(["account/repo", "upstream/other"]), "Pipes preserve explicit upstream and prefix only slugs.");
        Check(AppVeyorProjects.Resolve("slug", "").Count == 0, "Slug without an account is not guessed from a Git host.");
        foreach (string bad in new[] { "../repo", "a/b/c", "a/r?token=x", "a/r%2Fother", "a/\\bad" })
        { bool refused = false; try { _ = AppVeyorProjects.Resolve(bad, ""); } catch (ArgumentException) { refused = true; } Check(refused, "Unsafe project path is rejected before HTTP."); }
        Settings("upstream/public", token: "").Validate(); return Task.CompletedTask;
    }
    private static Task MasterFixtureAsync() => FixtureAsync("master");
    private static Task PullRequestFixtureAsync() => FixtureAsync("pull_request_build");
    private static async Task FixtureAsync(string suffix)
    {
        string root = FixtureDirectory(); string fixture = File.ReadAllText(System.IO.Path.Combine(root, "MockData", "AppVeyorResult_" + suffix + ".json"));
        string name = suffix == "master" ? "master" : "pull_request";
        string approved = File.ReadAllText(System.IO.Path.Combine(root, "ApprovedFiles", "AppVeyorAdapterTests.Should_return_a_build_Info_When_Json_content_is_the_one_of_a_" + name + "_build.verified.txt"));
        string Expected(string key) => approved.Split('\n').Select(line => line.Trim().TrimStart('-', ' ')).First(line => line.StartsWith(key + ":", StringComparison.Ordinal))[(key.Length + 1)..].Trim();
        using Handler handler = new((_, _) => Reply(Text(fixture))); await using AppVeyorApiClient client = new(Settings("account/repo"), handler);
        AppVeyorBuild b = (await client.ReadAsync()).Builds.Single();
        Check(b.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture) == Expected("BuildId") && b.Version == Expected("Id")
            && b.CommitHash == Expected("CommitId") && b.DurationMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) == Expected("Duration")
            && b.Status.ToString() == Expected("Status") && b.PageUri?.AbsoluteUri == Expected("Url") && b.ShowInBuildReportTab && !b.IsRunning, "Existing Windows JSON and approved oracle remain the authorities.");
        Check((b.PullRequestUri?.AbsoluteUri ?? "") == Expected("PullRequestUrl") && b.Branch == Expected("Branch") && handler.Calls == 1, "PR-head SHA wins and initial parsing does not query optional details.");
    }
    private static string FixtureDirectory()
    {
        const string relative = "tests/plugins/UnitTests/BuildServerIntegration/AppVeyorIntegration.Tests";
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        { string path = System.IO.Path.Combine(current.FullName, relative); if (Directory.Exists(path)) { return path; } }
        throw new InvalidOperationException("The original AppVeyor fixtures are missing from the source checkout.");
    }
    private static async Task GuestWireAsync()
    {
        using Handler handler = new((r, _) =>
        {
            Check(r.Method == HttpMethod.Get && r.RequestUri!.AbsoluteUri == "https://ci.appveyor.com/api/projects/account/public/history?recordsNumber=25"
                && r.Headers.Authorization is null && r.Headers.Accept.Single().MediaType == "application/json", "Original fixed endpoint and anonymous GET."); return Reply(History(Build()));
        });
        await using AppVeyorApiClient client = new(Settings("public", "account"), handler); Check((await client.ReadAsync()).Builds.Count == 1, "Public project requires no account token.");
    }
    private static async Task DiscoveryAsync()
    {
        using Handler handler = new((r, _) =>
        {
            Check(r.Headers.Authorization is { Scheme: "Bearer", Parameter: "fake-token" }, "Only the explicit token is sent.");
            string path = r.RequestUri!.AbsolutePath;
            return Reply(path == "/api/projects/" ? Json(new[] { new { slug = "first" }, new { slug = "second" } }) : History(Build(path.Contains("first", StringComparison.Ordinal) ? 1 : 2, sha: path.Contains("first", StringComparison.Ordinal) ? Sha : Sha256)));
        });
        await using AppVeyorApiClient client = new(Settings("", "account", "fake-token"), handler);
        AppVeyorSnapshot s = await client.ReadAsync(); Check(handler.Calls == 3 && s.Builds.Select(b => b.ProjectId).ToHashSet().SetEquals(["account/first", "account/second"]), "Every accessible discovered project is queried.");
    }
    private static async Task V2Async()
    {
        List<string> paths = []; using Handler handler = new((r, _) => { paths.Add(r.RequestUri!.AbsolutePath); return Reply(paths.Count == 1 ? Json(new[] { new { slug = "repo" } }) : History(Build())); });
        await using AppVeyorApiClient client = new(Settings("", "account", "v2.fake"), handler); await client.ReadAsync();
        Check(paths.SequenceEqual(["/api/account/account/projects/", "/api/projects/account/repo/history"]), "Original v2 route distinction is preserved.");
    }
    private static async Task DisabledAsync()
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No network"));
        await using AppVeyorApiClient disabled = new(new(), handler); Check((await disabled.ReadAsync()).Builds.Count == 0 && handler.Calls == 0, "Inactive provider makes no HTTP calls.");
        await using AppVeyorApiClient incomplete = new(Settings(""), new Handler((_, _) => throw new InvalidOperationException("No network")));
        Check((await Expect<AppVeyorException>(() => incomplete.ReadAsync())).Message.Contains("Configure", StringComparison.Ordinal), "Incomplete active config is visible, not false empty success.");
    }
    private static Task StatusAsync()
    {
        foreach ((string raw, AppVeyorBuildStatus status) in new[] { ("success", AppVeyorBuildStatus.Success), ("failed", AppVeyorBuildStatus.Failure), ("cancelled", AppVeyorBuildStatus.Stopped), ("queued", AppVeyorBuildStatus.InProgress), ("running", AppVeyorBuildStatus.InProgress), ("canceled", AppVeyorBuildStatus.Unknown), ("future", AppVeyorBuildStatus.Unknown) })
        { Check(AppVeyorBuild.ParseStatus(raw) == status, "Exact original status mapping."); }
        return Task.CompletedTask;
    }
    private static Task SelectionAsync()
    {
        AppVeyorBuild Old(long id, string project, int start) => new() { BuildId = id, ProjectId = project, CommitHash = Sha, Version = "v", StartedAt = Epoch.AddSeconds(start), Status = AppVeyorBuildStatus.Success };
        AppVeyorSnapshot s = new([Old(100, "a/old", 1), Old(2, "a/new", 3), Old(3, "b/tie", 3)], Epoch);
        Check(s.Builds.Count == 1 && s.Commits[Sha].Build.BuildId == 2 && s.Commits[Sha].Builds.Count == 1, "StartedAt across all projects wins; stable input order breaks equal timestamps."); return Task.CompletedTask;
    }
    private static async Task AdmissionAsync()
    {
        using Handler handler = new((r, _) => { Check(IsHistory(r), "Hidden commits must not request details."); return Reply(History(Build(1, "running"), Build(2, sha: Sha256), Build(3, sha: new string('0', 40)), Build(4, sha: "bad"))); });
        await using AppVeyorApiClient client = new(Settings(), handler, admitCommit: sha => sha == Sha256);
        AppVeyorSnapshot s = await client.ReadAsync(); Check(s.Builds.Single().CommitHash == Sha256 && handler.Calls == 1 && s.Diagnostics.Count > 0, "Visible SHA256 retained; invalid hashes are diagnosed and zero is not a revision.");
    }
    private static async Task DetailOptionAsync()
    {
        foreach (string status in new[] { "success", "failed", "cancelled", "future" })
        {
            foreach (bool load in new[] { false, true })
            {
                using Handler handler = new((r, _) => Reply(IsHistory(r) ? History(Build(status: status)) : Detail(Build(status: status), Job(status))));
                await using AppVeyorApiClient client = new(Settings(load: load), handler); await client.ReadAsync();
                Check(handler.Calls == (load && (status is "success" or "failed") ? 2 : 1), "Checkbox controls finished success/failure enrichment only.");
            }
        }
    }
    private static async Task RunningDetailsAsync()
    {
        using Handler handler = new((r, _) => Reply(IsHistory(r) ? History(Build(status: "queued")) : Detail(Build(status: "queued"), Job("failed", 3, 0, 3), Job("running"))));
        await using AppVeyorApiClient client = new(Settings(), handler); AppVeyorBuild b = (await client.ReadAsync()).Builds.Single();
        Check(handler.Calls == 2 && b.IsRunning && b.Tests is { Total: 10, Passed: 7, Failed: 2, Skipped: 3 } && b.ProgressCounter == 1
            && b.Tooltip.Contains("In progress.", StringComparison.Ordinal) && b.Description.Contains("10 tests ( 2 failed, 3 skipped )", StringComparison.Ordinal), "Last job and compatibility skipped formula, not matrix aggregation.");
    }
    private static async Task RunningContinuationAsync()
    {
        int histories = 0, details = 0; using Handler handler = new((r, _) =>
        {
            if (IsHistory(r)) { histories++; return Reply(histories == 1 ? History(Build(status: "running")) : History()); }
            details++; return Reply(Detail(Build(status: "running"), Job(details == 1 ? "running" : "success")));
        });
        await using AppVeyorApiClient client = new(Settings(), handler); Check((await client.ReadAsync()).Builds.Single().IsRunning, "First details are running.");
        AppVeyorSnapshot completed = await client.ReadAsync(true); Check(histories == 2 && details == 2 && completed.Builds.Single().Status == AppVeyorBuildStatus.Success, "Running details survive an explicit refresh with a shifted history window.");
    }
    private static async Task PollingAsync()
    {
        int histories = 0, running = 0, finished = 0;
        using Handler handler = new((r, _) =>
        {
            if (IsHistory(r)) { histories++; return Reply(History(Build(1, "running", version: "active"), Build(2, sha: Sha256, version: "done"))); }
            if (r.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal))
            { running++; return Reply(Detail(Build(1, "running", version: "active"), Job(running == 1 ? "running" : "success"))); }
            finished++; return Reply(Detail(Build(2, sha: Sha256, version: "done"), Job()));
        });
        await using AppVeyorApiClient client = new(Settings(load: true), handler) { CacheDuration = TimeSpan.Zero };
        await client.ReadAsync(); await client.ReadAsync(); await client.ReadAsync();
        Check(histories == 1 && running == 2 && finished == 1, "Ticks do not rediscover projects/history or reload terminal test details; the final running transition is observed.");
        await client.ReadAsync(true); Check(histories == 2 && finished == 2, "Explicit refresh really reloads history and optional terminal test details.");
    }
    private static async Task VersionRecoveryAsync()
    {
        List<string> urls = []; using Handler handler = new((r, _) =>
        {
            string url = r.RequestUri!.PathAndQuery; urls.Add(url);
            return Reply(url.EndsWith("/build/old", StringComparison.Ordinal) ? new(HttpStatusCode.NotFound)
                : url.Contains("startBuildId", StringComparison.Ordinal) ? History(Build(3000000000, "running", version: "renamed"))
                : IsHistory(r) ? History(Build(3000000000, "running", version: "old")) : Detail(Build(3000000000, "running", version: "renamed"), Job()));
        });
        await using AppVeyorApiClient client = new(Settings(), handler); AppVeyorBuild b = (await client.ReadAsync()).Builds.Single();
        Check(b.BuildId == 3000000000 && b.Version == "renamed" && b.PageUri!.AbsolutePath.EndsWith("/build/renamed", StringComparison.Ordinal)
            && urls.Count == 4 && urls[2].EndsWith("recordsNumber=1&startBuildId=3000000001", StringComparison.Ordinal), "Long stable identity and exact one-step version recovery.");
    }
    private static async Task BadRecoveryAsync()
    {
        foreach (HttpStatusCode code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            using Handler handler = new((r, _) => Reply(r.RequestUri!.Query.Contains("startBuildId", StringComparison.Ordinal) ? History(Build(2, "running", version: "other"))
                : IsHistory(r) ? History(Build(status: "running")) : new(code)));
            await using AppVeyorApiClient client = new(Settings(token: "fake-secret"), handler); AppVeyorException e = await Expect<AppVeyorException>(() => client.ReadAsync());
            Check(handler.Calls == (code == HttpStatusCode.NotFound ? 3 : 2) && !e.ToString().Contains("fake-secret", StringComparison.Ordinal), "Only404 tries recovery and another identity is refused.");
        }
    }
    private static async Task DurationAsync()
    {
        Dictionary<string, object?> value = Build(); value.Remove("started"); value["updated"] = Epoch.AddMilliseconds(612026);
        using Handler handler = new((_, _) => Reply(History(value))); await using AppVeyorApiClient client = new(Settings(), handler);
        AppVeyorBuild b = (await client.ReadAsync()).Builds.Single(); Check(b.DurationMilliseconds == 612026 && b.Description == "10:12  1.0"
            && b.Tooltip == "Success" + Environment.NewLine + "10:12" + Environment.NewLine + "1.0", "Created fallback, ms and original whitespace formatter.");
        value.Remove("created"); value.Remove("updated"); Check((await client.ReadAsync(true)).Builds.Single().DurationMilliseconds == 0, "Missing dates retain Windows zero duration.");
    }
    private static async Task InvalidDetailsAsync()
    {
        foreach (int kind in Enumerable.Range(0, 6))
        {
            using Handler handler = new((r, _) =>
            {
                if (IsHistory(r)) { return Reply(History(Build(status: "running"))); }
                Dictionary<string, object?> value = Build(kind == 3 ? 2 : 1, "running"); if (kind == 4) { value["started"] = "not-a-date"; }
                return Reply(kind == 0 ? Detail(value) : Detail(value, Job("success", kind == 1 ? -1 : kind == 5 ? 0 : 10, kind == 2 ? 10 : 7, 2)));
            });
            await using AppVeyorApiClient client = new(Settings(), handler); await Expect<AppVeyorException>(() => client.ReadAsync());
        }
    }
    private static async Task LinksAsync()
    {
        using Handler handler = new((r, _) => { Check(r.RequestUri!.OriginalString.Contains("repo%20space/history", StringComparison.Ordinal), "Project component is URL encoded."); return Reply(History(Build(version: "version /?#"))); });
        await using AppVeyorApiClient client = new(Settings("account/repo space", token: "fake-secret"), handler); AppVeyorBuild b = (await client.ReadAsync()).Builds.Single();
        Check(client.BuildPageUri(b)?.OriginalString.Contains("version%20%2F%3F%23", StringComparison.Ordinal) == true && !b.PageUri!.OriginalString.Contains("fake-secret", StringComparison.Ordinal), "Version is one encoded component without token.");
        foreach ((string type, string host, string suffix) in new[] { ("gitHub", "github.com", "/pull/7"), ("bitBucket", "bitbucket.org", "/pull-requests/7"), ("gitLab", "gitlab.com", "/merge_requests/7"), ("vso", "", "") })
        {
            Dictionary<string, object?> pr = Build(); pr["pullRequestId"] = "7";
            using Handler links = new((_, _) => Reply(Json(new { project = new { repositoryType = type, repositoryName = "owner/repo" }, builds = new[] { pr } })));
            await using AppVeyorApiClient other = new(Settings(), links); Uri? uri = (await other.ReadAsync()).Builds.Single().PullRequestUri;
            Check(host.Length == 0 ? uri is null : uri is not null && uri.Host == host && uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal), "Original provider PR routes only.");
        }
        Check(client.BuildPageUri(b with { ProjectId = "a/../b" }) is null, "Arbitrary DTO cannot inject an unsafe browser URL.");
    }
    private static async Task BoundsAsync()
    {
        using Handler projects = new((_, _) => Reply(History(Build())));
        await using AppVeyorApiClient p = new(Settings("a/first|b/second"), projects) { MaxProjects = 1 };
        Check((await p.ReadAsync()).Truncated && projects.Calls == 1, "Project bound is explicit.");
        using Handler items = new((_, _) => Reply(History(Build(), Build(2, sha: Sha256)))); await using AppVeyorApiClient i = new(Settings(), items) { MaxItems = 1 };
        Check((await i.ReadAsync()) is { Truncated: true, Builds.Count: 1 }, "Item bound is explicit.");
        using Handler requests = new((_, _) => Reply(History(Build(status: "running")))); await using AppVeyorApiClient q = new(Settings(), requests) { MaxRequests = 1 };
        Check((await q.ReadAsync()).Truncated && requests.Calls == 1, "Missing detail due to request bound is never called complete.");
        using Handler window = new((_, _) => Reply(History(Enumerable.Range(1, 25).Select(n => Build(n, sha: n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture))).ToArray())));
        await using AppVeyorApiClient w = new(Settings(), window); Check((await w.ReadAsync()) is { Truncated: true, Builds.Count: 25 }, "A full25 window is explicitly bounded history.");
    }
    private static async Task CacheAsync()
    {
        using Handler handler = new((r, _) =>
        {
            if (r.Headers.IfNoneMatch.Count > 0) { Check(r.Headers.IfNoneMatch.Single().Tag == "\"one\"", "Exact conditional validator."); return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage response = History(Build()); response.Headers.ETag = new("\"one\""); return Reply(response);
        });
        await using AppVeyorApiClient client = new(Settings(), handler); AppVeyorSnapshot a = await client.ReadAsync(), b = await client.ReadAsync();
        Check(ReferenceEquals(a, b) && handler.Calls == 1, "Ordinary recent snapshot avoids HTTP."); Check((await client.ReadAsync(true)).Builds.Count == 1 && handler.Calls == 2, "Force still revalidates ETag.");
    }
    private static async Task AtomicAsync()
    {
        int calls = 0; using Handler handler = new((r, _) =>
        {
            calls++; if (calls == 2) { HttpResponseMessage bad = Text("{bad"); bad.Headers.ETag = new("\"bad\""); return Reply(bad); }
            if (calls == 3) { Check(r.Headers.IfNoneMatch.Single().Tag == "\"good\"", "Failed parse cannot advance conditional cache."); return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage good = History(Build()); good.Headers.ETag = new("\"good\""); return Reply(good);
        });
        await using AppVeyorApiClient client = new(Settings(), handler); AppVeyorSnapshot original = await client.ReadAsync(); await Expect<AppVeyorException>(() => client.ReadAsync(true));
        Check(ReferenceEquals(original, await client.ReadAsync()) && calls == 2, "Failed refresh leaves original publication."); Check((await client.ReadAsync(true)).Builds.Count == 1 && calls == 3, "Original conditional cache survives.");
    }
    private static async Task RedirectAsync()
    {
        int calls = 0; using Handler handler = new((r, _) => { calls++; Check(r.Headers.Authorization?.Parameter == "fake-token", "Same fixed resource retains only explicit token."); return Reply(calls == 1 ? Redirect(r.RequestUri!.AbsoluteUri) : History(Build())); });
        await using AppVeyorApiClient client = new(Settings(token: "fake-token"), handler); Check((await client.ReadAsync()).Builds.Count == 1 && calls == 2, "Bounded same-resource redirect is followed.");
    }
    private static async Task UnsafeRedirectAsync()
    {
        foreach (string url in new[] { "https://other.invalid/api/projects/account/repo/history?recordsNumber=25", "http://ci.appveyor.com/api/projects/account/repo/history?recordsNumber=25", "https://ci.appveyor.com/project/a/b", "https://ci.appveyor.com/api/projects/other/repo/history?recordsNumber=25", "https://ci.appveyor.com/api/projects/account/repo/history?recordsNumber=1", "https://user:secret@ci.appveyor.com/api/projects/account/repo/history?recordsNumber=25", "https://ci.appveyor.com/api/projects/account/repo/history?recordsNumber=25#fragment" })
        {
            using Handler handler = new((_, _) => Reply(Redirect(url))); await using AppVeyorApiClient client = new(Settings(token: "fake-token"), handler);
            AppVeyorException e = await Expect<AppVeyorException>(() => client.ReadAsync()); Check(handler.Calls == 1 && !e.ToString().Contains("fake-token", StringComparison.Ordinal) && !e.ToString().Contains("secret", StringComparison.Ordinal), "No second request leaks credentials or original URL text.");
        }
    }
    private static async Task ErrorsAsync()
    {
        foreach (int status in new[] { 401, 403, 404, 429, 500 })
        {
            using Handler handler = new((_, _) => Reply(new((HttpStatusCode)status) { Content = new StringContent("fake-secret-body") }));
            await using AppVeyorApiClient client = new(Settings(token: "fake-token"), handler); AppVeyorException e = await Expect<AppVeyorException>(() => client.ReadAsync());
            Check(e.StatusCode == status && !e.ToString().Contains("fake-secret", StringComparison.Ordinal) && !e.ToString().Contains("fake-token", StringComparison.Ordinal), "Fixed useful status without payload or token.");
        }
        using Handler transport = new((_, _) => throw new HttpRequestException("fake-secret-transport")); await using AppVeyorApiClient failure = new(Settings(), transport);
        Check(!(await Expect<AppVeyorException>(() => failure.ReadAsync())).ToString().Contains("fake-secret", StringComparison.Ordinal), "No inner transport exception is retained.");
    }
    private static async Task InvalidResponsesAsync()
    {
        foreach (string json in new[] { "null", "{", "{}", "{\"builds\":null}", new string('[', 40) + "0" + new string(']', 40), "{\"builds\":[],\"x\":\"" + new string('x', 3000) + "\"}" })
        { using Handler handler = new((_, _) => Reply(Text(json))); await using AppVeyorApiClient client = new(Settings(), handler) { MaxResponseBytes = 1024 }; await Expect<AppVeyorException>(() => client.ReadAsync()); }
        using Handler uncached = new((_, _) => Reply(new(HttpStatusCode.NotModified))); await using AppVeyorApiClient noCache = new(Settings(), uncached); await Expect<AppVeyorException>(() => noCache.ReadAsync());
    }
    private static async Task ChunkedAsync()
    {
        using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekStream(Encoding.UTF8.GetBytes(new string('x', 3000)))) }));
        await using AppVeyorApiClient client = new(Settings(), handler) { MaxResponseBytes = 1024 }; await Expect<AppVeyorException>(() => client.ReadAsync());
    }
    private static async Task CancellationAsync()
    {
        int calls = 0; TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Handler handler = new(async (_, token) => { if (++calls == 1) { return History(Build()); } started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return History(); });
        await using AppVeyorApiClient client = new(Settings(), handler); AppVeyorSnapshot original = await client.ReadAsync(); using CancellationTokenSource cancel = new(); Task<AppVeyorSnapshot> work = client.ReadAsync(true, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Expect<OperationCanceledException>(() => work); Check(ReferenceEquals(original, await client.ReadAsync()), "Cancellation cannot publish an empty or partial replacement.");
    }
    private static async Task BodyTimeoutAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        await using AppVeyorApiClient client = new(Settings(), handler) { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        AppVeyorException error = await Expect<AppVeyorException>(() => client.ReadAsync()); Check(error.Message.Contains("timed out", StringComparison.Ordinal) && stream.Disposed && stream.Started.Task.IsCompleted, "Deadline reaches body, disposes stream and returns fixed timeout.");
    }
    private static async Task DrainAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        AppVeyorApiClient client = new(Settings(), handler); using CancellationTokenSource cancel = new(); Task<AppVeyorSnapshot> work = client.ReadAsync(cancellationToken: cancel.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Expect<OperationCanceledException>(() => work); await client.DisposeAsync(); Check(stream.Disposed, "Caller cancellation disposes body before disposal returns.");
        bool refused = false; try { _ = client.ReadAsync(); } catch (ObjectDisposedException) { refused = true; } Check(refused, "Closed client cannot accept new work.");
        BlockingStream second = new(); using Handler blocked = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(second) }));
        AppVeyorApiClient closing = new(Settings(), blocked); Task<AppVeyorSnapshot> active = closing.ReadAsync(); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closing.DisposeAsync(); await Expect<OperationCanceledException>(() => active); Check(active.IsCompleted && second.Disposed, "Dispose itself cancels and drains owned work.");
    }
    private static async Task GateAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        AppVeyorApiClient client = new(Settings(), handler); Task<AppVeyorSnapshot> first = client.ReadAsync(); await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancel = new(); Task<AppVeyorSnapshot> queued = client.ReadAsync(true, cancel.Token); cancel.Cancel(); await Expect<OperationCanceledException>(() => queued);
        await client.DisposeAsync(); await Expect<OperationCanceledException>(() => first); Check(handler.Calls == 1, "Gate waiter cancellation never starts another HTTP operation.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { private int _calls; public int Calls => Volatile.Read(ref _calls); protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token) { Interlocked.Increment(ref _calls); return send(r, token); } }
    private sealed class NonSeekStream(byte[] value) : MemoryStream(value) { public override bool CanSeek => false; }
    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-appveyor-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name + ".settings");
        public void Write(string name, params (string Key, string Value)[] values) => File.WriteAllText(Path(name), new XElement("dictionary", values.Select(v => new XElement("item", new XElement("key", new XElement("string", v.Key)), new XElement("value", new XElement("string", v.Value))))).ToString());
        public async Task<GitDistributedSettingsSnapshot> Snapshot() => new(await GitXmlSettingsStore.LoadAsync(Path("global")), await GitXmlSettingsStore.LoadAsync(Path("local")), await GitXmlSettingsStore.LoadAsync(Path("distributed")));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
