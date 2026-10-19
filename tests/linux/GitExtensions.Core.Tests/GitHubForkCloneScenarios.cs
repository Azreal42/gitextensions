using System.Net;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitUIPluginInterfaces.BuildServerIntegration;

internal static class GitHubForkCloneScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("GitHub ForkClone public search encodes the whole literal query and stays anonymous", SearchWireAsync),
        ("GitHub ForkClone search reads the real envelope and all numeric pages atomically", SearchPagesAsync),
        ("GitHub ForkClone search exposes API incomplete and local page caps", SearchIncompleteAsync),
        ("GitHub ForkClone search never requests beyond the documented thousand results", SearchThousandAsync),
        ("GitHub ForkClone malformed search envelopes fail without payload disclosure", SearchMalformedAsync),
        ("GitHub ForkClone cancelled second page never publishes partial repository results", SearchCancellationAsync),
        ("GitHub ForkClone metadata retains nullable permissions fork counts and explicit external homepage", MetadataAsync),
        ("GitHub ForkClone unsafe homepage and invalid metadata do not become usable URLs", MetadataInvalidAsync),
        ("GitHub ForkClone interface reuses distinct owner and authenticated repository routes", RepositoryRoutesAsync),
        ("GitHub ForkClone accepted personal fork is a single authenticated POST with parent identity", ForkAsync),
        ("GitHub ForkClone Guest cannot emit a fork and a write redirect is never replayed", ForkAdmissionAsync),
        ("GitHub ForkClone HTTP failures retain status but not response bodies or credentials", ErrorsAsync),
        ("GitHub shared PR transport deadline includes response headers", HeaderDeadlineAsync),
        ("GitHub shared PR diff transport deadline includes the body after headers", BodyDeadlineAsync),
        ("GitHub shared PR body cancellation drains the stream before returning", BodyCancellationAsync),
        ("GitHub ForkClone search redirect cannot escape an Enterprise API prefix", SearchRedirectAsync),
        ("GitHub hosted shallow clone leaves native single branch behavior while old options retain all branches", ShallowCompatibilityAsync),
        ("GitHub ForkClone search results own immutable lists and expose truncation", ImmutableAsync),
    ];
    private const string Secret = "synthetic-fork-token";
    private static readonly GitHubRepositoryId Id = new("owner", "repo");
    private static BuildServerCredentials Auth() => new() { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = Secret };
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static Dictionary<string, object?> Repository(int id = 1, string owner = "owner", string name = "repo") => new()
    {
        ["id"] = id, ["owner"] = new { login = owner }, ["name"] = name, ["description"] = "Literal <description> & 雪",
        ["private"] = false, ["fork"] = false, ["default_branch"] = "main", ["clone_url"] = $"https://github.com/{owner}/{name}.git",
        ["ssh_url"] = $"git@github.com:{owner}/{name}.git", ["html_url"] = $"https://github.com/{owner}/{name}", ["forks_count"] = 7
    };
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Envelope(IEnumerable<object> items, long total = 1, bool incomplete = false)
        => new { items = items.ToArray(), total_count = total, incomplete_results = incomplete };
    private sealed record Request(string Method, string Path, string Query, string? Authorization, string? Body);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return await send(request, token);
        }
    }
    private static async Task SearchWireAsync()
    {
        const string query = "  name:雪 fork:true language:C# & literal+term  ";
        using Handler handler = new((_, _) => Task.FromResult(Json(Envelope([Repository()]))));
        using GitHubHostingClient concrete = new(new(), handler: handler); IGitHubForkCloneClient client = concrete;
        GitHubRepositorySearchResult result = await client.SearchRepositoriesAsync(query);
        Request request = handler.Requests.Single();
        Check(request.Method == "GET" && request.Path == "/search/repositories" && request.Query == "?q=" + Uri.EscapeDataString(query) + "&per_page=100&page=1", "Search qualifiers remain one literal encoded query.");
        Check(request.Authorization is null && result.Items.Single().Identity == Id && !result.Truncated, "Guest is anonymous and a complete search is explicit.");
        await Expect<ArgumentException>(() => client.SearchRepositoriesAsync("  ")); await Expect<ArgumentException>(() => client.SearchRepositoriesAsync("bad\nquery"));
        Check(handler.Requests.Count == 1, "Invalid input sends nothing.");
    }
    private static async Task SearchPagesAsync()
    {
        using Handler handler = new((request, _) => Task.FromResult(Json(Envelope(
            request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal) ? Enumerable.Range(1, 100).Select(n => (object)Repository(n, name: "repo" + n)) : [Repository(101, name: "last")], 101))));
        using GitHubHostingClient client = new(new(), Auth(), handler); GitHubRepositorySearchResult result = await client.SearchRepositoriesAsync("fork:true");
        Check(result.Items.Count == 101 && result.TotalCount == 101 && !result.IncompleteResults && !result.Truncated, "Both envelopes were materialized.");
        Check(handler.Requests.Select(r => r.Query).SequenceEqual(new[] { "?q=fork%3Atrue&per_page=100&page=1", "?q=fork%3Atrue&per_page=100&page=2" }), "Only numeric pages under the configured query are read.");
    }
    private static async Task SearchIncompleteAsync()
    {
        foreach (bool incomplete in new[] { false, true })
        {
            using Handler handler = new((_, _) => Task.FromResult(Json(Envelope([Repository()], incomplete ? 1 : 500, incomplete))));
            using GitHubHostingClient client = new(new(), handler: handler) { MaxPages = 1 };
            GitHubRepositorySearchResult result = await client.SearchRepositoriesAsync("repo");
            Check(result.Truncated && result.IncompleteResults == incomplete && handler.Requests.Count == 1, "Neither server incompleteness nor local bounds claim complete results.");
        }
    }
    private static async Task SearchThousandAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Json(Envelope(Enumerable.Range(1, 100).Select(n => (object)Repository(n)), 5000))));
        using GitHubHostingClient client = new(new(), handler: handler) { MaxPages = 100 };
        GitHubRepositorySearchResult result = await client.SearchRepositoriesAsync("repo");
        Check(result.Items.Count == 1000 && handler.Requests.Count == 10 && result.Truncated, "No illegal search page11, even when generic list bounds allow more pages.");
    }
    private static async Task SearchMalformedAsync()
    {
        foreach (object body in new object[] { new { items = new[] { Repository() }, total_count = -1, incomplete_results = false }, new { items = Secret }, Envelope(Enumerable.Range(1, 101).Select(n => (object)Repository(n)), 101) })
        {
            using Handler handler = new((_, _) => Task.FromResult(Json(body))); using GitHubHostingClient client = new(new(), Auth(), handler);
            Check(!(await Expect<GitHubHostingException>(() => client.SearchRepositoriesAsync("repo"))).ToString().Contains(Secret, StringComparison.Ordinal), "Invalid envelope cannot disclose payload.");
        }
    }
    private static async Task SearchCancellationAsync()
    {
        using CancellationTokenSource cancellation = new();
        using Handler handler = new((request, token) =>
        {
            if (request.RequestUri!.Query.EndsWith("page=2", StringComparison.Ordinal)) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(Json(Envelope(Enumerable.Range(1, 100).Select(n => (object)Repository(n)), 101)));
        });
        using GitHubHostingClient client = new(new(), handler: handler);
        await Expect<OperationCanceledException>(() => client.SearchRepositoriesAsync("repo", cancellation.Token)); Check(handler.Requests.Count == 2, "Cancellation interrupted the actual second page.");
    }
    private static async Task MetadataAsync()
    {
        Dictionary<string, object?> payload = Repository(); payload["permissions"] = new { push = false }; payload["homepage"] = "https://project.example.invalid/docs";
        using Handler handler = new((_, _) => Task.FromResult(Json(payload))); using GitHubHostingClient client = new(new(), handler: handler);
        GitHubHostedRepository repository = await client.GetRepositoryAsync(Id);
        Check(repository.CanPush == false && repository.ForksCount == 7 && repository.Homepage == "https://project.example.invalid/docs", "The original homepage is retained separately from its GitHub web page and push is not inferred.");
        payload.Remove("permissions"); payload.Remove("forks_count"); repository = await client.GetRepositoryAsync(Id);
        Check(repository.CanPush is null && repository.ForksCount is null, "Absent permission and count are unknown, not optimistic values.");
    }
    private static async Task MetadataInvalidAsync()
    {
        foreach (string homepage in new[] { "https://token@github.com/repo", "file:///etc/passwd", "https://example.invalid/\nsecret" })
        {
            var payload = Repository(); payload["homepage"] = homepage;
            using Handler handler = new((_, _) => Task.FromResult(Json(payload))); using GitHubHostingClient client = new(new(), handler: handler);
            Check((await client.GetRepositoryAsync(Id)).Homepage is null, "Malformed/executable/credential-bearing links are not offered to the browser.");
        }
        var invalid = Repository(); invalid["forks_count"] = -1;
        using Handler malformed = new((_, _) => Task.FromResult(Json(invalid))); using GitHubHostingClient broken = new(new(), handler: malformed);
        await Expect<GitHubHostingException>(() => broken.GetRepositoryAsync(Id));
    }
    private static async Task RepositoryRoutesAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(Json(new[] { Repository() })));
        using GitHubHostingClient concrete = new(new(), Auth(), handler); IGitHubForkCloneClient client = concrete;
        await client.GetRepositoriesAsync("owner"); await client.GetRepositoriesAsync();
        Check(handler.Requests.Select(r => r.Path).SequenceEqual(new[] { "/users/owner/repos", "/user/repos" }), "The existing endpoints are reused, not guessed from search.");
    }
    private static async Task ForkAsync()
    {
        var payload = Repository(2, "me"); payload["fork"] = true; payload["parent"] = Repository();
        using Handler handler = new((_, _) => Task.FromResult(Json(payload, HttpStatusCode.Accepted)));
        using GitHubHostingClient concrete = new(new(), Auth(), handler); IGitHubForkCloneClient client = concrete;
        Check(handler.Requests.Count == 0, "Construction makes no mutation."); GitHubHostedRepository fork = await client.ForkRepositoryAsync(Id);
        Request request = handler.Requests.Single(); Check(request.Method == "POST" && request.Path == "/repos/owner/repo/forks" && request.Body == "{}" && request.Authorization == "Bearer " + Secret, "A personal fork is one explicit authenticated write.");
        Check(fork.Parent == Id && fork.Identity == new GitHubRepositoryId("me", "repo"), "Accepted fork metadata is sufficient to select it, without claiming ready Git objects.");
    }
    private static async Task ForkAdmissionAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new("https://api.github.com/repos/owner/new/forks") } }));
        using GitHubHostingClient guest = new(new(), handler: new Handler((_, _) => throw new InvalidOperationException("Guest must not request")));
        await Expect<GitHubHostingException>(() => guest.ForkRepositoryAsync(Id));
        using GitHubHostingClient client = new(new(), Auth(), handler); await Expect<GitHubHostingException>(() => client.ForkRepositoryAsync(Id));
        Check(handler.Requests.Count == 1, "POST is never replayed, including same-origin redirects.");
    }
    private static async Task ErrorsAsync()
    {
        foreach (int status in new[] { 401, 403, 404, 422, 429, 503 })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret) }));
            using GitHubHostingClient client = new(new(), Auth(), handler);
            GitHubHostingException error = await Expect<GitHubHostingException>(() => client.SearchRepositoriesAsync("repo"));
            Check(error.StatusCode == status && !error.ToString().Contains(Secret, StringComparison.Ordinal), "Status is real while diagnostics omit server body/token.");
        }
    }
    private static async Task HeaderDeadlineAsync()
    {
        bool exited = false;
        using Handler handler = new(async (_, token) => { try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Repository()); } finally { exited = true; } });
        using GitHubHostingClient client = new(new(), handler: handler) { RequestTimeout = TimeSpan.FromMilliseconds(250) };
        Check((await Expect<GitHubHostingException>(() => client.GetPullRequestAsync(Id, 1))).Message.Contains("timed out", StringComparison.Ordinal) && exited, "The deadline drains a stalled PR header request.");
    }
    private sealed class BlockingBody : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exited { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; } finally { Exited = true; } }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static async Task BodyDeadlineAsync()
    {
        using BlockingBody stream = new(); using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using GitHubHostingClient client = new(new(), handler: handler) { RequestTimeout = TimeSpan.FromMilliseconds(250) };
        GitHubHostingException error = await Expect<GitHubHostingException>(() => client.GetPullRequestDiffAsync(Id, 1));
        Check(stream.Started.Task.IsCompleted && stream.Exited && error.Message.Contains("timed out", StringComparison.Ordinal), "A body stalled AFTER valid headers is bounded and drained too.");
    }
    private static async Task BodyCancellationAsync()
    {
        using BlockingBody stream = new(); using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using GitHubHostingClient client = new(new(), handler: handler); using CancellationTokenSource cancel = new();
        Task read = client.GetPullRequestDiffAsync(Id, 1, cancel.Token); await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Expect<OperationCanceledException>(() => read); Check(stream.Exited, "Caller cancellation retains OCE after stream drainage, not a timeout or partial diff.");
    }
    private static async Task SearchRedirectAsync()
    {
        using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://enterprise.invalid/login") } }));
        using GitHubHostingClient client = new(new("https://enterprise.invalid", "https://enterprise.invalid/api/v3/"), Auth(), handler);
        await Expect<GitHubHostingException>(() => client.SearchRepositoriesAsync("repo")); Check(handler.Requests.Count == 1, "No credentials leave the Enterprise API prefix.");
    }
    private static async Task ShallowCompatibilityAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await f.Repository.CreateBranchAsync("topic", checkout: false);
        f.Write("second.txt", "second\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Second");
        string source = new Uri(f.Repository.Path).AbsoluteUri;
        foreach (bool? single in new bool?[] { false, null, true })
        {
            GitCloneOptions options = new(RecurseSubmodules: false, Depth: 1, SingleBranch: single);
            GitRepository clone = await GitRepository.CloneAsync(source, Path.Combine(f.Root, "clone-" + (single?.ToString() ?? "native")), options);
            bool topic = (await clone.GetReferencesAsync()).Any(reference => reference.Name == "refs/remotes/origin/topic");
            Check(topic == (single == false), "Only the compatible old default false retains every remote branch in a shallow clone.");
        }
        Check(new GitCloneOptions().SingleBranch == false, "All old callers retain the existing default.");
    }
    private static Task ImmutableAsync()
    {
        GitHubHostedRepository repository = new(1, "owner", "repo", "", false, false, "main", "", "", "");
        List<GitHubHostedRepository> draft = [repository]; GitHubRepositorySearchResult page = new(draft, 2); draft.Clear();
        Check(page.Items.Count == 1 && page.Truncated && ((ICollection<GitHubHostedRepository>)page.Items).IsReadOnly, "Result ownership does not depend on the mutable transport list."); return Task.CompletedTask;
    }
}
