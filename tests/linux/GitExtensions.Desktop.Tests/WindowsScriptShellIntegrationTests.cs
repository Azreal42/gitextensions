using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("WindowsScriptShellIntegration"), NonParallelizable, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class WindowsScriptShellIntegrationTests
{
    [TestCase(0)]
    [TestCase(73)]
    public async Task ActualGitBashUsesLiteralArgumentsCapturedEnvironmentAndDirectoryWithoutUsrBinOnPath(int exitCode)
    {
        string root = Directory.CreateTempSubdirectory("git-shell ' λ ").FullName;
        try
        {
            string git = await FindGitAsync();
            Dictionary<string, string?> variables = new(GitExecutionEnvironment.Capture(new()).EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
            variables.Remove("BASH_ENV"); variables.Remove("ENV");
            variables["PATH"] = Path.GetDirectoryName(git) + Path.PathSeparator + Environment.GetFolderPath(Environment.SpecialFolder.System);
            variables["GE_CAPTURED_SHELL_VALUE"] = "captured ' $() λ";
            GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = git }, variables);
            string executable = WindowsGitShellResolver.ResolveScriptCommand("bash", environment);
            Assert.That(variables["PATH"]!.Split(Path.PathSeparator), Does.Not.Contain(Path.GetDirectoryName(executable)));
            string[] literal = ["", "one space", "quote\" and ' literal", "line\nbreak", "trailing\\", "$HOME $(literal) λ"];
            string program = "[[ -n \"$BASH_VERSION\" ]] || exit 99; printf '%s\\0' \"$@\"; printf '%s' \"$GE_CAPTURED_SHELL_VALUE\" >&2; printf ran > cwd-proof; exit "
                + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            ScriptProcessResult result = await new ScriptProcessRunner().RunAsync(executable, root, ["-c", program, "shell", .. literal],
                timeout.Token, executionEnvironment: environment);
            Assert.That(result.ExitCode, Is.EqualTo(exitCode));
            Assert.That(result.StandardOutputBytes, Is.EqualTo(Encoding.UTF8.GetBytes(string.Join('\0', literal) + "\0")));
            Assert.That(result.StandardErrorBytes, Is.EqualTo(Encoding.UTF8.GetBytes("captured ' $() λ")));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "cwd-proof")), Is.EqualTo("ran"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<string> FindGitAsync()
    {
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("git.exe"); using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(process.ExitCode, Is.Zero, error.Result);
        string git = output.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).First();
        Assert.That(File.Exists(git), Is.True); return git;
    }
}
