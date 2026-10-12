using GitExtensions.Linux;

internal static class DifftasticScenarios
{
    public static Task OriginalFixtureAsync()
    {
        string fixture = File.ReadAllText(DiffColorScenarios.Original("SampleDifftastic.diff"));
        foreach (bool reverse in new[] { false, true })
        {
            GitColoredText input = GitAnsiColorParser.Parse(fixture, DiffColorScenarios.Palette(), reverse);
            GitDifftasticText result = GitDifftasticFormatter.Format(input, 200, reverse);
            Check(result.RightColumnStart == 97, "Original Difftastic fixture keeps its exact 97-character right column.");
            CheckLine(5, null, 16, GitDiffLineKind.Added, true);
            CheckLine(22, 51, null, GitDiffLineKind.Removed, true);
            CheckLine(29, 106, 111, GitDiffLineKind.Context, false);
            CheckLine(30, null, null, GitDiffLineKind.Context, false);
            CheckLine(33, 109, 114, GitDiffLineKind.Context, true);
            // The upstream golden forces reverse=false. Nested color+dim ellipses use a
            // foreground on EditorBackground, which the original reverse-mode guess treats as unchanged.
            CheckLine(34, null, null, GitDiffLineKind.Context, !reverse);
            if (reverse)
            {
                GitDifftasticLine continuation = result.Lines[33];
                Check(result.Content.Spans.Any(span => span.Offset >= continuation.Offset && span.Offset < continuation.Offset + continuation.Length
                    && span.Background is GitDiffColor color && color.R != color.G), "Reverse-mode continuation retains its real changed-token colors despite unchanged ellipsis gutters.");
            }
            CheckLine(56, null, 342, GitDiffLineKind.Added, true);
            Check(!result.Content.Text.Contains('\u001b') && result.Content.Text.Length < input.Text.Length,
                "Textual Difftastic numbers/separators are removed, preserving a colored content document.");
            Check(input.Text == GitAnsiColorParser.StripAnsi(fixture), "Formatting does not mutate the original colored input.");
            Check(result.Content.Spans.All(span => result.Lines.Any(line => span.Offset >= line.Offset && span.Offset + span.Length <= line.Offset + line.Length)),
                "Every remapped color span remains in a rendered content line.");
            void CheckLine(int index, int? oldLine, int? newLine, GitDiffLineKind kind, bool changed)
            {
                GitDifftasticLine line = result.Lines[index - 1];
                Check(line.OldLine == oldLine && line.NewLine == newLine && line.Kind == kind && line.IsChanged == changed,
                    $"Original Difftastic metadata line{index}: actual {line}.");
            }
        }
        return Task.CompletedTask;
    }

    public static Task ColumnsAndColorsAsync()
    {
        const string e = "\u001b[";
        string source = $"file --- text\n{e}31m1 old{e}m     {e}32m1 new{e}m\n{e}31m22 old{e}m    {e}32m22 new{e}m\n";
        GitColoredText input = GitAnsiColorParser.Parse(source, DiffColorScenarios.Palette());
        GitDifftasticText result = GitDifftasticFormatter.Format(input, 20, false);
        Check(result.Content.Text == "file --- text\nold     new\nold     new\n" && result.RightColumnStart == 8,
            "Increasing line-number width aligns both content columns after stripping each side's number.");
        Check(result.Lines[1] is { OldLine: 1, NewLine: 1, IsChanged: true, Kind: GitDiffLineKind.Context }
            && result.Lines[2] is { OldLine: 22, NewLine: 22, IsChanged: true }, "Both changed sides retain old/new metadata without a new line-kind enum.");
        Check(result.Content.Spans.Count == 4 && result.Content.Spans.All(span => span.Length == 3)
            && result.Content.Spans.Select(span => result.Content.Text.Substring(span.Offset, span.Length)).SequenceEqual(["old", "new", "old", "new"]),
            "Colors are attached to exact code tokens, with no residual number or alignment-padding color.");
        Check(result.Content.Spans[0].Foreground == input.Spans[0].Foreground
            && result.Content.Spans[1].Foreground == input.Spans[1].Foreground, "Original ANSI colors survive offset remapping unchanged.");
        Check(!result.Content.Spans.Any(span => span.Offset < result.Lines[0].Length), "Uncolored headers do not acquire invented background markers.");
        return Task.CompletedTask;
    }

    public static Task OneSidedAndUnicodeAsync()
    {
        const string e = "\u001b[";
        foreach (bool reverse in new[] { false, true })
        {
            string source = $"λ --- text\r\n{e}31m7 {e}m{e}31m😀 old{e}m\r\n{e}32m8 {e}m{e}32m🦊 new{e}m\r\n\r\nnext --- text\r\n... 9 tail\r\n";
            GitColoredText input = GitAnsiColorParser.Parse(source, DiffColorScenarios.Palette(), reverse);
            GitDifftasticText result = GitDifftasticFormatter.Format(input, 40, reverse);
            Check(result.Content.Text == "λ --- text\r\n😀 old\r\n🦊 new\r\nnext --- text\r\ntail\r\n",
                "One-sided text keeps Unicode and CRLF; blank section separators disappear.");
            Check(result.Lines[1] is { OldLine: 7, NewLine: null, Kind: GitDiffLineKind.Removed, IsChanged: true }
                && result.Lines[2] is { OldLine: null, NewLine: 8, Kind: GitDiffLineKind.Added, IsChanged: true }
                && result.Lines[3] is { OldLine: null, NewLine: null, Kind: GitDiffLineKind.Header }
                && result.Lines[4] is { OldLine: null, NewLine: 9, Kind: GitDiffLineKind.Context }, "Removed/added/ellipsis lines move their numbers into the correct gutter.");
            Check(result.Content.Spans.All(span => span.Length == 6 && result.Content.Text.Substring(span.Offset, span.Length).EndsWith("old", StringComparison.Ordinal)
                || span.Length == 6 && result.Content.Text.Substring(span.Offset, span.Length).EndsWith("new", StringComparison.Ordinal)), "UTF16 surrogate colors no longer include the textual numbers.");
        }
        string noneLeft = "file --- text\n1 old     1 new\n          2 added";
        GitDifftasticText formatted = GitDifftasticFormatter.Format(new(noneLeft, []), 20, false);
        Check(formatted.Lines[2].OldLine is null && formatted.Lines[2].NewLine == 2
            && formatted.Content.Text.EndsWith("        added\n", StringComparison.Ordinal), "A zero-context right-only column stays aligned with the preceding two-sided line.");
        return Task.CompletedTask;
    }

    public static Task UnexpectedOutputAndBoundsAsync()
    {
        GitColoredText input = new("header\nplain output without numbers\n9999999999999999999999999 content\n", []);
        GitDifftasticText result = GitDifftasticFormatter.Format(input, 10, false);
        Check(result.Content.Text == "header\nplain output without numbers\ncontent\n"
            && result.Lines.All(line => line.OldLine is null && line.NewLine is null), "Unexpected nonnumeric output stays visible and oversized line numbers cannot overflow.");
        Check(GitDifftasticFormatter.Format(new("\r\n \n", []), 80, false) is { Content.Text.Length: 0, Lines.Count: 0, RightColumnStart: 0 },
            "An empty presentation has no synthetic header or gutter row.");
        ExpectArgument(() => GitDifftasticFormatter.Format(input, 0, false));
        ExpectArgument(() => GitDifftasticFormatter.Format(new("line", [new(3, 2)]), 80, false));
        return Task.CompletedTask;
    }

    private static void ExpectArgument(Action action)
    { try { action(); throw new InvalidOperationException("Invalid Difftastic segment/width was accepted."); } catch (ArgumentException) { } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
