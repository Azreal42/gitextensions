using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
public sealed class GitEnvironmentChecklistTests
{
    [Test]
    public async Task ConfiguredCustomCommandsPassWithoutExecutingThemOrChangingAnyGitData()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Config("--global", "user.name", " Global Ω Name ");
        await f.Config("--global", "user.email", " literal@example.invalid ");
        await f.Config("--global", "core.editor", "editor --wait '$LITERAL'");
        string marker = Path.Combine(f.Root, "custom-command-was-executed");
        string command = "printf 'should never run' > " + Quote(marker);
        await f.Config("--local", "merge.guitool", "custom merge Ω");
        await f.Config("--local", "mergetool.custom merge Ω.cmd", command);
        await f.Config("--local", "diff.guitool", "custom diff Ω");
        await f.Config("--local", "difftool.custom diff Ω.cmd", command);
        string before = f.GitData();
        string? inheritedHome = Environment.GetEnvironmentVariable("HOME"), inheritedEditor = Environment.GetEnvironmentVariable("GIT_EDITOR");

        GitEnvironmentChecklistResult scan = await f.Scan(environment: f.WithVersion("git version 2.53.0"));
        Assert.That(scan.Checks.Select(check => check.Id), Is.EquivalentTo(Enum.GetValues<GitEnvironmentCheckId>()));
        Assert.That(scan.AllGood, Is.True, string.Join("\n", scan.Checks));
        Assert.That(scan.EnvironmentInformation, Does.Contain("Custom command configured; it was not executed.")
            .And.Contain("SSH authentication has not been tested.").And.Contain(f.Global));
        Assert.That(File.Exists(marker), Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
        Assert.That(Environment.GetEnvironmentVariable("HOME"), Is.EqualTo(inheritedHome));
        Assert.That(Environment.GetEnvironmentVariable("GIT_EDITOR"), Is.EqualTo(inheritedEditor));
    }

    [TestCase("git version 2.42.9", "Error")]
    [TestCase("git version 2.43.0", "Warning")]
    [TestCase("git version 2.53.0", "Good")]
    public async Task GitVersionBoundariesUseTheVersionActuallyReportedByTheSelectedExecutable(string version, string expected)
    {
        using Fixture f = await Fixture.CreateAsync();
        string before = f.GitData();
        GitEnvironmentChecklistResult scan = await f.Scan(environment: f.WithVersion(version));
        Assert.That(Row(scan, GitEnvironmentCheckId.GitExecutable).Status.ToString(), Is.EqualTo(expected));
        Assert.That(scan.Diagnostic.Version?.ToString(), Is.EqualTo(version["git version ".Length..]));
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task AnExecutableThatIsNotGitCannotProduceAGoodGitRowOrHideIndependentChecks()
    {
        using Fixture f = await Fixture.CreateAsync();
        string before = f.GitData();
        GitEnvironmentChecklistResult scan = await f.Scan(environment: f.WithVersion("a program that is not Git"));
        Assert.That(scan.Diagnostic.Git.Result?.Success, Is.True);
        Assert.That(scan.Diagnostic.Git.Success, Is.False);
        Assert.That(Row(scan, GitEnvironmentCheckId.GitExecutable).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
        Assert.That(Row(scan, GitEnvironmentCheckId.GlobalIdentity).Status, Is.EqualTo(GitEnvironmentCheckStatus.Unavailable));
        Assert.That(Row(scan, GitEnvironmentCheckId.Home).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(scan, GitEnvironmentCheckId.Translation).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(scan.Checks, Has.Count.EqualTo(9));
        Assert.That(scan.AllGood, Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task GlobalIdentityDoesNotBorrowLocalIdentityAndNonemptyLiteralValuesRemainValid()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Config("--local", "user.name", "Local only");
        await f.Config("--local", "user.email", "local@example.invalid");
        GitEnvironmentChecklistResult missing = await f.Scan();
        Assert.That(Row(missing, GitEnvironmentCheckId.GlobalIdentity).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));

        await f.Config("--global", "user.name", " ");
        await f.Config("--global", "user.email", " Ω '$HOME' ");
        string before = f.GitData();
        GitEnvironmentChecklistResult literal = await f.Scan();
        Assert.That(Row(literal, GitEnvironmentCheckId.GlobalIdentity).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [TestCase("git", "git editor --literal '$HOME'")]
    [TestCase("global", "global editor --literal '$HOME'")]
    [TestCase("visual", "visual editor --literal '$HOME'")]
    [TestCase("editor", "last editor --literal '$HOME'")]
    public async Task EditorUsesWindowsGlobalPrecedenceAndNeverTheRunnersForcedNoninteractiveEditor(string winner, string expected)
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Config("--local", "core.editor", "local editor must not win");
        if (winner is "git" or "global") { await f.Config("--global", "core.editor", "global editor --literal '$HOME'"); }
        Dictionary<string, string?> values = new(f.Values);
        if (winner == "git") { values["GIT_EDITOR"] = expected; }
        if (winner != "editor") { values["VISUAL"] = "visual editor --literal '$HOME'"; }
        values["EDITOR"] = "last editor --literal '$HOME'";
        string before = f.GitData();

        GitEnvironmentChecklistResult scan = await f.Scan(environment: f.Environment(values: values));
        Assert.That(Row(scan, GitEnvironmentCheckId.Editor).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(scan, GitEnvironmentCheckId.Editor).Message, Does.Contain(expected).And.Not.Contain("local editor"));
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task NoConfiguredEditorDoesNotMistakeTheRunnersGitEditorTrueForAUserChoice()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitEnvironmentChecklistResult scan = await f.Scan();
        Assert.That(Row(scan, GitEnvironmentCheckId.Editor).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
    }

    [Test]
    public async Task ConditionalGlobalIncludesAndEffectiveLocalToolChoicesUseTheSelectedRepository()
    {
        using Fixture f = await Fixture.CreateAsync();
        string include = Path.Combine(f.Root, "conditional-global.config");
        File.WriteAllText(include, "[user]\nname = Conditional Ω\nemail = conditional@example.invalid\n[core]\neditor = included editor\n");
        await f.Config("--global", "includeIf.gitdir:" + f.Repository.Path + "/.git.path", include);
        await f.Config("--global", "merge.guitool", "wrong-global-unknown-tool");
        await f.Config("--global", "diff.guitool", "wrong-global-unknown-tool");
        await f.Config("--local", "merge.guitool", "local custom");
        await f.Config("--local", "mergetool.local custom.cmd", "literal merge command");
        await f.Config("--local", "diff.guitool", "local custom");
        await f.Config("--local", "difftool.local custom.cmd", "literal diff command");
        string before = f.GitData();

        GitEnvironmentChecklistResult selected = await f.Scan();
        Assert.That(Row(selected, GitEnvironmentCheckId.GlobalIdentity).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(selected, GitEnvironmentCheckId.Editor).Message, Does.Contain("included editor"));
        Assert.That(Row(selected, GitEnvironmentCheckId.MergeTool).Message, Does.Contain("local custom"));
        Assert.That(Row(selected, GitEnvironmentCheckId.DiffTool).Message, Does.Contain("local custom"));
        Assert.That(selected.EnvironmentInformation, Does.Contain(include).And.Contain(".git/config"));

        string cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = f.Repository.Path;
            GitEnvironmentChecklistResult globalOnly = await f.Scan(globalOnly: true);
            Assert.That(Row(globalOnly, GitEnvironmentCheckId.GlobalIdentity).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
            Assert.That(Row(globalOnly, GitEnvironmentCheckId.MergeTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
            Assert.That(globalOnly.EnvironmentInformation, Does.Contain("Global configuration only").And.Not.Contain(".git/config"));
        }
        finally { Environment.CurrentDirectory = cwd; }
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task NativeToolHelpRecognizesConfiguredFallbackToolsWithoutExecutingThemOrPromisingAvailability()
    {
        using Fixture f = await Fixture.CreateAsync();
        string tools = Path.Combine(f.Root, "tools");
        string marker = Path.Combine(f.Root, "meld-was-executed");
        string meld = Executable(tools, "meld", ": > " + Quote(marker) + "\n");
        // Keep native Git's shell helpers, while ensuring a host-installed GUI cannot satisfy the second probe.
        foreach (string helper in new[] { "git", "sh", "ssh", "ls", "sort", "sed", "grep", "basename", "dirname", "expr", "tr", "cut", "cat", "uname", "which" })
        { File.CreateSymbolicLink(Path.Combine(tools, helper), "/usr/bin/" + helper); }
        await f.Config("--local", "merge.tool", "meld");
        await f.Config("--local", "diff.tool", "meld");
        string configuredPath = Path.Combine(f.Root, "a configured path that does not exist");
        await f.Config("--local", "mergetool.meld.path", configuredPath);
        await f.Config("--local", "difftool.meld.path", configuredPath);
        Dictionary<string, string?> values = new(f.Values) { ["PATH"] = tools };
        string before = f.GitData();

        GitEnvironmentChecklistResult available = await f.Scan(environment: f.Environment(values: values));
        Assert.That(Row(available, GitEnvironmentCheckId.MergeTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(available, GitEnvironmentCheckId.DiffTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(available.EnvironmentInformation, Does.Contain("Git lists the native default as available."));
        Assert.That(available.EnvironmentInformation, Does.Contain("Configured path (not executed): " + configuredPath));
        Assert.That(File.Exists(marker), Is.False);

        if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(meld, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        GitEnvironmentChecklistResult unavailable = await f.Scan(environment: f.Environment(values: values));
        Assert.That(Row(unavailable, GitEnvironmentCheckId.MergeTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(unavailable, GitEnvironmentCheckId.DiffTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(unavailable.EnvironmentInformation, Does.Contain("Git lists the native default as not currently available."));
        Assert.That(File.Exists(marker), Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task FailureOfOneNativeToolListingKeepsOtherObservedRowsAndNeverClaimsSuccessForThatRow()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Config("--global", "user.name", "Global name");
        await f.Config("--global", "user.email", "global@example.invalid");
        await f.Config("--global", "core.editor", "configured editor");
        await f.Config("--local", "merge.tool", "custom merge");
        await f.Config("--local", "mergetool.custom merge.cmd", "literal configured command");
        await f.Config("--local", "diff.tool", "meld");
        string git = Executable(f.Root, "git with failing difftool help", "if test \"$1\" = difftool; then printf 'tool help failed\\n' >&2; exit 42; fi\nexec /usr/bin/git \"$@\"\n");
        string before = f.GitData();

        GitEnvironmentChecklistResult scan = await f.Scan(environment: f.Environment(git));
        Assert.That(Row(scan, GitEnvironmentCheckId.DiffTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Unavailable));
        Assert.That(Row(scan, GitEnvironmentCheckId.DiffTool).Message, Does.Contain("tool help failed"));
        foreach (GitEnvironmentCheckId id in new[] { GitEnvironmentCheckId.GlobalIdentity, GitEnvironmentCheckId.Editor, GitEnvironmentCheckId.MergeTool, GitEnvironmentCheckId.Home })
        { Assert.That(Row(scan, id).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good)); }
        Assert.That(scan.AllGood, Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task APathForAnUnknownToolCannotReplaceItsRequiredCustomCommand()
    {
        using Fixture f = await Fixture.CreateAsync();
        string marker = Path.Combine(f.Root, "unknown-was-executed");
        string executable = Executable(f.Root, "unknown-tool", ": > " + Quote(marker) + "\n");
        await f.Config("--local", "merge.guitool", "unknown-native-tool-Ω");
        await f.Config("--local", "mergetool.unknown-native-tool-Ω.path", executable);
        await f.Config("--local", "diff.guitool", "unknown-native-tool-Ω");
        await f.Config("--local", "difftool.unknown-native-tool-Ω.path", executable);
        string before = f.GitData();

        GitEnvironmentChecklistResult scan = await f.Scan();
        Assert.That(Row(scan, GitEnvironmentCheckId.MergeTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
        Assert.That(Row(scan, GitEnvironmentCheckId.DiffTool).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
        Assert.That(File.Exists(marker), Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task InvalidNativeConfigurationLeavesIndependentChecksVisibleAndTheOriginalDataUntouched()
    {
        using Fixture f = await Fixture.CreateAsync();
        File.WriteAllText(f.Global, "[broken global configuration\n");
        string before = f.GitData();

        GitEnvironmentChecklistResult scan = await f.Scan();
        foreach (GitEnvironmentCheckId id in new[] { GitEnvironmentCheckId.GlobalIdentity, GitEnvironmentCheckId.Editor, GitEnvironmentCheckId.MergeTool, GitEnvironmentCheckId.DiffTool })
        { Assert.That(Row(scan, id).Status, Is.EqualTo(GitEnvironmentCheckStatus.Unavailable)); }
        Assert.That(Row(scan, GitEnvironmentCheckId.Home).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(scan, GitEnvironmentCheckId.Shell).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(Row(scan, GitEnvironmentCheckId.Translation).Status, Is.EqualTo(GitEnvironmentCheckStatus.Good));
        Assert.That(scan.EnvironmentInformation, Does.Contain("bad config line"));
        Assert.That(scan.AllGood, Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task LocalSshFailureAndUnavailableLanguageOrHomeNeverBecomeAllGood()
    {
        using Fixture f = await Fixture.CreateAsync();
        string tools = Path.Combine(f.Root, "local-probes");
        string marker = Path.Combine(f.Root, "ssh-argv");
        Executable(tools, "ssh", "printf '%s' \"$*\" > " + Quote(marker) + "\nprintf 'local ssh failure\\n' >&2\nexit 7\n");
        Dictionary<string, string?> values = new(f.Values) { ["PATH"] = tools + ":/usr/bin:/bin", ["HOME"] = Path.Combine(f.Root, "missing-home") };
        string before = f.GitData();

        GitEnvironmentChecklistResult scan = await f.Scan(environment: f.Environment(values: values), language: "Unavailable catalogue Ω");
        Assert.That(File.ReadAllText(marker), Is.EqualTo("-V"), "The SSH probe must remain local and must not contain a host or authentication request.");
        Assert.That(Row(scan, GitEnvironmentCheckId.Ssh).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
        Assert.That(Row(scan, GitEnvironmentCheckId.Ssh).Message, Does.Contain("local ssh failure").And.Contain("Authentication has not been tested"));
        Assert.That(Row(scan, GitEnvironmentCheckId.Home).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
        Assert.That(Row(scan, GitEnvironmentCheckId.Translation).Status, Is.EqualTo(GitEnvironmentCheckStatus.Error));
        Assert.That(scan.AllGood, Is.False);
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    [Test]
    public async Task CancellationOfAnOwnedNativeProbePropagatesAndNeverPublishesAPartialChecklist()
    {
        using Fixture f = await Fixture.CreateAsync();
        string ready = Path.Combine(f.Root, "version-ready");
        string git = Executable(f.Root, "gated git", "if test \"$1\" = --version; then\n: > " + Quote(ready)
            + "\nwhile :; do /bin/sleep 0.01; done\nfi\nexec /usr/bin/git \"$@\"\n");
        string before = f.GitData();
        using CancellationTokenSource cancellation = new();
        Task<GitEnvironmentChecklistResult> running = f.Scan(environment: f.Environment(git), token: cancellation.Token);
        try
        {
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(ready) && DateTime.UtcNow < end) { await Task.Delay(10); }
            Assert.That(File.Exists(ready), Is.True);
            cancellation.Cancel();
            Assert.That(async () => { _ = await running.WaitAsync(TimeSpan.FromSeconds(10)); }, Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        Assert.That(f.GitData(), Is.EqualTo(before));
    }

    private static GitEnvironmentCheck Row(GitEnvironmentChecklistResult result, GitEnvironmentCheckId id) => result.Checks.Single(check => check.Id == id);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Executable(string directory, string name, string body)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body, new UTF8Encoding(false));
        if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return path;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-checklist-" + Guid.NewGuid().ToString("N"));
        internal string Global => Path.Combine(Root, "global.config");
        internal GitRepository Repository { get; }
        internal Dictionary<string, string?> Values { get; }
        private readonly GitRunner _runner;
        private Fixture()
        {
            string home = Path.Combine(Root, "home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Global, "");
            Values = new(StringComparer.Ordinal)
            {
                ["PATH"] = "/usr/bin:/bin", ["HOME"] = home, ["XDG_CONFIG_HOME"] = Path.Combine(Root, "xdg"),
                ["GIT_CONFIG_GLOBAL"] = Global, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "0"
            };
            _runner = Environment().CreateRunner();
            Repository = new(Path.Combine(Root, "repository"), _runner);
            Directory.CreateDirectory(Repository.Path);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                await f.Git("init", "--initial-branch=main");
                File.WriteAllText(Path.Combine(f.Repository.Path, "staged.txt"), "index witness Ω\n");
                await f.Git("add", "--", "staged.txt");
                File.WriteAllText(Path.Combine(f.Repository.Path, "untracked.txt"), "worktree witness Ω\r\n");
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal GitExecutionEnvironment Environment(string? executable = null, IReadOnlyDictionary<string, string?>? values = null)
            => GitExecutionEnvironment.Capture(new() { Executable = executable ?? "/usr/bin/git" }, values ?? Values);
        internal GitExecutionEnvironment WithVersion(string version)
        {
            string executable = Executable(Root, "git version Ω", "if test \"$1\" = --version; then printf '%s\\n' " + Quote(version)
                + "; exit 0; fi\nexec /usr/bin/git \"$@\"\n");
            return Environment(executable);
        }
        internal Task<GitEnvironmentChecklistResult> Scan(GitExecutionEnvironment? environment = null, string? language = "English", bool globalOnly = false, CancellationToken token = default)
            => GitEnvironmentChecklist.ScanAsync(globalOnly ? null : Repository, environment ?? Environment(),
                TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en")), language, token);
        internal Task Config(string scope, string key, string value) => Git("config", scope, "--", key, value);
        private async Task Git(params string[] arguments)
        {
            GitResult result = await _runner.RunAsync(Repository.Path, arguments);
            Assert.That(result.Success, Is.True, result.StandardError);
        }
        internal string GitData()
        {
            IEnumerable<string> files = Directory.GetFiles(Repository.Path, "*", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Root, "*.config"));
            return JsonSerializer.Serialize(files.Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(Root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
