using System.Text;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformRepositoryTests
{
    [Test]
    public async Task CommitBranchCheckoutAndDiffUseTheActualGitWithLiteralUnicodePaths()
    {
        using Fixture f = await Fixture.CreateAsync();
        const string name = "folder/space ' quote & λ.txt";
        string file = Path.Combine(f.Repository.Path, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "baseline λ\n", new UTF8Encoding(false));
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("baseline ' λ & literal");
        string before = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.CreateBranchAsync("platform-work");
        await File.WriteAllTextAsync(file, "changed λ\n", new UTF8Encoding(false));
        await f.Repository.StageAsync([name]); await f.Repository.CommitAsync("second commit");
        string after = await f.Repository.ResolveRevisionAsync("HEAD");
        Assert.That(after, Is.Not.EqualTo(before));
        Assert.That(await f.Repository.GetCommitDiffAsync(after, name), Does.Contain("+changed λ"));
        await f.Repository.CheckoutAsync("main"); Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("baseline λ\n"));
        await f.Repository.CheckoutAsync("platform-work"); Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("changed λ\n"));
    }

    [Test]
    public async Task StashRestoresTrackedAndUntrackedBytesWithoutAPlatformShell()
    {
        using Fixture f = await Fixture.CreateAsync();
        string tracked = Path.Combine(f.Repository.Path, "tracked λ.txt"), untracked = Path.Combine(f.Repository.Path, "untracked & note.txt");
        await File.WriteAllTextAsync(tracked, "base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("initial");
        await File.WriteAllTextAsync(tracked, "working\n"); await File.WriteAllTextAsync(untracked, "new file λ\n");
        await f.Repository.StashAsync("platform stash", includeUntracked: true);
        Assert.That(await File.ReadAllTextAsync(tracked), Is.EqualTo("base\n")); Assert.That(File.Exists(untracked), Is.False);
        GitStash stash = (await f.Repository.GetStashesAsync()).Single();
        await f.Repository.ApplyStashAsync(stash.Reference, pop: true);
        Assert.That(await File.ReadAllTextAsync(tracked), Is.EqualTo("working\n"));
        Assert.That(await File.ReadAllTextAsync(untracked), Is.EqualTo("new file λ\n"));
        Assert.That(await f.Repository.GetStashesAsync(), Is.Empty);
    }

    [Test]
    public async Task RulesFileSavePreservesTheBaselineAndRejectsAnExternalEdit()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitRulesFileSnapshot missing = await f.Repository.GetRulesFileSnapshotAsync(GitRulesFileKind.Ignore);
        GitRulesFileSnapshot saved = await f.Repository.SaveRulesFileAsync(missing, "bin/\n");
        Assert.That(await File.ReadAllTextAsync(saved.Path), Is.EqualTo("bin/\n"));
        await File.WriteAllTextAsync(saved.Path, "external change\n");
        Assert.ThrowsAsync<IOException>(async () => await f.Repository.SaveRulesFileAsync(saved, "stale change\n"));
        Assert.That(await File.ReadAllTextAsync(saved.Path), Is.EqualTo("external change\n"));
    }

    [Test]
    public async Task TheRealRebaseEditorRunsFromAnInstallationWithSpacesAndShellMetacharacters()
    {
        using Fixture f = await Fixture.CreateAsync();
        string file = Path.Combine(f.Repository.Path, "file.txt");
        await File.WriteAllTextAsync(file, "base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("base");
        string baseline = await f.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(file, "second\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("before reword");
        string checkout = TestDotnetHost.CreateStartInfo().WorkingDirectory;
        string original = Path.Combine(checkout, "src/linux/GitExtensions.Desktop/bin/Release/net10.0");
        string installation = Path.Combine(f.Repository.Path, "..", "installation 'quoted & λ'"); Directory.CreateDirectory(installation);
        foreach (string directory in Directory.EnumerateDirectories(original, "*", SearchOption.AllDirectories))
        { Directory.CreateDirectory(Path.Combine(installation, Path.GetRelativePath(original, directory))); }
        foreach (string source in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
        { File.Copy(source, Path.Combine(installation, Path.GetRelativePath(original, source))); }
        string editor = Path.Combine(installation, OperatingSystem.IsWindows() ? "GitExtensions.exe" : "GitExtensions");
        Assert.That(File.Exists(editor), Is.True, "Run the actual current build, never an installed application or a replacement editor.");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(editor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        GitRebasePlan plan = await f.Repository.GetRebasePlanAsync(baseline);
        GitResult result = await f.Repository.InteractiveRebaseAsync(plan with
        { Steps = plan.Steps.Select(step => step with { Action = GitRebaseAction.Reword, Message = "edited ' λ & literal" }).ToArray() }, editor);
        Assert.That(result.Success, Is.True, result.StandardError);
        Assert.That((await f.Repository.GetLogAsync(1)).Single().Subject, Is.EqualTo("edited ' λ & literal"));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("second\n"));
        Assert.That((await f.Repository.GetRebaseStateAsync()).IsActive, Is.False);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions platform 'λ & " + Guid.NewGuid().ToString("N"));
        internal GitRepository Repository { get; private set; } = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(); Directory.CreateDirectory(f._root);
            try
            {
                string emptyConfig = Path.Combine(f._root, "empty.gitconfig"); await File.WriteAllTextAsync(emptyConfig, "");
                Dictionary<string, string?> inherited = new(GitEnvironment.Current.EnvironmentVariables,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    { ["GIT_CONFIG_GLOBAL"] = emptyConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" };
                GitRunner runner = GitExecutionEnvironment.Capture(new(), inherited).CreateRunner();
                GitResult initialized = await runner.RunAsync(f._root, ["init", "--initial-branch=main", "repository"]);
                Assert.That(initialized.Success, Is.True, initialized.StandardError);
                f.Repository = await GitRepository.OpenAsync(Path.Combine(f._root, "repository"), runner: runner);
                foreach ((string name, string value) in new[] { ("user.name", "Platform Test"), ("user.email", "platform@example.invalid"),
                    ("commit.gpgsign", "false"), ("core.autocrlf", "false"), ("core.quotepath", "false"), ("maintenance.auto", "false"), ("gc.auto", "0") })
                { await f.Repository.SetConfigAsync(name, value); }
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (!Directory.Exists(_root)) { return; }
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(_root, recursive: true);
        }
    }
}
