using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace GitExtensions.Linux;

public sealed partial class GitHubHostingClient
{
    public async Task<GitHubRepositorySearchResult> SearchRepositoriesAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Any(char.IsControl) || query.Length > 4096) { throw new ArgumentException("Enter a repository search without control characters within the supported length.", nameof(query)); }
        if (MaxPages is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(MaxPages)); }
        // The search endpoint returns an object, unlike the array-based repository APIs,
        // and GitHub exposes no more than 1000 results for a single query.
        List<GitHubHostedRepository> items = []; long total = 0; bool incomplete = false, more = false;
        int pages = Math.Min(MaxPages, 10);
        for (int page = 1; page <= pages; page++)
        {
            Payload response = await SendAsync(HttpMethod.Get, "search/repositories?q=" + Uri.EscapeDataString(query)
                + "&per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture), null, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
            var result = Parse(response.Bytes, root =>
            {
                long count = root.GetProperty("total_count").GetInt64();
                if (count < 0) { throw new JsonException(); }
                bool partial = root.GetProperty("incomplete_results").GetBoolean();
                GitHubHostedRepository[] repositories = root.GetProperty("items").EnumerateArray().Select(Repository).ToArray();
                if (repositories.Length > PageSize) { throw new JsonException(); }
                return (Count: count, Partial: partial, Repositories: repositories);
            });
            total = Math.Max(total, result.Count); incomplete |= result.Partial; items.AddRange(result.Repositories);
            more = response.Next ?? (result.Repositories.Length == PageSize && items.Count < total);
            if (!more || result.Repositories.Length == 0) { break; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(items, total, incomplete, more);
    }
}
