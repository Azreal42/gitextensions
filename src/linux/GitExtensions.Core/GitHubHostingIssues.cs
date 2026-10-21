using System.Text.Json;

namespace GitExtensions.Linux;

/// <summary>Borrowed by one Commit window; close cancels and drains the request before disposal.</summary>
public interface IGitHubAssignedIssuesClient : IDisposable
{
    Task<GitHubHostingPage<GitHubAssignedIssue>> GetAssignedIssuesAsync(CancellationToken cancellationToken = default);
}

public sealed partial class GitHubHostingClient : IGitHubAssignedIssuesClient
{
    public async Task<GitHubHostingPage<GitHubAssignedIssue>> GetAssignedIssuesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
        if (_authorization is null) { throw new GitHubHostingException("Assigned GitHub issues require explicit hosting credentials."); }
        GitHubHostingPage<GitHubAssignedIssue?> page = await ListAsync(
            "issues?filter=assigned&state=open&sort=updated&direction=desc", AssignedIssue, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(page.Items.OfType<GitHubAssignedIssue>(), page.Truncated);
    }

    private static GitHubAssignedIssue? AssignedIssue(JsonElement value)
    {
        // The assigned-issues feed can include PR objects. GitHub3's includePullRequests default is false.
        if (value.TryGetProperty("pull_request", out _)) { return null; }
        int number = value.GetProperty("number").GetInt32(); if (number <= 0) { throw new JsonException(); }
        JsonElement repository = value.GetProperty("repository");
        string owner = GitHubHostingJson.RequiredText(repository.GetProperty("owner"), "login");
        string name = GitHubHostingJson.RequiredText(repository, "name");
        GitHubHostingSettings.Segment(owner); GitHubHostingSettings.Segment(name);
        string body = value.TryGetProperty("body", out JsonElement description) && description.ValueKind != JsonValueKind.Null
            ? description.GetString() ?? "" : "";
        return new(new(owner, name), number, GitHubHostingJson.RequiredText(value, "title"), body, value.GetProperty("updated_at").GetDateTimeOffset());
    }
}
