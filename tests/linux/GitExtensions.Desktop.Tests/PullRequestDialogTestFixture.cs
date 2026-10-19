using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

internal sealed class PullRequestTestClient : IGitHubHostingClient
{
    internal Func<GitHubRepositoryId, CancellationToken, Task<GitHubHostingPage<GitHubHostedBranch>>>? ReadBranches = null;
    internal Func<GitHubRepositoryId, CancellationToken, Task<GitHubHostingPage<GitHubPullRequest>>>? ReadPulls = null;
    internal Func<int, CancellationToken, Task<string>>? ReadDiff = null;
    internal Func<int, CancellationToken, Task<GitHubHostingPage<GitHubPullRequestDiscussionEntry>>>? ReadDiscussion = null;
    internal Func<GitHubRepositoryId, GitHubPullRequestCreate, CancellationToken, Task<GitHubPullRequest>>? Create = null;
    internal Func<int, string, CancellationToken, Task<GitHubPullRequestDiscussionEntry>>? Post = null;
    internal Func<int, CancellationToken, Task<GitHubPullRequest>>? Close = null;
    internal List<(GitHubRepositoryId Repository, GitHubPullRequestCreate Draft)> Created { get; } = [];
    internal List<(int Number, string Body)> Posted { get; } = [];
    internal List<int> Closed { get; } = [];
    internal List<(int Number, string Kind)> Reads { get; } = [];
    internal bool Disposed { get; private set; }
    public GitHubHostingSettings Settings { get; } = new();
    internal static GitHubHostedRepository Target { get; } = new(1, "upstream", "repo", "target", false, false, "main", "https://github.com/upstream/repo.git", "git@github.com:upstream/repo.git", "https://github.com/upstream/repo");
    internal static GitHubHostedRepository Mine { get; } = new(2, "tester", "repo", "fork", false, true, "main", "https://github.com/tester/repo.git", "git@github.com:tester/repo.git", "https://github.com/tester/repo", Target.Identity);
    internal static GitHubPullRequest Pull(int number = 7, string? title = null) => new(number, title ?? "Fix literal <title> #" + number, "Description **literal** <script>kept as text</script>", "tester", DateTimeOffset.Parse("2026-10-19T10:00:00Z"), "open", "https://github.com/upstream/repo/pull/" + number,
        new(Target, "main", new string('a', 40)), new(Mine, "feature", new string('b', 40)));
    internal const string Patch = "diff --git a/literal file.txt b/literal file.txt\nindex 1111111..2222222 100644\n--- a/literal file.txt\n+++ b/literal file.txt\n@@ -1 +1 @@\n-old\n+new\n";
    public Task<string> GetCurrentUserAsync(CancellationToken cancellationToken = default) => Task.FromResult("tester");
    public Task<GitHubHostedRepository> GetRepositoryAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default) => Task.FromResult(repository == Mine.Identity ? Mine : Target);
    public Task<GitHubHostingPage<GitHubHostedRepository>> GetRepositoriesAsync(string? owner = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitHubHostingPage<GitHubHostedRepository>([Target, Mine]));
    public Task<GitHubHostingPage<GitHubHostedRepository>> GetForksAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default) => Task.FromResult(new GitHubHostingPage<GitHubHostedRepository>([Mine]));
    public Task<GitHubHostingPage<GitHubHostedBranch>> GetBranchesAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default)
        => ReadBranches?.Invoke(repository, cancellationToken) ?? Task.FromResult(new GitHubHostingPage<GitHubHostedBranch>([new("main", new string('a', 40)), new("feature", new string('b', 40))]));
    public Task<GitHubHostingPage<GitHubPullRequest>> GetPullRequestsAsync(GitHubRepositoryId repository, string state = "open", CancellationToken cancellationToken = default)
        => ReadPulls?.Invoke(repository, cancellationToken) ?? Task.FromResult(new GitHubHostingPage<GitHubPullRequest>([Pull(), Pull(8)]));
    public Task<GitHubPullRequest> GetPullRequestAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default) => Task.FromResult(Pull(number));
    public Task<GitHubHostingPage<GitHubPullRequestFile>> GetPullRequestFilesAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    { Reads.Add((number, "files")); return Task.FromResult(new GitHubHostingPage<GitHubPullRequestFile>([new("literal file.txt", null, "modified", 1, 1, 2, "@@ -1 +1 @@\n-old\n+new\n")])); }
    public Task<string> GetPullRequestDiffAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    { Reads.Add((number, "diff")); return ReadDiff?.Invoke(number, cancellationToken) ?? Task.FromResult(Patch); }
    public Task<GitHubHostingPage<GitHubPullRequestDiscussionEntry>> GetDiscussionAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    {
        Reads.Add((number, "discussion")); return ReadDiscussion?.Invoke(number, cancellationToken) ?? Task.FromResult(new GitHubHostingPage<GitHubPullRequestDiscussionEntry>(
        [new(GitHubDiscussionKind.Description, "tester", DateTimeOffset.Parse("2026-10-19T10:00:00Z"), "PR " + number + " body <script>literal</script> https://example.invalid/issue/7"),
            new(GitHubDiscussionKind.Commit, "Commit Author", DateTimeOffset.Parse("2026-10-19T11:00:00Z"), "Commit message", Sha: new string('c', 40)),
            new(GitHubDiscussionKind.Comment, "reviewer", DateTimeOffset.Parse("2026-10-19T12:00:00Z"), "Review comment")]));
    }
    public Task<GitHubPullRequest> CreatePullRequestAsync(GitHubRepositoryId repository, GitHubPullRequestCreate draft, CancellationToken cancellationToken = default)
    { Created.Add((repository, draft)); return Create?.Invoke(repository, draft, cancellationToken) ?? Task.FromResult(Pull(9, draft.Title)); }
    public Task<GitHubPullRequestDiscussionEntry> PostCommentAsync(GitHubRepositoryId repository, int number, string body, CancellationToken cancellationToken = default)
    { Posted.Add((number, body)); return Post?.Invoke(number, body, cancellationToken) ?? Task.FromResult(new GitHubPullRequestDiscussionEntry(GitHubDiscussionKind.Comment, "tester", DateTimeOffset.Now, body)); }
    public Task<GitHubPullRequest> ClosePullRequestAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default)
    { Closed.Add(number); return Close?.Invoke(number, cancellationToken) ?? Task.FromResult(Pull(number) with { State = "closed" }); }
    public void Dispose() => Disposed = true;
}

internal sealed class PullRequestUiFixture : IAsyncDisposable
{
    internal GitFixture Git { get; }
    internal PullRequestTestClient Client { get; } = new();
    internal Window? Window { get; set; }
    internal List<Action> ReleaseOnDispose { get; } = [];
    internal static PullRequestHostedRemote[] Remotes => [new("upstream", PullRequestTestClient.Target, false), new("origin", PullRequestTestClient.Mine, true)];
    private PullRequestUiFixture(GitFixture git) => Git = git;
    internal static async Task<PullRequestUiFixture> CreateAsync()
    {
        GitFixture git = await GitFixture.CreateAsync();
        try
        {
            new PreferencesStore().Save(new() { Language = "English", Dictionary = "none" });
            await git.GitAsync("update-ref", "refs/remotes/origin/main", "HEAD"); await git.GitAsync("update-ref", "refs/remotes/origin/feature", "HEAD");
            return new(git);
        }
        catch { git.Dispose(); throw; }
    }
    internal T Field<T>(string name) where T : Control => Window!.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal void Click(string name) { Pump(); PushProcessTestDriver.Pointer(Field<Control>(name)); Pump(); }
    internal void Pump() { if (Window is not null && Window.IsVisible) { DetailedSettingsUiHost.Pump(Window); } }
    internal async Task IdleAsync()
    { await DetailedSettingsUiHost.Until(() => Window is not PullRequestDialog dialog || !dialog.IsBusy); Pump(); }
    internal void Edit(string name, string text)
    {
        Click(name);
        TopLevel inputOwner = TopLevel.GetTopLevel(Field<TextBox>(name)) ?? throw new InvalidOperationException("The editor is not attached to a window.");
        inputOwner.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        if (inputOwner.IsVisible) { inputOwner.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control); }
        Assert.That(inputOwner.IsVisible, Is.True, "Editing must keep its input window open.");
        inputOwner.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        if (inputOwner.IsVisible) { inputOwner.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None); }
        Assert.That(inputOwner.IsVisible, Is.True, "Editing must keep its input window open.");
        inputOwner.KeyTextInput(text); Pump();
        Assert.That(Field<TextBox>(name).Text, Is.EqualTo(text));
    }
    internal void Capture(string name)
    {
        Pump(); using var frame = Window!.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string output = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(output);
        string file = Path.Combine(output, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (Action release in ReleaseOnDispose) { release(); }
        if (Window is PullRequestDialog dialog)
        { await dialog.BeginClose(); if (dialog.IsVisible) { dialog.Close(); } await dialog.Completion; }
        else { Window?.Close(); }
        Client.Dispose(); Git.Dispose();
    }
}
