using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("CrossPlatform")]
public sealed class UserTerminalEnvironmentTests
{
    [TestCase("LANG")]
    [TestCase("LC_ALL")]
    [TestCase("LC_COLLATE")]
    [TestCase("LC_CTYPE")]
    [TestCase("LC_MESSAGES")]
    [TestCase("LC_MONETARY")]
    [TestCase("LC_NUMERIC")]
    [TestCase("LC_TIME")]
    public void AnAbsentUserLocaleCanUseOnlyTheTerminalsEncodingContext(string localeVariable)
    {
        GitExecutionEnvironment captured = Capture();
        Dictionary<string, string?> terminal = new()
        {
            ["TERM"] = "native terminal", [localeVariable] = "C.UTF-8", ["PATH"] = "/foreign",
            ["HOME"] = "/foreign home", ["GIT_PAGER"] = "foreign pager", ["BROKER_SECRET"] = "not captured"
        };
        GitExecutionEnvironment actual = InteractiveFileConsoleEnvironment.WithUserTerminalContext(captured, key => terminal.GetValueOrDefault(key));
        Dictionary<string, string?> expected = new(captured.EnvironmentVariables, StringComparer.Ordinal)
        { ["TERM"] = "native terminal", [localeVariable] = "C.UTF-8" };
        Assert.That(actual.EnvironmentVariables, Is.EquivalentTo(expected));
        Assert.That(actual.Options, Is.EqualTo(captured.Options));
        Assert.That(captured.EnvironmentVariables.ContainsKey(localeVariable), Is.False);
    }

    [TestCase("LANG", "C")]
    [TestCase("LANG", "")]
    [TestCase("LC_ALL", "C")]
    [TestCase("LC_CTYPE", "POSIX")]
    [TestCase("LC_FUTURE", "future")]
    [TestCase("LANGUAGE", "fr")]
    public void AnExplicitCapturedLocaleCannotBeOverriddenByAnotherTerminalCategory(string name, string value)
    {
        Dictionary<string, string?> variables = new(Capture().EnvironmentVariables, StringComparer.Ordinal) { [name] = value };
        GitExecutionEnvironment captured = GitExecutionEnvironment.Capture(new(), variables);
        Dictionary<string, string?> terminal = new()
        { ["TERM"] = "native terminal", ["LANG"] = "C.UTF-8", ["LC_ALL"] = "C.UTF-8", ["LC_CTYPE"] = "C.UTF-8" };
        GitExecutionEnvironment actual = InteractiveFileConsoleEnvironment.WithUserTerminalContext(captured, key => terminal.GetValueOrDefault(key));
        Dictionary<string, string?> expected = new(variables, StringComparer.Ordinal) { ["TERM"] = "native terminal" };
        Assert.That(actual.EnvironmentVariables, Is.EquivalentTo(expected));
        Assert.That(captured.EnvironmentVariables, Is.EquivalentTo(variables));
    }

    [Test]
    public void AnAbsentTerminalContextKeepsTheOriginalSnapshotWithoutAdoptingUnknownLocaleVariables()
    {
        GitExecutionEnvironment captured = Capture();
        Dictionary<string, string?> terminal = new() { ["TERM"] = "", ["LANG"] = "", ["LC_ALL"] = null, ["LC_UNKNOWN"] = "foreign" };
        Assert.That(InteractiveFileConsoleEnvironment.WithUserTerminalContext(captured, key => terminal.GetValueOrDefault(key)), Is.SameAs(captured));
    }

    private static GitExecutionEnvironment Capture() => GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?>
    { ["PATH"] = "/captured", ["HOME"] = "/captured home", ["GIT_PAGER"] = "user pager λ" });
}
