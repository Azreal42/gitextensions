using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux;

public sealed class GitHubHostingException(string message, int? statusCode = null) : InvalidOperationException(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Explicit REST operations only. No login discovery, retries of writes, repository mutations or secret-bearing diagnostics.</summary>
public sealed partial class GitHubHostingClient : IGitHubHostingClient, IGitHubForkCloneClient
{
    public const string ApiVersion = "2022-11-28";
    public const int PageSize = 100;
    private readonly HttpClient _http;
    private readonly Uri _api;
    private readonly AuthenticationHeaderValue? _authorization;
    private bool _disposed;
    public GitHubHostingSettings Settings { get; }
    public int MaxPages { get; init; } = 10;
    public int MaxResponseBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public GitHubHostingClient(GitHubHostingSettings settings, IBuildServerCredentials? credentials = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); Settings = settings; _api = GitHubHostingSettings.Endpoint(settings.ApiUrl);
        GitHubHostingCredentials auth = GitHubHostingCredentials.From(credentials); auth.Validate(settings);
        _authorization = auth.Type switch
        {
            BuildServerCredentialsType.BearerToken => new("Bearer", auth.BearerToken),
            BuildServerCredentialsType.UsernameAndPassword => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.Username + ":" + auth.Password))),
            _ => null
        };
        _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static string Repo(GitHubRepositoryId repository)
    { ArgumentNullException.ThrowIfNull(repository); return "repos/" + GitHubHostingSettings.Segment(repository.Owner) + "/" + GitHubHostingSettings.Segment(repository.Name); }
    private static string Pull(GitHubRepositoryId repository, int number)
    { if (number <= 0) { throw new ArgumentOutOfRangeException(nameof(number)); } return Repo(repository) + "/pulls/" + number.ToString(CultureInfo.InvariantCulture); }
    private void RequireWrite()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (_authorization is null) { throw new GitHubHostingException("This action requires explicit hosting credentials. Public pull requests can still be read as Guest."); } }
    public async Task<string> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => await ReadAsync("user", value => GitHubHostingJson.RequiredText(value, "login"), cancellationToken);
    public Task<GitHubHostedRepository> GetRepositoryAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default)
        => ReadAsync(Repo(repository), Repository, cancellationToken);
    public Task<GitHubHostingPage<GitHubHostedRepository>> GetRepositoriesAsync(string? owner = null, CancellationToken cancellationToken = default)
        => ListAsync(owner is null ? "user/repos?sort=full_name" : "users/" + GitHubHostingSettings.Segment(owner) + "/repos?sort=full_name", Repository, cancellationToken);
    public Task<GitHubHostingPage<GitHubHostedRepository>> GetForksAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default)
        => ListAsync(Repo(repository) + "/forks", Repository, cancellationToken);
    public Task<GitHubHostingPage<GitHubHostedBranch>> GetBranchesAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default)
        => ListAsync(Repo(repository) + "/branches", GitHubHostingJson.Branch, cancellationToken);
    public Task<GitHubHostingPage<GitHubPullRequest>> GetPullRequestsAsync(GitHubRepositoryId repository, string state = "open", CancellationToken cancellationToken = default)
    {
        if (state is not ("open" or "closed" or "all")) { throw new ArgumentException("Choose open, closed or all pull requests.", nameof(state)); }
        return ListAsync(Repo(repository) + "/pulls?state=" + state, PullRequest, cancellationToken);
    }
    public Task<GitHubPullRequest> GetPullRequestAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
        => ReadAsync(Pull(repository, number), PullRequest, cancellationToken);
    public Task<GitHubHostingPage<GitHubPullRequestFile>> GetPullRequestFilesAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
        => ListAsync(Pull(repository, number) + "/files", GitHubHostingJson.File, cancellationToken);
    public async Task<string> GetPullRequestDiffAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    {
        Payload value = await SendAsync(HttpMethod.Get, Pull(repository, number), null, "application/vnd.github.diff", cancellationToken);
        try { return new UTF8Encoding(false, true).GetString(value.Bytes); }
        catch (DecoderFallbackException) { throw new GitHubHostingException("GitHub returned an invalid UTF-8 diff."); }
    }
    public async Task<GitHubHostingPage<GitHubPullRequestDiscussionEntry>> GetDiscussionAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    {
        GitHubPullRequest pull = await GetPullRequestAsync(repository, number, cancellationToken);
        Task<GitHubHostingPage<GitHubPullRequestDiscussionEntry>> commits = ListAsync(Pull(repository, number) + "/commits", value => GitHubHostingJson.Commit(value, Settings), cancellationToken);
        Task<GitHubHostingPage<GitHubPullRequestDiscussionEntry>> comments = ListAsync(Repo(repository) + "/issues/" + number.ToString(CultureInfo.InvariantCulture) + "/comments", value => GitHubHostingJson.Comment(value, Settings), cancellationToken);
        await Task.WhenAll(commits, comments); cancellationToken.ThrowIfCancellationRequested();
        GitHubPullRequestDiscussionEntry description = new(GitHubDiscussionKind.Description, pull.Author, pull.CreatedAt, pull.Body, pull.WebUrl);
        return new(new[] { description }.Concat(commits.Result.Items).Concat(comments.Result.Items).OrderBy(value => value.CreatedAt),
            commits.Result.Truncated || comments.Result.Truncated || (pull.CommitCount is int count && count > commits.Result.Items.Count));
    }
    public Task<GitHubPullRequest> CreatePullRequestAsync(GitHubRepositoryId repository, GitHubPullRequestCreate draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft); RequireWrite();
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrWhiteSpace(draft.Head) || string.IsNullOrWhiteSpace(draft.Base)
            || draft.Head.Any(char.IsControl) || draft.Base.Any(char.IsControl))
        { throw new ArgumentException("Enter a title, source branch and target branch."); }
        return WriteAsync(HttpMethod.Post, Repo(repository) + "/pulls", new { title = draft.Title.Trim(), head = draft.Head, @base = draft.Base,
            body = draft.Body, draft = draft.Draft, maintainer_can_modify = draft.MaintainerCanModify }, PullRequest, cancellationToken);
    }
    public Task<GitHubPullRequestDiscussionEntry> PostCommentAsync(GitHubRepositoryId repository, int number, string body, CancellationToken cancellationToken = default)
    {
        RequireWrite(); Pull(repository, number);
        if (string.IsNullOrWhiteSpace(body)) { throw new ArgumentException("Enter a comment.", nameof(body)); }
        return WriteAsync(HttpMethod.Post, Repo(repository) + "/issues/" + number.ToString(CultureInfo.InvariantCulture) + "/comments", new { body }, value => GitHubHostingJson.Comment(value, Settings), cancellationToken);
    }
    public Task<GitHubPullRequest> ClosePullRequestAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    { RequireWrite(); return WriteAsync(HttpMethod.Patch, Pull(repository, number), new { state = "closed" }, PullRequest, cancellationToken); }
    public Task<GitHubHostedRepository> ForkRepositoryAsync(GitHubRepositoryId repository, string? organization = null, CancellationToken cancellationToken = default)
    {
        RequireWrite(); if (organization is not null) { GitHubHostingSettings.Segment(organization); }
        return WriteAsync(HttpMethod.Post, Repo(repository) + "/forks", organization is null ? new { } : (object)new { organization }, Repository, cancellationToken);
    }
    private GitHubHostedRepository Repository(JsonElement value) => GitHubHostingJson.Repository(value, Settings);
    private GitHubPullRequest PullRequest(JsonElement value) => GitHubHostingJson.PullRequest(value, Settings);
    private async Task<T> ReadAsync<T>(string path, Func<JsonElement, T> project, CancellationToken token)
        => Parse((await SendAsync(HttpMethod.Get, path, null, "application/vnd.github+json", token)).Bytes, project);
    private async Task<T> WriteAsync<T>(HttpMethod method, string path, object body, Func<JsonElement, T> project, CancellationToken token)
        => Parse((await SendAsync(method, path, JsonSerializer.SerializeToUtf8Bytes(body), "application/vnd.github+json", token)).Bytes, project);
    private async Task<GitHubHostingPage<T>> ListAsync<T>(string path, Func<JsonElement, T> project, CancellationToken token)
    {
        if (MaxPages is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(MaxPages)); }
        List<T> items = []; bool more = false;
        for (int page = 1; page <= MaxPages; page++)
        {
            Payload response = await SendAsync(HttpMethod.Get, path + (path.Contains('?') ? "&" : "?") + "per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture), null, "application/vnd.github+json", token);
            T[] next = Parse(response.Bytes, root => root.EnumerateArray().Select(project).ToArray());
            items.AddRange(next); more = response.Next ?? next.Length == PageSize;
            if (!more) { break; }
        }
        token.ThrowIfCancellationRequested(); return new(items, more);
    }
    private static T Parse<T>(byte[] bytes, Func<JsonElement, T> project)
    {
        try { using JsonDocument json = JsonDocument.Parse(bytes); return project(json.RootElement); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException or ArgumentException)
        { throw new GitHubHostingException("GitHub returned an invalid hosting response."); }
    }
    private sealed record Payload(byte[] Bytes, bool? Next);
    private async Task<Payload> SendAsync(HttpMethod method, string path, byte[]? body, string accept, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        if (MaxResponseBytes is < 1024 or > 64 * 1024 * 1024) { throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes)); }
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5)) { throw new ArgumentOutOfRangeException(nameof(RequestTimeout)); }
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Keep this
        // one deadline alive until the last body byte, including GET redirects.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        CancellationToken requestToken = deadline.Token;
        Uri uri = new(_api, path);
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                requestToken.ThrowIfCancellationRequested();
                if (!GitHubHostingSettings.SameOrigin(_api, uri) || !uri.AbsolutePath.StartsWith(_api.AbsolutePath, StringComparison.Ordinal)
                    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                { throw new GitHubHostingException("The API redirected outside the configured hosting endpoint. Credentials were not forwarded."); }
                using HttpRequestMessage request = new(method, uri);
                request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha"); request.Headers.Accept.Add(new(accept));
                request.Headers.Add("X-GitHub-Api-Version", ApiVersion); request.Headers.Authorization = _authorization;
                if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
                int code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    if (method != HttpMethod.Get) { throw new GitHubHostingException("GitHub redirected this write. It was not replayed; check the current pull request before trying again.", code); }
                    if (redirects >= 3 || response.Headers.Location is not { } location) { throw new GitHubHostingException("The hosting API redirected repeatedly. Configure its final endpoint.", code); }
                    uri = location.IsAbsoluteUri ? location : new(uri, location); continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    string error = code switch
                    { 401 => "GitHub authentication failed. Check the hosting credentials.", 403 or 429 => "GitHub denied access or its rate limit was reached. Check repository permissions or try again later.",
                      404 => "The GitHub repository or pull request was not found, or is private.", 409 => "GitHub reported a conflict. Refresh the pull request before trying again.",
                      422 => "GitHub rejected the request. Check the title, branches, permissions and whether a pull request already exists.",
                      _ => "GitHub hosting returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + "." };
                    throw new GitHubHostingException(error, code);
                }
                if (response.Content.Headers.ContentLength > MaxResponseBytes) { throw new GitHubHostingException("The GitHub response exceeds the supported size. Open the pull request explicitly in the browser."); }
                await using Stream stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using MemoryStream bytes = new(); byte[] buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), requestToken).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > MaxResponseBytes) { throw new GitHubHostingException("The GitHub response exceeds the supported size. Open the pull request explicitly in the browser."); }
                    bytes.Write(buffer, 0, read);
                }
                requestToken.ThrowIfCancellationRequested();
                bool? next = response.Headers.TryGetValues("Link", out IEnumerable<string>? links) ? links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)) : null;
                return new(bytes.ToArray(), next);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new GitHubHostingException("The GitHub hosting request timed out."); }
        catch (GitHubHostingException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException or InvalidOperationException)
        { throw new GitHubHostingException("Could not read the GitHub hosting API. Check the endpoint and network connection."); }
    }
    public void Dispose() { if (_disposed) { return; } _disposed = true; _http.Dispose(); }
}
