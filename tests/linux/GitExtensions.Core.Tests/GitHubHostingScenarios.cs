using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitUIPluginInterfaces.BuildServerIntegration;

internal static class GitHubHostingScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("GitHub hosting clones original credentials and keeps inactive drafts without secret ToString", CredentialsAsync),
        ("GitHub hosting original plugin OAuth Token codec preserves unknown fields and isolates CI", CodecAsync),
        ("GitHub hosting all credential modes are pinned to an explicit API origin", OriginAsync),
        ("GitHub hosting endpoints and HTTPS credentials are validated before any request", EndpointsAsync),
        ("GitHub hosting remote identities support HTTPS SSH scp and custom SSH ports without URL credentials", RemotesAsync),
        ("GitHub hosting repository details forks and branches retain original repository identities", RepositoriesAsync),
        ("GitHub hosting owner and current-user repositories use distinct explicit endpoints", OwnersAsync),
        ("GitHub pull requests paginate numeric pages and retain deleted heads and Windows fetch names", PaginationAsync),
        ("GitHub hosting pagination reports truncation and never publishes cancelled partial lists", BoundsAsync),
        ("GitHub pull request details and diff use the API media type instead of payload DiffUrl", DiffRequestAsync),
        ("GitHub pull request files preserve rename binary and literal patch metadata", FilesAsync),
        ("GitHub pull request discussion combines description commits and issue comments chronologically", DiscussionAsync),
        ("GitHub pull request discussion exposes the documented commit-list limit as incomplete", DiscussionLimitAsync),
        ("GitHub create pull request is a single explicit POST with exact branches and literal body", CreateAsync),
        ("GitHub pull request comments use explicit issue POST and close uses only explicit PATCH", CommentCloseAsync),
        ("GitHub fork creation requires a separate explicit authenticated POST", ForkAsync),
        ("GitHub Guest and invalid write drafts never send mutating requests", GuestAsync),
        ("GitHub hosting headers freeze Basic and Bearer credentials and Guest stays anonymous", HeadersAsync),
        ("GitHub hosting GET redirects cannot cross API origin downgrade or escape Enterprise API prefix", RedirectAsync),
        ("GitHub hosting writes are never replayed on redirects", WriteRedirectAsync),
        ("GitHub hosting errors retain HTTP status but never token body or transport details", ErrorsAsync),
        ("GitHub hosting malformed JSON timestamps and commit identities fail without payload disclosure", InvalidAsync),
        ("GitHub hosting bounds declared and streaming response bytes", SizeAsync),
        ("GitHub hosting cancellation reaches the handler and pre-cancelled or disposed clients do not request", CancellationAsync),
        ("GitHub pull request template keeps literal UTF8 and cancellation within the selected repository", TemplateAsync),
        ("GitHub PR diff projection preserves exact patches and matches API paths independently of ordering", ProjectionAsync),
        ("GitHub PR diff projection handles C quoted UTF8 control paths rename deletion and binary headers", QuotedProjectionAsync),
        ("GitHub PR diff projection refuses missing duplicate and ambiguous metadata instead of guessing", AmbiguousProjectionAsync),
    ];
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Secret = "fake-private-token";
    private static readonly GitHubRepositoryId Repo = new("octo", "repo");
    private static readonly GitHubHostingSettings Default = new();
    private static BuildServerCredentials Token() => new() { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = Secret };
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static T Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Repository(string owner = "octo", string name = "repo", bool fork = false, object? parent = null) => new
    { id = 42, owner = new { login = owner }, name, description = "literal <repository>", @private = true, fork, default_branch = "main", clone_url = "https://github.com/" + owner + "/" + name + ".git", ssh_url = "git@github.com:" + owner + "/" + name + ".git", html_url = "https://github.com/" + owner + "/" + name, parent };
    private static object Pull(int number = 7, string state = "open", bool deleted = false, int? commits = null, string url = "https://github.com/octo/repo/pull/7", bool merged = false) => new
    { number, title = "Fix λ", body = "literal <body>\nsecond", user = new { login = "contributor" }, created_at = "2020-01-01T00:00:00Z", state, html_url = url, diff_url = "https://evil.invalid/secret", merged, commits,
        @base = new { @ref = "main", sha = Sha, repo = Repository() }, head = new { @ref = "feature/λ", sha = new string('a', 64), repo = deleted ? null : Repository("contributor", "fork", true) } };
    private static object Comment(string body = "comment", string date = "2020-01-03T00:00:00Z") => new
    { user = new { login = "reviewer" }, body, created_at = date, html_url = "https://github.com/octo/repo/pull/7#issuecomment-1" };
    private static object Commit(string message = "commit <literal>", string date = "2020-01-02T00:00:00Z") => new
    { sha = Sha, html_url = "https://github.com/octo/repo/commit/" + Sha, commit = new { message, author = new { name = "Author <name>", date } } };

    private static Task CredentialsAsync()
    {
        BuildServerCredentials original = Token(); original.Username = "kept-user"; original.Password = "kept-password";
        GitHubHostingCredentials frozen = GitHubHostingCredentials.From(original); original.BearerToken = "changed";
        BuildServerCredentials draft = frozen.ToDraft(); draft.Password = "changed-too";
        Check(frozen.BearerToken == Secret && frozen.Password == "kept-password" && frozen.ToDraft().Password == "kept-password", "Neither input nor result mutation affects the immutable snapshot.");
        Check(!frozen.ToString().Contains(Secret, StringComparison.Ordinal) && !frozen.ToString().Contains("kept-password", StringComparison.Ordinal), "Secrets never appear in ToString.");
        Check(GitHubHostingCredentials.CloneDraft(null).BuildServerCredentialsType == BuildServerCredentialsType.Guest, "Missing credentials remain Guest.");
        return Task.CompletedTask;
    }
    private static Task CodecAsync()
    {
        Dictionary<string, string?> values = new() { [GitHubHostingSettings.TokenKey] = Secret, ["future"] = "literal λ", [GitHubActionsSettings.TokenKey] = "different-CI-token" };
        GitHubHostingSettings settings = GitHubHostingSettings.FromPluginSettings(values);
        Check(settings.CredentialsFromPluginSettings(values).BearerToken == Secret, "Original OAuth Token is used, never an Actions token.");
        IReadOnlyDictionary<string, string?> changed = settings.WithPersonalAccessToken(values, "new-token");
        Check(values[GitHubHostingSettings.TokenKey] == Secret && changed["future"] == "literal λ" && changed[GitHubActionsSettings.TokenKey] == "different-CI-token", "The original input and unknown/CI values are preserved.");
        Check(changed[GitHubHostingSettings.CredentialsOriginKey] == "https://api.github.com" && changed[GitHubHostingSettings.TokenKey] == "new-token", "An explicit new PAT is anchored.");
        Check(new GitHubHostingSettings().CredentialsFromPluginSettings(new Dictionary<string, string?> { [GitHubActionsSettings.TokenKey] = Secret }).Type == BuildServerCredentialsType.Guest, "A CI-only configuration cannot enable hosting authentication.");
        return Task.CompletedTask;
    }
    private static Task OriginAsync()
    {
        GitHubHostingSettings enterprise = new("https://git.example.invalid", "https://git.example.invalid/api/v3/");
        foreach (BuildServerCredentialsType mode in Enum.GetValues<BuildServerCredentialsType>())
        {
            BuildServerCredentials draft = new() { BuildServerCredentialsType = mode, Username = "user", Password = "password", BearerToken = Secret };
            IReadOnlyDictionary<string, string?> saved = enterprise.WithCredentials(new Dictionary<string, string?> { ["future"] = "retained" }, draft);
            GitHubHostingCredentials read = enterprise.CredentialsFromPluginSettings(saved);
            Check(read.Type == mode && read.Username == "user" && read.Password == "password" && read.BearerToken == Secret, "All modes retain inactive fields, anchored together.");
            if (mode != BuildServerCredentialsType.Guest) { Expect<ArgumentException>(() => new GitHubHostingSettings("https://other.invalid", "https://other.invalid/api/v3/").CredentialsFromPluginSettings(saved)); }
        }
        Expect<ArgumentException>(() => enterprise.CredentialsFromPluginSettings(new Dictionary<string, string?> { [GitHubHostingSettings.TokenKey] = Secret }));
        Expect<ArgumentException>(() => GitHubHostingSettings.FromPluginSettings(new Dictionary<string, string?> { [GitHubHostingSettings.HostnameKey] = "git.example.invalid" }));
        return Task.CompletedTask;
    }
    private static Task EndpointsAsync()
    {
        foreach (string endpoint in new[] { "https://user:password@github.com", "https://github.com?token=private", "https://github.com#fragment", "file:///tmp/token" })
        { Expect<ArgumentException>(() => new GitHubHostingSettings(ApiUrl: endpoint).Validate()); }
        Expect<ArgumentException>(() => new GitHubHostingClient(new(ApiUrl: "http://api.example.invalid"), Token()));
        Expect<ArgumentException>(() => new GitHubHostingClient(Default, new BuildServerCredentials { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = "bad\r\nheader" }));
        Expect<ArgumentException>(() => new GitHubHostingClient(Default, new BuildServerCredentials { BuildServerCredentialsType = BuildServerCredentialsType.UsernameAndPassword, Username = "user:injected", Password = "password" }));
        Check(Default.SafeWebUri("https://evil.invalid/") is null && Default.SafeWebUri("https://token@github.com/octo/repo") is null, "Browser actions cannot expose credentials or switch origins.");
        Check(Default.PullRequestUri(Repo, 7).AbsoluteUri == "https://github.com/octo/repo/pull/7", "A safe explicit browser URL can be constructed.");
        return Task.CompletedTask;
    }
    private static Task RemotesAsync()
    {
        foreach (string url in new[] { "https://github.com/octo/repo.git", "git@github.com:octo/repo.git", "ssh://git@github.com:2222/octo/repo.git" })
        { Check(Default.TryParseRemote(url, out GitHubRepositoryId? repo) && repo == Repo, "Windows hosting identity works for each Git transport."); }
        foreach (string url in new[] { "https://token@github.com/octo/repo", "https://evil.invalid/octo/repo", "https://github.com/octo/%2Frepo", "git@github.com:octo/repo/extra", "https://github.com/octo/repo?secret=value" })
        { Check(!Default.TryParseRemote(url, out _), "Unsafe or unrelated remote rejected without copying its URL."); }
        GitHubHostingSettings ghe = new("https://ghe.invalid:8443/web/", "https://api.ghe.invalid/api/v3/");
        Check(ghe.TryParseRemote("https://ghe.invalid:8443/web/octo/repo.git", out GitHubRepositoryId? identity) && identity == Repo, "An explicit Enterprise web base path is respected.");
        Check(ghe.TryParseRemote("ssh://git@ghe.invalid:2200/octo/repo.git", out identity) && identity == Repo, "The SSH port is independent of its HTTPS port.");
        return Task.CompletedTask;
    }
    private static async Task RepositoriesAsync()
    {
        using Handler handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/branches", StringComparison.Ordinal)
            ? Json(new[] { new { name = "feature/λ", commit = new { sha = Sha } } }) : request.RequestUri.AbsolutePath.EndsWith("/forks", StringComparison.Ordinal)
            ? Json(new[] { Repository("contributor", "fork", true, Repository()) }) : Json(Repository("contributor", "fork", true, Repository()))));
        using GitHubHostingClient client = new(Default, handler: handler);
        GitHubHostedRepository repo = await client.GetRepositoryAsync(Repo); Check(repo.Parent == Repo && repo.DefaultBranch == "main" && repo.Fork && repo.Private, "Default branch, parent and fork/private flags survive.");
        Check((await client.GetForksAsync(Repo)).Items.Single().Owner == "contributor", "Fork repositories are actually fetched.");
        GitHubHostingPage<GitHubHostedBranch> branches = await client.GetBranchesAsync(Repo); Check(branches.Items.Single().Name == "feature/λ" && branches.Items.Single().Sha == Sha, "Literal branch names and SHA retained.");
        Check(((ICollection<GitHubHostedBranch>)branches.Items).IsReadOnly && handler.Records.All(r => r.Method == "GET"), "Lists are immutable and all reads are GET.");
    }
    private static async Task OwnersAsync()
    {
        using Handler handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal) ? Json(new { login = "octo" }) : Json(new[] { Repository() })));
        using GitHubHostingClient client = new(Default, Token(), handler); Check(handler.Calls == 0, "Construction makes no request.");
        Check(await client.GetCurrentUserAsync() == "octo", "Login is obtained only by an explicit call."); await client.GetRepositoriesAsync("octo"); await client.GetRepositoriesAsync();
        Check(handler.Records.Select(r => r.Path).SequenceEqual(new[] { "/user", "/users/octo/repos", "/user/repos" }), "Owner and current-user APIs stay separate.");
    }
    private static async Task PaginationAsync()
    {
        using Handler handler = new((request, _) =>
        {
            bool first = request.RequestUri!.Query.Contains("&page=1", StringComparison.Ordinal); HttpResponseMessage response = first ? Json(Enumerable.Range(1, 100).Select(n => Pull(n)).ToArray()) : Json(new[] { Pull(101, deleted: true) });
            if (first) { response.Headers.Add("Link", "<https://evil.invalid/steal>; rel=\"next\""); }
            return Task.FromResult(response);
        });
        using GitHubHostingClient client = new(Default, handler: handler); GitHubHostingPage<GitHubPullRequest> page = await client.GetPullRequestsAsync(Repo, "all");
        Check(page.Items.Count == 101 && !page.Truncated && handler.Calls == 2 && page.Items[^1].Head.Repository is null, "Both actual numeric pages and deleted repository heads are supported.");
        Check(page.Items[0].FetchBranch == "pr/n1_feature/λ" && page.Items[0].Head.Sha.Length == 64, "Original fetch branch naming and commit identity are retained.");
        Check(handler.Records.All(r => r.Host == "api.github.com" && r.Query.Contains("state=all&per_page=100&page=", StringComparison.Ordinal)), "Link cannot redirect authentication or change query state.");
        await ExpectAsync<ArgumentException>(() => client.GetPullRequestsAsync(Repo, "bad")); Check(handler.Calls == 2, "Invalid state sends nothing.");
    }
    private static async Task BoundsAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Json(Enumerable.Range(1, 100).Select(n => Pull(n)).ToArray())));
        using GitHubHostingClient client = new(Default, handler: handler) { MaxPages = 1 }; Check((await client.GetPullRequestsAsync(Repo)).Truncated && handler.Calls == 1, "The cap is never presented as a complete list.");
        using CancellationTokenSource cancel = new(); using Handler cancelled = new((request, token) =>
        {
            if (request.RequestUri!.Query.Contains("&page=1", StringComparison.Ordinal)) { return Task.FromResult(Json(Enumerable.Range(1, 100).Select(n => Pull(n)).ToArray())); }
            cancel.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException("Unreachable");
        });
        using GitHubHostingClient partial = new(Default, handler: cancelled); await ExpectAsync<OperationCanceledException>(() => partial.GetPullRequestsAsync(Repo, cancellationToken: cancel.Token));
        Check(cancelled.Calls == 2, "Cancellation of a subsequent page returns no partial page.");
    }
    private static async Task DiffRequestAsync()
    {
        const string diff = "diff --git a/file b/file\n--- a/file\n+++ b/file\n@@ -1 +1 @@\n-old\n+λ\n\\ No newline at end of file\n";
        using Handler handler = new((request, _) => Task.FromResult(request.Headers.Accept.Any(v => v.MediaType == "application/vnd.github.diff") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(diff, Encoding.UTF8) } : Json(Pull(url: "https://evil.invalid/fake"))));
        using GitHubHostingClient client = new(Default, Token(), handler); Check((await client.GetPullRequestAsync(Repo, 7)).WebUrl == "", "Unsafe payload URLs are not eligible browser actions.");
        Check(await client.GetPullRequestDiffAsync(Repo, 7) == diff && handler.Records.All(r => r.Path == "/repos/octo/repo/pulls/7"), "Diff is byte-text exact from configured API, never the hostile DiffUrl.");
    }
    private static async Task FilesAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Json(new object[]
        { new { filename = "new λ\nfile", previous_filename = "old \"file", status = "renamed", additions = 2, deletions = 1, changes = 3, patch = "@@ -1 +1 @@\n-old\n+new" },
          new { filename = "binary.png", previous_filename = (string?)null, status = "added", additions = 0, deletions = 0, changes = 0, patch = (string?)null } })));
        using GitHubHostingClient client = new(Default, handler: handler); var page = await client.GetPullRequestFilesAsync(Repo, 7);
        Check(page.Items[0].Filename == "new λ\nfile" && page.Items[0].PreviousFilename == "old \"file" && page.Items[0].Changes == 3 && page.Items[1].Patch is null, "Renames and missing binary patches remain distinct literal metadata.");
    }
    private static async Task DiscussionAsync()
    {
        using Handler handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/commits", StringComparison.Ordinal) ? Json(new[] { Commit() }) : request.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal) ? Json(new[] { Comment() }) : Json(Pull(commits: 1))));
        using GitHubHostingClient client = new(Default, handler: handler); var discussion = await client.GetDiscussionAsync(Repo, 7);
        Check(discussion.Items.Select(v => v.Kind).SequenceEqual(new[] { GitHubDiscussionKind.Description, GitHubDiscussionKind.Commit, GitHubDiscussionKind.Comment }) && !discussion.Truncated, "Description, commits and issue comments keep chronological Windows semantics.");
        Check(discussion.Items[1].Author == "Author <name>" && discussion.Items[1].Body == "commit <literal>" && discussion.Items[1].Sha == Sha, "Discussion content is literal data, not HTML escaping or execution.");
        Check(handler.Records.Any(r => r.Path == "/repos/octo/repo/issues/7/comments"), "Issue comments endpoint used for PR discussion.");
    }
    private static async Task DiscussionLimitAsync()
    {
        using Handler handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/commits", StringComparison.Ordinal) ? Json(new[] { Commit() }) : request.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal) ? Json(Array.Empty<object>()) : Json(Pull(commits: 251))));
        using GitHubHostingClient client = new(Default, handler: handler); Check((await client.GetDiscussionAsync(Repo, 7)).Truncated, "A server-limited commit list is explicitly incomplete.");
    }
    private static async Task CreateAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Json(Pull()))); using GitHubHostingClient client = new(Default, Token(), handler);
        const string body = "  literal λ\n\"quoted\" & <tag>  "; await client.CreatePullRequestAsync(Repo, new("contributor:feature/λ", "main", "  Title  ", body, true, false));
        Request record = handler.Records.Single(); using JsonDocument payload = JsonDocument.Parse(record.Body!);
        Check(record.Method == "POST" && record.Path == "/repos/octo/repo/pulls" && payload.RootElement.GetProperty("head").GetString() == "contributor:feature/λ"
            && payload.RootElement.GetProperty("base").GetString() == "main" && payload.RootElement.GetProperty("title").GetString() == "Title"
            && payload.RootElement.GetProperty("body").GetString() == body && payload.RootElement.GetProperty("draft").GetBoolean() && !payload.RootElement.GetProperty("maintainer_can_modify").GetBoolean(), "Only the explicit action sends a single exact JSON payload; body is not altered.");
    }
    private static async Task CommentCloseAsync()
    {
        using Handler handler = new((request, _) => Task.FromResult(request.Method == HttpMethod.Patch ? Json(Pull(state: "closed")) : Json(Comment("literal\ncomment")))); using GitHubHostingClient client = new(Default, Token(), handler);
        await client.PostCommentAsync(Repo, 7, "literal\ncomment"); GitHubPullRequest closed = await client.ClosePullRequestAsync(Repo, 7);
        Check(!closed.Merged && !closed.IsOpen && handler.Records[0].Method == "POST" && handler.Records[0].Path == "/repos/octo/repo/issues/7/comments", "A comment uses issue POST and close parses the returned state without merging.");
        using JsonDocument payload = JsonDocument.Parse(handler.Records[1].Body!); Check(handler.Records[1].Method == "PATCH" && payload.RootElement.EnumerateObject().Count() == 1 && payload.RootElement.GetProperty("state").GetString() == "closed", "Close changes only state, never merges or adds content.");
    }
    private static async Task ForkAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Json(Repository("org", "fork", true, Repository())))); using GitHubHostingClient client = new(Default, Token(), handler);
        Check(handler.Calls == 0, "No fork is implicit."); Check((await client.ForkRepositoryAsync(Repo, "org")).Fork && handler.Records.Single().Method == "POST" && handler.Records.Single().Path == "/repos/octo/repo/forks", "Only an explicit fork method mutates fork state.");
    }
    private static async Task GuestAsync()
    {
        using Handler handler = new((_, _) => throw new InvalidOperationException("No HTTP expected")); using GitHubHostingClient guest = new(Default, handler: handler);
        await ExpectAsync<GitHubHostingException>(() => guest.CreatePullRequestAsync(Repo, new("head", "main", "Title", "")));
        await ExpectAsync<GitHubHostingException>(() => guest.PostCommentAsync(Repo, 7, "comment")); await ExpectAsync<GitHubHostingException>(() => guest.ClosePullRequestAsync(Repo, 7)); await ExpectAsync<GitHubHostingException>(() => guest.ForkRepositoryAsync(Repo));
        using GitHubHostingClient authenticated = new(Default, Token(), new Handler((_, _) => throw new InvalidOperationException("No HTTP expected")));
        await ExpectAsync<ArgumentException>(() => authenticated.CreatePullRequestAsync(Repo, new("bad\nref", "main", "Title", "")));
        await ExpectAsync<ArgumentException>(() => authenticated.CreatePullRequestAsync(Repo, new("head", "main", " ", ""))); await ExpectAsync<ArgumentException>(() => authenticated.PostCommentAsync(Repo, 7, " "));
        Check(handler.Calls == 0, "Guest cannot send writes.");
    }
    private static async Task HeadersAsync()
    {
        foreach (BuildServerCredentialsType mode in Enum.GetValues<BuildServerCredentialsType>())
        {
            BuildServerCredentials draft = new() { BuildServerCredentialsType = mode, Username = "user", Password = "password", BearerToken = Secret };
            using Handler handler = new((request, _) =>
            {
                Check(request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2022-11-28" && request.Headers.Accept.Single().MediaType == "application/vnd.github+json" && request.Headers.UserAgent.Count > 0, "Documented API headers are explicit.");
                Check(mode switch { BuildServerCredentialsType.Guest => request.Headers.Authorization is null, BuildServerCredentialsType.BearerToken => request.Headers.Authorization?.ToString() == "Bearer " + Secret, _ => request.Headers.Authorization?.ToString() == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:password")) }, "Original credential type controls only its own header.");
                return Task.FromResult(Json(Repository()));
            });
            using GitHubHostingClient client = new(Default, draft, handler); draft.BearerToken = "mutated"; draft.Password = "mutated"; await client.GetRepositoryAsync(Repo);
        }
    }
    private static async Task RedirectAsync()
    {
        GitHubHostingSettings ghe = new("https://ghe.invalid", "https://ghe.invalid/api/v3/");
        foreach (string destination in new[] { "https://evil.invalid/api/v3/repos/octo/repo", "http://ghe.invalid/api/v3/repos/octo/repo", "https://ghe.invalid/login", "https://token@ghe.invalid/api/v3/elsewhere" })
        {
            using Handler handler = new((_, _) => { HttpResponseMessage r = new(HttpStatusCode.TemporaryRedirect); r.Headers.Location = new(destination); return Task.FromResult(r); });
            using GitHubHostingClient client = new(ghe, Token(), handler); await ExpectAsync<GitHubHostingException>(() => client.GetRepositoryAsync(Repo)); Check(handler.Calls == 1, "Credentials are not forwarded beyond the API origin/prefix.");
        }
        int calls = 0; using Handler valid = new((_, _) => { if (++calls > 1) { return Task.FromResult(Json(Repository())); } HttpResponseMessage r = new(HttpStatusCode.MovedPermanently); r.Headers.Location = new("/api/v3/repos/octo/renamed", UriKind.Relative); return Task.FromResult(r); });
        using GitHubHostingClient safe = new(ghe, Token(), valid); await safe.GetRepositoryAsync(Repo); Check(valid.Calls == 2 && valid.Records[1].Path == "/api/v3/repos/octo/renamed", "A same-origin API GET redirect is followed explicitly.");
    }
    private static async Task WriteRedirectAsync()
    {
        using Handler handler = new((_, _) => { HttpResponseMessage r = new(HttpStatusCode.TemporaryRedirect); r.Headers.Location = new("https://api.github.com/repos/octo/new/pulls"); return Task.FromResult(r); });
        using GitHubHostingClient client = new(Default, Token(), handler); GitHubHostingException error = await ExpectAsync<GitHubHostingException>(() => client.CreatePullRequestAsync(Repo, new("head", "main", "Title", "body")));
        Check(handler.Calls == 1 && error.Message.Contains("not replayed", StringComparison.Ordinal), "Even a same-origin redirect never repeats POST.");
    }
    private static async Task ErrorsAsync()
    {
        foreach (int status in new[] { 401, 403, 404, 409, 422, 429, 500 })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret + " confidential server payload") }));
            using GitHubHostingClient client = new(Default, Token(), handler); GitHubHostingException error = await ExpectAsync<GitHubHostingException>(() => client.GetRepositoryAsync(Repo));
            Check(error.StatusCode == status && !error.ToString().Contains(Secret, StringComparison.Ordinal) && !error.ToString().Contains("confidential", StringComparison.Ordinal), "Status remains useful without response payload or token.");
        }
        using Handler broken = new((_, _) => throw new HttpRequestException(Secret)); using GitHubHostingClient transport = new(Default, Token(), broken);
        Check(!(await ExpectAsync<GitHubHostingException>(() => transport.GetRepositoryAsync(Repo))).ToString().Contains(Secret, StringComparison.Ordinal), "Transport details and inner exceptions are not exposed.");
    }
    private static async Task InvalidAsync()
    {
        foreach (string body in new[] { "{ " + Secret, "[]", "{\"number\":7,\"title\":\"" + Secret + "\"}", JsonSerializer.Serialize(Pull()).Replace(Sha, "invalid", StringComparison.Ordinal), JsonSerializer.Serialize(Pull()).Replace("2020-01-01T00:00:00Z", "not-a-date", StringComparison.Ordinal) })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })); using GitHubHostingClient client = new(Default, Token(), handler);
            Check(!(await ExpectAsync<GitHubHostingException>(() => client.GetPullRequestAsync(Repo, 7))).ToString().Contains(Secret, StringComparison.Ordinal), "Malformed payload does not escape the fixed error boundary.");
        }
    }
    private static async Task SizeAsync()
    {
        foreach (bool unknown in new[] { false, true })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = unknown ? new UnknownLengthContent(new byte[2048]) : new ByteArrayContent(new byte[2048]) }));
            using GitHubHostingClient client = new(Default, handler: handler) { MaxResponseBytes = 1024 }; Check((await ExpectAsync<GitHubHostingException>(() => client.GetRepositoryAsync(Repo))).Message.Contains("size", StringComparison.Ordinal), "Both Content-Length and actual streamed byte count are bounded.");
        }
    }
    private static async Task CancellationAsync()
    {
        using CancellationTokenSource cancel = new(); TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously); bool finished = false;
        using Handler handler = new(async (_, token) => { started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Repository()); } finally { finished = true; } });
        using GitHubHostingClient client = new(Default, handler: handler); Task read = client.GetRepositoryAsync(Repo, cancel.Token); await started.Task; cancel.Cancel(); await ExpectAsync<OperationCanceledException>(() => read); Check(finished, "The real handler drained cancellation.");
        using CancellationTokenSource before = new(); before.Cancel(); int count = handler.Calls; await ExpectAsync<OperationCanceledException>(() => client.GetRepositoryAsync(Repo, before.Token)); Check(handler.Calls == count, "Pre-cancellation sends nothing.");
        client.Dispose(); await ExpectAsync<ObjectDisposedException>(() => client.GetRepositoryAsync(Repo)); Check(handler.Calls == count, "Disposed client sends nothing.");
    }
    private static async Task TemplateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "github-template-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string outside = root + "-outside"; Directory.CreateDirectory(outside);
        try
        {
            Check(await GitHubHostingTemplates.ReadAsync(root) == "", "Missing template is empty."); Directory.CreateDirectory(Path.Combine(root, ".github"));
            string path = Path.Combine(root, ".github/PULL_REQUEST_TEMPLATE.md"), literal = "  ## λ\n<script>literal</script>\n"; await File.WriteAllTextAsync(path, literal); Check(await GitHubHostingTemplates.ReadAsync(root) == literal, "Template content is literal.");
            using CancellationTokenSource cancel = new(); cancel.Cancel(); await ExpectAsync<OperationCanceledException>(() => GitHubHostingTemplates.ReadAsync(root, cancel.Token));
            if (!OperatingSystem.IsWindows())
            {
                string secret = Path.Combine(outside, "not-a-template"); await File.WriteAllTextAsync(secret, "private"); File.Delete(path); File.CreateSymbolicLink(path, secret);
                await ExpectAsync<IOException>(() => GitHubHostingTemplates.ReadAsync(root));
                File.Delete(path); Directory.Delete(Path.Combine(root, ".github")); Directory.CreateSymbolicLink(Path.Combine(root, ".github"), outside);
                await ExpectAsync<IOException>(() => GitHubHostingTemplates.ReadAsync(root));
            }
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }
    private static GitHubPullRequestFile FileInfo(string name, string? previous = null, string status = "modified") => new(name, previous, status, 1, 1, 2, null);
    private static async Task ProjectionAsync()
    {
        const string a = "diff --git a/a.txt b/a.txt\r\n--- a/a.txt\r\n+++ b/a.txt\r\n@@ -1 +1 @@\r\n-old\r\n+new\r\n";
        const string b = "diff --git a/b.txt b/b.txt\n--- a/b.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-b\n+B\n\\ No newline at end of file\n";
        var files = GitHubPullRequestDiff.Project(a + b, new[] { FileInfo("b.txt"), FileInfo("a.txt") });
        Check(files[0].Diff == b && files[1].Diff == a && files[0].File.Filename == "b.txt", "API order is independent of exact header association and all line endings/markers survive.");
        await Task.CompletedTask;
    }
    private static Task QuotedProjectionAsync()
    {
        const string quoted = "diff --git \"a/old \\\"name\" \"b/\\303\\251\\nfile\"\nrename from \"old \\\"name\"\nrename to \"\\303\\251\\nfile\"\n";
        const string binary = "diff --git a/image.png b/image.png\nnew file mode 100644\nBinary files /dev/null and b/image.png differ\n";
        const string deleted = "diff --git a/gone b/gone\ndeleted file mode 100644\n--- a/gone\n+++ /dev/null\n@@ -1 +0,0 @@\n-old\n";
        var projected = GitHubPullRequestDiff.Project(quoted + binary + deleted, new[] { FileInfo("é\nfile", "old \"name", "renamed"), FileInfo("image.png", status: "added"), FileInfo("gone", status: "removed") });
        Check(projected[0].Diff == quoted && projected[1].IsBinary && projected[1].Diff == binary && projected[2].Diff == deleted, "C-octal UTF8, escaped quote/newline, rename-only, binary and delete patches map exactly.");
        return Task.CompletedTask;
    }
    private static Task AmbiguousProjectionAsync()
    {
        Expect<GitHubHostingException>(() => GitHubPullRequestDiff.Project("diff --git a/file b/file\n", new[] { FileInfo("other") }));
        Expect<GitHubHostingException>(() => GitHubPullRequestDiff.Project("", new[] { FileInfo("file") }));
        Expect<GitHubHostingException>(() => GitHubPullRequestDiff.Project("diff --git a/file b/file\n", new[] { FileInfo("file"), FileInfo("file") }));
        Expect<GitHubHostingException>(() => GitHubPullRequestDiff.Project("diff --git a/A b/B b/C\n", new[] { FileInfo("B b/C", "A", "renamed"), FileInfo("C", "A b/B", "renamed") }));
        return Task.CompletedTask;
    }
    private sealed record Request(string Method, string Host, string Path, string Query, string? Body);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Request> _records = new();
        internal IReadOnlyList<Request> Records => _records.ToArray();
        internal int Calls => _records.Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            _records.Enqueue(new(request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return await send(request, token);
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
