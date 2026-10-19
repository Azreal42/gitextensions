using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GitExtensions.Linux;

public sealed class GitHubActionsException(string message) : InvalidOperationException(message);

/// <summary>GET-only Actions reader. No credential discovery, commands or mutating API endpoints.</summary>
public sealed class GitHubActionsApiClient : IDisposable
{
    public const string ApiVersion = "2022-11-28"; // Windows compatibility; GitHub supports this version through 2028-03-10.
    public const int PageSize = 100;
    private readonly GitHubActionsSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, Page> _pages = [];
    private GitHubActionsRunSnapshot? _cached;
    private bool _disposed;
    private sealed record Page(string? ETag, IReadOnlyList<GitHubActionsWorkflowRun> Runs, bool Next);
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPages { get; init; } = 10;
    public Uri RepositoryApiUri { get; }

    public GitHubActionsApiClient(GitHubActionsSettings settings, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); _settings = settings; _clock = clock ?? TimeProvider.System;
        _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        RepositoryApiUri = new(GitHubActionsSettings.ApiUri(settings.ApiUrl), "repos/" + Uri.EscapeDataString(settings.Owner) + "/" + Uri.EscapeDataString(settings.Repository) + "/");
    }

    public async Task<GitHubActionsRunSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!force && _cached is not null && _clock.GetUtcNow() - _cached.ReadAt < CacheDuration) { return _cached; }
            if (MaxPages < 1 || MaxPages > 100) { throw new ArgumentOutOfRangeException(nameof(MaxPages)); }
            List<GitHubActionsWorkflowRun> runs = []; Dictionary<int, Page> accepted = [];
            bool more = false;
            for (int page = 1; page <= MaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Page value = await ReadPageAsync(page, cancellationToken).ConfigureAwait(false);
                accepted.Add(page, value); runs.AddRange(value.Runs); more = value.Next;
                if (!more) { break; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Neither partial pages nor an aborted publication replace the last complete cache.
            GitHubActionsRunSnapshot snapshot = new(runs, _clock.GetUtcNow(), more);
            _pages.Clear(); foreach ((int page, Page value) in accepted) { _pages.Add(page, value); }
            return _cached = snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new GitHubActionsException("The GitHub Actions request timed out."); }
        catch (HttpRequestException) { throw new GitHubActionsException("Could not connect to the GitHub Actions API. Check the endpoint and network connection."); }
        catch (JsonException) { throw new GitHubActionsException("The GitHub Actions API returned an invalid workflow response."); }
        catch (FormatException) { throw new GitHubActionsException("The GitHub Actions API returned invalid response headers."); }
        finally { _gate.Release(); }
    }

    private async Task<Page> ReadPageAsync(int number, CancellationToken token)
    {
        Uri uri = new(RepositoryApiUri, $"actions/runs?per_page={PageSize}&page={number}");
        _pages.TryGetValue(number, out Page? cached);
        for (int redirects = 0; ; redirects++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("GitExtensions/7.2.1-linux-alpha");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            if (_settings.Token.Length > 0) { request.Headers.Authorization = new("Bearer", _settings.Token); }
            if (cached?.ETag is string etag && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? parsed))
            { request.Headers.IfNoneMatch.Add(parsed); }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? location = response.Headers.Location;
                if (redirects >= 3 || location is null) { throw new GitHubActionsException("The API redirected repeatedly. Configure its final endpoint."); }
                Uri next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!GitHubActionsSettings.SameOrigin(RepositoryApiUri, next) || !string.IsNullOrEmpty(next.UserInfo)
                    || !string.IsNullOrEmpty(next.Fragment))
                { throw new GitHubActionsException("The API redirected to a different origin. Configure the new endpoint explicitly; credentials were not forwarded."); }
                uri = next; continue;
            }
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) { return cached; }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new GitHubActionsException("GitHub authentication failed. Check the configured API token."); }
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            { throw new GitHubActionsException("GitHub denied the request or its rate limit was reached. Check Actions read permission or try again later."); }
            if (response.StatusCode == HttpStatusCode.NotFound) { throw new GitHubActionsException("The repository was not found or is private. Check the owner, repository and token access."); }
            if (!response.IsSuccessStatusCode) { throw new GitHubActionsException($"GitHub Actions returned HTTP {(int)response.StatusCode}."); }
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            GitHubActionsWorkflowRunsResponse? result = await JsonSerializer.DeserializeAsync<GitHubActionsWorkflowRunsResponse>(stream, cancellationToken: token).ConfigureAwait(false);
            if (result?.WorkflowRuns is null || result.WorkflowRuns.Any(r => r is null)) { throw new GitHubActionsException("The GitHub Actions API response has no workflow list."); }
            // Rebuild numeric pages ourselves: a Link header can never send a token to another origin.
            bool nextPage = response.Headers.TryGetValues("Link", out IEnumerable<string>? links)
                ? links.Any(value => value.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
                : result.WorkflowRuns.Count == PageSize;
            return new(response.Headers.ETag?.ToString(), Array.AsReadOnly(result.WorkflowRuns.ToArray()), nextPage);
        }
    }

    public Uri? BuildPageUri(GitHubActionsWorkflowRun run)
    {
        // Only an explicitly selected, same-site HTTP(S) run page is eligible for xdg-open.
        if (!Uri.TryCreate(run.HtmlUrl, UriKind.Absolute, out Uri? value) || value.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(value.UserInfo)) { return null; }
        Uri api = GitHubActionsSettings.ApiUri(_settings.ApiUrl);
        string webHost = api.IdnHost == "api.github.com" ? "github.com" : api.IdnHost;
        if (!string.Equals(value.IdnHost, webHost, StringComparison.OrdinalIgnoreCase) || value.Scheme != api.Scheme
            || (api.IdnHost != "api.github.com" && value.Port != api.Port)) { return null; }
        string prefix = "/" + Uri.EscapeDataString(_settings.Owner) + "/" + Uri.EscapeDataString(_settings.Repository) + "/actions/runs/";
        return value.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    public void Dispose() { if (_disposed) { return; } _disposed = true; _http.Dispose(); }
}
