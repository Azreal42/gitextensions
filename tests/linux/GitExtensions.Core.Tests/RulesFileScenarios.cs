using System.Text;
using GitExtensions.Linux;

internal static class RulesFileScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("rules editors create UTF8 LF files without staging or committing and Git reads ignore attributes and exclusions", NativeRulesAsync),
        ("mailmap editor updates native and grid identities while immutable commit headers remain raw", MailmapAsync),
        ("rules snapshots preserve mixed EOL missing final EOL and bytes on an unchanged save", MixedEndingsAsync),
        ("rules saves preserve original UTF BOMs and strict codecs independently of configured fallback", BomCodecsAsync),
        ("rules editors retain configured CP1252 Latin1 and explicit legacy overrides", LegacyCodecsAsync),
        ("rules editors refuse binary invalid decoding and unrepresentable edits before changing file bytes or modes", InvalidCodecsAsync),
        ("rules saves reject external edits creation deletion replacement and permission changes", ExternalChangesAsync),
        ("rules saves retain readonly modes symbolic links hardlinks and legitimate outside targets", LinkedTargetsAsync),
        ("rules saves reject changed parent links target links and replaced targets without overwriting either destination", ChangedLinksAsync),
        ("rules snapshots allow absent nested directories and dangling targets but reject directories cycles and path escape", MissingAndPathsAsync),
        ("rules local exclusions use the common Git path in a linked worktree and root rules remain independent", WorktreesAsync),
        ("rules operations reject bare repositories foreign snapshots invalid patterns and precancellation unchanged", ValidationAsync),
        ("ignore literal helpers escape glob comment negation spaces and shell-looking names for real check-ignore", LiteralPatternsAsync),
        ("ignore raw pattern appends preserve original order case duplicates negations and EOL", PatternOrderAsync),
        ("ignore native preview includes tracked modified and untracked matches once and remains readonly", PreviewAsync),
        ("ignore root subdirectory anchors directories wildcard and extension patterns follow native Git matching", ScopedPatternsAsync),
    ];

    private static async Task NativeRulesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index")); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.SetConfigAsync("i18n.filesEncoding", "windows-1252");
        GitRulesFileSnapshot ignore = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        Check(!ignore.Exists && ignore.Text == "" && ignore.EncodingName == "utf-8" && !ignore.HasBom && ignore.NewLine == "\n", "New files use UTF8 without BOM and LF even when an existing-file fallback is configured.");
        ignore = await f.Repository.SaveRulesFileAsync(ignore, "/generated/");
        Check(ignore.Exists && ignore.Text == "/generated/\n" && File.ReadAllBytes(ignore.Path).SequenceEqual("/generated/\n"u8.ToArray()), "A changed save creates the exact root rule and final LF.");
        f.Write("generated/file.txt", "ignored"); f.Write("private.log", "local"); f.Write("data.bin", "value");
        GitRulesFileSnapshot exclude = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.LocalExclude);
        await f.Repository.AppendIgnorePatternsAsync(exclude, ["/private.log"]);
        GitRulesFileSnapshot attributes = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Attributes);
        await f.Repository.SaveRulesFileAsync(attributes, "*.bin -text custom=value");
        Check((await f.Repository.RunAsync(["check-ignore", "--", "generated/file.txt", "private.log"])).StandardOutput == "generated/file.txt\nprivate.log\n", "Both shared ignore and local exclude are actually read by installed Git.");
        Check((await f.Repository.RunAsync(["check-attr", "text", "custom", "--", "data.bin"])).StandardOutput == "data.bin: text: unset\ndata.bin: custom: value\n", "Native Git reads the attributes editor's real file.");
        byte[] actualIndex = await File.ReadAllBytesAsync(f.File(".git/index"));
        Check(head == await f.Repository.ResolveRevisionAsync("HEAD") && index.SequenceEqual(actualIndex), "No editor or append implicitly stages files or commits.");
    }

    private static async Task MailmapAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitCommit before = (await f.Repository.GetLogAsync()).Single();
        GitRulesFileSnapshot mailmap = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.MailMap);
        await f.Repository.SaveRulesFileAsync(mailmap, "Canonical Name <canonical@example.invalid> Linux Test <linux-test@example.invalid>");
        string native = (await f.Repository.RunAsync(["check-mailmap", "Linux Test <linux-test@example.invalid>"])).StandardOutput;
        Check(native == "Canonical Name <canonical@example.invalid>\n", "The written mailmap changes actual native identity mapping.");
        GitCommit mapped = (await f.Repository.GetLogAsync()).Single();
        Check(mapped.Hash == before.Hash && mapped.AuthorName == "Canonical Name" && mapped.AuthorEmail == "canonical@example.invalid", "The history grid uses Windows RevisionReader's %aN/%aE mapping.");
        string raw = (await f.Repository.RunAsync(["cat-file", "commit", "HEAD"])).StandardOutput;
        Check(raw.Contains("author Linux Test <linux-test@example.invalid>", StringComparison.Ordinal), "Mapping presentation never rewrites raw object identities.");
        GitRulesFileSnapshot refreshed = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.MailMap);
        await f.Repository.SaveRulesFileAsync(refreshed, "Second Name <second@example.invalid> Linux Test <linux-test@example.invalid>");
        Check((await f.Repository.GetLogAsync()).Single().AuthorName == "Second Name", "An ordinary subsequent refresh reads the changed mailmap without cached identities.");
    }

    private static async Task MixedEndingsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string original = "# first\r\n# second\nlast";
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(original)]; await File.WriteAllBytesAsync(f.File(".gitignore"), bytes);
        DateTime stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc); File.SetLastWriteTimeUtc(f.File(".gitignore"), stamp);
        GitRulesFileSnapshot snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        Check(snapshot.Text == original && snapshot.NewLine == "\r\n" && snapshot.HasBom, "Mixed original line endings and the absence of a final line ending are represented exactly.");
        await f.Repository.SaveRulesFileAsync(snapshot, snapshot.Text);
        byte[] untouched = await File.ReadAllBytesAsync(snapshot.Path);
        Check(bytes.SequenceEqual(untouched) && File.GetLastWriteTimeUtc(snapshot.Path) == stamp, "An unchanged save performs no write or line-ending normalization.");
        GitRulesFileSnapshot saved = await f.Repository.SaveRulesFileAsync(snapshot, original.Replace("second", "changed", StringComparison.Ordinal));
        Check(saved.Text == "# first\r\n# changed\nlast\r\n" && saved.HasBom, "Editing preserves interior mixed endings and adds the preferred final ending.");
    }

    private static async Task BomCodecsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        foreach ((string name, byte[] bom) in new (string, byte[])[] { ("utf-8", [0xef, 0xbb, 0xbf]), ("utf-16", [0xff, 0xfe]), ("utf-16BE", [0xfe, 0xff]), ("utf-32", [0xff, 0xfe, 0, 0]), ("utf-32BE", [0, 0, 0xfe, 0xff]) })
        {
            Encoding codec = GitFileEncodingCodec.GetEncoding(name);
            await File.WriteAllBytesAsync(f.File(".gitattributes"), [.. bom, .. codec.GetBytes("# café\r\n")]);
            GitRulesFileSnapshot snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Attributes, encodingName: "windows-1252");
            Check(snapshot.EncodingName == codec.WebName && snapshot.HasBom && snapshot.Text == "# café\r\n", "A detected BOM takes priority over the fallback codec: " + name);
            await f.Repository.SaveRulesFileAsync(snapshot, "# changed λ");
            Check((await File.ReadAllBytesAsync(snapshot.Path)).SequenceEqual(bom.Concat(codec.GetBytes("# changed λ\r\n"))), "Saving retains the exact endian BOM and original codec: " + name);
        }
    }

    private static async Task LegacyCodecsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        foreach (string name in new[] { "windows-1252", "iso-8859-1" })
        {
            Encoding codec = GitFileEncodingCodec.GetEncoding(name); string text = name == "windows-1252" ? "# café €\r\n*.log" : "# café ÿ\r\n*.log";
            await f.Repository.SetConfigAsync("i18n.filesEncoding", name); await File.WriteAllBytesAsync(f.File(".gitignore"), codec.GetBytes(text));
            GitRulesFileSnapshot snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
            Check(snapshot.Text == text && snapshot.EncodingName == codec.WebName && !snapshot.HasBom, "The configured legacy codec is read strictly.");
            await f.Repository.SaveRulesFileAsync(snapshot, text + "\r\n*.tmp");
            Check((await File.ReadAllBytesAsync(snapshot.Path)).SequenceEqual(codec.GetBytes(text + "\r\n*.tmp\r\n")), "Legacy rules retain their actual bytes and CRLF on save.");
        }
        await f.Repository.SetConfigAsync("i18n.filesEncoding", "utf-8");
        await ExpectAsync<DecoderFallbackException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore));
        Check((await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, encodingName: "iso-8859-1")).Text.Contains('ÿ'), "The user can explicitly reload an undecodable file in its real codec without writing configuration.");
    }

    private static async Task InvalidCodecsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        foreach (byte[] bytes in new byte[][] { [0xc3, 0x28], [0, 1, 2], [0xff, 0xfe, 0x41] })
        {
            await File.WriteAllBytesAsync(f.File(".gitignore"), bytes);
            await ExpectAsync<DecoderFallbackException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore));
            byte[] untouched = await File.ReadAllBytesAsync(f.File(".gitignore"));
            Check(bytes.SequenceEqual(untouched), "Invalid text never becomes a replacement-character editing baseline.");
        }
        await File.WriteAllTextAsync(f.File(".gitignore"), "# ascii\n");
        GitRulesFileSnapshot snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, encodingName: "us-ascii");
        await ExpectAsync<EncoderFallbackException>(() => f.Repository.SaveRulesFileAsync(snapshot, "# λ"));
        await ExpectAsync<ArgumentException>(() => f.Repository.SaveRulesFileAsync(snapshot, "a\0b"));
        Check(await File.ReadAllTextAsync(snapshot.Path) == snapshot.Text, "An unrepresentable edit is rejected before opening the file for writing.");
    }

    private static async Task ExternalChangesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await File.WriteAllTextAsync(f.File(".gitignore"), "original\n");
        GitRulesFileSnapshot original = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        await File.WriteAllTextAsync(original.Path, "external\n");
        await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(original, "editor"));
        Check(await File.ReadAllTextAsync(original.Path) == "external\n", "An external content edit is preserved.");
        GitRulesFileSnapshot current = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        string replacement = f.File("replacement"); await File.WriteAllTextAsync(replacement, current.Text); File.Move(replacement, current.Path, overwrite: true);
        await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(current, "editor"));
        File.Delete(current.Path); await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(current, "editor"));
        GitRulesFileSnapshot absent = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        await File.WriteAllTextAsync(absent.Path, "appeared\n");
        await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(absent, "editor"));
        Check(await File.ReadAllTextAsync(absent.Path) == "appeared\n", "A file created after an absent snapshot is never overwritten.");
        if (!OperatingSystem.IsWindows())
        {
            GitRulesFileSnapshot mode = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
            File.SetUnixFileMode(mode.Path, UnixFileMode.UserRead);
            await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(mode, "editor"));
            Check(File.GetUnixFileMode(mode.Path) == UnixFileMode.UserRead, "An external permissions change is retained.");
        }
    }

    private static async Task LinkedTargetsAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string outside = System.IO.Path.Combine(f.Root, "outside rules λ"); await File.WriteAllTextAsync(outside, "# readonly\r\n");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(outside, mode); File.CreateSymbolicLink(f.File(".gitignore"), "../outside rules λ");
        GitRulesFileSnapshot linked = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        Check(linked.ResolvedPath == outside && linked.Mode == mode, "A legitimate external link target is editable rather than rejected by path containment.");
        await f.Repository.SaveRulesFileAsync(linked, "# edited");
        Check(new FileInfo(linked.Path).LinkTarget == "../outside rules λ" && File.GetUnixFileMode(outside) == mode && await File.ReadAllTextAsync(outside) == "# edited\r\n", "The symbolic link and its original readonly mode survive the write.");
        string hardlink = f.File("hardlink"); GitResult linkedResult = await new GitRunner("ln").RunAsync(f.Repository.Path, ["--", outside, hardlink]);
        Check(linkedResult.Success, "The hardlink fixture is real.");
        linked = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        await f.Repository.SaveRulesFileAsync(linked, "# hardlink edit");
        Check(await File.ReadAllTextAsync(hardlink) == "# hardlink edit\r\n" && File.GetUnixFileMode(hardlink) == mode, "Writing the owned inode also preserves existing hardlink behavior.");
    }

    private static async Task ChangedLinksAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string first = System.IO.Path.Combine(f.Root, "first"), second = System.IO.Path.Combine(f.Root, "second");
        await File.WriteAllTextAsync(first, "first\n"); await File.WriteAllTextAsync(second, "second\n");
        File.CreateSymbolicLink(f.File(".gitignore"), first);
        GitRulesFileSnapshot snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        File.Delete(snapshot.Path); File.CreateSymbolicLink(snapshot.Path, second);
        await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(snapshot, "editor"));
        Check(await File.ReadAllTextAsync(first) == "first\n" && await File.ReadAllTextAsync(second) == "second\n", "A retargeted symbolic link never writes either old or new target.");
        snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        string temp = second + ".replacement"; await File.WriteAllTextAsync(temp, "second\n"); File.Move(temp, second, overwrite: true);
        await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(snapshot, "editor"));
        string one = System.IO.Path.Combine(f.Root, "dir-one"), two = System.IO.Path.Combine(f.Root, "dir-two"); Directory.CreateDirectory(one); Directory.CreateDirectory(two);
        Directory.CreateSymbolicLink(f.File("rules-dir"), one);
        GitRulesFileSnapshot parent = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, "rules-dir");
        Directory.Delete(f.File("rules-dir")); Directory.CreateSymbolicLink(f.File("rules-dir"), two);
        await ExpectAsync<IOException>(() => f.Repository.SaveRulesFileAsync(parent, "editor"));
        Check(!File.Exists(System.IO.Path.Combine(one, ".gitignore")) && !File.Exists(System.IO.Path.Combine(two, ".gitignore")), "A replaced parent link cannot redirect creation.");
    }

    private static async Task MissingAndPathsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitRulesFileSnapshot nested = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, "new/deep");
        Check(!nested.Exists && nested.Directory == "new/deep", "Absent nested directories produce an editable missing baseline.");
        await f.Repository.AppendIgnorePatternsAsync(nested, ["/only-here"]);
        Check(await File.ReadAllTextAsync(f.File("new/deep/.gitignore")) == "/only-here\n", "Saving can create the missing parent directories and file.");
        await ExpectAsync<ArgumentException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, "../escape"));
        await ExpectAsync<ArgumentException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, ".git/objects"));
        await ExpectAsync<ArgumentException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.LocalExclude, "new"));
        Directory.CreateDirectory(f.File(".gitattributes")); await ExpectAsync<IOException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Attributes));
        if (!OperatingSystem.IsWindows())
        {
            string missing = System.IO.Path.Combine(f.Root, "new outside", "rules"); File.CreateSymbolicLink(f.File(".gitignore"), missing);
            GitRulesFileSnapshot dangling = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
            await f.Repository.SaveRulesFileAsync(dangling, "/made");
            Check(new FileInfo(dangling.Path).LinkTarget == missing && await File.ReadAllTextAsync(missing) == "/made\n", "A legitimate dangling link can create its target while preserving the link.");
            File.Delete(dangling.Path); File.CreateSymbolicLink(dangling.Path, ".gitignore");
            await ExpectAsync<IOException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore));
        }
    }

    private static async Task WorktreesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string linkedPath = System.IO.Path.Combine(f.Root, "linked λ\n"); await f.Repository.RunAsync(["worktree", "add", "--detach", linkedPath, "HEAD"]);
        GitRepository linked = await GitRepository.OpenAsync(linkedPath);
        GitRulesFileSnapshot local = await linked.GetRulesFileSnapshotAsync(GitRulesFileKind.LocalExclude), primary = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.LocalExclude);
        Check(local.Path == primary.Path && local.Path == f.File(".git/info/exclude"), "Local exclusions are resolved by Git's shared --git-path rather than linked .git being treated as a directory.");
        await linked.AppendIgnorePatternsAsync(local, ["/linked-local.tmp"]);
        Check((await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.LocalExclude)).Text.Contains("/linked-local.tmp", StringComparison.Ordinal), "Both worktrees observe the same info/exclude file.");
        GitRulesFileSnapshot root = await linked.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore); await linked.SaveRulesFileAsync(root, "/linked-only");
        Check(File.Exists(System.IO.Path.Combine(linkedPath, ".gitignore")) && !File.Exists(f.File(".gitignore")), "Ordinary root rule files remain specific to their worktree.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        GitRulesFileSnapshot snapshot = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, cancellationToken: cancelled.Token));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.SaveRulesFileAsync(snapshot, "x", cancelled.Token));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.AppendIgnorePatternsAsync(snapshot, ["x"], cancelled.Token));
        await ExpectAsync<OperationCanceledException>(() => f.Repository.GetIgnoredFilesAsync(["*.log"], cancelled.Token));
        foreach (string pattern in new[] { "a\0b", "a\nb", "a\rb" }) { await ExpectAsync<ArgumentException>(() => f.Repository.AppendIgnorePatternsAsync(snapshot, [pattern])); }
        GitRepository bare = await GitRepository.InitAsync(System.IO.Path.Combine(f.Root, "bare.git"), bare: true);
        foreach (GitRulesFileKind kind in Enum.GetValues<GitRulesFileKind>()) { await ExpectAsync<InvalidOperationException>(() => bare.GetRulesFileSnapshotAsync(kind)); }
        await ExpectAsync<InvalidOperationException>(() => bare.SaveRulesFileAsync(snapshot, "x"));
        await ExpectAsync<InvalidOperationException>(() => bare.GetIgnoredFilesAsync(["x"]));
        using RepositoryFixture other = await RepositoryFixture.CreateAsync();
        await ExpectAsync<ArgumentException>(() => other.Repository.SaveRulesFileAsync(snapshot, "x"));
        GitRulesFileSnapshot attributes = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Attributes);
        await ExpectAsync<ArgumentException>(() => f.Repository.AppendIgnorePatternsAsync(attributes, ["x"]));
        Check(!File.Exists(snapshot.Path) && !File.Exists(other.File(".gitignore")), "Invalid or cancelled requests create no rule files.");
        Check(!(await f.Repository.AppendIgnorePatternsAsync(snapshot, [])).Exists && !File.Exists(snapshot.Path), "Empty Ignore appends retain the original absent file.");
        foreach (GitRulesFileKind kind in new[] { GitRulesFileKind.Attributes, GitRulesFileKind.MailMap })
        {
            GitRulesFileSnapshot empty = await f.Repository.GetRulesFileSnapshotAsync(kind);
            GitRulesFileSnapshot saved = await f.Repository.SaveRulesFileAsync(empty, "");
            Check(saved.Exists && saved.Text == "\n" && (await File.ReadAllBytesAsync(saved.Path)).SequenceEqual("\n"u8.ToArray()),
                "Explicit Save creates the absent Attributes/Mailmap file with the Windows editor's final EOL.");
        }
    }

    private static async Task LiteralPatternsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string[] files = ["[one]*?.txt", "!important", "#comment", "trailing space ", "a\\b", "$(touch-marker);'λ", "same.TXT", "same.txt"];
        foreach (string file in files) { f.Write(file, "untracked"); }
        f.Write("oZZ.txt", "decoy"); f.Write("nested/!important", "decoy");
        GitRulesFileSnapshot ignore = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        await f.Repository.AppendIgnorePatternsAsync(ignore, files.Select(file => GitIgnorePatterns.ForPath(file)).ToArray());
        foreach (string file in files) { Check((await f.Repository.RunAsync(["check-ignore", "--", file])).Success, "The exact literal filename is ignored: " + file); }
        GitResult decoys = await new GitRunner().RunAsync(f.Repository.Path, ["check-ignore", "--", "oZZ.txt", "nested/!important"]);
        Check(decoys.ExitCode == 1 && !File.Exists(f.File("touch-marker")), "Literal stars, anchors, comment/negation prefixes and shell-looking filenames never broaden or execute.");
        foreach (string invalid in new[] { "x\ny", "x\0y", "../escape", "/absolute" })
        { try { GitIgnorePatterns.ForPath(invalid); throw new InvalidOperationException("Expected invalid literal path."); } catch (ArgumentException) { } }
    }

    private static async Task PatternOrderAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await File.WriteAllTextAsync(f.File(".gitignore"), "# existing\r\n");
        GitRulesFileSnapshot ignore = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        GitRulesFileSnapshot saved = await f.Repository.AppendIgnorePatternsAsync(ignore, ["*.log", "!keep.log", "*.log", "UPPER", "upper", "# comment", ""]);
        Check(saved.Text == "# existing\r\n*.log\r\n!keep.log\r\n*.log\r\nUPPER\r\nupper\r\n# comment\r\n", "Raw pattern append never sorts, folds case or removes a meaningful repeated rule.");
        f.Write("keep.log", "ignored after re-inclusion");
        Check((await f.Repository.RunAsync(["check-ignore", "--", "keep.log"])).Success, "The last duplicate exclusion remains effective after the intervening negation.");
        GitRulesFileSnapshot empty = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.LocalExclude);
        Check((await f.Repository.AppendIgnorePatternsAsync(empty, [])).ContentHash == empty.ContentHash, "No patterns perform no content edit.");
    }

    private static async Task PreviewAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write("tracked.log", "tracked"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Tracked log");
        f.Write("tracked.log", "modified"); f.Write("untracked.log", "untracked"); f.Write("keep.txt", "keep");
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        IReadOnlyList<string> matches = await f.Repository.GetIgnoredFilesAsync(["*.log", "*.log"]);
        Check(matches.Order(StringComparer.Ordinal).SequenceEqual(["tracked.log", "untracked.log"]), "The original preview includes tracked/modified/untracked matches and deduplicates paths only.");
        byte[] actualIndex = await File.ReadAllBytesAsync(f.File(".git/index"));
        Check(index.SequenceEqual(actualIndex) && !File.Exists(f.File(".gitignore")), "Preview neither stages nor writes any rules file.");
        Check((await f.Repository.GetIgnoredFilesAsync(["", "   "])).Count == 0, "Empty and whitespace-only preview input reports no matches.");
        Check((await f.Repository.GetIgnoredFilesAsync(["*.log", "!untracked.log"])).SequenceEqual(["tracked.log"]), "Preview preserves the ordered native negation semantics.");
    }

    private static async Task ScopedPatternsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        foreach (string file in new[] { "root.log", "nested/root.log", "nested/only.txt", "nested/deep/only.txt", "nested/cache/a", "nested/deep/cache/a", "other/only.txt" }) { f.Write(file, "value"); }
        GitRulesFileSnapshot root = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        await f.Repository.AppendIgnorePatternsAsync(root, [GitIgnorePatterns.ForPath("root.log")]);
        GitRulesFileSnapshot nested = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore, "nested");
        await f.Repository.AppendIgnorePatternsAsync(nested, [GitIgnorePatterns.ForPath("only.txt"), GitIgnorePatterns.ForPath("cache", directory: true), GitIgnorePatterns.ForExtension(".log")]);
        Check((await f.Repository.RunAsync(["check-ignore", "--", "root.log", "nested/root.log", "nested/only.txt", "nested/cache/a"])).StandardOutput == "root.log\nnested/root.log\nnested/only.txt\nnested/cache/a\n", "Anchors are relative to their ignore file; raw extension wildcards follow native Git.");
        GitResult others = await new GitRunner().RunAsync(f.Repository.Path, ["check-ignore", "--", "nested/deep/only.txt", "nested/deep/cache/a", "other/only.txt"]);
        Check(others.ExitCode == 1, "Nested anchors and directory suffixes do not leak into deeper or sibling directories.");
    }

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
