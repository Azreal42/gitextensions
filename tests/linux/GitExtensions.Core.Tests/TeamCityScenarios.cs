using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using GitExtensions.Linux;
using GitUIPluginInterfaces.BuildServerIntegration;

internal static class TeamCityScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("TeamCity exact Windows settings defaults and project expansion do not enable CI or write XML", SettingsAsync),
        ("TeamCity raw distributed setting precedence preserves invalid higher values and unknown keys", ScopeAsync),
        ("TeamCity Windows HTTP default and explicit HTTP HTTPS base paths preserve ports without URL credentials", EndpointAsync),
        ("TeamCity Guest Basic and Bearer use their original distinct REST prefixes without OS discovery", CredentialsAsync),
        ("TeamCity credential drafts are snapshotted and inactive fields are never sent", DraftAsync),
        ("TeamCity projects paginate through empty next pages and preserve archive and parent metadata", ProjectsAsync),
        ("TeamCity project build types paginate nested XML and retain the original display name", BuildTypesAsync),
        ("TeamCity direct build type lookup refuses a different returned configuration", BuildTypeAsync),
        ("TeamCity configured projects and regex select build IDs across all branches with detailed revisions", ReadAsync),
        ("TeamCity running cancelled success failure and future states map without false success", StatesAsync),
        ("TeamCity snapshots keep sibling build definitions and newest reruns per SHA immutably", SnapshotAsync),
        ("TeamCity dates respect server UTC offsets and malformed details never assign a commit", DatesAsync),
        ("TeamCity ETag revalidation supports 304 only with a cached XML representation", ETagAsync),
        ("TeamCity fresh snapshots avoid requests and force refresh revalidates actual GETs", CacheAsync),
        ("TeamCity pagination cancellation retains the last complete snapshot and honours pre-cancellation", CancellationAsync),
        ("TeamCity disposal cancels and drains owned HTTP requests and refuses new operations", DisposeAsync),
        ("TeamCity safe redirects stay within the configured REST origin and application base path", RedirectAsync),
        ("TeamCity unsafe redirect and nextHref origins basepaths downgrades and cycles are rejected", UnsafeLinksAsync),
        ("TeamCity bounded pages items requests and build details report incomplete history explicitly", BoundsAsync),
        ("TeamCity auth rate limit and server errors never disclose credentials or response bodies", ErrorsAsync),
        ("TeamCity HTML XML DTD and truncated responses fail safely without exposing payloads", InvalidXmlAsync),
        ("TeamCity response byte bounds apply to known and unknown content lengths", SizeAsync),
        ("TeamCity transport failures and timeouts are readable without original exception secrets", TransportAsync),
        ("TeamCity explicit build pages preserve base path and guest option without server URL query secrets", PagesAsync),
        ("TeamCity disabled integration sends no polling HTTP and invalid regex is admitted before no network", DisabledAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherSha = "abcdef0123456789abcdef0123456789abcdef01";
    private static TeamCitySettings Settings(string server = "https://ci.invalid/tc/") => new()
    { Enabled = true, Provider = TeamCitySettings.ProviderName, ServerUrl = server, ProjectName = "P|Q", BuildIdFilter = "^P_", LogAsGuest = true };
    private static void Check(bool value, string why) { if (!value) { throw new InvalidOperationException(why); } }
    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    private static HttpResponseMessage Empty() => Xml("<projects count='0'/>");
    private static Task<HttpResponseMessage> Reply(HttpResponseMessage response) => Task.FromResult(response);
    private static HttpResponseMessage Redirect(string uri)
    { HttpResponseMessage r = new(HttpStatusCode.Redirect); r.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute); return r; }
    private static TeamCityBuild Build(string id, string type, string state = "finished", string status = "SUCCESS", params string[] hashes) =>
        new(id, type, "1", state, status, state == "running", false, DateTimeOffset.UnixEpoch, null, "Build", "https://ci.invalid/tc/viewLog.html?buildId=" + id, hashes.Length == 0 ? [Sha] : hashes);
    private static HttpResponseMessage Details(string id, string type = "P_B", string state = "finished", string status = "SUCCESS", string? extra = null) => Xml(
        $"<build id='{id}' buildTypeId='{type}' number='15' state='{state}' running='{(state == "running" ? "true" : "false")}' status='{status}' webUrl='https://ci.invalid/tc/viewLog.html?buildId={id}'>"
        + "<startDate>20261019T120000+0230</startDate><statusText>Finished</statusText><running-info currentStageText='Step 2/3'/>"
        + $"<revisions><revision version='{Sha}'/><revision version='{OtherSha}'/><revision version='svn-123'/></revisions>{extra}</build>");
    private static HttpResponseMessage Standard(HttpRequestMessage request)
    {
        string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath), query = Uri.UnescapeDataString(request.RequestUri.Query);
        Check(request.Method == HttpMethod.Get, "No operation mutates a TeamCity server.");
        if (path.EndsWith("/projects/P", StringComparison.Ordinal) || path.EndsWith("/projects/Q", StringComparison.Ordinal))
        { return Xml("<project id='P'><buildTypes count='2'><buildType id='P_B' name='Build' projectId='P'/><buildType id='X_B' name='Excluded' projectId='P'/></buildTypes></project>"); }
        if (path.EndsWith("/buildTypes/id:P_B/builds/", StringComparison.Ordinal))
        { return Xml(query.Contains("running:true", StringComparison.Ordinal) ? "<builds count='1'><build id='11'/></builds>" : "<builds count='1'><build id='10'/></builds>"); }
        if (path.EndsWith("/builds/id:10", StringComparison.Ordinal)) { return Details("10", status: "FAILURE"); }
        if (path.EndsWith("/builds/id:11", StringComparison.Ordinal)) { return Details("11", state: "running"); }
        throw new InvalidOperationException("Unexpected fake endpoint: " + path);
    }
    private static async Task SettingsAsync()
    {
        using Files f = new(); GitXmlSettingsSnapshot global = await GitXmlSettingsStore.LoadAsync(f.Path("global"));
        TeamCitySettings s = TeamCitySettings.Read(new(global), "repo", p => p + "_expanded");
        Check(!s.Active && !s.ShowBuildResultPage && !s.LogAsGuest && s.ProjectName == "repo_expanded" && !File.Exists(global.Path), "Defaults and expansion are pure and do not enable CI.");
        Check(TeamCitySettings.ServerUrlKey == "BuildServer.TeamCity.BuildServerUrl" && TeamCitySettings.ProjectNameKey == "BuildServer.TeamCity.ProjectName"
            && TeamCitySettings.BuildIdFilterKey == "BuildServer.TeamCity.BuildIdFilter" && TeamCitySettings.LogAsGuestKey == "BuildServer.TeamCity.LogAsGuest", "Exact original keys.");
        Check(new TeamCitySettings { ProjectName = "P||Q|P" }.ProjectIds.SequenceEqual(["P", "Q"]), "Pipe selection preserves IDs and avoids duplicate requests.");
    }
    private static async Task ScopeAsync()
    {
        using Files f = new(); f.Write("global", (TeamCitySettings.EnableKey, "true"), (TeamCitySettings.ProviderKey, "TeamCity"), (TeamCitySettings.ServerUrlKey, "http://ci.invalid/base"));
        f.Write("local", (TeamCitySettings.EnableKey, "future")); f.Write("distributed", (TeamCitySettings.ProjectNameKey, "P|Q"), ("future.option", "untouched"));
        GitDistributedSettingsSnapshot raw = new(await GitXmlSettingsStore.LoadAsync(f.Path("global")), await GitXmlSettingsStore.LoadAsync(f.Path("local")), await GitXmlSettingsStore.LoadAsync(f.Path("distributed")));
        TeamCitySettings settings = TeamCitySettings.Read(raw);
        Check(!settings.Enabled && settings.Provider == "TeamCity" && settings.ProjectIds.Count == 2 && raw.GetValue("future.option") == "untouched", "Raw scope precedes scalar parsing without rewriting unknown data.");
    }
    private static Task EndpointAsync()
    {
        Check(TeamCitySettings.ParseServerUri("ci.invalid:8111/tc").AbsoluteUri == "http://ci.invalid:8111/tc/", "Windows scheme-less endpoint defaults to HTTP.");
        Check(TeamCitySettings.ParseServerUri("https://ci.invalid:9443/my%20tc").AbsoluteUri == "https://ci.invalid:9443/my%20tc/", "Port and application base path remain explicit.");
        foreach (string value in new[] { "file:///tmp/server", "https://user:private-password@ci.invalid", "https://ci.invalid/?token=private-token", "https://ci.invalid/#fragment" })
        {
            try { _ = TeamCitySettings.ParseServerUri(value); throw new InvalidOperationException("Unsafe endpoint admitted."); }
            catch (ArgumentException e) { Check(!e.ToString().Contains("private-"), "No URL credentials appear in errors."); }
        }
        return Task.CompletedTask;
    }
    private static async Task CredentialsAsync()
    {
        foreach ((BuildServerCredentialsType mode, string prefix, string? scheme) in new[]
        { (BuildServerCredentialsType.Guest, "guestAuth/", (string?)null), (BuildServerCredentialsType.UsernameAndPassword, "httpAuth/", "Basic"), (BuildServerCredentialsType.BearerToken, "", "Bearer") })
        {
            BuildServerCredentials draft = new() { BuildServerCredentialsType = mode, Username = "name", Password = "secret-password", BearerToken = "secret-token" };
            using Handler h = new((r, _) =>
            {
                Check(r.RequestUri!.AbsoluteUri.StartsWith("http://ci.invalid:8111/tc/" + prefix + "app/rest/", StringComparison.Ordinal), "Each original auth route preserves HTTP/port/basepath.");
                Check(r.Headers.Authorization?.Scheme == scheme && r.Headers.Accept.Any(a => a.MediaType == "application/xml"), "Only explicit credentials and XML acceptance are sent.");
                if (scheme == "Basic") { Check(r.Headers.Authorization?.Parameter == Convert.ToBase64String(Encoding.UTF8.GetBytes("name:secret-password")), "Basic encoding matches Windows."); }
                if (scheme == "Bearer") { Check(r.Headers.Authorization?.Parameter == "secret-token", "Bearer uses its configured token."); }
                return Reply(Empty());
            });
            await using TeamCityApiClient client = new(Settings("http://ci.invalid:8111/tc"), draft, h);
            await client.GetProjectsAsync(); Check(h.Calls == 1, "No OS authentication fallback or construction request.");
        }
    }
    private static async Task DraftAsync()
    {
        BuildServerCredentials draft = new() { BuildServerCredentialsType = BuildServerCredentialsType.UsernameAndPassword, Username = "old", Password = "old-password", BearerToken = "inactive" };
        using Handler h = new((r, _) =>
        { Check(r.Headers.Authorization?.Parameter == Convert.ToBase64String(Encoding.UTF8.GetBytes("old:old-password")), "Caller mutation cannot retarget the owned immutable credentials."); return Reply(Empty()); });
        await using TeamCityApiClient client = new(Settings(), draft, h); draft.Username = "new"; draft.Password = "new"; draft.BuildServerCredentialsType = BuildServerCredentialsType.Guest;
        await client.GetProjectsAsync();
    }
    private static async Task ProjectsAsync()
    {
        using Handler h = new((r, _) => Reply(r.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? Xml("<projects count='2'><project id='_Root' name='Root'/><project id='P' name='Project' parentProjectId='_Root' archived='true'/></projects>")
            : Xml("<projects count='0' nextHref='/tc/guestAuth/app/rest/projects?page=2'/>")));
        await using TeamCityApiClient client = new(Settings(), handler: h); TeamCityPage<TeamCityProject> p = await client.GetProjectsAsync();
        Check(h.Calls == 2 && p.Items.Count == 2 && p.Items[1].Archived && p.Items[1].ParentProjectId == "_Root" && !p.Truncated, "Empty pages with nextHref are not falsely complete; UI receives archive/parent data.");
    }
    private static async Task BuildTypesAsync()
    {
        using Handler h = new((r, _) => Reply(r.RequestUri!.AbsolutePath.EndsWith("/projects/P", StringComparison.Ordinal)
            ? Xml("<project id='P'><buildTypes count='1' nextHref='/tc/guestAuth/app/rest/buildTypes?page=2'><buildType id='P_B' name='Build' projectId='P'/></buildTypes></project>")
            : Xml("<buildTypes count='1'><buildType id='P_C' name='Checks' projectId='P'/></buildTypes>")));
        await using TeamCityApiClient client = new(Settings(), handler: h); TeamCityPage<TeamCityBuildType> p = await client.GetProjectBuildTypesAsync("P");
        Check(p.Items.Count == 2 && p.Items[0].DisplayName == "Build (P_B)" && p.Items[0].ProjectId == "P" && !p.Truncated, "Chooser retains original display/result fields over pages.");
    }
    private static async Task BuildTypeAsync()
    {
        bool mismatch = false; using Handler h = new((_, _) => Reply(Xml($"<buildType id='{(mismatch ? "other" : "P_B")}' name='Build' projectId='P'/>")));
        await using TeamCityApiClient client = new(Settings(), handler: h); Check((await client.GetBuildTypeAsync("P_B")).ProjectId == "P", "Explicit lookup supports paste/chooser restoration.");
        mismatch = true; await Expect<TeamCityException>(() => client.GetBuildTypeAsync("P_B"));
    }
    private static async Task ReadAsync()
    {
        List<string> urls = []; using Handler h = new((r, _) => { urls.Add(Uri.UnescapeDataString(r.RequestUri!.AbsoluteUri)); return Reply(Standard(r)); });
        await using TeamCityApiClient client = new(Settings(), handler: h); TeamCityBuildSnapshot s = await client.ReadAsync();
        Check(s.Builds.Count == 2 && s.Commits.Count == 2 && s.Commits[Sha].Status == TeamCityBuildStatus.InProgress && s.Builds[0].Description == "Step 2/3", "All valid VCS revisions and live stage are retained; newest configuration status wins.");
        Check(urls.Count(u => u.Contains("/buildTypes/id:P_B/builds/", StringComparison.Ordinal)) == 2 && !urls.Any(u => u.Contains("id:X_B", StringComparison.Ordinal))
            && urls.Any(u => u.Contains("branch:(default:any)", StringComparison.Ordinal)), "Regex filters build configuration IDs, selects all branches, and deduplicates shared project build types.");
    }
    private static Task StatesAsync()
    {
        Check(Build("1", "b", "running").Status == TeamCityBuildStatus.InProgress && Build("1", "b", "queued").Status == TeamCityBuildStatus.InProgress, "SUCCESS while still running is not a completed success.");
        Check((Build("1", "b") with { Cancelled = true }).Status == TeamCityBuildStatus.Stopped && Build("1", "b", status: "FAILURE").Status == TeamCityBuildStatus.Failure, "Cancellation and failure remain distinct.");
        Check(Build("1", "b").Status == TeamCityBuildStatus.Success && Build("1", "b", status: "future").Status == TeamCityBuildStatus.Unknown && Build("1", "b", "future").Status == TeamCityBuildStatus.Unknown, "Unknown lifecycle/status is never success.");
        return Task.CompletedTask;
    }
    private static Task SnapshotAsync()
    {
        List<string> hashes = [Sha]; TeamCityBuild source = Build("10", "A") with { CommitHashes = hashes };
        TeamCityBuildSnapshot s = new([Build("9", "A", status: "FAILURE"), source, Build("11", "B", status: "FAILURE"), Build("12", "C", hashes: ["svn-1"])], DateTimeOffset.UnixEpoch);
        hashes.Clear(); Check(s.Commits[Sha].Builds.Count == 2 && s.Commits[Sha].Status == TeamCityBuildStatus.Failure && s.Builds.Count == 4, "Newest rerun A replaces old A without hiding sibling B failure; input mutation cannot alter publication.");
        Check(TeamCityBuildSnapshot.IsCommitHash(new string('a', 64)) && !TeamCityBuildSnapshot.IsCommitHash("123"), "Git SHA1/SHA256 only."); return Task.CompletedTask;
    }
    private static async Task DatesAsync()
    {
        using Handler h = new((r, _) => Reply(Standard(r))); await using TeamCityApiClient client = new(Settings(), handler: h);
        TeamCityBuild build = (await client.ReadAsync()).Builds.First(); Check(build.StartDate?.UtcDateTime == new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc), "Server offset is applied in the correct direction.");
        using Handler bad = new((r, _) => Reply(r.RequestUri!.AbsolutePath.Contains("/builds/id:", StringComparison.Ordinal)
            ? Xml($"<build id='10' buildTypeId='P_B'><startDate>private-payload-invalid</startDate><revisions><revision version='{Sha}'/></revisions></build>") : Standard(r)));
        await using TeamCityApiClient invalid = new(Settings(), handler: bad); Check(!(await Expect<TeamCityException>(() => invalid.ReadAsync())).ToString().Contains("private-payload"), "Invalid detail dates cannot publish a status or disclose XML.");
    }
    private static async Task ETagAsync()
    {
        using Handler h = new((r, _) =>
        {
            if (r.Headers.IfNoneMatch.Count > 0) { Check(r.Headers.IfNoneMatch.Single().Tag == "\"v1\"", "Opaque ETag is revalidated."); return Reply(new(HttpStatusCode.NotModified)); }
            HttpResponseMessage response = Xml("<projects count='1'><project id='P' name='Project'/></projects>"); response.Headers.ETag = new EntityTagHeaderValue("\"v1\""); return Reply(response);
        });
        await using TeamCityApiClient client = new(Settings(), handler: h); await client.GetProjectsAsync(); Check((await client.GetProjectsAsync()).Items.Count == 1 && h.Calls == 2, "304 retains the original parsed representation.");
        using Handler bare = new((_, _) => Reply(new(HttpStatusCode.NotModified))); await using TeamCityApiClient invalid = new(Settings(), handler: bare); await Expect<TeamCityException>(() => invalid.GetProjectsAsync());
    }
    private static async Task CacheAsync()
    {
        using Handler h = new((r, _) => Reply(Standard(r))); await using TeamCityApiClient client = new(Settings(), handler: h);
        TeamCityBuildSnapshot first = await client.ReadAsync(); int count = h.Calls;
        Check(ReferenceEquals(first, await client.ReadAsync()) && h.Calls == count, "Fresh snapshot is returned without new HTTP.");
        await client.ReadAsync(force: true); Check(h.Calls > count, "Explicit refresh bypasses time cache.");
    }
    private static async Task CancellationAsync()
    {
        using CancellationTokenSource cancel = new(); bool abort = false;
        using Handler h = new((r, token) =>
        {
            if (abort && Uri.UnescapeDataString(r.RequestUri!.Query).Contains("running:true", StringComparison.Ordinal)) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            return Reply(Standard(r));
        });
        await using TeamCityApiClient client = new(Settings(), handler: h); TeamCityBuildSnapshot first = await client.ReadAsync(); abort = true;
        await Expect<OperationCanceledException>(() => client.ReadAsync(force: true, cancel.Token));
        Check(ReferenceEquals(first, await client.ReadAsync()), "No aborted partial replacement is published.");
        using CancellationTokenSource already = new(); already.Cancel(); int calls = h.Calls;
        await Expect<OperationCanceledException>(() => client.GetProjectsAsync(already.Token)); Check(h.Calls == calls, "Pre-cancelled operation has no request.");
    }
    private static async Task DisposeAsync()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Handler h = new(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Empty(); });
        TeamCityApiClient client = new(Settings(), handler: h); Task request = client.GetProjectsAsync(); await entered.Task;
        await client.DisposeAsync(); await Expect<OperationCanceledException>(() => request);
        await Expect<ObjectDisposedException>(() => client.GetProjectsAsync()); Check(h.Calls == 1, "Close cancels/drains the original request and starts no replacement.");
    }
    private static async Task RedirectAsync()
    {
        int calls = 0; using Handler h = new((r, _) =>
        {
            Check(r.Headers.Authorization?.Parameter == "private-token", "Credentials stay at the explicitly configured origin.");
            return Reply(++calls == 1 ? Redirect("/my%20tc/app/rest/projects?moved=1") : Empty());
        });
        await using TeamCityApiClient client = new(Settings("https://ci.invalid/my%20tc/"), new BuildServerCredentials { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = "private-token" }, h);
        await client.GetProjectsAsync(); Check(calls == 2, "Safe base-path redirect works, including encoded application path.");
    }
    private static async Task UnsafeLinksAsync()
    {
        foreach (string url in new[] { "https://evil.invalid/tc/guestAuth/app/rest/projects", "http://ci.invalid/tc/guestAuth/app/rest/projects", "/other/guestAuth/app/rest/projects", "https://user:password@ci.invalid/tc/guestAuth/app/rest/projects", "/tc/guestAuth/app/rest/%2e%2e/%2e%2e/steal" })
        {
            foreach (bool pagination in new[] { false, true })
            {
                using Handler h = new((_, _) => Reply(pagination ? Xml(new XElement("projects", new XAttribute("count", 0), new XAttribute("nextHref", url)).ToString()) : Redirect(url)));
                await using TeamCityApiClient client = new(Settings(), handler: h); await Expect<TeamCityException>(() => client.GetProjectsAsync()); Check(h.Calls == 1, "No unsafe second request is sent.");
            }
        }
        using Handler cycle = new((r, _) => Reply(Xml(new XElement("projects", new XAttribute("count", 0), new XAttribute("nextHref", r.RequestUri!.AbsoluteUri)).ToString())));
        await using TeamCityApiClient loop = new(Settings(), handler: cycle); await Expect<TeamCityException>(() => loop.GetProjectsAsync()); Check(cycle.Calls == 1, "A repeated pagination URL is rejected.");
    }
    private static async Task BoundsAsync()
    {
        using Handler pages = new((_, _) => Reply(Xml("<projects count='1' nextHref='/tc/guestAuth/app/rest/projects?page=2'><project id='P' name='Project'/></projects>")));
        await using TeamCityApiClient bounded = new(Settings(), handler: pages) { MaxPages = 1 }; TeamCityPage<TeamCityProject> partial = await bounded.GetProjectsAsync();
        Check(partial.Truncated && partial.Diagnostics.Count > 0 && pages.Calls == 1, "Page bound is visible.");
        using Handler items = new((_, _) => Reply(Xml("<projects count='2'><project id='P' name='P'/><project id='Q' name='Q'/></projects>")));
        await using TeamCityApiClient itemBound = new(Settings(), handler: items) { MaxItems = 1 }; Check((await itemBound.GetProjectsAsync()).Truncated, "Item bound is visible.");
        using Handler details = new((r, _) => Reply(Standard(r))); await using TeamCityApiClient detailBound = new(Settings(), handler: details) { MaxBuildDetails = 1 };
        TeamCityBuildSnapshot snapshot = await detailBound.ReadAsync(); Check(snapshot.Truncated && snapshot.Builds.Count == 1 && snapshot.Diagnostics.Count > 0, "Detail bound never claims complete history.");
        using Handler requests = new((r, _) => Reply(Standard(r))); await using TeamCityApiClient requestBound = new(Settings(), handler: requests) { MaxRequests = 1 };
        await Expect<TeamCityException>(() => requestBound.ReadAsync()); Check(requests.Calls == 1, "Global HTTP budget stops before another request.");
    }
    private static async Task ErrorsAsync()
    {
        foreach (int code in new[] { 401, 403, 404, 429, 500 })
        {
            using Handler h = new((_, _) => Reply(new((HttpStatusCode)code) { Content = new StringContent("private-token private-password private-payload") }));
            await using TeamCityApiClient client = new(Settings(), new BuildServerCredentials { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = "private-token" }, h);
            TeamCityException error = await Expect<TeamCityException>(() => client.GetProjectsAsync());
            Check(error.StatusCode == code && !error.ToString().Contains("private-") && h.Calls == 1, "HTTP status remains visible, no secret/body or implicit authentication retry.");
        }
    }
    private static async Task InvalidXmlAsync()
    {
        foreach (string body in new[] { "<projects>private-payload", "<html>private-payload</html>", "<!DOCTYPE projects [<!ENTITY x SYSTEM 'file:///private-payload'>]><projects>&x;</projects>", "<projects count='2'><project id='P' name='P'/></projects>", "<projects><project name='No ID'/></projects>" })
        {
            using Handler h = new((_, _) => Reply(Xml(body))); await using TeamCityApiClient client = new(Settings(), handler: h);
            Check(!(await Expect<TeamCityException>(() => client.GetProjectsAsync())).ToString().Contains("private-payload"), "XML parse/admission errors omit body and entity text.");
        }
    }
    private static async Task SizeAsync()
    {
        foreach (bool unknownLength in new[] { false, true })
        {
            string body = "<projects count='0'><!--" + new string('x', 5000) + "--></projects>";
            using Handler h = new((_, _) => Reply(new(HttpStatusCode.OK) { Content = unknownLength ? new StreamContent(new NoSeekStream(Encoding.UTF8.GetBytes(body))) : new StringContent(body) }));
            await using TeamCityApiClient client = new(Settings(), handler: h) { MaxResponseBytes = 1024 }; await Expect<TeamCityException>(() => client.GetProjectsAsync());
        }
    }
    private static async Task TransportAsync()
    {
        foreach (bool timeout in new[] { false, true })
        {
            using Handler h = new((_, _) => timeout ? Task.FromException<HttpResponseMessage>(new OperationCanceledException("private-token")) : throw new HttpRequestException("private-token"));
            await using TeamCityApiClient client = new(Settings(), handler: h); Check(!(await Expect<TeamCityException>(() => client.GetProjectsAsync())).ToString().Contains("private-token"), "No underlying exception detail is retained.");
        }
    }
    private static Task PagesAsync()
    {
        using Handler h = new((_, _) => throw new InvalidOperationException("No HTTP")); using TeamCityApiClient client = new(Settings(), handler: h);
        Uri? page = client.BuildPageUri(Build("10", "P_B") with { WebUrl = "https://ci.invalid/tc/viewLog.html?buildId=10&token=private-payload#tab" });
        Check(page?.AbsoluteUri == "https://ci.invalid/tc/viewLog.html?buildId=10&guest=1" && h.Calls == 0, "Only canonical explicit build page is produced; guest is a web option, not credentials.");
        foreach (string url in new[] { "file:///tmp/a", "https://evil.invalid/tc/viewLog.html", "https://user:password@ci.invalid/tc/viewLog.html", "https://ci.invalid/other/viewLog.html" })
        { Check(client.BuildPageUri(Build("10", "P_B") with { WebUrl = url }) is null, "Unsafe page data cannot launch another origin/application."); }
        return Task.CompletedTask;
    }
    private static async Task DisabledAsync()
    {
        using Handler h = new((_, _) => throw new InvalidOperationException("Disabled CI has no HTTP"));
        await using TeamCityApiClient client = new(new() { ServerUrl = "http://ci.invalid/tc/" }, handler: h);
        Check((await client.ReadAsync()).Builds.Count == 0 && h.Calls == 0, "Polling is off by default.");
        bool refused = false;
        try { using TeamCityApiClient invalid = new(new() { ServerUrl = "http://ci.invalid", BuildIdFilter = "[" }, handler: new Handler((_, _) => throw new InvalidOperationException("No request"))); }
        catch (ArgumentException) { refused = true; }
        Check(refused && Settings().CreateBuildIdRegex().MatchTimeout == TimeSpan.FromMilliseconds(250), "Regex validation/admission is bounded before network.");
    }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { public int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) { Calls++; return send(r, t); } }
    private sealed class NoSeekStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-teamcity-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name + ".settings");
        public void Write(string name, params (string Key, string Value)[] values) => File.WriteAllText(Path(name), new XElement("dictionary", values.Select(v =>
            new XElement("item", new XElement("key", new XElement("string", v.Key)), new XElement("value", new XElement("string", v.Value))))).ToString());
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
