using System.Text;
using GitExtensions.Linux;

internal static class MergeConflictScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("merge explicit fast forward overrides merge.ff configuration", FastForwardAsync),
        ("merge no-fast-forward creates two-parent commit and custom log message", MergeCommitAsync),
        ("merge no-commit leaves the index and native continuation without a commit", NoCommitAsync),
        ("merge squash leaves a single-parent commit to the user", SquashAsync),
        ("merge unrelated histories require the explicit option", UnrelatedAsync),
        ("merge invalid flags and incompatible message codec preserve repository state", InvalidAsync),
        ("merge conflict stages resolve literal paths with base ours theirs and manual bytes", ConflictsAsync),
        ("merge modify-delete requires an explicit deletion choice", DeleteAsync),
        ("merge configured tool receives literal stages and refuses unsupported control paths unchanged", ToolAsync),
        ("merge retains native branch messages and reports failed hooks despite MERGE_HEAD", MessageAndHookAsync)
    ];
    private static void Check(bool condition, string reason) { if (!condition) { throw new Exception(reason); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task FastForwardAsync()
    {
        using F f = await F.Create(); string topic = await f.Topic(); await f.Git("config", "merge.ff", "false");
        f.Write("unrelated.txt", "local bytes");
        GitMergeResult result = await f.Repository.MergeAsync(new GitMergeOptions(topic));
        Check(result.Result.Success && result.HeadChanged && await f.Text("rev-parse", "HEAD") == topic, "The FF choice must override configured no-FF and move directly to the source.");
        Check(File.ReadAllText(f.File("unrelated.txt")) == "local bytes", "Unrelated untracked data survives.");
    }
    private static async Task MergeCommitAsync()
    {
        using F f = await F.Create(); string topic = await f.Topic(); await f.Git("config", "merge.ff", "only");
        GitMergeResult result = await f.Repository.MergeAsync(new GitMergeOptions(topic, AllowFastForward: false, Strategy: "ort", Message: "Custom λ\n\nBody", LogCount: 20));
        Check(result.Result.Success && (await f.Text("show", "-s", "--format=%P", "HEAD")).Split(' ').Length == 2, "The no-FF choice creates a merge even with merge.ff=only.");
        string message = await f.Text("show", "-s", "--format=%B", "HEAD");
        Check(message.Contains("Custom λ") && message.Contains("Topic change"), "Custom message and native shortlog both reach the merge commit.");
    }
    private static async Task NoCommitAsync()
    {
        using F f = await F.Create(); string topic = await f.Topic();
        GitMergeResult result = await f.Repository.MergeAsync(new GitMergeOptions(topic, AllowFastForward: false, NoCommit: true));
        Check(result.Result.Success && result.CanContinue && !result.HeadChanged && await f.Text("rev-parse", "HEAD") == f.Initial, "No-commit retains the original HEAD and native MERGE_HEAD.");
        Check(await f.Text("show", ":topic.txt") == "topic", "The merged file is staged for a later commit.");
    }
    private static async Task SquashAsync()
    {
        using F f = await F.Create(); string topic = await f.Topic();
        GitMergeResult result = await f.Repository.MergeAsync(new GitMergeOptions(topic, Squash: true));
        Check(result.Result.Success && !result.HeadChanged && !result.CanContinue, "Squash stages the aggregate without creating native merge parent metadata.");
        await f.Git("commit", "-m", "Squashed"); Check(await f.Text("show", "-s", "--format=%P", "HEAD") == f.Initial, "A later squash commit has one parent.");
    }
    private static async Task UnrelatedAsync()
    {
        using F f = await F.Create(); await f.Git("checkout", "--orphan", "unrelated"); await f.Git("rm", "-rf", "."); f.Write("other.txt", "other"); await f.Git("add", "."); await f.Git("commit", "-m", "Other root");
        string other = await f.Text("rev-parse", "HEAD"); await f.Git("checkout", "main");
        await Expect<GitCommandException>(() => f.Repository.MergeAsync(new GitMergeOptions(other)));
        Check(await f.Text("rev-parse", "HEAD") == f.Initial && await f.Text("status", "--porcelain") == "", "Unrelated rejection preserves checkout.");
        Check((await f.Repository.MergeAsync(new GitMergeOptions(other, AllowUnrelatedHistories: true))).Result.Success, "Explicit unrelated option admits both roots.");
    }
    private static async Task InvalidAsync()
    {
        using F f = await F.Create(); string topic = await f.Topic(); f.Write("local.txt", "local"); await f.Git("add", "local.txt");
        string head = await f.Text("rev-parse", "HEAD"), status = await f.Text("status", "--porcelain=v2"); byte[] index = File.ReadAllBytes(f.File(".git/index"));
        await Expect<ArgumentException>(() => f.Repository.MergeAsync(new GitMergeOptions(topic, AllowFastForward: false, Squash: true)));
        await Expect<ArgumentOutOfRangeException>(() => f.Repository.MergeAsync(new GitMergeOptions(topic, LogCount: -1)));
        await f.Git("config", "i18n.commitencoding", "iso-8859-1");
        await Expect<EncoderFallbackException>(() => f.Repository.MergeAsync(new GitMergeOptions(topic, Message: "λ")));
        Check(await f.Text("rev-parse", "HEAD") == head && await f.Text("status", "--porcelain=v2") == status && File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "Validation and codec failures precede index/worktree mutation.");
    }
    private static async Task ConflictsAsync()
    {
        const string path = "literal [*] λ\nfile.txt";
        foreach ((GitConflictResolution choice, string expected) in new[] { (GitConflictResolution.Base, "base\n"), (GitConflictResolution.Ours, "ours\n"), (GitConflictResolution.Theirs, "theirs\n"), (GitConflictResolution.Manual, "manual\n") })
        {
            using F f = await F.Create(path); string topic = await f.Topic(path, "theirs\n"); f.Write(path, "ours\n"); await f.Git("add", "--", path); await f.Git("commit", "-m", "Ours");
            GitMergeResult result = await f.Repository.MergeAsync(new GitMergeOptions(topic, NoCommit: true));
            GitConflictPath conflict = (await f.Repository.GetConflictsAsync()).Single();
            Check(result.ConflictedPaths.SequenceEqual([path]) && conflict.Path == path && conflict.Stages.Select(stage => stage.Stage).SequenceEqual([1, 2, 3]), "Read actual NUL-delimited unmerged stages.");
            if (choice == GitConflictResolution.Manual) { f.Write(path, expected); }
            await f.Repository.ResolveConflictAsync(path, choice);
            Check(File.ReadAllText(f.File(path)) == expected && await f.Text("show", ":" + path) == expected.TrimEnd('\n') && (await f.Repository.GetConflictsAsync()).Count == 0, "Selected stage or manual bytes reach worktree and index exactly.");
        }
    }
    private static async Task DeleteAsync()
    {
        using F f = await F.Create(); string topic = await f.Topic("base.txt", "theirs\n"); await f.Git("rm", "base.txt"); await f.Git("commit", "-m", "Delete ours");
        await f.Repository.MergeAsync(new GitMergeOptions(topic, NoCommit: true));
        await Expect<InvalidOperationException>(() => f.Repository.ResolveConflictAsync("base.txt", GitConflictResolution.Ours));
        Check((await f.Repository.GetConflictsAsync()).Count == 1 && File.Exists(f.File("base.txt")), "An absent version must not silently delete the file.");
        await f.Repository.ResolveConflictAsync("base.txt", GitConflictResolution.Delete);
        Check(!File.Exists(f.File("base.txt")) && (await f.Repository.GetConflictsAsync()).Count == 0, "Explicit Delete resolves the modify-delete conflict.");
    }
    private static async Task ToolAsync()
    {
        const string path = "literal [*] λ file.txt"; using F f = await F.Create(path); string topic = await f.Topic(path, "theirs\n"); f.Write(path, "ours\n"); await f.Git("add", "--", path); await f.Git("commit", "-m", "Ours"); await f.Repository.MergeAsync(new GitMergeOptions(topic, NoCommit: true));
        await f.Git("config", "merge.tool", "private-test"); await f.Git("config", "mergetool.private-test.cmd", "test -f \"$BASE\" && test -f \"$LOCAL\" && test -f \"$REMOTE\" && cp \"$REMOTE\" \"$MERGED\"");
        await f.Git("config", "mergetool.private-test.trustExitCode", "true"); await f.Git("config", "mergetool.keepBackup", "false");
        await f.Repository.RunMergeToolAsync(path);
        Check(File.ReadAllText(f.File(path)) == "theirs\n" && (await f.Repository.GetConflictsAsync()).Count == 0, "Native configured tool receives the real stage files and literal glob/space pathname.");
        const string unsupported = "line\nfile.txt"; using F other = await F.Create(unsupported); string otherTopic = await other.Topic(unsupported, "theirs\n"); other.Write(unsupported, "ours\n"); await other.Git("add", "--", unsupported); await other.Git("commit", "-m", "Ours"); await other.Repository.MergeAsync(new GitMergeOptions(otherTopic, NoCommit: true));
        byte[] index = File.ReadAllBytes(other.File(".git/index")), working = File.ReadAllBytes(other.File(unsupported));
        await Expect<NotSupportedException>(() => other.Repository.RunMergeToolAsync(unsupported));
        Check(File.ReadAllBytes(other.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(other.File(unsupported)).SequenceEqual(working), "Unsupported native helper input must preserve all user conflict data.");
    }
    private static async Task MessageAndHookAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        using F f = await F.Create(); string topic = await f.Topic();
        await f.Repository.MergeAsync(new GitMergeOptions("topic", AllowFastForward: false));
        Check((await f.Text("show", "-s", "--format=%s", "HEAD")).Contains("Merge branch 'topic'", StringComparison.Ordinal), "Native default message retains the selected branch name.");
        Check((await f.Text("show", "-s", "--format=%P", "HEAD")).Split(' ')[1] == topic, "The second parent is the actual selected topic.");
        using F rejected = await F.Create(); await rejected.Topic(); rejected.Write(".git/hooks/pre-merge-commit", "#!/bin/sh\necho merge-hook-rejected >&2\nexit 7\n");
        File.SetUnixFileMode(rejected.File(".git/hooks/pre-merge-commit"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitCommandException error = await Expect<GitCommandException>(() => rejected.Repository.MergeAsync(new GitMergeOptions("topic", AllowFastForward: false)));
        Check(error.Result.StandardError.Contains("merge-hook-rejected", StringComparison.Ordinal) && File.Exists(rejected.File(".git/MERGE_HEAD")) && await rejected.Text("rev-parse", "HEAD") == rejected.Initial, "A hook failure remains an error even when Git leaves MERGE_HEAD and staged merge results.");
    }
    private sealed class F : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-merge-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository => new(Root);
        public string Initial { get; private set; } = "";
        public string File(string path) => System.IO.Path.Combine(Root, path);
        public void Write(string path, string value) => System.IO.File.WriteAllText(File(path), value);
        public Task<GitResult> Git(params string[] arguments) => new GitRunner().RunAsync(Root, arguments).ContinueWith(task => { GitResult result = task.GetAwaiter().GetResult(); if (!result.Success) { throw new GitCommandException(arguments, result); } return result; });
        public async Task<string> Text(params string[] arguments) => (await Git(arguments)).StandardOutput.TrimEnd('\r', '\n');
        public static async Task<F> Create(string path = "base.txt")
        {
            F f = new(); Directory.CreateDirectory(f.Root); await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Merge Test"); await f.Git("config", "user.email", "merge@example.invalid"); f.Write(path, "base\n"); await f.Git("add", "--", path); await f.Git("commit", "-m", "Base"); f.Initial = await f.Text("rev-parse", "HEAD"); return f;
        }
        public async Task<string> Topic(string path = "topic.txt", string bytes = "topic\n")
        { await Git("checkout", "-b", "topic"); Write(path, bytes); await Git("add", "--", path); await Git("commit", "-m", "Topic change"); string head = await Text("rev-parse", "HEAD"); await Git("checkout", "main"); return head; }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
