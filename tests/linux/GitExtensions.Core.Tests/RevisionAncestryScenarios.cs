using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class RevisionAncestryScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("revision ancestry retains native all-parent merge order and includes its captured commit without writes", NativeMergeAsync),
        ("revision ancestry fixes its maximum at fifty and resolves a moved ref afresh without a cache", LimitAndFreshnessAsync),
        ("revision ancestry resolves literal Unicode refs annotated tags expressions and uncached bare repositories", LiteralAndBareAsync),
        ("revision ancestry rejects invalid noncommit and malformed records while retaining native failures", ValidationAndFailureAsync),
        ("revision ancestry cancellation drains its exact native read identity without returning a fallback", CancellationAsync)
    ];

    private static async Task NativeMergeAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await Git(f.Repository, "branch", "side");
        f.Write("main.txt", "main\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Main descendant");
        await Git(f.Repository, "checkout", "side"); f.Write("side.txt", "side\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Other parent");
        string side = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, "checkout", "main"); await Git(f.Repository, "merge", "--no-ff", "side", "-m", "Actual merge");
        string captured = await f.Repository.ResolveRevisionAsync("HEAD");
        string[] expected = Lines((await Git(f.Repository, "rev-list", "--max-count=50", captured, "--")).StandardOutput);
        string[] firstParent = Lines((await Git(f.Repository, "rev-list", "--max-count=50", "--first-parent", captured, "--")).StandardOutput);
        string before = Material(f.Repository.Path);
        IReadOnlyList<string> actual = await f.Repository.ReadRevisionAncestryAsync(captured);
        Check(actual.SequenceEqual(expected) && actual[0] == captured && actual.Contains(side) && !firstParent.Contains(side)
            && actual.Count > firstParent.Length, "The native all-parent reader order, opening commit or second-parent ancestry changed.");
        Check(Material(f.Repository.Path) == before, "Reading merge ancestry changed a ref, index, worktree, config or object byte.");
    }

    private static async Task LimitAndFreshnessAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        StringBuilder input = new();
        for (int index = 0; index < 70; index++)
        {
            string message = "Ancestry " + index.ToString(CultureInfo.InvariantCulture) + "\n";
            input.Append("commit refs/heads/main\nmark :").Append(index + 1).Append("\n")
                .Append("author Linux Test <linux-test@example.invalid> ").Append(1000000000L + index).Append(" +0000\n")
                .Append("committer Linux Test <linux-test@example.invalid> ").Append(1000000000L + index).Append(" +0000\n")
                .Append("data ").Append(Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message);
            if (index > 0) { input.Append("from :").Append(index).Append('\n'); }
            input.Append('\n');
        }
        input.Append("done\n");
        GitResult imported = await new GitRunner().RunAsync(f.Repository.Path, ["fast-import", "--quiet", "--date-format=raw"], standardInput: input.ToString());
        Check(imported.Success, imported.StandardError);
        string[] native = Lines((await Git(f.Repository, "rev-list", "HEAD", "--")).StandardOutput);
        Check(native.Length == 70, "The independent history must exceed the fixed fifty-commit read.");
        await Git(f.Repository, "branch", "moving"); string before = Material(f.Repository.Path);
        IReadOnlyList<string> limited = await f.Repository.ReadRevisionAncestryAsync("moving");
        Check(limited.Count == 50 && limited.SequenceEqual(native.Take(50)) && limited[0] == native[0],
            "The fixed maximum includes the selected commit and must neither reverse nor truncate its parents differently.");
        Check(Material(f.Repository.Path) == before, "The first bounded read wrote repository materials.");
        await Git(f.Repository, "update-ref", "refs/heads/moving", native[30]); before = Material(f.Repository.Path);
        IReadOnlyList<string> moved = await f.Repository.ReadRevisionAncestryAsync("moving");
        Check(moved.SequenceEqual(native.Skip(30)) && moved.Count == 40 && Material(f.Repository.Path) == before,
            "A second read reused a cached alias/result or changed materials rather than resolving the moved ref afresh.");
    }

    private static async Task LiteralAndBareAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string initial = await f.Repository.ResolveRevisionAsync("HEAD");
        f.Write("next.txt", "next\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Later");
        const string literal = "literal/été-$(touch${IFS}ancestry-executed);雪";
        await Git(f.Repository, "branch", literal, initial); await Git(f.Repository, "tag", "-a", "Tag/Été", "-m", "Annotated", initial);
        string barePath = Path.Combine(f.Root, "bare λ '$().git"); await Git(f.Repository, "clone", "--bare", f.Repository.Path, barePath);
        GitRepository bare = new(barePath); Check(bare.Info is null, "Bare support must not depend on prepopulated discovery metadata.");
        string before = Material(f.Repository.Path), bareBefore = Material(barePath);
        foreach (string expression in new[] { literal, "Tag/Été", "HEAD~1" })
        {
            Check((await f.Repository.ReadRevisionAncestryAsync(expression)).SequenceEqual([initial])
                && (await bare.ReadRevisionAncestryAsync(expression)).SequenceEqual([initial]),
                "A literal ref, annotated tag or strict commit expression did not resolve in normal and bare repositories: " + expression);
        }
        Check((await bare.ReadRevisionAncestryAsync("HEAD")).SequenceEqual(Lines((await Git(bare, "rev-list", "--max-count=50", "HEAD", "--")).StandardOutput)),
            "The uncached bare repository lost native ancestry order.");
        Check(Material(f.Repository.Path) == before && Material(barePath) == bareBefore
            && !File.Exists(f.File("ancestry-executed")) && !File.Exists(Path.Combine(barePath, "ancestry-executed")),
            "Literal metacharacters executed or normal/bare ancestry reads changed repository bytes.");
    }

    private static async Task ValidationAndFailureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string before = Material(f.Repository.Path);
        foreach (string invalid in new[] { "", " \t", "--all", "HEAD\0--all" })
        { await Expect<ArgumentException>(() => f.Repository.ReadRevisionAncestryAsync(invalid)); }
        foreach (string absent in new[] { "missing-ancestry-ref", "HEAD --all", "HEAD;touch ancestry-executed" })
        { await Expect<GitCommandException>(() => f.Repository.ReadRevisionAncestryAsync(absent)); }
        string tree = (await Git(f.Repository, "rev-parse", "HEAD^{tree}")).StandardOutput.TrimEnd('\r', '\n');
        await Expect<GitCommandException>(() => f.Repository.ReadRevisionAncestryAsync(tree));
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        OperationCanceledException cancellation = await Expect<OperationCanceledException>(() => f.Repository.ReadRevisionAncestryAsync("HEAD", cancelled.Token));
        Check(cancellation.CancellationToken == cancelled.Token && Material(f.Repository.Path) == before && !File.Exists(f.File("ancestry-executed")),
            "Invalid/noncommit inputs or caller precancellation produced a fallback ancestry or modified repository bytes.");
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        await Expect<GitCommandException>(() => unborn.Repository.ReadRevisionAncestryAsync("HEAD"));
        if (!OperatingSystem.IsLinux()) { return; }
        string mode = Path.Combine(f.Root, "ancestry-mode"), argv = Path.Combine(f.Root, "ancestry-arguments");
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        string executable = await Executable(f.Root, "ancestry failure git", "for arg do\n  if test \"$arg\" = rev-list; then\n"
            + "    printf '%s\\0' \"$@\" > " + Quote(argv) + "\n"
            + "    printf '%s\\n' " + Quote(head) + "\n"
            + "    if test -f " + Quote(mode) + "; then printf '%s\\n' malformed-id; exit 0; fi\n"
            + "    printf '%s' 'private ancestry native failure' >&2; exit 37\n  fi\ndone\nexec /usr/bin/git \"$@\"\n");
        GitRepository injected = new(f.Repository.Path, new GitRunner(executable));
        GitCommandException failure = await Expect<GitCommandException>(() => injected.ReadRevisionAncestryAsync("HEAD"));
        Check(failure.Result.ExitCode == 37 && failure.Result.StandardError.Contains("private ancestry native failure", StringComparison.Ordinal)
            && failure.Arguments.SequenceEqual(["--no-optional-locks", "rev-list", "--max-count=50", head, "--"]),
            "A failed native read must retain its failure, even if it emitted a valid-looking prefix.");
        string[] actualArgs = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(argv)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int command = Array.IndexOf(actualArgs, "rev-list");
        Check(command > 0 && actualArgs[command - 1] == "--no-optional-locks"
            && actualArgs[command..].SequenceEqual(["rev-list", "--max-count=50", head, "--"]),
            "The bounded request must use one resolved commit and the literal revision terminator, without view filters or first-parent.");
        await File.WriteAllTextAsync(mode, "malformed\n"); await Expect<InvalidDataException>(() => injected.ReadRevisionAncestryAsync("HEAD"));
        Check(Material(f.Repository.Path) == before, "Native failures or malformed ancestry records changed repository bytes.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string before = Material(f.Repository.Path);
        string pidFile = Path.Combine(f.Root, "ancestry-read-pid"), release = Path.Combine(f.Root, "ancestry-release");
        string executable = Path.Combine(f.Root, "retained ancestry git");
        await File.WriteAllTextAsync(executable, "#!/usr/bin/python3\nimport os, pathlib, sys, time\n"
            + "if 'rev-list' in sys.argv[1:]:\n"
            + "    target = pathlib.Path(" + Python(pidFile) + ")\n"
            + "    temporary = target.with_suffix('.tmp')\n    temporary.write_text(str(os.getpid()))\n    os.replace(temporary, target)\n"
            + "    while not pathlib.Path(" + Python(release) + ").exists(): time.sleep(.02)\n"
            + "os.execv('/usr/bin/git', ['/usr/bin/git', *sys.argv[1:]])\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository repository = new(f.Repository.Path, new GitRunner(executable));
        using CancellationTokenSource cancellation = new(); Task<IReadOnlyList<string>> work = repository.ReadRevisionAncestryAsync("HEAD", cancellation.Token);
        try
        {
            (int Pid, string Start)? identity = null;
            await Until(() =>
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out int pid) && Start(pid) is string start)
                { identity = (pid, start); return true; }
                return false;
            });
            (int Pid, string Start) captured = identity ?? throw new InvalidOperationException("The native ancestry identity was not captured.");
            Check(!work.IsCompleted && Alive(captured), "Cancellation must witness an actual pending native ancestry read.");
            cancellation.Cancel(); OperationCanceledException error = await Expect<OperationCanceledException>(() => work);
            Check(error.CancellationToken == cancellation.Token && work.IsCanceled && !Alive(captured),
                "Read cancellation returned before the exact PID/start identity disappeared, or lost the caller token.");
            Check(Material(f.Repository.Path) == before, "Canceling ancestry modified repository materials.");
            await File.WriteAllTextAsync(release, "release\n");
            Check(!Alive(captured) && Material(f.Repository.Path) == before, "A retired read cannot execute or write after its release marker appears.");
        }
        finally
        {
            cancellation.Cancel(); await File.WriteAllTextAsync(release, "release\n");
            try { await work.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(TimeSpan.FromSeconds(15)); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); } Check(condition(), "The native ancestry gate did not reach its barrier."); }
    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    { GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-optional-locks", .. arguments]); if (!result.Success) { throw new GitCommandException(arguments, result); } return result; }
    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    private static string Material(string repository) => string.Join('\n', Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(repository, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Python(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    private static async Task<string> Executable(string directory, string name, string script)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private executable fixtures require Linux."); }
        string path = Path.Combine(directory, name); await File.WriteAllTextAsync(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static string? Start(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => Start(identity.Pid) == identity.Start;
}
