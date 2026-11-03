using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform")]
public sealed class DesktopResourceLauncherTests
{
    [TestCase((int)DesktopPlatform.Linux)]
    [TestCase((int)DesktopPlatform.Windows)]
    [TestCase((int)DesktopPlatform.MacOS)]
    public void ResourceTargetsRemainLiteralWithEachNativeOpener(int platformValue)
    {
        DesktopPlatform platform = (DesktopPlatform)platformValue;
        foreach (string target in new[] { "https://example.invalid/path?a=1&b='λ';$(literal)", "/tmp/a file 'λ' & $literal.txt" })
        {
            var start = DesktopResourceLauncher.CreateStartInfo(target, platform, "working directory");
            Assert.That(start.WorkingDirectory, Is.EqualTo("working directory"));
            Assert.That(start.Arguments, Is.Empty);
            if (platform == DesktopPlatform.Windows)
            {
                Assert.That(start.UseShellExecute, Is.True); Assert.That(start.FileName, Is.EqualTo(target));
                Assert.That(start.Verb, Is.EqualTo("open")); Assert.That(start.ArgumentList, Is.Empty);
            }
            else
            {
                Assert.That(start.UseShellExecute, Is.False);
                Assert.That(start.FileName, Is.EqualTo(platform == DesktopPlatform.MacOS ? "/usr/bin/open" : "xdg-open"));
                Assert.That(start.ArgumentList, Is.EqualTo(platform == DesktopPlatform.MacOS ? new[] { "--", target } : new[] { target }));
            }
        }
    }

    [TestCase((int)DesktopPlatform.Linux)]
    [TestCase((int)DesktopPlatform.Windows)]
    [TestCase((int)DesktopPlatform.MacOS)]
    public void InvalidTargetsAreRejectedBeforeLaunching(int platformValue)
    {
        DesktopPlatform platform = (DesktopPlatform)platformValue;
        Assert.Throws<ArgumentException>(() => DesktopResourceLauncher.CreateStartInfo("invalid\0resource", platform));
        Assert.Throws<ArgumentException>(() => DesktopResourceLauncher.CreateStartInfo("https://example.invalid", platform, "bad\0directory"));
    }

    [Test]
    public void TheCapturedEnvironmentUsesTheNativeVariableNameRules()
    {
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { ToolsDirectory = "additional-tools" },
            new Dictionary<string, string?> { ["Path"] = "native-path", ["git_ssh"] = "native-ssh" });
        Assert.That(environment.GetVariable("PATH"), Is.EqualTo(OperatingSystem.IsWindows()
            ? "native-path" + Path.PathSeparator + "additional-tools" : "additional-tools"));
        Assert.That(environment.GetVariable("GIT_SSH"), Is.EqualTo(OperatingSystem.IsWindows() ? "native-ssh" : null));
    }
}
