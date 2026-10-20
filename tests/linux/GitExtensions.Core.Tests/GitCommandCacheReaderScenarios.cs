using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GitCommands.Settings;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class GitCommandCacheReaderScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("command cache real parent reads hit and preserve different ancestry", Guard(ParentsAsync)),
        ("command cache real left-right counts hit and partition reversed bounds", Guard(CountsAsync)),
        ("command cache real merge-base hits and partitions different immutable bounds", Guard(MergeBaseAsync)),
        ("command cache real changed-file lists hit and partition reversed immutable bounds", Guard(FileListsAsync)),
        ("command cache canonical immutable file diffs hit and separate context and whitespace options", Guard(DiffOptionsAsync)),
        ("command cache colored word and codec presentations hit their true Git results independently", Guard(DiffDisplaysAsync)),
        ("command cache real combined content hits and partitions merge options while list stays fresh", Guard(CombinedAsync)),
        ("command cache real range-diff hits and partitions explicit bounds summary and coloring", Guard(RangeAsync)),
        ("command cache real comparison tree hits and index source stays fresh", Guard(ComparisonTreeAsync)),
        ("command cache real object previews hit without caching recovery object validation", Guard(ObjectPreviewAsync)),
        ("command cache native batch exit0 missing objects are rejected then repaired without Clear", Guard(MissingBatchAsync)),
        ("command cache encoded grep temp patterns hit and preserve files lines options and actual bytes", Guard(GrepAsync)),
        ("command cache no-match grep remains native and worktree index sources never admit", Guard(GrepMutableAsync)),
        ("command cache real blame hits and separates whitespace and source codecs", Guard(BlameAsync)),
        ("command cache real preceding blame line diff hits and separates histogram options", Guard(PreviousBlameAsync)),
        ("command cache real difftool hits and separates viewport all effective DFT variables and executable bytes", Guard(DifftasticAsync)),
        ("command cache attribute bytes changed on the same inode length and timestamp invalidate a positive diff hit", Guard(AttributesAsync)),
        ("command cache missing and inactive includes becoming present invalidate positive colored diff hits", Guard(IncludesAsync)),
        ("command cache absent global attributes and changed symbolic link targets invalidate positive diff hits", Guard(GlobalAttributesAsync)),
        ("command cache external replace and shallow authority changes invalidate actual ancestry and trees", Guard(GraphAuthorityAsync)),
        ("command cache linked worktrees partition identical immutable object reads", Guard(LinkedWorktreesAsync)),
        ("command cache config changed during the real factory returns native success without retention", Guard(() => InFlightAuthorityAsync(false))),
        ("command cache attributes changed during the real factory return native success without retention", Guard(() => InFlightAuthorityAsync(true))),
        ("command cache display filters only prefix config while full key and literal post-verb option survive", Guard(DisplayKeyAsync)),
        ("command cache null revision arguments keep native validation before any command or admission", Guard(NullRevisionAsync)),
        ("command cache bootstrap native observation begins on a worker rather than its dedicated caller", Guard(BootstrapWorkerAsync)),
        ("command cache difftool permits known simple operands but bypasses unknown shell expansions", Guard(DifftasticUnknownExpansionAsync)),
        ("command cache real tree preview caches both binary object and pretty listing without caching Recovery", Guard(TreePreviewAsync)),
        ("command cache raw tree and blob hits ignore an unreadable unrelated worktree subtree", Guard(RawAuthorityAsync)),
        ("command cache positive blame hits become fresh while any textconv dependency is unverified", Guard(BlameTextconvAsync)),
        ("command cache native replacement shallow and graft override files invalidate positive immutable hits", Guard(GraphOverridesAsync)),
        ("command cache unclosed Git scripts stay fresh when stable executable bytes read a changed external marker", Guard(ScriptGitBypassAsync))
    ];

    private static Func<Task> Guard(Func<Task> action) => async () =>
    {
        GitEnvironmentOptions options = GitEnvironment.Options; GitCommandLogCapturePolicy policy = GitCommandLog.CapturePolicy;
        string? fallback = GitRunner.AskpassFallbackPath;
        string[] names = ["GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_OPTIONAL_LOCKS", "GIT_ASKPASS", "SSH_ASKPASS", "DISPLAY", "WAYLAND_DISPLAY",
            "DFT_COLOR", "DFT_BACKGROUND", "DFT_SYNTAX_HIGHLIGHT", "DFT_CONTEXT", "DFT_STRIP_CR", "DFT_WIDTH"];
        Dictionary<string, string?> previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        GitEnvironment.Configure(new() { Executable = "/usr/bin/git" }); GitCommandLog.ConfigureCapture(new(false)); GitRunner.AskpassFallbackPath = null;
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null"); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1"); Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        foreach (string name in names.Skip(3)) { Environment.SetEnvironmentVariable(name, null); }
        Reset(); GitCommandLog.Changed += ObserveNativeCommands;
        try { await action(); }
        finally
        {
            GitCommandLog.Changed -= ObserveNativeCommands;
            Reset(); GitEnvironment.Configure(options); GitCommandLog.ConfigureCapture(policy); GitRunner.AskpassFallbackPath = fallback;
            foreach ((string name, string? value) in previous) { Environment.SetEnvironmentVariable(name, value); }
        }
    };
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    // The product journal evicts after 500 rows. Observe each confirmed native
    // launch once so its later eviction cannot look like a cache hit in this oracle.
    private static readonly ConcurrentDictionary<long, string> NativeCommands = new();
    private static void ObserveNativeCommands()
    {
        foreach (GitCommandLogEntry entry in GitCommandLog.Snapshot())
        { if (entry.ProcessId is > 0) { NativeCommands.TryAdd(entry.Id, entry.Command); } }
    }
    private static void Reset() { GitCommandCache.Shared.Clear(); GitCommandLog.Clear(); NativeCommands.Clear(); }
    private static int Calls(string verb) => NativeCommands.Values.Count(command => command == "git " + verb);
    private static async Task<(T First, T Second)> Hit<T>(string verb, Func<Task<T>> read)
    {
        int before = Calls(verb); T first = await read(); int native = Calls(verb);
        Check(native > before, "Initial " + verb + " did not really execute.");
        T second = await read(); Check(Calls(verb) == native, "Repeated " + verb + " launched another target process; always-decline is not an accepted cache.");
        return (first, second);
    }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private const string Literal = "literal [1] λ.txt";
    private sealed record Pair(RepositoryFixture Seed, string First, string Second) : IDisposable
    { internal GitRepository Repository => Seed.Repository; public void Dispose() => Seed.Dispose(); }
    private static async Task<Pair> PairAsync()
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync();
        try
        {
            await f.Repository.SetConfigAsync("core.autocrlf", "false");
            f.Write(Literal, "one\ntwo\nthree\nfour\nfive\nold  value λ\nseven\neight\nnine\nten\n");
            await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("before"); string first = await f.Repository.ResolveRevisionAsync("HEAD");
            f.Write(Literal, "one\ntwo\nthree\nfour\nfive\nnew value 終\nseven\neight\nnine\nten\n");
            await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("after"); string second = await f.Repository.ResolveRevisionAsync("HEAD");
            return new(f, first, second);
        }
        catch { f.Dispose(); throw; }
    }
    private static GitRevisionChanges Changes(Pair p) => new(p.Second, p.First, [new(Literal, 'M')]);
    private static bool SameTree(IEnumerable<GitComparisonTreeEntry> a, IEnumerable<GitComparisonTreeEntry> b)
        => a.Select(entry => (entry.Path, entry.Mode, entry.Hash)).SequenceEqual(b.Select(entry => (entry.Path, entry.Mode, entry.Hash)));

    private static async Task ParentsAsync()
    {
        using Pair p = await PairAsync(); Reset();
        var (first, second) = await Hit("rev-list", () => p.Repository.GetRevisionChangesAsync(p.Second));
        Check(first.BaseRevision == p.First && second.BaseRevision == p.First && first.Files.SequenceEqual(second.Files), "Actual immutable parent changed on hit.");
        int calls = Calls("rev-list"); GitRevisionChanges previous = await p.Repository.GetRevisionChangesAsync(p.First);
        Check(previous.BaseRevision != p.First && Calls("rev-list") > calls, "Different commit ancestry collided.");
    }
    private static async Task CountsAsync()
    {
        using Pair p = await PairAsync(); Reset();
        var (a,b) = await Hit("rev-list", () => p.Repository.GetComparisonCommitCountsAsync(new(p.First), new(p.Second)));
        Check(a is { BaseOnly:0, CompareOnly:1 } && b == a, "Real left/right counts changed.");
        int calls = Calls("rev-list"); GitComparisonCommitCounts? reverse = await p.Repository.GetComparisonCommitCountsAsync(new(p.Second), new(p.First));
        Check(reverse is { BaseOnly:1, CompareOnly:0 } && Calls("rev-list") > calls, "Reversed immutable bounds collided.");
    }
    private static async Task MergeBaseAsync()
    {
        using Pair p = await PairAsync(); string oldest = (await p.Repository.GetRevisionChangesAsync(p.First)).BaseRevision!; Reset();
        var (a,b) = await Hit("merge-base", () => p.Repository.GetComparisonMergeBaseAsync(new(p.First),new(p.Second)));
        Check(a == p.First && b == a, "Actual merge-base was lost.");
        int calls = Calls("merge-base"); Check(await p.Repository.GetComparisonMergeBaseAsync(new(oldest),new(p.Second)) == oldest && Calls("merge-base") > calls, "Different merge-base pair collided.");
    }
    private static async Task FileListsAsync()
    {
        using Pair p = await PairAsync(); Reset();
        var (a,b) = await Hit("diff", () => p.Repository.GetComparisonAsync(new(p.First),new(p.Second)));
        Check(a.Files.Single().Path == Literal && a.Files.SequenceEqual(b.Files), "Real changed-file list did not survive hit.");
        int calls = Calls("diff"); GitComparison reverse = await p.Repository.GetComparisonAsync(new(p.Second),new(p.First));
        Check(reverse.Base.Revision == p.Second && reverse.Compare.Revision == p.First && Calls("diff") > calls, "Changed-file bounds were omitted from identity.");
    }
    private static async Task DiffOptionsAsync()
    {
        using Pair p = await PairAsync(); GitRevisionChanges changes = Changes(p); GitRevisionFile file = changes.Files.Single(); Reset();
        var (a,b) = await Hit("diff", () => p.Repository.GetRevisionFileDiffAsync(changes,file));
        Check(a == b && a.Contains("-old  value λ",StringComparison.Ordinal) && a.Contains("+new value 終",StringComparison.Ordinal), "Canonical patch changed.");
        int calls = Calls("diff"); string zero = await p.Repository.GetRevisionFileDiffAsync(changes,file,options:new(ContextLines:0));
        Check(zero != a && Calls("diff") > calls, "Context option was absent from identity.");
        calls = Calls("diff"); string whitespace = await p.Repository.GetRevisionFileDiffAsync(changes,file,options:new(IgnoreWhitespaceKind.AllSpace));
        Check(whitespace.Contains("new value 終",StringComparison.Ordinal) && Calls("diff") > calls, "Whitespace arguments collided even when this semantic change still differs.");
        var repeated = await p.Repository.GetRevisionFileDiffAsync(changes,file,options:new(ContextLines:0)); Check(repeated == zero, "Context variant did not retain its own bytes.");
    }
    private static async Task DiffDisplaysAsync()
    {
        using Pair p = await PairAsync(); GitRevisionChanges changes = Changes(p); GitRevisionFile file = changes.Files.Single(); Reset();
        var (plain,plainHit) = await Hit("diff", () => p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:new(UseGitColoring:false)));
        Check(plain.PatchBytes.Span.SequenceEqual(plainHit.PatchBytes.Span) && !plain.Display.Contains('\u001b'), "Plain canonical bytes changed.");
        int calls = Calls("diff"); GitDiffDocument color = await p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:new(ReverseGitColoring:false));
        Check(Calls("diff") > calls && color.Display.Contains('\u001b') && color.Patch == plain.Patch, "Color variant reused plain display or corrupted canonical patch.");
        calls = Calls("diff"); GitDiffDocument colorHit = await p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:new(ReverseGitColoring:false));
        Check(Calls("diff") == calls && colorHit.Display == color.Display, "Colored output did not hit.");
        calls = Calls("diff"); GitDiffDocument word = await p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:new(Appearance:GitDiffAppearance.GitWordDiff));
        Check(Calls("diff") > calls && word.EffectiveAppearance == GitDiffAppearance.GitWordDiff && word.Display != color.Display && word.Patch == plain.Patch, "Word presentation collided with patch/color.");
        calls = Calls("diff"); GitDiffDocument wordHit = await p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:new(Appearance:GitDiffAppearance.GitWordDiff));
        Check(Calls("diff") == calls && wordHit.Display == word.Display, "Word output did not hit.");
        GitDiffDocument codec = await p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:new(UseGitColoring:false) { FileEncodingName="iso-8859-1" });
        Check(codec.PatchBytes.Span.SequenceEqual(plain.PatchBytes.Span) && codec.FileEncodingName == "iso-8859-1" && codec.Patch != plain.Patch, "A cached byte result accidentally retained an earlier text codec projection.");
    }
    private sealed record Series(RepositoryFixture Seed,string Base,string Left,string Right) : IDisposable
    { internal GitRepository Repository => Seed.Repository; public void Dispose() => Seed.Dispose(); }
    private static async Task<Series> SeriesAsync(bool matchingPatches = false)
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync();
        try
        {
            string before = matchingPatches ? string.Concat(Enumerable.Range(2, 19).Select(i => "line " + i + "\n")) : "line two\nline three\n";
            string after = matchingPatches ? string.Concat(Enumerable.Range(2, 19).Select(i => "shared change " + i + "\n")) : before;
            f.Write("series.txt","base\n" + before); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("base"); string basis=await f.Repository.ResolveRevisionAsync("HEAD");
            f.Write("series.txt","left\n" + after); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("left"); string left=await f.Repository.ResolveRevisionAsync("HEAD");
            Check((await f.Repository.RunAsync(["switch","--detach",basis])).Success,"Seed detach failed.");
            f.Write("series.txt","right\n" + after); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("right"); string right=await f.Repository.ResolveRevisionAsync("HEAD");
            return new(f,basis,left,right);
        }
        catch { f.Dispose(); throw; }
    }
    private static async Task CombinedAsync()
    {
        using Series s = await SeriesAsync(); s.Seed.Write("series.txt","resolved merge\nline two\nline three\n"); await s.Repository.StageAllAsync();
        string tree=(await s.Repository.RunAsync(["write-tree"])).StandardOutput.Trim();
        string merge=(await s.Repository.RunAsync(["commit-tree",tree,"-p",s.Left,"-p",s.Right,"-m","merge"])).StandardOutput.Trim();
        GitCombinedDiff combined=await s.Repository.GetCombinedDiffAsync(merge); GitRevisionFile file=combined.Files.Single(); Reset();
        var (a,b)=await Hit("diff-tree",()=>s.Repository.GetCombinedFileDiffAsync(combined,file));
        Check(a==b && a.Contains("@@@",StringComparison.Ordinal) && a.Contains("resolved merge",StringComparison.Ordinal),"Combined parent columns or content changed.");
        int calls=Calls("diff-tree"); string variant=await s.Repository.GetCombinedFileDiffAsync(combined,file,new(OmitUninterestingDiff:true,DiffOptions:new(ContextLines:0)));
        Check(Calls("diff-tree")>calls && variant.Contains("@@@",StringComparison.Ordinal),"Combined options collided.");
        calls=Calls("diff-tree"); await s.Repository.GetCombinedDiffAsync(merge); await s.Repository.GetCombinedDiffAsync(merge);
        Check(Calls("diff-tree")==calls+2,"The Windows-uncached combined list was activated as an invented cached consumer.");
        calls=Calls("diff-tree"); GitDiffDocument display=await s.Repository.GetCombinedFileDiffDocumentAsync(combined,file,displayOptions:new(UseGitColoring:true));
        Check(Calls("diff-tree")>calls && display.Patch==a && display.Display.Contains('\u001b'),"Combined display did not retain distinct colored Git output.");
        calls=Calls("diff-tree"); Check((await s.Repository.GetCombinedFileDiffDocumentAsync(combined,file,displayOptions:new(UseGitColoring:true))).Display==display.Display && Calls("diff-tree")==calls,"Combined display did not hit.");
    }
    private static async Task RangeAsync()
    {
        using Series s=await SeriesAsync(matchingPatches: true); GitRangeDiffComparison range=await s.Repository.CreateRangeDiffComparisonAsync(s.Left,s.Right,s.Base,s.Base); Reset();
        var (a,b)=await Hit("range-diff",()=>s.Repository.GetRangeDiffAsync(range));
        Check(a==b && a.Contains("left",StringComparison.Ordinal) && a.Contains("right",StringComparison.Ordinal),"Range contents changed.");
        int calls=Calls("range-diff"); string summary=await s.Repository.GetRangeDiffAsync(range,new(ContextLines:0));
        Check(Calls("range-diff")>calls && summary!=a && !summary.Contains("@@",StringComparison.Ordinal),"Range summary options collided.");
        string oldest=(await s.Repository.GetRevisionChangesAsync(s.Base)).BaseRevision!;
        GitRangeDiffComparison alternate=await s.Repository.CreateRangeDiffComparisonAsync(s.Left,s.Right,oldest,s.Base);calls=Calls("range-diff");
        string bounded=await s.Repository.GetRangeDiffAsync(alternate);
        Check(Calls("range-diff")>calls && alternate.First.CommitCount==2 && bounded!=a,"Four explicit range bounds collided with the original one-commit series.");
        calls=Calls("range-diff"); GitDiffDocument colored=await s.Repository.GetRangeDiffDocumentAsync(range,new(ContextLines:2,UseGitColoring:true));
        Check(Calls("range-diff")>calls && colored.Display.Contains('\u001b') && colored.PatchBytes.Length>0,"Range document omitted real colored output.");
        calls=Calls("range-diff"); Check((await s.Repository.GetRangeDiffDocumentAsync(range,new(ContextLines:2,UseGitColoring:true))).Display==colored.Display && Calls("range-diff")==calls,"Range document did not hit.");
    }
    private static async Task ComparisonTreeAsync()
    {
        using Pair p=await PairAsync(); Reset();
        var (a,b)=await Hit("ls-tree",()=>p.Repository.GetComparisonTreeAsync(new(p.First)));
        Check(SameTree(a,b) && a.Any(entry=>entry.Path==Literal),"Comparison tree metadata changed.");
        int calls=Calls("ls-tree"); IReadOnlyList<GitComparisonTreeEntry> variant=await p.Repository.GetComparisonTreeAsync(new(p.Second));
        Check(Calls("ls-tree")>calls && variant.Single(entry=>entry.Path==Literal).Hash!=a.Single(entry=>entry.Path==Literal).Hash,"Comparison tree OIDs collided.");
        await p.Repository.GetComparisonTreeAsync(new(Kind:GitCompareRevisionKind.Index));
        calls=Calls("ls-files"); p.Seed.Write("index only.txt","new\n"); await p.Repository.StageAsync(["index only.txt"]);
        Check((await p.Repository.GetComparisonTreeAsync(new(Kind:GitCompareRevisionKind.Index))).Any(entry=>entry.Path=="index only.txt") && Calls("ls-files")>calls,"Index tree was cached.");
    }
    private static async Task ObjectPreviewAsync()
    {
        using Pair p=await PairAsync(); string blob=(await p.Repository.RunAsync(["rev-parse",p.First+":"+Literal])).StandardOutput.Trim(); Reset();
        var (a,b)=await Hit("cat-file",()=>p.Repository.GetObjectPreviewAsync(blob));
        Check(a.ReadError is null && b.ReadError is null && a.Object is not null && b.Object is not null && a.Object.Bytes.SequenceEqual(b.Object.Bytes),"Object preview did not preserve real object bytes.");
        int calls=Calls("cat-file"); GitObjectData recovery1=await p.Repository.ReadObjectAsync(blob), recovery2=await p.Repository.ReadObjectAsync(blob);
        Check(recovery1.Bytes.SequenceEqual(recovery2.Bytes) && Calls("cat-file")==calls+2,"Recovery object integrity validation reused preview cache.");
        string other=(await p.Repository.RunAsync(["rev-parse",p.Second+":"+Literal])).StandardOutput.Trim(); calls=Calls("cat-file");
        GitObjectPreview changed=await p.Repository.GetObjectPreviewAsync(other); Check(changed.Text!=a.Text && Calls("cat-file")>calls,"Preview object IDs collided.");
    }
    private static async Task MissingBatchAsync()
    {
        using Pair p=await PairAsync();GitObjectData original=await p.Repository.ReadObjectAsync(p.Second);
        string text=Encoding.UTF8.GetString(original.Bytes);int body=text.IndexOf("\n\n",StringComparison.Ordinal);
        Check(body>=0,"Real commit fixture omitted its header separator.");
        text=text[..(body+2)]+"missing cache object "+Guid.NewGuid().ToString("N")+"\n";
        GitRunner writer=new("/usr/bin/git");GitResult name=await writer.RunAsync(p.Repository.Path,["hash-object","-t","commit","--stdin"],standardInput:text);
        Check(name.Success,"Hash-only repair fixture failed.");string missing=name.StandardOutput.Trim();
        GitBinaryResult witness=await writer.RunBinaryAsync(p.Repository.Path,["cat-file","--batch"],standardInput:Encoding.ASCII.GetBytes(missing+"\n"));
        Check(witness.ExitCode==0 && Encoding.ASCII.GetString(witness.StandardOutput)==missing+" missing\n","Actual native exit0/missing witness absent.");Reset();
        await Expect<FileNotFoundException>(()=>p.Repository.GetCommitDetailsAsync(missing,includeNotes:false));
        GitObjectPreview absent=await p.Repository.GetObjectPreviewAsync(missing);
        Check(absent.ReadError is not null && absent.Object is null && Calls("cat-file")==2,"Missing object readers did not exercise both native batch admissions.");
        Check(!GitCommandCache.Shared.Snapshot().Any(entry=>entry.Key.Contains("cat-file",StringComparison.Ordinal)),"Successful-exit missing frame was admitted before parsing.");
        GitResult repaired=await writer.RunAsync(p.Repository.Path,["hash-object","-t","commit","-w","--stdin"],standardInput:text);
        Check(repaired.Success && repaired.StandardOutput.Trim()==missing,"Real object repair wrote the wrong object identity.");
        // No cache Clear here: a retained missing-frame would mask the repaired object.
        var(a,b)=await Hit("cat-file",()=>p.Repository.GetCommitDetailsAsync(missing,includeNotes:false));
        Check(a.Hash==missing && a.Message==text[(body+2)..] && b.Message==a.Message,"Missing frame poisoned the later real repaired commit.");
    }
    private static async Task GrepAsync()
    {
        using RepositoryFixture f=await RepositoryFixture.CreateAsync(); Encoding cp=GitFileEncodingCodec.GetEncoding("windows-1252");
        File.WriteAllBytes(f.File("legacy λ.txt"),cp.GetBytes("café tea €\r\ncafé only\r\nTEA\n"));
        File.WriteAllBytes(f.File("binary.bin"),[0,..cp.GetBytes("café tea")]);
        await f.Repository.StageAllAsync();await f.Repository.CommitAsync("encoded grep");string sha=await f.Repository.ResolveRevisionAsync("HEAD");Reset();
        GitGrepOptions options=new("café",Revision:sha,EncodingName:"windows-1252");
        var (a,b)=await Hit("grep",()=>f.Repository.GrepAsync(options));
        Check(a.Matches.Count==2 && b.Matches.Select(m=>m.Text).SequenceEqual(a.Matches.Select(m=>m.Text)) && a.BinaryMatches.Single().Path=="binary.bin",
            "Encoded grep files/lines or binary membership changed.");
        Check(a.Matches[0].LineBytes.Span.SequenceEqual(cp.GetBytes("café tea €\r")),"Real CP1252 source bytes were decoded/re-encoded incorrectly.");
        int calls=Calls("grep");GitGrepResult different=await f.Repository.GrepAsync(options with{Expression="only"});
        Check(Calls("grep")>calls && different.Matches.Single().LineNumber==2,"Different encoded patterns collided.");
        calls=Calls("grep");GitGrepResult caseVariant=await f.Repository.GrepAsync(options with{Expression="tea",IgnoreCase=true});
        Check(Calls("grep")>calls && caseVariant.Matches.Count==2,"Grep matching options collided.");
        string pattern=Path.Combine(f.Root,"external.pattern");File.WriteAllText(pattern,"tea\n");
        GitGrepOptions external=options with{Paths=["legacy λ.txt"],UserArguments="-f "+Quote(pattern)+" --and"};
        var (old,hit)=await Hit("grep",()=>f.Repository.GrepAsync(external));
        Check(old.Matches.Single().LineNumber==1 && hit.Matches.Single().LineNumber==1,"External pattern positive witness absent.");
        DateTime time=File.GetLastWriteTimeUtc(pattern);File.WriteAllText(pattern,"only");File.SetLastWriteTimeUtc(pattern,time);
        calls=Calls("grep");GitGrepResult changed=await f.Repository.GrepAsync(external);
        Check(changed.Matches.Single().LineNumber==2 && Calls("grep")>calls,"External -f bytes changed at identical length/mtime but a stale hit survived.");
    }
    private static async Task GrepMutableAsync()
    {
        using RepositoryFixture f=await RepositoryFixture.CreateAsync();f.Write("search.txt","historical match\n");await f.Repository.StageAllAsync();await f.Repository.CommitAsync("grep source");
        string sha=await f.Repository.ResolveRevisionAsync("HEAD");Reset();
        Check(!(await f.Repository.GrepAsync(new("missing-pattern",Revision:sha))).HasMatches,"No-match positive witness failed.");int calls=Calls("grep");
        Check(!(await f.Repository.GrepAsync(new("missing-pattern",Revision:sha))).HasMatches && Calls("grep")>calls,"Exit1 no-match became a success0 cache entry.");
        f.Write("search.txt","index match\n");await f.Repository.StageAsync(["search.txt"]);f.Write("search.txt","worktree match\n");
        GitGrepResult index=await f.Repository.GrepAsync(new("match",Cached:true));calls=Calls("grep");
        Check(index.Matches.Single().Text=="index match","Index source positive witness failed.");
        await f.Repository.GrepAsync(new("match",Cached:true));Check(Calls("grep")>calls,"Index grep entered immutable cache.");
        GitGrepResult worktree=await f.Repository.GrepAsync(new("match"));calls=Calls("grep");
        Check(worktree.Matches.Single().Text=="worktree match","Worktree source positive witness failed.");
        f.Write("search.txt","live changed match\n");Check((await f.Repository.GrepAsync(new("match"))).Matches.Single().Text=="live changed match" && Calls("grep")>calls,"Worktree grep became stale.");
    }
    private static async Task BlameAsync()
    {
        using Pair p=await PairAsync();Reset();var(a,b)=await Hit("blame",()=>p.Repository.GetFileBlameAsync(Literal,p.Second));
        Check(a.Bytes.SequenceEqual(b.Bytes) && a.Lines.Select(line=>line.Line).SequenceEqual(b.Lines.Select(line=>line.Line)),"Real blame attribution/bytes changed.");
        int calls=Calls("blame");GitFileBlame whitespace=await p.Repository.GetFileBlameAsync(Literal,p.Second,new(IgnoreWhitespace:true));
        Check(Calls("blame")>calls && whitespace.Lines.Count==a.Lines.Count,"Blame arguments collided.");
        calls=Calls("blame");await p.Repository.GetFileBlameAsync(Literal,p.Second,new(IgnoreWhitespace:true));Check(Calls("blame")==calls,"Blame option variant did not retain its own result.");
        GitFileBlame codec=await p.Repository.GetFileBlameAsync(Literal,p.Second,fileEncodingName:"iso-8859-1");
        Check(codec.Bytes.SequenceEqual(a.Bytes) && codec.FileEncodingName=="iso-8859-1" && codec.Lines.Any(line=>line.Line.Text.Contains("ç",StringComparison.Ordinal)),"A cache hit froze its previous source-text codec instead of preserving bytes.");
        calls=Calls("blame");await p.Repository.GetFileBlameAsync(Literal,revision:null);await p.Repository.GetFileBlameAsync(Literal,revision:null);
        Check(Calls("blame")>=calls+2,"Live worktree blame was admitted.");
    }
    private static async Task PreviousBlameAsync()
    {
        using Pair p=await PairAsync();GitFileBlame blame=await p.Repository.GetFileBlameAsync(Literal,p.Second);
        GitFileBlameLine selected=blame.Lines.Single(line=>line.Line.Hash==p.Second);Reset();
        var(a,b)=await Hit("diff",()=>p.Repository.GetBlamePreviousRevisionAsync(selected));
        Check(a is not null && b==a && a.Revision==p.First && a.Path==Literal && a.Line>0,"Actual preceding blame target changed.");
        int calls=Calls("diff");GitBlamePreviousRevision? histogram=await p.Repository.GetBlamePreviousRevisionAsync(selected,useHistogramDiffAlgorithm:true);
        Check(histogram is not null && Calls("diff")>calls,"Previous-line histogram option collided.");
    }
    private static async Task DifftasticAsync()
    {
        using Pair p=await PairAsync();string counter=Path.Combine(p.Seed.Root,"tool-calls"),tool=Path.Combine(p.Seed.Root,"difftastic fixture");
        string Script(string marker)=>"#!/bin/sh\nprintf x >> "+Quote(counter)+"\nprintf '"+marker+" %s|%s|%s|%s|%s|%s\\n' \"$DFT_COLOR\" \"$DFT_BACKGROUND\" \"$DFT_SYNTAX_HIGHLIGHT\" \"$DFT_CONTEXT\" \"$DFT_STRIP_CR\" \"$DFT_WIDTH\"\ncat < \"$1\"\ncat < \"$2\"\n";
        File.WriteAllText(counter, ""); WriteExecutable(tool,Script("DFT-A"));await p.Repository.SetConfigAsync("difftool.difftastic.cmd",Quote(tool)+" \"$LOCAL\" \"$REMOTE\"");
        GitRevisionChanges changes=Changes(p);GitRevisionFile file=changes.Files.Single();GitDiffDisplayOptions display=new(Appearance:GitDiffAppearance.Difftastic,ViewerWidth:800);Reset();
        Task<GitDiffDocument> Read()=>p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:display);
        var(a,b)=await Hit("difftool",Read);
        Check(a.EffectiveAppearance==GitDiffAppearance.Difftastic && a.Display==b.Display && a.Display.Contains("DFT-A always|light|on|3|off|",StringComparison.Ordinal)
            && File.ReadAllText(counter)=="x","Real configured tool/default DFT output was absent or repeated.");
        int calls=Calls("difftool");GitDiffDocument width=await p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:display with{ViewerWidth=1});
        Check(Calls("difftool")>calls && width.DifftasticWidth==88 && width.Display.Contains("|88",StringComparison.Ordinal),"Per-call effective width collided despite unchanged ambient environment.");
        foreach((string name,string value) in new[]{("DFT_COLOR","never"),("DFT_BACKGROUND","dark"),("DFT_SYNTAX_HIGHLIGHT","off"),("DFT_CONTEXT","7"),("DFT_STRIP_CR","on"),("DFT_WIDTH","177")})
        {
            Environment.SetEnvironmentVariable(name,value);calls=Calls("difftool");GitDiffDocument variant=await Read();
            Check(Calls("difftool")>calls && variant.Display.Contains(value,StringComparison.Ordinal),"Effective "+name+" did not partition actual tool output.");
            calls=Calls("difftool");Check((await Read()).Display==variant.Display && Calls("difftool")==calls,"Effective "+name+" did not hit after admission.");
        }
        string before=File.ReadAllText(tool);DateTime stamp=File.GetLastWriteTimeUtc(tool);WriteExecutable(tool,before.Replace("DFT-A","DFT-B",StringComparison.Ordinal));File.SetLastWriteTimeUtc(tool,stamp);
        calls=Calls("difftool");GitDiffDocument changed=await Read();
        Check(Calls("difftool")>calls && changed.Display.Contains("DFT-B",StringComparison.Ordinal),"Executable bytes changed at same path/size/mtime but stale tool display survived.");
    }
    private static async Task AttributesAsync()
    {
        using Pair p=await PairAsync();string attributes=p.Seed.File(".gitattributes");byte[] original=Encoding.ASCII.GetBytes("*.txt diff \n"),changed=Encoding.ASCII.GetBytes("*.txt -diff\n");
        Check(original.Length==changed.Length,"Attribute fixture must have equal lengths.");File.WriteAllBytes(attributes,original);
        DateTime time=File.GetLastWriteTimeUtc(attributes);byte[] index=File.ReadAllBytes(p.Seed.File(".git/index")),config=File.ReadAllBytes(p.Seed.File(".git/config"));
        GitRevisionChanges revisions=Changes(p);GitRevisionFile file=revisions.Files.Single();Reset();
        var(a,b)=await Hit("diff",()=>p.Repository.GetRevisionFileDiffAsync(revisions,file));
        Check(a==b && a.Contains("@@",StringComparison.Ordinal),"Initial attribute-sensitive patch did not really hit.");
        using FileStream witness=new(attributes,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1);
        File.WriteAllBytes(attributes,changed);File.SetLastWriteTimeUtc(attributes,time);
        using MemoryStream read=new();await witness.CopyToAsync(read);
        Check(read.ToArray().SequenceEqual(changed) && File.GetLastWriteTimeUtc(attributes)==time,"Attribute fixture failed its original-handle/same-length/mtime witness.");
        int calls=Calls("diff");string binary=await p.Repository.GetRevisionFileDiffAsync(revisions,file);
        Check(Calls("diff")>calls && binary.Contains("Binary files",StringComparison.Ordinal),"Same-inode/mtime changed attributes returned a stale textual patch.");
        Check(File.ReadAllBytes(p.Seed.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(p.Seed.File(".git/config")).SequenceEqual(config),"Read invalidation mutated repository state.");
    }
    private static async Task IncludesAsync()
    {
        using Pair p=await PairAsync();string include=Path.Combine(p.Seed.Root,"missing include.config");
        await p.Repository.SetConfigAsync("include.path",include);GitRevisionChanges revisions=Changes(p);GitRevisionFile file=revisions.Files.Single();Reset();
        Task<GitDiffDocument> Read()=>p.Repository.GetRevisionFileDiffDocumentAsync(revisions,file,displayOptions:new(ReverseGitColoring:false));
        var(a,b)=await Hit("diff",Read);Check(a.Display==b.Display && a.Display.Contains('\u001b'),"Missing-include positive colored witness absent.");
        File.WriteAllText(include,"[color \"diff\"]\n new = blue\n");int calls=Calls("diff");GitDiffDocument blue=await Read();
        Check(Calls("diff")>calls && blue.Display!=a.Display && blue.Display.Contains("\u001b[34m",StringComparison.Ordinal),"Previously absent include was omitted from the authority closure.");
        DateTime time=File.GetLastWriteTimeUtc(include);File.WriteAllText(include,"[color \"diff\"]\n new = cyan\n");File.SetLastWriteTimeUtc(include,time);calls=Calls("diff");
        GitDiffDocument cyan=await Read();Check(Calls("diff")>calls && cyan.Display!=blue.Display && cyan.Display.Contains("\u001b[36m",StringComparison.Ordinal),"Same-size/mtime included config bytes were ignored.");
        // An inactive condition's target must also be tracked, even when it does not alter today's result.
        string inactive=Path.Combine(p.Seed.Root,"inactive.config");await p.Repository.SetConfigAsync("includeIf.onbranch:never-current.path",inactive);Reset();
        await Hit("diff",Read);File.WriteAllText(inactive,"[color \"diff\"]\n old = magenta\n");calls=Calls("diff");
        GitDiffDocument same=await Read();Check(Calls("diff")>calls && same.Display==cyan.Display,"An inactive include target appearing did not revalidate the closure, or incorrectly became active.");
    }
    private static async Task GlobalAttributesAsync()
    {
        using Pair p=await PairAsync();string link=Path.Combine(p.Seed.Root,"global-attributes"),text=link+".text",binary=link+".binary";
        File.WriteAllText(text,"*.txt diff\n");File.WriteAllText(binary,"*.txt -diff\n");await p.Repository.SetConfigAsync("core.attributesFile",link);
        GitRevisionChanges changes=Changes(p);GitRevisionFile file=changes.Files.Single();Reset();
        var(a,b)=await Hit("diff",()=>p.Repository.GetRevisionFileDiffAsync(changes,file));Check(a==b && a.Contains("@@",StringComparison.Ordinal),"Absent global attribute positive witness failed.");
        File.CreateSymbolicLink(link,binary);int calls=Calls("diff");string altered=await p.Repository.GetRevisionFileDiffAsync(changes,file);
        Check(Calls("diff")>calls && altered.Contains("Binary files",StringComparison.Ordinal),"Previously absent global attributes were ignored.");
        File.Delete(link);File.CreateSymbolicLink(link,text);calls=Calls("diff");string restored=await p.Repository.GetRevisionFileDiffAsync(changes,file);
        Check(Calls("diff")>calls && restored==a,"Changing the symbolic link destination retained old binary attributes.");
    }
    private static async Task GraphAuthorityAsync()
    {
        using Pair p=await PairAsync();Reset();var(original,hit)=await Hit("ls-tree",()=>p.Repository.GetTreeAsync(p.First));
        Check(original.SequenceEqual(hit),"Initial graph-sensitive tree did not hit.");
        string replacement=p.Seed.File(".git/refs/replace/"+p.First);Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);File.WriteAllText(replacement,p.Second+"\n");
        try
        {
            int calls=Calls("ls-tree");IReadOnlyList<GitTreeEntry> altered=await p.Repository.GetTreeAsync(p.First);
            Check(Calls("ls-tree")>calls && altered.Single(entry=>entry.Path==Literal).Hash!=original.Single(entry=>entry.Path==Literal).Hash,"External replace ref left a stale immutable tree.");
        }
        finally{File.Delete(replacement);}
        Check((await p.Repository.GetTreeAsync(p.First)).SequenceEqual(original),"Removing external replacement did not restore real object semantics.");
        Reset();var(before,again)=await Hit("rev-list",()=>p.Repository.GetRevisionChangesAsync(p.Second));Check(before.BaseRevision==p.First && again.BaseRevision==p.First,"Actual parents positive witness absent.");
        string shallow=p.Seed.File(".git/shallow");File.WriteAllText(shallow,p.Second+"\n");
        try
        {int calls=Calls("rev-list");GitRevisionChanges boundary=await p.Repository.GetRevisionChangesAsync(p.Second);Check(Calls("rev-list")>calls && boundary.BaseRevision is null,"New shallow boundary retained stale parent ancestry.");}
        finally{File.Delete(shallow);}
        Check((await p.Repository.GetRevisionChangesAsync(p.Second)).BaseRevision==p.First,"Removing shallow boundary did not restore parents.");
    }
    private static async Task LinkedWorktreesAsync()
    {
        using Pair p=await PairAsync();string path=Path.Combine(p.Seed.Root,"linked worktree");Check((await p.Repository.RunAsync(["worktree","add","--detach",path,p.Second])).Success,"Seed linked worktree failed.");
        GitRepository linked=new(path);Reset();
        var(a,b)=await Hit("cat-file",()=>p.Repository.GetFileBytesAsync(Literal,p.Second));Check(a.SequenceEqual(b),"Main-worktree blob did not hit.");
        int calls=Calls("cat-file");byte[] other=await linked.GetFileBytesAsync(Literal,p.Second);
        Check(Calls("cat-file")>calls && other.SequenceEqual(a),"Linked worktree shared a namespace only by common object store.");
        calls=Calls("cat-file");Check((await linked.GetFileBytesAsync(Literal,p.Second)).SequenceEqual(other) && Calls("cat-file")==calls,"Linked worktree lacked its own true hit.");
    }
    private static async Task InFlightAuthorityAsync(bool attributes)
    {
        using Pair p=await PairAsync();GitRevisionChanges changes=Changes(p);GitRevisionFile file=changes.Files.Single();
        string dependency=attributes?p.Seed.File(".gitattributes"):p.Seed.File(".git/config");
        if(attributes){File.WriteAllText(dependency,"*.txt diff \n");}
        Reset();await Hit("diff",()=>p.Repository.GetRevisionFileDiffAsync(changes,file));
        // Force a real miss only after native Git has proven a positive admission/hit.
        Reset();int mutations=0;long launchId=0;Exception? observerError=null;Task<string>? operation=null;string actual="";
        using CancellationTokenSource cancel=new(TimeSpan.FromSeconds(20));
        void Observe()
        {
            GitCommandLogEntry? entry=GitCommandLog.Snapshot().LastOrDefault(item=>item.Command=="git diff" && item.Directory==p.Repository.Path && item.IsRunning && item.ProcessId is null);
            if(entry is null || Interlocked.CompareExchange(ref mutations,1,0)!=0){return;}
            launchId=entry.Id;
            try
            {
                // Begin publishes synchronously after authority capture, before Configure/ProcessStart.
                if(attributes){File.WriteAllText(dependency,"*.txt -diff\n");}else{File.AppendAllText(dependency,"\n[cache-test]\n during = changed\n");}
            }
            catch(Exception error){observerError=error;} // Diagnostic observers isolate throws; assert outside the callback.
        }
        GitCommandLog.Changed+=Observe;
        try
        {
            operation=p.Repository.GetRevisionFileDiffAsync(changes,file,cancellationToken:cancel.Token);
            actual=await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Check(mutations==1 && launchId>0 && observerError is null && Calls("diff")==1,"Real native factory did not observe exactly one successful pre-launch dependency mutation.");
            Check(attributes?actual.Contains("Binary files",StringComparison.Ordinal):actual.Contains("new value 終",StringComparison.Ordinal),"Caller did not receive the native successful result after the authority changed.");
            Check(!GitCommandCache.Shared.Snapshot().Any(entry=>entry.Key.StartsWith("diff ",StringComparison.Ordinal)||entry.Key.Contains(" diff ",StringComparison.Ordinal)),"Changed authority admitted its in-flight result.");
            GitCommandLogEntry completed=GitCommandLog.Snapshot().Single(entry=>entry.Id==launchId);
            Check(completed is { IsRunning:false, ExitCode:0, Cancelled:false, ProcessId:>0 } && !LinuxProcessState.IsRunning(completed.ProcessId!.Value),"Awaited native Git did not publish a real PID, successful completion and retirement.");
        }
        finally
        {
            GitCommandLog.Changed-=Observe;cancel.Cancel();
            if(operation is not null){try{await operation.WaitAsync(TimeSpan.FromSeconds(15));}catch(OperationCanceledException) when(cancel.IsCancellationRequested){}}
        }
        var(a,b)=await Hit("diff",()=>p.Repository.GetRevisionFileDiffAsync(changes,file));
        Check(a==actual && b==actual,"New stable authority did not admit a true hit after the discarded result.");
    }
    private static async Task DisplayKeyAsync()
    {
        using RepositoryFixture f=await RepositoryFixture.CreateAsync();f.Write("-c","literal option path\n");await f.Repository.StageAllAsync();await f.Repository.CommitAsync("literal option");string sha=await f.Repository.ResolveRevisionAsync("HEAD");Reset();
        Type family=typeof(GitRepository).Assembly.GetType("GitExtensions.Linux.GitCachedReadKind",throwOnError:true)!;
        System.Reflection.MethodInfo helper=typeof(GitRepository).GetMethod("RunCachedBinaryAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        string[] arguments=["--no-optional-locks","-c","cache.display=before","ls-tree","-r","-z",sha,"--","-c"];
        Task<GitBinaryResult> Read()=>(Task<GitBinaryResult>)helper.Invoke(f.Repository,[Enum.Parse(family,"Tree"),arguments,CancellationToken.None,null,null,null])!;
        var(a,b)=await Hit("ls-tree",Read);Check(a.Success && a.StandardOutput.SequenceEqual(b.StandardOutput) && Encoding.UTF8.GetString(a.StandardOutput).Contains("\t-c\0",StringComparison.Ordinal),"Literal post-verb -c path was not actually read.");
        GitCommandCacheEntry entry=GitCommandCache.Shared.Snapshot().Single(item=>item.Key.StartsWith("--no-optional-locks -c cache.display=before ls-tree",StringComparison.Ordinal));
        Check(entry.Key.Contains("cache.display=before",StringComparison.Ordinal) && entry.DisplayString.StartsWith("ls-tree ",StringComparison.Ordinal)
            && entry.DisplayString.EndsWith(" -- -c",StringComparison.Ordinal) && !entry.DisplayString.Contains("cache.display=before",StringComparison.Ordinal),"Display filtering corrupted lookup key or post-verb literal argument.");
    }
    private static async Task NullRevisionAsync()
    {
        using RepositoryFixture f=await RepositoryFixture.CreateAsync();Reset();
        await Expect<ArgumentNullException>(()=>f.Repository.GetTreeAsync(null!));
        await Expect<ArgumentNullException>(()=>f.Repository.GetFileBytesAsync("seed.txt",null!));
        await Expect<ArgumentNullException>(()=>f.Repository.GetRevisionChangesAsync(null!));
        await Expect<ArgumentNullException>(()=>f.Repository.GetCombinedDiffAsync(null!));
        Check(GitCommandLog.Snapshot().Count==0 && GitCommandCache.Shared.Snapshot().Count==0,"Null validation launched a command or retained content.");
    }
    private static async Task BootstrapWorkerAsync()
    {
        using RepositoryFixture f=await RepositoryFixture.CreateAsync();string sha=await f.Repository.ResolveRevisionAsync("HEAD");
        GitRepository fresh=new(f.Repository.Path);Reset();int caller=0,firstNativeThread=0;
        void Observe(){if(GitCommandLog.Snapshot().Count>0){Interlocked.CompareExchange(ref firstNativeThread,Environment.CurrentManagedThreadId,0);}}
        TaskCompletionSource<Task<IReadOnlyList<GitTreeEntry>>> returned=new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancel=new(TimeSpan.FromSeconds(20));Task<IReadOnlyList<GitTreeEntry>>? read=null;
        Thread thread=new(()=>
        {
            caller=Environment.CurrentManagedThreadId;
            try{read=fresh.GetTreeAsync(sha,cancellationToken:cancel.Token);returned.TrySetResult(read);}
            catch(Exception error){returned.TrySetException(error);}
        }){IsBackground=true};
        GitCommandLog.Changed+=Observe;
        try
        {
            thread.Start();Task<IReadOnlyList<GitTreeEntry>> operation=await returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check((await operation.WaitAsync(TimeSpan.FromSeconds(20))).Any(entry=>entry.Path=="seed.txt"),"Worker bootstrap did not perform a real positive read.");
            Check(firstNativeThread>0 && firstNativeThread!=caller,"First native bootstrap Begin ran synchronously on the dedicated calling thread.");
            int targets=Calls("ls-tree");await fresh.GetTreeAsync(sha);Check(Calls("ls-tree")==targets,"Worker offload was substituted with always-decline admission.");
        }
        finally
        {
            cancel.Cancel();bool retired=thread.Join(TimeSpan.FromSeconds(5));
            try{if(read is not null){await read.WaitAsync(TimeSpan.FromSeconds(15));}}catch(OperationCanceledException){}
            finally{GitCommandLog.Changed-=Observe;}
            Check(retired,"Dedicated caller did not retire.");
        }
    }

    private static async Task DifftasticUnknownExpansionAsync()
    {
        using Pair p=await PairAsync();string counter=Path.Combine(p.Seed.Root,"expansion-calls"),tool=Path.Combine(p.Seed.Root,"expansion tool"),external=Path.Combine(p.Seed.Root,"expansion-value");
        File.WriteAllText(counter,"");File.WriteAllText(external,"outside-A");
        WriteExecutable(tool,"#!/bin/sh\nprintf x >> "+Quote(counter)+"\nprintf 'EXTRA:%s\\n' \"$3\"\ncat < \"$1\"\ncat < \"$2\"\n");
        GitRevisionChanges changes=Changes(p);GitRevisionFile file=changes.Files.Single();GitDiffDisplayOptions display=new(Appearance:GitDiffAppearance.Difftastic);
        Task<GitDiffDocument> Read()=>p.Repository.GetRevisionFileDiffDocumentAsync(changes,file,displayOptions:display);
        string Simple(string extra="")=>Quote(tool)+" \"$LOCAL\" \"$REMOTE\""+extra;
        string? previousUnknown=Environment.GetEnvironmentVariable("UNTRACKED"),previousSuffix=Environment.GetEnvironmentVariable("LOCAL_SUFFIX");
        try
        {
            Environment.SetEnvironmentVariable("UNTRACKED","unknown-A");Environment.SetEnvironmentVariable("LOCAL_SUFFIX","suffix-A");
            await p.Repository.SetConfigAsync("difftool.difftastic.cmd",Simple());Reset();
            var(positive,hit)=await Hit("difftool",Read);
            Check(positive.EffectiveAppearance==GitDiffAppearance.Difftastic && positive.Display==hit.Display && positive.Display.Contains("old  value λ",StringComparison.Ordinal)
                && positive.Display.Contains("new value 終",StringComparison.Ordinal) && File.ReadAllText(counter)=="x","Simple known-operand DFT fixture never established actual admission.");
            await p.Repository.SetConfigAsync("difftool.difftastic.cmd",Quote(tool)+" \"${LOCAL}\" \"${REMOTE}\"");
            var(braced,bracedHit)=await Hit("difftool",Read);Check(braced.Display==bracedHit.Display,"Entire braced LOCAL/REMOTE operands did not remain a simple positive command.");
            foreach(string extra in new[]{" \"$UNTRACKED\""," \"${UNTRACKED}\""," \"$LOCAL_SUFFIX\""," \"${LOCAL}suffix\""," \"literal*glob\""," \"~\""," \"literal#comment\""})
            {
                await p.Repository.SetConfigAsync("difftool.difftastic.cmd",Simple(extra));int calls=Calls("difftool"),tools=File.ReadAllText(counter).Length;
                GitDiffDocument first=await Read(),again=await Read();
                Check(Calls("difftool")==calls+2 && File.ReadAllText(counter).Length==tools+2 && first.EffectiveAppearance==GitDiffAppearance.Difftastic && again.EffectiveAppearance==GitDiffAppearance.Difftastic,
                    "Unknown expansion or shell metacharacter was admitted instead of executing twice: "+extra);
            }
            await p.Repository.SetConfigAsync("difftool.difftastic.cmd",Simple(" \"$UNTRACKED\""));
            GitDiffDocument before=await Read();int native=Calls("difftool");Environment.SetEnvironmentVariable("UNTRACKED","unknown-B");GitDiffDocument after=await Read();
            Check(before.Display.Contains("EXTRA:unknown-A",StringComparison.Ordinal) && after.Display.Contains("EXTRA:unknown-B",StringComparison.Ordinal) && Calls("difftool")==native+1,"Unknown environment expansion did not expose its new native value.");
            await p.Repository.SetConfigAsync("difftool.difftastic.cmd",Simple(" \"$(cat "+Quote(external)+")\""));
            GitDiffDocument initial=await Read();native=Calls("difftool");GitDiffDocument repeated=await Read();
            Check(initial.Display.Contains("EXTRA:outside-A",StringComparison.Ordinal) && repeated.Display==initial.Display && Calls("difftool")==native+1,"Command substitution was retained while its file authority was unverified.");
            DateTime stamp=File.GetLastWriteTimeUtc(external);File.WriteAllText(external,"outside-B");File.SetLastWriteTimeUtc(external,stamp);native=Calls("difftool");
            GitDiffDocument changed=await Read();Check(changed.Display.Contains("EXTRA:outside-B",StringComparison.Ordinal) && Calls("difftool")==native+1,"Unknown substitution file changed at equal length/mtime but native output stayed stale.");
        }
        finally{Environment.SetEnvironmentVariable("UNTRACKED",previousUnknown);Environment.SetEnvironmentVariable("LOCAL_SUFFIX",previousSuffix);}
    }
    private static async Task TreePreviewAsync()
    {
        using Pair p=await PairAsync();string tree=(await p.Repository.RunAsync(["rev-parse",p.First+"^{tree}"])).StandardOutput.Trim();
        string other=(await p.Repository.RunAsync(["rev-parse",p.Second+"^{tree}"])).StandardOutput.Trim();Reset();
        var(first,again)=await Hit("cat-file",()=>p.Repository.GetObjectPreviewAsync(tree));
        Check(Calls("cat-file")==2 && first.ReadError is null && again.ReadError is null && first.Object is { Type:GitLostObjectType.Tree }
            && again.Object is not null && first.Object.Bytes.SequenceEqual(again.Object.Bytes) && first.Text is not null && first.Text==again.Text && first.Text.Contains("seed.txt",StringComparison.Ordinal),
            "Tree preview did not retain both the real batch object and native pretty listing.");
        int calls=Calls("cat-file");GitObjectPreview changed=await p.Repository.GetObjectPreviewAsync(other);
        Check(Calls("cat-file")==calls+2 && changed.Object is { Type:GitLostObjectType.Tree } && changed.Text!=first.Text,"Different tree OID reused binary or pretty output.");
        calls=Calls("cat-file");await p.Repository.GetObjectPreviewAsync(other);Check(Calls("cat-file")==calls,"Different tree preview lacked its own positive hit.");
        calls=Calls("cat-file");GitObjectData recovery1=await p.Repository.ReadObjectAsync(tree),recovery2=await p.Repository.ReadObjectAsync(tree);
        Check(Calls("cat-file")==calls+2 && recovery1.Bytes.SequenceEqual(recovery2.Bytes) && recovery1.Bytes.SequenceEqual(first.Object!.Bytes),"Ordinary Recovery integrity reads accidentally consumed the tree preview cache.");
    }
    private static async Task RawAuthorityAsync()
    {
        if(!OperatingSystem.IsLinux()){throw new PlatformNotSupportedException("Private unreadable subtree requires Linux permissions.");}
        using Pair p=await PairAsync();string directory=p.Seed.File("unrelated private subtree");Directory.CreateDirectory(directory);
        string attributes=Path.Combine(directory,".gitattributes");File.WriteAllText(attributes,"*.txt diff\n");UnixFileMode original=File.GetUnixFileMode(directory);Reset();
        var(tree,treeHit)=await Hit("ls-tree",()=>p.Repository.GetTreeAsync(p.Second));var(blob,blobHit)=await Hit("cat-file",()=>p.Repository.GetFileBytesAsync(Literal,p.Second));
        Check(tree.SequenceEqual(treeHit) && blob.SequenceEqual(blobHit),"Raw family positive witnesses absent.");
        int trees=Calls("ls-tree"),blobs=Calls("cat-file");
        try
        {
            File.WriteAllText(attributes,"*.txt -diff\n");File.SetUnixFileMode(directory,UnixFileMode.None);
            await Expect<UnauthorizedAccessException>(()=>Task.Run(()=>Directory.GetFileSystemEntries(directory)));
            IReadOnlyList<GitTreeEntry> actualTree=await p.Repository.GetTreeAsync(p.Second);byte[] actualBlob=await p.Repository.GetFileBytesAsync(Literal,p.Second);
            Check(actualTree.SequenceEqual(tree) && actualBlob.SequenceEqual(blob) && Calls("ls-tree")==trees && Calls("cat-file")==blobs,
                "Unrelated worktree traversal or attributes removed already-proven raw tree/blob hits.");
        }
        finally{File.SetUnixFileMode(directory,original);}
    }
    private static async Task BlameTextconvAsync()
    {
        using Pair p=await PairAsync();Reset();var(first,again)=await Hit("blame",()=>p.Repository.GetFileBlameAsync(Literal,p.Second));
        Check(first.Bytes.SequenceEqual(again.Bytes),"Blame positive witness absent.");
        await p.Repository.SetConfigAsync("diff.cache-unused.textconv","/bin/cat");int calls=Calls("blame");
        GitFileBlame native1=await p.Repository.GetFileBlameAsync(Literal,p.Second),native2=await p.Repository.GetFileBlameAsync(Literal,p.Second);
        Check(Calls("blame")==calls+2 && native1.Bytes.SequenceEqual(first.Bytes) && native2.Lines.Select(line=>line.Line).SequenceEqual(native1.Lines.Select(line=>line.Line)),
            "Unverified nonempty textconv definition retained blame instead of invoking the real reader each time.");
        await p.Repository.UnsetConfigAsync("diff.cache-unused.textconv");
        GitFileBlame restored=await p.Repository.GetFileBlameAsync(Literal,p.Second);calls=Calls("blame");GitFileBlame restoredHit=await p.Repository.GetFileBlameAsync(Literal,p.Second);
        Check(restored.Bytes.SequenceEqual(first.Bytes) && restoredHit.Bytes.SequenceEqual(first.Bytes) && Calls("blame")==calls,"Removing textconv did not restore eligible blame admission or reuse a still-valid prior entry.");
    }

    private static async Task GraphOverridesAsync()
    {
        using Pair p=await PairAsync();string[] names=["GIT_REPLACE_REF_BASE","GIT_SHALLOW_FILE","GIT_GRAFT_FILE"];
        Dictionary<string,string?> previous=names.ToDictionary(name=>name,Environment.GetEnvironmentVariable);
        string shallow=Path.Combine(p.Seed.Root,"cache-shallow"),grafts=Path.Combine(p.Seed.Root,"cache-grafts"),replacement=p.Seed.File(".git/refs/cache-replacements/"+p.First);
        try
        {
            foreach(string name in names){Environment.SetEnvironmentVariable(name,null);}
            Environment.SetEnvironmentVariable("GIT_SHALLOW_FILE","../cache-shallow");Reset();
            var(parents,parentsHit)=await Hit("rev-list",()=>p.Repository.GetRevisionChangesAsync(p.Second));
            Check(parents.BaseRevision==p.First && parentsHit.BaseRevision==p.First,"Custom missing shallow-file positive witness absent.");
            File.WriteAllText(shallow,p.Second+"\n");int calls=Calls("rev-list");GitRevisionChanges boundary=await p.Repository.GetRevisionChangesAsync(p.Second);
            Check(boundary.BaseRevision is null && Calls("rev-list")>calls,"Creation of the same relative GIT_SHALLOW_FILE left stale parent ancestry.");
            File.Delete(shallow);Check((await p.Repository.GetRevisionChangesAsync(p.Second)).BaseRevision==p.First,"Removing the custom shallow boundary failed to restore native ancestry.");
            Environment.SetEnvironmentVariable("GIT_SHALLOW_FILE",null);Environment.SetEnvironmentVariable("GIT_GRAFT_FILE","../cache-grafts");Reset();
            var(graftBefore,graftHit)=await Hit("rev-list",()=>p.Repository.GetRevisionChangesAsync(p.Second));
            Check(graftBefore.BaseRevision==p.First && graftHit.BaseRevision==p.First,"Custom missing graft-file positive witness absent.");
            File.WriteAllText(grafts,p.Second+"\n");calls=Calls("rev-list");GitRevisionChanges grafted=await p.Repository.GetRevisionChangesAsync(p.Second);
            Check(grafted.BaseRevision is null && Calls("rev-list")>calls,"Creation of the same relative GIT_GRAFT_FILE retained old native parents.");
            File.Delete(grafts);Check((await p.Repository.GetRevisionChangesAsync(p.Second)).BaseRevision==p.First,"Removing the custom graft did not restore native parents.");
            Environment.SetEnvironmentVariable("GIT_GRAFT_FILE",null);Environment.SetEnvironmentVariable("GIT_REPLACE_REF_BASE","refs/cache-replacements/");Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);Reset();
            var(tree,treeHit)=await Hit("ls-tree",()=>p.Repository.GetTreeAsync(p.First));Check(tree.SequenceEqual(treeHit),"Custom replacement namespace positive witness absent.");
            File.WriteAllText(replacement,p.Second+"\n");calls=Calls("ls-tree");IReadOnlyList<GitTreeEntry> replaced=await p.Repository.GetTreeAsync(p.First);
            Check(Calls("ls-tree")>calls && replaced.Single(entry=>entry.Path==Literal).Hash!=tree.Single(entry=>entry.Path==Literal).Hash,"New custom replacement namespace ref left a stale tree for the same OID.");
            File.Delete(replacement);Check((await p.Repository.GetTreeAsync(p.First)).SequenceEqual(tree),"Removing the custom replacement failed to restore the actual tree.");
        }
        finally
        {
            foreach((string name,string? value) in previous){Environment.SetEnvironmentVariable(name,value);}
            File.Delete(shallow);File.Delete(grafts);File.Delete(replacement);
        }
    }
    private static async Task ScriptGitBypassAsync()
    {
        using Pair p=await PairAsync();string script=Path.Combine(p.Seed.Root,"git"),marker=Path.Combine(p.Seed.Root,"external-marker"),actors=Path.Combine(p.Seed.Root,"wrapper-actors");
        File.WriteAllText(marker,"MARKER-A\n");File.WriteAllText(actors,"");
        WriteExecutable(script,"#!/bin/sh\nmatch=no\nfor arg in \"$@\"; do test \"$arg\" != cat-file || match=yes; done\n"
            +"if test \"$match\" != yes; then exec /usr/bin/git \"$@\"; fi\n/usr/bin/git \"$@\" &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" >> "+Quote(actors)+"\n"
            +"wait \"$child\" || exit $?\ncat < \"$GE_CACHE_WRAPPER_MARKER\"\n");
        byte[] scriptBytes=File.ReadAllBytes(script);string? previous=Environment.GetEnvironmentVariable("GE_CACHE_WRAPPER_MARKER");
        try
        {
            Environment.SetEnvironmentVariable("GE_CACHE_WRAPPER_MARKER",marker);Reset();
            var(native,nativeHit)=await Hit("cat-file",()=>p.Repository.GetFileBytesAsync(Literal,p.Second));
            Check(native.SequenceEqual(nativeHit),"The native Git positive witness never admitted its immutable blob.");
            long[] admitted=GitCommandCache.Shared.Snapshot().Select(entry=>entry.Id).ToArray();long retained=GitCommandCache.Shared.RetainedBytes;
            long lastId=GitCommandLog.Snapshot().Max(entry=>entry.Id);int calls=Calls("cat-file");GitRepository wrapped=new(p.Repository.Path,new GitRunner(script));
            byte[] first=await wrapped.GetFileBytesAsync(Literal,p.Second);
            Check(first.SequenceEqual(native.Concat(Encoding.UTF8.GetBytes("MARKER-A\n"))),"Actual script/native Git fixture did not expose its external marker.");
            DateTime stamp=File.GetLastWriteTimeUtc(marker);File.WriteAllText(marker,"MARKER-B\n");File.SetLastWriteTimeUtc(marker,stamp);
            byte[] second=await wrapped.GetFileBytesAsync(Literal,p.Second);
            Check(Calls("cat-file")==calls+2 && second.SequenceEqual(native.Concat(Encoding.UTF8.GetBytes("MARKER-B\n"))) && !second.SequenceEqual(first),"Stable script bytes and constant environment retained stale output instead of invoking both real reads.");
            Check(GitCommandCache.Shared.Snapshot().Select(entry=>entry.Id).SequenceEqual(admitted) && GitCommandCache.Shared.RetainedBytes==retained,"Unclosed Git script admitted new data or altered an independent native entry.");
            Check(File.ReadAllBytes(script).SequenceEqual(scriptBytes) && Environment.GetEnvironmentVariable("GE_CACHE_WRAPPER_MARKER")==marker,"Fixture changed executable bytes or environment identity to manufacture a miss.");
            GitCommandLogEntry[] completed=GitCommandLog.Snapshot().Where(entry=>entry.Id>lastId && entry.Command=="git cat-file").ToArray();
            Check(completed.Length==2 && completed.All(entry=>entry is { IsRunning:false, ExitCode:0, Cancelled:false, ProcessId:>0 } && !LinuxProcessState.IsRunning(entry.ProcessId!.Value)),"Actual wrapper reads did not complete and drain their real recorded processes.");
            string[] records=File.ReadAllLines(actors);Check(records.Length==2,"Script failed to expose both actual shell/native-child pairs.");
            foreach(string record in records)
            {
                string[] ids=record.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                Check(ids.Length==2 && int.TryParse(ids[0],out int shell) && int.TryParse(ids[1],out int child) && shell>0 && child>0
                    && !LinuxProcessState.IsRunning(shell) && !LinuxProcessState.IsRunning(child),"Owned wrapper or native Git child survived its awaited read.");
            }
        }
        finally{Environment.SetEnvironmentVariable("GE_CACHE_WRAPPER_MARKER",previous);}
    }
    private static string Quote(string value)=>"'"+value.Replace("'","'\"'\"'",StringComparison.Ordinal)+"'";
    private static void WriteExecutable(string path,string text)
    {
        if(!OperatingSystem.IsLinux()){throw new PlatformNotSupportedException("Native cache reader fixtures require Linux.");}
        File.WriteAllText(path,text);File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
}
