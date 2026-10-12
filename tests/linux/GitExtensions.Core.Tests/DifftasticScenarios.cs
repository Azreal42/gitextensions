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
            string textHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.Content.Text))).ToLowerInvariant();
            Check(textHash == "229f27e6eb961c96b56ec69dad434d76af8e1d46aebfe499ef875097d05adaae"
                && result.Content.Text.Length == 29036 && result.Lines.Count == 239, "The original golden retains its complete canonical rendered text and exact offsets.");
            foreach ((int row, int offset, int length) in new[] { (5, 182, 20), (22, 2062, 97), (29, 2924, 192), (30, 3117, 122), (33, 3563, 157), (34, 3721, 97), (56, 6591, 123) })
            {
                Check(result.Lines[row - 1].Offset == offset && result.Lines[row - 1].Length == length, $"Original golden line{row} retains its exact UTF16 offset and length.");
            }
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
            Check(result.Content.Text == "λ --- text\r\n😀 old\r\n🦊 new\r\nnext --- text\r\n... 9 tail\r\n",
                "Proven one-sided ANSI prefixes keep Unicode and CRLF; an uncolored continuation remains literal.");
            Check(result.Lines[1] is { OldLine: 7, NewLine: null, Kind: GitDiffLineKind.Removed, IsChanged: true }
                && result.Lines[2] is { OldLine: null, NewLine: 8, Kind: GitDiffLineKind.Added, IsChanged: true }
                && result.Lines[3] is { OldLine: null, NewLine: null, Kind: GitDiffLineKind.Header }
                && result.Lines[4] is { OldLine: null, NewLine: null, Kind: GitDiffLineKind.Context }, "Only proven removed/added prefixes move numbers into gutters.");
            Check(result.Content.Spans.All(span => span.Length == 6 && result.Content.Text.Substring(span.Offset, span.Length).EndsWith("old", StringComparison.Ordinal)
                || span.Length == 6 && result.Content.Text.Substring(span.Offset, span.Length).EndsWith("new", StringComparison.Ordinal)), "UTF16 surrogate colors no longer include the textual numbers.");
        }
        string noneLeft = "file --- text\n1 old     1 new\n          2 added";
        GitDifftasticText formatted = GitDifftasticFormatter.Format(new(noneLeft, []), 20, false);
        Check(formatted.Content.Text == noneLeft && formatted.RightColumnStart == 0
            && formatted.Lines.All(line => line.OldLine is null && line.NewLine is null), "Uncolored columns remain the exact tool text instead of an inferred grid.");
        return Task.CompletedTask;
    }

    public static Task UnexpectedOutputAndBoundsAsync()
    {
        GitColoredText input = new("header\nplain output without numbers\n9999999999999999999999999 content\n", []);
        GitDifftasticText result = GitDifftasticFormatter.Format(input, 10, false);
        Check(result.Content.Text == input.Text && result.Lines.All(line => line.OldLine is null && line.NewLine is null),
            "Unexpected uncolored output, including oversized numeric code, stays byte-for-byte visible without invented gutter numbers.");
        Check(GitDifftasticFormatter.Format(new("\r\n \n", []), 80, false) is { Content.Text: "\r\n \n", RightColumnStart: 0 },
            "A whitespace-only uncolored presentation preserves the emitted text without a synthetic grid.");
        ExpectArgument(() => GitDifftasticFormatter.Format(input, 0, false));
        ExpectArgument(() => GitDifftasticFormatter.Format(new("line", [new(3, 2)]), 80, false));
        return Task.CompletedTask;
    }

    public static Task RealCompactAsync()
    {
        string fixture = Fixture("difftastic-0.71.0-compact.ansi.txt");
        string[] raw = GitAnsiColorParser.StripAnsi(fixture).Split('\n');
        string expected = raw[0] + "\n" + string.Concat(raw.Skip(1).Where(line => line.Length > 0)
            .Select(line => line.Substring(3, 53) + line[59..] + "\n"));
        foreach (bool dark in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        {
            GitColoredText input = GitAnsiColorParser.Parse(fixture, DiffColorScenarios.Palette(dark), reverse);
            GitDifftasticText result = GitDifftasticFormatter.Format(input, 200, reverse);
            Check(result.RightColumnStart == 53 && result.Content.Text == expected,
                "Official DFT 0.71.0 width200 compact grid strips the exact two three-character gutter fields, not source numbers.");
            Check(result.Lines.Count == 17 && result.Lines.Skip(1).Select(line => line.OldLine).SequenceEqual(Enumerable.Range(1, 16).Select(value => (int?)value))
                && result.Lines.Skip(1).Select(line => line.NewLine).SequenceEqual(Enumerable.Range(1, 16).Select(value => (int?)value)),
                "All sixteen real compact rows retain old/new gutter numbers under both original palettes and color reversal.");
            Check(result.Content.Text.Contains("=> 42;", StringComparison.Ordinal) && result.Content.Text.Contains("=> 43;", StringComparison.Ordinal)
                && result.Content.Text.Contains("Bonjour λ 🦊", StringComparison.Ordinal), "Actual source literals and surrogate pairs survive compact formatting.");
            Check(result.Lines[3].IsChanged && result.Lines[9].IsChanged && result.Lines[15].IsChanged, "The actual changed rows retain navigation metadata.");
            ColorsAndOffsets(input, result, ["42", "43"]);
        }
        return Task.CompletedTask;
    }

    public static Task RealNoColor177Async()
    {
        GitColoredText input = GitAnsiColorParser.Parse(Fixture("difftastic-0.71.0-compact-177.ansi.txt"), DiffColorScenarios.Palette());
        Check(input.Spans.Count == 0, "The official probe respects DFT_COLOR=never with width override177 and three one-context hunks.");
        GitDifftasticText result = GitDifftasticFormatter.Format(input, 177, false);
        Check(result.RightColumnStart == 0 && result.Content.Text == input.Text
            && result.Lines.All(line => line.OldLine is null && line.NewLine is null)
            && result.Lines.Count(line => line.Kind == GitDiffLineKind.Header) == 3,
            "The color-never177 override preserves every byte, literal number and hunk separator, without guessing gutters.");
        Check(result.Content.Text.Contains("=> 42;", StringComparison.Ordinal) && result.Content.Text.Contains("=> 43;", StringComparison.Ordinal)
            && result.Content.Text.Contains("return value * 3;", StringComparison.Ordinal), "A no-color numeric code token is never mistaken for a gutter.");
        ColorsAndOffsets(input, result, []);
        string ambiguity = Fixture("difftastic-0.71.0-plain-ambiguity.txt");
        GitDifftasticText numeric = GitDifftasticFormatter.Format(new(ambiguity, []), 177, false);
        Check(numeric.Content.Text == ambiguity && numeric.RightColumnStart == 0
            && numeric.Lines.All(line => line.OldLine is null && line.NewLine is null)
            && numeric.Content.Text.Contains("left                       1 code", StringComparison.Ordinal)
            && numeric.Content.Text.Contains("left                       2 code", StringComparison.Ordinal),
            "Official DFT empty-to-added numeric source is geometrically indistinguishable from a grid: its entire emitted text must remain intact.");
        return Task.CompletedTask;
    }

    public static Task RealUnicodeUnilateralAsync()
    {
        string fixture = Fixture("difftastic-0.71.0-numeric-unicode-177.ansi.txt");
        foreach (bool dark in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        foreach (int width in new[] { 177, 58 })
        {
            GitColoredText input = GitAnsiColorParser.Parse(fixture, DiffColorScenarios.Palette(dark), reverse);
            GitDifftasticText result = GitDifftasticFormatter.Format(input, width, reverse);
            Check(result.RightColumnStart == 27, "Real DFT's minimum25 source cells produce a 27-cell rendered right column.");
            int?[] old = [null, 1, 2, 3, 4, 5, 6, null, null], current = [null, 1, null, 2, 3, 4, 5, 6, 7];
            Check(result.Lines.Select(line => line.OldLine).SequenceEqual(old) && result.Lines.Select(line => line.NewLine).SequenceEqual(current),
                "Real deleted-only and added-only rows remain in the appropriate old/new gutters.");
            Check(result.Lines[2] is { Kind: GitDiffLineKind.Removed, IsChanged: true }
                && result.Lines[7] is { Kind: GitDiffLineKind.Added, IsChanged: true }
                && result.Lines[8] is { Kind: GitDiffLineKind.Added, IsChanged: true }, "Unilateral real rows retain removed/added navigation kinds.");
            Check(result.Content.Text.Contains("2000 deleted λ 🦊 漢字", StringComparison.Ordinal)
                && result.Content.Text.Contains("8000 fresh addition A", StringComparison.Ordinal) && result.Content.Text.Contains("9000 fresh addition B", StringComparison.Ordinal),
                "Numbers from code, emoji and wide CJK characters remain literal content.");
            string cjk = Line(result, 2);
            Check(cjk == "2000 deleted λ 🦊 漢字     ", "The CJK row removes its right placeholder at the actual UTF16 offset, without cutting wide source characters.");
            Check(Line(result, 7) == new string(' ', 27) + "8000 fresh addition A", "A right-only real row aligns at the detected cell boundary.");
            ColorsAndOffsets(input, result, ["5000", "6000", "8000", "9000"]);
        }
        foreach (bool reverse in new[] { false, true })
        foreach (int width in new[] { 177, 58 })
        {
            GitColoredText emoji = GitAnsiColorParser.Parse(Fixture("difftastic-0.71.0-emoji-177.ansi.txt"), DiffColorScenarios.Palette(), reverse);
            GitDifftasticText result = GitDifftasticFormatter.Format(emoji, width, reverse);
            Check(result.RightColumnStart == 27 && result.Lines.Select(line => line.NewLine).SequenceEqual(new int?[] { null, 1, null, 2, 3, 4, 5, 6, 7 }),
                "ZWJ, skin-tone, keycap and flag sequences use terminal grapheme cells, even when the actual grid coincides with the nominal half-width.");
            Check(Line(result, 1).Contains("1000 👩‍💻 unchanged marker", StringComparison.Ordinal)
                && Line(result, 2).Contains("2000 👍🏽 deleted marker", StringComparison.Ordinal)
                && Line(result, 4).Contains("4000 1️⃣ old value", StringComparison.Ordinal)
                && Line(result, 5).Contains("5000 🇫🇷 old value", StringComparison.Ordinal)
                && Line(result, 6).IndexOf("6000", 1, StringComparison.Ordinal) == 27, "Composed emoji and later ASCII numeric tokens retain literal code and aligned right-column metadata.");
            ColorsAndOffsets(emoji, result, ["7000", "8000"]);
        }
        // Official one-column grammar colors the entire source line. Its own
        // leading number is code, even if another section establishes a grid.
        const string e = "\u001b[";
        GitColoredText single = GitAnsiColorParser.Parse(fixture + $"\none-sided --- Text\n{e}32;1m1 12345 numeric source{e}m\n", DiffColorScenarios.Palette());
        GitDifftasticText one = GitDifftasticFormatter.Format(single, 177, false);
        Check(one.Content.Text.EndsWith("12345 numeric source\n", StringComparison.Ordinal)
            && one.Lines[^1] is { OldLine: null, NewLine: 1 }, "A one-column whole-line color cannot turn a numeric source token into a second gutter.");
        foreach (string solitary in new[] { "one-sided --- Text\n1 12345 numeric source\n", $"one-sided --- Text\n{e}32;1m1 12345 numeric source{e}m\n", $"one-sided --- Text\n{e}32;1m1 1000 {e}m\n" })
        {
            GitColoredText solitaryInput = GitAnsiColorParser.Parse(solitary, DiffColorScenarios.Palette());
            GitDifftasticText solitaryResult = GitDifftasticFormatter.Format(solitaryInput, 177, false);
            Check(solitaryResult.RightColumnStart == 0 && solitaryResult.Content.Text.EndsWith(solitary.Contains("1000", StringComparison.Ordinal) ? "1000 \n" : "12345 numeric source\n", StringComparison.Ordinal)
                && solitaryResult.Lines[^1].NewLine != 12345 && solitaryResult.Lines[^1].NewLine != 1000,
                "An autonomous one-column document also preserves numeric code without any adaptive layout evidence.");
        }
        foreach (string numericCode in new[]
        {
            "one-sided --- Text\n1 " + new string(' ', 27) + "1000 code\n2 " + new string(' ', 27) + "2000 code\n",
            "one-sided --- Text\n1 left" + new string(' ', 23) + "1000 code\n2 left" + new string(' ', 23) + "2000 code\n",
            "one-sided --- Text\n1 left" + new string(' ', 23) + "1000 code\n2 left" + new string(' ', 23) + "1001 code\n"
        })
        {
            GitDifftasticText numericResult = GitDifftasticFormatter.Format(new(numericCode, []), 177, false);
            Check(numericResult.RightColumnStart == 0 && numericResult.Content.Text.Contains("1000 code", StringComparison.Ordinal)
                && numericResult.Content.Text.Contains(numericCode.Contains("2000", StringComparison.Ordinal) ? "2000 code" : "1001 code", StringComparison.Ordinal),
                "Aligned numeric source text without a credible consecutive right gutter remains literal.");
        }
        return Task.CompletedTask;
    }

    public static Task RealMultipleFilesAsync()
    {
        string fixture = Fixture("difftastic-0.71.0-compact.ansi.txt") + "\n" + Fixture("difftastic-0.71.0-numeric-unicode-177.ansi.txt");
        foreach (bool reverse in new[] { false, true })
        {
            GitColoredText input = GitAnsiColorParser.Parse(fixture, DiffColorScenarios.Palette(), reverse);
            GitDifftasticText result = GitDifftasticFormatter.Format(input, 200, reverse);
            Check(result.RightColumnStart == 53 && result.Lines.Count == 26, "Concatenated files share a single ruler using the widest actual source grid.");
            Check(Line(result, 24) == new string(' ', 53) + "8000 fresh addition A",
                "The narrower file's right-only rows receive uncolored padding up to the document ruler.");
            Check(Line(result, 18).IndexOf("1000", 1, StringComparison.Ordinal) == 53,
                "A narrower two-sided row aligns its right numeric code token without stripping it.");
            ColorsAndOffsets(input, result, ["42", "43", "8000"]);
        }
        // Real DFT_CONTEXT can expose thousands of rows. Detection visits only
        // spans of the current row and a bounded prefix around the grid.
        const string e = "\u001b[";
        string large = "large --- Text\n" + string.Concat(Enumerable.Range(1, 9000).Select(value =>
            $"{e}2m{value,4} {e}mcode{new string(' ', 27)}{e}2m{value,4} {e}m{e}1mvalue 123{e}m\n"));
        GitColoredText largeInput = GitAnsiColorParser.Parse(large, DiffColorScenarios.Palette());
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        GitDifftasticText largeResult = GitDifftasticFormatter.Format(largeInput, 200, false);
        Check(largeResult.Lines.Count == 9001 && largeResult.Lines[^1] is { OldLine: 9000, NewLine: 9000 }
            && largeResult.Content.Text.Contains("value 123", StringComparison.Ordinal), "A 9000-row colored source retains its real rows, gutters and syntax literals.");
        Check(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
            "A 9000-row source must not regress to a rows-times-all-spans search.");
        return Task.CompletedTask;
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "tests", "linux", "Fixtures", name);
            if (File.Exists(path)) { return File.ReadAllText(path); }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Exact official Difftastic fixture was not found.", name);
    }

    private static string Line(GitDifftasticText result, int row) => result.Content.Text.Substring(result.Lines[row].Offset, result.Lines[row].Length);

    private static void ColorsAndOffsets(GitColoredText input, GitDifftasticText result, string[] tokens)
    {
        Check(result.Content.Spans.All(span => result.Lines.Any(line => span.Offset >= line.Offset && span.Offset + span.Length <= line.Offset + line.Length)),
            "Compact grid remapping retains valid UTF16 color offsets within each rendered line.");
        Check(result.Lines.All(line => line.Offset >= 0 && line.Offset + line.Length < result.Content.Text.Length && result.Content.Text[line.Offset + line.Length] == '\n'),
            "Every rendered line's exact offset/length ends at its own newline.");
        foreach (string token in tokens)
        {
            GitDiffColorSpan[] before = [.. input.Spans.Where(span => input.Text.Substring(span.Offset, span.Length).Contains(token, StringComparison.Ordinal))];
            GitDiffColorSpan[] after = [.. result.Content.Spans.Where(span => result.Content.Text.Substring(span.Offset, span.Length).Contains(token, StringComparison.Ordinal))];
            Check(after.Length > 0 && after.All(span => before.Any(original => original.Background == span.Background && original.Foreground == span.Foreground)),
                $"Actual ANSI palette colors survive compact layout for literal token {token}.");
        }
    }

    private static void ExpectArgument(Action action)
    { try { action(); throw new InvalidOperationException("Invalid Difftastic segment/width was accepted."); } catch (ArgumentException) { } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
