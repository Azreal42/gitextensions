using System.Security.Cryptography;
using System.Text.Json;
using GitExtensions.Linux;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitHubPullRequestFetchTests
{
    private const string HeadRef = "feature/topic";
    private const string PullBranch = "pr/n12_feature/topic";
    private const string Owner = "literal-owner";

    [Test]
    public async Task DirectFetchTransfersOneBareSourceBranchToTheOriginalPrNameWithoutTouchingHeadIndexOrWorktree()
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        RepositoryState before = await fixture.CaptureAsync();
        Dictionary<string, string> refs = await fixture.RefsAsync(); byte[] config = fixture.ConfigBytes;
        fixture.ClearTrace();
        GitResult result = await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, HeadRef, PullBranch);
        Assert.That(result.ExitCode, Is.Zero); Assert.That(result.Success, Is.True);
        refs.Add("refs/heads/" + PullBranch, fixture.SourceTip);
        Assert.That(await fixture.RefsAsync(), Is.EquivalentTo(refs));
        Assert.That(fixture.ConfigBytes, Is.EqualTo(config), "URL-only fetch does not invent a remote or tracking configuration.");
        await before.AssertUnchangedAsync(fixture);
        AssertFetch(fixture, fixture.Source, PullBranch);
    }

    [Test]
    public async Task OwnerModeAddsTheExactRemoteAndOwnerBranchAndRetainsItsStaleRefAndLocalTags()
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        await fixture.Git.GitAsync("update-ref", "refs/remotes/" + Owner + "/stale", "HEAD");
        RepositoryState before = await fixture.CaptureAsync();
        fixture.ClearTrace(); string branch = Owner + "/" + HeadRef;
        GitResult result = await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, HeadRef, branch, Owner);
        Assert.That(result.Success, Is.True);
        Assert.That((await fixture.Git.GitAsync("remote")).StandardOutput.Trim(), Is.EqualTo(Owner));
        Assert.That((await fixture.Git.GitAsync("config", "--get-all", "remote." + Owner + ".url")).StandardOutput.TrimEnd('\n'), Is.EqualTo(fixture.Source));
        Assert.That((await fixture.Git.GitAsync("config", "--get-all", "remote." + Owner + ".fetch")).StandardOutput.TrimEnd('\n'),
            Is.EqualTo("+refs/heads/*:refs/remotes/" + Owner + "/*"));
        Dictionary<string, string> refs = await fixture.RefsAsync();
        Assert.That(refs["refs/heads/" + branch], Is.EqualTo(fixture.SourceTip));
        Assert.That(refs["refs/remotes/" + Owner + "/stale"], Is.EqualTo(before.HeadCommit), "Fetching the PR does not prune an unrelated stale remote ref.");
        Assert.That(refs["refs/tags/local-only"], Is.EqualTo(before.HeadCommit));
        Assert.That(refs.ContainsKey("refs/tags/source-tag"), Is.False, "The source tag is reachable from the fetched commit but --no-tags still suppresses auto-follow.");
        Assert.That(refs.ContainsKey("refs/heads/" + PullBranch), Is.False);
        await before.AssertUnchangedAsync(fixture); AssertFetch(fixture, Owner, branch);
        string[] commands = fixture.Commands().Where(args => args.Contains("remote")).Select(args => string.Join('\n', args)).ToArray();
        Assert.That(commands.Count(command => command.EndsWith("remote\nadd\n--\n" + Owner + "\n" + fixture.Source, StringComparison.Ordinal)), Is.EqualTo(1));
    }

    [Test]
    public async Task AnExistingExactRemoteIsReusedWithoutRewritingItsConfiguration()
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        await fixture.Git.GitAsync("remote", "add", Owner, fixture.Source);
        await fixture.Git.GitAsync("config", "remote." + Owner + ".pushurl", "ssh://example.invalid/retained/push.git");
        byte[] config = fixture.ConfigBytes; RepositoryState before = await fixture.CaptureAsync(); fixture.ClearTrace();
        await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, HeadRef, Owner + "/" + HeadRef, Owner);
        Assert.That(fixture.ConfigBytes, Is.EqualTo(config));
        Assert.That(await fixture.Repository.ResolveRevisionAsync("refs/heads/" + Owner + "/" + HeadRef), Is.EqualTo(fixture.SourceTip));
        Assert.That(fixture.Commands().Any(args => args.Contains("remote") && args.Contains("add")), Is.False);
        await before.AssertUnchangedAsync(fixture); AssertFetch(fixture, Owner, Owner + "/" + HeadRef);
    }

    [TestCase(false)] [TestCase(true)]
    public async Task CollidingRemoteUrlOrMultipleFetchUrlsAreRefusedBeforeAnyFetchOrConfigChange(bool multipleUrls)
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        await fixture.Git.GitAsync("remote", "add", Owner, multipleUrls ? fixture.Source : fixture.Git.Output("different source.git"));
        if (multipleUrls) { await fixture.Git.GitAsync("config", "--add", "remote." + Owner + ".url", fixture.Git.Output("second source.git")); }
        byte[] config = fixture.ConfigBytes; Dictionary<string, string> refs = await fixture.RefsAsync();
        RepositoryState before = await fixture.CaptureAsync(); fixture.ClearTrace();
        InvalidOperationException? failure = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, HeadRef, Owner + "/" + HeadRef, Owner));
        Assert.That(failure!.Message, Does.Contain("different URL"));
        Assert.That(fixture.ConfigBytes, Is.EqualTo(config)); Assert.That(await fixture.RefsAsync(), Is.EquivalentTo(refs));
        Assert.That(fixture.Commands().Any(args => args.Contains("fetch") || args.Contains("remote") && args.Contains("add")), Is.False);
        await before.AssertUnchangedAsync(fixture);
    }

    [TestCase("bad..source", PullBranch, Owner)]
    [TestCase(HeadRef, "bad branch", Owner)]
    [TestCase(HeadRef, PullBranch, "bad..owner")]
    public async Task InvalidSourceDestinationOrOwnerRefIsRefusedBeforeRemoteCreation(string head, string branch, string owner)
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        byte[] config = fixture.ConfigBytes; Dictionary<string, string> refs = await fixture.RefsAsync();
        RepositoryState before = await fixture.CaptureAsync(); fixture.ClearTrace();
        GitCommandException? failure = Assert.ThrowsAsync<GitCommandException>(async () =>
            await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, head, branch, owner));
        Assert.That(failure!.Arguments[0], Is.EqualTo("check-ref-format")); Assert.That(failure.Result.Success, Is.False);
        Assert.That(fixture.ConfigBytes, Is.EqualTo(config)); Assert.That(await fixture.RefsAsync(), Is.EquivalentTo(refs));
        Assert.That(fixture.Commands().Any(args => args.Contains("fetch") || args.Contains("remote") && args.Contains("add")), Is.False);
        await before.AssertUnchangedAsync(fixture);
    }

    [Test]
    public async Task DivergentExistingDestinationIsRejectedByNativeGitWithoutForcePruneTagsOrAnyCheckout()
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        string localCommit = (await fixture.Git.GitAsync("commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "Independent local destination")).StandardOutput.Trim();
        await fixture.Git.GitAsync("update-ref", "refs/heads/" + PullBranch, localCommit);
        await fixture.Git.GitAsync("update-ref", "refs/remotes/keep/stale", "HEAD");
        Dictionary<string, string> refs = await fixture.RefsAsync(); byte[] config = fixture.ConfigBytes;
        RepositoryState before = await fixture.CaptureAsync(); fixture.ClearTrace();
        GitCommandException? failure = Assert.ThrowsAsync<GitCommandException>(async () =>
            await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, HeadRef, PullBranch));
        Assert.That(failure!.Result.Success, Is.False); Assert.That(failure.Result.StandardError, Does.Contain("non-fast-forward"));
        Assert.That(await fixture.RefsAsync(), Is.EquivalentTo(refs)); Assert.That(fixture.ConfigBytes, Is.EqualTo(config));
        await before.AssertUnchangedAsync(fixture); AssertFetch(fixture, fixture.Source, PullBranch);
    }

    [Test]
    public async Task MissingSourceBranchThrowsTheRealGitFailureInsteadOfReturningASuccessfulResult()
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        Dictionary<string, string> refs = await fixture.RefsAsync(); byte[] config = fixture.ConfigBytes;
        RepositoryState before = await fixture.CaptureAsync(); fixture.ClearTrace();
        GitResult? result = null;
        GitCommandException? failure = Assert.ThrowsAsync<GitCommandException>(async () =>
        { result = await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, "missing/branch", PullBranch); });
        Assert.That(result, Is.Null); Assert.That(failure!.Result.ExitCode, Is.Not.Zero);
        Assert.That(failure.Arguments, Is.EqualTo(new[] { "fetch", "--no-tags", "--progress", "--", fixture.Source, "refs/heads/missing/branch:refs/heads/" + PullBranch }));
        Assert.That(await fixture.RefsAsync(), Is.EquivalentTo(refs)); Assert.That(fixture.ConfigBytes, Is.EqualTo(config));
        await before.AssertUnchangedAsync(fixture);
    }

    [Test]
    public async Task PreCancelledFetchHasNoFabricatedResultAndDoesNotCreateTheRequestedRemoteOrBranch()
    {
        using FetchFixture fixture = await FetchFixture.CreateAsync();
        Dictionary<string, string> refs = await fixture.RefsAsync(); byte[] config = fixture.ConfigBytes;
        RepositoryState before = await fixture.CaptureAsync(); fixture.ClearTrace();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel(); GitResult? result = null;
        Assert.CatchAsync<OperationCanceledException>(async () =>
        { result = await fixture.Repository.FetchPullRequestBranchAsync(fixture.Source, HeadRef, Owner + "/" + HeadRef, Owner, cancellation.Token); });
        Assert.That(result, Is.Null); Assert.That(fixture.Commands(), Is.Empty, "Cancellation before admission must not launch Git.");
        Assert.That(fixture.ConfigBytes, Is.EqualTo(config)); Assert.That(await fixture.RefsAsync(), Is.EquivalentTo(refs));
        await before.AssertUnchangedAsync(fixture);
    }

    private static void AssertFetch(FetchFixture fixture, string source, string localBranch)
    {
        string[][] commands = fixture.Commands();
        string[] actual = commands.Single(args => args.Contains("fetch")); int verb = Array.IndexOf(actual, "fetch");
        Assert.That(actual[verb..], Is.EqualTo(new[] { "fetch", "--no-tags", "--progress", "--", source, "refs/heads/" + HeadRef + ":refs/heads/" + localBranch }));
        Assert.That(actual, Does.Not.Contain("--force").And.Not.Contain("-f").And.Not.Contain("--prune").And.Not.Contain("--prune-tags").And.Not.Contain("--tags"));
        Assert.That(commands.Any(args => args.Contains("checkout") || args.Contains("switch") || args.Contains("reset")), Is.False);
    }

    private sealed record RepositoryState(byte[] Head, string HeadCommit, byte[] Index, string Worktree)
    {
        internal async Task AssertUnchangedAsync(FetchFixture fixture)
        {
            RepositoryState actual = await fixture.CaptureAsync();
            Assert.That(actual.Head, Is.EqualTo(Head), "HEAD must retain its exact symbolic/detached representation.");
            Assert.That(actual.HeadCommit, Is.EqualTo(HeadCommit)); Assert.That(actual.Index, Is.EqualTo(Index), "The staged index is byte-for-byte unchanged.");
            Assert.That(actual.Worktree, Is.EqualTo(Worktree), "Tracked, unstaged and untracked worktree bytes and modes are preserved.");
        }
    }

    private sealed class FetchFixture : IDisposable
    {
        internal GitFixture Git { get; }
        internal GitRepository Repository => Git.Repository;
        internal string Source { get; }
        internal string SourceTip { get; private set; } = "";
        private string Trace { get; }
        internal byte[] ConfigBytes => File.ReadAllBytes(Git.PathInRepository(".git/config"));
        private FetchFixture(GitFixture git)
        {
            Git = git; Source = git.Output("bare source [literal] λ.git"); Trace = git.Output("fetch trace.jsonl");
            git.SetEnvironment("GIT_ALLOW_PROTOCOL", "file"); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
            git.SetEnvironment("GIT_TRACE2_EVENT", Trace);
        }
        internal static async Task<FetchFixture> CreateAsync()
        {
            FetchFixture fixture = new(await GitFixture.CreateAsync());
            try
            {
                string sourceWorktree = fixture.Git.Output("source worktree");
                await fixture.Git.GitAsync("clone", fixture.Repository.Path, sourceWorktree);
                await NativeAsync(sourceWorktree, "config", "user.name", "Pull request source tester");
                await NativeAsync(sourceWorktree, "config", "user.email", "pr-source@example.invalid");
                await NativeAsync(sourceWorktree, "config", "commit.gpgsign", "false");
                await NativeAsync(sourceWorktree, "config", "core.hooksPath", fixture.Git.Output("no-source-hooks"));
                await NativeAsync(sourceWorktree, "checkout", "-b", HeadRef);
                await File.WriteAllTextAsync(Path.Combine(sourceWorktree, "source-only.txt"), "Actual PR commit body Ω\n");
                await NativeAsync(sourceWorktree, "add", "source-only.txt"); await NativeAsync(sourceWorktree, "commit", "-m", "Actual source topic");
                await NativeAsync(sourceWorktree, "tag", "source-tag");
                fixture.SourceTip = (await NativeAsync(sourceWorktree, "rev-parse", "HEAD")).StandardOutput.Trim();
                await fixture.Git.GitAsync("clone", "--bare", sourceWorktree, fixture.Source);
                await fixture.Git.GitAsync("tag", "local-only");
                await File.WriteAllTextAsync(fixture.Git.File, "Staged local contents\n"); await fixture.Git.GitAsync("add", "file.txt");
                await File.WriteAllTextAsync(fixture.Git.File, "Unstaged local contents Ω\n");
                await File.WriteAllBytesAsync(fixture.Git.PathInRepository("untracked literal λ.bin"), [0, 1, 2, 127, 255]);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal async Task<RepositoryState> CaptureAsync()
        {
            string head = (await Git.GitAsync("rev-parse", "--verify", "HEAD")).StandardOutput.Trim();
            string[] material = Directory.EnumerateFiles(Repository.Path, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Relative: Path.GetRelativePath(Repository.Path, path)))
                .Where(pair => !pair.Relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderBy(pair => pair.Relative, StringComparer.Ordinal)
                .Select(pair => pair.Relative.Length + ":" + pair.Relative + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Path)))
                    + ":" + (OperatingSystem.IsWindows() ? File.GetAttributes(pair.Path).ToString() : File.GetUnixFileMode(pair.Path).ToString())).ToArray();
            return new(File.ReadAllBytes(Git.PathInRepository(".git/HEAD")), head,
                File.ReadAllBytes(Git.PathInRepository(".git/index")), string.Join('\n', material));
        }
        internal async Task<Dictionary<string, string>> RefsAsync()
            => (await Git.GitAsync("for-each-ref", "--format=%(refname)%00%(objectname)")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\0')).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        internal void ClearTrace() => File.WriteAllText(Trace, "");
        internal string[][] Commands()
        {
            if (!File.Exists(Trace)) { return []; }
            List<string[]> result = [];
            foreach (string line in File.ReadLines(Trace))
            {
                using JsonDocument json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("event", out JsonElement type) && type.GetString() == "start")
                { result.Add(json.RootElement.GetProperty("argv").EnumerateArray().Select(arg => arg.GetString()!).ToArray()); }
            }
            return result.ToArray();
        }
        private static async Task<GitResult> NativeAsync(string directory, params string[] arguments)
        {
            GitResult result = await new GitRunner().RunAsync(directory, arguments);
            Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        public void Dispose() => Git.Dispose();
    }
}
