using Win32Exception = System.ComponentModel.Win32Exception;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformExecutableTests
{
    [Test]
    public async Task AConfiguredProgramUsesItsCapturedPathWithoutChangingTheParentPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "configured tools ' λ & " + Guid.NewGuid().ToString("N"));
        string? parentPath = Environment.GetEnvironmentVariable("PATH");
        Directory.CreateDirectory(directory);
        try
        {
            string root = TestDotnetHost.CreateStartInfo().WorkingDirectory;
            string original = Path.Combine(root, "tests/linux/GitExtensions.Core.Tests/bin/Release/net10.0");
            foreach (string subdirectory in Directory.EnumerateDirectories(original, "*", SearchOption.AllDirectories))
            { Directory.CreateDirectory(Path.Combine(directory, Path.GetRelativePath(original, subdirectory))); }
            foreach (string file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            { File.Copy(file, Path.Combine(directory, Path.GetRelativePath(original, file))); }
            string probe = "configured-git-" + Guid.NewGuid().ToString("N");
            string extension = OperatingSystem.IsWindows() ? ".exe" : "";
            string executable = Path.Combine(directory, probe + extension);
            File.Copy(Path.Combine(original, "GitExtensions.Core.Tests" + extension), executable);
            if (!OperatingSystem.IsWindows())
            { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Dictionary<string, string?> values = new(GitEnvironment.Current.EnvironmentVariables,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { ["PATH"] = directory };
            GitRunner runner = GitExecutionEnvironment.Capture(new() { Executable = probe }, values).CreateRunner();
            GitResult result = await runner.RunAsync(directory, ["--fake-user-askpass", "Username ' λ & literal"]);
            Assert.That(result.Success, Is.True, result.StandardError);
            Assert.That(result.StandardOutput.TrimEnd('\r', '\n'), Is.EqualTo("explicit-user"));
            Assert.That(Environment.GetEnvironmentVariable("PATH"), Is.EqualTo(parentPath));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void AnEmptyCapturedPathDoesNotFallBackToAnotherAmbientGit()
    {
        Dictionary<string, string?> values = new(GitEnvironment.Current.EnvironmentVariables,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { ["PATH"] = "" };
        GitRunner runner = GitExecutionEnvironment.Capture(new() { Executable = "git" }, values).CreateRunner();
        Assert.ThrowsAsync<Win32Exception>(async () => await runner.RunAsync(Path.GetTempPath(), ["--version"]));
    }
}
