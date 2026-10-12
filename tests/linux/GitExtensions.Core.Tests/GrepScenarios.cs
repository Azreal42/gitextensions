using System.Text;
using GitExtensions.Linux;

internal static class GrepScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("typed grep preserves configured CP1252 and Latin1 source bytes and accented patterns", EncodingsAsync),
        ("typed grep keeps NUL-delimited colon newline literal and Unicode path identities", PathsAsync),
        ("typed grep preserves Windows basic regexp case whole-word and user matching arguments", MatchingAsync),
        ("typed grep preserves boolean expressions and encoded patterns without invoking a shell", ExpressionsAsync),
        ("typed grep identifies immutable historical and index sources without changing the repository", SourcesAsync),
        ("typed grep distinguishes context separators from newline filenames and byte columns", ContextAsync),
        ("typed grep retains undecodable source bytes and converts UTF8 byte columns to text columns", LosslessAsync),
        ("typed grep retains native binary-file path matches across immutable index and worktree sources", BinaryFilesAsync),
        ("typed grep no-match invalid pattern incompatible options bare and cancellation remain explicit", ValidationAsync)
    ];

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<string> Snapshot(GitRepository r)
        => (await r.RunAsync(["status", "--porcelain=v2", "-z"])).StandardOutput + "\0" + (await r.RunAsync(["ls-files", "--stage", "-z"])).StandardOutput
            + "\0" + (await r.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"])).StandardOutput;

    private static async Task EncodingsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        Encoding cp = GitFileEncodingCodec.GetEncoding("windows-1252");
        byte[] bytes = cp.GetBytes("prélude: café €\r\nsecond café line"); await f.WriteAsync("cp:1252.txt", bytes); await f.CommitAsync();
        await r.SetConfigAsync("i18n.filesEncoding", "windows-1252"); string before = await Snapshot(r);
        GitGrepResult result = await r.GrepAsync(new("café"));
        Check(result.EncodingName == "windows-1252" && result.Matches.Count == 2 && result.Matches.All(match => match.IsLossless), "Configured codec decodes both matching source lines exactly.");
        GitGrepMatch first = result.Matches[0];
        Check(first.LineBytes.Span.SequenceEqual(cp.GetBytes("prélude: café €\r")) && first.Text == "prélude: café €\r" && first.ByteColumn == 10 && first.TextColumn == 10, "CP1252 euro, accents, CR and byte column remain exact.");
        Check(result.Matches[1].LineNumber == 2 && result.Matches[1].LineBytes.Span.SequenceEqual(cp.GetBytes("second café line")), "Git's final-line terminator is excluded without inventing source contents.");
        Check((await r.GrepAsync(new("€", Paths: ["cp:1252.txt"]))).Matches.Single().Text == "prélude: café €\r", "A pattern that differs between UTF8 and CP1252 is encoded before searching.");
        Check(before == await Snapshot(r), "Encoded source search does not change the index/worktree/refs.");
        Encoding latin = GitFileEncodingCodec.GetEncoding("iso-8859-1"); await f.WriteAsync("latin.txt", latin.GetBytes("à café ÿ\n")); await r.StageAsync(["latin.txt"]);
        string latinBefore = await Snapshot(r);
        GitGrepMatch latinMatch = (await r.GrepAsync(new("café", Paths: ["latin.txt"], EncodingName: "iso-8859-1"))).Matches.Single();
        Check(latinMatch.IsLossless && latinMatch.Text == "à café ÿ" && latinMatch.LineBytes.Span.SequenceEqual(latin.GetBytes(latinMatch.Text)), "An explicit Latin1 codec and accented pattern preserve source bytes.");
        Check(latinBefore == await Snapshot(r), "Search reads do not change tracked index/worktree/refs.");
    }

    private static async Task PathsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        const string literal = ":name[1]* λ\n雪:42.txt", adjacent = ":name1 λ\n雪:42.txt";
        await f.WriteAsync(literal, "target: source\n"); await f.WriteAsync(adjacent, "target: adjacent\n"); await f.CommitAsync();
        string before = await Snapshot(r); GitGrepMatch match = (await r.GrepAsync(new("target", Paths: [literal]))).Matches.Single();
        Check(match.Path == literal && match.HasLosslessPath && match.PathBytes.Span.SequenceEqual(Encoding.UTF8.GetBytes(literal)) && match.LineNumber == 1 && match.Text == "target: source", "Literal pathspecs and NUL pathname headers preserve colon/newline filenames.");
        Check(await Snapshot(r) == before, "Literal source search is read-only.");
    }

    private static async Task MatchingAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        await f.WriteAsync("patterns.txt", "Cat\ncat\nconcatenate\ncatfish\ncat.\na+b\nab\n-end\n"); await f.CommitAsync();
        Check((await r.GrepAsync(new("cat", Paths: ["patterns.txt"]))).Matches.Select(m => m.LineNumber).SequenceEqual([2, 3, 4, 5]), "Default is case-sensitive basic regexp.");
        Check((await r.GrepAsync(new("cat", IgnoreCase: true, MatchWholeWord: true))).Matches.Select(m => m.LineNumber).SequenceEqual([1, 2, 5]), "Windows case and whole-word options remain independent.");
        Check((await r.GrepAsync(new("a+b"))).Matches.Single().LineNumber == 6, "Basic regexp treats an unescaped plus literally.");
        Check((await r.GrepAsync(new("a+b", UserArguments: "-E"))).Matches.Single().LineNumber == 7, "User options can explicitly choose extended regexp.");
        Check((await r.GrepAsync(new("cat.", UserArguments: "-Fiw"))).Matches.Single().LineNumber == 5, "Combined fixed-string case and word options are preserved.");
        Check((await r.GrepAsync(new("-end"))).Matches.Single().LineNumber == 8, "A leading dash in a plain pattern cannot become a Git option.");
    }

    private static async Task ExpressionsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        Encoding cp = GitFileEncodingCodec.GetEncoding("windows-1252");
        await f.WriteAsync("expression.txt", cp.GetBytes("café tea\ncafé only\ntea only\nword 42\n$(touch never-created)\n")); await f.CommitAsync();
        GitGrepOptions both = new("-e \"café\" --and -e 'tea'", EncodingName: "windows-1252");
        Check((await r.GrepAsync(both)).Matches.Single().LineNumber == 1, "Boolean -e expressions keep their grouping and encoded pattern bytes.");
        Check((await r.GrepAsync(new("tea", UserArguments: "-e 'café' --and", EncodingName: "windows-1252"))).Matches.Single().LineNumber == 1, "Original user arguments can precede the expression and supply a boolean operand.");
        Check((await r.GrepAsync(new("-e 'word [0-9]+'", UserArguments: "--extended-regexp", EncodingName: "windows-1252"))).Matches.Single().LineNumber == 4, "Quoted regexp argument syntax remains native Git matching.");
        Check((await r.GrepAsync(new("$(touch never-created)", UserArguments: "--fixed-strings", EncodingName: "windows-1252"))).Matches.Single().LineNumber == 5 && !File.Exists(f.File("never-created")), "Shell-looking content remains a literal pattern.");
        Check((await r.GrepAsync(new("-e 'tea' --or -e 'only'", EncodingName: "windows-1252"))).Matches.Count == 3, "Explicit OR remains a Git boolean expression.");
    }

    private static async Task SourcesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        const string path = "source:HEAD\nλ.txt"; await f.WriteAsync(path, "historical target\n"); await f.CommitAsync();
        string head = await r.ResolveRevisionAsync("HEAD"); await f.WriteAsync(path, "index target\n"); await r.StageAsync([path]); await f.WriteAsync(path, "worktree target\n");
        string before = await Snapshot(r);
        GitGrepResult historical = await r.GrepAsync(new("target", Revision: "HEAD"));
        Check(historical.Revision == head && historical.Matches.Single().Path == path && historical.Matches.Single().Text == "historical target", "Historical result removes only the resolved hash prefix and keeps an immutable navigation source.");
        GitGrepResult index = await r.GrepAsync(new("target", Cached: true));
        Check(index.Cached && index.Revision is null && index.Matches.Single().Text == "index target", "Index search ignores differing worktree contents.");
        Check((await r.GrepAsync(new("target"))).Matches.Single().Text == "worktree target" && await Snapshot(r) == before, "Worktree search remains separate and read-only.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true); await bare.RunAsync(["fetch", r.Path, "HEAD"]);
        Check((await bare.GrepAsync(new("target", Revision: "FETCH_HEAD"))).Matches.Single().Text == "historical target", "Bare repositories permit immutable historical grep.");
    }

    private static async Task ContextAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        foreach (string path in new[] { "--\ncontext:1.txt", "--\n--\ncontext:1.txt", "context:1.txt" })
        { await f.WriteAsync(path, "before\ntarget\nafter\ngap\ngap\ntarget\nlast\n"); }
        await f.CommitAsync(); string before = await Snapshot(r);
        GitGrepResult result = await r.GrepAsync(new("target", UserArguments: "--context=1"));
        Check(result.Matches.Count == 18 && result.Matches.Count(match => !match.IsContext) == 6, "Matched and surrounding source lines remain distinct across native separators.");
        foreach (IGrouping<string, GitGrepMatch> group in result.Matches.GroupBy(match => match.Path))
        {
            Check(group.Count() == 6 && group.Where(match => !match.IsContext).Select(match => match.LineNumber).SequenceEqual([2, 6]), "A newline filename beginning like a separator keeps its complete identity.");
            Check(group.Where(match => match.IsContext).All(match => match.ByteColumn is null) && group.Where(match => !match.IsContext).All(match => match.ByteColumn == 1), "Context records have no invented match column.");
        }
        GitGrepResult historical = await r.GrepAsync(new("target", Revision: "HEAD", UserArguments: "-A1 -B1"));
        Check(historical.Matches.Count == result.Matches.Count && historical.Matches.All(match => !match.Path.StartsWith(historical.Revision!, StringComparison.Ordinal)), "Context in a resolved revision keeps filename identity too.");
        Check(await Snapshot(r) == before, "Context reads preserve repository state.");
    }

    private static async Task LosslessAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        await f.WriteAsync("unicode.txt", "λé: target\n"); await f.WriteAsync("invalid.txt", [0xff, (byte)' ', (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', 10]);
        await f.CommitAsync(); GitGrepResult result = await r.GrepAsync(new("target"));
        GitGrepMatch unicode = result.Matches.Single(match => match.Path == "unicode.txt"), invalid = result.Matches.Single(match => match.Path == "invalid.txt");
        Check(unicode.ByteColumn == 7 && unicode.TextColumn == 5 && unicode.Text == "λé: target" && unicode.IsLossless, "Git's byte column converts to an exact editor UTF16 column.");
        Check(!invalid.IsLossless && invalid.LineBytes.Span.SequenceEqual(new byte[] { 0xff, 32, 116, 97, 114, 103, 101, 116 }) && !invalid.Text.Contains('\uFFFD'), "Unmappable output retains raw bytes and never invents replacement characters.");
        GitGrepMatch decoded = (await r.GrepAsync(new("target", Paths: ["invalid.txt"], EncodingName: "iso-8859-1"))).Matches.Single();
        Check(decoded.IsLossless && decoded.Text == "ÿ target" && decoded.TextColumn == 3, "The user can select a lossless codec over unchanged source bytes.");
    }

    private static async Task BinaryFilesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        const string path = ":binary[1]* λ\nmatch.bin";
        byte[] historicalBytes = Encoding.UTF8.GetBytes("\0historical_target\n"), indexBytes = Encoding.UTF8.GetBytes("\0index_target\n"), worktreeBytes = Encoding.UTF8.GetBytes("\0worktree_target\n");
        await f.WriteAsync(path, historicalBytes); await f.WriteAsync("adjacent.bin", Encoding.UTF8.GetBytes("\0historical_target\n")); await f.CommitAsync();
        string hash = await r.ResolveRevisionAsync("HEAD"); await f.WriteAsync(path, indexBytes); await r.StageAsync([path]); await f.WriteAsync(path, worktreeBytes);
        string before = await Snapshot(r);
        foreach ((GitGrepOptions options, byte[] bytes) in new (GitGrepOptions, byte[])[]
        {
            (new("historical_target", Revision: "HEAD", Paths: [path]), historicalBytes),
            (new("index_target", Cached: true, Paths: [path]), indexBytes),
            (new("worktree_target", Paths: [path]), worktreeBytes)
        })
        {
            GitGrepResult result = await r.GrepAsync(options); GitGrepBinaryMatch binary = result.BinaryMatches.Single();
            Check(result.HasMatches && result.Matches.Count == 0 && binary.Path == path && binary.HasLosslessPath
                && binary.PathBytes.Span.SequenceEqual(Encoding.UTF8.GetBytes(path)), "Native -l -z binary-file membership keeps literal colon/newline path bytes without invented line records.");
            Check(result.Revision == (options.Revision is null ? null : hash) && result.Cached == options.Cached, "A binary match carries the same immutable hash or index/worktree scope as text matches.");
            Check(!(await r.GrepAsync(options with { UserArguments = "-I" })).HasMatches, "An explicit native -I still excludes binary files.");
            GitGrepResult text = await r.GrepAsync(options with { UserArguments = "-a" }); GitGrepMatch line = text.Matches.Single();
            Check(text.BinaryMatches.Count == 0 && line.LineBytes.Span.SequenceEqual(bytes.AsSpan(0, bytes.Length - 1)) && line.ByteColumn == 2,
                "Explicit -a supplies real byte-line metadata and keeps NUL bytes rather than a binary notice.");
            GitGrepResult context = await r.GrepAsync(options with { UserArguments = "-C1" });
            Check(context.Matches.Count == 0 && context.BinaryMatches.Single().Path == path, "Binary files stay visible with context without fabricated surrounding lines.");
        }
        Check(!(await r.GrepAsync(new("worktree_target", Cached: true, Paths: [path]))).HasMatches
            && !(await r.GrepAsync(new("index_target", Paths: [path]))).HasMatches, "Binary membership does not mix index and worktree contents.");
        Check(await Snapshot(r) == before && File.ReadAllBytes(f.File(path)).AsSpan().SequenceEqual(worktreeBytes), "All binary-source searches preserve index, worktree bytes and refs.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository r = f.Repository;
        await f.WriteAsync("file.txt", "target\n"); await f.WriteAsync("binary.bin", [0, 116, 97, 114, 103, 101, 116, 10]); await f.CommitAsync();
        string before = await Snapshot(r); Check(!(await r.GrepAsync(new("absent"))).HasMatches, "Git exit 1 means a successful empty search.");
        GitGrepResult defaultResult = await r.GrepAsync(new("target"));
        Check(defaultResult.Matches.Count == 1 && defaultResult.BinaryMatches.Single().Path == "binary.bin", "Default grep keeps binary file membership apart from text source-line records.");
        GitGrepMatch binary = (await r.GrepAsync(new("target", UserArguments: "-a", Paths: ["binary.bin"]))).Matches.Single();
        Check(binary.LineBytes.Span.SequenceEqual(new byte[] { 0, 116, 97, 114, 103, 101, 116 }) && binary.ByteColumn == 2, "Explicit text matching retains binary NUL bytes in the line payload.");
        try { await r.GrepAsync(new("[")); throw new Exception("Invalid regexp accepted."); } catch (GitCommandException ex) { Check(ex.Result.ExitCode != 1 && ex.Result.StandardError.Length > 0, "Invalid regex exposes native Git diagnostics."); }
        foreach (GitGrepOptions options in new GitGrepOptions[]
        {
            new("target", UserArguments: "-l"), new("target", UserArguments: "--heading"), new("target", UserArguments: "-o"),
            new("target", UserArguments: "-e 'unterminated"), new("target", UserArguments: "-a -C1"), new("target", Cached: true, Revision: "HEAD"),
            new("target", Paths: ["../outside"]), new("target", Paths: []), new("雪", EncodingName: "windows-1252")
        })
        { try { await r.GrepAsync(options); throw new Exception("Incompatible options accepted."); } catch (ArgumentException) { } }
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        try { await r.GrepAsync(new("target"), cancelled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        try { await bare.GrepAsync(new("target")); throw new Exception("Bare worktree accepted."); } catch (InvalidOperationException) { }
        Check(await Snapshot(r) == before && File.ReadAllText(f.File("file.txt")) == "target\n", "Errors validation and cancellation cannot change repository state.");
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, GitRepository repository) { Root = root; Repository = repository; }
        internal string Root { get; }
        internal GitRepository Repository { get; }
        internal string File(string path) => Path.Combine(Repository.Path, path);
        internal Task WriteAsync(string path, string text) => WriteAsync(path, Encoding.UTF8.GetBytes(text));
        internal Task WriteAsync(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(File(path))!); return System.IO.File.WriteAllBytesAsync(File(path), bytes); }
        internal async Task CommitAsync() { await Repository.StageAllAsync(); await Repository.CommitAsync("Grep source"); }
        internal static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "gitextensions-grep-test-" + Guid.NewGuid().ToString("N"));
            GitRepository r = await GitRepository.InitAsync(Path.Combine(root, "repository"));
            await r.SetConfigAsync("user.name", "Grep Test"); await r.SetConfigAsync("user.email", "grep@example.invalid");
            await r.SetConfigAsync("commit.gpgsign", "false"); await r.SetConfigAsync("core.hooksPath", "/dev/null");
            return new(root, r);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
