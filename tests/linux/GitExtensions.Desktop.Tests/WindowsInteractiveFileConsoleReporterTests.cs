using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("CrossPlatform")]
public sealed class WindowsInteractiveFileConsoleReporterTests
{
    [Test]
    public async Task ConsoleClosing_should_report_cancellation_before_cleanup_and_only_once()
    {
        using StringWriter writer = new();
        WindowsInteractiveFileConsoleReporter reporter = new(writer);
        reporter.ConsoleClosing();
        Assert.That(writer.ToString(), Is.Not.Empty);
        reporter.Report(new(1, false, "Console cleanup failed."));
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(writer.ToString())));
        InteractiveFileConsoleResult result = await InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleResult>(reader);
        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.Error, Is.Null);
        Assert.That(await reader.ReadLineAsync(), Is.Null);
    }

    [TestCase(0)]
    [TestCase(73)]
    public async Task ConsoleClosing_should_preserve_a_drained_Git_result(int exitCode)
    {
        using StringWriter writer = new();
        WindowsInteractiveFileConsoleReporter reporter = new(writer);
        reporter.GitCompleted(new GitResult(exitCode, "", ""));
        await Task.WhenAll(Task.Run(reporter.ConsoleClosing), Task.Run(reporter.ConsoleClosing));
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(writer.ToString())));
        InteractiveFileConsoleResult result = await InteractiveFileConsoleProtocol.ReadAsync<InteractiveFileConsoleResult>(reader);
        Assert.That(result.Cancelled, Is.False);
        Assert.That(result.ExitCode, Is.EqualTo(exitCode));
        Assert.That(await reader.ReadLineAsync(), Is.Null);
    }
}
