using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static class BranchFilterScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("custom branches combine local remote tag and full refs without changing single revision callers", MultipleReferencesAsync),
        ("branch exclusions and two or three dot ranges retain native reachability", ExclusionsAndRangesAsync),
        ("original branch wildcards preserve question bracket star and exact-name semantics", WildcardsAsync),
        ("open revision options retain selector exclusion not traversal and count ordering", RevisionOptionsAsync),
        ("Enter validates only simple references and preserves valid token order", ValidationAsync),
        ("invalid ranges and unknown revision options are Git errors rather than removed tokens", PassthroughErrorsAsync),
        ("a previously accepted deleted reference fails refresh without expanding all branches", DeletedReferenceAsync),
        ("custom and current scope include only the latest stash before revision options", LatestStashAsync),
        ("branch filters preserve canonical UTF8 bodies parents and literal path filters", CanonicalAndLiteralPathsAsync),
        ("branch option quote transport matches the original ProcessStartInfo Arguments without a shell", QuoteTransportAsync),
        ("presentation input output and external-tool options cannot corrupt or mutate history reads", IncompatibleOptionsAsync),
        ("empty filtered scope shows all and supports unborn and bare repositories", EmptyAndBareAsync),
        ("branch expressions retain native SHA256 commit identities", Sha256Async),
        ("branch validation reads and cancellation preserve checkout refs index config and bytes", ReadonlyAndCancellationAsync),
    ];

    private static async Task MultipleReferencesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string filter = "main\tfeature/a\nmirror/second  release/v1 refs/heads/feature/b";
        GitBranchFilterValidation validated = await f.Repository.ValidateBranchFilterAsync(filter);
        Check(validated.Filter == "main feature/a mirror/second release/v1 refs/heads/feature/b" && validated.InvalidTokens.Count == 0, "Whitespace is normalized, with no quoting-based phrase parser.");
        IReadOnlyList<GitCommit> commits = await Log(f.Repository, validated.Filter);
        Check(commits.Select(c => c.Hash).ToHashSet().SetEquals([f.RootCommit, f.Main, f.A, f.B]), "Every requested ref contributes; unrequested deep branch must stay absent.");
        await SameAsGit(f.Repository, validated.Filter, validated.Arguments);
        Check((await f.Repository.GetLogAsync(new GitLogQuery(Revision: "main", All: false))).Select(c => c.Hash).ToHashSet().SetEquals([f.RootCommit, f.Main]), "Ordinary single-revision callers stay strict and do not acquire custom-filter semantics.");
        await Expect<GitCommandException>(() => f.Repository.GetLogAsync(revision: "main feature/a"));
        await Expect<GitCommandException>(() => f.Repository.GetLogAsync(revision: "main..feature/a"));
        await Expect<ArgumentException>(() => f.Repository.GetLogAsync(new GitLogQuery(Revision: "main") { BranchFilter = "feature/a" }));
    }

    private static async Task ExclusionsAndRangesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        Check((await Log(f.Repository, "main feature/a ^main")).Select(c => c.Hash).SequenceEqual([f.A]), "^main excludes main and its ancestors after taking the union.");
        Check((await Log(f.Repository, "main..feature/a")).Select(c => c.Hash).SequenceEqual([f.A]), "Two-dot range excludes the left reachability set.");
        Check((await Log(f.Repository, "main...feature/a")).Select(c => c.Hash).ToHashSet().SetEquals([f.Main, f.A]), "Three-dot range uses the native symmetric difference.");
        foreach (string expression in new[] { "..feature/a", "feature/a..", "main...feature/a", "HEAD~1", "HEAD@{0}" })
        {
            GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(expression);
            Check(result.InvalidTokens.Count == 0, expression);
            await SameAsGit(f.Repository, result.Filter, result.Arguments);
        }
        // These revision-set shorthands are legitimate direct Git arguments, but the
        // original toolbar's single-commit RevParse check does not accept them at Enter.
        await SameAsGit(f.Repository, "main^!", ["main^!"]);
        await SameAsGit(f.Repository, "main^@", ["main^@"]);
    }

    private static async Task WildcardsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach ((string expression, string[] tips) in new[]
        {
            ("feature/*", new[] { f.A, f.B, f.Deep }),
            ("feature/?", new[] { f.A, f.B }),
            ("feature/[ab]", new[] { f.A, f.B }),
        })
        {
            GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(expression);
            Check(result.Arguments.SequenceEqual(["--branches=" + expression]) && result.InvalidTokens.Count == 0, "Only original ?/*/[ wildcard tokens become --branches= selectors.");
            Check((await Log(f.Repository, expression)).Select(c => c.Hash).ToHashSet().SetEquals(tips.Append(f.RootCommit)), expression);
            await SameAsGit(f.Repository, expression, result.Arguments);
        }
        GitBranchFilterValidation exact = await f.Repository.ValidateBranchFilterAsync("feature/a");
        Check(exact.Arguments.SequenceEqual(["feature/a"]), "An exact branch must not acquire Git's implicit /* suffix.");
        Check((await Log(f.Repository, exact.Filter)).Select(c => c.Hash).ToHashSet().SetEquals([f.A, f.RootCommit]), "The exact branch remains reachable.");
        foreach (string expression in new[] { "nothing*", "feature[", "^feature/*" })
        {
            GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(expression);
            Check(result.InvalidTokens.Count == 0 && result.Arguments.SequenceEqual(["--branches=" + expression]), "Preserve native wildcard/no-match and upstream caret-wildcard behavior, without inventing glob exclusion syntax.");
            await SameAsGit(f.Repository, expression, result.Arguments);
        }
    }

    private static async Task RevisionOptionsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string[] expressions = ["--remotes=mirror/*", "--tags=release/*", "--glob=refs/heads/feature/*", "--exclude=refs/heads/feature/* --all",
            "--all --not main", "--branches=feature/* --not main --not main", "--reverse --no-merges main feature/a", "--ancestry-path main...feature/a",
            "--boundary --left-right main...feature/a", "--cherry-pick main...feature/a", "--reflog --no-walk=sorted",
            "--date-order main feature/a", "--author-date-order main feature/a"];
        foreach (string expression in expressions)
        {
            GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(expression);
            Check(result.InvalidTokens.Count == 0, expression);
            await SameAsGit(f.Repository, result.Filter, result.Arguments);
        }
        IReadOnlyList<GitCommit> bounded = await f.Repository.GetLogAsync(new GitLogQuery(Count: 1) { BranchFilter = "--max-count=2 main" });
        Check(bounded.Count == 2, "Original custom --max-count occurs after the initial graph limit and keeps Git precedence.");
        IReadOnlyList<GitCommit> skip = await Log(f.Repository, "--skip=1 main");
        Check(skip.Select(c => c.Hash).SequenceEqual([f.RootCommit]), "Native option values keep their order and effect.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string text = " main\tmissing ^also-missing feature/a \n release/v1 --tags=release/* wildcard* main..missing ";
        GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(text);
        Check(result.InvalidTokens.Select(i => i.Token).SequenceEqual(["missing", "^also-missing"]), "Only the two simple missing refs produce individual warnings.");
        Check(result.Filter == "main feature/a release/v1 --tags=release/* wildcard* main..missing", "Removing a bad ref never loses its valid neighbours, options or range.");
        Check(result.ValidTokens.SequenceEqual(["main", "feature/a", "release/v1", "--tags=release/*", "wildcard*", "main..missing"]), "Preserve the original token order.");
        Check(result.Arguments.SequenceEqual(["main", "feature/a", "release/v1", "--tags=release/*", "--branches=wildcard*", "main..missing"]), "Range and explicit option never become --branches= patterns.");
        GitBranchFilterValidation cleaned = await f.Repository.ValidateBranchFilterAsync("main missing ^also-missing feature/a");
        await SameAsGit(f.Repository, cleaned.Filter, ["main", "feature/a"]);
        Check((await Log(f.Repository, cleaned.Filter)).Select(c => c.Hash).ToHashSet().SetEquals([f.Main, f.A, f.RootCommit]), "Warnings retain the valid result set.");
        GitBranchFilterValidation empty = await f.Repository.ValidateBranchFilterAsync("missing ^also-missing");
        Check(empty.Filter.Length == 0 && empty.InvalidTokens.Count == 2, "All invalid refs leave an explicitly empty accepted filter.");
        Check((await f.Repository.GetLogAsync(new GitLogQuery(All: false) { BranchFilter = empty.Filter })).Select(c => c.Hash).ToHashSet()
            .SetEquals((await f.Repository.GetLogAsync()).Select(c => c.Hash)), "Filtered with empty text shows all, independently of the old All flag.");
    }

    private static async Task PassthroughErrorsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (string expression in new[] { "main..non-existing", "non-existing...feature/a", "main --some_option" })
        {
            GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(expression);
            Check(result.InvalidTokens.Count == 0 && result.Filter == expression, "Original ApplyCustomBranchFilter bypasses range and --option validation.");
            await Expect<GitCommandException>(() => Log(f.Repository, result.Filter));
        }
        await Expect<GitCommandException>(() => Log(f.Repository, "a-direct-missing-ref"));
    }

    private static async Task DeletedReferenceAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Git(f.Repository, "branch", "accepted", f.Deep);
        GitBranchFilterValidation accepted = await f.Repository.ValidateBranchFilterAsync("accepted");
        IReadOnlyList<GitCommit> before = await Log(f.Repository, accepted.Filter);
        Check(before.Any(c => c.Hash == f.Deep) && before.All(c => c.Hash != f.Main), "The accepted scope is narrower than all refs.");
        await Git(f.Repository, "branch", "-D", "accepted");
        State state = await Capture(f.Repository);
        await Expect<GitCommandException>(() => Log(f.Repository, accepted.Filter));
        Check(await SameState(f.Repository, state), "A refresh error must not change repository state or silently expand the query.");
        GitBranchFilterValidation reapplied = await f.Repository.ValidateBranchFilterAsync(accepted.Filter);
        Check(reapplied.InvalidTokens.Single().Token == "accepted", "Only a later explicit Enter validates the disappeared ref again.");
    }

    private static async Task LatestStashAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitRepository repo = f.Repository;
        string main = await repo.ResolveRevisionAsync("HEAD");
        await Git(repo, "checkout", "--orphan", "old-stash-base"); await Git(repo, "rm", "-rf", ".");
        string oldBase = await Commit(f, "old.txt", "old root\n", "Old unrelated stash base");
        f.Write("old.txt", "old changed\n"); await repo.StashAsync("older stash", includeUntracked: false);
        string oldStash = await repo.ResolveRevisionAsync("refs/stash");
        await Git(repo, "checkout", "--orphan", "new-stash-base"); await Git(repo, "rm", "-rf", ".");
        string newBase = await Commit(f, "new.txt", "new root\n", "New unrelated stash base");
        string gap = await Commit(f, "gap.txt", "new ancestor gap\n", "Latest stash ancestor gap");
        f.Write("new.txt", "new stashed change\n"); await repo.StashAsync("latest stash", includeUntracked: false);
        string latest = await repo.ResolveRevisionAsync("refs/stash");
        await Git(repo, "checkout", "main"); await Git(repo, "branch", "-D", "old-stash-base", "new-stash-base");
        IReadOnlyList<GitCommit> current = await Log(repo, "HEAD");
        Check(current.Any(c => c.Hash == latest) && current.Any(c => c.Hash == newBase) && current.Any(c => c.Hash == gap), "Current/custom scope includes latest stash and its real ancestors even away from HEAD.");
        Check(current.Any(c => c.Hash == main) && current.All(c => c.Hash != oldStash && c.Hash != oldBase), "Older stash reflog entries and their unrelated histories are not automatically included.");
        Check((await repo.GetLogAsync(new GitLogQuery(Revision: "HEAD", All: false))).Select(c => c.Hash).SequenceEqual([main]), "Other callers' single Revision does not acquire stash inclusion.");
        Check((await repo.GetLogAsync(new GitLogQuery() { BranchFilter = "HEAD", IncludeLatestStashInBranchFilter = false })).Select(c => c.Hash).SequenceEqual([main]), "Explicit ShowStashes off is available without changing revision semantics.");
        string filter = "--exclude=refs/stash --all";
        await SameAsGit(repo, filter, ["--exclude=refs/stash", "--all"]);
        Check((await Log(repo, filter)).Any(c => c.Hash == latest), "Original latest stash glob is added before the custom --exclude and --all, not after them.");
    }

    private static async Task CanonicalAndLiteralPathsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = ":scope[*] λ.txt";
        f.Seed.Write(path, "literal content\n"); await f.Repository.StageAsync([path]); await f.Repository.CommitAsync("Exact[1] Café λ\n\nBody λ\nsecond line\n");
        await f.Repository.SetConfigAsync("color.ui", "always");
        GitLogQuery query = new(Path: path, Author: "Linux Test") { BranchFilter = "main feature/a", IncludeLatestStashInBranchFilter = false };
        IReadOnlyList<GitCommit> commits = await f.Repository.GetLogAsync(query);
        Check(commits.Count == 1 && commits[0].Subject == "Exact[1] Café λ" && commits[0].Body == "Body λ\nsecond line\n", "Canonical UTF8 framing retains exact multibyte subject/body despite color config and combined filters.");
        Check(commits[0].Parents.SequenceEqual([f.Main]), "Parent column remains canonical.");
        await SameAsGit(f.Repository, query.BranchFilter!, ["main", "feature/a"], path, includeStash: false);
        string literalRef = "literal$(id);echo_owned";
        await Git(f.Repository, "branch", literalRef, f.A);
        GitBranchFilterValidation literal = await f.Repository.ValidateBranchFilterAsync(literalRef);
        Check(literal.InvalidTokens.Count == 0 && literal.Arguments.Single() == literalRef, "Shell metacharacters remain one literal Git argument.");
        Check((await Log(f.Repository, literal.Filter)).Any(c => c.Hash == f.A), "Literal reference resolves through Git, not shell expansion.");
    }

    private static async Task QuoteTransportAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (string expression in new[] { "--fixed-strings --grep=\"Alpha[1]\" feature/a", "--fixed-strings --grep='Alpha[1]' feature/a",
            "--fixed-strings --grep=\\\"Alpha[1]\\\" feature/a", "--fixed-strings --grep=\"Al\"\"pha[1]\" feature/a", "--branches=\"feature/*\"" })
        {
            GitBranchFilterValidation result = await f.Repository.ValidateBranchFilterAsync(expression);
            IReadOnlyList<GitCommit> actual = await Log(f.Repository, result.Filter);
            string[] original = await OriginalArgumentsOracle(f.Repository, expression);
            Check(actual.Select(c => c.Hash).SequenceEqual(original), "Whitespace tokens retain original Arguments double-quote/backslash transport: " + expression);
        }
        GitBranchFilterValidation decoded = await f.Repository.ValidateBranchFilterAsync("--fixed-strings --grep=\"Alpha[1]\" feature/a");
        Check(decoded.Arguments.SequenceEqual(["--fixed-strings", "--grep=Alpha[1]", "feature/a"]), "Syntactic double quotes disappear from argv, not from the visible accepted expression.");
        Check((await Log(f.Repository, decoded.Filter)).Single().Hash == f.A, "The historical quoted option actually matches the commit subject.");
        await Expect<ArgumentException>(() => Log(f.Repository, "--out\"put\"=" + Path.Combine(f.Seed.Root, "should-not-exist")));
        await Expect<ArgumentException>(() => Log(f.Repository, "--grep=\"unclosed feature/a"));
    }

    private static async Task IncompatibleOptionsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private alternateRefsCommand canary requires a Linux executable."); }
        GitRepository alternate = await GitRepository.CloneAsync(f.Repository.Path, Path.Combine(f.Seed.Root, "alternate.git"),
            new GitCloneOptions(Bare: true, RecurseSubmodules: false));
        string alternatesFile = Path.Combine(f.Repository.Path, ".git/objects/info/alternates");
        Directory.CreateDirectory(Path.GetDirectoryName(alternatesFile)!);
        await File.WriteAllTextAsync(alternatesFile, Path.Combine(alternate.Path, "objects") + "\n");
        byte[] alternatesBefore = await File.ReadAllBytesAsync(alternatesFile);
        string alternateRefsBefore = (await Git(alternate, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        byte[] alternateConfigBefore = await File.ReadAllBytesAsync(Path.Combine(alternate.Path, "config"));
        string alternateFilesBefore = HashDirectory(alternate.Path);
        string marker = Path.Combine(f.Seed.Root, "alternate-command-executed"), helper = Path.Combine(f.Seed.Root, "alternate-command.py");
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nfrom pathlib import Path\nPath(" + JsonSerializer.Serialize(marker)
            + ").write_text('must not execute\\n')\nprint(" + JsonSerializer.Serialize(f.Main) + ")\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await f.Repository.SetConfigAsync("core.alternateRefsCommand", "'" + helper.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'");
        string output = Path.Combine(f.Seed.Root, "forbidden-output.txt");
        State before = await Capture(f.Repository);
        foreach (string option in new[] { "--output=" + output, "--out=" + output, "--o=" + output, "-o" + output, "--stdin", "--ext-diff", "--textconv",
            "--graph", "--gr", "--log-size", "--format=%s", "--pretty=raw", "--oneline", "--raw", "--patch-with-stat", "--name-only", "--name-status", "--word-diff", "--color=always",
            "--alternate-refs", "--alternate", "--alt", "--" })
        {
            await Expect<ArgumentException>(() => f.Repository.ValidateBranchFilterAsync("main " + option));
            await Expect<ArgumentException>(() => Log(f.Repository, "main " + option));
        }
        await Expect<ArgumentException>(() => Log(f.Repository, "main\0--output=" + output));
        Check(!File.Exists(output) && await SameState(f.Repository, before), "Output paths, tools, framing flags and abbreviated variants do not execute or alter any state.");
        Check(!File.Exists(marker) && (await File.ReadAllBytesAsync(alternatesFile)).SequenceEqual(alternatesBefore), "Even with actual alternate objects and a configured command, validate/log reject alternate-refs and its abbreviations before the canary can execute.");
        Check((await Git(alternate, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput == alternateRefsBefore
            && (await File.ReadAllBytesAsync(Path.Combine(alternate.Path, "config"))).SequenceEqual(alternateConfigBefore)
            && HashDirectory(alternate.Path) == alternateFilesBefore, "The alternate repository's refs, configuration and every stored file remain intact.");
        await SameAsGit(f.Repository, "--no-ext-diff --no-textconv --no-color main", ["--no-ext-diff", "--no-textconv", "--no-color", "main"]);
        Check(!File.Exists(marker), "An ordinary read-only history query also leaves the configured alternate command unexecuted.");
    }

    private static async Task EmptyAndBareAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string[] all = (await f.Repository.GetLogAsync()).Select(c => c.Hash).ToArray();
        Check((await f.Repository.GetLogAsync(new GitLogQuery(All: false) { BranchFilter = " \t\n" })).Select(c => c.Hash).SequenceEqual(all), "Filtered empty text follows --all even when its scope label remains Filtered.");
        GitRepository bare = await GitRepository.CloneAsync(f.Repository.Path, Path.Combine(f.Seed.Root, "bare.git"), new GitCloneOptions(Bare: true, RecurseSubmodules: false));
        await SameAsGit(bare, "main feature/a release/v1", ["main", "feature/a", "release/v1"]);
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        Check((await unborn.Repository.GetLogAsync(new GitLogQuery(All: false) { BranchFilter = "" })).Count == 0, "Unborn --all returns an empty graph.");
        Check((await unborn.Repository.ValidateBranchFilterAsync("HEAD")).InvalidTokens.Single().Token == "HEAD", "Explicit apply warns about missing unborn HEAD.");
    }

    private static async Task Sha256Async()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        string path = Path.Combine(f.Root, "sha256");
        GitResult initialized = await new GitRunner().RunAsync(f.Root, ["init", "--initial-branch=main", "--object-format=sha256", path]);
        Check(initialized.Success, initialized.StandardError);
        GitRepository repo = await GitRepository.OpenAsync(path); await RepositoryFixture.ConfigureAsync(repo);
        await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "SHA256 literal\n"); await repo.StageAllAsync(); await repo.CommitAsync("SHA256 initial");
        string hash = await repo.ResolveRevisionAsync("HEAD"); Check(hash.Length == 64, "The test uses actual SHA256 storage.");
        await Git(repo, "branch", "feature/sha", hash); await Git(repo, "tag", "sha-tag", hash);
        GitBranchFilterValidation result = await repo.ValidateBranchFilterAsync("main feature/sha sha-tag " + hash);
        Check(result.InvalidTokens.Count == 0 && (await Log(repo, result.Filter)).Single().Hash == hash, "Canonical framing and validation do not assume SHA1 object names.");
    }

    private static async Task ReadonlyAndCancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Seed.Write("seed.txt", "staged bytes λ\n"); await f.Repository.StageAsync(["seed.txt"]); f.Seed.Write("seed.txt", "staged bytes λ\nunstaged bytes\n"); f.Seed.Write("untracked [*].txt", "untracked literal\n");
        State before = await Capture(f.Repository);
        await f.Repository.ValidateBranchFilterAsync("main missing ^feature/a --tags=release/*");
        await Log(f.Repository, "main feature/a ^feature/b");
        using CancellationTokenSource cts = new(); cts.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.ValidateBranchFilterAsync("main", cts.Token));
        await Expect<OperationCanceledException>(() => f.Repository.GetLogAsync(new GitLogQuery() { BranchFilter = "main" }, cts.Token));
        Check(await SameState(f.Repository, before), "Read-only queries, invalid warnings and cancellation preserve tracked/untracked bytes, index stages, refs, checkout and local config.");
    }

    private static Task<IReadOnlyList<GitCommit>> Log(GitRepository repo, string expression)
        => repo.GetLogAsync(new GitLogQuery() { BranchFilter = expression });

    private static async Task SameAsGit(GitRepository repo, string expression, IEnumerable<string> arguments, string? path = null, bool includeStash = true)
    {
        List<string> args = ["--no-pager", "-c", "color.ui=false", "--literal-pathspecs", "log", "--encoding=UTF-8", "--topo-order", "-z", "--format=%H", "--max-count=100"];
        if (includeStash) { args.Add("--glob=refs/stas[h]"); }
        args.AddRange(arguments); args.Add("--"); if (path is not null) { args.Add(path); }
        GitResult oracle = await new GitRunner().RunAsync(repo.Path, args); Check(oracle.Success, oracle.StandardError);
        IReadOnlyList<GitCommit> actual = await repo.GetLogAsync(new GitLogQuery(Path: path) { BranchFilter = expression, IncludeLatestStashInBranchFilter = includeStash });
        Check(actual.Select(c => c.Hash).SequenceEqual(oracle.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)), "Real Git ordering/reachability must match the original argument vector: " + expression);
    }

    private static async Task<string[]> OriginalArgumentsOracle(GitRepository repo, string expression)
    {
        ProcessStartInfo start = new("git")
        {
            Arguments = "--no-pager -c color.ui=false log --encoding=UTF-8 --topo-order -z --format=%H --max-count=100 --glob=refs/stas[h] " + expression + " --",
            WorkingDirectory = repo.Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Check(process.ExitCode == 0, await errors);
        return (await output).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<GitResult> Git(GitRepository repo, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(repo.Path, ["--no-pager", "-c", "color.ui=false", .. arguments]);
        Check(result.Success, string.Join(' ', arguments) + "\n" + result.StandardError); return result;
    }

    private static async Task<string> Commit(RepositoryFixture f, string path, string content, string message)
    {
        f.Write(path, content); await f.Repository.StageAsync([path]); await f.Repository.CommitAsync(message); return await f.Repository.ResolveRevisionAsync("HEAD");
    }

    private sealed record State(string Head, string Refs, string Status, string Index, string Config, IReadOnlyDictionary<string, string> Files);
    private static async Task<State> Capture(GitRepository repo)
    {
        string status = (await Git(repo, "status", "--porcelain=v2", "-z")).StandardOutput;
        Dictionary<string, string> files = Directory.EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(repo.Path, path).Split(Path.DirectorySeparatorChar).Contains(".git"))
            .ToDictionary(path => Path.GetRelativePath(repo.Path, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
        return new(await repo.ResolveRevisionAsync("HEAD"), (await Git(repo, "for-each-ref", "--format=%(refname) %(objectname) %(symref)")).StandardOutput,
            status, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo.Path, ".git/index")))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo.Path, ".git/config")))), files);
    }
    private static async Task<bool> SameState(GitRepository repo, State before) => JsonSerializer.Serialize(await Capture(repo)) == JsonSerializer.Serialize(before);
    private static string HashDirectory(string root) => JsonSerializer.Serialize(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal));
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture seed, string root, string main, string a, string b, string deep)
        { Seed = seed; RootCommit = root; Main = main; A = a; B = b; Deep = deep; }
        public RepositoryFixture Seed { get; }
        public GitRepository Repository => Seed.Repository;
        public string RootCommit { get; }
        public string Main { get; }
        public string A { get; }
        public string B { get; }
        public string Deep { get; }
        public static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture seed = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository repo = seed.Repository; string root = await repo.ResolveRevisionAsync("HEAD");
                string main = await Commit(seed, "main.txt", "main history\n", "Main branch");
                await Git(repo, "checkout", "-b", "feature/a", root);
                string a = await Commit(seed, "a.txt", "alpha\n", "Alpha[1] λ\n\nCafé body\n");
                await Git(repo, "checkout", "-b", "feature/b", root);
                string b = await Commit(seed, "b.txt", "beta\n", "Beta branch");
                await Git(repo, "checkout", "-b", "feature/deep/tip", root);
                string deep = await Commit(seed, "deep.txt", "deep\n", "Deep branch");
                await Git(repo, "checkout", "main");
                await Git(repo, "update-ref", "refs/remotes/mirror/topic", a); await Git(repo, "update-ref", "refs/remotes/mirror/second", b);
                await Git(repo, "tag", "-a", "release/v1", a, "-m", "Annotated alpha tag"); await Git(repo, "tag", "release/b", b);
                return new(seed, root, main, a, b, deep);
            }
            catch { seed.Dispose(); throw; }
        }
        public void Dispose() => Seed.Dispose();
    }
}
