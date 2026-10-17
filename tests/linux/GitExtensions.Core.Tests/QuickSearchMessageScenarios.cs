using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class QuickSearchMessageScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("quick search log preserves native subject body and exact full message independently", MessageFieldsAsync),
        ("quick search revision and renamed-file graphs retain the same exact native messages", GraphMessagesAsync),
        ("quick search branch filters and pagination preserve complete NUL record boundaries", FilteredPagingAsync),
        ("quick search legacy DTO and direct file-history contracts retain a null full message", LegacyReadersAsync),
        ("quick search full messages retain native UTF8 recoding of a legacy encoded commit", EncodedMessageAsync),
        ("quick search messages work in bare linked and SHA256 histories without writes", RepositoryKindsAsync),
        ("quick search full messages come from the single existing log request", SingleReadAsync),
        ("quick search cancellation drains the real log reader and its descendants", CancellationAsync),
    ];

    private static readonly string[] Messages =
    [
        "Single subject\n",
        "Folded subject line one\nline two\twith tab\n\nBody line\n\nLast paragraph\n",
        "Only subject\ncontinuation without body\n",
        "Unicode é Ω 🧪\nmore subject λ\n\nCorps café\n\n终\n",
        "CRLF subject\r\ncontinued\r\n\r\nCRLF body\r\n\r\nLast\r\n",
        "Mixed subject\r\nsecond\n\nMixed body\r\nlast line without terminator",
        "\n\nLeading blanks\n\nBody after leading blanks\n\n\n",
        "",
        "Subject with trailing spaces  \n\n  indented body\t \n\n",
    ];

    private sealed record Fixture(RepositoryFixture Owner, GitRepository Repository, IReadOnlyList<string> Hashes,
        string OldPath, string Path) : IDisposable
    {
        public void Dispose() => Owner.Dispose();
    }

    private sealed record MessageFields(string Subject, string Body, string FullMessage);

    private static async Task<Fixture> CreateAsync(bool sha256 = false)
    {
        RepositoryFixture owner = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            GitRepository repository = owner.Repository;
            if (sha256)
            {
                string path = System.IO.Path.Combine(owner.Root, "sha256");
                await Git(repository, "init", "--object-format=sha256", "-b", "main", path);
                repository = new(path);
                await RepositoryFixture.ConfigureAsync(repository);
            }

            const string oldPath = "old [*] é.txt", newPath = "renamed [*] é.txt";
            List<string> hashes = [];
            string? parent = null;
            for (int index = 0; index < Messages.Length; index++)
            {
                string path = index == 0 ? oldPath : newPath;
                if (index == 1) { await Git(repository, "mv", "--", oldPath, newPath); }
                else
                {
                    await File.AppendAllTextAsync(System.IO.Path.Combine(repository.Path, path),
                        "stable tracked line " + index.ToString(CultureInfo.InvariantCulture) + "\n");
                }
                await repository.StageAllAsync();
                parent = await WriteCommitAsync(repository, parent, Encoding.UTF8.GetBytes(Messages[index]), index);
                hashes.Add(parent);
            }

            return new(owner, repository, hashes, oldPath, newPath);
        }
        catch { owner.Dispose(); throw; }
    }

    private static async Task<string> WriteCommitAsync(GitRepository repository, string? parent, byte[] message, int number,
        string? encoding = null)
    {
        string tree = (await Git(repository, "write-tree")).StandardOutput.TrimEnd('\r', '\n');
        string seconds = (981173106L + number).ToString(CultureInfo.InvariantCulture);
        string header = "tree " + tree + "\n" + (parent is null ? "" : "parent " + parent + "\n")
            + "author Message Tester <message@example.invalid> " + seconds + " +0530\n"
            + "committer Message Tester <message@example.invalid> " + seconds + " -0400\n"
            + (encoding is null ? "" : "encoding " + encoding + "\n") + "\n";
        GitBinaryResult result = await new GitRunner().RunBinaryAsync(repository.Path, ["hash-object", "-t", "commit", "-w", "--stdin"],
            standardInput: [.. Encoding.UTF8.GetBytes(header), .. message]);
        Check(result.Success, "Creating the raw native message fixture failed: " + result.StandardError);
        string hash = Encoding.UTF8.GetString(result.StandardOutput).TrimEnd('\r', '\n');
        await Git(repository, "update-ref", "refs/heads/main", hash);
        return hash;
    }

    private static async Task<MessageFields> NativeFieldsAsync(GitRepository repository, string hash)
    {
        // format: adds no implicit record terminator, so every CR/LF before the final NUL belongs to the native field.
        string output = (await Git(repository, "show", "--no-patch", "--encoding=UTF-8", "--format=format:%s%x00%b%x00%B%x00", hash, "--"))
            .StandardOutput;
        string[] fields = output.Split('\0');
        Check(fields.Length == 4 && fields[^1].Length == 0, "The independent native message oracle is incomplete.");
        return new(fields[0], fields[1], fields[2]);
    }

    private static async Task AssertMessagesAsync(GitRepository repository, IEnumerable<GitCommit> commits)
    {
        foreach (GitCommit commit in commits)
        {
            MessageFields native = await NativeFieldsAsync(repository, commit.Hash);
            Check(commit.Subject == native.Subject, "The existing %s projection changed for " + commit.Hash);
            Check(commit.Body == native.Body, "The existing %b body changed or received a duplicated subject for " + commit.Hash);
            Check(commit.FullMessage == native.FullMessage, "The full %B message was reconstructed, trimmed or normalized for " + commit.Hash);
        }
    }

    private static async Task MessageFieldsAsync()
    {
        using Fixture f = await CreateAsync();
        File.AppendAllText(System.IO.Path.Combine(f.Repository.Path, f.Path), "dirty working bytes\r\n");
        File.WriteAllText(System.IO.Path.Combine(f.Repository.Path, "untracked.txt"), "untracked\n");
        string before = Snapshot(f.Repository.Path);
        IReadOnlyList<GitCommit> commits = await f.Repository.GetLogAsync();
        Check(commits.Select(c => c.Hash).SequenceEqual(f.Hashes.Reverse()), "The native message records lost their history order or identities.");
        await AssertMessagesAsync(f.Repository, commits);
        GitCommit folded = commits.Single(c => c.Hash == f.Hashes[1]);
        Check(folded.Subject.StartsWith("Folded subject line one line two", StringComparison.Ordinal) && !folded.Subject.Contains('\n')
            && folded.Body == "Body line\n\nLast paragraph\n"
            && folded.FullMessage == Messages[1], "The fixture must distinguish a folded %s subject from its original subject lines and %b body.");
        Check(commits.Single(c => c.Hash == f.Hashes[7]).FullMessage == "", "A loaded empty message must not become a legacy null message.");
        Check(commits.Single(c => c.Hash == f.Hashes[4]).FullMessage!.Contains("\r\n", StringComparison.Ordinal),
            "The CRLF fixture must actually reach the reader without newline normalization.");
        Check(before == Snapshot(f.Repository.Path), "Reading messages changed HEAD, index, refs, objects or working bytes.");
    }

    private static async Task GraphMessagesAsync()
    {
        using Fixture f = await CreateAsync(); string before = Snapshot(f.Repository.Path);
        IReadOnlyList<GitCommit> normal = await f.Repository.GetLogAsync();
        IReadOnlyList<GitCommit> graph = await f.Repository.GetRevisionGraphAsync(new(Author: "Message Tester"));
        GitFileHistoryGraph file = await f.Repository.GetFileHistoryGraphAsync(new(Path: f.Path));
        Check(graph.Select(c => c.Hash).SequenceEqual(normal.Select(c => c.Hash))
            && file.Commits.Select(c => c.Hash).SequenceEqual(normal.Select(c => c.Hash)),
            "The graph readers must include all real message fixtures, including the former pathname.");
        Check(file.Paths.SequenceEqual([f.Path, f.OldPath]) && file.PathByCommit[f.Hashes[0]] == f.OldPath,
            "The message field must not alter separate followed-name discovery.");
        await AssertMessagesAsync(f.Repository, graph);
        await AssertMessagesAsync(f.Repository, file.Commits);
        Check(before == Snapshot(f.Repository.Path), "Reading the two graph variants modified the repository.");
    }

    private static async Task FilteredPagingAsync()
    {
        using Fixture f = await CreateAsync(); string before = Snapshot(f.Repository.Path);
        GitLogQuery query = new(Count: 5, Skip: 2, Author: "Message Tester") { BranchFilter = "main" };
        IReadOnlyList<GitCommit> commits = await f.Repository.GetLogAsync(query);
        Check(commits.Select(c => c.Hash).SequenceEqual(f.Hashes.Reverse().Skip(2).Take(5)),
            "The strict branch-filter validator or page boundaries lost a canonical message record.");
        await AssertMessagesAsync(f.Repository, commits);
        GitFileHistoryGraph graph = await f.Repository.GetFileHistoryGraphAsync(query with { Path = f.Path });
        Check(graph.Commits.Select(c => c.Hash).SequenceEqual(commits.Select(c => c.Hash)), "Branch-filtered file graph paging diverged.");
        await AssertMessagesAsync(f.Repository, graph.Commits);
        await ExpectAsync<ArgumentException>(() => f.Repository.GetLogAsync(query with { BranchFilter = "--pretty=oneline main" }));
        Check(before == Snapshot(f.Repository.Path), "Filtered message reads or rejected output overrides mutated the repository.");
    }

    private static async Task LegacyReadersAsync()
    {
        using Fixture f = await CreateAsync(); string before = Snapshot(f.Repository.Path);
        GitCommit legacy = new("legacy", [], "Author", "author@example.invalid", DateTimeOffset.UnixEpoch, "Subject", "Body");
        Check(legacy.FullMessage is null && legacy.Subject == "Subject" && legacy.Body == "Body", "Existing DTO constructors must retain the nullable legacy contract.");
        IReadOnlyList<GitFileHistoryEntry> direct = await f.Repository.GetFileHistoryAsync(f.Path);
        Check(direct.Count == Messages.Length && direct.All(entry => entry.Commit.FullMessage is null),
            "The dedicated direct file-history reader must retain its distinct, unchanged format.");
        foreach (GitFileHistoryEntry entry in direct)
        {
            MessageFields native = await NativeFieldsAsync(f.Repository, entry.Commit.Hash);
            Check(entry.Commit.Subject == native.Subject && entry.Commit.Body == native.Body,
                "Legacy direct-reader subject/body fields changed while adding the graph full message.");
        }
        Check(before == Snapshot(f.Repository.Path), "Checking legacy messages changed repository bytes.");
    }

    private static async Task EncodedMessageAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        f.Write("encoded.txt", "encoded\n"); await f.Repository.StageAllAsync();
        const string message = "Résumé composé\ncontinuation été\n\nCorps latin café\r\nFin\n";
        string hash = await WriteCommitAsync(f.Repository, null, Encoding.Latin1.GetBytes(message), 0, "ISO-8859-1");
        string before = Snapshot(f.Repository.Path);
        GitCommit commit = (await f.Repository.GetLogAsync()).Single();
        await AssertMessagesAsync(f.Repository, [commit]);
        Check(commit.Hash == hash && commit.FullMessage == message && commit.FullMessage?.Contains('\uFFFD') == false,
            "Git's UTF8 recoding must preserve the complete legacy-encoded message rather than decode raw bytes as UTF8.");
        Check(before == Snapshot(f.Repository.Path), "Legacy-encoded message reads modified the repository.");
    }

    private static async Task RepositoryKindsAsync()
    {
        using Fixture f = await CreateAsync();
        string bare = System.IO.Path.Combine(f.Owner.Root, "bare.git"), linked = System.IO.Path.Combine(f.Owner.Root, "linked");
        await Git(f.Repository, "clone", "--bare", f.Repository.Path, bare);
        await Git(f.Repository, "worktree", "add", "--detach", linked, "HEAD");
        string before = Snapshot(f.Owner.Root);
        foreach (string path in new[] { bare, linked })
        {
            GitRepository repository = await GitRepository.OpenAsync(path);
            IReadOnlyList<GitCommit> commits = await repository.GetLogAsync();
            Check(commits.Select(c => c.Hash).SequenceEqual(f.Hashes.Reverse()), "Bare/linked message histories lost commits.");
            await AssertMessagesAsync(repository, commits);
        }
        Check(before == Snapshot(f.Owner.Root), "Bare/linked message reads wrote repository state.");
        using Fixture sha = await CreateAsync(sha256: true); before = Snapshot(sha.Owner.Root);
        IReadOnlyList<GitCommit> wide = await sha.Repository.GetLogAsync(new GitLogQuery { BranchFilter = "main" });
        Check(wide.Count == Messages.Length && wide.All(c => c.Hash.Length == 64), "The canonical validator truncated SHA256 message records.");
        await AssertMessagesAsync(sha.Repository, wide);
        Check(before == Snapshot(sha.Owner.Root), "SHA256 message reads modified repository bytes.");
    }

    private static async Task SingleReadAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await CreateAsync();
        string trace = System.IO.Path.Combine(f.Owner.Root, "message-arguments"), wrapper = System.IO.Path.Combine(f.Owner.Root, "record-message-git");
        File.WriteAllText(wrapper, "#!/bin/sh\nprintf 'RECORD\\0' >> " + Quote(trace) + "\nprintf '%s\\0' \"$@\" >> " + Quote(trace) + "\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository recording = new(f.Repository.Path, new GitRunner(wrapper)); string before = Snapshot(f.Repository.Path);
        IReadOnlyList<GitCommit> commits = await recording.GetLogAsync();
        string[][] commands = File.ReadAllText(trace).Split("RECORD\0", StringSplitOptions.RemoveEmptyEntries)
            .Select(command => command.Split('\0', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        string[] log = commands.Single(command => command.Contains("log", StringComparer.Ordinal));
        string format = log.Single(argument => argument.StartsWith("--format=", StringComparison.Ordinal));
        Check(format == "--format=%H%x00%P%x00%aN%x00%aE%x00%aI%x00%s%x00%b%x00%B",
            "Full messages must share the existing canonical log request and keep the original projections.");
        Check(!commands.Any(command => command.Any(argument => argument is "show" or "cat-file" or "for-each-ref"))
            && commands.Count(command => command.Contains("log", StringComparer.Ordinal)) == 1,
            "Loading full messages must not add per-commit Git metadata requests.");
        await AssertMessagesAsync(f.Repository, commits);
        Check(before == Snapshot(f.Repository.Path), "The recorded log read changed repository content.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await CreateAsync();
        string wrapper = System.IO.Path.Combine(f.Owner.Root, "gated-message-git"), marker = System.IO.Path.Combine(f.Owner.Root, "message-pids");
        File.WriteAllText(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = log ]; then\n  sleep 60 &\n  child=$!\n  printf '%s %s\\n' \"$$\" \"$child\" > "
            + Quote(marker) + "\n  wait \"$child\"\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper)); string before = Snapshot(f.Repository.Path);
        using CancellationTokenSource token = new();
        Task<IReadOnlyList<GitCommit>> pending = guarded.GetLogAsync(new GitLogQuery(), token.Token);
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            string[] recorded = [];
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                recorded = File.Exists(marker) ? File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];
                if (recorded.Length == 2 && recorded.All(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))) { break; }
                await Task.Delay(10);
            }
            Check(recorded.Length == 2, "The native log reader did not reach its complete private cancellation barrier.");
            int[] pids = recorded.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            Check(pids.Length == 2, "The actual reader and descendant were not both recorded.");
            token.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending);
            Check(pids.All(pid => !LinuxProcessState.IsRunning(pid)), "Cancellation returned before draining the native reader and its descendant.");
            Check(before == Snapshot(f.Repository.Path), "Cancelling the message reader changed repository bytes.");
        }
        finally
        {
            token.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-optional-locks", "--literal-pathspecs", .. arguments]);
        if (!result.Success) { throw new GitCommandException(arguments, result); }
        return result;
    }

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Snapshot(string root)
        => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => System.IO.Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
}
