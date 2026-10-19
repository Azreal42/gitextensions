using System.Globalization;
using System.Text.Json;

namespace GitExtensions.Linux;

internal static class GitHubHostingJson
{
    internal static string RequiredText(JsonElement value, string name)
        => value.GetProperty(name).GetString() ?? throw new JsonException();
    private static string Text(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) && property.ValueKind != JsonValueKind.Null ? property.GetString() ?? "" : "";
    private static int Count(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement p) ? p.GetInt32() : 0;
    private static bool Flag(JsonElement value, string name) => !value.TryGetProperty(name, out JsonElement p) ? false : p.ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False or JsonValueKind.Null => false, _ => throw new JsonException() };
    private static string Author(JsonElement value) => value.TryGetProperty("user", out JsonElement user) && user.ValueKind == JsonValueKind.Object ? Text(user, "login") : "(deleted)";
    private static DateTimeOffset Date(JsonElement value, string name) => value.GetProperty(name).GetDateTimeOffset();
    private static bool Hash(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    private static string? Homepage(JsonElement value)
    {
        string text = Text(value, "homepage");
        return !text.Any(char.IsControl) && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme is "https" or "http") && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }
    private static string Web(JsonElement value, GitHubHostingSettings settings) => settings.SafeWebUri(Text(value, "html_url"))?.AbsoluteUri ?? "";
    private static string Clone(string url, string owner, string name, GitHubHostingSettings settings)
        => settings.TryParseRemote(url, out GitHubRepositoryId? identity) && identity.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)
            && identity.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? url : "";
    internal static GitHubHostedRepository Repository(JsonElement value, GitHubHostingSettings settings)
    {
        string owner = RequiredText(value.GetProperty("owner"), "login"), name = RequiredText(value, "name");
        GitHubHostingSettings.Segment(owner); GitHubHostingSettings.Segment(name);
        GitHubRepositoryId? parent = value.TryGetProperty("parent", out JsonElement p) && p.ValueKind == JsonValueKind.Object
            ? new(RequiredText(p.GetProperty("owner"), "login"), RequiredText(p, "name")) : null;
        long? forks = value.TryGetProperty("forks_count", out JsonElement f) && f.ValueKind != JsonValueKind.Null ? f.GetInt64() : null;
        if (forks < 0) { throw new JsonException(); }
        bool? push = value.TryGetProperty("permissions", out JsonElement permissions) && permissions.ValueKind == JsonValueKind.Object
            && permissions.TryGetProperty("push", out JsonElement permission) ? permission.GetBoolean() : null;
        return new(value.GetProperty("id").GetInt64(), owner, name, Text(value, "description"), Flag(value, "private"), Flag(value, "fork"),
            Text(value, "default_branch"), Clone(Text(value, "clone_url"), owner, name, settings), Clone(Text(value, "ssh_url"), owner, name, settings), Web(value, settings), parent)
        { ForksCount = forks, Homepage = Homepage(value), CanPush = push };
    }
    private static GitHubPullRequestRef Ref(JsonElement value, GitHubHostingSettings settings)
    {
        string sha = RequiredText(value, "sha"); if (!Hash(sha)) { throw new JsonException(); }
        GitHubHostedRepository? repository = value.TryGetProperty("repo", out JsonElement repo) && repo.ValueKind != JsonValueKind.Null ? Repository(repo, settings) : null;
        return new(repository, RequiredText(value, "ref"), sha);
    }
    internal static GitHubPullRequest PullRequest(JsonElement value, GitHubHostingSettings settings)
    {
        int number = value.GetProperty("number").GetInt32(); if (number <= 0) { throw new JsonException(); }
        return new(number, RequiredText(value, "title"), Text(value, "body"), Author(value), Date(value, "created_at"), RequiredText(value, "state"), Web(value, settings),
            Ref(value.GetProperty("base"), settings), Ref(value.GetProperty("head"), settings), Flag(value, "merged"), value.TryGetProperty("commits", out JsonElement c) && c.ValueKind != JsonValueKind.Null ? c.GetInt32() : null);
    }
    internal static GitHubHostedBranch Branch(JsonElement value)
    { string sha = RequiredText(value.GetProperty("commit"), "sha"); if (!Hash(sha)) { throw new JsonException(); } return new(RequiredText(value, "name"), sha); }
    internal static GitHubPullRequestFile File(JsonElement value) => new(RequiredText(value, "filename"), value.TryGetProperty("previous_filename", out JsonElement p) ? p.GetString() : null,
        RequiredText(value, "status"), Count(value, "additions"), Count(value, "deletions"), Count(value, "changes"), value.TryGetProperty("patch", out JsonElement patch) ? patch.GetString() : null);
    internal static GitHubPullRequestDiscussionEntry Comment(JsonElement value, GitHubHostingSettings settings)
        => new(GitHubDiscussionKind.Comment, Author(value), Date(value, "created_at"), Text(value, "body"), Web(value, settings));
    internal static GitHubPullRequestDiscussionEntry Commit(JsonElement value, GitHubHostingSettings settings)
    {
        JsonElement commit = value.GetProperty("commit"); JsonElement author = commit.GetProperty("author");
        string sha = RequiredText(value, "sha"); if (!Hash(sha)) { throw new JsonException(); }
        return new(GitHubDiscussionKind.Commit, Text(author, "name"), Date(author, "date"), Text(commit, "message"), Web(value, settings), sha);
    }
}
