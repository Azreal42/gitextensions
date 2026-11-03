using NUnit.Framework;
using Host = GitExtensions.Askpass.Host.Program;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform")]
public sealed class AskpassHostTests
{
    [Test]
    public void AnOmittedPromptDelegatesToTheExistingDefaultPrompt()
    {
        System.Diagnostics.ProcessStartInfo start = Host.CreateStartInfo([])!;
        Assert.That(start, Is.Not.Null);
        Assert.That(start.FileName, Is.EqualTo(Path.Combine(AppContext.BaseDirectory, "GitExtensions.exe")));
        Assert.That(start.ArgumentList, Is.EqualTo(new[] { "--askpass" }));
        Assert.That(start.UseShellExecute, Is.False);
        Assert.That(start.RedirectStandardOutput && start.RedirectStandardError && start.CreateNoWindow, Is.True);
    }

    [TestCase("")] [TestCase("literal λ '$() & \"quotes\"\nsecond line")]
    public void AProvidedPromptRemainsOneLiteralArgument(string prompt)
    {
        System.Diagnostics.ProcessStartInfo start = Host.CreateStartInfo([prompt])!;
        Assert.That(start.ArgumentList, Is.EqualTo(new[] { "--askpass", prompt }));
        Assert.That(start.Arguments, Is.Empty);
        Assert.That(start.UseShellExecute, Is.False);
    }

    [TestCase(2)] [TestCase(3)]
    public void ExcessArgumentsNeverStartTheDesktop(int count)
        => Assert.That(Host.CreateStartInfo(Enumerable.Repeat("invalid", count).ToArray()), Is.Null);
}
