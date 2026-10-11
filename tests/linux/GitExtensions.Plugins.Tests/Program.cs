using System.Globalization;
using GitExtensions.Linux;
using GitExtensions.Linux.Plugins;

// Exercise actual Git repositories and local bare remotes; no network or test adapter is needed.
Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null");
Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");

(string Name, Func<Task> Run)[] tests =
[
    ("create tracking branches, preserve local branches and skip symbolic remote HEAD", TrackingBranchesAsync),
    ("tracking selection is validated before creating branches", TrackingSelectionAsync),
    ("obsolete preview shows merged/current/reference/age exclusions without mutations", BranchPreviewAsync),
    ("branch deletion requires confirmation and validates the entire selection", BranchDeletionAsync),
    ("unmerged forced deletion is an explicit opt-in", UnmergedDeletionAsync),
    ("remote preview filters its remote and confirmed deletion changes a real bare remote", RemoteDeletionAsync),
    ("remote deletion preserves a branch updated after the last fetch", RemoteChangedAsync),
    ("large blobs preserve literal paths, exact threshold and distinct commit counts", LargeFilesAsync),
    ("packed blob sizes and selected revision history are read from Git", PackedFilesAsync),
    ("release notes honour range, order, Unicode bodies and HTML escaping", ReleaseNotesAsync),
    ("invalid ranges and cancelled execution leave the repository untouched", InvalidAndCancelledAsync),
];
int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + name + "\n" + exception);
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} plugin scenarios passed.");
return failures == 0 ? 0 : 1;

static Dictionary<string, string?> Values(params (string Key, string Value)[] entries)
    => entries.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.Ordinal);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Cell(PluginResult result, string key, string column)
{
    PluginTable table = result.Table ?? throw new InvalidOperationException("Missing result table.");
    int index = table.Columns.ToList().IndexOf(column);
    return table.Rows.Single(row => row.Key == key).Cells[index];
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

static async Task<bool> HasBranchAsync(GitRepository repository, string name)
    => (await repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/" + name);

static async Task TrackingBranchesAsync()
{
    using Fixture fixture = await Fixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("feature/été", checkout: false);
    await fixture.Repository.CreateBranchAsync("existing", checkout: false);
    GitRepository clone = await fixture.CloneRemoteAsync();
    await fixture.CommitAtAsync(clone, "Local main differs from remote", Fixture.Now);
    string existingHash = await clone.ResolveRevisionAsync("HEAD");
    await clone.CreateBranchAsync("existing", checkout: false);
    CreateLocalBranchesPlugin plugin = new();
    PluginResult result = await plugin.ExecuteAsync(new(clone), Values());
    Check(result.Success && result.RepositoryChanged, "Tracking creation did not report a real change.");
    Check(await HasBranchAsync(clone, "feature/été"), "Unicode tracking branch missing.");
    Check((await clone.GetStatusAsync()).Branch == "main", "Plugin checked out a created branch.");
    Check(await clone.ResolveRevisionAsync("existing") == existingHash, "Existing local branch was changed.");
    Check((await clone.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/feature/été").Upstream == "origin/feature/été", "Upstream was not configured.");
    Check(!await HasBranchAsync(clone, "HEAD"), "Remote symbolic HEAD became a local branch.");
    PluginResult repeated = await plugin.ExecuteAsync(new(clone), Values());
    Check(!repeated.RepositoryChanged && repeated.Success, "Repeated creation should preserve existing branches.");
}

static async Task TrackingSelectionAsync()
{
    using Fixture fixture = await Fixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("one", checkout: false);
    await fixture.Repository.CreateBranchAsync("two", checkout: false);
    GitRepository clone = await fixture.CloneRemoteAsync();
    CreateLocalBranchesPlugin plugin = new();
    await ThrowsAsync<ArgumentException>(() => plugin.ExecuteAsync(new(clone), Values(("branches", "origin/one\nmissing"))));
    Check(!await HasBranchAsync(clone, "one"), "Invalid selection partly created branches.");
    await plugin.ExecuteAsync(new(clone), Values(("branches", "refs/remotes/origin/one")));
    Check(await HasBranchAsync(clone, "one") && !await HasBranchAsync(clone, "two"), "Explicit tracking selection was ignored.");
}

static async Task BranchPreviewAsync()
{
    using Fixture fixture = await Fixture.CreateBranchesAsync();
    DeleteUnusedBranchesPlugin plugin = new();
    string before = await fixture.AllRefsAsync();
    PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository, Now: Fixture.Now), Values(("mergedInto", "reference")));
    Check(!result.RepositoryChanged && before == await fixture.AllRefsAsync(), "Preview changed references.");
    Check(Cell(result, "refs/heads/old/topic", "Status") == "Suggested", "Old merged branch not suggested.");
    Check(Cell(result, "refs/heads/recent-copy", "Status") == "Eligible", "Age incorrectly forbids manual selection.");
    Check(Cell(result, "refs/heads/main", "Reason") == "Current branch.", "Current branch not excluded.");
    Check(Cell(result, "refs/heads/reference", "Reason") == "Reference branch.", "Merge reference not excluded.");
    Check(Cell(result, "refs/heads/unmerged", "Reason").Contains("Not merged", StringComparison.Ordinal), "Unmerged branch not excluded.");
    result = await plugin.ExecuteAsync(new(fixture.Repository, Now: Fixture.Now), Values(("mergedInto", "reference"), ("pattern", "^OLD/"), ("ignoreCase", "true")));
    Check(Cell(result, "refs/heads/old/topic", "Status") == "Suggested", "Case-insensitive regex filter changed source semantics.");
    Check(Cell(result, "refs/heads/recent-copy", "Status") == "Excluded", "Regex did not exclude nonmatching branch.");
    result = await plugin.ExecuteAsync(new(fixture.Repository, Now: Fixture.Now), Values(("mergedInto", "reference"), ("pattern", "^old/"), ("invertPattern", "true")));
    Check(Cell(result, "refs/heads/old/topic", "Status") == "Excluded", "Inverted regex not applied.");
    Check(!plugin.Descriptor.NeedsConfirmation(Values()), "Preview unnecessarily requires mutation confirmation.");
    Check(plugin.Descriptor.NeedsConfirmation(Values(("operation", "delete"))), "Deletion confirmation missing from descriptor.");
}

static async Task BranchDeletionAsync()
{
    using Fixture fixture = await Fixture.CreateBranchesAsync();
    DeleteUnusedBranchesPlugin plugin = new();
    Dictionary<string, string?> values = Values(("operation", "delete"), ("mergedInto", "reference"), ("branches", "old/topic"));
    await ThrowsAsync<InvalidOperationException>(() => plugin.ExecuteAsync(new(fixture.Repository), values));
    Check(await HasBranchAsync(fixture.Repository, "old/topic"), "Unconfirmed deletion mutated a branch.");
    await ThrowsAsync<ArgumentException>(() => plugin.ExecuteAsync(new(fixture.Repository, Confirmed: true), Values(("operation", "delete"), ("mergedInto", "reference"), ("branches", "old/topic\nmain"))));
    Check(await HasBranchAsync(fixture.Repository, "old/topic"), "Invalid batch partly deleted branches.");
    PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository, Confirmed: true), values);
    Check(result.Success && result.RepositoryChanged && !await HasBranchAsync(fixture.Repository, "old/topic"), "Confirmed merged deletion failed.");
    Check(await HasBranchAsync(fixture.Repository, "unmerged") && await HasBranchAsync(fixture.Repository, "main"), "Excluded branches were deleted.");
    result = await plugin.ExecuteAsync(new(fixture.Repository, Confirmed: true), Values(("operation", "delete"), ("mergedInto", "reference"), ("branches", "recent-copy")));
    Check(result.Success && !await HasBranchAsync(fixture.Repository, "recent-copy"), "Explicit newer eligible selection should work like the original checkbox.");
}

static async Task UnmergedDeletionAsync()
{
    using Fixture fixture = await Fixture.CreateBranchesAsync();
    DeleteUnusedBranchesPlugin plugin = new();
    await ThrowsAsync<ArgumentException>(() => plugin.ExecuteAsync(new(fixture.Repository, Confirmed: true), Values(("operation", "delete"), ("branches", "unmerged"))));
    Check(await HasBranchAsync(fixture.Repository, "unmerged"), "Default deletion forced an unmerged branch.");
    PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository, Confirmed: true), Values(("operation", "delete"), ("includeUnmerged", "true"), ("branches", "unmerged")));
    Check(result.Success && !await HasBranchAsync(fixture.Repository, "unmerged"), "Opted-in forced branch deletion failed.");
}

static async Task RemoteDeletionAsync()
{
    using Fixture fixture = await Fixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("obsolete", checkout: false);
    GitRepository clone = await fixture.CloneRemoteAsync();
    await clone.AddRemoteAsync("other", fixture.RemotePath);
    await clone.FetchAsync("other", prune: false);
    DeleteUnusedBranchesPlugin plugin = new();
    PluginResult preview = await plugin.ExecuteAsync(new(clone), Values(("remoteBranches", "true")));
    Check(Cell(preview, "refs/remotes/other/obsolete", "Reason") == "Different remote.", "Preview includes another remote.");
    Check(Cell(preview, "refs/remotes/origin/HEAD", "Reason") == "Symbolic reference.", "Symbolic remote HEAD is eligible.");
    Check(Cell(preview, "refs/heads/main", "Status") == "Excluded", "Remote mode includes a local branch.");
    PluginResult result = await plugin.ExecuteAsync(new(clone, Confirmed: true), Values(("operation", "delete"), ("remoteBranches", "true"), ("branches", "origin/obsolete")));
    GitResult remote = await fixture.Runner.RunAsync(fixture.RemotePath, ["show-ref", "--verify", "refs/heads/obsolete"]);
    Check(result.Success && result.RepositoryChanged && !remote.Success, "Bare remote branch was not deleted.");
    Check(await HasBranchAsync(fixture.Repository, "obsolete"), "Remote deletion changed source local branch.");
}

static async Task RemoteChangedAsync()
{
    using Fixture fixture = await Fixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("obsolete", checkout: false);
    GitRepository clone = await fixture.CloneRemoteAsync();
    string advanced = await fixture.CommitAtAsync(fixture.Repository, "Remote advances after clone", Fixture.Now);
    GitResult pushed = await fixture.Runner.RunAsync(fixture.Repository.Path,
        ["push", "--", fixture.RemotePath, "HEAD:refs/heads/obsolete"]);
    Check(pushed.Success, "Failed to advance the bare remote fixture.");
    PluginResult result = await new DeleteUnusedBranchesPlugin().ExecuteAsync(new(clone, Confirmed: true),
        Values(("operation", "delete"), ("remoteBranches", "true"), ("branches", "origin/obsolete")));
    GitResult actual = await fixture.Runner.RunAsync(fixture.RemotePath, ["rev-parse", "refs/heads/obsolete"]);
    Check(!result.Success && !result.RepositoryChanged && actual.StandardOutput.Trim() == advanced,
        "Deletion removed a remote branch whose fetched tip was stale.");
    Check(Cell(result, "refs/remotes/origin/obsolete", "Status") == "Failed", "Lease rejection was not exposed to the user.");
}

static async Task LargeFilesAsync()
{
    using Fixture fixture = await Fixture.CreateBlobsAsync();
    FindLargeFilesPlugin plugin = new();
    string refs = await fixture.AllRefsAsync();
    PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository), Values(("minimumMegabytes", "0.00390625")));
    Check(result.Table!.Rows.Count == 2, "Exact 4096-byte boundary should include two distinct large blobs.");
    PluginRow original = result.Table.Rows.Single(row => row.Cells[2] == "4096");
    Check(original.Cells[1] == Fixture.LiteralPath, "NUL-delimited tree path lost tabs, newlines, quotes or Unicode.");
    Check(original.Cells[4] == "2", "The same blob in two paths counted a commit twice.");
    Check(original.Cells[3] == "Unknown", "Loose blob should not invent compressed size.");
    GitResult size = await fixture.Repository.RunAsync(["cat-file", "-s", original.Key]);
    Check(size.StandardOutput.Trim() == original.Cells[2], "Reported blob size disagrees with Git.");
    Check(original.Cells[5].StartsWith("2020-", StringComparison.Ordinal), "Last date was not the newest commit containing the blob.");
    Check(!result.RepositoryChanged && refs == await fixture.AllRefsAsync() && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Analysis changed repository state.");
}

static async Task PackedFilesAsync()
{
    using Fixture fixture = await Fixture.CreateBlobsAsync();
    await fixture.Repository.RunAsync(["gc", "--prune=now"]);
    FindLargeFilesPlugin plugin = new();
    PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository), Values(("minimumMegabytes", "0.00390625")));
    Check(result.Table!.Rows.All(row => long.TryParse(row.Cells[3], out long compressed) && compressed > 0), "verify-pack compressed sizes were not populated.");
    result = await plugin.ExecuteAsync(new(fixture.Repository), Values(("minimumMegabytes", "0.00390625"), ("revision", fixture.FirstBlobCommit!)));
    Check(result.Table!.Rows.Count == 1 && result.Table.Rows[0].Cells[4] == "1", "Selected revision included later blobs or commits.");
}

static async Task ReleaseNotesAsync()
{
    using Fixture fixture = await Fixture.CreateAsync();
    string from = await fixture.Repository.ResolveRevisionAsync("HEAD");
    string first = await fixture.CommitAtAsync(fixture.Repository, "First élève <tag>\n\nBody α\nsecond line", Fixture.Now.AddDays(-2));
    string second = await fixture.CommitAtAsync(fixture.Repository, "Second & final\n\nFinal body", Fixture.Now.AddDays(-1));
    ReleaseNotesGeneratorPlugin plugin = new();
    PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository), Values(("fromRevision", from)));
    Check(result.Table!.Rows.Count == 2 && second.StartsWith(result.Table.Rows[0].Key, StringComparison.Ordinal) && first.StartsWith(result.Table.Rows[1].Key, StringComparison.Ordinal), "Release range/order incorrect.");
    Check(result.Text.Contains("élève", StringComparison.Ordinal) && result.Text.Contains("Body α", StringComparison.Ordinal), "Unicode subject or body lost.");
    result = await plugin.ExecuteAsync(new(fixture.Repository), Values(("fromRevision", from), ("toRevision", first), ("format", "html")));
    Check(result.Table!.Rows.Count == 1 && result.Text.Contains("&lt;tag&gt;", StringComparison.Ordinal) && !result.Text.Contains("<tag>", StringComparison.Ordinal), "HTML export unescaped or incorrect range.");
    result = await plugin.ExecuteAsync(new(fixture.Repository), Values(("fromRevision", second), ("toRevision", second), ("format", "tsv")));
    Check(result.Table!.Rows.Count == 0 && !result.RepositoryChanged, "Empty range created notes or mutations.");
}

static async Task InvalidAndCancelledAsync()
{
    using Fixture fixture = await Fixture.CreateAsync();
    string refs = await fixture.AllRefsAsync();
    ReleaseNotesGeneratorPlugin release = new();
    await ThrowsAsync<GitCommandException>(() => release.ExecuteAsync(new(fixture.Repository), Values(("fromRevision", "HEAD; touch sentinel"))));
    Check(!File.Exists(Path.Combine(fixture.Repository.Path, "sentinel")), "Revision field was interpreted by a shell.");
    using CancellationTokenSource cancellation = new();
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => new FindLargeFilesPlugin().ExecuteAsync(new(fixture.Repository), Values(), cancellation.Token));
    Check(refs == await fixture.AllRefsAsync(), "Invalid/cancelled operations changed refs.");
}

sealed class Fixture : IDisposable
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00+00:00", CultureInfo.InvariantCulture);
    internal const string LiteralPath = "a ü\n'quote\t.bin";
    private Fixture(string root, GitRepository repository) { Root = root; Repository = repository; }
    internal string Root { get; }
    internal GitRepository Repository { get; }
    internal GitRunner Runner { get; } = new();
    internal string RemotePath => Path.Combine(Root, "bare remote.git");
    internal string? FirstBlobCommit { get; private set; }

    internal static async Task<Fixture> CreateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "gitextensions plugin " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        GitRepository repository = await GitRepository.InitAsync(Path.Combine(root, "repository"));
        Fixture fixture = new(root, repository);
        await repository.SetConfigAsync("user.name", "Plugin Test");
        await repository.SetConfigAsync("user.email", "plugin-test@example.invalid");
        await repository.SetConfigAsync("commit.gpgsign", "false");
        await repository.SetConfigAsync("core.hooksPath", "/dev/null");
        await fixture.CommitAtAsync(repository, "Base", DateTimeOffset.Parse("2000-01-01T12:00:00+00:00", CultureInfo.InvariantCulture));
        return fixture;
    }

    internal static async Task<Fixture> CreateBranchesAsync()
    {
        Fixture fixture = await CreateAsync();
        await fixture.Repository.CreateBranchAsync("old/topic", checkout: false);
        await fixture.Repository.CreateBranchAsync("unmerged", checkout: true);
        File.WriteAllText(Path.Combine(fixture.Repository.Path, "unmerged.txt"), "unmerged");
        await fixture.CommitAtAsync(fixture.Repository, "Unmerged", Now.AddDays(-10));
        await fixture.Repository.CheckoutAsync("main");
        File.WriteAllText(Path.Combine(fixture.Repository.Path, "main.txt"), "main");
        await fixture.CommitAtAsync(fixture.Repository, "Recent main", Now);
        await fixture.Repository.CreateBranchAsync("recent-copy", checkout: false);
        await fixture.Repository.CreateBranchAsync("reference", checkout: false);
        return fixture;
    }

    internal static async Task<Fixture> CreateBlobsAsync()
    {
        Fixture fixture = await CreateAsync();
        byte[] content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fixture.Repository.Path, LiteralPath), content);
        File.WriteAllBytes(Path.Combine(fixture.Repository.Path, "z duplicate.bin"), content);
        fixture.FirstBlobCommit = await fixture.CommitAtAsync(fixture.Repository, "First blobs", DateTimeOffset.Parse("2001-01-01T12:00:00+00:00", CultureInfo.InvariantCulture));
        File.WriteAllBytes(Path.Combine(fixture.Repository.Path, "second.bin"), Enumerable.Repeat((byte)42, 8192).ToArray());
        File.WriteAllText(Path.Combine(fixture.Repository.Path, "small.txt"), "small");
        await fixture.CommitAtAsync(fixture.Repository, "Second blobs", DateTimeOffset.Parse("2020-01-01T12:00:00+00:00", CultureInfo.InvariantCulture));
        return fixture;
    }

    internal async Task<GitRepository> CloneRemoteAsync()
    {
        GitResult result = await Runner.RunAsync(Root, ["clone", "--bare", Repository.Path, RemotePath]);
        if (!result.Success) throw new InvalidOperationException(result.StandardError);
        GitRepository clone = await GitRepository.CloneAsync(RemotePath, Path.Combine(Root, "clone"), recursive: false);
        await clone.SetConfigAsync("user.name", "Plugin Test");
        await clone.SetConfigAsync("user.email", "plugin-test@example.invalid");
        await clone.SetConfigAsync("commit.gpgsign", "false");
        await clone.SetConfigAsync("core.hooksPath", "/dev/null");
        return clone;
    }

    internal async Task<string> CommitAtAsync(GitRepository repository, string message, DateTimeOffset date)
    {
        await repository.StageAllAsync();
        string timestamp = date.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        GitResult result = await Runner.RunAsync(repository.Path,
            ["commit", "--allow-empty", "--cleanup=verbatim", "-m", message], environment:
            new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = timestamp, ["GIT_COMMITTER_DATE"] = timestamp });
        if (!result.Success) throw new InvalidOperationException(result.StandardError);
        return await repository.ResolveRevisionAsync("HEAD");
    }

    internal async Task<string> AllRefsAsync() => (await Repository.RunAsync(["show-ref"])).StandardOutput;
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
