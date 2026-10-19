using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using GitExtensions.Linux;
using GitUIPluginInterfaces.BuildServerIntegration;

internal static class JenkinsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Jenkins original successful freestyle JSON fixture retains number SHA duration and page", () => OriginalFixtureAsync("freestyle_success", JenkinsBuildStatus.Success, 42, 600000, "d4295d591a45c67d812aa1c4fa17b6a0f2ea3114")),
        ("Jenkins original failed freestyle JSON fixture retains its real SHA and duration", () => OriginalFixtureAsync("freestyle_failure", JenkinsBuildStatus.Failure, 43, 120000, "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0")),
        ("Jenkins original running JSON fixture keeps null duration despite wire zero", () => OriginalFixtureAsync("freestyle_running", JenkinsBuildStatus.InProgress, 44, null, "b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1")),
        ("Jenkins original tests JSON fixture retains 100 total 2 failed 5 skipped and unstable", OriginalTestsAsync),
        ("Jenkins original two-branch fixture remains one build and one commit", () => OriginalFixtureAsync("with_branches", JenkinsBuildStatus.Success, 46, 450000, "e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4")),
        ("Jenkins original timestamp fixture yields its 2019 start and exact original duration text", OriginalDateAsync),
        ("Jenkins exact XML hierarchy unknown values raw variables and read-only scope remain intact", SettingsAsync),
        ("Jenkins server defaults preserve explicit HTTP HTTPS ports basepath and reject URI credentials", ServerAsync),
        ("Jenkins pipe projects trim expand deduplicate encode folders and refuse unresolved runtime variables", ProjectsAsync),
        ("Jenkins freestyle wire uses original depth1 full tree with GET only", WireAsync),
        ("Jenkins actual multibranch jobs response uses depth2 and keeps same-number child identities distinct", MultiBranchAsync),
        ("Jenkins custom tree selector is one encoded parameter and needs a parser-compatible response", CustomTreeAsync),
        ("Jenkins Guest ignores populated inactive credential fields and never emits Authorization", GuestAsync),
        ("Jenkins explicit HTTP Basic uses UTF8 and snapshots caller credential drafts", BasicAsync),
        ("Jenkins Bearer and invalid credential modes fail before HTTP without secret text", CredentialsAsync),
        ("Jenkins real transport disables cookies default OS credentials redirects and proxy credentials", TransportAsync),
        ("Jenkins idle cache polls only lastBuild and retains statuses on unchanged timestamp", TimestampCacheAsync),
        ("Jenkins newer lastBuild triggers exactly one full refresh after the short probe", NewTimestampAsync),
        ("Jenkins running cache expires at10s and requests full info again without a short probe", RunningCacheAsync),
        ("Jenkins force bypasses time and timestamp caches while ETag304 reuses validated full bytes", ETagAsync),
        ("Jenkins Windows per-pass priority and later watcher start-time checks preserve emission order", PriorityAsync),
        ("Jenkins watcher never regresses a known SHA to an older build after refresh", WatcherRetentionAsync),
        ("Jenkins cached completed project is not replayed against another project fresh running build", CachedProjectDoesNotReplayAsync),
        ("Jenkins required full emissions precede full reads promoted by timestamp probes", RequiredFullEmissionOrderAsync),
        ("Jenkins global retained build bound excludes unadmitted full emissions from commit statuses and report runs", RetainedBuildBoundFiltersEmissionsAsync),
        ("Jenkins automatic recovery from404 clears only the recovered project incomplete warning", AutomaticMissingRecoveryAsync),
        ("Jenkins cached incomplete project warnings survive a request bound until that project recovers", CachedIncompleteRecoveryAsync),
        ("Jenkins multi-SHA mapping supports SHA256 and ignores the whole build on any matched branch", IgnoreAsync),
        ("Jenkins missing Git plugin gives no fabricated SHA and an explicit diagnostic", MissingGitAsync),
        ("Jenkins status running precedence unknown aborted tests and durations retain original semantics", StatusAsync),
        ("Jenkins malformed counts dates actions IDs and custom shape cannot publish partial history", MalformedAsync),
        ("Jenkins failed full refresh preserves accepted snapshot timestamps and ETags atomically", AtomicAsync),
        ("Jenkins project job build and request bounds expose truncation without fake complete history", BoundsAsync),
        ("Jenkins job404 gives an explicit incomplete diagnostic and retains previous known results", MissingAsync),
        ("Jenkins401403 HTML-login rate and transport failures redact response credential and URI text", ErrorsAsync),
        ("Jenkins same-resource redirects are bounded and retain explicitly configured HTTP", RedirectAsync),
        ("Jenkins foreign basepath origin downgrade query and userinfo redirects never receive credentials", UnsafeRedirectAsync),
        ("Jenkins build page links remain inside configured server and job and match the build number", PagesAsync),
        ("Jenkins45s request deadline also covers blocked body and disposes the owned stream", BodyTimeoutAsync),
        ("Jenkins request deadline bounds response headers without becoming caller cancellation", HeaderTimeoutAsync),
        ("Jenkins adversarial ignore regex is bounded and cannot publish an unfinished read", RegexTimeoutAsync),
        ("Jenkins caller cancellation including gate wait does not issue an extra request", CancellationAsync),
        ("Jenkins DisposeAsync cancels drains headers and body and rejects later reads", DrainAsync),
        ("Jenkins declared chunked deep malformed and uncached304 bodies respect response bounds", ResponseBoundsAsync),
        ("Jenkins inactive integration does no HTTP and debug strings omit payload and credentials", DisabledAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Other = new('b', 64);
    private const string Base = "http://server.invalid:8080/jenkins/";
    private static JenkinsSettings Settings(string projects = "p", string ignore = "", string server = Base) => new()
    { Enabled = true, Provider = JenkinsSettings.ProviderName, ServerUrl = server, ProjectName = projects, IgnoreBuildBranch = ignore };
    private static BuildServerCredentials Guest() => new() { BuildServerCredentialsType = BuildServerCredentialsType.Guest };
    private static BuildServerCredentials Basic() => new() { BuildServerCredentialsType = BuildServerCredentialsType.UsernameAndPassword, Username = "user-é", Password = "fake-password" };
    private static JsonObject Build(long number = 1, string? result = "SUCCESS", bool running = false, string sha = Sha, long timestamp = 1000, string project = "p") => new()
    {
        ["number"] = number, ["result"] = result, ["timestamp"] = timestamp, ["url"] = Base + "job/" + project + "/" + number + "/",
        ["building"] = running, ["duration"] = 60000,
        ["actions"] = new JsonArray(new JsonObject { ["lastBuiltRevision"] = new JsonObject { ["SHA1"] = sha, ["branch"] = new JsonArray(new JsonObject { ["name"] = "origin/main" }) } })
    };
    private static JsonObject Job(params JsonNode[] builds) => new()
    {
        ["lastBuild"] = builds.Length == 0 ? null : new JsonObject { ["timestamp"] = builds.Max(b => b["timestamp"]!.GetValue<long>()) },
        ["builds"] = new JsonArray(builds.Select(b => b.DeepClone()).ToArray())
    };
    private static HttpResponseMessage Text(string text, string media = "application/json") => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, media) };
    private static HttpResponseMessage Json(JsonNode value) => Text(value.ToJsonString());
    private static Task<HttpResponseMessage> Reply(HttpResponseMessage value) => Task.FromResult(value);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
    private static string Original(string suffix)
    {
        string relative = "tests/plugins/UnitTests/BuildServerIntegration/JenkinsIntegration.Tests/MockData/JenkinsResult_" + suffix + ".json";
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        { string candidate = Path.Combine(folder.FullName, relative); if (File.Exists(candidate)) { return candidate; } }
        throw new InvalidOperationException("The original Windows Jenkins JSON fixture is missing.");
    }
    private static JsonNode Fixture(string suffix)
    {
        Dictionary<string, string> hashes = new()
        {
            ["freestyle_success"] = "ba7088f9aad9506aadccab98966e237ae4dbc0873560d3af51370f684fbc3d6a",
            ["freestyle_failure"] = "4b02672e75fd02f56ecbb9c32d451ebc0c8c952f5e7379bd87f99474ed480603",
            ["freestyle_running"] = "617a245f8473dfd072f7942d10fbbb2a65f4acedd5a9b67b4bf3cd1e702c2f58",
            ["with_tests"] = "da19768d3075269af609f2145c263378397d42f34c4d1d8df52ac2de083d5bf5",
            ["with_branches"] = "0120a6bf339c30eec8be1da21ba8901cfd9b817f7f67e155e68c7ec43f92d705"
        };
        byte[] bytes = File.ReadAllBytes(Original(suffix));
        Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == hashes[suffix], "The real Windows fixture bytes changed.");
        return JsonNode.Parse(bytes)!;
    }
    private static async Task<JenkinsBuild> ReadFixture(string suffix)
    {
        using Handler handler = new((_, _) => Reply(Json(Job(Fixture(suffix)))));
        await using JenkinsApiClient client = new(Settings("my-project", server: "https://jenkins.example.com/"), Guest(), handler);
        return (await client.ReadAsync()).Builds.Single();
    }
    private static async Task OriginalFixtureAsync(string suffix, JenkinsBuildStatus status, long number, long? duration, string sha)
    {
        JenkinsBuild build = await ReadFixture(suffix);
        Check(build.Number == number && build.Status == status && build.DurationMilliseconds == duration && build.CommitHashes.SequenceEqual([sha]), "Original build oracle changed.");
        Check(build.PageUri?.AbsoluteUri == "https://jenkins.example.com/job/my-project/" + number + "/" && build.BuildId == "/job/my-project#" + number && build.ShowInBuildReportTab, "Real page, stable identity and report flag.");
    }
    private static async Task OriginalTestsAsync()
    {
        JenkinsBuild build = await ReadFixture("with_tests"); Check(build.Status == JenkinsBuildStatus.Unstable && build.TestResults == new JenkinsTestResults(100, 2, 5), "Original test totals.");
        Check(build.Description == "#45 05:00 100 tests (2 failed, 5 skipped) Unstable", "Original full description.");
    }
    private static async Task OriginalDateAsync()
    { JenkinsBuild build = await ReadFixture("freestyle_success"); Check(build.StartedAt.Year == 2019 && build.StartedAt.ToUnixTimeMilliseconds() == 1551700088000 && build.Description == "#42 10:00  Success", "Original timestamp and formatter."); }
    private static async Task SettingsAsync()
    {
        using Files files = new(); files.Write("global", (JenkinsSettings.EnableKey,"true"), (JenkinsSettings.ProviderKey,"Jenkins"), (JenkinsSettings.ServerUrlKey,Base), (JenkinsSettings.ProjectNameKey," {cRepoProject}|p?m "), (JenkinsSettings.IgnoreBuildBranchKey,"release"), ("future.ci","retain"));
        files.Write("local", (JenkinsSettings.ProjectNameKey,"local")); string before = File.ReadAllText(files.Path("global")); GitDistributedSettingsSnapshot snapshot = await files.Snapshot();
        Check(JenkinsSettings.Keys.Count == 6 && JenkinsSettings.IgnoreBuildBranchKey == "BuildServer.Jenkins.IgnoreBuildBranch", "Exact Windows keys.");
        Check(JenkinsSettings.Read(snapshot).ProjectName == "local" && JenkinsSettings.Read(snapshot, scope: GitDistributedSettingsScope.Global).ProjectName == " {cRepoProject}|p?m ", "Raw scope values preserved.");
        JenkinsSettings expanded = JenkinsSettings.Read(snapshot, s => s.Replace("{cRepoProject}", "folder/job/repo", StringComparison.Ordinal), GitDistributedSettingsScope.Global);
        Check(expanded.ProjectName == " folder/job/repo|p?m " && expanded.Active && expanded.CreateIgnoreBuildBranchRegex()!.IsMatch("origin/release"), "Expansion and original regex.");
        Check(File.ReadAllText(files.Path("global")) == before && snapshot.GetValue("future.ci") == "retain", "No persistence or unknown-key loss on Read.");
        await Expect<ArgumentException>(() => { _ = Settings(ignore:"[").CreateIgnoreBuildBranchRegex(); return Task.CompletedTask; });
    }
    private static async Task ServerAsync()
    {
        Check(JenkinsSettings.ParseServerUri("server.invalid").AbsoluteUri == "http://server.invalid:8080/", "Windows default8080.");
        Check(JenkinsSettings.ParseServerUri("server.invalid:9000/jenkins").AbsoluteUri == "http://server.invalid:9000/jenkins/", "Explicit port and basepath.");
        Check(JenkinsSettings.ParseServerUri("https://server.invalid/path").Port == 443, "Explicit HTTPS.");
        foreach (string address in new[] { "", "file:///tmp/jenkins", "https://user:fake-secret@host/", "https://host/?token=fake-secret", "https://host/#fragment", "http://host/\\other", "http://host/\n" })
        { ArgumentException error = await Expect<ArgumentException>(() => { _ = JenkinsSettings.ParseServerUri(address); return Task.CompletedTask; }); Check(!error.ToString().Contains("fake-secret",StringComparison.Ordinal), "No rejected address in errors."); }
    }
    private static async Task ProjectsAsync()
    {
        Uri server = Settings().ServerUri;
        IReadOnlyList<JenkinsProject> projects = JenkinsProjects.Resolve(" p |p|{cRepoProject}?m|space name ", server, s => s.Replace("{cRepoProject}","folder/job/repo",StringComparison.Ordinal));
        Check(projects.Count == 3 && projects[1].IsMultiBranch && projects[1].ProjectUri.AbsoluteUri == Base+"job/folder/job/repo/" && projects[2].ProjectUri.AbsoluteUri.Contains("space%20name",StringComparison.Ordinal), "Trim, dedup, explicit folder path and encoding.");
        Settings("{cRepoShortName}").Validate();
        await Expect<ArgumentException>(() => { using Handler h = new((_,_)=>throw new InvalidOperationException()); _ = new JenkinsApiClient(Settings("{cRepoShortName}"),Guest(),h); return Task.CompletedTask; });
        foreach (string value in new[] { "../other", "p//q", "p\\q", "p#q", "{unknown}", "p?" })
        { await Expect<ArgumentException>(() => { _ = JenkinsProjects.Resolve(value,server); return Task.CompletedTask; }); }
    }
    private static async Task WireAsync()
    {
        Uri? uri = null; HttpMethod? method = null;
        using Handler handler = new((request,_) => { uri=request.RequestUri; method=request.Method; return Reply(Json(Job(Build()))); });
        await using JenkinsApiClient client = new(Settings(),Guest(),handler); JenkinsSnapshot snapshot = await client.ReadAsync();
        Check(method == HttpMethod.Get && uri!.AbsolutePath == "/jenkins/job/p/api/json" && Query(uri)["depth"] == "1" && Query(uri)["tree"] == "lastBuild[timestamp],builds["+JenkinsProjects.BuildTree+"]", "Actual GET original freestyle route/tree.");
        Check(snapshot.Commits[Sha].Status == JenkinsBuildStatus.Success && !snapshot.Truncated, "Real parsed status.");
    }
    private static async Task MultiBranchAsync()
    {
        JsonObject a=Build(project:"p/job/a"), b=Build(sha:Other,project:"p/job/b");
        using Handler handler = new((request,_) => { Check(Query(request.RequestUri!)["depth"] == "2" && Query(request.RequestUri!)["tree"].StartsWith("jobs[",StringComparison.Ordinal), "Original multibranch tree."); return Reply(Json(new JsonObject { ["jobs"]=new JsonArray(Job(a),Job(b)) })); });
        await using JenkinsApiClient client = new(Settings("p?m|p?m"),Guest(),handler); JenkinsSnapshot snapshot=await client.ReadAsync();
        Check(handler.Calls == 1 && snapshot.Builds.Count == 2 && snapshot.Builds.Select(x=>x.BuildId).Distinct().Count()==2 && snapshot.Commits.Count==2, "Real jobs[] branches and stable project+number dedup.");
    }
    private static async Task CustomTreeAsync()
    {
        using Handler handler = new((request,_) => { Check(Query(request.RequestUri!)["tree"] == "?custom[builds]" && Query(request.RequestUri!).Count == 2, "Original custom text retained as one encoded parameter."); return Reply(Json(Job(Build()))); });
        await using JenkinsApiClient client = new(Settings("p?custom[builds]"),Guest(),handler); Check((await client.ReadAsync()).Builds.Count==1,"Compatible custom tree parsed.");
        using Handler missing = new((_,_)=>Reply(Text("{}"))); await using JenkinsApiClient invalid = new(Settings("p?custom"),Guest(),missing); await Expect<JenkinsException>(()=>invalid.ReadAsync());
    }
    private static async Task GuestAsync()
    {
        bool anonymous=false; BuildServerCredentials draft=Basic(); draft.BuildServerCredentialsType=BuildServerCredentialsType.Guest; draft.BearerToken="fake-unused-token";
        using Handler handler = new((r,_)=>{ anonymous=r.Headers.Authorization is null; return Reply(Json(Job(Build()))); });
        await using JenkinsApiClient client=new(Settings(),draft,handler); await client.ReadAsync(); Check(anonymous,"Guest really anonymous despite saved inactive fields.");
    }
    private static async Task BasicAsync()
    {
        string? header=null; BuildServerCredentials draft=Basic(); string expected="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("user-é:fake-password"));
        using Handler handler=new((r,_)=>{ header=r.Headers.Authorization?.ToString(); return Reply(Json(Job(Build()))); });
        await using JenkinsApiClient client=new(Settings(),draft,handler); draft.Username="changed"; draft.Password="changed"; draft.BuildServerCredentialsType=BuildServerCredentialsType.Guest;
        await client.ReadAsync(); Check(header==expected,"Explicit UTF8 Basic captured once for configured HTTP endpoint.");
    }
    private static async Task CredentialsAsync()
    {
        foreach (BuildServerCredentials draft in new[] { new BuildServerCredentials { BuildServerCredentialsType=BuildServerCredentialsType.BearerToken,BearerToken="fake-secret" },new BuildServerCredentials { BuildServerCredentialsType=(BuildServerCredentialsType)99 },new BuildServerCredentials { BuildServerCredentialsType=BuildServerCredentialsType.UsernameAndPassword,Username="bad:fake-secret",Password="fake-secret" } })
        { using Handler handler=new((_,_)=>throw new InvalidOperationException("No HTTP")); ArgumentException error=await Expect<ArgumentException>(()=>{ _=new JenkinsApiClient(Settings(),draft,handler);return Task.CompletedTask; }); Check(handler.Calls==0&&!error.ToString().Contains("fake-secret",StringComparison.Ordinal),"Unsupported mode rejects without exposing draft."); }
    }
    private static async Task TransportAsync()
    {
        using HttpClientHandler transport=new() { AllowAutoRedirect=true,UseCookies=true,UseDefaultCredentials=true,DefaultProxyCredentials=new NetworkCredential("fake-user","fake-password") };
        await using JenkinsApiClient client=new(Settings(),Guest(),transport);
        Check(!transport.AllowAutoRedirect&&!transport.UseCookies&&!transport.UseDefaultCredentials&&transport.Credentials is null&&transport.DefaultProxyCredentials is null,"No OS credential or cookie inheritance.");
    }
    private static async Task TimestampCacheAsync()
    {
        Clock clock=new();List<string> trees=[];using Handler handler=new((r,_)=>{trees.Add(Query(r.RequestUri!)["tree"]);return Reply(Json(Job(Build())));});
        await using JenkinsApiClient client=new(Settings(),Guest(),handler,clock);JenkinsSnapshot first=await client.ReadAsync();Check(ReferenceEquals(first,await client.ReadAsync())&&handler.Calls==1,"Idle time cache.");
        clock.Advance(121);JenkinsSnapshot next=await client.ReadAsync();Check(handler.Calls==2&&trees[0].Contains("builds[",StringComparison.Ordinal)&&trees[1]=="lastBuild[timestamp]"&&next.Commits[Sha].Build.BuildId==first.Commits[Sha].Build.BuildId,"Unchanged timestamp retains full results.");
    }
    private static async Task NewTimestampAsync()
    {
        Clock clock=new();int call=0;List<string> trees=[];using Handler handler=new((r,_)=>{trees.Add(Query(r.RequestUri!)["tree"]);return Reply(Json(Job(++call==1?Build():Build(2,sha:Other,timestamp:2000))));});
        await using JenkinsApiClient client=new(Settings(),Guest(),handler,clock);await client.ReadAsync();clock.Advance(121);JenkinsSnapshot next=await client.ReadAsync();
        Check(call==3&&trees[1]=="lastBuild[timestamp]"&&trees[2].Contains("builds[",StringComparison.Ordinal)&&next.Commits.ContainsKey(Other),"Short then full only on newer timestamp.");
    }
    private static async Task RunningCacheAsync()
    {
        Clock clock=new();List<string> trees=[];using Handler handler=new((r,_)=>{trees.Add(Query(r.RequestUri!)["tree"]);return Reply(Json(Job(Build(result:null,running:true))));});
        await using JenkinsApiClient client=new(Settings(),Guest(),handler,clock);await client.ReadAsync();clock.Advance(9);await client.ReadAsync();Check(handler.Calls==1,"Running cache below10s.");clock.Advance(2);await client.ReadAsync();Check(handler.Calls==2&&trees.All(t=>t.Contains("builds[",StringComparison.Ordinal)),"Running resets project timestamp cache.");
    }
    private static async Task ETagAsync()
    {
        int call=0;bool sent=false;using Handler handler=new((r,_)=>{if(++call>1){sent=r.Headers.IfNoneMatch.Any(t=>t.Tag=="\"one\"");return Reply(new(HttpStatusCode.NotModified));}HttpResponseMessage response=Json(Job(Build()));response.Headers.ETag=new("\"one\"");return Reply(response);});
        await using JenkinsApiClient client=new(Settings(),Guest(),handler);await client.ReadAsync();JenkinsSnapshot second=await client.ReadAsync(true);Check(call==2&&sent&&second.Commits[Sha].Status==JenkinsBuildStatus.Success,"Force revalidation and validated304 bytes.");
    }
    private static JenkinsBuild Model(long number,JenkinsBuildStatus status,int start,string sha=Sha)=>new(){ProjectId="p",Number=number,Status=status,StartedAt=DateTimeOffset.UnixEpoch.AddSeconds(start),CommitHashes=Array.AsReadOnly(new[]{sha})};
    private static Task PriorityAsync()
    {
        JenkinsBuild stopped=Model(1,JenkinsBuildStatus.Stopped,1),running=Model(2,JenkinsBuildStatus.InProgress,2),success=Model(3,JenkinsBuildStatus.Success,3),failed=Model(4,JenkinsBuildStatus.Failure,4);
        Check(new JenkinsSnapshot([stopped,running,success,failed],DateTimeOffset.UnixEpoch).Commits[Sha].Build==success,"First completed never replaced by later completed in same pass.");
        Check(new JenkinsSnapshot([running,success with{Number=running.Number}],DateTimeOffset.UnixEpoch).Builds.Single().Status==JenkinsBuildStatus.Success,"Duplicate build identity cannot discard a completed update before priority selection.");
        Check(new JenkinsSnapshot([running,success with{StartedAt=stopped.StartedAt}],DateTimeOffset.UnixEpoch).Commits[Sha].Build==running,"Watcher start-time guard follows priority admission.");return Task.CompletedTask;
    }
    private static Task WatcherRetentionAsync()
    {
        JenkinsBuild newer=Model(2,JenkinsBuildStatus.InProgress,10),older=Model(1,JenkinsBuildStatus.Success,1);JenkinsSnapshot first=new([newer],DateTimeOffset.UnixEpoch);
        JenkinsSnapshot second=new([older],DateTimeOffset.UnixEpoch,previous:first.Commits);Check(second.Commits[Sha].Build==newer,"Older known history cannot regress watcher.");
        JenkinsSnapshot third=new([newer with{Status=JenkinsBuildStatus.Success}],DateTimeOffset.UnixEpoch,previous:second.Commits);Check(third.Commits[Sha].Status==JenkinsBuildStatus.Success,"Equal start completion replaces running.");return Task.CompletedTask;
    }
    private static async Task CachedProjectDoesNotReplayAsync()
    {
        Clock clock = new(); bool nextPass = false; List<string> requests = [];
        using Handler handler = new((request, _) =>
        {
            bool p = request.RequestUri!.AbsolutePath.Contains("/job/p/", StringComparison.Ordinal);
            bool full = Query(request.RequestUri)["tree"].Contains("builds[", StringComparison.Ordinal);
            requests.Add((p ? "p" : "q") + (full ? ":full" : ":probe"));
            if (p) { return Reply(Json(Job(Build(timestamp: 100, project: "p")))); }
            JsonObject old = Build(timestamp: 50, project: "q");
            return Reply(Json(nextPass ? Job(Build(2, result: null, running: true, timestamp: 200, project: "q"), old) : Job(old)));
        });
        await using JenkinsApiClient client = new(Settings("p|q"), Guest(), handler, clock);
        JenkinsSnapshot first = await client.ReadAsync();
        Check(first.Commits[Sha].Build.ProjectId == "/jenkins/job/p", "The first pass admits the configured first completed project.");
        nextPass = true; requests.Clear(); clock.Advance(121);
        JenkinsSnapshot second = await client.ReadAsync();
        Check(second.Commits[Sha].Status == JenkinsBuildStatus.InProgress && second.Commits[Sha].Build.ProjectId == "/jenkins/job/q"
            && second.Commits[Sha].Build.Number == 2 && second.Commits[Sha].Build.StartedAt.ToUnixTimeMilliseconds() == 200,
            "Only q emits this pass, so its t200 running build replaces the watcher's t100 completed build.");
        Check(requests.SequenceEqual(["p:probe", "q:probe", "q:full"]), "The unchanged p is probed but never read or emitted as full.");
        Check(second.Builds.Count == 3 && second.Builds.Select(build => build.BuildId).ToHashSet().SetEquals(
            new[] { "/jenkins/job/p#1", "/jenkins/job/q#1", "/jenkins/job/q#2" }), "Retained history still contains p and both real q builds.");
    }

    private static async Task RequiredFullEmissionOrderAsync()
    {
        Clock clock = new(); bool nextPass = false;
        using Handler handler = new((request, _) =>
        {
            bool p = request.RequestUri!.AbsolutePath.Contains("/job/p/", StringComparison.Ordinal);
            if (p) { return Reply(Json(Job(Build(result: nextPass ? "SUCCESS" : null, running: !nextPass, timestamp: 100, project: "p")))); }
            JsonObject old = Build(timestamp: 50, project: "q");
            return Reply(Json(nextPass ? Job(Build(2, result: "FAILURE", timestamp: 200, project: "q"), old) : Job(old)));
        });
        // q is configured first, but p's running state already requires a full read next pass.
        await using JenkinsApiClient client = new(Settings("q|p"), Guest(), handler, clock);
        JenkinsSnapshot first = await client.ReadAsync(); Check(first.Commits[Sha].Build.ProjectId == "/jenkins/job/q", "Initial configured order is unchanged.");
        nextPass = true; clock.Advance(121); JenkinsSnapshot second = await client.ReadAsync();
        Check(second.Commits[Sha].Status == JenkinsBuildStatus.Success && second.Commits[Sha].Build.ProjectId == "/jenkins/job/p"
            && second.Commits[Sha].Build.StartedAt.ToUnixTimeMilliseconds() == 100,
            "p's already-required full emission admits the first completed status before q's timestamp-promoted full, exactly as Windows.");
        Check(second.Builds.Count == 3 && second.Builds.Any(build => build.ProjectId == "/jenkins/job/q" && build.Number == 2 && build.Status == JenkinsBuildStatus.Failure),
            "Priority admission does not discard q's newer failed build from the full retained list.");
    }

    private static async Task RetainedBuildBoundFiltersEmissionsAsync()
    {
        Clock clock = new(); bool nextPass = false; List<string> requests = [];
        string cachedSha = new('a', 40);
        using Handler handler = new((request, _) =>
        {
            bool q = request.RequestUri!.AbsolutePath.Contains("/job/q/", StringComparison.Ordinal);
            bool full = Query(request.RequestUri)["tree"].Contains("builds[", StringComparison.Ordinal);
            requests.Add((q ? "q" : "p") + (full ? ":full" : ":probe"));
            Check(!nextPass || !q, "The second pass must reach the global budget after p's required full, before probing cached q.");
            if (q) { return Reply(Json(Job(Build(timestamp: 50, sha: cachedSha, project: "q")))); }
            return Reply(Json(nextPass
                ? Job(Build(3, result: null, running: true, timestamp: 300, project: "p"), Build(2, sha: Other, timestamp: 200, project: "p"))
                : Job(Build(result: null, running: true, timestamp: 100, project: "p"))));
        });
        await using JenkinsApiClient client = new(Settings("q|p"), Guest(), handler, clock) { MaxBuilds = 2 };
        JenkinsSnapshot first = await client.ReadAsync();
        Check(!first.Truncated && first.Builds.Count == 2 && first.Commits.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([cachedSha, Sha])
            && requests.SequenceEqual(["q:full", "p:full"]), "The initial pass fills the bound with q and p's two real statuses in configured order.");
        nextPass = true; requests.Clear(); clock.Advance(121); JenkinsSnapshot second = await client.ReadAsync();
        string[] retainedIds = ["/jenkins/job/q#1", "/jenkins/job/p#3"];
        Check(requests.SequenceEqual(["p:full"]), "The already-running p emits before cached q is considered, without an invented probe.");
        Check(second.Truncated && second.Diagnostics.Any(message => message.Contains("retained build bound", StringComparison.Ordinal)),
            "The configured-order retained history explicitly reports the dropped build.");
        Check(second.Builds.Select(build => build.BuildId).SequenceEqual(retainedIds),
            "q's cached A and p's newest B are retained; p's newly emitted C is outside MaxBuilds=2.");
        Check(second.Commits.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([cachedSha, Sha])
            && !second.Commits.ContainsKey(Other) && second.Commits[Sha].Build.BuildId == retainedIds[1],
            "A truncated fresh emission must not create a phantom commit status while the retained update still reaches the watcher.");
        BuildServerSnapshot projected = BuildServerSnapshot.FromJenkins(second, client.BuildPageUri);
        Check(projected.Truncated && projected.Diagnostics.SequenceEqual(second.Diagnostics)
            && projected.Runs.Select(run => run.Id).SequenceEqual(retainedIds)
            && projected.Commits.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([cachedSha, Sha])
            && projected.Commits.Values.SelectMany(status => status.Runs).All(run => retainedIds.Contains(run.Id))
            && projected.Commits[Sha].Runs.Single().Id == retainedIds[1],
            "The shared report/grid projection must not resurrect excluded C as a status, run or build-page action.");
    }

    private static async Task AutomaticMissingRecoveryAsync()
    {
        Clock clock = new(); int calls = 0;
        using Handler handler = new((_, _) => Reply(++calls == 1 ? new(HttpStatusCode.NotFound) : Json(Job(Build()))));
        await using JenkinsApiClient client = new(Settings(), Guest(), handler, clock);
        JenkinsSnapshot missing = await client.ReadAsync();
        Check(missing.Truncated && missing.Diagnostics.Any(text => text.Contains("not found", StringComparison.Ordinal)), "The actual 404 creates incomplete project provenance.");
        clock.Advance(121); JenkinsSnapshot recovered = await client.ReadAsync();
        Check(calls == 2 && recovered.Builds.Count == 1 && recovered.Commits.ContainsKey(Sha) && !recovered.Truncated
            && recovered.Diagnostics.Count == 0, "A successful automatic full replaces the missing state without requiring force.");
    }

    private static async Task CachedIncompleteRecoveryAsync()
    {
        Clock clock = new(); int pass = 0, secondPassCalls = 0;
        using Handler handler = new((request, _) =>
        {
            bool busy = request.RequestUri!.AbsolutePath.Contains("/job/busy/", StringComparison.Ordinal);
            if (pass == 0) { return Reply(busy ? Json(Job(Build(result: null, running: true, timestamp: 100, project: "busy"))) : new(HttpStatusCode.NotFound)); }
            if (pass == 1)
            {
                Check(busy, "The request budget must stop before another HTTP call for the cached missing project.");
                return Reply(++secondPassCalls == 1 ? Redirect(request.RequestUri.AbsoluteUri) : Json(Job(Build(timestamp: 100, project: "busy"))));
            }
            return Reply(Json(busy ? Job(Build(timestamp: 100, project: "busy")) : Job(Build(sha: Other, timestamp: 200, project: "missing"))));
        });
        await using JenkinsApiClient client = new(Settings("busy|missing"), Guest(), handler, clock) { MaxRequests = 2 };
        JenkinsSnapshot first = await client.ReadAsync(); Check(first.Truncated, "Missing state initially incomplete.");
        pass = 1; clock.Advance(121); JenkinsSnapshot limited = await client.ReadAsync();
        Check(secondPassCalls == 2 && limited.Truncated && limited.Diagnostics.Any(text => text.Contains("not found", StringComparison.Ordinal))
            && limited.Diagnostics.Any(text => text.Contains("request bound", StringComparison.Ordinal)),
            "Refreshing busy cannot clear the cached missing-project warning when the request bound prevents that project's full read.");
        pass = 2; clock.Advance(121); JenkinsSnapshot recovered = await client.ReadAsync();
        Check(!recovered.Truncated && recovered.Diagnostics.Count == 0 && recovered.Builds.Count == 2 && recovered.Commits.ContainsKey(Other),
            "Once the missing project is actually refreshed, its warning and the old pass request bound both disappear.");
    }

    private static async Task IgnoreAsync()
    {
        JsonObject node=Build();node["actions"]!.AsArray().Add(new JsonObject{["lastBuiltRevision"]=new JsonObject{["SHA1"]=Other,["branch"]=new JsonArray(new JsonObject{["name"]="origin/release"})}});
        using Handler handler=new((_,_)=>Reply(Json(Job(node))));await using JenkinsApiClient client=new(Settings(),Guest(),handler);JenkinsSnapshot snapshot=await client.ReadAsync();Check(snapshot.Commits.Count==2&&snapshot.Commits.ContainsKey(Other),"SHA256 and multiple actions.");
        using Handler ignored=new((_,_)=>Reply(Json(Job(node))));await using JenkinsApiClient filtered=new(Settings(ignore:"release"),Guest(),ignored);Check((await filtered.ReadAsync()).Builds.Count==0,"Any branch match suppresses entire build including all its SHAs.");
        using Handler original=new((_,_)=>Reply(Json(Job(Fixture("with_branches")))));await using JenkinsApiClient real=new(Settings("my-project",ignore:"release",server:"https://jenkins.example.com/"),Guest(),original);Check((await real.ReadAsync()).Commits.Count==0,"Real original two-branch fixture is filtered whole.");
    }
    private static async Task MissingGitAsync()
    {JsonObject node=Build();node["actions"]=new JsonArray(new JsonObject());using Handler handler=new((_,_)=>Reply(Json(Job(node))));await using JenkinsApiClient client=new(Settings(),Guest(),handler);JenkinsSnapshot snapshot=await client.ReadAsync();Check(snapshot.Commits.Count==0&&snapshot.Diagnostics.Count>0,"No synthetic commit from a non-Git build.");}
    private static async Task StatusAsync()
    {
        foreach((string result,JenkinsBuildStatus expected)in new[]{("SUCCESS",JenkinsBuildStatus.Success),("FAILURE",JenkinsBuildStatus.Failure),("UNSTABLE",JenkinsBuildStatus.Unstable),("ABORTED",JenkinsBuildStatus.Stopped),("NOT_BUILT",JenkinsBuildStatus.Unknown)})
        {Check(JenkinsBuild.Map(result,false)==expected&&JenkinsBuild.Map(result,true)==JenkinsBuildStatus.InProgress,"Original result map and running priority.");}
        using Handler handler=new((_,_)=>Reply(Json(Job(Build(result:"FAILURE",running:true)))));await using JenkinsApiClient client=new(Settings(),Guest(),handler);JenkinsBuild build=(await client.ReadAsync()).Builds.Single();Check(build.DurationMilliseconds is null&&build.Description=="#1   InProgress"&&build.Tooltip is null,"Original running empty duration and unset tooltip.");
    }
    private static async Task MalformedAsync()
    {
        List<JsonObject> values=[];
        foreach((string key,JsonNode? value)in new (string,JsonNode?)[]{("number",JsonValue.Create(0)),("timestamp",JsonValue.Create(long.MaxValue)),("duration",JsonValue.Create(-1)),("building",JsonValue.Create("true")),("actions",new JsonObject())}){JsonObject n=Build();n[key]=value;values.Add(n);}
        JsonObject counts=Build();counts["actions"]!.AsArray().Add(new JsonObject{["totalCount"]=2,["failCount"]=3,["skipCount"]=0});values.Add(counts);
        JsonObject sha=Build();sha["actions"]![0]!["lastBuiltRevision"]!["SHA1"]="fake-secret-invalid-sha";values.Add(sha);
        foreach(JsonObject value in values){using Handler handler=new((_,_)=>Reply(Json(Job(value))));await using JenkinsApiClient client=new(Settings(),Guest(),handler);JenkinsException error=await Expect<JenkinsException>(()=>client.ReadAsync());Check(!error.ToString().Contains("fake-secret",StringComparison.Ordinal),"Invalid response values never logged.");}
    }
    private static async Task AtomicAsync()
    {
        Clock clock=new();int call=0;bool originalETag=false;using Handler handler=new((r,_)=>{call++;if(call==2)return Reply(Json(Job(Build(2,timestamp:2000))));if(call==3){HttpResponseMessage bad=Text("{bad}");bad.Headers.ETag=new("\"bad\"");return Reply(bad);}if(call==4)originalETag=r.Headers.IfNoneMatch.Any(t=>t.Tag=="\"stable\"");HttpResponseMessage good=Json(Job(Build()));good.Headers.ETag=new("\"stable\"");return Reply(good);});
        await using JenkinsApiClient client=new(Settings(),Guest(),handler,clock);JenkinsSnapshot first=await client.ReadAsync();clock.Advance(121);await Expect<JenkinsException>(()=>client.ReadAsync());JenkinsSnapshot recovered=await client.ReadAsync(true);Check(originalETag&&call==4&&recovered.Commits[Sha].Build.BuildId==first.Commits[Sha].Build.BuildId,"Failed parse did not publish bad ETag or staged timestamp.");
    }
    private static async Task BoundsAsync()
    {
        using Handler builds=new((_,_)=>Reply(Json(Job(Build(),Build(2,sha:Other)))));await using JenkinsApiClient limited=new(Settings(),Guest(),builds){MaxBuildsPerProject=1};JenkinsSnapshot a=await limited.ReadAsync();Check(a.Truncated&&a.Builds.Count==1&&a.Diagnostics.Count>0,"Per-project builds explicit bound.");
        using Handler projects=new((_,_)=>Reply(Json(Job(Build()))));await using JenkinsApiClient one=new(Settings("p|q"),Guest(),projects){MaxProjects=1};Check((await one.ReadAsync()).Truncated&&projects.Calls==1,"Configured project bound.");
        using Handler requests=new((_,_)=>Reply(Json(Job(Build()))));await using JenkinsApiClient req=new(Settings("p|q"),Guest(),requests){MaxRequests=1};Check((await req.ReadAsync()).Truncated&&requests.Calls==1,"Request bound preserves first partial project explicitly.");
        using Handler jobs=new((_,_)=>Reply(Json(new JsonObject{["jobs"]=new JsonArray(Job(Build(project:"p/job/a")),Job(Build(project:"p/job/b")))})));await using JenkinsApiClient multi=new(Settings("p?m"),Guest(),jobs){MaxJobs=1};JenkinsSnapshot b=await multi.ReadAsync();Check(b.Truncated&&b.Builds.Count==1,"Multibranch job bound.");
    }
    private static async Task MissingAsync()
    {
        int call=0;using Handler handler=new((_,_)=>Reply(++call==1?Json(Job(Build())):new(HttpStatusCode.NotFound)));await using JenkinsApiClient client=new(Settings(),Guest(),handler);await client.ReadAsync();JenkinsSnapshot second=await client.ReadAsync(true);Check(second.Truncated&&second.Diagnostics.Count>0&&second.Commits.ContainsKey(Sha),"404 is visible, retains known data and is not a complete empty success.");
    }
    private static async Task ErrorsAsync()
    {
        foreach(int code in new[]{401,403,429,500}){using Handler handler=new((_,_)=>Reply(new((HttpStatusCode)code){Content=new StringContent("fake-secret-response")}));await using JenkinsApiClient client=new(Settings(),Basic(),handler);JenkinsException error=await Expect<JenkinsException>(()=>client.ReadAsync());Check(error.StatusCode==code&&!error.ToString().Contains("fake-secret",StringComparison.Ordinal)&&!error.ToString().Contains("fake-password",StringComparison.Ordinal),"Readable fixed status without secrets.");}
        using Handler html=new((_,_)=>Reply(Text("<html>fake-secret-login</html>","text/html")));await using JenkinsApiClient login=new(Settings(),Guest(),html);Check((await Expect<JenkinsException>(()=>login.ReadAsync())).StatusCode==401&&html.Calls==1,"HTML login is explicit auth error without recursive retry.");
        using Handler transport=new((_,_)=>throw new HttpRequestException("fake-secret-transport"));await using JenkinsApiClient offline=new(Settings(),Guest(),transport);Check(!(await Expect<JenkinsException>(()=>offline.ReadAsync())).ToString().Contains("fake-secret",StringComparison.Ordinal),"No inner transport leakage.");
    }
    private static HttpResponseMessage Redirect(string url){HttpResponseMessage response=new(HttpStatusCode.Redirect);response.Headers.Location=new(url,UriKind.RelativeOrAbsolute);return response;}
    private static async Task RedirectAsync()
    {int call=0;using Handler handler=new((r,_)=>Reply(++call==1?Redirect(r.RequestUri!.AbsoluteUri):Json(Job(Build()))));await using JenkinsApiClient client=new(Settings(),Basic(),handler);Check((await client.ReadAsync()).Builds.Count==1&&call==2,"Same-resource bounded redirect.");using Handler loop=new((r,_)=>Reply(Redirect(r.RequestUri!.AbsoluteUri)));await using JenkinsApiClient endless=new(Settings(),Guest(),loop);await Expect<JenkinsException>(()=>endless.ReadAsync());Check(loop.Calls==4,"Finite redirect bound.");}
    private static async Task UnsafeRedirectAsync()
    {
        foreach(string target in new[]{"https://other.invalid/jenkins/job/p/api/json","https://server.invalid:8080/other/job/p/api/json","https://server.invalid:8080/jenkins/job/other/api/json","https://user:fake-secret@server.invalid:8080/jenkins/job/p/api/json","https://server.invalid:8080/jenkins/job/p/api/json?token=fake-secret","https://server.invalid:8080/jenkins/job/p/api/json#fake-secret","http://server.invalid:8080/jenkins/job/p/api/json"})
        {using Handler handler=new((_,_)=>Reply(Redirect(target)));await using JenkinsApiClient client=new(Settings(server:"https://server.invalid:8080/jenkins/"),Basic(),handler);JenkinsException error=await Expect<JenkinsException>(()=>client.ReadAsync());Check(handler.Calls==1&&!error.ToString().Contains("fake-secret",StringComparison.Ordinal),"No credential forwarded to unsafe destination.");}
    }
    private static async Task PagesAsync()
    {
        foreach(string target in new[]{"https://foreign.invalid/job/p/1/",Base+"job/q/1/",Base+"job/p/2/",Base+"job/p/1/?secret=fake-secret","http://user:fake-secret@server.invalid:8080/jenkins/job/p/1/"})
        {JsonObject node=Build();node["url"]=target;using Handler handler=new((_,_)=>Reply(Json(Job(node))));await using JenkinsApiClient client=new(Settings(),Guest(),handler);JenkinsBuild build=(await client.ReadAsync()).Builds.Single();Check(build.PageUri is null&&client.BuildPageUri(build)is null,"Unsafe link never launched.");}
    }
    private static async Task BodyTimeoutAsync()
    {
        BlockingStream stream=new();using Handler handler=new((_,_)=>Reply(new(HttpStatusCode.OK){Content=new StreamContent(stream)}));await using JenkinsApiClient client=new(Settings(),Guest(),handler){RequestTimeout=TimeSpan.FromMilliseconds(100)};
        JenkinsException error=await Expect<JenkinsException>(()=>client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)));Check(error.Message.Contains("timed out",StringComparison.Ordinal)&&stream.Disposed,"Deadline covers body and disposes it.");
    }
    private static async Task HeaderTimeoutAsync()
    {
        using Handler handler=new(async(_,token)=>{await Task.Delay(Timeout.Infinite,token);return Json(Job());});await using JenkinsApiClient client=new(Settings(),Guest(),handler){RequestTimeout=TimeSpan.FromMilliseconds(100)};
        Check((await Expect<JenkinsException>(()=>client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))).Message.Contains("timed out",StringComparison.Ordinal),"Owned deadline is a readable timeout rather than caller cancellation.");
    }
    private static async Task RegexTimeoutAsync()
    {
        JsonObject node=Build();node["actions"]![0]!["lastBuiltRevision"]!["branch"]![0]!["name"]=new string('a',20000)+"!";
        using Handler handler=new((_,_)=>Reply(Json(Job(node))));await using JenkinsApiClient client=new(Settings(ignore:"(a+)+$"),Guest(),handler);
        Check((await Expect<JenkinsException>(()=>client.ReadAsync())).Message.Contains("time bound",StringComparison.Ordinal),"Bounded regex cannot stall the reader or commit partial results.");
    }
    private static async Task CancellationAsync()
    {
        TaskCompletionSource entered=new(TaskCreationOptions.RunContinuationsAsynchronously);using Handler handler=new(async(_,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return Json(Job());});await using JenkinsApiClient client=new(Settings(),Guest(),handler);
        using CancellationTokenSource first=new(),queued=new();Task<JenkinsSnapshot> a=client.ReadAsync(cancellationToken:first.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Task<JenkinsSnapshot> b=client.ReadAsync(cancellationToken:queued.Token);queued.Cancel();await Expect<OperationCanceledException>(()=>b);first.Cancel();await Expect<OperationCanceledException>(()=>a);Check(handler.Calls==1,"Canceled gate waiter does not touch HTTP.");
    }
    private static async Task DrainAsync()
    {
        BlockingStream stream=new();using Handler handler=new((_,_)=>Reply(new(HttpStatusCode.OK){Content=new StreamContent(stream)}));JenkinsApiClient client=new(Settings(),Guest(),handler);Task<JenkinsSnapshot> read=client.ReadAsync();await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));await Expect<OperationCanceledException>(()=>read);Check(stream.Disposed,"Dispose drains active body.");await Expect<ObjectDisposedException>(()=>client.ReadAsync());await client.DisposeAsync();
    }
    private static async Task ResponseBoundsAsync()
    {
        foreach(string text in new[]{"null","{",new string('[',40)+"0"+new string(']',40),new string('x',3000)}){using Handler handler=new((_,_)=>Reply(Text(text)));await using JenkinsApiClient client=new(Settings(),Guest(),handler){MaxResponseBytes=1024};await Expect<JenkinsException>(()=>client.ReadAsync());}
        using Handler chunked=new((_,_)=>Reply(new(HttpStatusCode.OK){Content=new StreamContent(new NonSeekStream(Encoding.UTF8.GetBytes(new string('x',3000))))}));await using JenkinsApiClient bounded=new(Settings(),Guest(),chunked){MaxResponseBytes=1024};await Expect<JenkinsException>(()=>bounded.ReadAsync());
        using Handler cache=new((_,_)=>Reply(new(HttpStatusCode.NotModified)));await using JenkinsApiClient missing=new(Settings(),Guest(),cache);await Expect<JenkinsException>(()=>missing.ReadAsync());
    }
    private static async Task DisabledAsync()
    {using Handler handler=new((_,_)=>throw new InvalidOperationException("No HTTP"));await using JenkinsApiClient client=new(new(){ServerUrl=Base,ProjectName="p"},Guest(),handler);Check((await client.ReadAsync()).Builds.Count==0&&handler.Calls==0,"Inactive means no requests.");Check(!Settings().ToString().Contains(Base,StringComparison.Ordinal)&&!Model(1,JenkinsBuildStatus.Success,1).ToString().Contains(Sha,StringComparison.Ordinal),"Safe debug strings.");}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {private int _calls;public int Calls=>Volatile.Read(ref _calls);protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Interlocked.Increment(ref _calls);return send(request,token);}}
    private sealed class Clock:TimeProvider
    {private DateTimeOffset _now=DateTimeOffset.UnixEpoch;public override DateTimeOffset GetUtcNow()=>_now;public void Advance(int seconds)=>_now=_now.AddSeconds(seconds);}
    private sealed class NonSeekStream(byte[] bytes):MemoryStream(bytes){public override bool CanSeek=>false;}
    private sealed class BlockingStream:Stream
    {
        public TaskCompletionSource Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Disposed{get;private set;}
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){Started.TrySetResult();await Task.Delay(Timeout.Infinite,token);return 0;}
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class Files:IDisposable
    {
        private readonly string _root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ge-jenkins-"+Guid.NewGuid().ToString("N"));public Files()=>Directory.CreateDirectory(_root);
        public string Path(string name)=>System.IO.Path.Combine(_root,name+".settings");
        public void Write(string name,params(string Key,string Value)[] values)=>File.WriteAllText(Path(name),new XElement("dictionary",values.Select(v=>new XElement("item",new XElement("key",new XElement("string",v.Key)),new XElement("value",new XElement("string",v.Value))))).ToString());
        public async Task<GitDistributedSettingsSnapshot> Snapshot()=>new(await GitXmlSettingsStore.LoadAsync(Path("global")),await GitXmlSettingsStore.LoadAsync(Path("local")),await GitXmlSettingsStore.LoadAsync(Path("distributed")));
        public void Dispose()=>Directory.Delete(_root,true);
    }
}
