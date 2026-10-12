using System.Text;
using GitExtensions.Linux;

internal static class FileEncodingScenarios
{
    private const string Literal = ":[fichier]* été\t\n[1].txt";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("file codec repository default and session override never write config", DefaultsAsync),
        ("Latin1 mixed UTF8 headers and exact canonical/color/apply bytes", Latin1Async),
        ("CP1252 selected lines stage and unstage exact euro/quote bytes", SelectedLinesAsync),
        ("encoded and UTF8 patch selections honor half-open LF CRLF caret and adjacent hunk bounds", SelectionBoundsAsync),
        ("Latin1 selected hunks and reset retain omitted content bytes", SelectedHunksAsync),
        ("legacy rename root and bare revision readers preserve literal headers", RenameAndBareAsync),
        ("legacy word diff decodes unprefixed bodies and remains readonly", WordDiffAsync),
        ("UTF8 BOM content survives canonical patch and partial staging", BomPatchAsync),
        ("UTF16 UTF32 BOM priority and binary bytes never acquire replacements", UnicodeAndBinaryAsync),
        ("invalid file codecs and corrupt UTF8 cannot stage lossy decoded patches", InvalidAsync),
        ("CP1252 untracked comparison retains raw no-index patch bytes", UntrackedAsync),
        ("legacy combined three parents and range series remain readonly", CombinedAndRangeAsync),
        ("legacy two-read race disables staging and cancellation leaves bytes untouched", RaceAndCancellationAsync)
    ];

    public static async Task DefaultsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        Check(await f.Repository.GetFileEncodingNameAsync() == "utf-8", "Files default is UTF-8 without writing config.");
        await f.Repository.SetConfigAsync("i18n.filesencoding", "ISO-8859-1");
        await f.Repository.SetConfigAsync("i18n.commitencoding", "windows-1252");
        Write(f, Literal, "old été\n", "iso-8859-1"); await f.Repository.StageAsync([Literal]); await f.Repository.CommitAsync("Legacy file");
        Write(f, Literal, "new été\n", "iso-8859-1");
        byte[] config = File.ReadAllBytes(f.File(".git/config")), index = File.ReadAllBytes(f.File(".git/index"));
        GitDiffDocument repositoryDefault = await f.Repository.GetDiffDocumentAsync(Literal);
        Check(repositoryDefault.FileEncodingName == "iso-8859-1" && repositoryDefault.Patch.Contains("+new été", StringComparison.Ordinal), "Effective file config is independent of commit config.");
        GitDiffDocument session = await f.Repository.GetDiffDocumentAsync(Literal, displayOptions: new() { FileEncodingName = "windows-1252" });
        Check(session.FileEncodingName == "windows-1252" && session.PatchBytes.Span.SequenceEqual(repositoryDefault.PatchBytes.Span), "Session changes only the decode, not Git bytes.");
        Check((await f.Repository.GetCommitMessageEncodingAsync()).WebName == "windows-1252", "The commit codec was not changed.");
        Unchanged(f, config, index);
    }

    public static async Task Latin1Async()
    {
        using RepositoryFixture f = await LegacyFixtureAsync("iso-8859-1", "old été coût £\n");
        Write(f, Literal, "new été coût £\n", "iso-8859-1");
        f.Write(":[fichier]* été\t\n1.txt", "adjacent forbidden\n");
        byte[] worktree = File.ReadAllBytes(f.File(Literal));
        GitDiffDocument d = await f.Repository.GetDiffDocumentAsync(Literal);
        Check(d.IsLosslessPatch && d.CanStageEquivalent && d.Patch.Contains("+new été coût £", StringComparison.Ordinal), "Latin1 text is decoded without replacement.");
        Check(d.Patch.Contains("été", StringComparison.Ordinal) && !d.Patch.Contains("Ã©", StringComparison.Ordinal) && !d.Patch.Contains('\ufffd'), "Path headers retain UTF-8 while content uses Latin1.");
        Check(d.PatchBytes.Span.IndexOf((byte)0xe9) >= 0 && d.PatchBytes.Span.IndexOf(new byte[] { 0xc3, 0xa9 }) >= 0, "The actual patch contains both legacy content and UTF-8 path bytes.");
        Check(GitFileEncodingCodec.EncodePatch(d.Patch, d.FileEncodingName).AsSpan().SequenceEqual(d.PatchBytes.Span), "Canonical mixed decoding is byte-reversible.");
        Check(GitAnsiColorParser.StripAnsi(d.Display) == d.Patch, "Colored body decodes with the same codec.");
        GitBinaryResult raw = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["--literal-pathspecs", "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--find-renames", "--find-copies", "--unified=3", "--", Literal]);
        Check(raw.Success && raw.StandardOutput.AsSpan().SequenceEqual(d.PatchBytes.Span), "Stored canonical bytes equal real Git stdout.");
        await f.Repository.ApplyPatchAsync(d.PatchBytes);
        Check((await f.Repository.GetIndexFileBytesAsync(Literal)).AsSpan().SequenceEqual(worktree), "Apply sends the exact raw body bytes to Git.");
        Check(File.ReadAllBytes(f.File(Literal)).AsSpan().SequenceEqual(worktree), "Index staging did not transcode the working file.");
        await f.Repository.ApplyPatchAsync(d.PatchBytes, reverse: true);
        string legacy = await f.Repository.GetDiffAsync(Literal);
        Check(legacy == d.Patch, "The legacy reader shares the exact mixed encoding codec.");
        await f.Repository.ApplyPatchAsync(legacy);
        Check((await f.Repository.GetIndexFileBytesAsync(Literal)).AsSpan().SequenceEqual(worktree), "The public string reader/apply pair preserves Latin1 bytes too.");
    }

    public static async Task SelectedLinesAsync()
    {
        using RepositoryFixture f = await LegacyFixtureAsync("windows-1252", "old € ‘quote’\nsecond old €\n");
        Write(f, Literal, "new € “quote”\nsecond new €\n", "windows-1252");
        GitDiffDocument d = await f.Repository.GetDiffDocumentAsync(Literal);
        Check(d.CanStageEquivalent && d.Patch.Contains("“quote”", StringComparison.Ordinal), "CP1252 smart punctuation and euros decode as glyphs, not Latin1 controls.");
        // Git emits both removed lines before additions. Select one added line only, leaving the other addition unstaged.
        int start = d.Patch.IndexOf("+new € “quote”", StringComparison.Ordinal);
        byte[] selected = GitEncodedPatch.SelectLines(d, start, "+new € “quote”\n".Length)!;
        Check(selected.AsSpan().IndexOf((byte)0x80) >= 0 && selected.AsSpan().IndexOf(new byte[] { 0xe2, 0x82, 0xac }) < 0, "Partial body is CP1252, not UTF-8.");
        await f.Repository.ApplyPatchAsync(selected);
        string index = GitFileEncodingCodec.GetEncoding("windows-1252").GetString(await f.Repository.GetIndexFileBytesAsync(Literal));
        Check(index.Contains("new € “quote”", StringComparison.Ordinal) && !index.Contains("second new", StringComparison.Ordinal), "Actual selected lines reach the index; omitted addition stays unstaged.");
        GitDiffDocument staged = await f.Repository.GetDiffDocumentAsync(Literal, staged: true);
        byte[] undo = GitEncodedPatch.SelectHunks(staged, [0]);
        await f.Repository.ApplyPatchAsync(undo, reverse: true);
        Check((await f.Repository.GetIndexFileBytesAsync(Literal)).AsSpan().SequenceEqual(GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes("old € ‘quote’\nsecond old €\n")), "Reverse application returns the original legacy bytes exactly.");
    }

    public static async Task SelectedHunksAsync()
    {
        string original = "old été\n" + string.Join('\n', Enumerable.Range(0, 15).Select(i => "stable " + i)) + "\nlast old £\n";
        using RepositoryFixture f = await LegacyFixtureAsync("iso-8859-1", original);
        string changed = original.Replace("old été", "new été", StringComparison.Ordinal).Replace("last old £", "last new £", StringComparison.Ordinal);
        Write(f, Literal, changed, "iso-8859-1");
        GitDiffDocument d = await f.Repository.GetDiffDocumentAsync(Literal, options: new(ContextLines: 1));
        Check(GitPatch.Parse(d.Patch).Single().Hunks.Count == 2, "Actual separated Git changes produce two hunks.");
        await f.Repository.ApplyPatchAsync(GitEncodedPatch.SelectHunks(d, [0]));
        string index = GitFileEncodingCodec.GetEncoding("iso-8859-1").GetString(await f.Repository.GetIndexFileBytesAsync(Literal));
        Check(index == original.Replace("old été", "new été", StringComparison.Ordinal), "Selecting one hunk preserves omitted index content.");
        GitDiffDocument remaining = await f.Repository.GetDiffDocumentAsync(Literal);
        int offset = remaining.Patch.IndexOf("+last new £", StringComparison.Ordinal);
        byte[] reset = GitEncodedPatch.SelectResetLines(remaining, offset, "+last new £\n".Length)!;
        await f.Repository.ApplyPatchAsync(reset, staged: false);
        Check(!GitFileEncodingCodec.GetEncoding("iso-8859-1").GetString(File.ReadAllBytes(f.File(Literal))).Contains("last new", StringComparison.Ordinal), "Reset selected lines operates on actual legacy working-tree bytes.");
    }

    public static async Task SelectionBoundsAsync()
    {
        foreach (string newline in new[] { "\n", "\r\n" })
        {
            string original = "old € one" + newline + "old € two" + newline;
            string changed = "new € one" + newline + "new € two" + newline;
            using RepositoryFixture f = await LegacyFixtureAsync("windows-1252", original);
            Write(f, Literal, changed, "windows-1252");
            GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal);
            int first = document.Patch.IndexOf("+new € one", StringComparison.Ordinal);
            int second = document.Patch.IndexOf("+new € two", StringComparison.Ordinal);
            byte[] expectedFirst = GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(original + "new € one" + newline);
            foreach ((int offset, int length) in new (int, int)[] { (first, "+new € one".Length), (first, "+new € one".Length + newline.Length), (first + 3, 0) })
            {
                byte[] patch = GitEncodedPatch.SelectLines(document, offset, length)!;
                await f.Repository.ApplyPatchAsync(patch);
                Check((await f.Repository.GetIndexFileBytesAsync(Literal)).AsSpan().SequenceEqual(expectedFirst), "Content-only, full-line with LF/CRLF, and zero-length caret select only the first real addition.");
                await f.Repository.ApplyPatchAsync((await f.Repository.GetDiffDocumentAsync(Literal, staged: true)).PatchBytes, reverse: true);
            }
            byte[] secondOnly = GitEncodedPatch.SelectLines(document, second, 0)!;
            await f.Repository.ApplyPatchAsync(secondOnly);
            Check((await f.Repository.GetIndexFileBytesAsync(Literal)).AsSpan().SequenceEqual(GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(original + "new € two" + newline)), "A caret at the adjacent row start chooses that row, not its neighbor.");
            await f.Repository.ApplyPatchAsync((await f.Repository.GetDiffDocumentAsync(Literal, staged: true)).PatchBytes, reverse: true);
            Check(GitEncodedPatch.SelectLines(document, document.Patch.Length, 0) is null, "A caret on the trailing empty line does not select a hidden previous change.");
        }
        const string utf8 = "diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1,3 @@\n old\n+first λ\n+second 🦊\n";
        int position = utf8.IndexOf("+first λ", StringComparison.Ordinal);
        string contentOnly = GitPatch.SelectLines(utf8, position, "+first λ".Length)!;
        string withLf = GitPatch.SelectLines(utf8, position, "+first λ\n".Length)!;
        Check(withLf == contentOnly && !withLf.Contains("+second", StringComparison.Ordinal), "The legacy UTF-8 wrapper uses the same half-open boundary as the encoded wrapper.");
        string separated = utf8 + "@@ -20 +22,2 @@\n context\n+separate\n";
        string untilNextHeader = GitPatch.SelectLines(separated, position, separated.IndexOf("@@ -20", StringComparison.Ordinal) - position)!;
        Check(!untilNextHeader.Contains("+separate", StringComparison.Ordinal), "An exclusive boundary at the next hunk header cannot select its added rows.");
    }

    public static async Task RenameAndBareAsync()
    {
        using RepositoryFixture f = await LegacyFixtureAsync("windows-1252", "old €\n" + string.Concat(Enumerable.Repeat("unchanged €\n", 20)));
        string renamed = "renommé\n'\"[*] λ.txt";
        File.Move(f.File(Literal), f.File(renamed));
        Write(f, renamed, "new €\n" + string.Concat(Enumerable.Repeat("unchanged €\n", 20)), "windows-1252");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Real rename");
        GitRevisionChanges revision = await f.Repository.GetRevisionChangesAsync("HEAD");
        GitRevisionFile file = revision.Files.Single(entry => entry.Path == renamed);
        Check(file.OriginalPath == Literal, "Git detected a real literal rename.");
        GitDiffDocument d = await f.Repository.GetRevisionFileDiffDocumentAsync(revision, file);
        Check(d.IsLosslessPatch && d.Patch.Contains("+new €", StringComparison.Ordinal) && d.Patch.Contains("renommé", StringComparison.Ordinal), "Both pathname headers and body retain their different codecs.");
        GitRepository bare = await GitRepository.CloneAsync(f.Repository.Path, Path.Combine(f.Root, "bare.git"), new GitCloneOptions(Bare: true));
        await bare.SetConfigAsync("i18n.filesencoding", "windows-1252");
        GitDiffDocument b = await bare.GetRevisionFileDiffDocumentAsync(revision, file);
        Check(b.PatchBytes.Span.SequenceEqual(d.PatchBytes.Span) && b.Patch == d.Patch, "Bare readers have no worktree dependency.");
        GitRevisionChanges root = await f.Repository.GetRevisionChangesAsync(revision.BaseRevision!);
        GitDiffDocument rootDocument = await f.Repository.GetRevisionFileDiffDocumentAsync(root, root.Files.Single(entry => entry.Path == Literal));
        Check(rootDocument.Patch.Contains("+old €", StringComparison.Ordinal) && rootDocument.IsLosslessPatch, "Root diff-tree retains legacy additions.");
    }

    public static async Task WordDiffAsync()
    {
        foreach (string encoding in new[] { "windows-1252", "iso-8859-1" })
        {
            string symbol = encoding == "windows-1252" ? "€ ‘smart’" : "£ été";
            using RepositoryFixture f = await LegacyFixtureAsync(encoding, "const old " + symbol + "\nunchanged " + symbol + "\n");
            Write(f, Literal, "const new " + symbol + "\nunchanged " + symbol + "\n", encoding);
            GitDiffDocument word = await f.Repository.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.GitWordDiff));
            string plain = GitAnsiColorParser.StripAnsi(word.Display);
            Check(word.IsLosslessPatch && !word.CanStageEquivalent && word.EffectiveAppearance == GitDiffAppearance.GitWordDiff, "Word mode remains readonly regardless of canonical byte preservation.");
            Check(plain.Contains(symbol, StringComparison.Ordinal) && plain.Contains("été", StringComparison.Ordinal) && !plain.Contains('\ufffd') && !plain.Contains('\u0080'), "Unprefixed word/context bodies use the file codec, headers keep UTF-8.");
            Expect<InvalidOperationException>(() => GitEncodedPatch.SelectHunks(word, [0]));
        }
    }

    public static async Task BomPatchAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        byte[] first = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("old été\nunchanged\n")];
        byte[] next = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("new été\nunchanged\n")];
        File.WriteAllBytes(f.File(Literal), first); await f.Repository.StageAsync([Literal]); await f.Repository.CommitAsync("BOM old");
        File.WriteAllBytes(f.File(Literal), next);
        GitDiffDocument d = await f.Repository.GetDiffDocumentAsync(Literal);
        Check(d.Patch.Contains("+\ufeffnew été", StringComparison.Ordinal) && d.CanStageEquivalent, "Unlike the file preview, canonical content retains the BOM character and bytes.");
        await f.Repository.ApplyPatchAsync(GitEncodedPatch.SelectHunks(d, [0]));
        Check((await f.Repository.GetIndexFileBytesAsync(Literal)).AsSpan().SequenceEqual(next), "Partial staging retained the exact BOM once, not removed or doubled.");
        GitDecodedFile preview = GitFileEncodingCodec.DecodeFile(next, "windows-1252");
        Check(preview.HasBom && preview.EncodingName == "utf-8" && preview.Text == "new été\nunchanged\n", "File BOM wins over the repo/session default and is omitted only from preview text.");
    }

    public static async Task UnicodeAndBinaryAsync()
    {
        foreach (Encoding encoding in new Encoding[] { new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            const string value = "été λ 🦊\r\n42\n";
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(value)];
            GitDecodedFile decoded = GitFileEncodingCodec.DecodeFile(bytes, "windows-1252");
            Check(decoded.Text == value && decoded.HasBom && !decoded.IsBinary && decoded.IsLossless && decoded.EncodingName == encoding.WebName, "Every Unicode BOM wins and its complete code points decode strictly.");
            GitDecodedFile noBom = GitFileEncodingCodec.DecodeFile(encoding.GetBytes(value), encoding.WebName);
            Check(noBom.Text == value && !noBom.HasBom, "Explicit Unicode session choice reads a no-BOM file.");
            using RepositoryFixture f = await RepositoryFixture.CreateAsync();
            File.WriteAllBytes(f.File("unicode.txt"), bytes); await f.Repository.StageAsync(["unicode.txt"]); await f.Repository.CommitAsync("Unicode bytes");
            File.WriteAllBytes(f.File("unicode.txt"), [.. encoding.GetPreamble(), .. encoding.GetBytes(value + "new\n")]);
            GitDiffDocument document = await f.Repository.GetDiffDocumentAsync("unicode.txt", displayOptions: new() { FileEncodingName = encoding.WebName });
            Check(!document.CanStageEquivalent && document.PatchBytes.Length > 0, "Raw Unicode Git binary patches remain inspectable; unsafe partial text staging is refused.");
            Expect<InvalidOperationException>(() => GitEncodedPatch.SelectHunks(document, [0]));
        }
        foreach (string codec in new[] { "utf-8", "windows-1252", "iso-8859-1" })
        {
            GitDecodedFile binary = GitFileEncodingCodec.DecodeFile(new byte[] { 0, 1, 0xff, 0x80 }, codec);
            Check(binary.IsBinary && binary.Text.Contains("0001FF80", StringComparison.Ordinal) && !binary.Text.Contains('\ufffd'), "Actual NUL binary data stays a hex view in every single-byte choice.");
        }
    }

    public static async Task InvalidAsync()
    {
        using RepositoryFixture f = await LegacyFixtureAsync("iso-8859-1", "old été\n");
        Write(f, Literal, "new été\n", "iso-8859-1");
        byte[] config = File.ReadAllBytes(f.File(".git/config")), index = File.ReadAllBytes(f.File(".git/index"));
        GitDiffDocument wrong = await f.Repository.GetDiffDocumentAsync(Literal, displayOptions: new() { FileEncodingName = "utf-8" });
        Check(!wrong.IsLosslessPatch && !wrong.CanStageEquivalent && !wrong.Patch.Contains('\ufffd') && wrong.PatchBytes.Span.IndexOf((byte)0xe9) >= 0, "A failed strict decode retains raw bytes and refuses partial apply.");
        Expect<InvalidOperationException>(() => GitEncodedPatch.SelectLines(wrong, 0, 1));
        Expect<EncoderFallbackException>(() => GitFileEncodingCodec.EncodePatch("diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n+€\n", "iso-8859-1"));
        await ExpectAsync<NotSupportedException>(() => f.Repository.GetDiffDocumentAsync(Literal, displayOptions: new() { FileEncodingName = "not-a-codec" }));
        Unchanged(f, config, index);
        await f.Repository.SetConfigAsync("i18n.filesencoding", "not-a-codec");
        byte[] invalidConfig = File.ReadAllBytes(f.File(".git/config"));
        await ExpectAsync<NotSupportedException>(() => f.Repository.GetFileEncodingNameAsync());
        Check(File.ReadAllBytes(f.File(".git/config")).AsSpan().SequenceEqual(invalidConfig), "Unknown repo config is reported, never repaired by a write.");
    }

    public static async Task UntrackedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await f.Repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
        Write(f, Literal, "untracked € “quoted”\n", "windows-1252");
        GitComparison comparison = await f.Repository.GetComparisonAsync(new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitDiffDocument d = await f.Repository.GetComparisonFileDiffDocumentAsync(comparison, comparison.Files.Single(file => file.Path == Literal));
        Check(d.Patch.Contains("+untracked € “quoted”", StringComparison.Ordinal) && d.CanStageEquivalent && d.IsLosslessPatch && d.PatchBytes.Span.IndexOf((byte)0x80) >= 0, "No-index output retains difference exit1 and legacy bytes.");
        Check(GitFileEncodingCodec.EncodePatch(d.Patch, d.FileEncodingName).AsSpan().SequenceEqual(d.PatchBytes.Span), "Untracked absolute literal pathname remains UTF-8 mixed with CP1252 content.");
        Check((await f.Repository.GetStatusAsync()).Changes.Single(change => change.Path == Literal).IsUntracked, "Reading suggestions/diffs does not create an index entry.");
    }

    public static async Task CombinedAndRangeAsync()
    {
        const string shared = "unchanged € ‘context’\nsecond unchanged €\n";
        string commonAddition = string.Concat(Enumerable.Range(0, 12).Select(i => "shared added " + i + " €\n"));
        using RepositoryFixture f = await LegacyFixtureAsync("windows-1252", "root €\n" + shared); GitRepository r = f.Repository;
        string root = await r.ResolveRevisionAsync("HEAD");
        List<string> parents = [];
        for (int i = 0; i < 3; i++)
        {
            await r.RunAsync(["checkout", "-B", "parent" + i, root]); Write(f, Literal, "parent " + i + " €\n" + shared + commonAddition, "windows-1252");
            await r.StageAsync([Literal]); await r.CommitAsync("Shared legacy series"); parents.Add(await r.ResolveRevisionAsync("HEAD"));
        }
        Write(f, Literal, "resolution €\n" + shared, "windows-1252"); await r.StageAsync([Literal]);
        string tree = (await r.RunAsync(["write-tree"])).StandardOutput.Trim();
        string merge = (await new GitRunner().RunAsync(r.Path, ["commit-tree", tree, "-p", parents[0], "-p", parents[1], "-p", parents[2]], standardInput: "Merge\n")).StandardOutput.Trim();
        await r.RunAsync(["update-ref", "HEAD", merge]);
        GitCombinedDiff combined = await r.GetCombinedDiffAsync("HEAD");
        GitDiffDocument d = await r.GetCombinedFileDiffDocumentAsync(combined, combined.Files.Single(), displayOptions: new(Appearance: GitDiffAppearance.GitWordDiff));
        Check(d.Patch.Contains("+++resolution €", StringComparison.Ordinal) && !d.CanStageEquivalent && d.IsLosslessPatch && d.EffectiveAppearance == GitDiffAppearance.Patch, "Three-parent prefix columns and CP1252 content decode without enabling Word or staging.");
        Check(d.Patch.Contains("été", StringComparison.Ordinal) && !d.Patch.Contains("Ã©", StringComparison.Ordinal), "Combined headers preserve the literal Unicode/newline pathname in UTF-8.");
        // Git range-diff's internal patch parser loses the first part of newline
        // pathnames before emitting stdout. Keep that path in the combined case,
        // and use a Unicode/metacharacter literal without newline for range headers.
        const string rangePath = ":[fichier]* été [1].txt";
        using RepositoryFixture ranges = await LegacyFixtureAsync("windows-1252", "root €\n" + shared, rangePath);
        GitRepository rangeRepository = ranges.Repository;
        string rangeBase = await rangeRepository.ResolveRevisionAsync("HEAD");
        List<string> tips = [];
        for (int i = 0; i < 2; i++)
        {
            await rangeRepository.RunAsync(["checkout", "-B", "range" + i, rangeBase]);
            Write(ranges, rangePath, "parent " + i + " €\n" + shared + commonAddition, "windows-1252");
            await rangeRepository.StageAsync([rangePath]); await rangeRepository.CommitAsync("Shared legacy series");
            tips.Add(await rangeRepository.ResolveRevisionAsync("HEAD"));
        }
        GitRangeDiffComparison range = await rangeRepository.CreateRangeDiffComparisonAsync(tips[0], tips[1]);
        GitDiffDocument rangeDoc = await rangeRepository.GetRangeDiffDocumentAsync(range, new(Paths: [rangePath]));
        Check(rangeDoc.Patch.Contains(" ! ", StringComparison.Ordinal) && rangeDoc.PatchBytes.Span.IndexOf((byte)0x80) >= 0, "Git paired the real related series and actually emitted CP1252 body bytes.");
        Check(rangeDoc.Patch.Contains("parent 0 €", StringComparison.Ordinal) && rangeDoc.Patch.Contains("parent 1 €", StringComparison.Ordinal) && !rangeDoc.CanStageEquivalent && !rangeDoc.Patch.Contains('\ufffd'), "The real paired series contains both legacy patch bodies and remains readonly.");
        Check(rangeDoc.Patch.Contains("été", StringComparison.Ordinal) && !rangeDoc.Patch.Contains("Ã©", StringComparison.Ordinal), "Nested file headers retain UTF-8 despite the legacy body and literal metacharacter pathname.");
    }

    public static async Task RaceAndCancellationAsync()
    {
        using RepositoryFixture f = await LegacyFixtureAsync("windows-1252", "old €\n");
        Write(f, Literal, "first €\n", "windows-1252");
        string wrapper = Path.Combine(f.Root, "changing-git");
        string literal = System.Text.Json.JsonSerializer.Serialize(f.File(Literal));
        File.WriteAllText(wrapper, "#!/usr/bin/python3\nimport os,sys\nif '--color=always' in sys.argv[1:]:\n    with open(" + literal + ", 'wb') as f: f.write('second €\\n'.encode('cp1252'))\nos.execvp('git', ['git'] + sys.argv[1:])\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        GitRepository racing = new(f.Repository.Path, new GitRunner(wrapper));
        GitDiffDocument d = await racing.GetDiffDocumentAsync(Literal);
        Check(d.Patch.Contains("first €", StringComparison.Ordinal) && d.Display.Contains("second €", StringComparison.Ordinal) && d.IsLosslessPatch && !d.CanStageEquivalent, "Two real binary reads retain different content and disable selection staging.");
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        byte[] config = File.ReadAllBytes(f.File(".git/config")), index = File.ReadAllBytes(f.File(".git/index"));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.GetDiffDocumentAsync(Literal, cancellationToken: canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.ApplyPatchAsync(d.PatchBytes, cancellationToken: canceled.Token));
        Unchanged(f, config, index);
    }

    private static async Task<RepositoryFixture> LegacyFixtureAsync(string encodingName, string content, string path = Literal)
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await f.Repository.SetConfigAsync("i18n.filesencoding", encodingName);
        Write(f, path, content, encodingName); await f.Repository.StageAsync([path]); await f.Repository.CommitAsync("Legacy source");
        return f;
    }
    private static void Write(RepositoryFixture f, string path, string text, string encoding)
        => File.WriteAllBytes(f.File(path), GitFileEncodingCodec.GetEncoding(encoding).GetBytes(text));
    private static void Unchanged(RepositoryFixture f, byte[] config, byte[] index)
    {
        Check(File.ReadAllBytes(f.File(".git/config")).AsSpan().SequenceEqual(config), "Reading never writes repository config.");
        Check(File.ReadAllBytes(f.File(".git/index")).AsSpan().SequenceEqual(index), "Reading never writes index bytes.");
    }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static void Expect<T>(Action operation) where T : Exception
    { try { operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
