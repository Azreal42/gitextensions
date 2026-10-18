using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitExtensions.Linux;
using GitUI.AutoCompletion;

internal static class CommitCompletionScenarios
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("commit completion snapshots literal Unicode basenames and exact shared metadata without ignored paths", NamesAsync),
        ("commit completion reads both actual index and working patches for MM with Windows default captures", BothSidesAsync),
        ("commit completion keeps real renamed basenames and sends the old path only to the index projection", RenameAsync),
        ("commit completion uses five patch context lines and disables external diff textconv and color", PatchAsync),
        ("commit completion falls back to real HEAD current and old names without substituting index bytes", HeadFallbackAsync),
        ("commit completion added and unreadable files use silent working text fallback", WorkingFallbackAsync),
        ("commit completion real submodules contribute only their current basename", SubmoduleAsync),
        ("commit completion custom regex captures retain case sensitive extensions groups and Windows options", RulesAsync),
        ("commit completion exposes invalid configured rules and bounded regex extraction failures", InvalidRulesAsync),
        ("commit completion captures the Git executable environment and immutable status once", CapturedContextAsync),
        ("commit completion cancellation drains the witnessed native diff and starts no fallback query", CancellationAsync)
    ];

    private static readonly string[] Metadata = ["Co-authored-by: ", "Signed-off-by: ", "BREAKING CHANGE: ", "Reviewed-by: ", "Tested-by: "];

    private static async Task NamesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        const string path = "folder/:literal [*] Ω\\name\tline\n.cs";
        f.Write(path, "public class Unicode候補 {}\n"); f.Write("ignored.cs", "public class MustNeverAppear {}\n"); f.Write(".gitignore", "ignored.cs\n");
        GitStatus status = await f.Repository.GetStatusAsync(); List<GitChange> changes = status.Changes.ToList();
        GitCommitAutoCompleteProvider provider = new(f.Repository, changes); changes.Clear();
        IEnumerable<AutoCompleteWord> result = await provider.GetAutoCompleteWordsAsync(); HashSet<string> words = result.Select(word => word.Word).ToHashSet(StringComparer.Ordinal);
        Check(words.Contains(Path.GetFileName(path)) && words.Contains(Path.GetFileNameWithoutExtension(path)) && words.Contains("Unicode候補")
            && !words.Contains(path) && !words.Contains("ignored.cs") && !words.Contains("MustNeverAppear"),
            "Current status contributes literal POSIX basename/stem and Unicode symbols, with no directory paths or ignored files.");
        Check(Metadata.All(words.Contains) && words.Where(word => word.EndsWith(": ", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal).SetEquals(Metadata)
            && !words.Contains("Linux Test") && !words.Contains("test@example.com"), "Only the five original shared metadata prefixes, including trailing spaces, are added.");
        Check(provider.Changes.Count == status.Changes.Count && result.Count() == words.Count, "The caller list cannot change the copied status and the corpus deduplicates ordinally.");
        await Expect<NotSupportedException>(() => { ((IList<GitChange>)provider.Changes).Clear(); return Task.CompletedTask; });
        await Expect<NotSupportedException>(() => { ((IList<AutoCompleteWord>)result).Clear(); return Task.CompletedTask; });
        Check(new AutoCompleteWord("PascalCaseName").Matches("pcn") && new AutoCompleteWord("élève").Matches("ÉL"),
            "The actual linked Windows completion word supplies camel-hump and ordinal-ignore-case prefix matching.");
    }

    private static async Task BothSidesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write("Both.cs", "public class HeadSymbol {}\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Seed completion symbols");
        f.Write("Both.cs", "public class IndexOnlySymbol {}\n"); await f.Repository.StageAsync(["Both.cs"]);
        f.Write("Both.cs", "public class WorktreeOnlySymbol {}\n"); GitStatus status = await f.Repository.GetStatusAsync();
        Check(status.Changes.Single(change => change.Path == "Both.cs") is { IndexStatus: 'M', WorkTreeStatus: 'M' }, "The real status row represents MM.");
        string journal = f.Output("both.jsonl"); GitRepository wrapped = await Wrapped(f, journal);
        HashSet<string> words = await Words(new(wrapped, status.Changes));
        string[][] diffs = Arguments(journal).Where(args => args.Contains("diff")).ToArray();
        Check(words.IsSupersetOf(["HeadSymbol", "IndexOnlySymbol", "WorktreeOnlySymbol", "Both.cs", "Both"])
            && diffs.Length == 2 && diffs.Count(args => args.Contains("--cached")) == 1,
            "The shared default C# captures see both native index and working-tree patches rather than choosing one side.");
    }

    private static async Task RenameAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); const string old = "before/Old Ω.cs", current = "after/New \\ Ω.cs";
        string stable = string.Join('\n', Enumerable.Range(0, 50).Select(i => "// stable " + i)) + "\n";
        f.Write(old, stable + "public class OldRenameSymbol {}\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Before rename");
        Directory.CreateDirectory(Path.GetDirectoryName(f.File(current))!); await f.GitAsync("mv", "--", old, current);
        f.Write(current, stable + "public class IndexRenameSymbol {}\n"); await f.Repository.StageAsync([current]);
        f.Write(current, stable + "public class WorkingRenameSymbol {}\n"); GitStatus status = await f.Repository.GetStatusAsync();
        GitChange renamed = status.Changes.Single(change => change.Path == current);
        Check(renamed.IndexStatus == 'R' && renamed.WorkTreeStatus == 'M' && renamed.OriginalPath == old, "Actual native rename detection retains both names and its later worktree change.");
        string journal = f.Output("rename.jsonl"); HashSet<string> words = await Words(new(await Wrapped(f, journal), status.Changes));
        Check(words.IsSupersetOf([Path.GetFileName(old), Path.GetFileNameWithoutExtension(old), Path.GetFileName(current), Path.GetFileNameWithoutExtension(current)])
            && !words.Contains(old) && !words.Contains(current), "Only old/current basenames and stems are offered for the staged rename.");
        string[][] diffs = Arguments(journal).Where(args => args.Contains("diff")).ToArray();
        Check(diffs.Single(args => args.Contains("--cached")).TakeLast(3).SequenceEqual(["--", current, old])
            && diffs.Single(args => !args.Contains("--cached")).TakeLast(2).SequenceEqual(["--", current]),
            "The old path participates only in the index projection; the working side uses its current literal path.");
    }

    private static async Task PatchAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string profile = Rules(f, ".sample = SYMBOL:(\\w+)\n");
        string[] lines = Enumerable.Range(0, 80).Select(i => "stable " + i).ToArray();
        lines[0] = "SYMBOL:FarOutsideContext"; lines[34] = "SYMBOL:NearContext"; lines[39] = "SYMBOL:RemovedSymbol";
        f.Write("Context.sample", string.Join('\n', lines) + "\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Five context lines");
        lines[39] = "SYMBOL:AddedSymbol"; f.Write("Context.sample", string.Join('\n', lines) + "\n");
        string external = f.Output("external-must-not-run");
        await f.GitAsync("config", "diff.external", "/no/such/external-" + Path.GetFileName(external));
        await f.GitAsync("config", "diff.test.textconv", "/no/such/textconv"); f.Write(".gitattributes", "*.sample diff=test\n");
        string journal = f.Output("context.jsonl"); HashSet<string> words = await Words(new(await Wrapped(f, journal), (await f.Repository.GetStatusAsync()).Changes, profile, true));
        string[] diff = Arguments(journal).Single(args => args.Contains("diff") && args.Contains("Context.sample"));
        Check(words.IsSupersetOf(["NearContext", "RemovedSymbol", "AddedSymbol"]) && !words.Contains("FarOutsideContext"), "Extraction sees the actual patch's five context lines and removed/added text, not the entire file.");
        Check(new[] { "--no-optional-locks", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", "--find-copies", "--unified=5", "--histogram" }.All(diff.Contains)
            && !Arguments(journal).Any(args => args.Contains("difftool")), "Completion invokes native Git with Windows context/rename options while disabling external presentation and text conversion.");
    }

    private static async Task HeadFallbackAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string profile = Rules(f, ".sample = TOKEN:(\\w+)\n");
        f.Write("Head.sample", "TOKEN:HeadOnly\n"); f.Write("Old.sample", "TOKEN:OldHeadOnly\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("HEAD fallback");
        f.Write("Head.sample", "TOKEN:IndexOnly\n"); await f.Repository.StageAsync(["Head.sample"]); f.Write("Head.sample", "TOKEN:WorkingOnly\n");
        GitStatus status = await f.Repository.GetStatusAsync(); string journal = f.Output("head-fallback.jsonl");
        GitRepository wrapped = await Wrapped(f, journal, "if 'diff' in a:sys.exit(0)\n");
        HashSet<string> words = await Words(new(wrapped, status.Changes, profile));
        Check(words.Contains("HeadOnly") && !words.Contains("IndexOnly") && !words.Contains("WorkingOnly"),
            "When native patches are absent, both MM sides use the real HEAD content, without reading the index as a replacement fallback.");
        GitChange rename = new("Head.sample", "Old.sample", 'R', '.', false, false);
        HashSet<string> renamed = await Words(new(wrapped, [rename], profile));
        Check(renamed.Contains("HeadOnly") && renamed.Contains("OldHeadOnly"), "The index-rename HEAD fallback concatenates current then old-name blobs when both really exist.");
        string[] blobs = Arguments(journal).Where(args => args.Contains("cat-file")).Select(args => args[^1]).ToArray();
        Check(blobs.All(blob => !blob.StartsWith(':')) && blobs.TakeLast(2).Select(blob => blob[(blob.IndexOf(':') + 1)..]).SequenceEqual(["Head.sample", "Old.sample"]),
            "Actual fallback argv addresses commit blobs in current/old order, never index-stage blobs.");
    }

    private static async Task WorkingFallbackAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string profile = Rules(f, ".sample = TOKEN:(\\w+)\n");
        f.Write("Added.sample", "TOKEN:IndexNew\n"); await f.Repository.StageAsync(["Added.sample"]); f.Write("Added.sample", "TOKEN:WorkingNew\n");
        f.Write("Gone.sample", "TOKEN:UnreachableSymbol\n"); GitStatus status = await f.Repository.GetStatusAsync(); File.Delete(f.File("Gone.sample"));
        string journal = f.Output("working-fallback.jsonl"); GitRepository wrapped = await Wrapped(f, journal, "if 'diff' in a:sys.exit(1)\n");
        HashSet<string> words = await Words(new(wrapped, status.Changes, profile));
        Check(words.Contains("WorkingNew") && !words.Contains("IndexNew") && words.Contains("Gone.sample") && !words.Contains("UnreachableSymbol"),
            "Patch failures for a staged addition fall through to the actual working file; a disappeared untracked file keeps its basename silently.");
        Check(!Arguments(journal).Any(args => args.Contains("cat-file") && args[^1].EndsWith(":Gone.sample", StringComparison.Ordinal)), "Untracked paths never trigger a HEAD read.");
    }

    private static async Task SubmoduleAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); using RepositoryFixture child = await RepositoryFixture.CreateAsync();
        child.Write("Inside.cs", "public class MustNotInspectSubmodule {}\n"); await child.Repository.StageAllAsync(); await child.Repository.CommitAsync("Child symbol");
        const string path = "vendor/Module.Name.cs";
        await f.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--", child.Repository.Path, path);
        GitStatus status = await f.Repository.GetStatusAsync(); Check(status.Changes.Single(change => change.Path == path).IsSubmodule, "Native status marks the actual submodule.");
        string journal = f.Output("submodule.jsonl"); HashSet<string> words = await Words(new(await Wrapped(f, journal), status.Changes));
        Check(words.Contains("Module.Name.cs") && !words.Contains("Module.Name") && !words.Contains(path) && !words.Contains("MustNotInspectSubmodule")
            && !Arguments(journal).Any(args => args.Contains("diff") || args.Contains("cat-file")), "A submodule contributes only its current basename and causes no content or history query.");
    }

    private static async Task RulesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string profile = Rules(f, ".sample = TOKEN:(\\p{L}+)(?:,(\\p{L}+))*\n.anch = ^TOKEN:(\\w+)\n");
        f.Write("Captures.sample", "TOKEN:Alpha,Bêta,Γάμμα\n"); f.Write("NoMultiline.anch", "prefix\nTOKEN:MustNotUseMultiline\n");
        f.Write("Upper.SAMPLE", "TOKEN:MustNotIgnoreExtensionCase\n");
        HashSet<string> words = await Words(new(f.Repository, (await f.Repository.GetStatusAsync()).Changes, profile));
        Check(words.IsSupersetOf(["Alpha", "Bêta", "Γάμμα"]) && !words.Contains("TOKEN:Alpha,Bêta,Γάμμα")
            && !words.Contains("MustNotUseMultiline") && !words.Contains("MustNotIgnoreExtensionCase"),
            "All captures of all nonzero groups are included, while whole matches, multiline matching and extension case folding are not invented.");
    }

    private static async Task InvalidRulesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); f.Write("Slow.evil", new string('a', 100000) + "!\n");
        GitStatus status = await f.Repository.GetStatusAsync(); string profile = Rules(f, "missing separator\n");
        InvalidDataException malformed = await Expect<InvalidDataException>(() => new GitCommitAutoCompleteProvider(f.Repository, status.Changes, profile).GetAutoCompleteWordsAsync());
        Check(malformed.Message.Contains("AutoCompleteRegexes.txt", StringComparison.Ordinal) && malformed.Message.Contains("line 1", StringComparison.Ordinal), "A malformed configured rule remains visible with its source and line.");
        await File.WriteAllTextAsync(Path.Combine(profile, "AutoCompleteRegexes.txt"), ".evil = (\n");
        await Expect<InvalidDataException>(() => new GitCommitAutoCompleteProvider(f.Repository, status.Changes, profile).GetAutoCompleteWordsAsync());
        await File.WriteAllTextAsync(Path.Combine(profile, "AutoCompleteRegexes.txt"), ".evil = (x)\n.evil = (y)\n");
        await Expect<InvalidDataException>(() => new GitCommitAutoCompleteProvider(f.Repository, status.Changes, profile).GetAutoCompleteWordsAsync());
        await File.WriteAllTextAsync(Path.Combine(profile, "AutoCompleteRegexes.txt"), ".evil = ((a+)+)$\n");
        Stopwatch elapsed = Stopwatch.StartNew();
        await Expect<RegexMatchTimeoutException>(() => new GitCommitAutoCompleteProvider(f.Repository, status.Changes, profile).GetAutoCompleteWordsAsync());
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "An explicitly visible regex extraction timeout bounds pathological configured backtracking.");
    }

    private static async Task CapturedContextAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); f.Write("Captured.cs", "public class CapturedSymbol {}\n");
        string journal = f.Output("context-capture.jsonl"); GitRepository wrapped = await Wrapped(f, journal);
        string? previousHome = Environment.GetEnvironmentVariable("HOME"); GitEnvironmentOptions previousOptions = GitEnvironment.Options;
        string firstHome = f.Output("first-home"), laterHome = f.Output("later-home"); Directory.CreateDirectory(firstHome); Directory.CreateDirectory(laterHome);
        try
        {
            Environment.SetEnvironmentVariable("HOME", firstHome);
            GitCommitAutoCompleteProvider provider = new(wrapped, (await f.Repository.GetStatusAsync()).Changes);
            Environment.SetEnvironmentVariable("HOME", laterHome); GitEnvironment.Configure(new() { Executable = "/no/such/later-git" });
            HashSet<string> words = await Words(provider);
            Check(words.Contains("CapturedSymbol") && Arguments(journal).Length == 0, "An untracked snapshot reads its captured worktree without launching a new Git status query.");
        }
        finally { Environment.SetEnvironmentVariable("HOME", previousHome); GitEnvironment.Configure(previousOptions); }
        f.Write("Tracked.cs", "public class BeforeContextSymbol {}\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Capture native environment");
        f.Write("Tracked.cs", "public class AfterContextSymbol {}\n");
        try
        {
            Environment.SetEnvironmentVariable("HOME", firstHome); GitCommitAutoCompleteProvider provider = new(wrapped, (await f.Repository.GetStatusAsync()).Changes);
            Environment.SetEnvironmentVariable("HOME", laterHome); GitEnvironment.Configure(new() { Executable = "/no/such/later-git" });
            Check((await Words(provider)).Contains("AfterContextSymbol") && Homes(journal).All(home => home == firstHome),
                "All actual native provider commands retain the executable and environment captured before the later application/profile change.");
        }
        finally { Environment.SetEnvironmentVariable("HOME", previousHome); GitEnvironment.Configure(previousOptions); }
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); f.Write("Cancel.cs", "public class BeforeCancel {}\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Owned diff"); f.Write("Cancel.cs", "public class AfterCancel {}\n");
        GitStatus status = await f.Repository.GetStatusAsync(); using CancellationTokenSource pre = new(); pre.Cancel();
        await Expect<OperationCanceledException>(() => new GitCommitAutoCompleteProvider(f.Repository, status.Changes).GetAutoCompleteWordsAsync(pre.Token));
        if (!OperatingSystem.IsLinux()) { return; }
        string marker = f.Output("diff-started.json"), journal = f.Output("cancel.jsonl");
        GitRepository wrapped = await Wrapped(f, journal, "import subprocess,time\nif 'diff' in a:\n r=subprocess.run(['/usr/bin/git']+a)\n m=" + JsonSerializer.Serialize(marker)
            + ";t=m+'.tmp'\n with open(t,'w') as f:json.dump({'pid':os.getpid(),'exit':r.returncode},f)\n os.replace(t,m)\n while True:time.sleep(.05)\n");
        using CancellationTokenSource cancel = new(); Task<IEnumerable<AutoCompleteWord>> operation = new GitCommitAutoCompleteProvider(wrapped, status.Changes).GetAutoCompleteWordsAsync(cancel.Token);
        int pid = 0, count = 0; Exception? observation = null;
        try
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(marker)) { if (operation.IsCompleted) { await operation; } if (DateTime.UtcNow > until) { throw new TimeoutException("The owned native diff did not publish its witness."); } await Task.Delay(15); }
            using JsonDocument record = JsonDocument.Parse(await File.ReadAllTextAsync(marker)); pid = record.RootElement.GetProperty("pid").GetInt32();
            Check(record.RootElement.GetProperty("exit").GetInt32() == 0 && Directory.Exists("/proc/" + pid), "The real native diff succeeded while its witnessed owned wrapper remains live."); count = File.ReadAllLines(journal).Length;
        }
        catch (Exception error) { observation = error; }
        finally { cancel.Cancel(); }
        await Expect<OperationCanceledException>(async () => await operation);
        if (observation is not null) { throw new InvalidOperationException("Observation failed after the owned provider task drained.", observation); }
        Check(!Directory.Exists("/proc/" + pid) && File.ReadAllLines(journal).Length == count && !Arguments(journal).Any(args => args.Contains("cat-file")),
            "Provider cancellation drains its actual native process and starts no HEAD/worktree fallback or replacement query.");
    }

    private static string Rules(RepositoryFixture f, string contents)
    { string directory = f.Output("profile"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "AutoCompleteRegexes.txt"), contents); return directory; }
    private static async Task<HashSet<string>> Words(GitCommitAutoCompleteProvider provider)
        => (await provider.GetAutoCompleteWordsAsync()).Select(word => word.Word).ToHashSet(StringComparer.Ordinal);
    private static async Task<GitRepository> Wrapped(RepositoryFixture f, string journal, string body = "")
    {
        string wrapper = f.Output("git-completion-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport json,os,sys\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(journal)
            + ",'a') as f:f.write(json.dumps({'argv':a,'home':os.environ.get('HOME')})+'\\n')\n" + body + "os.execv('/usr/bin/git',['git']+a)\n");
        if (!OperatingSystem.IsWindows())
        { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return new(f.Repository.Path, new GitRunner(wrapper));
    }
    private static string[][] Arguments(string journal) => !File.Exists(journal) ? [] : File.ReadAllLines(journal).Select(line =>
    { using JsonDocument item = JsonDocument.Parse(line); return item.RootElement.GetProperty("argv").EnumerateArray().Select(value => value.GetString()!).ToArray(); }).ToArray();
    private static string?[] Homes(string journal) => File.ReadAllLines(journal).Select(line =>
    { using JsonDocument item = JsonDocument.Parse(line); return item.RootElement.GetProperty("home").GetString(); }).ToArray();
    private static string Output(this RepositoryFixture fixture, string name) => Path.Combine(fixture.Root, name);
    private static async Task<GitResult> GitAsync(this RepositoryFixture fixture, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(fixture.Repository.Path, arguments);
        if (!result.Success) { throw new GitCommandException(arguments, result); } return result;
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
}
