using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class CommitMessageFormattingTests
{
    // Primary Windows oracle: UnitTests/GitUI.Tests/CommandsDialogs/FormCommitTests.cs.
    [TestCase("master", "Message:{{(.*)}} end", "Message:master end")]
    [TestCase("master", "Message:{{.*}} end", "Message: end")]
    [TestCase("master", @"Name: {{([A-Z]+-\d+)-(.*)}}", "Name: ")]
    [TestCase("feature/ABC-4587-commitMessageRegex", @"{{([A-Z]+-\d+)}}: My message, issue:{{[A-Z]+-(\d+)}}", "ABC-4587: My message, issue:4587")]
    [TestCase("feature/ABC-4587-commitMessageRegex", @"Name: {{([A-Z]+-\d+)-(.*)}}[2], issue: {{([A-Z]+-\d+)-(.*)}}[1]", "Name: commitMessageRegex, issue: ABC-4587")]
    [TestCase("feature/ABC-4587-commitMessageRegex", @"{{([A-Z]+-\d+)-(.*)}}: My message", "ABC-4587: My message")]
    [TestCase("feature/ABC-4587-commitMessageRegex", @"Commit from:{{^feature/(.*)$}}[2] branch", "Commit from: branch")]
    [TestCase("feature/ABC-4587-commitMessageRegex", @"Commit from:{{^feature/(.*)$}} branch", "Commit from:ABC-4587-commitMessageRegex branch")]
    [TestCase("", "Commit message {{}}", "Commit message ")]
    [TestCase("", "Commit message {{.*}}", "Commit message ")]
    [TestCase("main", "Commit message {{ } end", "Commit message {{ } end")]
    [TestCase("main", "Literal ? () {} []", "Literal ? () {} []")]
    public void BranchRegexMatchesTheWindowsCorpus(string branch, string template, string expected)
    {
        var actual = CommitMessageFormatting.Expand(new("named", template, true), branch);
        Assert.That(actual.Text, Is.EqualTo(expected)); Assert.That(actual.Diagnostic, Is.Null);
    }
    [Test]
    public void DisabledRegexKeepsLiteralBracesAndAnInvalidLaterPatternKeepsTheTextAlreadyReached()
    {
        const string original = "{{(.*)}} then {{[}}";
        Assert.That(CommitMessageFormatting.Expand(new("", original), "main").Text, Is.EqualTo(original));
        var result = CommitMessageFormatting.Expand(new("", original, true), "main");
        Assert.That(result.Text, Is.EqualTo("main then {{[}}")); Assert.That(result.Diagnostic, Is.Not.Empty);
    }
    [Test]
    public void PathologicalRegexTimesOutWithoutReplacingItsUnprocessedPlaceholder()
    {
        const string pattern = "before {{(a+)+$}} after";
        var result = CommitMessageFormatting.Expand(new("", pattern, true), new string('a', 5000) + "!");
        Assert.That(result.Text, Is.EqualTo(pattern)); Assert.That(result.Diagnostic, Is.Not.Empty);
        Assert.That(CommitMessageFormatting.RegexTimeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }
    // Primary Windows oracle: IntegrationTests/UI.IntegrationTests/CommandsDialogs/FormCommitTests.cs:615–678.
    [TestCase("", 0, false, "feat: ", 6)]
    [TestCase("text", 3, false, "feat: text", 9)]
    [TestCase("", 0, true, "feat(): ", 5)]
    [TestCase("text", 3, true, "feat(): text", 5)]
    [TestCase("fix: ", 0, false, "feat: ", 6)]
    [TestCase("fix: text", 3, false, "feat: text", 6)]
    [TestCase("fix: ", 0, true, "feat(): ", 5)]
    [TestCase("fix: text", 3, true, "feat(): text", 5)]
    [TestCase("fix(scope): ", 0, true, "feat(scope): ", 13)]
    [TestCase("fix(scope): text", 14, true, "feat(scope): text", 15)]
    public void ConventionalTitlesAndCaretMatchWindows(string text, int caret, bool scope, string expected, int position)
    {
        var result = CommitMessageFormatting.PrefixKeyword(text, caret, "feat", scope);
        Assert.That(result.Title, Is.EqualTo(expected)); Assert.That(result.Caret, Is.EqualTo(position));
    }
    [Test]
    public void DisabledDefaultsKeepEveryByteAndWrappingUsesTheSharedStrictWordBoundary()
    {
        const string text = "Subject\r\nbody keeps  two spaces\r\nlast\r\n";
        Assert.That(CommitMessageFormatting.Format(text, new()), Is.Empty);
        Assert.That(Apply("Subject\none two three", new() { MaxLineLength = 8 }), Is.EqualTo("Subject\none two\nthree"));
        Assert.That(Apply("Subject\none two three", new() { MaxLineLength = 8, AutoWrap = false }), Is.EqualTo("Subject\none two three"));
        Assert.That(Apply("Subject\none two three", new() { MaxLineLength = 8, AutoWrap = false }, true), Is.EqualTo("Subject\none two\nthree"));
    }
    [Test]
    public void BlankSecondLineAndIndentAreIndependentAndPreserveTheSubject()
    {
        Assert.That(Apply("Subject\nbody", new() { SecondLineMustBeEmpty = true }), Is.EqualTo("Subject\n\n - body"));
        Assert.That(Apply("Subject\nbody", new() { SecondLineMustBeEmpty = true, IndentAfterFirstLine = false }), Is.EqualTo("Subject\n\nbody"));
        Assert.That(Apply("Subject\n\nbody", new() { SecondLineMustBeEmpty = true }), Is.EqualTo("Subject\n\nbody"));
    }
    [Test]
    public void ValidationKeepsWarningOrderAndTheWindowsMoreThanTwoLinesCondition()
    {
        var settings = new CommitMessagePreferences { MaxFirstLineLength = 3, MaxLineLength = 3, SecondLineMustBeEmpty = true, ValidationRegex = "^other$" };
        var warnings = CommitMessageFormatting.Warnings("subject\nbody\nend", settings, out var error);
        Assert.That(error, Is.Null);
        Assert.That(warnings.Select(w => w.Item), Is.EqualTo(new[] { "_commitMsgFirstLineInvalid", "_commitMsgLineInvalid", "_commitMsgLineInvalid", "_commitMsgSecondLineNotEmpty", "_commitMsgRegExNotMatched" }));
        Assert.That(warnings[1].Line, Is.EqualTo("subject")); Assert.That(warnings[2].Line, Is.EqualTo("body"));
        Assert.That(CommitMessageFormatting.Warnings("subject\nbody", new() { SecondLineMustBeEmpty = true }, out _), Is.Empty);
    }
    [Test]
    public void RegexValidationSkipsFixupSquashUsesAmendBodyAndReportsInvalidPatternWithoutAVeto()
    {
        var settings = new CommitMessagePreferences { ValidationRegex = "^body$" };
        foreach (string text in new[] { "fixup! anything", "squash! anything", "amend! subject\n\nbody" })
        { Assert.That(CommitMessageFormatting.Warnings(text, settings, out _), Is.Empty, text); }
        Assert.That(CommitMessageFormatting.Warnings("ordinary", settings, out _), Has.Count.EqualTo(1));
        Assert.That(CommitMessageFormatting.Warnings("ordinary", settings with { ValidationRegex = "[" }, out var error), Is.Empty);
        Assert.That(error, Is.Not.Empty);
    }
    private static string Apply(string text, CommitMessagePreferences settings, bool manual = false)
    {
        foreach (var edit in CommitMessageFormatting.Format(text, settings, manual).Reverse()) { text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text); }
        return text;
    }
}
