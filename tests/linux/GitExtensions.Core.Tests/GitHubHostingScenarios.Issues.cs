using System.Globalization;
using System.Net;
using System.Text;
using GitExtensions.Linux;
using GitUIPluginInterfaces.BuildServerIntegration;

internal static partial class GitHubHostingScenarios
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> AssignedIssueScenarios =>
    [
        ("GitHub assigned issue helper uses original defaults Int32 fallback and preserves untouched plugin keys", IssueSettingsAsync),
        ("GitHub assigned issue choices filter real identities sort recent limit and retain exact template text", IssueProjectionAsync),
        ("GitHub assigned issues use the authenticated Enterprise feed and numeric pages while excluding PRs", AssignedIssueRequestAsync),
        ("GitHub assigned issue bounds are visible and Guest precancelled and disposed clients send nothing", AssignedIssueBoundsAsync),
        ("GitHub assigned issue HTTP and malformed payload errors never disclose response or credentials", AssignedIssueErrorsAsync),
        ("GitHub assigned issue declared and streamed response bytes keep the existing transport bound", AssignedIssueSizeAsync),
        ("GitHub assigned issue cancellation drains the actual handler and deadline covers pending reads", AssignedIssueCancellationAsync),
    ];

    private static BuildServerCredentials IssueCredentials() => new()
    { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = Guid.NewGuid().ToString("N") };
    private static object AssignedIssue(int number, string owner = "octo", string repo = "repo", string title = "Fix λ", string? body = "literal <body>\nsecond", string updated = "2020-01-03T00:00:00Z")
        => new { number, title, body, updated_at = updated, repository = new { owner = new { login = owner }, name = repo } };

    private static Task IssueSettingsAsync()
    {
        Dictionary<string, string?> values = new() { ["future"] = "keep λ", [GitHubIssueHelperSettings.EnabledKey] = "unknown", [GitHubIssueHelperSettings.MaxCountKey] = "not-an-int" };
        GitHubIssueHelperSettings read = GitHubIssueHelperSettings.FromPluginSettings(values);
        Check(read == new GitHubIssueHelperSettings(), "Missing or invalid settings retain enabled=true and maxCount=10.");
        Check(read.WithChanges(values, read).SequenceEqual(values), "An untouched/default view neither materializes nor normalizes old values.");
        IReadOnlyDictionary<string, string?> changed = new GitHubIssueHelperSettings(false, -1).WithChanges(values, read);
        Check(changed[GitHubIssueHelperSettings.EnabledKey] == "false" && changed[GitHubIssueHelperSettings.MaxCountKey] == "-1" && changed["future"] == "keep λ", "Only explicitly changed keys are published; unknown neighbours remain.");
        Check(values[GitHubIssueHelperSettings.EnabledKey] == "unknown", "Input snapshot remains untouched.");
        foreach (int count in new[] { int.MinValue, -1, 0, 1, 42, int.MaxValue })
        {
            values[GitHubIssueHelperSettings.MaxCountKey] = count.ToString(CultureInfo.CurrentCulture);
            Check(GitHubIssueHelperSettings.FromPluginSettings(values).MaxCount == count, "The original whole Int32 domain is preserved.");
        }
        values[GitHubIssueHelperSettings.EnabledKey] = "FALSE";
        Check(!GitHubIssueHelperSettings.FromPluginSettings(values).Enabled, "Original BoolSetting accepts case-insensitive false without guessing other spellings.");
        return Task.CompletedTask;
    }

    private static Task IssueProjectionAsync()
    {
        DateTimeOffset old = DateTimeOffset.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        GitHubAssignedIssue[] issues = [new(new("other", "repo"), 90, "Outside", "", old.AddDays(9)),
            new(new("octo", "repo"), 1, "Old", "old", old), new(new("OCTO", "REPO"), 2, "Recent λ", "body\n<script>literal</script>", old.AddDays(1)),
            new(new("octo", "fork"), 3, "Newest", "", old.AddDays(2)), new(new("octo", "repo"), 0, "Not an issue", "", old.AddDays(10))];
        GitHubRepositoryId[] remotes = [new("octo", "repo"), new("octo", "fork")];
        IReadOnlyList<GitHubIssueCommitTemplate> chosen = GitHubIssueCommitTemplates.Create(issues, remotes, 2);
        Check(chosen.Select(x => x.Name).SequenceEqual(["3: Newest (octo/fork)", "2: Recent λ (OCTO/REPO)"]), "Repository filter precedes newest-update ordering and Take; canonical identity casing matches.");
        Check(chosen[1].Text == "\nFixes #2 : Recent λ\n\nbody\n<script>literal</script>\n", "The exact original issue template keeps leading/trailing newline and literal body.");
        Check(chosen[0].Text == "\nFixes #3 : Newest\n\n\n", "An empty body remains literal.");
        Check(GitHubIssueCommitTemplates.Create(issues, [remotes[0]], 10).All(x => !x.Name.Contains('(')), "One recognized remote needs no repository suffix.");
        Check(GitHubIssueCommitTemplates.Create(issues, remotes, 0).Count == 0 && GitHubIssueCommitTemplates.Create(issues, remotes, -1).Count == 0, "Windows Take semantics admit zero/negative settings without issues.");
        return Task.CompletedTask;
    }

    private static async Task AssignedIssueRequestAsync()
    {
        GitHubHostingSettings enterprise = new("https://git.example.invalid", "https://git.example.invalid/api/v3/");
        BuildServerCredentials credentials = IssueCredentials(); string opaque = credentials.BearerToken!;
        using Handler handler = new((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.Content is null, "The helper sends only GET, never a body or mutating request.");
            Check(request.Headers.Authorization is { Scheme: "Bearer" } authorization && authorization.Parameter == opaque, "The immutable explicit hosting credential snapshot is used without printing it.");
            Check(request.Headers.GetValues("X-GitHub-Api-Version").Single() == GitHubHostingClient.ApiVersion, "The existing API version remains pinned.");
            bool first = request.RequestUri!.Query.Contains("&page=1", StringComparison.Ordinal);
            HttpResponseMessage response = first ? Json(new object[] { AssignedIssue(7), new { pull_request = new { url = "https://git.example.invalid/api/v3/repos/octo/repo/pulls/8" } } })
                : Json(new[] { AssignedIssue(9, body: null) });
            if (first) { response.Headers.TryAddWithoutValidation("Link", "<https://git.example.invalid/api/v3/issues?page=2>; rel=\"next\""); }
            return Task.FromResult(response);
        });
        using GitHubHostingClient client = new(enterprise, credentials, handler); credentials.BearerToken = null;
        GitHubHostingPage<GitHubAssignedIssue> page = await client.GetAssignedIssuesAsync();
        Check(page.Items.Select(issue => issue.Number).SequenceEqual([7, 9]) && page.Items[1].Body == "" && !page.Truncated, "PR discriminants are excluded before issue projection; nullable body and numeric pagination are retained.");
        Check(handler.Calls == 2 && handler.Records.All(request => request.Method == "GET" && request.Host == "git.example.invalid" && request.Path == "/api/v3/issues"
            && request.Query.Contains("filter=assigned", StringComparison.Ordinal) && request.Query.Contains("state=open", StringComparison.Ordinal)
            && request.Query.Contains("sort=updated", StringComparison.Ordinal) && request.Query.Contains("direction=desc", StringComparison.Ordinal)
            && request.Query.Contains("per_page=100", StringComparison.Ordinal)), "Only the configured API prefix and assigned/open/recent feed is read.");
        Check(handler.Records[1].Query.Contains("page=2", StringComparison.Ordinal), "Link controls whether a next numeric page is read.");
    }

    private static async Task AssignedIssueBoundsAsync()
    {
        using Handler handler = new((_, _) => { HttpResponseMessage response = Json(new[] { AssignedIssue(7) }); response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/issues?page=2>; rel=\"next\""); return Task.FromResult(response); });
        using GitHubHostingClient client = new(Default, IssueCredentials(), handler) { MaxPages = 1 };
        GitHubHostingPage<GitHubAssignedIssue> page = await client.GetAssignedIssuesAsync();
        Check(page.Items.Count == 1 && page.Truncated && handler.Calls == 1, "The returned list retains an explicit truncation flag at the real page cap.");
        using Handler none = new((_, _) => throw new InvalidOperationException("No request is expected."));
        using GitHubHostingClient guest = new(Default, handler: none);
        await ExpectAsync<GitHubHostingException>(() => guest.GetAssignedIssuesAsync()); Check(none.Calls == 0, "Guest cannot request authenticated assigned issues.");
        using GitHubHostingClient authenticated = new(Default, IssueCredentials(), none); using CancellationTokenSource canceled = new(); canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => authenticated.GetAssignedIssuesAsync(canceled.Token));
        authenticated.Dispose(); await ExpectAsync<ObjectDisposedException>(() => authenticated.GetAssignedIssuesAsync()); Check(none.Calls == 0, "Precancelled and disposed clients send nothing.");
    }

    private static async Task AssignedIssueErrorsAsync()
    {
        string opaque = Guid.NewGuid().ToString("N");
        foreach (int code in new[] { 401, 403, 429 })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(opaque) }));
            using GitHubHostingClient client = new(Default, IssueCredentials(), handler);
            GitHubHostingException error = await ExpectAsync<GitHubHostingException>(() => client.GetAssignedIssuesAsync());
            Check(error.StatusCode == code && !error.ToString().Contains(opaque, StringComparison.Ordinal), "HTTP status survives, response contents do not escape.");
        }
        foreach (object malformed in new object[] { new[] { new { number = 0 } }, new[] { new { number = 7, title = opaque, body = opaque, updated_at = "invalid", repository = new { owner = new { login = "octo" }, name = "repo" } } } })
        {
            using Handler handler = new((_, _) => Task.FromResult(Json(malformed))); using GitHubHostingClient client = new(Default, IssueCredentials(), handler);
            GitHubHostingException error = await ExpectAsync<GitHubHostingException>(() => client.GetAssignedIssuesAsync());
            Check(!error.ToString().Contains(opaque, StringComparison.Ordinal), "A malformed issue is sanitized at the existing parser boundary.");
        }
        using Handler redirect = new((_, _) => { HttpResponseMessage response = new(HttpStatusCode.Redirect); response.Headers.Location = new("https://outside.invalid/issues"); return Task.FromResult(response); });
        using GitHubHostingClient pinned = new(Default, IssueCredentials(), redirect);
        await ExpectAsync<GitHubHostingException>(() => pinned.GetAssignedIssuesAsync()); Check(redirect.Calls == 1, "Credentials are never forwarded off the configured hosting API origin.");
    }

    private static async Task AssignedIssueSizeAsync()
    {
        foreach (bool streamed in new[] { false, true })
        {
            using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = streamed ? new UnknownLengthContent(new byte[2048]) : new ByteArrayContent(new byte[2048]) }));
            using GitHubHostingClient client = new(Default, IssueCredentials(), handler) { MaxResponseBytes = 1024 };
            GitHubHostingException error = await ExpectAsync<GitHubHostingException>(() => client.GetAssignedIssuesAsync());
            Check(error.Message.Contains("size", StringComparison.Ordinal), "Assigned issues retain declared and actual-body byte bounds.");
        }
    }

    private static async Task AssignedIssueCancellationAsync()
    {
        using CancellationTokenSource cancel = new(); TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously); bool finished = false;
        using Handler handler = new(async (_, token) => { started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Array.Empty<object>()); } finally { finished = true; } });
        using GitHubHostingClient client = new(Default, IssueCredentials(), handler); Task read = client.GetAssignedIssuesAsync(cancel.Token);
        await started.Task; cancel.Cancel(); await ExpectAsync<OperationCanceledException>(() => read); Check(finished, "The actual HTTP handler has completed its cancellation cleanup.");
        using Handler deadline = new(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Array.Empty<object>()); });
        using GitHubHostingClient timed = new(Default, IssueCredentials(), deadline) { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        GitHubHostingException error = await ExpectAsync<GitHubHostingException>(() => timed.GetAssignedIssuesAsync());
        Check(error.Message.Contains("timed out", StringComparison.Ordinal), "The inherited request deadline cancels a pending helper read.");
    }
}
