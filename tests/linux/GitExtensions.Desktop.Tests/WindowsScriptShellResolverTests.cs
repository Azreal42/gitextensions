using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("CrossPlatform"), NonParallelizable]
public sealed class WindowsScriptShellResolverTests
{
    [TestCase("bash")]
    [TestCase("BASH.EXE")]
    [TestCase("sh")]
    [TestCase("SH.EXE")]
    public void ScriptsUseTheExactRequestedShellFromCapturedGitInsteadOfAnUnrelatedPathEntry(string command)
    {
        using Fixture fixture = new();
        string expected = fixture.File(Path.Combine(fixture.GitRoot, "usr", "bin", command.StartsWith("bash", StringComparison.OrdinalIgnoreCase) ? "bash.exe" : "sh.exe"));
        fixture.File(Path.Combine(fixture.Unrelated, "bash.exe")); fixture.File(Path.Combine(fixture.Unrelated, "sh.exe"));
        string actual = WindowsGitShellResolver.ResolveScriptCommand(command, fixture.Environment());
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ScriptAliasesHonorExplicitToolsAndAnUnavailableBashCannotBecomeShOrWsl()
    {
        using Fixture fixture = new(); string tools = fixture.Directory("explicit tools");
        string expected = fixture.File(Path.Combine(tools, "bash.exe")); fixture.File(Path.Combine(fixture.GitRoot, "usr", "bin", "bash.exe"));
        Assert.That(WindowsGitShellResolver.ResolveScriptCommand("bash", fixture.Environment(tools)), Is.EqualTo(expected));
        File.Delete(expected); File.Delete(Path.Combine(fixture.GitRoot, "usr", "bin", "bash.exe"));
        fixture.File(Path.Combine(fixture.GitRoot, "usr", "bin", "sh.exe")); fixture.File(Path.Combine(fixture.Unrelated, "bash.exe"));
        Assert.Throws<FileNotFoundException>(() => WindowsGitShellResolver.ResolveScriptCommand("bash", fixture.Environment(tools)));
        Assert.That(WindowsGitShellResolver.ResolveScriptCommand("sh", fixture.Environment(tools)), Is.EqualTo(Path.Combine(fixture.GitRoot, "usr", "bin", "sh.exe")));
    }

    [TestCase("./bash.exe")]
    [TestCase("custom/bash.exe")]
    [TestCase("C:\\Windows\\System32\\bash.exe")]
    [TestCase("git-bash.exe")]
    [TestCase("custom-program")]
    public void ExplicitPathsAndOtherCommandsRemainLiteralEvenWithoutAnInstalledShell(string command)
    {
        using Fixture fixture = new(); Assert.That(WindowsGitShellResolver.ResolveScriptCommand(command, fixture.Environment()), Is.EqualTo(command));
    }

    [Test]
    public void QuotedCapturedGitPathAndAnUsrBinSelectionShareTheInteractiveResolver()
    {
        using Fixture fixture = new(); string bash = fixture.File(Path.Combine(fixture.GitRoot, "usr", "bin", "bash.exe"));
        string launcher = fixture.File(Path.Combine(fixture.GitRoot, "git-bash.exe"));
        GitExecutionEnvironment bare = GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?> { ["PATH"] = "\"" + Path.Combine(fixture.GitRoot, "cmd") + "\"" });
        Assert.That(WindowsGitShellResolver.ResolveScriptCommand("bash", bare), Is.EqualTo(bash));
        string usrGit = fixture.File(Path.Combine(fixture.GitRoot, "usr", "bin", "git.exe"));
        GitExecutionEnvironment selected = GitExecutionEnvironment.Capture(new() { Executable = usrGit }, new Dictionary<string, string?>());
        Assert.That(WindowsGitShellResolver.FindInteractiveShell(selected), Is.EqualTo(launcher));
        Assert.That(WindowsGitShell.CreateStartInfo(fixture.Root, selected).FileName, Is.EqualTo(launcher));
    }

    [Test]
    public void InteractiveFallbackUsesCapturedPathWithoutAdoptingALauncherFromTheCurrentRepository()
    {
        using Fixture fixture = new(); fixture.File(Path.Combine(fixture.Root, "git-bash.exe"));
        string expected = fixture.File(Path.Combine(fixture.Unrelated, "git-bash.exe"));
        string previous = System.Environment.CurrentDirectory;
        try
        {
            System.Environment.CurrentDirectory = fixture.Root;
            Assert.That(WindowsGitShellResolver.FindInteractiveShell(fixture.Environment()), Is.EqualTo(expected));
            File.Delete(expected);
            Assert.That(WindowsGitShellResolver.FindInteractiveShell(fixture.Environment()), Is.Null);
        }
        finally { System.Environment.CurrentDirectory = previous; }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "script-shell-resolver-" + Guid.NewGuid().ToString("N"));
        internal string GitRoot { get; }
        internal string Unrelated { get; }
        internal Fixture() { GitRoot = Directory("Git ' λ"); Unrelated = Directory("unrelated WSL"); File(Path.Combine(GitRoot, "cmd", "git.exe")); }
        internal GitExecutionEnvironment Environment(string? tools = null) => GitExecutionEnvironment.Capture(new() { Executable = Path.Combine(GitRoot, "cmd", "git.exe"), ToolsDirectory = tools },
            new Dictionary<string, string?> { ["PATH"] = Unrelated, ["CAPTURED"] = "literal value" });
        internal string Directory(string name) { string path = Path.Combine(Root, name); System.IO.Directory.CreateDirectory(path); return path; }
        internal string File(string path) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, "lookup fixture only"); return path; }
        public void Dispose() => System.IO.Directory.Delete(Root, true);
    }
}
