using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitExtensions.Linux;

internal static class ReferenceSortingScenarios
{
    private const string LiteralBranch = "topic/été;$()'quoted";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        .. Enum.GetValues<GitRefsSortBy>().SelectMany(by => Enum.GetValues<GitRefsSortOrder>().Select(order =>
            ($"reference sorting {by} {order} matches native Windows keys for all refs and both GoTo lists", (Func<Task>)(() => NativeSortAsync(by, order))))),
        ("reference sorting applies version and descending order before the 100 and 1000 item bounds", BoundedListsAsync),
        ("reference sorting remains native in bare and SHA256 repositories and keeps raw annotated identities", BareAndSha256Async),
        ("reference priorities preserve stable case-sensitive anchored first-match buckets and literal keys", PrioritiesAsync),
        ("reference sorting rejects invalid modes and priority syntax without changing existing preferences or Git", ValidationAsync),
        ("reference sorting snapshots survive concurrent preference changes and cancelled reads drain without locking writes", SnapshotAndCancellationAsync),
    ];

    private static async Task NativeSortAsync(GitRefsSortBy by, GitRefsSortOrder order)
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository repository = f.Repository;
        string before = await Snapshot(f);
        GitReferenceSorting sorting = new(by, order); repository.ReferenceSorting = sorting;
        IReadOnlyList<GitReference> all = await repository.GetReferencesAsync();
        string[] expected = await NativeNames(repository, sorting, includeTags: true);
        Check(all.Select(reference => reference.Name).SequenceEqual(expected), $"All native references retain Windows {by}/{order} ordering.");
        foreach (GitGoToCommitReferenceKind kind in Enum.GetValues<GitGoToCommitReferenceKind>())
        {
            bool tags = kind == GitGoToCommitReferenceKind.Tag;
            IReadOnlyList<GitReference> actual = await repository.GetGoToCommitReferencesAsync(kind);
            Check(actual.Select(reference => reference.Name).SequenceEqual(await NativeNames(repository, sorting, tags, tags ? "refs/tags/" : "refs/heads/", 1000)),
                $"The independent bounded {kind} query uses Windows native {by}/{order} ordering.");
            Check(actual.All(reference => tags ? reference.IsTag : reference.IsBranch), "Tag/local lists never acquire remote references or the other reference type.");
            Check(actual.All(reference => all.Single(item => item.Name == reference.Name).Hash == reference.Hash), "Bounded lists retain the direct native object identity.");
        }
        GitReference tag = all.Single(reference => reference.Name == "refs/tags/v2");
        string raw = (await Git(repository, "rev-parse", "refs/tags/v2")).StandardOutput.Trim();
        string commit = (await Git(repository, "rev-parse", "refs/tags/v2^{commit}")).StandardOutput.Trim();
        Check(tag.Hash == raw && tag.RevisionHash == commit && raw != commit, "Sort keys never replace an annotated tag's raw object with its display target.");
        Check(all.Single(reference => reference.Name == "refs/heads/" + LiteralBranch).ShortName == LiteralBranch, "Reference names containing shell characters and Unicode remain literal.");
        Check(all.Single(reference => reference.Name == "refs/remotes/origin/HEAD").SymbolicTarget == "refs/remotes/origin/main", "The shared reader retains symbolic remote metadata.");
        Check(all.Single(reference => reference.Name == "refs/heads/z-topic").Upstream == "origin/main", "Native upstream ordering does not discard upstream metadata.");
        Check(await Snapshot(f) == before, "Every sorting read leaves HEAD, config, raw index, refs and all worktree bytes intact.");
    }

    private static async Task BoundedListsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository repository = f.Repository;
        string head = await repository.ResolveRevisionAsync("HEAD");
        StringBuilder updates = new();
        for (int index = 0; index < 1105; index++)
        {
            string name = index.ToString(CultureInfo.InvariantCulture);
            updates.Append("create refs/heads/version-").Append(name).Append(' ').Append(head).Append('\n');
            updates.Append("create refs/tags/version-").Append(name).Append(' ').Append(head).Append('\n');
        }
        GitResult update = await new GitRunner().RunAsync(repository.Path, ["update-ref", "--stdin"], standardInput: updates.ToString());
        Check(update.Success, update.StandardError);
        string before = await Snapshot(f);
        foreach (GitReferenceSorting sorting in new[]
        {
            new GitReferenceSorting(GitRefsSortBy.RefName, GitRefsSortOrder.Descending),
            new GitReferenceSorting(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Descending),
            new GitReferenceSorting(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Ascending),
        })
        {
            repository.ReferenceSorting = sorting;
            foreach (GitGoToCommitReferenceKind kind in Enum.GetValues<GitGoToCommitReferenceKind>())
            {
                bool tags = kind == GitGoToCommitReferenceKind.Tag;
                IReadOnlyList<GitReference> actual = await repository.GetGoToCommitReferencesAsync(kind);
                string[] expected = await NativeNames(repository, sorting, tags, tags ? "refs/tags/" : "refs/heads/", 1000);
                Check(actual.Count == 1000 && actual.Select(reference => reference.Name).SequenceEqual(expected), "Sort must precede the independent 1000-entry native limits.");
            }
            string[] firstHundred = (await repository.GetReferencesAsync()).Where(reference => reference.IsBranch).Take(100).Select(reference => reference.Name).ToArray();
            string[] expectedHundred = await NativeNames(repository, sorting, includeTags: false, "refs/heads/", 100);
            Check(firstHundred.SequenceEqual(expectedHundred), "The branch toolbar consumer's first100 comes from the native configured order.");
        }
        repository.ReferenceSorting = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Descending);
        Check((await repository.GetGoToCommitReferencesAsync(GitGoToCommitReferenceKind.Tag))[0].ShortName == "version-1104", "A late version beyond the alphabetical first1000 must still reach the front.");
        IReadOnlyList<GitReference> quick = await repository.GetQuickActionReferencesAsync(head);
        Check(quick.Count == 2211, "QuickRef action discovery remains unbounded; its desktop groups keep their separate cultural sort.");
        Check(await Snapshot(f) == before, "Bounded and unbounded sorted reads never mutate repository state.");
    }

    private static async Task BareAndSha256Async()
    {
        using RepositoryFixture f = await CreateAsync();
        string barePath = Path.Combine(f.Root, "bare repository.git"); await Git(f.Repository, "clone", "--bare", f.Repository.Path, barePath);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        bare.ReferenceSorting = new(GitRefsSortBy.CreatorDate, GitRefsSortOrder.Descending);
        Check((await bare.GetReferencesAsync()).Select(reference => reference.Name).SequenceEqual(await NativeNames(bare, bare.ReferenceSorting, includeTags: true)), "Reference ordering works in a bare repository without introducing worktree requirements.");
        string shaPath = Path.Combine(f.Root, "sha256"); Directory.CreateDirectory(shaPath); GitRepository sha = new(shaPath);
        await Git(sha, "init", "--object-format=sha256", "--initial-branch=main"); await RepositoryFixture.ConfigureAsync(sha);
        await Git(sha, "commit", "--allow-empty", "-m", "SHA256"); await Git(sha, "tag", "-a", "v2", "-m", "SHA256 annotated tag"); await Git(sha, "tag", "v10");
        sha.ReferenceSorting = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Ascending);
        IReadOnlyList<GitReference> refs = await sha.GetReferencesAsync(); GitReference tag = refs.Single(reference => reference.ShortName == "v2");
        Check(refs.Select(reference => reference.Name).SequenceEqual(await NativeNames(sha, sha.ReferenceSorting, includeTags: true)), "SHA256 ordering uses native sort keys rather than hash width assumptions.");
        Check(tag.Hash.Length == 64 && tag.RevisionHash?.Length == 64 && tag.Hash != tag.RevisionHash && tag.Sha1Id is null, "SHA256 retains raw and display identities without false SHA1 projections.");
    }

    private static Task PrioritiesAsync()
    {
        (string Name, int Id)[] input = [("feature/main", 0), ("Main", 1), ("release/2", 2), ("master", 3), ("main", 4), ("plain", 5), ("release/1", 6), ("master", 7)];
        IReadOnlyList<(string Name, int Id)> defaults = GitReferencePriorities.OrderByPriority(input, item => item.Name, GitReferencePriorities.DefaultBranchPatterns);
        Check(defaults.Select(item => item.Id).SequenceEqual([2, 3, 4, 6, 7, 0, 1, 5]), "The Windows default is one regex bucket; equal-priority and duplicate-name entries keep their native input order.");
        Check(GitReferencePriorities.OrderByPriority(input, item => item.Name, " master ; ; main ; release/.* ; ").Select(item => item.Id).SequenceEqual([3, 7, 4, 2, 6, 0, 1, 5]), "Semicolon priorities trim entries, ignore empty entries and use first whole-name match without case folding.");
        Check(GitReferencePriorities.OrderByPriority(input, item => item.Name, " ; ; ").SequenceEqual(input), "An empty priority list preserves every entry and its order.");
        Check(GitReferencePriorities.OrderByPriority(input, item => item.Name, "m.*;master").Select(item => item.Id).SequenceEqual([3, 4, 7, 0, 1, 2, 5, 6]), "Overlapping patterns use their first matching priority, not most-specific or last-match precedence.");
        string[] remotes = ["other", "upstream", "origin", "Origin", "origin/nested"];
        Check(GitReferencePriorities.OrderByPriority(remotes, name => name, GitReferencePriorities.DefaultRemotePatterns).SequenceEqual(["upstream", "origin", "other", "Origin", "origin/nested"]), "Remote priority uses the supplied full name, is case-sensitive and does not reorder equal matches.");
        string literal = "topic/a$()'quoted";
        Check(GitReferencePriorities.OrderByPriority(new[] { "other", literal, "prefix" + literal }, name => name, Regex.Escape(literal)).SequenceEqual([literal, "other", "prefix" + literal]), "Escaped metacharacters are literal and priority matching stays whole-name anchored.");
        Check(GitReferencePriorities.OrderByPriority(new[] { "feature/main", "release/release", "topic/topic", "release/other" }, name => name,
            @"(?<part>[^/]+)/\k<part>").SequenceEqual(["release/release", "topic/topic", "feature/main", "release/other"]), "Named captures and named backreferences keep Windows ExplicitCapture behavior.");
        return Task.CompletedTask;
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string before = await Snapshot(f);
        GitReferenceSorting saved = new(GitRefsSortBy.RefName, GitRefsSortOrder.Descending); f.Repository.ReferenceSorting = saved;
        Expect<ArgumentOutOfRangeException>(() => f.Repository.ReferenceSorting = new((GitRefsSortBy)999));
        Expect<ArgumentOutOfRangeException>(() => f.Repository.ReferenceSorting = new(Order: (GitRefsSortOrder)999));
        Expect<ArgumentNullException>(() => f.Repository.ReferenceSorting = null!);
        Check(f.Repository.ReferenceSorting == saved, "A rejected preference cannot replace the last valid snapshot.");
        Expect<ArgumentException>(() => GitReferencePriorities.Validate("main;["));
        Expect<ArgumentException>(() => GitReferencePriorities.OrderByPriority(new[] { "main" }, name => name, "main;["));
        GitReferencePriorities.Validate(" ;main[^/]*|master[^/]*|release/.*;origin|upstream; ");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => f.Repository.GetReferencesAsync(cancelled.Token));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.GetGoToCommitReferencesAsync(GitGoToCommitReferenceKind.Tag, cancelled.Token));
        await ExpectAsync<ArgumentOutOfRangeException>(() => f.Repository.GetGoToCommitReferencesAsync((GitGoToCommitReferenceKind)999));
        Check(await Snapshot(f) == before, "Invalid preferences, cancelled reads and invalid list kinds preserve all repository data.");
    }

    private static async Task SnapshotAndCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await CreateAsync();
        string wrapper = Path.Combine(f.Root, "gated git"), pidFile = Path.Combine(f.Root, "reader.pid"), released = Path.Combine(f.Root, "release"), argvFile = Path.Combine(f.Root, "argv");
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = for-each-ref ]; then\n  printf '%s\\000' \"$@\" > " + Quote(argvFile)
            + "\n  printf '%s' \"$$\" > " + Quote(pidFile) + "\n  while [ ! -f " + Quote(released) + " ]; do sleep 0.01; done\n  break\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper));
        foreach (GitGoToCommitReferenceKind? kind in new GitGoToCommitReferenceKind?[] { null, GitGoToCommitReferenceKind.LocalBranch, GitGoToCommitReferenceKind.Tag })
        {
            File.Delete(released); File.Delete(pidFile);
            GitReferenceSorting old = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Descending);
            guarded.ReferenceSorting = old; using CancellationTokenSource lifetime = new();
            Task<IReadOnlyList<GitReference>> pending = kind is null ? guarded.GetReferencesAsync(lifetime.Token) : guarded.GetGoToCommitReferencesAsync(kind.Value, lifetime.Token);
            try
            {
                await WaitFor(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, "The actual native reference reader reached the barrier.");
                string[] actualArguments = (await File.ReadAllTextAsync(argvFile)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                Check(actualArguments.Contains("--sort=-version:refname", StringComparer.Ordinal), "The in-flight native argv captured the old descending snapshot.");
                guarded.ReferenceSorting = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Ascending);
                await guarded.SetConfigAsync("sorting.probe", "writes-are-not-locked").WaitAsync(TimeSpan.FromSeconds(3));
                await File.WriteAllTextAsync(released, "release");
                IReadOnlyList<GitReference> actual = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                bool tags = kind != GitGoToCommitReferenceKind.LocalBranch;
                string? prefix = kind is null ? null : kind == GitGoToCommitReferenceKind.Tag ? "refs/tags/" : "refs/heads/";
                Check(actual.Select(reference => reference.Name).SequenceEqual(await NativeNames(f.Repository, old, tags, prefix, kind is null ? 0 : 1000)), "A preference changed during Git does not change the earlier read's ordering.");
                IReadOnlyList<GitReference> next = kind is null ? await guarded.GetReferencesAsync() : await guarded.GetGoToCommitReferencesAsync(kind.Value);
                Check(next.Select(reference => reference.Name).SequenceEqual(await NativeNames(f.Repository, guarded.ReferenceSorting, tags, prefix, kind is null ? 0 : 1000)), "The next read captures the newly configured order.");
                Check(!actual.Select(reference => reference.Name).SequenceEqual(next.Select(reference => reference.Name)), "The gate fixture genuinely distinguishes both sort directions.");
            }
            finally { lifetime.Cancel(); await File.WriteAllTextAsync(released, "drain"); try { await pending; } catch (OperationCanceledException) { } }
        }
        File.Delete(released); File.Delete(pidFile); using CancellationTokenSource cancel = new();
        Task<IReadOnlyList<GitReference>> blocked = guarded.GetGoToCommitReferencesAsync(GitGoToCommitReferenceKind.Tag, cancel.Token);
        try
        {
            await WaitFor(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, "The cancellation test has a live native reader.");
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
            cancel.Cancel(); await ExpectAsync<OperationCanceledException>(async () => await blocked.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(!Alive(pid), "Cancelling the sorted read drains its actual wrapper/Git process.");
        }
        finally { cancel.Cancel(); await File.WriteAllTextAsync(released, "drain"); try { await blocked; } catch (OperationCanceledException) { } }
    }

    private static async Task<RepositoryFixture> CreateAsync()
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            List<string> commits = [];
            for (int index = 0; index < 4; index++)
            {
                f.Write("seed.txt", new string((char)('a' + index), 20 + index * 80) + "\n"); await f.Repository.StageAllAsync();
                int[] authorYears = [2008, 2001, 2005, 2003], committerYears = [2002, 2007, 2004, 2006];
                await GitWithDates(f.Repository, ["commit", "-m", "Sorting commit " + index.ToString(CultureInfo.InvariantCulture) + new string('x', index * 70)], authorYears[index], committerYears[index]);
                commits.Add(await f.Repository.ResolveRevisionAsync("HEAD"));
            }
            await Git(f.Repository, "branch", "a-topic", commits[0]); await Git(f.Repository, "branch", "z-topic", commits[1]);
            await Git(f.Repository, "branch", LiteralBranch, commits[2]);
            await Git(f.Repository, "update-ref", "refs/remotes/origin/main", commits[1]);
            await Git(f.Repository, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
            await f.Repository.SetConfigAsync("remote.origin.url", Path.Combine(f.Root, "not fetched.git"));
            await f.Repository.SetConfigAsync("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
            await f.Repository.SetConfigAsync("branch.z-topic.remote", "origin"); await f.Repository.SetConfigAsync("branch.z-topic.merge", "refs/heads/main");
            await GitWithDates(f.Repository, ["tag", "-a", "v2", "-m", "Annotated version2", commits[0]], 2010, 2010);
            await Git(f.Repository, "tag", "v10", commits[3]);
            await GitWithDates(f.Repository, ["tag", "-a", "v20", "-m", "Long annotated version20 " + new string('t', 200), commits[2]], 2009, 2009);
            await GitWithDates(f.Repository, ["tag", "-a", "nested", "-m", "Nested native tag", "v2"], 2011, 2011);
            f.Write("seed.txt", "Dirty tracked bytes preserved\n"); f.Write("untracked '$() literal.txt", "untracked\n"); return f;
        }
        catch { f.Dispose(); throw; }
    }

    // Independent native oracle copied from Windows Commands.GetRefs, not the production helper.
    private static string[] WindowsSort(GitReferenceSorting sorting, bool includeTags)
    {
        string? key = sorting.By switch
        {
            GitRefsSortBy.GitDefault => null, GitRefsSortBy.AuthorDate => "authordate", GitRefsSortBy.CommitterDate => "committerdate",
            GitRefsSortBy.CreatorDate => "creatordate", GitRefsSortBy.TaggerDate => "taggerdate", GitRefsSortBy.RefName => "refname",
            GitRefsSortBy.VersionRefName => "version:refname", GitRefsSortBy.ObjectSize => "objectsize", GitRefsSortBy.Upstream => "upstream",
            _ => throw new InvalidOperationException("Invalid oracle criterion")
        };
        if (key is null) { return []; }
        string prefix = sorting.Order == GitRefsSortOrder.Ascending ? "" : "-";
        return includeTags ? ["--sort=" + prefix + "*" + (sorting.By == GitRefsSortBy.VersionRefName ? "refname" : key), "--sort=" + prefix + key] : ["--sort=" + prefix + key];
    }

    private static async Task<string[]> NativeNames(GitRepository repository, GitReferenceSorting sorting, bool includeTags, string? pattern = null, int count = 0)
    {
        List<string> arguments = ["--no-optional-locks", "for-each-ref", .. WindowsSort(sorting, includeTags), "--format=%(refname)"];
        if (count > 0) { arguments.Add("--count=" + count.ToString(CultureInfo.InvariantCulture)); }
        if (pattern is not null) { arguments.Add(pattern); }
        return (await Git(repository, arguments.ToArray())).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    { GitResult result = await new GitRunner().RunAsync(repository.Path, arguments); Check(result.Success, string.Join(' ', arguments) + "\n" + result.StandardError); return result; }
    private static async Task GitWithDates(GitRepository repository, IReadOnlyList<string> arguments, int authorYear, int committerYear)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, arguments, environment: new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_DATE"] = authorYear.ToString(CultureInfo.InvariantCulture) + "-01-02T03:04:05+00:00",
            ["GIT_COMMITTER_DATE"] = committerYear.ToString(CultureInfo.InvariantCulture) + "-01-02T03:04:05+00:00"
        }); Check(result.Success, result.StandardError);
    }
    private static async Task<string> Snapshot(RepositoryFixture f)
    {
        List<string> values = [(await Git(f.Repository, "show-ref", "--head")).StandardOutput];
        foreach (string path in Directory.EnumerateFiles(f.Repository.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(f.Repository.Path, path);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative is not ".git/HEAD" and not ".git/config" and not ".git/index") { continue; }
            values.Add(relative); values.Add(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        return string.Join('\0', values);
    }
    private static async Task WaitFor(Func<bool> ready, string message)
    { Stopwatch watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); } Check(ready(), message); }
    private static bool Alive(int pid)
    { try { string value = File.ReadAllText($"/proc/{pid}/stat"); return value[(value.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); } catch (IOException) { return false; } }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
