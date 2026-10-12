using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitExtensions.Linux;

internal static class BlameEncodingScenarios
{
    private const string Literal = ":[fichier]* été\t\n[180].txt";
    private const string Adjacent = ":[fichier]* été\t\n1180.txt";
    private const string Author = "Åuteur Ω 🧪";
    private const string Summary = "Révision Ω 🧪 UTF-8";

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Latin1 blame separates UTF8 metadata and file bytes at literal line180", Latin1Async),
        ("CP1252 historical and worktree blame preserve euro and smart quotes", Windows1252Async),
        ("blame session codec overrides do not change repository defaults or metadata", SessionOverrideAsync),
        ("bare renamed blame retains byte content and original literal filename", BareRenameAsync),
        ("blame invalid codec and invalid UTF8 fail strictly without replacement or mutation", InvalidAsync),
        ("blame cancellation interrupts binary reads and descendants without mutation", CancellationAsync)
    ];

    public static async Task Latin1Async()
    {
        const string content = "Café coût £ à la ligne 180";
        using RepositoryFixture fixture = await CreateAsync("iso-8859-1", content);
        Snapshot snapshot = await SnapshotAsync(fixture);
        GitFileBlame blame = await fixture.Repository.GetFileBlameAsync(Literal, "HEAD", new(), CancellationToken.None);
        Check(blame.FileEncodingName == "iso-8859-1", "The effective file codec is exposed without changing the record constructor.");
        Check(blame.Bytes.AsSpan().SequenceEqual(snapshot.Blob), "The raw blob bytes remain exact.");
        Check(blame.Lines.Count == 240 && blame.Lines[179].Line.LineNumber == 180 && blame.Lines[179].Line.OriginalLine == 180, "High line numbers stay mapped to the real file.");
        Check(blame.Lines[179].Line.Text == content, "A Latin1 e9 byte must decode to é, never U+FFFD or mojibake.");
        Check(snapshot.Blob.AsSpan().IndexOf((byte)0xe9) >= 0, "The fixture really stores Latin1, not UTF8 text with a label.");
        Check(blame.Lines.All(line => line.OriginalPath == Literal && line.Line.AuthorName == Author && line.Line.Summary == Summary), "Pathnames, author and summary retain their independent UTF8 metadata codec.");
        Check(blame.Lines.All(line => !line.Line.Text.Contains('\ufffd')), "No replacement characters are introduced.");
        IReadOnlyList<GitBlameLine> compatible = await fixture.Repository.GetBlameAsync(Literal, "HEAD");
        Check(compatible.SequenceEqual(blame.Lines.Select(line => line.Line)), "The existing public blame API shares the byte-safe decoder.");
        await UnchangedAsync(fixture, snapshot);
    }

    public static async Task Windows1252Async()
    {
        const string committed = "Café € ‘gauche’ “droite” ligne 180";
        const string worktree = "Travail Café € ‘nouveau’ “non indexé” ligne 180";
        using RepositoryFixture fixture = await CreateAsync("windows-1252", committed);
        Snapshot original = await SnapshotAsync(fixture);
        GitFileBlame historical = await fixture.Repository.GetFileBlameAsync(Literal, "HEAD", new(true, true, true));
        Check(historical.Lines[179].Line.Text == committed && historical.FileEncodingName == "windows-1252", "Euro and all four smart quotes use real CP1252 bytes.");
        Check(original.Blob.AsSpan().IndexOf((byte)0x80) >= 0 && original.Blob.AsSpan().IndexOf((byte)0x91) >= 0
            && original.Blob.AsSpan().IndexOf((byte)0x93) >= 0, "The fixture contains raw CP1252 punctuation.");
        Check(historical.Lines[179].Line.AuthorName == Author && historical.Lines[179].Line.Summary == Summary, "UTF8 metadata must not be decoded as CP1252.");
        await UnchangedAsync(fixture, original);

        Write(fixture, Literal, Contents(worktree), "windows-1252");
        Snapshot modified = await SnapshotAsync(fixture);
        GitFileBlame current = await fixture.Repository.GetFileBlameAsync(Literal, null);
        IReadOnlyList<GitBlameLine> compatible = await fixture.Repository.GetBlameAsync(Literal);
        Check(current.Revision is null && current.Lines[179].Line.Text == worktree && current.Lines[179].Line.Hash.All(character => character == '0'), "Null revision still blames the actual uncommitted worktree line.");
        Check(compatible[179].Text == worktree && compatible[179].LineNumber == 180 && compatible[179].Hash == current.Lines[179].Line.Hash, "The legacy API's null/worktree default is preserved.");
        Check(current.Bytes.AsSpan().SequenceEqual(modified.Worktree), "Worktree bytes remain distinct from the committed blob.");
        GitFileBlame committedAgain = await fixture.Repository.GetFileBlameAsync(Literal, original.Head);
        Check(committedAgain.Lines[179].Line.Text == committed && committedAgain.Bytes.AsSpan().SequenceEqual(original.Blob), "A resolved historical revision must not read modified worktree contents.");
        await UnchangedAsync(fixture, modified);
    }

    public static async Task SessionOverrideAsync()
    {
        const string content = "Café € ‘session’ “codec” ligne 180";
        using RepositoryFixture fixture = await CreateAsync("windows-1252", content);
        await fixture.Repository.SetConfigAsync("i18n.filesencoding", "iso-8859-1");
        Snapshot snapshot = await SnapshotAsync(fixture);
        GitFileBlame configured = await fixture.Repository.GetFileBlameAsync(Literal);
        GitFileBlame session = await fixture.Repository.GetFileBlameAsync(Literal, "HEAD", new(), CancellationToken.None, "windows-1252");
        Check(configured.FileEncodingName == "iso-8859-1" && configured.Lines[179].Line.Text.Contains('\u0080'), "The configured codec is honored, not guessed from the bytes.");
        Check(session.FileEncodingName == "windows-1252" && session.Lines[179].Line.Text == content, "The final optional parameter changes only the file decoder.");
        Check(session.Bytes.AsSpan().SequenceEqual(configured.Bytes) && session.Lines[179].Line.AuthorName == Author
            && session.Lines[179].Line.Summary == Summary && session.Lines[179].OriginalPath == Literal, "Changing the file codec preserves raw bytes and all UTF8 metadata.");
        Check(await fixture.Repository.GetFileEncodingNameAsync() == "iso-8859-1", "A session choice never persists repository config.");
        await UnchangedAsync(fixture, snapshot);
    }

    public static async Task BareRenameAsync()
    {
        const string renamed = ":[renommé]* Ω\t\n\"180\".txt";
        const string content = "Café € ‘renommé’ “bare” ligne 180";
        using RepositoryFixture fixture = await CreateAsync("windows-1252", content);
        string sourceCommit = await fixture.Repository.ResolveRevisionAsync("HEAD");
        File.Move(fixture.File(Literal), fixture.File(renamed));
        await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Rename Ω 🧪");
        GitRepository bare = await GitRepository.CloneAsync(fixture.Repository.Path, Path.Combine(fixture.Root, "bare.git"), new GitCloneOptions(Bare: true));
        await bare.SetConfigAsync("i18n.filesencoding", "windows-1252");
        byte[] config = File.ReadAllBytes(Path.Combine(bare.Path, "config"));
        string head = await bare.ResolveRevisionAsync("HEAD");
        byte[] bytes = await bare.GetFileBytesAsync(renamed);
        GitFileBlame blame = await bare.GetFileBlameAsync(renamed, "HEAD");
        IReadOnlyList<GitBlameLine> compatible = await bare.GetBlameAsync(renamed, "HEAD");
        Check(blame.FileEncodingName == "windows-1252" && blame.Lines[179].Line.Text == content, "Historical blame needs no worktree and retains the legacy body codec.");
        Check(blame.Lines.All(line => line.Line.Hash == sourceCommit && line.OriginalPath == Literal), "Blame follows the exact rename and C-quoted original Unicode/tab/newline path.");
        Check(compatible.SequenceEqual(blame.Lines.Select(line => line.Line)), "The extracted public reader has the same historical records in a bare repository.");
        Check(blame.Bytes.AsSpan().SequenceEqual(bytes) && (await bare.GetFileBytesAsync(renamed)).AsSpan().SequenceEqual(bytes), "The bare blob is byte-exact before and after reading.");
        Check(head == await bare.ResolveRevisionAsync("HEAD") && File.ReadAllBytes(Path.Combine(bare.Path, "config")).AsSpan().SequenceEqual(config), "Blame does not rewrite refs or effective configuration.");
    }

    public static async Task InvalidAsync()
    {
        using RepositoryFixture fixture = await CreateAsync("windows-1252", "Café € “strict” ligne 180");
        Snapshot snapshot = await SnapshotAsync(fixture);
        await ExpectAsync<DecoderFallbackException>(() => fixture.Repository.GetFileBlameAsync(Literal, fileEncodingName: "utf-8"));
        await ExpectAsync<NotSupportedException>(() => fixture.Repository.GetFileBlameAsync(Literal, fileEncodingName: "not-a-codec"));
        await ExpectAsync<ArgumentException>(() => fixture.Repository.GetFileBlameAsync("../outside"));
        await ExpectAsync<GitCommandException>(() => fixture.Repository.GetBlameAsync("missing.txt", "HEAD"));
        await UnchangedAsync(fixture, snapshot);
        await fixture.Repository.SetConfigAsync("i18n.filesencoding", "utf-8");
        Snapshot wrongDefault = await SnapshotAsync(fixture);
        await ExpectAsync<DecoderFallbackException>(() => fixture.Repository.GetBlameAsync(Literal, "HEAD"));
        await ExpectAsync<DecoderFallbackException>(() => fixture.Repository.GetFileBlameAsync(Literal));
        await UnchangedAsync(fixture, wrongDefault);
    }

    public static async Task CancellationAsync()
    {
        using RepositoryFixture fixture = await CreateAsync("windows-1252", "Café € “cancel” ligne 180");
        Snapshot snapshot = await SnapshotAsync(fixture);
        using CancellationTokenSource alreadyCancelled = new(); alreadyCancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => fixture.Repository.GetFileBlameAsync(Literal, cancellationToken: alreadyCancelled.Token));
        await ExpectAsync<OperationCanceledException>(() => fixture.Repository.GetBlameAsync(Literal, cancellationToken: alreadyCancelled.Token));
        if (OperatingSystem.IsLinux())
        {
            string executable = Path.Combine(fixture.Root, "slow-blame-git"), parentFile = Path.Combine(fixture.Root, "blame.pid"), childFile = Path.Combine(fixture.Root, "blame.child");
            // A controlled process gate delegates every actual repository read
            // to installed Git, then blocks only the requested blame command.
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = blame ]; then\n  printf '%s' \"$$\" > " + Quote(parentFile)
                + "\n  sleep 60 &\n  printf '%s' \"$!\" > " + Quote(childFile) + "\n  wait\n  exit 1\n fi\ndone\nexec git \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository repository = new(fixture.Repository.Path, new GitRunner(executable));
            using CancellationTokenSource cancel = new();
            Task<GitFileBlame> pending = repository.GetFileBlameAsync(Literal, cancellationToken: cancel.Token);
            Stopwatch waiting = Stopwatch.StartNew();
            while ((!File.Exists(childFile) || new FileInfo(childFile).Length == 0) && !pending.IsCompleted && waiting.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
            Check(File.Exists(childFile) && new FileInfo(childFile).Length > 0, "The binary blame command reached its actual process gate.");
            int parent = int.Parse(await File.ReadAllTextAsync(parentFile), CultureInfo.InvariantCulture), child = int.Parse(await File.ReadAllTextAsync(childFile), CultureInfo.InvariantCulture);
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            waiting.Restart();
            while ((Running(parent) || Running(child)) && waiting.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
            Check(!Running(parent) && !Running(child), "Cancellation terminates the blame reader and its descendant.");
        }
        await UnchangedAsync(fixture, snapshot);
    }

    private static async Task<RepositoryFixture> CreateAsync(string encoding, string line180)
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            await fixture.Repository.SetConfigAsync("user.name", Author);
            await fixture.Repository.SetConfigAsync("i18n.filesencoding", encoding);
            await fixture.Repository.SetConfigAsync("i18n.commitencoding", "utf-8");
            await fixture.Repository.SetConfigAsync("i18n.logoutputencoding", "windows-1252");
            Write(fixture, Literal, Contents(line180), encoding);
            fixture.Write(Adjacent, "Adjacent file must never be selected\n");
            await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync(Summary);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static string Contents(string line180)
        => string.Join('\n', Enumerable.Range(1, 240).Select(line => line == 180 ? line180 : "unchanged line " + line.ToString(CultureInfo.InvariantCulture))) + "\n";

    private static void Write(RepositoryFixture fixture, string path, string text, string encoding)
        => File.WriteAllBytes(fixture.File(path), GitFileEncodingCodec.GetEncoding(encoding).GetBytes(text));

    private sealed record Snapshot(string Head, byte[] Blob, byte[] Worktree, byte[] Index, byte[] Config, byte[] Adjacent);
    private static async Task<Snapshot> SnapshotAsync(RepositoryFixture fixture)
        => new(await fixture.Repository.ResolveRevisionAsync("HEAD"), await fixture.Repository.GetFileBytesAsync(Literal), File.ReadAllBytes(fixture.File(Literal)),
            File.ReadAllBytes(fixture.File(".git/index")), File.ReadAllBytes(fixture.File(".git/config")), File.ReadAllBytes(fixture.File(Adjacent)));

    private static async Task UnchangedAsync(RepositoryFixture fixture, Snapshot snapshot)
    {
        Check(await fixture.Repository.ResolveRevisionAsync("HEAD") == snapshot.Head, "Reading blame must not change HEAD.");
        Check((await fixture.Repository.GetFileBytesAsync(Literal)).AsSpan().SequenceEqual(snapshot.Blob), "Reading must not change the committed blob.");
        Check(File.ReadAllBytes(fixture.File(Literal)).AsSpan().SequenceEqual(snapshot.Worktree), "Reading must not change source bytes.");
        Check(File.ReadAllBytes(fixture.File(".git/index")).AsSpan().SequenceEqual(snapshot.Index), "The entire index must remain unchanged.");
        Check(File.ReadAllBytes(fixture.File(".git/config")).AsSpan().SequenceEqual(snapshot.Config), "Reading and codec choices do not persist config.");
        Check(File.ReadAllBytes(fixture.File(Adjacent)).AsSpan().SequenceEqual(snapshot.Adjacent), "A literal path must not match its adjacent glob-shaped filename.");
    }

    private static bool Running(int pid) => LinuxProcessState.IsRunning(pid);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
