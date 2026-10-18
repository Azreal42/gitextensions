using System.Text;
using GitCommands;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class FilePathPresentationTests
{
    [TestCase("name.ext", null, "name.ext")]
    [TestCase("path/name.ext", "path/", "name.ext")]
    [TestCase("nested/path/name.ext", "nested/path/", "name.ext")]
    [TestCase("/name.ext", "/", "name.ext")]
    [TestCase("/nested/path/name.ext", "/nested/path/", "name.ext")]
    [TestCase("C:/path/name.ext", "C:/path/", "name.ext")]
    [TestCase(@"path\name.ext", null, @"path\name.ext")]
    [TestCase(@"path\submodule.dir\", null, @"path\submodule.dir\")]
    [TestCase("/", null, "/")]
    [TestCase("submodule.dir/", null, "submodule.dir/")]
    [TestCase("/submodule.dir/", "/", "submodule.dir/")]
    [TestCase("path/submodule.dir/", "path/", "submodule.dir/")]
    [TestCase("/path/submodule.dir/", "/path/", "submodule.dir/")]
    [TestCase("", null, "")]
    [TestCase(" ", null, " ")]
    public void NoneUsesTheOriginalPosixSplitAndKeepsTerminalSlash(string name, string? prefix, string text)
    {
        AssertParts(FilePathPresentation.Format(TruncatePathMethod.None, 0, name, null,
            _ => throw new AssertionException("None does not truncate to measured width.")), prefix, text, null);
    }

    [TestCase("name.ext", "old.ext", null, "name.ext", " (old.ext)")]
    [TestCase("path/name.ext", "old/old.ext", "path/", "name.ext", " (old/old.ext)")]
    [TestCase("path/name.ext", "other/name.ext", "path/", "name.ext", " (other/name.ext)")]
    [TestCase("name.ext", "", null, "name.ext", null)]
    public void NoneRetainsTheFullPreviousPathIncludingSameBasenames(string name, string oldName,
        string? prefix, string text, string? suffix)
        => AssertParts(Format(TruncatePathMethod.None, 0, name, oldName), prefix, text, suffix);

    [TestCase("new.ext", null, "new.ext", null)]
    [TestCase("new.ext", "", "new.ext", null)]
    [TestCase("path/new.ext", null, "new.ext", null)]
    [TestCase("/path/new.ext", null, "new.ext", null)]
    [TestCase("C:/path/new.ext", null, "new.ext", null)]
    [TestCase("path/new.ext", "old.ext", "new.ext", " (old.ext)")]
    [TestCase("path/new.ext", "oldPath/old.ext", "new.ext", " (old.ext)")]
    [TestCase("path/new.ext", "/oldPath/old.ext", "new.ext", " (old.ext)")]
    [TestCase("path/new.ext", "other/new.ext", "new.ext", null)]
    [TestCase("path/submodule.dir/", null, "submodule.dir", null)]
    [TestCase("/", null, "", null)]
    [TestCase(@"path\new.ext", null, @"path\new.ext", null)]
    [TestCase(@"C:\path\new.ext", null, @"C:\path\new.ext", null)]
    [TestCase("path/new.ext", @"oldPath\old.ext", "new.ext", @" (oldPath\old.ext)")]
    public void FilenameOnlyKeepsStrictPosixBasenamesAndSuppressesOnlyAnEqualOldBasename(string name,
        string? oldName, string text, string? suffix)
        => AssertParts(FilePathPresentation.Format(TruncatePathMethod.FileNameOnly, 0, name, oldName,
            _ => throw new AssertionException("Filename only does not truncate the basename to width.")), null, text, suffix);

    [TestCase(TruncatePathMethod.TrimStart)]
    [TestCase(TruncatePathMethod.Compact)]
    public void ACompleteFittingRenameRetainsItsOriginalThreeParts(TruncatePathMethod method)
    {
        const string name = "path/name.ext", oldName = "old/old.ext";
        double exact = (name + " (" + oldName + ")").Length;
        AssertParts(Format(method, exact, name, oldName), "path/", "name.ext", " (old/old.ext)");
    }

    [TestCase(TruncatePathMethod.TrimStart)]
    [TestCase(TruncatePathMethod.Compact)]
    public void PreviousNameIsConsumedBeforeTheCurrentName(TruncatePathMethod method)
    {
        const string name = "new.txt", oldName = "old/very-long-previous.txt";
        // Seven current characters and the minimal six-character old-name suffix fit exactly.
        AssertParts(Format(method, 13, name, oldName), null, name, " (...)");
        var smaller = Format(method, 12, name, oldName);
        Assert.That(smaller.suffix, Is.EqualTo(" (...)"));
        Assert.That(smaller.text, Is.Not.EqualTo(name));
        Assert.That(Display(smaller).Length, Is.LessThanOrEqualTo(12));
    }

    [TestCase(TruncatePathMethod.TrimStart)]
    [TestCase(TruncatePathMethod.Compact)]
    public void AShortButWidePreviousNameCannotForceAnOtherwiseFittingCurrentNameToShrink(TruncatePathMethod method)
    {
        const string name = "iiiiiiiiii";
        var result = FilePathPresentation.Format(method, 16, name, "W", text => text.Sum(character => character == 'W' ? 40d : 1d));
        AssertParts(result, null, name, " (...)");
    }

    [TestCase(TruncatePathMethod.TrimStart)]
    [TestCase(TruncatePathMethod.Compact)]
    public void TinyWidthsNeverMakeANonemptyCurrentNameDisappear(TruncatePathMethod method)
    {
        foreach (double width in new[] { -1d, 0d, 1d, 2d })
        { AssertParts(Format(method, width, "path/file.ext", "old/file.ext"), null, "...", null); }
    }

    [TestCase(TruncatePathMethod.TrimStart)]
    [TestCase(TruncatePathMethod.Compact)]
    public void EmptyCurrentNameCanShowOnlyThePreviousSuffix(TruncatePathMethod method)
        => AssertParts(Format(method, 4, "", "b"), null, "", " (b)");

    [Test]
    public void TrimStartKeepsTheEndAndUsesMeasuredWidthRatherThanCharacterCount()
    {
        var result = FilePathPresentation.Format(TruncatePathMethod.TrimStart, 8, "WWWWii", null,
            text => text.Sum(character => character == 'W' ? 7d : 1d));
        AssertParts(result, null, "...ii", null);
        AssertParts(Format(TruncatePathMethod.TrimStart, 12, "deep/path/file.txt"), ".../", "file.txt", null);
    }

    [Test]
    public void CompactMatchesTheDocumentedFilenameBudgetIncludingTerminatingNull()
    {
        // Microsoft documents cchMax=10 -> nine visible characters, "My Fil...".
        AssertParts(Format(TruncatePathMethod.Compact, 9, "My Filename"), null, "My Fil...", null);
        AssertParts(Format(TruncatePathMethod.Compact, 11, "My Filename"), null, "My Filename", null);
    }

    [Test]
    public void CompactReplacesDirectoryComponentsAndPreservesTheCompleteFilenameWhenItFits()
    {
        AssertParts(Format(TruncatePathMethod.Compact, 18, "alpha/beta/gamma/file.txt"), "alpha/.../", "file.txt", null);
        AssertParts(Format(TruncatePathMethod.Compact, 15, "/alpha/beta/file.txt"), "/.../", "file.txt", null);
        var narrower = Format(TruncatePathMethod.Compact, 11, "alpha/beta/very-long-name.txt");
        Assert.That(Display(narrower).Length, Is.LessThanOrEqualTo(11));
        Assert.That(Display(narrower), Does.Contain(".../").And.EndsWith("..."));
    }

    [Test]
    public void CompactMeasuresTheActualCandidateRatherThanAssumingEveryGlyphHasEqualWidth()
    {
        const string name = "WWWiiiWWW.txt";
        static double Width(string text) => text.Sum(character => character == 'W' ? 6d : 1d);
        AssertParts(FilePathPresentation.Format(TruncatePathMethod.Compact, 14, name, null, Width), null, "W...", null);
        AssertParts(Format(TruncatePathMethod.Compact, 14, name), null, name, null);
    }

    [TestCase(TruncatePathMethod.TrimStart)]
    [TestCase(TruncatePathMethod.Compact)]
    public void TruncationDoesNotSplitSurrogatesOrSeparateACombiningMarkFromItsBase(TruncatePathMethod method)
    {
        const string name = "folder/🦊e\u0301🦊e\u0301-file.txt", oldName = "old/🦊e\u0301-name.txt";
        for (int width = 0; width <= name.Length + oldName.Length + 3; width++)
        {
            string result = Display(Format(method, width, name, oldName));
            Assert.That(result.EnumerateRunes().Any(rune => rune.Value == 0xfffd), Is.False, "width=" + width);
            Assert.That(result, Does.Not.Contain("...\u0301"), "width=" + width);
            foreach (int index in Enumerable.Range(0, result.Length).Where(index => result[index] == '\u0301'))
            { Assert.That(index, Is.GreaterThan(0)); Assert.That(result[index - 1], Is.EqualTo('e')); }
        }
    }

    [Test]
    public void LiteralBackslashesNeverBecomeDirectoryBoundariesInAnyMode()
    {
        const string name = @"literal\folder\name.txt";
        foreach (TruncatePathMethod method in Enum.GetValues<TruncatePathMethod>())
        {
            var result = Format(method, 12, name);
            Assert.That(result.prefix, Is.Null, method.ToString());
        }
        AssertParts(Format(TruncatePathMethod.FileNameOnly, 0, name), null, name, null);
    }

    [Test]
    public void LiteralPathContentIsNeitherEscapedNormalizedNorInterpretedByThePureFormatter()
    {
        const string name = ":folder[1]/λ; $(touch nope)\n?.txt", oldName = @"old\literal/previous?.txt";
        var result = Format(TruncatePathMethod.None, 0, name, oldName);
        Assert.That(result.prefix + result.text, Is.EqualTo(name));
        Assert.That(result.suffix, Is.EqualTo(" (" + oldName + ")"));
        AssertParts(Format(TruncatePathMethod.FileNameOnly, 0, name, oldName), null, "λ; $(touch nope)\n?.txt", " (previous?.txt)");
    }

    [Test]
    public void InvalidPoliciesAndInvalidWidthMeasurementsAreVisibleErrors()
    {
        Assert.That(() => Format((TruncatePathMethod)99, 10, "name"), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => Format(TruncatePathMethod.TrimStart, double.NaN, "name"), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => FilePathPresentation.Format(TruncatePathMethod.TrimStart, 10, "name", null, _ => double.NaN), Throws.ArgumentException);
    }

    private static (string? prefix, string? text, string? suffix) Format(TruncatePathMethod method, double width,
        string name, string? oldName = null) => FilePathPresentation.Format(method, width, name, oldName, text => text.Length);
    private static string Display((string? prefix, string? text, string? suffix) value) => string.Concat(value.prefix, value.text, value.suffix);
    private static void AssertParts((string? prefix, string? text, string? suffix) value, string? prefix, string text, string? suffix)
    {
        Assert.Multiple(() =>
        {
            Assert.That(value.prefix, Is.EqualTo(prefix)); Assert.That(value.text, Is.EqualTo(text)); Assert.That(value.suffix, Is.EqualTo(suffix));
        });
    }
}
