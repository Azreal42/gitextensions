using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Plugins.Tests;

internal static class ReleaseNotesArgumentsScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("release notes editable log arguments filter real merge history while preserving text tsv and html exports", MergeFilterAndExportsAsync),
        ("release notes formatted quoted pathspecs retain braces Unicode newlines and shell-looking literal names", LiteralPathAsync),
        ("release notes reject malformed format quoting output redirection and native Git errors without repository writes", InvalidArgumentsAsync),
        ("release notes resolve both revision inputs before substituting their OIDs and leave an empty range valid", ResolvedRangeAsync),
        ("release notes cancellation drains a genuine Git log blocked on its private trace FIFO without repository changes", CancellationAsync)
    ];
    private const string DefaultArguments = "--pretty=\"format:%h@%s%b\" --abbrev-commit {0}..{1}";
    private static Dictionary<string, string?> Values(string from, string? arguments = null, string format = "text", string to = "HEAD")
    {
        Dictionary<string, string?> values = new() { ["fromRevision"] = from, ["toRevision"] = to, ["format"] = format };
        if (arguments is not null) { values["arguments"] = arguments; }
        return values;
    }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static string FormatLiteral(string text) => text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
    private static string Snapshot(string root) => JsonSerializer.Serialize(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(file => Path.GetRelativePath(root, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal));
    private static void Unchanged(global::Fixture fixture, string before)
        => Check(Snapshot(fixture.Repository.Path) == before, "Release-note read changed repository refs, config, index, objects or worktree bytes.");

    private static async Task MergeFilterAndExportsAsync()
    {
        using global::Fixture f = await global::Fixture.CreateAsync(); string from = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.CreateBranchAsync("topic", checkout: true);
        File.WriteAllText(Path.Combine(f.Repository.Path, "topic.txt"), "topic\n");
        string topic = await f.CommitAtAsync(f.Repository, "Topic <tag> & élève\n\nBody α", global::Fixture.Now.AddDays(-2));
        await f.Repository.CheckoutAsync("main"); File.WriteAllText(Path.Combine(f.Repository.Path, "main.txt"), "main\n");
        string main = await f.CommitAtAsync(f.Repository, "Main change", global::Fixture.Now.AddDays(-1));
        await f.Repository.RunAsync(["merge", "--no-ff", "-m", "Merge topic", "topic"]);
        string merged = await f.Repository.ResolveRevisionAsync("HEAD"), before = Snapshot(f.Repository.Path);
        ReleaseNotesGeneratorPlugin plugin = new();
        PluginResult all = await plugin.ExecuteAsync(new(f.Repository), Values(from));
        Check(all.Table!.Rows.Count == 3 && all.Table!.Rows.Any(row => merged.StartsWith(row.Key, StringComparison.Ordinal)), "The unchanged default must include the actual merge.");
        foreach (string format in new[] { "text", "tsv", "html" })
        {
            PluginResult result = await plugin.ExecuteAsync(new(f.Repository), Values(from, DefaultArguments + " --no-merges", format));
            Check(result.Success && !result.RepositoryChanged && result.Table!.Rows.Count == 2, "Editable --no-merges did not filter the actual merge.");
            Check(result.Table!.Rows.Any(row => topic.StartsWith(row.Key, StringComparison.Ordinal)) && result.Table!.Rows.Any(row => main.StartsWith(row.Key, StringComparison.Ordinal)), "Filtering lost a real non-merge commit.");
            if (format == "html")
            {
                string decoded = System.Net.WebUtility.HtmlDecode(result.Text);
                Check(result.Text.Contains("&lt;tag&gt; &amp;", StringComparison.Ordinal)
                    && !result.Text.Contains("<tag>", StringComparison.Ordinal)
                    && decoded.Contains("Topic <tag> & élève", StringComparison.Ordinal)
                    && decoded.Contains("Body α", StringComparison.Ordinal),
                    "HTML export must escape markup and retain the original Unicode message after entity decoding.");
            }
            else { Check(result.Text.Contains("Topic <tag> & élève", StringComparison.Ordinal) && result.Text.Contains("Body α", StringComparison.Ordinal), "Native text/TSV export lost parsed Unicode body content."); }
            if (format == "tsv") { Check(result.Text.Contains('\t'), "TSV export lost tab-separated columns."); }
            Unchanged(f, before);
        }
    }
    private static async Task LiteralPathAsync()
    {
        using global::Fixture f = await global::Fixture.CreateAsync(); string from = await f.Repository.ResolveRevisionAsync("HEAD");
        const string literal = "literal ' \"雪\\line\n\t{braces} $(touch SENTINEL).txt";
        File.WriteAllText(Path.Combine(f.Repository.Path, literal), "selected\n");
        string selected = await f.CommitAtAsync(f.Repository, "Selected literal path", global::Fixture.Now.AddDays(-2));
        File.WriteAllText(Path.Combine(f.Repository.Path, "unrelated.txt"), "unrelated\n");
        await f.CommitAtAsync(f.Repository, "Unrelated path", global::Fixture.Now.AddDays(-1));
        const string optionName = "--output=literal {braces}.txt";
        File.WriteAllText(Path.Combine(f.Repository.Path, optionName), "literal option-looking path\n");
        string optionCommit = await f.CommitAtAsync(f.Repository, "Literal option-looking filename", global::Fixture.Now);
        string before = Snapshot(f.Repository.Path);
        PluginResult result = await new ReleaseNotesGeneratorPlugin().ExecuteAsync(new(f.Repository),
            Values(from, DefaultArguments + " -- " + FormatLiteral(Quote(literal))));
        Check(result.Table!.Rows.Count == 1 && selected.StartsWith(result.Table!.Rows[0].Key, StringComparison.Ordinal), "The quoted literal pathspec was split, expanded or failed to filter Git.");
        result = await new ReleaseNotesGeneratorPlugin().ExecuteAsync(new(f.Repository), Values(from, DefaultArguments + " -- " + FormatLiteral(Quote(optionName))));
        Check(result.Table!.Rows.Count == 1 && optionCommit.StartsWith(result.Table!.Rows[0].Key, StringComparison.Ordinal), "A literal --output-looking path after -- was rejected as an option.");
        Check(!File.Exists(Path.Combine(f.Repository.Path, "SENTINEL")), "The argument template was interpreted by a shell.");
        Unchanged(f, before);
    }
    private static async Task InvalidArgumentsAsync()
    {
        using global::Fixture f = await global::Fixture.CreateAsync(); string from = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.CommitAtAsync(f.Repository, "Actual release commit", global::Fixture.Now);
        string before = Snapshot(f.Repository.Path); ReleaseNotesGeneratorPlugin plugin = new();
        foreach (string template in new[] { DefaultArguments + " {2}", DefaultArguments + " {", DefaultArguments + " }" })
        {
            FormatException error = await ThrowsAsync<FormatException>(() => plugin.ExecuteAsync(new(f.Repository), Values(from, template)));
            Check(error.Message.Contains("Git log arguments format", StringComparison.Ordinal), "The format error did not identify the editable field.");
        }
        foreach (string template in new[] { DefaultArguments + " 'unfinished", DefaultArguments + " " + '\\' })
        {
            ArgumentException error = await ThrowsAsync<ArgumentException>(() => plugin.ExecuteAsync(new(f.Repository), Values(from, template)));
            Check(error.Message.Contains("Git log arguments quoting", StringComparison.Ordinal), "The quoting diagnostic did not identify the editable field.");
        }
        string output = Path.Combine(f.Root, "must-not-be-created notes.txt");
        foreach (string option in new[] { "--output " + Quote(output), "--output=" + Quote(output) })
        {
            ArgumentException error = await ThrowsAsync<ArgumentException>(() => plugin.ExecuteAsync(new(f.Repository), Values(from, DefaultArguments + " " + option)));
            Check(error.Message.Contains("--output", StringComparison.Ordinal) && !File.Exists(output), "The readonly reader redirected output to a file.");
        }
        GitCommandException native = await ThrowsAsync<GitCommandException>(() => plugin.ExecuteAsync(new(f.Repository), Values(from, DefaultArguments + " --not-a-release-notes-option")));
        Check(native.Message.Length > 0, "The native Git error was hidden behind a fabricated success.");
        Unchanged(f, before);
    }
    private static async Task ResolvedRangeAsync()
    {
        using global::Fixture f = await global::Fixture.CreateAsync(); await f.Repository.RunAsync(["tag", "from-été"]);
        string hash = await f.CommitAtAsync(f.Repository, "Last commit", global::Fixture.Now); await f.Repository.RunAsync(["tag", "to-été"]);
        string before = Snapshot(f.Repository.Path); ReleaseNotesGeneratorPlugin plugin = new();
        // Alignment and escaped braces use .NET composite formatting, not a simple string replacement.
        PluginResult result = await plugin.ExecuteAsync(new(f.Repository), Values("from-été", "--pretty=\"format:%h@{{literal}} %s%b\" --abbrev-commit {0,45}..{1}", to: "to-été"));
        Check(result.Table!.Rows.Count == 1 && hash.StartsWith(result.Table!.Rows[0].Key, StringComparison.Ordinal) && result.Text.Contains("{literal} Last commit", StringComparison.Ordinal), "Composite formatting or resolved endpoint substitution changed.");
        result = await plugin.ExecuteAsync(new(f.Repository), Values("to-été", to: "to-été"));
        Check(result.Success && result.Table!.Rows.Count == 0, "An actual empty resolved range is a valid empty result.");
        await ThrowsAsync<GitCommandException>(() => plugin.ExecuteAsync(new(f.Repository), Values("HEAD; touch SENTINEL")));
        Check(!File.Exists(Path.Combine(f.Repository.Path, "SENTINEL")), "Revision input reached a shell rather than safe resolution.");
        Unchanged(f, before);
    }
    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The real FIFO cancellation fixture requires Linux."); }
        using global::Fixture f = await global::Fixture.CreateAsync(); string from = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.CommitAtAsync(f.Repository, "Read me", global::Fixture.Now); string before = Snapshot(f.Repository.Path);
        using CancellationTokenSource pre = new(); pre.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new ReleaseNotesGeneratorPlugin().ExecuteAsync(new(f.Repository), Values(from), pre.Token));
        string gate = Path.Combine(f.Root, "git-log-gate"); Directory.CreateDirectory(gate); string wrapper = Path.Combine(gate, "git");
        File.WriteAllText(wrapper, """
            #!/usr/bin/python3
            import json,os,pathlib,sys
            args=sys.argv[1:];i=0
            while i<len(args):
                if args[i] in ['-c','--git-dir','--work-tree','--namespace','-C','--config-env']:i+=2;continue
                if args[i].startswith('-'):i+=1;continue
                break
            env=dict(os.environ)
            if i<len(args) and args[i]=='log':
                root=pathlib.Path(__file__).parent;fifo=root/'trace.fifo';os.mkfifo(fifo,0o600)
                fields=pathlib.Path('/proc/self/stat').read_text().rsplit(')',1)[1].split()
                (root/'pid.tmp').write_text(json.dumps(dict(pid=os.getpid(),start_ticks=int(fields[19]))));(root/'pid.tmp').replace(root/'pid.json')
                env['GIT_TRACE2_EVENT']=str(fifo)
            os.execve('/usr/bin/git',['/usr/bin/git',*args],env)
            """);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository gated = new(f.Repository.Path, new GitRunner(GitExecutionEnvironment.Capture(new GitEnvironmentOptions { Executable = wrapper })));
        using CancellationTokenSource cancellation = new();
        Task<PluginResult> pending = new ReleaseNotesGeneratorPlugin().ExecuteAsync(new(gated), Values(from), cancellation.Token);
        int pid = 0; long start = 0; bool genuineGit = false;
        string nativeGit = new FileInfo("/usr/bin/git").ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath("/usr/bin/git");
        try
        {
            Stopwatch timer = Stopwatch.StartNew(); string marker = Path.Combine(gate, "pid.json");
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (File.Exists(marker))
                {
                    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(marker)); pid = json.RootElement.GetProperty("pid").GetInt32(); start = json.RootElement.GetProperty("start_ticks").GetInt64();
                    if (Matching(pid, start) && new FileInfo("/proc/" + pid + "/exe").ResolveLinkTarget(true)?.FullName == nativeGit) { genuineGit = true; break; }
                }
                await Task.Delay(10);
            }
            Check(genuineGit && pid > 0 && Matching(pid, start) && !pending.IsCompleted, "The real Git log never reached its blocked FIFO.");
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
            Check(!Matching(pid, start), "Cancellation returned before the exact native Git PID had been reaped.");
            Unchanged(f, before);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
        }
    }
    private static bool Matching(int pid, long start)
    {
        try { return long.Parse(File.ReadAllText("/proc/" + pid + "/stat").Split(')')[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19], CultureInfo.InvariantCulture) == start; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
