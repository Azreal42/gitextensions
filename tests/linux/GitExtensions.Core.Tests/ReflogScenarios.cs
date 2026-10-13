using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static class ReflogScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("complete reflogs exceed100 while legacy and explicit limits retain ordinal selectors", CompleteHistoryAsync),
        ("reflog choices distinguish local and real fetched remote references", LocalAndRemoteAsync),
        ("literal Unicode quote and shell-looking reflog names select only their own journal", LiteralReferencesAsync),
        ("unborn absent journals and populated bare reflogs preserve native empty and error semantics", EmptyAndBareAsync),
        ("complete reflogs retain UTF8 selectors and mixed UTF8 CP1252 Latin1 subjects", EncodingsAsync),
        ("both reflog overloads reject invalid limits and reference arguments before effects", InvalidArgumentsAsync),
        ("complete reflog cancellation stops binary readers and descendants without effects", CancellationAsync),
        ("complete reflog reads preserve detached checkout dirty index worktree refs config and objects", ReadonlyAsync),
    ];

    private static async Task CompleteHistoryAsync()
    {
        using RepositoryFixture fixture = await TwoCommitsAsync();
        GitRepository repository = fixture.Repository;
        string tip = await repository.ResolveRevisionAsync("HEAD"), previous = await repository.ResolveRevisionAsync("HEAD^");
        const string reference = "refs/heads/history/complete";
        await Git(repository, "update-ref", "--create-reflog", "-m", "initial history", reference, previous);
        for (int index = 1; index <= 137; index++)
        {
            await Git(repository, "update-ref", "-m", "real transition " + index.ToString(CultureInfo.InvariantCulture),
                reference, index % 2 == 1 ? tip : previous);
        }

        string before = Materials(repository.Path);
        IReadOnlyList<GitReflogEntry> complete = await repository.GetReflogAsync(count: null, reference: reference);
        IReadOnlyList<GitReflogEntry> legacy = await repository.GetReflogAsync(reference: reference);
        IReadOnlyList<GitReflogEntry> limited = await repository.GetReflogAsync(7, reference);
        IReadOnlyList<GitReflogEntry> nullableLimit = await repository.GetReflogAsync((int?)7, reference);
        Check(complete.Count == 138 && legacy.Count == 100, "The complete native journal must not inherit the default100 cap.");
        Check(legacy.SequenceEqual(complete.Take(100)) && limited.SequenceEqual(complete.Take(7)) && nullableLimit.SequenceEqual(limited),
            "Only the requested count changes; the legacy positive-count overload stays compatible.");
        Check(complete.Select(entry => entry.Selector).Distinct(StringComparer.Ordinal).Count() == complete.Count,
            "Repeated hashes still represent distinct ordinal reflog records.");
        for (int ordinal = 0; ordinal < complete.Count; ordinal++)
        {
            Check(complete[ordinal].Selector.EndsWith("@{" + ordinal.ToString(CultureInfo.InvariantCulture) + "}", StringComparison.Ordinal),
                "Selectors are native ordinal identities, not dates or synthesized UI rows.");
            Check(complete[ordinal].Hash == await repository.ResolveRevisionAsync(complete[ordinal].Selector),
                "Every selector resolves to its own actual historical hash.");
        }
        await MatchesGit(repository, reference, complete);
        await MatchesGit(repository, reference, legacy, 100);
        Check(before == Materials(repository.Path), "Reading and resolving the complete journal changes no repository bytes or modes.");
    }

    private static async Task LocalAndRemoteAsync()
    {
        using RepositoryFixture fixture = await TwoCommitsAsync();
        GitRepository repository = fixture.Repository;
        string tip = await repository.ResolveRevisionAsync("HEAD"), previous = await repository.ResolveRevisionAsync("HEAD^");
        const string local = "refs/heads/topic/é", remoteBranch = "refs/heads/topic/remote", tracking = "refs/remotes/mirror/topic/remote";
        await Git(repository, "update-ref", "--create-reflog", "-m", "local first", local, previous);
        await Git(repository, "update-ref", "-m", "local second", local, tip);
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(fixture.Root, "private-origin.git"), bare: true);
        await RepositoryFixture.ConfigureAsync(bare);
        await Git(bare, "fetch", "--", repository.Path, "HEAD:" + remoteBranch);
        await repository.AddRemoteAsync("mirror", bare.Path);
        await Git(repository, "fetch", "mirror", remoteBranch + ":" + tracking);
        await Git(bare, "update-ref", "-m", "private remote rollback", remoteBranch, previous);
        await Git(repository, "fetch", "mirror", "+" + remoteBranch + ":" + tracking);

        string before = Materials(repository.Path, bare.Path);
        IReadOnlyList<GitReflogEntry> localEntries = await repository.GetReflogAsync(null, local);
        IReadOnlyList<GitReflogEntry> remoteEntries = await repository.GetReflogAsync(null, tracking);
        Check(localEntries.Count == 2 && localEntries[0].Hash == tip && localEntries[1].Hash == previous, "Local choice reads its own real branch transitions.");
        Check(remoteEntries.Count == 2 && remoteEntries[0].Hash == previous && remoteEntries[1].Hash == tip, "Remote choice reads fetched tracking updates in their actual reverse order.");
        Check((await repository.GetReflogAsync(null, "topic/é")).SequenceEqual(localEntries)
            && (await repository.GetReflogAsync(null, "mirror/topic/remote")).SequenceEqual(remoteEntries), "Windows short local/remote choices remain native Git references.");
        await MatchesGit(repository, local, localEntries);
        await MatchesGit(repository, tracking, remoteEntries);
        Check(before == Materials(repository.Path, bare.Path), "Local/remote journal reads do not fetch, update refs or alter the private bare repository.");
    }

    private static async Task LiteralReferencesAsync()
    {
        using RepositoryFixture fixture = await TwoCommitsAsync();
        GitRepository repository = fixture.Repository;
        string tip = await repository.ResolveRevisionAsync("HEAD"), previous = await repository.ResolveRevisionAsync("HEAD^");
        const string name = "topic/café\"'$()&;", neighbour = "topic/café-other";
        await Git(repository, "check-ref-format", "refs/heads/" + name);
        await Git(repository, "update-ref", "--create-reflog", "-m", "Literal Ω 🧪", "refs/heads/" + name, tip);
        await Git(repository, "update-ref", "--create-reflog", "-m", "Neighbour", "refs/heads/" + neighbour, previous);
        string before = Materials(repository.Path);
        IReadOnlyList<GitReflogEntry> full = await repository.GetReflogAsync(null, "refs/heads/" + name);
        IReadOnlyList<GitReflogEntry> shorthand = await repository.GetReflogAsync(null, name);
        Check(full.Count == 1 && shorthand.SequenceEqual(full) && full[0].Hash == tip && full[0].Description == "Literal Ω 🧪",
            "Quotes, Unicode and shell-looking characters remain one exact reference argument.");
        Check(full[0].Selector.Contains("café", StringComparison.Ordinal) && !full[0].Selector.Contains('\ufffd'), "UTF8 selector identities retain their actual reference name.");
        await MatchesGit(repository, "refs/heads/" + name, full);
        Check((await repository.GetReflogAsync(null, neighbour)).Single().Hash == previous, "A nearby reference never leaks into the selected journal.");
        Check(before == Materials(repository.Path), "Literal reads execute no shell operation and change no metadata/worktree data.");
    }

    private static async Task EmptyAndBareAsync()
    {
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        string unbornBefore = Materials(unborn.Repository.Path);
        Check((await unborn.Repository.GetReflogAsync()).Count == 0 && (await unborn.Repository.GetReflogAsync(null)).Count == 0,
            "Both signatures retain empty unborn HEAD behavior.");
        await Expect<GitCommandException>(() => unborn.Repository.GetReflogAsync(null, "missing"));
        Check(unbornBefore == Materials(unborn.Repository.Path), "An unborn read creates neither HEAD/index nor journal data.");

        using RepositoryFixture fixture = await TwoCommitsAsync();
        GitRepository repository = fixture.Repository;
        string tip = await repository.ResolveRevisionAsync("HEAD"), previous = await repository.ResolveRevisionAsync("HEAD^");
        await Git(repository, "-c", "core.logAllRefUpdates=false", "update-ref", "refs/heads/no-journal", tip);
        Check(!File.Exists(fixture.File(".git/logs/refs/heads/no-journal")), "The fixture really has a valid ref without any reflog.");
        string noLogBefore = Materials(repository.Path);
        Check((await repository.GetReflogAsync(null, "no-journal")).Count == 0, "A valid choice with no journal has no invented entry.");
        Check(noLogBefore == Materials(repository.Path), "An empty journal read must not create one.");

        GitRepository bare = await GitRepository.CloneAsync(repository.Path, Path.Combine(fixture.Root, "bare.git"), new GitCloneOptions(Bare: true));
        await RepositoryFixture.ConfigureAsync(bare);
        await bare.SetConfigAsync("core.logAllRefUpdates", "true");
        await Git(bare, "update-ref", "--create-reflog", "-m", "bare first", "refs/heads/main", previous);
        await Git(bare, "update-ref", "-m", "bare second", "refs/heads/main", tip);
        string before = Materials(repository.Path, bare.Path);
        IReadOnlyList<GitReflogEntry> entries = await bare.GetReflogAsync(null, "refs/heads/main");
        Check(entries.Count == 2 && entries[0].Hash == tip && entries[1].Hash == previous, "A real bare reflog needs no worktree.");
        await MatchesGit(bare, "refs/heads/main", entries);
        await MatchesGit(bare, "HEAD", await bare.GetReflogAsync(null));
        Check(before == Materials(repository.Path, bare.Path), "Bare reading preserves all objects, configuration and branch logs.");
    }

    private static async Task EncodingsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repository = fixture.Repository;
        await repository.SetConfigAsync("user.name", "Åuteur Ω 🧪");
        await repository.CommitAsync(new GitCommitOptions("UTF8 Ω 🧪", AllowEmpty: true));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await repository.SetConfigAsync("i18n.commitEncoding", "windows-1252");
        const string cp1252 = "Café € ‘gauche’ Â©  deux\t mots\nAutre paragraphe conservé\n";
        GitBinaryResult cp = await BinaryGit(repository, ["commit", "--allow-empty", "--file=-"], Encoding.GetEncoding("windows-1252").GetBytes(cp1252),
            new Dictionary<string, string?> { ["GIT_REFLOG_ACTION"] = "Étape 🧪" });
        Check(cp.Success, cp.StandardError);
        string cpHash = await repository.ResolveRevisionAsync("HEAD");
        await repository.SetConfigAsync("i18n.commitEncoding", "iso-8859-1");
        GitBinaryResult latin = await BinaryGit(repository, ["commit", "--allow-empty", "--file=-"], Encoding.GetEncoding("iso-8859-1").GetBytes("Café ISO Â©\nDeuxième ligne\n"));
        Check(latin.Success, latin.StandardError);
        string latinHash = await repository.ResolveRevisionAsync("HEAD");
        await repository.SetConfigAsync("i18n.commitEncoding", "utf-8");
        string before = Materials(repository.Path);
        IReadOnlyList<GitReflogEntry> complete = await repository.GetReflogAsync(null);
        Check(complete[0].Hash == latinHash && complete[0].Description == "commit: Café ISO Â©", "Latin1 e9 and the accidentally-valid UTF8 pair C2A9 retain the actual subject.");
        Check(complete.Single(entry => entry.Hash == cpHash).Description == "Étape 🧪: Café € ‘gauche’ Â© deux mots",
            "Mixed UTF8 action and CP1252 subject are decoded from their real commit codec, including native first-line whitespace.");
        Check(complete.Any(entry => entry.Description == "commit: UTF8 Ω 🧪") && complete.All(entry => !entry.Selector.Contains('\ufffd') && !entry.Description.Contains('\ufffd')),
            "The UTF8 records and selector identities remain intact beside legacy subjects.");
        Check(complete.SequenceEqual(await repository.GetReflogAsync()), "The compatibility overload retains exactly the same encoded subject behavior.");
        GitBinaryResult rawCommit = await BinaryGit(repository, ["cat-file", "commit", cpHash]);
        Check(rawCommit.StandardOutput.AsSpan().IndexOf("encoding windows-1252\n"u8) >= 0
            && rawCommit.StandardOutput.AsSpan().IndexOf((byte)0x80) >= 0 && rawCommit.StandardOutput.AsSpan().IndexOf((byte)0xe9) >= 0,
            "The fixture really contains CP1252 commit bytes, not UTF8 data carrying a label.");
        Check(before == Materials(repository.Path), "Legacy decoding and raw object reads do not rewrite any commit, reflog or config byte.");
    }

    private static async Task InvalidArgumentsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repository = fixture.Repository;
        string before = Materials(repository.Path);
        await Expect<ArgumentOutOfRangeException>(() => repository.GetReflogAsync(0));
        await Expect<ArgumentOutOfRangeException>(() => repository.GetReflogAsync(-1));
        await Expect<ArgumentOutOfRangeException>(() => repository.GetReflogAsync((int?)0));
        await Expect<ArgumentOutOfRangeException>(() => repository.GetReflogAsync((int?)-1));
        foreach (string reference in new[] { "", "--all", "--output=" + Path.Combine(fixture.Root, "must-not-exist"), "HEAD\0ignored" })
        {
            await Expect<ArgumentException>(() => repository.GetReflogAsync(null, reference));
            await Expect<ArgumentException>(() => repository.GetReflogAsync(1, reference));
        }
        await Expect<ArgumentException>(() => repository.GetReflogAsync(null, null!));
        await Expect<GitCommandException>(() => repository.GetReflogAsync(null, "missing-ref"));
        await Expect<GitCommandException>(() => repository.GetReflogAsync(null, "main missing-ref"));
        Check(!File.Exists(Path.Combine(fixture.Root, "must-not-exist")) && before == Materials(repository.Path),
            "Invalid references are data/errors, never output options or multiple unvalidated command arguments.");
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture fixture = await TwoCommitsAsync();
        string before = Materials(fixture.Repository.Path);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => fixture.Repository.GetReflogAsync(null, cancellationToken: cancelled.Token));
        await Expect<OperationCanceledException>(() => fixture.Repository.GetReflogAsync(cancellationToken: cancelled.Token));
        if (OperatingSystem.IsLinux())
        {
            string executable = Path.Combine(fixture.Root, "slow-reflog-git"), parentFile = Path.Combine(fixture.Root, "reflog.pid"), childFile = Path.Combine(fixture.Root, "reflog.child");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = reflog ]; then\n  printf '%s' \"$$\" > " + Quote(parentFile)
                + "\n  sleep 60 &\n  printf '%s' \"$!\" > " + Quote(childFile) + "\n  wait\n  exit 1\n fi\ndone\nexec git \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository repository = new(fixture.Repository.Path, new GitRunner(executable));
            using CancellationTokenSource cancellation = new();
            Task<IReadOnlyList<GitReflogEntry>> pending = repository.GetReflogAsync(null, cancellationToken: cancellation.Token);
            try
            {
                Stopwatch waiting = Stopwatch.StartNew();
                while ((!File.Exists(childFile) || new FileInfo(childFile).Length == 0) && !pending.IsCompleted && waiting.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
                Check(File.Exists(childFile) && new FileInfo(childFile).Length > 0, "The actual binary reflog read reached its controlled process gate.");
                int parent = int.Parse(await File.ReadAllTextAsync(parentFile), CultureInfo.InvariantCulture), child = int.Parse(await File.ReadAllTextAsync(childFile), CultureInfo.InvariantCulture);
                cancellation.Cancel();
                await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                waiting.Restart();
                while ((LinuxProcessState.IsRunning(parent) || LinuxProcessState.IsRunning(child)) && waiting.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
                Check(!LinuxProcessState.IsRunning(parent) && !LinuxProcessState.IsRunning(child), "Cancellation stops the binary reader and its descendant, including Z/X procfs states.");
            }
            finally { cancellation.Cancel(); }
        }
        Check(before == Materials(fixture.Repository.Path), "Early and running cancellation preserve all Git/index/worktree bytes and modes.");
    }

    private static async Task ReadonlyAsync()
    {
        using RepositoryFixture fixture = await TwoCommitsAsync();
        GitRepository repository = fixture.Repository;
        fixture.Write("seed.txt", "staged Café Ω\n"); await repository.StageAsync(["seed.txt"]);
        fixture.Write("seed.txt", "staged Café Ω\nunstaged bytes\n");
        fixture.Write("untracked [*]\nfile.bin", "untracked\0\ufffd bytes\n");
        if (OperatingSystem.IsLinux())
        {
            File.CreateSymbolicLink(fixture.File("owned-link"), "seed.txt");
            File.SetUnixFileMode(fixture.File("seed.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string previous = await repository.ResolveRevisionAsync("HEAD^");
        await Git(repository, "switch", "--detach", previous);
        string before = Materials(repository.Path);
        IReadOnlyList<GitReflogEntry> entries = await repository.GetReflogAsync(null);
        Check(entries.Any(entry => entry.Description.StartsWith("checkout: moving from ", StringComparison.Ordinal)), "Detached checkout transitions remain visible in HEAD's native history.");
        await repository.GetReflogAsync(null, "main");
        await repository.GetReflogAsync(1);
        await MatchesGit(repository, "HEAD", entries);
        Check(before == Materials(repository.Path), "Read-only queries preserve dirty staging, raw index, executable/link/untracked data, detached HEAD, refs, logs, config and objects exactly.");
    }

    private static async Task<RepositoryFixture> TwoCommitsAsync()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        try
        {
            await fixture.Repository.CommitAsync(new GitCommitOptions("Second", AllowEmpty: true));
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task MatchesGit(GitRepository repository, string reference, IReadOnlyList<GitReflogEntry> actual, int? count = null)
    {
        List<string> arguments = ["reflog", "show", "--no-abbrev", "--encoding=UTF-8", "-z", "--format=%H%x00%gd%x00%gs%x00%cI"];
        if (count is int limit) { arguments.Add("--max-count=" + limit.ToString(CultureInfo.InvariantCulture)); }
        arguments.AddRange([reference, "--"]);
        GitBinaryResult result = await BinaryGit(repository, arguments);
        Check(result.Success, result.StandardError);
        string[] fields = new UTF8Encoding(false, true).GetString(result.StandardOutput).Split('\0');
        Check(fields[^1].Length == 0 && (fields.Length - 1) % 4 == 0 && actual.Count == (fields.Length - 1) / 4, "The independent Git oracle emits complete unbounded or bounded records.");
        for (int index = 0; index < actual.Count; index++)
        {
            Check(actual[index].Hash == fields[index * 4] && actual[index].Selector == fields[index * 4 + 1]
                && actual[index].Description == fields[index * 4 + 2]
                && actual[index].CommitDate == DateTimeOffset.Parse(fields[index * 4 + 3], CultureInfo.InvariantCulture),
                "Hash/ordinal/action/date must match actual native Git output without resorting or dropping repeated hashes.");
        }
    }

    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-pager", "-c", "color.ui=false", .. arguments]);
        Check(result.Success, string.Join(' ', arguments) + "\n" + result.StandardError);
        return result;
    }

    private static Task<GitBinaryResult> BinaryGit(GitRepository repository, IReadOnlyList<string> arguments, byte[]? input = null, IReadOnlyDictionary<string, string?>? environment = null)
        => new GitRunner().RunBinaryAsync(repository.Path, ["--no-pager", "-c", "color.ui=false", .. arguments], standardInput: input, environment: environment);

    private static string Materials(params string[] roots)
    {
        // Read physical bytes directly: no status/index refresh or timestamp normalization can disguise an effect.
        SortedDictionary<string, string> records = new(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            void Walk(string directory)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    string mode = OperatingSystem.IsLinux() ? ((int)File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture) : "not-unix";
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        string? target = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;
                        records[path] = "link|" + mode + "|" + target;
                    }
                    else if ((attributes & FileAttributes.Directory) != 0)
                    {
                        records[path] = "directory|" + mode;
                        Walk(path);
                    }
                    else
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        records[path] = "file|" + mode + "|" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(SHA256.HashData(bytes));
                    }
                }
            }
            Walk(root);
        }
        return JsonSerializer.Serialize(records);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
