using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("PosixConsoleLaunch"), NonParallelizable, Platform("Linux,MacOsX")]
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class MacInteractiveConsoleTerminalTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ToolsCommandInheritsOnlyTheTerminalTypeWhenItWasNotExplicitlyCaptured(bool captured)
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-term-type-");
        string command = Path.Combine(directory.DirectoryPath, "terminal.command"), record = Path.Combine(directory.DirectoryPath, "term");
        string injection = Path.Combine(directory.DirectoryPath, "injected");
        string hostTerm = "terminal ' λ; $(touch " + injection + ")";
        Dictionary<string, string?> values = new() { ["PATH"] = "/usr/bin:/bin" }; if (captured) { values["TERM"] = "captured terminal"; }
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new(), values);
        MacInteractiveConsoleTerminal.WriteCommand(command, "/bin/sh", ["-c", "printf '%s\\0%s' \"$TERM\" \"${UNRELATED_BROKER_SECRET-unset}\" > \"$1\"", "shell", record], directory.DirectoryPath, environment, preserveTerminalType: true);
        ProcessStartInfo start = new("/bin/sh") { UseShellExecute = false }; start.ArgumentList.Add(command);
        start.Environment["TERM"] = hostTerm; start.Environment["UNRELATED_BROKER_SECRET"] = "must not leak";
        using Process child = Process.Start(start)!; await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(child.ExitCode, Is.Zero); Assert.That(await File.ReadAllTextAsync(record), Is.EqualTo((captured ? "captured terminal" : hostTerm) + "\0unset"));
        Assert.That(File.Exists(injection), Is.False);
    }

    [Test]
    public async Task PrivateCommandPreservesLiteralArgumentsCapturedEnvironmentWorkingDirectoryAndExit()
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-command-");
        string repository = Path.Combine(directory.DirectoryPath, "repository ' λ"); Directory.CreateDirectory(repository);
        string record = Path.Combine(directory.DirectoryPath, "argv"), probe = Path.Combine(directory.DirectoryPath, "probe.sh");
        string command = Path.Combine(directory.DirectoryPath, "interactive.command"), injection = Path.Combine(directory.DirectoryPath, "injected");
        await File.WriteAllTextAsync(probe, "#!/bin/sh\nrecord=$1\nshift\nprintf '%s\\0' \"$@\" > \"$record\"\npwd -P > \"$record.cwd\"\nprintf '%s' \"$GE_CAPTURED_VALUE\" > \"$record.env\"\nprintf '%s' \"${GE_UNCAPTURED_VALUE-unset}\" > \"$record.unset\"\nexit 73\n");
        string hostile = "literal ' λ\n$(touch " + injection + ")";
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?>
        { ["PATH"] = "/usr/bin:/bin", ["GE_CAPTURED_VALUE"] = hostile, ["GE_UNCAPTURED_VALUE"] = null });
        string[] literal = ["", hostile, "tab\tline\nbreak", "quote\"", "trailing\\"];
        MacInteractiveConsoleTerminal.WriteCommand(command, "/bin/sh", [probe, record, .. literal], repository, environment);
        Assert.That(File.GetUnixFileMode(command), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        ProcessStartInfo start = new("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add(command); start.Environment["GE_UNCAPTURED_VALUE"] = "ambient must not leak";
        using Process child = Process.Start(start)!; await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(child.ExitCode, Is.EqualTo(73));
        string[] arguments = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(record)).Split('\0');
        Assert.That(arguments[^1], Is.Empty); Assert.That(arguments[..^1], Is.EqualTo(literal));
        Assert.That(await File.ReadAllTextAsync(record + ".env"), Is.EqualTo(hostile));
        Assert.That(await File.ReadAllTextAsync(record + ".unset"), Is.EqualTo("unset"));
        ProcessStartInfo pwd = new("/bin/pwd") { UseShellExecute = false, WorkingDirectory = repository, RedirectStandardOutput = true };
        pwd.ArgumentList.Add("-P"); using Process physical = Process.Start(pwd)!;
        string expected = await physical.StandardOutput.ReadToEndAsync(); await physical.WaitForExitAsync();
        Assert.That(physical.ExitCode, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(record + ".cwd"), Is.EqualTo(expected));
        Assert.That(File.Exists(injection), Is.False);
    }

    [Test]
    public void CreatingACommandNeverOverwritesAnExistingFileOrFollowsALink()
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-command-");
        string target = Path.Combine(directory.DirectoryPath, "existing"), link = Path.Combine(directory.DirectoryPath, "link.command");
        File.WriteAllText(target, "original"); File.CreateSymbolicLink(link, target);
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?>());
        foreach (string path in new[] { target, link })
        { Assert.Throws<IOException>(() => MacInteractiveConsoleTerminal.WriteCommand(path, "/bin/sh", [], directory.DirectoryPath, environment)); }
        Assert.That(File.ReadAllText(target), Is.EqualTo("original"));
    }

    [Test]
    public void InvalidLaunchValuesAreRefusedBeforeTheCommandFileExists()
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-command-");
        string command = Path.Combine(directory.DirectoryPath, "interactive.command");
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?>());
        Assert.Throws<ArgumentException>(() => MacInteractiveConsoleTerminal.WriteCommand(command, "/bin/sh", ["bad\0argument"], directory.DirectoryPath, environment));
        Assert.That(File.Exists(command), Is.False);
    }
}
