using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GitExtensions.Linux;

internal static class AzureDevOpsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Azure DevOps all43 original project token and clipboard URL declarations remain executable oracles", WindowsUrlsAsync),
        ("Azure DevOps original five running selection cases retain first started or first queued per raw commit", RunningSelectionAsync),
        ("Azure DevOps exact long provider XML keys raw hierarchy unknown keys and pure prefill stay compatible", SettingsAsync),
        ("Azure DevOps PAT provenance refuses inherited project origin basepath and orphan URL retargeting", ProvenanceAsync),
        ("Azure DevOps known variables are draft valid and runtime clients require concretization", VariablesAsync),
        ("Azure DevOps structural URL helpers preserve onprem HTTP ports spaces and reject credentials and ambiguous links", UnsafeUrlsAsync),
        ("Azure DevOps anonymous GETs use original definitions6 and running finished2 API versions without GCM", GuestAsync),
        ("Azure DevOps explicit HTTP PAT uses exact Basic colon PAT and clears inherited handler credentials", PatAsync),
        ("Azure DevOps repository names resolve to GUID and scope all definition queries with TfsGit", RepositoryAsync),
        ("Azure DevOps failed repository resolution never widens to unfiltered definitions", RepositoryFailureAsync),
        ("Azure DevOps server name wildcard lookup precedes regex fallback only when no definitions match", DefinitionFilterAsync),
        ("Azure DevOps no matching definitions makes no build request and gives an explicit diagnostic", EmptyDefinitionsAsync),
        ("Azure DevOps completed incremental queries use version4.1 overlapping watermark and deduplicate identities", IncrementalAsync),
        ("Azure DevOps status and completed result mapping includes partial success and unknown without collapsing cancelling", StatusAsync),
        ("Azure DevOps PR parameters retain raw revision and resolve source head plus original browser link", PullRequestAsync),
        ("Azure DevOps PR alias collisions use original running finished grouping then latest start watcher rule", AliasSelectionAsync),
        ("Azure DevOps duration text tooltip queue unknown finish and injected current time preserve Windows presentation", DurationAsync),
        ("Azure DevOps malformed dates PR JSON identities and list counts cannot publish partial success", InvalidBuildAsync),
        ("Azure DevOps exact project build pages reject foreign origins projects credentials and mismatched IDs", PagesAsync),
        ("Azure DevOps explicit clipboard definition lookup uses version2 and checks returned build identity", BuildLookupAsync),
        ("Azure DevOps opaque continuation tokens are encoded and all pages retain repository filter and API version", PaginationAsync),
        ("Azure DevOps repeated or malformed continuation headers fail without silently completing history", InvalidPaginationAsync),
        ("Azure DevOps page item definition and request bounds are explicit and retained history stays marked incomplete", BoundsAsync),
        ("Azure DevOps ETag304 cache retains continuation metadata and force actually revalidates each resource", ETagAsync),
        ("Azure DevOps running10s and idle120s time caches avoid excess HTTP while force bypasses both", TimeCacheAsync),
        ("Azure DevOps failed refresh preserves accepted snapshot definitions watermark and ETag cache atomically", AtomicAsync),
        ("Azure DevOps caller cancellation before headers preserves original publication and stays cancellation", CancellationAsync),
        ("Azure DevOps request deadlines cover blocked response body and dispose its stream", BodyTimeoutAsync),
        ("Azure DevOps caller body cancellation and DisposeAsync drain owned requests and refuse new work", DrainAsync),
        ("Azure DevOps cancellation waiting for the read gate cannot start a second request", GateAsync),
        ("Azure DevOps bounded same resource redirects retain explicit HTTP configuration", RedirectAsync),
        ("Azure DevOps redirects cannot forward PAT across origin project basepath query or HTTPS downgrade", UnsafeRedirectAsync),
        ("Azure DevOps authentication rate and transport errors are readable without response or credential leakage", ErrorsAsync),
        ("Azure DevOps malformed deep HTML oversized and uncached304 responses never become empty successful histories", ResponseBoundsAsync),
        ("Azure DevOps inactive provider performs no HTTP and settings debug strings redact PAT", DisabledAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Other = new('a', 64);
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private const string RepoId = "01234567-0123-4567-89ab-0123456789ab";
    private static AzureDevOpsSettings Settings(string filter = "", string repository = "", string token = "", string url = "http://server.invalid:8080/tfs/Collection/Project/") => new()
    { Enabled = true, Provider = AzureDevOpsSettings.ProviderName, ProjectUrl = url, BuildDefinitionFilter = filter, RepositoryName = repository, ApiToken = token };
    private static Dictionary<string, object?> Build(long id = 1, string state = "completed", string result = "succeeded", string sha = Sha, int start = 1, int finish = 61) => new()
    {
        ["id"] = id, ["sourceVersion"] = sha, ["status"] = state, ["result"] = result, ["buildNumber"] = "build-" + id,
        ["definition"] = new { id = 7, name = "Linux" }, ["startTime"] = Epoch.AddSeconds(start), ["finishTime"] = Epoch.AddSeconds(finish),
        ["_links"] = new { web = new { href = "http://server.invalid:8080/tfs/Collection/Project/_build/results?buildId=" + id + "&view=results" } }
    };
    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value));
    private static HttpResponseMessage List(IEnumerable<object> values, string? next = null)
    { object[] array = values.ToArray(); HttpResponseMessage r = Json(new { count = array.Length, value = array }); if (next is not null) { r.Headers.TryAddWithoutValidation("x-ms-continuationtoken", next); } return r; }
    private static HttpResponseMessage Definitions() => List([new { id = 7, name = "Linux" }]);
    private static bool Def(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/build/definitions", StringComparison.Ordinal);
    private static bool Running(HttpRequestMessage r) => Query(r).GetValueOrDefault("statusFilter") != "completed";
    private static Dictionary<string, string> Query(HttpRequestMessage r) => r.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal);
    private static HttpResponseMessage Standard(HttpRequestMessage r) => Def(r) ? Definitions() : List(Running(r) ? [] : [Build()]);
    private static Task<HttpResponseMessage> Reply(HttpResponseMessage value) => Task.FromResult(value);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> body) where T : Exception
    { try { await body(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static AzureDevOpsBuild Model(long id, string raw, int? start, int? finish, string sha = Sha, string? real = null) => new()
    { BuildId = id, SourceVersion = sha, CommitHash = real ?? sha, RawStatus = raw, Status = AzureDevOpsBuild.Map(raw, "succeeded"), StartedAt = start.HasValue ? Epoch.AddSeconds(start.Value) : null, FinishedAt = finish.HasValue ? Epoch.AddSeconds(finish.Value) : null };

    private static Task WindowsUrlsAsync()
    {
        string path = Original("tests/plugins/UnitTests/BuildServerIntegration/AzureDevOpsIntegration.Tests/ProjectUrlHelperTests.cs");
        List<string?[]> pending = []; int count = 0;
        const string argument = """@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\bnull\b|\d+""";
        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("[TestCase(", StringComparison.Ordinal))
            {
                string args = trimmed[(trimmed.IndexOf('(') + 1)..trimmed.LastIndexOf(")]", StringComparison.Ordinal)];
                pending.Add(Regex.Matches(args, argument).Select(m => m.Value == "null" ? null : m.Value.StartsWith("@\"", StringComparison.Ordinal)
                    ? m.Value[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : m.Value.StartsWith('"') ? JsonSerializer.Deserialize<string>(m.Value) : m.Value).ToArray());
            }
            if (!trimmed.StartsWith("public void ", StringComparison.Ordinal)) { continue; }
            bool expected = trimmed.Contains("_succeed_", StringComparison.Ordinal);
            foreach (string?[] args in pending)
            {
                if (trimmed.StartsWith("public void TryDetectProjectFromRemoteUrl", StringComparison.Ordinal))
                { (bool ok, string? url) = AzureDevOpsProjectUrls.TryDetectProjectFromRemoteUrl(args[0]); Check(ok == expected && (!expected || url == args[1]), "Original remote URL case changed."); }
                else if (trimmed.StartsWith("public void TryGetTokenManagementUrlFromProject", StringComparison.Ordinal))
                { (bool ok, string? url) = AzureDevOpsProjectUrls.TryGetTokenManagementUrlFromProject(args[0]); Check(ok == expected && (!expected || url == args[1]), "Original token URL case changed."); }
                else
                { (bool ok, string? url, long id) = AzureDevOpsProjectUrls.TryParseBuildUrl(args[0]); Check(ok == expected && (!expected || url == args[1] && id.ToString(System.Globalization.CultureInfo.InvariantCulture) == args[2]), "Original build URL case changed."); }
                count++;
            }
            pending.Clear();
        }
        Check(count == 43, "Every original declaration, including invalid verbatim strings, must be exercised."); return Task.CompletedTask;
    }
    private static string Original(string relative)
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        { string path = Path.Combine(current.FullName, relative); if (File.Exists(path)) { return path; } }
        throw new InvalidOperationException("The original Azure DevOps source oracle is missing from the checkout.");
    }
    private static Task RunningSelectionAsync()
    {
        AzureDevOpsBuild a = Model(1, "inProgress", 1, null), b = Model(2, "inProgress", 2, null), queued = Model(3, "notStarted", null, null);
        Check(new AzureDevOpsSnapshot([a], Epoch).Builds.Single() == a, "One build remains unchanged.");
        Check(new AzureDevOpsSnapshot([a, b with { SourceVersion = Other, CommitHash = Other }], Epoch).Builds.Count == 2, "Different raw commits remain distinct.");
        Check(new AzureDevOpsSnapshot([a, b, b with { BuildId = 4 }], Epoch).Builds.Single() == a, "First started wins.");
        Check(new AzureDevOpsSnapshot([queued, queued with { BuildId = 4 }], Epoch).Builds.Single() == queued, "First waiting build wins when none started.");
        Check(new AzureDevOpsSnapshot([queued, b, queued], Epoch).Builds.Single() == b, "Started build is preferred to queued candidates."); return Task.CompletedTask;
    }
    private static async Task SettingsAsync()
    {
        using Files files = new(); GitDistributedSettingsSnapshot empty = await files.Snapshot();
        AzureDevOpsSettings detected = AzureDevOpsSettings.Read(empty, ["https://dev.azure.com/org/project/_git/repo"]);
        Check(AzureDevOpsSettings.ProviderName == "Azure DevOps and Team Foundation Server (since TFS2015)" && AzureDevOpsSettings.ProjectUrlKey == "BuildServer." + AzureDevOpsSettings.ProviderName + ".ProjectUrl" && AzureDevOpsSettings.Keys.Count == 7, "Long Windows identity and seven exact XML keys.");
        Check(!detected.Active && detected.RepositoryName == "repo" && detected.ProjectUrl == "https://dev.azure.com/org/project" && !File.Exists(files.Path("global")), "Remote prefill has no persistence or activation.");
        files.Write("global", (AzureDevOpsSettings.ProjectUrlKey, "https://dev.azure.com/org/project"), (AzureDevOpsSettings.RepositoryNameKey, "preserved"), ("future.key", "kept"), (AzureDevOpsSettings.EnableKey, "true"));
        files.Write("local", (AzureDevOpsSettings.EnableKey, "unknown")); GitDistributedSettingsSnapshot snapshot = await files.Snapshot(); AzureDevOpsSettings s = AzureDevOpsSettings.Read(snapshot);
        Check(!s.Enabled && s.RepositoryName == "preserved" && snapshot.GetValue("future.key") == "kept", "Raw inheritance preserves unknown keys and masks invalid booleans.");
    }
    private static async Task ProvenanceAsync()
    {
        using Files files = new(); files.Write("global", (AzureDevOpsSettings.ProjectUrlKey, "https://dev.azure.com/org/a"), (AzureDevOpsSettings.ApiTokenKey, "fake-pat"));
        foreach (string url in new[] { "https://other.invalid/tfs/a", "https://dev.azure.com/org/b", "http://dev.azure.com/org/a", "https://dev.azure.com/other/a" })
        { files.Write("local", (AzureDevOpsSettings.ProjectUrlKey, url)); await Expect<ArgumentException>(async () => { _ = AzureDevOpsSettings.Read(await files.Snapshot()); }); }
        files.Write("local", (AzureDevOpsSettings.ProjectUrlKey, "https://dev.azure.com/org/a/")); Check(AzureDevOpsSettings.Read(await files.Snapshot()).ApiToken == "fake-pat", "Canonical same endpoint remains usable.");
        files.Write("global", (AzureDevOpsSettings.ApiTokenKey, "fake-pat")); await Expect<ArgumentException>(async () => { _ = AzureDevOpsSettings.Read(await files.Snapshot()); });
    }
    private static Task VariablesAsync()
    {
        Settings(url: "https://dev.azure.com/org/{cRepoProject}").Validate(); bool refused = false;
        try { _ = new AzureDevOpsApiClient(Settings(url: "https://dev.azure.com/org/{cRepoProject}")); } catch (ArgumentException) { refused = true; }
        Check(refused, "Known variables are allowed in drafts but not unresolved runtime clients.");
        foreach (string filter in new[] { "*", "[" }) { refused = false; try { Settings(filter).Validate(); } catch (ArgumentException) { refused = true; } Check(refused, "Original regex validation, despite wildcard help text."); }
        Settings("Linux*").Validate(); return Task.CompletedTask;
    }
    private static Task UnsafeUrlsAsync()
    {
        Check(AzureDevOpsProjectUrls.TryDetectProjectFromRemoteUrl("http://server:8080/tfs/DefaultCollection/My%20Project/_git/Other%20Repo", out AzureDevOpsRemote? remote)
            && remote.ProjectUri.AbsoluteUri == "http://server:8080/tfs/DefaultCollection/My%20Project/" && remote.RepositoryName == "Other Repo", "HTTP onprem ports and encoded names survive.");
        Check(AzureDevOpsProjectUrls.ParseProjectUri("https://host:9443/tfs/ArbitraryCollection/Project").AbsolutePath == "/tfs/ArbitraryCollection/Project/", "Explicit arbitrary collection URLs are not narrowed to DefaultCollection.");
        foreach (string value in new[] { "https://user:secret@dev.azure.com/org/p/_git/r", "https://dev.azure.com/org/p/_git/r?token=secret", "file:///org/p", "garbage https://dev.azure.com/org/p/_git/r" })
        { Check(!AzureDevOpsProjectUrls.TryDetectProjectFromRemoteUrl(value, out _), "Credential-bearing or unanchored remotes fail."); }
        Check(!AzureDevOpsProjectUrls.TryParseBuildUrl("https://dev.azure.com/o/p/_build/results?buildId=1&buildId=2", out _), "Ambiguous IDs are refused."); return Task.CompletedTask;
    }
    private static async Task GuestAsync()
    {
        List<Dictionary<string, string>> queries = []; using Handler handler = new((r, _) =>
        { Check(r.Method == HttpMethod.Get && r.Headers.Authorization is null && r.Headers.Accept.Single().MediaType == "application/json", "Only anonymous GET JSON; no credential lookup needed."); queries.Add(Query(r)); return Reply(Standard(r)); });
        await using AzureDevOpsApiClient client = new(Settings(), handler); Check((await client.ReadAsync()).Builds.Single().Status == AzureDevOpsBuildStatus.Success, "Guest reads public build payload.");
        Check(queries.Count == 3 && queries[0]["api-version"] == "6.0" && queries[1]["api-version"] == "2.0" && queries[2]["api-version"] == "2.0"
            && queries[1]["statusFilter"] == "cancelling,inProgress,none,notStarted,postponed" && queries.All(q => !q.ContainsKey("branchName")), "Original versions and scopes, no invented branch filter.");
    }
    private static async Task PatAsync()
    {
        using Handler handler = new((r, _) => { Check(r.RequestUri!.Scheme == "http" && r.Headers.Authorization is { Scheme: "Basic" } auth && Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!)) == ":fake-pat", "Explicit HTTP and Basic colon PAT exact semantics."); return Reply(Standard(r)); });
        await using AzureDevOpsApiClient client = new(Settings(token: "fake-pat"), handler); await client.ReadAsync();
        using HttpClientHandler inherited = new() { UseDefaultCredentials = true, UseCookies = true, AllowAutoRedirect = true };
        await using AzureDevOpsApiClient isolated = new(Settings(), inherited);
        Check(!inherited.UseDefaultCredentials && inherited.Credentials is null && !inherited.UseCookies && !inherited.AllowAutoRedirect && inherited.DefaultProxyCredentials is null, "Inherited OS credentials, cookies and automatic redirects are explicitly cleared.");
    }
    private static async Task RepositoryAsync()
    {
        int lookup = 0; using Handler handler = new((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.Contains("/git/repositories/", StringComparison.Ordinal))
            { lookup++; Check(r.RequestUri.OriginalString.Contains("repo%20name", StringComparison.Ordinal) && Query(r)["api-version"] == "6.0", "Repository is one encoded path component."); return Reply(Json(new { id = RepoId })); }
            if (Def(r)) { Dictionary<string, string> q = Query(r); Check(q["repositoryId"] == RepoId && q["repositoryType"] == "TfsGit", "Every definitions query remains repository scoped."); }
            return Reply(Standard(r));
        });
        await using AzureDevOpsApiClient client = new(Settings(repository: "repo name"), handler); await client.ReadAsync(); Check(lookup == 1, "Repository resolution is cached with definitions.");
    }
    private static async Task RepositoryFailureAsync()
    {
        foreach (int code in new[] { 401, 403, 404, 500 })
        {
            using Handler handler = new((_, _) => Reply(new((HttpStatusCode)code))); await using AzureDevOpsApiClient client = new(Settings(repository: "repo"), handler);
            Check((await Expect<AzureDevOpsException>(() => client.ReadAsync())).StatusCode == code && handler.Calls == 1, "Lookup failure cannot silently query all definitions.");
        }
    }
    private static async Task DefinitionFilterAsync()
    {
        int filtered = 0, fallback = 0; using Handler handler = new((r, _) =>
        {
            if (!Def(r)) { return Reply(Standard(r)); }
            if (Query(r).TryGetValue("name", out string? name)) { filtered++; Check(name == "^Linux.*$", "Name query is URI encoded and decoded unchanged."); return Reply(List([])); }
            fallback++; return Reply(List([new { id = 7, name = "Linux release" }, new { id = 8, name = "Windows" }]));
        });
        await using AzureDevOpsApiClient client = new(Settings("^Linux.*$"), handler); await client.ReadAsync(); Check(filtered == 1 && fallback == 1, "Zero wildcard server result triggers bounded client regex fallback.");
        using Handler direct = new((r, _) => { if (Def(r)) { Check(Query(r).ContainsKey("name"), "Nonempty direct match must not query an unfiltered fallback."); } return Reply(Standard(r)); });
        await using AzureDevOpsApiClient match = new(Settings("Linux*"), direct); await match.ReadAsync(); Check(direct.Calls == 3, "Direct match uses original two-step semantics.");
    }
    private static async Task EmptyDefinitionsAsync()
    {
        using Handler handler = new((r, _) => { Check(Def(r), "No build request without definition IDs."); return Reply(List([])); });
        await using AzureDevOpsApiClient client = new(Settings(), handler); AzureDevOpsSnapshot s = await client.ReadAsync(); Check(s.Builds.Count == 0 && s.Diagnostics.Count > 0 && handler.Calls == 1, "No-match is visible, not an authentication failure.");
    }
    private static async Task IncrementalAsync()
    {
        int completed = 0; using Handler handler = new((r, _) =>
        {
            if (Def(r)) { return Reply(Definitions()); } if (Running(r)) { return Reply(List([])); }
            completed++; Dictionary<string, string> q = Query(r);
            Check(q["api-version"] == (completed == 1 ? "2.0" : "4.1"), "Incremental completed route switches to original4.1 only with a watermark.");
            if (completed > 1) { Check(q["minTime"] == "1970-01-01T00:01:00", "Watermark overlaps the last finish by a second instead of skipping same-second builds."); }
            return Reply(List(completed == 1 ? [Build()] : [Build(), Build(2, sha: Other, finish: 62)]));
        });
        await using AzureDevOpsApiClient client = new(Settings(), handler) { IdleCacheDuration = TimeSpan.Zero }; await client.ReadAsync();
        AzureDevOpsSnapshot s = await client.ReadAsync(); Check(s.Builds.Count == 2 && s.Commits.Count == 2 && completed == 2 && handler.Calls == 5, "Definitions cached, overlap deduplicated, both commits retained.");
    }
    private static Task StatusAsync()
    {
        foreach (string raw in new[] { "cancelling", "inProgress", "none", "notStarted", "postponed", "future", "" })
        { Check(AzureDevOpsBuild.Map(raw, "canceled") == AzureDevOpsBuildStatus.InProgress, "Every noncompleted status follows original in-progress contract."); }
        foreach ((string result, AzureDevOpsBuildStatus expected) in new[] { ("failed", AzureDevOpsBuildStatus.Failure), ("canceled", AzureDevOpsBuildStatus.Stopped), ("succeeded", AzureDevOpsBuildStatus.Success), ("partiallySucceeded", AzureDevOpsBuildStatus.Unstable), ("future", AzureDevOpsBuildStatus.Unknown) })
        { Check(AzureDevOpsBuild.Map("completed", result) == expected, "Completed result is distinct from running status."); } return Task.CompletedTask;
    }
    private static async Task PullRequestAsync()
    {
        Dictionary<string, object?> build = Build(); build["reason"] = "validateShelveset";
        build["parameters"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["system.pullRequest.sourceCommitId"] = Other, ["system.pullRequest.pullRequestId"] = "42" });
        build["repository"] = new { url = "http://server.invalid:8080/tfs/Collection/Project/_git/repo" };
        using Handler handler = new((r, _) => Reply(Def(r) ? Definitions() : List(Running(r) ? [] : [build])));
        await using AzureDevOpsApiClient client = new(Settings(), handler); AzureDevOpsBuild b = (await client.ReadAsync()).Builds.Single();
        Check(b.SourceVersion == Sha && b.CommitHash == Other && b.PullRequestUri?.AbsoluteUri == "http://server.invalid:8080/tfs/Collection/Project/_git/repo/pullrequest/42"
            && b.Tooltip.EndsWith(Environment.NewLine + "PR #42", StringComparison.Ordinal) && !b.ShowInBuildReportTab, "String parameters use literal dotted keys, real head SHA and original report opt-out.");
    }
    private static Task AliasSelectionAsync()
    {
        AzureDevOpsBuild earlyFinished = Model(1, "completed", 1, 100), lateFinished = Model(2, "completed", 2, 200);
        Check(new AzureDevOpsSnapshot([lateFinished, earlyFinished], Epoch).Builds.Single().BuildId == 2, "Finished selection uses latest finish, not input order.");
        AzureDevOpsBuild running = Model(3, "inProgress", 50, null, Other, Sha);
        AzureDevOpsSnapshot s = new([earlyFinished, lateFinished, running], Epoch);
        Check(s.Builds.Count == 2 && s.Commits[Sha].Build.BuildId == 3, "Distinct raw revisions converging on a PR head use watcher latest start after separate raw grouping.");
        AzureDevOpsBuild tie = Model(4, "completed", 50, 250);
        Check(new AzureDevOpsSnapshot([running, tie], Epoch).Commits[Sha].Build.BuildId == 4, "Same-start terminal delivery replaces running as original >= guard."); return Task.CompletedTask;
    }
    private static async Task DurationAsync()
    {
        Clock clock = new(Epoch.AddSeconds(121)); Dictionary<string, object?> run = Build(1, "inProgress"); run.Remove("finishTime");
        using Handler handler = new((r, _) => Reply(Def(r) ? Definitions() : List(Running(r) ? [run] : [])));
        await using AzureDevOpsApiClient client = new(Settings(), handler, clock); AzureDevOpsBuild b = (await client.ReadAsync()).Builds.Single();
        Check(b.DurationMilliseconds == 120000 && b.DurationText == "02:00" && b.Description == "02:00 build-1" && b.Tooltip == "build-1 ▶️ - 02:00 [Linux]", "Injected clock and original display/tooltip.");
        foreach ((string state, string expected) in new[] { ("notStarted", ""), ("postponed", ""), ("cancelling", "???") })
        {
            run["status"] = state; b = (await client.ReadAsync(true)).Builds.Single(); Check(b.DurationText == expected, "Queue and missing-finish duration semantics remain explicit.");
        }
    }
    private static async Task InvalidBuildAsync()
    {
        foreach (int kind in Enumerable.Range(0, 5))
        {
            Dictionary<string, object?> b = Build();
            if (kind == 0) { b["startTime"] = "not-a-date"; }
            if (kind == 1) { b["finishTime"] = Epoch; }
            if (kind == 2) { b["id"] = -1; }
            if (kind == 3) { b["reason"] = "validateShelveset"; b["parameters"] = "{bad"; }
            using Handler handler = new((r, _) => Reply(Def(r) ? Definitions() : Running(r) ? List([]) : kind == 4 ? Json(new { count = 99, value = new[] { b } }) : List([b])));
            await using AzureDevOpsApiClient client = new(Settings(), handler); await Expect<AzureDevOpsException>(() => client.ReadAsync());
        }
    }
    private static async Task PagesAsync()
    {
        using Handler handler = new((r, _) => Reply(Standard(r))); await using AzureDevOpsApiClient client = new(Settings(), handler);
        AzureDevOpsBuild b = (await client.ReadAsync()).Builds.Single(); Check(client.BuildPageUri(b)?.AbsoluteUri == b.PageUri?.AbsoluteUri, "Exact configured project page accepted.");
        foreach (string page in new[] { "https://other.invalid/tfs/Collection/Project/_build/results?buildId=1", "http://server.invalid:8080/tfs/Other/Project/_build/results?buildId=1", "http://u:secret@server.invalid:8080/tfs/Collection/Project/_build/results?buildId=1", "http://server.invalid:8080/tfs/Collection/Project/_build/results?buildId=2", "file:///tmp/page" })
        { Check(client.BuildPageUri(b with { PageUri = new(page) }) is null, "Foreign or mismatched DTO page never opens."); }
    }
    private static async Task BuildLookupAsync()
    {
        using Handler handler = new((r, _) => { Check(r.RequestUri!.AbsolutePath.EndsWith("/build/builds/3000000000", StringComparison.Ordinal) && Query(r)["api-version"] == "2.0", "Original clipboard lookup and larger stable numerical ID."); return Reply(Json(new { id = 3000000000L, definition = new { name = "Linux*" } })); });
        await using AzureDevOpsApiClient client = new(Settings(), handler); Check(await client.GetBuildDefinitionNameFromIdAsync(3000000000) == "Linux*", "Definition name returned for explicit UI action.");
        using Handler wrong = new((_, _) => Reply(Json(new { id = 2, definition = new { name = "Other" } })));
        await using AzureDevOpsApiClient mismatch = new(Settings(), wrong); await Expect<AzureDevOpsException>(() => mismatch.GetBuildDefinitionNameFromIdAsync(1));
    }
    private static async Task PaginationAsync()
    {
        int pages = 0; using Handler handler = new((r, _) =>
        {
            Dictionary<string, string> q = Query(r);
            if (!Def(r)) { Check(q["definitions"] == "7,8", "IDs from all definition pages are retained."); return Reply(List(Running(r) ? [] : [Build()])); }
            pages++; Check(q["api-version"] == "6.0", "Every continuation preserves API version.");
            if (!q.TryGetValue("continuationToken", out string? next)) { return Reply(List([new { id = 7, name = "Linux" }], "opaque &?=/#")); }
            Check(next == "opaque &?=/#" && r.RequestUri!.Host == "server.invalid", "Continuation is encoded opaque data, never followed as a URL.");
            return Reply(List([new { id = 8, name = "Another" }]));
        });
        await using AzureDevOpsApiClient client = new(Settings(), handler); Check((await client.ReadAsync()).Builds.Count == 1 && pages == 2 && handler.Calls == 4, "All definition pages load once.");
    }
    private static async Task InvalidPaginationAsync()
    {
        foreach (bool duplicate in new[] { false, true })
        {
            using Handler handler = new((_, _) =>
            { HttpResponseMessage r = Definitions(); r.Headers.TryAddWithoutValidation("x-ms-continuationtoken", duplicate ? ["one", "two"] : ["same"]); return Reply(r); });
            await using AzureDevOpsApiClient client = new(Settings(), handler); await Expect<AzureDevOpsException>(() => client.ReadAsync()); Check(handler.Calls == (duplicate ? 1 : 2), "Ambiguous/cyclic headers fail before another unbounded page.");
        }
    }
    private static async Task BoundsAsync()
    {
        using Handler pages = new((_, _) => Reply(List([new { id = 7, name = "Linux" }], "next")));
        await using AzureDevOpsApiClient p = new(Settings(), pages) { MaxPages = 1, MaxRequests = 1 };
        Check((await p.ReadAsync()).Truncated, "Page/request bound is visible even when no builds could be read.");
        using Handler defs = new((r, _) => Reply(Def(r) ? List([new { id = 7, name = "Linux" }, new { id = 8, name = "Other" }]) : List([])));
        await using AzureDevOpsApiClient d = new(Settings(), defs) { MaxDefinitions = 1 }; Check((await d.ReadAsync()).Truncated, "Definition bound cannot masquerade as complete.");
        using Handler items = new((r, _) => Reply(Def(r) ? Definitions() : List(Running(r) ? [] : [Build(), Build(2, sha: Other)])));
        await using AzureDevOpsApiClient i = new(Settings(), items) { MaxItems = 1, IdleCacheDuration = TimeSpan.Zero };
        Check((await i.ReadAsync()).Truncated && (await i.ReadAsync()).Truncated, "Evicted historical results stay explicitly incomplete on incremental reads.");
    }
    private static async Task ETagAsync()
    {
        using Handler handler = new((r, _) =>
        {
            if (r.Headers.IfNoneMatch.Count > 0) { return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage response = Standard(r); response.Headers.ETag = new("\"one\""); return Reply(response);
        });
        await using AzureDevOpsApiClient client = new(Settings(), handler); AzureDevOpsSnapshot a = await client.ReadAsync(); await client.ReadAsync(true);
        Check(handler.Calls == 6 && a.Builds.Single().BuildId == 1, "Force revalidates definitions and both build scopes against cached payloads.");
        int calls = 0; using Handler paged = new((r, _) =>
        {
            if (!Def(r)) { return Reply(List([])); }
            calls++; if (r.Headers.IfNoneMatch.Count > 0) { return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage response = Query(r).ContainsKey("continuationToken") ? List([new { id = 8, name = "Second" }]) : List([new { id = 7, name = "First" }], "next"); response.Headers.ETag = new("\"one\""); return Reply(response);
        });
        await using AzureDevOpsApiClient pages = new(Settings(), paged); await pages.ReadAsync(); await pages.ReadAsync(true); Check(calls == 4, "304 first page retains continuation metadata and revalidates the second page.");
    }
    private static async Task TimeCacheAsync()
    {
        foreach (bool active in new[] { false, true })
        {
            Clock clock = new(Epoch.AddDays(1)); using Handler handler = new((r, _) => Reply(Def(r) ? Definitions() : List(Running(r) == active ? [Build(state: active ? "inProgress" : "completed")] : [])));
            await using AzureDevOpsApiClient client = new(Settings(), handler, clock); AzureDevOpsSnapshot first = await client.ReadAsync(); clock.Advance(TimeSpan.FromSeconds(active ? 9 : 119));
            Check(ReferenceEquals(first, await client.ReadAsync()) && handler.Calls == 3, "Recent snapshot avoids all HTTP."); clock.Advance(TimeSpan.FromSeconds(1));
            await client.ReadAsync(); Check(handler.Calls == 5, "Active10s or idle120s expiration performs real incremental read with cached definitions.");
        }
    }
    private static async Task AtomicAsync()
    {
        bool broken = false; using Handler handler = new((r, _) =>
        {
            if (broken && Def(r)) { HttpResponseMessage bad = Text("{bad"); bad.Headers.ETag = new("\"bad\""); return Reply(bad); }
            if (r.Headers.IfNoneMatch.Count > 0) { Check(r.Headers.IfNoneMatch.Single().Tag == "\"good\"", "Failed parse must not advance conditional cache."); return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage good = Standard(r); good.Headers.ETag = new("\"good\""); return Reply(good);
        });
        await using AzureDevOpsApiClient client = new(Settings(), handler); AzureDevOpsSnapshot first = await client.ReadAsync(); broken = true;
        await Expect<AzureDevOpsException>(() => client.ReadAsync(true)); Check(ReferenceEquals(first, await client.ReadAsync()), "Failed force does not clear accepted finished state or snapshot."); broken = false;
        Check((await client.ReadAsync(true)).Builds.Single().BuildId == 1, "Last valid cache and definitions are still usable.");
    }
    private static async Task CancellationAsync()
    {
        bool block = false; TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Handler handler = new(async (r, token) => { if (block) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } return Standard(r); });
        await using AzureDevOpsApiClient client = new(Settings(), handler); AzureDevOpsSnapshot original = await client.ReadAsync(); block = true; using CancellationTokenSource cancel = new(); Task<AzureDevOpsSnapshot> pending = client.ReadAsync(true, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Expect<OperationCanceledException>(() => pending); Check(ReferenceEquals(original, await client.ReadAsync()), "Cancelled refresh leaves accepted publication intact.");
    }
    private static async Task BodyTimeoutAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        await using AzureDevOpsApiClient client = new(Settings(), handler) { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        Check((await Expect<AzureDevOpsException>(() => client.ReadAsync())).Message.Contains("timed out", StringComparison.Ordinal) && stream.Disposed, "Deadline includes body read and disposal.");
    }
    private static async Task DrainAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        AzureDevOpsApiClient client = new(Settings(), handler); using CancellationTokenSource cancel = new(); Task<AzureDevOpsSnapshot> pending = client.ReadAsync(cancellationToken: cancel.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Expect<OperationCanceledException>(() => pending); await client.DisposeAsync(); Check(stream.Disposed, "Caller cancellation drains body.");
        bool refused = false; try { _ = client.ReadAsync(); } catch (ObjectDisposedException) { refused = true; } Check(refused, "Closed client refuses work.");
        BlockingStream second = new(); using Handler blocked = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(second) }));
        AzureDevOpsApiClient closing = new(Settings(), blocked); Task<AzureDevOpsSnapshot> active = closing.ReadAsync(); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await closing.DisposeAsync();
        await Expect<OperationCanceledException>(() => active); Check(active.IsCompleted && second.Disposed, "Dispose itself cancels and drains all owned HTTP.");
    }
    private static async Task GateAsync()
    {
        BlockingStream stream = new(); using Handler handler = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        AzureDevOpsApiClient client = new(Settings(), handler); Task<AzureDevOpsSnapshot> first = client.ReadAsync(); await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancel = new(); Task<AzureDevOpsSnapshot> second = client.ReadAsync(true, cancel.Token); cancel.Cancel(); await Expect<OperationCanceledException>(() => second); await client.DisposeAsync(); await Expect<OperationCanceledException>(() => first);
        Check(handler.Calls == 1, "Cancelled gate waiter starts no HTTP.");
    }
    private static HttpResponseMessage Redirect(string value)
    { HttpResponseMessage r = new(HttpStatusCode.Redirect); r.Headers.Location = new(value, UriKind.RelativeOrAbsolute); return r; }
    private static async Task RedirectAsync()
    {
        bool redirected = false; using Handler handler = new((r, _) => { if (!redirected) { redirected = true; return Reply(Redirect(r.RequestUri!.AbsoluteUri)); } return Reply(Standard(r)); });
        await using AzureDevOpsApiClient client = new(Settings(token: "fake-pat"), handler); Check((await client.ReadAsync()).Builds.Count == 1 && handler.Calls == 4, "Same exact resource redirect retains explicit configured HTTP.");
    }
    private static async Task UnsafeRedirectAsync()
    {
        foreach (string url in new[] { "https://other.invalid/tfs/Collection/Project/_apis/build/definitions?api-version=6.0", "http://server.invalid:8080/tfs/Collection/Project/_apis/build/definitions?api-version=6.0", "https://server.invalid:8080/tfs/Collection/Other/_apis/build/definitions?api-version=6.0", "https://user:secret@server.invalid:8080/tfs/Collection/Project/_apis/build/definitions?api-version=6.0", "https://server.invalid:8080/tfs/Collection/Project/_apis/build/definitions?api-version=2.0", "https://server.invalid:8080/tfs/Collection/Project/_apis/build/definitions?api-version=6.0#fragment" })
        {
            using Handler handler = new((_, _) => Reply(Redirect(url))); await using AzureDevOpsApiClient client = new(Settings(token: "fake-pat", url: "https://server.invalid:8080/tfs/Collection/Project"), handler);
            AzureDevOpsException error = await Expect<AzureDevOpsException>(() => client.ReadAsync()); Check(handler.Calls == 1 && !error.ToString().Contains("secret", StringComparison.Ordinal) && !error.ToString().Contains("fake-pat", StringComparison.Ordinal), "Only initial request is sent and redirect text is redacted.");
        }
    }
    private static async Task ErrorsAsync()
    {
        foreach (int code in new[] { 401, 403, 404, 429, 500 })
        {
            using Handler handler = new((_, _) => Reply(new((HttpStatusCode)code) { Content = new StringContent("fake-secret-response") }));
            await using AzureDevOpsApiClient client = new(Settings(token: "fake-pat"), handler); AzureDevOpsException error = await Expect<AzureDevOpsException>(() => client.ReadAsync());
            Check(error.StatusCode == code && !error.ToString().Contains("fake-secret", StringComparison.Ordinal) && !error.ToString().Contains("fake-pat", StringComparison.Ordinal), "Fixed useful HTTP code without response/PAT.");
        }
        using Handler transport = new((_, _) => throw new HttpRequestException("fake-secret-transport")); await using AzureDevOpsApiClient failure = new(Settings(), transport);
        Check(!(await Expect<AzureDevOpsException>(() => failure.ReadAsync())).ToString().Contains("fake-secret", StringComparison.Ordinal), "No inner transport text leaks.");
    }
    private static async Task ResponseBoundsAsync()
    {
        foreach (string text in new[] { "null", "{", "<html>login fake-secret</html>", "{\"value\":null}", new string('[', 40) + "0" + new string(']', 40), new string('x', 3000) })
        { using Handler handler = new((_, _) => Reply(Text(text))); await using AzureDevOpsApiClient client = new(Settings(), handler) { MaxResponseBytes = 1024 }; await Expect<AzureDevOpsException>(() => client.ReadAsync()); }
        using Handler chunked = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekStream(Encoding.UTF8.GetBytes(new string('x', 3000)))) }));
        await using AzureDevOpsApiClient bound = new(Settings(), chunked) { MaxResponseBytes = 1024 }; await Expect<AzureDevOpsException>(() => bound.ReadAsync());
        using Handler cache = new((_, _) => Reply(new(HttpStatusCode.NotModified))); await using AzureDevOpsApiClient missing = new(Settings(), cache); await Expect<AzureDevOpsException>(() => missing.ReadAsync());
    }
    private static async Task DisabledAsync()
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No HTTP expected"));
        await using AzureDevOpsApiClient client = new(new() { ProjectUrl = Settings().ProjectUrl }, handler); Check((await client.ReadAsync()).Builds.Count == 0 && handler.Calls == 0, "No HTTP when inactive.");
        Check(!Settings(token: "fake-pat").ToString().Contains("fake-pat", StringComparison.Ordinal) && !Model(1, "completed", 1, 2).ToString().Contains(Sha, StringComparison.Ordinal), "Debug strings omit credentials and payload.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { private int _calls; public int Calls => Volatile.Read(ref _calls); protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token) { Interlocked.Increment(ref _calls); return send(r, token); } }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { private DateTimeOffset _now = now; public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan value) => _now += value; }
    private sealed class NonSeekStream(byte[] value) : MemoryStream(value) { public override bool CanSeek => false; }
    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-azure-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name + ".settings");
        public void Write(string name, params (string Key, string Value)[] values) => File.WriteAllText(Path(name), new XElement("dictionary", values.Select(v => new XElement("item", new XElement("key", new XElement("string", v.Key)), new XElement("value", new XElement("string", v.Value))))).ToString());
        public async Task<GitDistributedSettingsSnapshot> Snapshot() => new(await GitXmlSettingsStore.LoadAsync(Path("global")), await GitXmlSettingsStore.LoadAsync(Path("local")), await GitXmlSettingsStore.LoadAsync(Path("distributed")));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
