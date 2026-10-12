using System.Globalization;
using System.Text.RegularExpressions;
using GitExtensions.Linux;

internal static class DiffColorScenarios
{
    // Expected offsets and RGB examples are from the existing WinForms tests, not produced by the new implementation.
    public static Task OriginalAnsiFixturesAsync()
    {
        const string e = "\u001b[";
        string input = $"Text with {e}31mred{e}0m escape sequence\nextra line\nSome {e}0;1;31mbold red{e}0m\nNow {e}101mbold reverse red{e}0m\nand some {e}2:31mdim red{e}0m\n{e}31m{e}mdefault not red{e}0m{e}31m, red no end\n  ";
        GitColoredText parsed = GitAnsiColorParser.Parse(input, Palette());
        Check(parsed.Text == "Text with red escape sequence\nextra line\nSome bold red\nNow bold reverse red\nand some dim red\ndefault not red, red no end\n  ", "Original complete ANSI fixture text.");
        CheckOffsets(parsed.Spans, [(10, 3), (46, 8), (59, 16), (85, 7), (108, 15)]);
        Check(parsed.Spans[0].Foreground == new GitDiffColor(211, 0, 11)
            && parsed.Spans[1].Foreground == new GitDiffColor(255, 94, 94)
            && parsed.Spans[2].Background == new GitDiffColor(255, 165, 165)
            && parsed.Spans[3].Foreground == new GitDiffColor(234, 188, 188), "Original RGB values include gamma-correct dim.");
        input = $"Text with {e}38;5;196mred without end\nextra line\nthen {e}0;48:5:46;31;1mreverse green,\nthen {e}38;2;0;0;255mblue,\n{e}999mcode out of range,\n{e}99munused code,\n{e}30mblack to the end\n{e}incomplete escape1,\n{e}1xmincomplete escape2";
        parsed = GitAnsiColorParser.Parse(input, Palette());
        CheckOffsets(parsed.Spans, [(10, 32), (42, 20), (62, 6), (100, 62)]);
        Check(parsed.Text.EndsWith("\u001b[incomplete escape1,\n\u001b[1xmincomplete escape2", StringComparison.Ordinal), "Malformed escapes remain literal source text.");
        Check(parsed.Spans[0].Foreground == new GitDiffColor(255, 0, 0)
            && parsed.Spans[1].Background == new GitDiffColor(0, 255, 0)
            && parsed.Spans[2].Foreground == new GitDiffColor(0, 0, 255), "Original extended palette and RGB fixture colors.");
        string terminal = File.ReadAllText(Original("AnsiTerminalColors.diff"));
        parsed = GitAnsiColorParser.Parse(terminal, Palette());
        Check(parsed.Spans.Count == 96 && parsed.Spans.All(span => parsed.Text.Substring(span.Offset, span.Length) == "@!"),
            "The original generated 96-color terminal fixture produces only the intended colored sample spans.");
        Check(!parsed.Text.Contains('\u001b') && GitAnsiColorParser.StripAnsi(terminal) == parsed.Text, "Palette-free strip and colored parse share canonical fixture text.");
        return Task.CompletedTask;
    }

    public static Task AnsiPaletteAndAttributesAsync()
    {
        foreach (bool dark in new[] { false, true })
        {
            GitDiffColorPalette palette = Palette(dark);
            for (int slot = 0; slot < 16; slot++)
            {
                int fore = slot < 8 ? 30 + slot : 90 + slot - 8, back = slot < 8 ? 40 + slot : 100 + slot - 8;
                Check(Colored($"{fore}", palette).Foreground == palette.Foregrounds[slot], "All sixteen injected foreground slots.");
                Check(Colored($"{back}", palette).Background == palette.Backgrounds[slot], "All sixteen injected background slots.");
                Check(Colored($"7;{fore}", palette).Background == palette.Backgrounds[slot], "Reverse uses the background palette for a foreground slot.");
            }
            Check(Colored("1;31", palette).Foreground == palette.Foregrounds[9], "Bold normal red uses the bright slot.");
            Check(Colored("1;22;31", palette).Foreground == palette.Foregrounds[1], "Normal intensity cancels bold in the same sequence.");
            Check(Colored("39", palette).Foreground == palette.Foregrounds[dark ? 7 : 0], "Default foreground follows the supplied editor theme.");
            GitColoredText state = GitAnsiColorParser.Parse("\u001b[34ma\u001b[1mb\u001b[mc\u001b[1md", palette);
            Check(state.Text == "abcd" && state.Spans.Count == 3 && state.Spans[1].Foreground == palette.Foregrounds[12]
                && state.Spans[2].Offset == 3 && state.Spans[2].Foreground == palette.Foregrounds[dark ? 15 : 8], "Attribute-only continuation remembers base foreground; empty reset restores the default.");
            Check(Colored("38;5;196", palette).Foreground == new GitDiffColor(255, 0, 0)
                && Colored("48:5:46", palette).Background == new GitDiffColor(0, 255, 0)
                && Colored("38;5;255", palette).Foreground == new GitDiffColor(253, 253, 253)
                && Colored("38;5;232", palette).Foreground == new GitDiffColor(0, 0, 0)
                && Colored("38;5;67", palette).Foreground == new GitDiffColor(51, 102, 153), "The original 216-cube and 24-step grayscale mapping.");
            Check(Colored("38;2;12;34;56", palette).Foreground == new GitDiffColor(12, 34, 56)
                && Colored("48;2;12;34;56", palette).Background == new GitDiffColor(12, 34, 56), "Explicit RGB foreground and background.");
            Check(Colored("7;38;2;12;34;56;48;2;65;43;21", palette) is { Background: { R: 12, G: 34, B: 56 }, Foreground: { R: 65, G: 43, B: 21 } }, "Reverse swaps both explicit RGB colors.");
        }
        Check(Colored("1;91", Palette()).Foreground == new GitDiffColor(255, 145, 145), "Original extra-bold HSL red adjustment.");
        Check(Colored("2;41", Palette()).Background == new GitDiffColor(255, 230, 230), "Original gamma-dim red background.");
        Check(Colored("1;2;31", Palette()).Foreground == new GitDiffColor(255, 197, 197), "Original bold and dim red foreground.");
        foreach (int alias in new[] { 4, 5, 6, 9 })
        { Check(Colored(alias.ToString(CultureInfo.InvariantCulture) + ";31", Palette()).Foreground == new GitDiffColor(255, 94, 94), "Original underline/blink/strike bold aliases."); }
        foreach (int alias in new[] { 3, 8 })
        { Check(Colored(alias.ToString(CultureInfo.InvariantCulture) + ";31", Palette()).Foreground == new GitDiffColor(234, 188, 188), "Original italic/conceal dim aliases."); }
        Check(GitDiffColorMath.TextColor(new(128, 128, 128), new(120, 120, 120)) == new GitDiffColor(22, 22, 22), "Low-contrast text is adjusted by the original incremental algorithm.");
        return Task.CompletedTask;
    }

    public static Task AnsiMergingAndMalformedAsync()
    {
        GitDiffColorPalette palette = Palette();
        string text = "😀\r\n\u001b[31maλ\u001b[m\r\n\u001b[31mb\u001b[0m\u001b[31m\r\u001b[m\u001b[31mc\u001b[m!";
        GitColoredText parsed = GitAnsiColorParser.Parse(text, palette);
        Check(parsed.Text == "😀\r\naλ\r\nb\rc!" && parsed.Spans.Count == 1 && parsed.Spans[0].Offset == 4 && parsed.Spans[0].Length == 7,
            "UTF16 surrogate offsets, CRLF gap merging and invisible CR spans preserve canonical text.");
        Check(GitAnsiColorParser.Parse("\u001b[31m\r\u001b[m", palette).Spans.Count == 0, "A carriage-return-only marker has no visible effect.");
        string malformed = "source \u001b[watm \u001b[31x \u001b[31 incomplete \u001b]0;title\u0007";
        Check(GitAnsiColorParser.Parse(malformed, palette) is { Spans.Count: 0 } literal && literal.Text == malformed
            && GitAnsiColorParser.StripAnsi(malformed) == malformed, "Non-SGR and malformed controls are not silently removed from source.");
        foreach (string code in new[] { "999", "38;5;256", "48;5;999", "38;2;256;0;0", "38", "9999999999999999999999999999" })
        {
            parsed = GitAnsiColorParser.Parse($"\u001b[{code}mx", palette);
            Check(parsed.Text == "x" && parsed.Spans.Count == 0, "Unknown or malformed complete SGR is stripped without an invalid RGB exception.");
        }
        parsed = GitAnsiColorParser.Parse("\u001b[31 ma\u001b[ 0m", palette);
        Check(parsed.Text == "a\u001b[ 0m", "Whitespace accepted by the original capture pattern does not broaden to a new syntax.");
        Check(GitAnsiColorParser.Parse("\u001b[31mx\u001b[0my", palette).Text == "xy", "Explicit reset preserves uncolored following source.");
        string incomplete = "\u001b[" + new string('1', 100_000) + "x";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Check(GitAnsiColorParser.StripAnsi(incomplete) == incomplete && GitAnsiColorParser.Parse(incomplete, palette).Text == incomplete,
            "A long incomplete sequence remains literal, without exponential SGR backtracking.");
        Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Incomplete SGR scanning remains bounded for a 100k-character fixture.");
        return Task.CompletedTask;
    }

    public static Task ThemeAdaptationAndDoubleDimAsync()
    {
        GitDiffColorPalette palette = Palette();
        Check(Colored("31", palette, true).Background == new GitDiffColor(255, 230, 230), "Difftastic normal foreground becomes a dim theme background.");
        Check(Colored("1;31", palette, true).Background == palette.Backgrounds[1], "Difftastic bold foreground becomes the normal theme background.");
        Check(Colored("1;91", palette, true).Background == palette.Backgrounds[9], "Difftastic extra-bold foreground becomes the bold theme background.");
        Check(Colored("2;31", palette, true).Background == palette.EditorBackground
            && Colored("7;31", palette, true).Background == palette.Backgrounds[1], "Explicit dim/reverse are not passed through foreground adaptation.");
        const string removed = "-identical_part_before_RemovedX_identical_part_after";
        const string added = "+identical_part_before_AddedY_identical_part_after";
        string text = removed + "\n" + added;
        var descriptors = Describe(text);
        var spans = GitDiffInlineHighlighter.Highlight(text, descriptors, palette).OrderBy(span => span.Offset).ToArray();
        CheckOffsets(spans, [(1, 22), (31, 21), (removed.Length + 2, 22), (removed.Length + 2 + 22 + 6, 21)]);
        Check(spans[0].Background == new GitDiffColor(255, 243, 243) && spans[0].Foreground == new GitDiffColor(0, 0, 0)
            && spans[2].Background == new GitDiffColor(243, 255, 243), "Identical patch segments dim gamma-correct backgrounds twice.");
        spans = [.. GitDiffInlineHighlighter.Highlight(text, descriptors, palette, dimBackground: false).OrderBy(span => span.Offset)];
        Check(spans[0].Background == palette.EditorBackground && spans[0].Foreground == new GitDiffColor(255, 197, 197), "Non-reverse Git mode dims foreground once on the editor background.");
        foreach (bool dark in new[] { false, true })
        {
            var p = Palette(dark); var actual = GitDiffInlineHighlighter.Highlight(text, descriptors, p);
            Check(actual.All(span => span.Background != p.Backgrounds[1] && span.Background != p.Backgrounds[2]), "Both original themes attenuate unchanged intraline segments.");
        }
        return Task.CompletedTask;
    }

    public static Task InlineAnchorsAndPrefixOffsetsAsync()
    {
        const string removed = "-deletion identical_part_before RemovedX identical_part_after ";
        const string added = "+ identical_part_before AddedY identical_part_after insertion";
        GitDiffColorPalette palette = Palette();
        string text = removed + "\r\n" + added;
        var spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        var anchors = spans.Where(span => span.IsAnchor).OrderBy(span => span.Offset).ToArray();
        Check(anchors.Length == 2 && anchors[0] is { Length: 0, Background: null }
            && anchors[0].Offset == removed.Length && anchors[0].Foreground == palette.Foregrounds[10]
            && anchors[1].Offset == removed.Length + 3 && anchors[1].Foreground == palette.Foregrounds[9], "Original insertion/deletion anchors carry zero length and opposite-line stroke colors.");
        string combined = "--😀 camelCase \r\n++😀 DelphiCase ";
        int second = combined.IndexOf("++", StringComparison.Ordinal);
        GitDiffLineDescriptor[] descriptors = [new(0, second - 2, GitDiffLineKind.Removed, 2), new(second, combined.Length - second, GitDiffLineKind.Added, 2)];
        spans = GitDiffInlineHighlighter.Highlight(combined, descriptors, palette);
        Check(spans.Any(span => span.Offset == 2 && span.Length == 3) && spans.Any(span => span.Offset == second + 2 && span.Length == 3), "Combined columns exclude two prefixes and use UTF16 emoji offsets.");
        Check(spans.Any(span => combined.Substring(span.Offset, span.Length) == "Case "), "Camel-case fallback preserves the longest identical subword.");
        string range = "     -😀 abc\n     +😀 abcd";
        second = range.IndexOf("     +", StringComparison.Ordinal);
        descriptors = [new(0, second - 1, GitDiffLineKind.Removed, 6), new(second, range.Length - second, GitDiffLineKind.Added, 6)];
        spans = GitDiffInlineHighlighter.Highlight(range, descriptors, palette);
        Check(spans.Any(span => span.IsAnchor && span.Offset == second - 1) && spans.Where(span => !span.IsAnchor).All(span => span.Offset is >= 6), "Nested range prefixes do not shift insertion anchors into the prefix.");
        Check(range == "     -😀 abc\n     +😀 abcd", "The highlighter never modifies canonical text.");
        return Task.CompletedTask;
    }

    public static Task OrderedLineMatchingAsync()
    {
        GitDiffColorPalette palette = Palette();
        string text = "-r0\n- trimmed line\t\n-r2\n+a0\n+a1\n+a2\n+trimmed line\n+a4";
        var spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        Check(spans.Count(span => span.Length == "trimmed line".Length) == 2
            && spans.Any(span => text.Substring(span.Offset, span.Length) == "trimmed line" && span.Offset == text.IndexOf("+trimmed line", StringComparison.Ordinal) + 1), "The original trimmed-line fixture pairs removed line1 with added line3, skipping intervening additions.");
        Check(!spans.Any(span => span.Offset >= text.IndexOf("+a1", StringComparison.Ordinal) && span.Offset < text.IndexOf("+trimmed line", StringComparison.Ordinal)), "Unpaired added lines do not inherit a transitive identity or false intraline match.");
        text = "-line 0 had some words\n-line 1 had some more common words\n- trimmed line\t\n-r2\n+some words\n+line 1 has some words\n+line 2 has some common words\n+trimmed line\n+a4";
        spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        Check(!spans.Any(span => span.Offset >= text.IndexOf("+some words", StringComparison.Ordinal) && span.Offset < text.IndexOf("+line 1", StringComparison.Ordinal))
            && spans.Any(span => text.Substring(span.Offset, span.Length).Contains("common words", StringComparison.Ordinal)), "Original word-score fixture chooses longest summed common words and ordered remaining matches.");
        text = "-long alpha\n-short beta\n+short beta\n+long alpha";
        spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        Check(spans.Count == 2 && spans.All(span => text.Substring(span.Offset, span.Length) == "long alpha"), "Reversed equal lines remain an ordered pairwise match, never a crossing/transitive equivalence.");
        text = "-multiple__under___scores_\n+different__under___scores_";
        spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        Check(spans.Count == 2 && spans.All(span => text.Substring(span.Offset, span.Length) == "__under___scores_"), "Snake-case subwords retain repeated underscores in the final identical suffix.");
        return Task.CompletedTask;
    }

    public static Task InlineBoundsAndBlocksAsync()
    {
        GitDiffColorPalette palette = Palette();
        string Make(int additions)
        {
            return string.Join('\n', Enumerable.Range(0, 10).Select(index => "-line" + index.ToString(CultureInfo.InvariantCulture))
                .Concat(Enumerable.Range(0, additions).Select(index => index is >= 10 and < 20 ? "+line" + (index - 10).ToString(CultureInfo.InvariantCulture) : "+~~~")));
        }
        string text = Make(1000);
        var spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        Check(spans.Count == 20 && spans.All(span => text.Substring(span.Offset, span.Length).StartsWith("line", StringComparison.Ordinal)), "At exactly 10,000 combinations real matching still searches identical later additions.");
        text = Make(1001);
        Check(GitDiffInlineHighlighter.Highlight(text, Describe(text), palette).Count == 0, "Above 10,000 combinations the original linear zip fallback avoids the later matches.");
        text = "-" + new string('a', 2001) + "X\n+" + new string('a', 2001) + "Y";
        spans = GitDiffInlineHighlighter.Highlight(text, Describe(text), palette);
        Check(spans.Count == 2 && spans.All(span => span.Length == 2000), "Only the first 2,000 UTF16 content characters participate in intraline work.");
        text = "-same old\n" + string.Concat(Enumerable.Repeat(" context\n", 5)) + "+same new";
        Check(GitDiffInlineHighlighter.Highlight(text, Describe(text), palette).Count == 2, "Up to five context lines may separate the original removed/added block.");
        text = "-same old\n" + string.Concat(Enumerable.Repeat(" context\n", 10)) + "+same new";
        Check(GitDiffInlineHighlighter.Highlight(text, Describe(text), palette).Count == 2, "The two original block searches can each cross five context lines.");
        text = "-same old\n" + string.Concat(Enumerable.Repeat(" context\n", 11)) + "+same new";
        Check(GitDiffInlineHighlighter.Highlight(text, Describe(text), palette).Count == 0, "Beyond two five-line gaps the removed/added pair is not formed.");
        text = "-same old\n+same new";
        var descriptors = Describe(text).Select(line => line with { IsMoved = true }).ToArray();
        Check(GitDiffInlineHighlighter.Highlight(text, descriptors, palette).Count == 0, "Moved lines are excluded from intraline matching.");
        ExpectArgument(() => GitDiffInlineHighlighter.Highlight(text, [new(0, text.Length + 1, GitDiffLineKind.Removed)], palette));
        ExpectArgument(() => GitDiffInlineHighlighter.Highlight(text, [new(0, 1, GitDiffLineKind.Added, 2)], palette));
        ExpectArgument(() => GitAnsiColorParser.Parse(text, palette with { Foregrounds = [] }));
        Check(GitDiffInlineHighlighter.Highlight(text, [], palette).Count == 0, "Plain source/header-only callers have no inferred diff markers.");
        return Task.CompletedTask;
    }

    public static Task OriginalGapFixtureAsync()
    {
        string text = File.ReadAllText(Original("gaps.diff")).ReplaceLineEndings("\n");
        GitDiffLineDescriptor[] descriptors = Describe(text);
        var spans = GitDiffInlineHighlighter.Highlight(text, descriptors, Palette());
        var anchors = spans.Where(span => span.IsAnchor).OrderBy(span => span.Offset).ToArray();
        Check(anchors.Length >= 2 && anchors.Any(span => span.Offset == 1592) && anchors.Any(span => span.Offset == 1648),
            "Original gaps.diff removed-only braces and return produce anchors on their empty added lines.");
        Check(spans.All(span => span.Offset >= 0 && span.Offset + span.Length <= text.Length)
            && spans.All(span => descriptors.Any(line => span.Offset >= line.Offset + line.ContentOffset && span.Offset + span.Length <= line.Offset + line.Length)), "All original fixture markers stay inside a parsed content line, never its patch prefixes or metadata.");
        return Task.CompletedTask;
    }

    private static GitDiffColorSpan Colored(string codes, GitDiffColorPalette palette, bool theme = false)
        => GitAnsiColorParser.Parse($"\u001b[{codes}mx\u001b[m", palette, theme).Spans.Single();
    private static GitDiffLineDescriptor[] Describe(string text)
    {
        List<GitDiffLineDescriptor> result = []; int offset = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            GitDiffLineKind kind = line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) ? GitDiffLineKind.Added
                : line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal) ? GitDiffLineKind.Removed
                : line.StartsWith(' ') ? GitDiffLineKind.Context : GitDiffLineKind.Header;
            result.Add(new(offset, line.Length, kind)); offset += raw.Length + 1;
        }
        return [.. result];
    }
    internal static GitDiffColorPalette Palette(bool dark = false)
    {
        string root = Root();
        string css = File.ReadAllText(Path.Combine(root, "src/app/GitUI/Themes/invariant.css"));
        if (dark) { css += File.ReadAllText(Path.Combine(root, "src/app/GitUI/Themes/dark.css")); }
        string[] names = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];
        GitDiffColor[] fore = new GitDiffColor[16], back = new GitDiffColor[16];
        for (int index = 0; index < 16; index++)
        {
            string prefix = "AnsiTerminal" + names[index % 8], suffix = index < 8 ? "Normal" : "Bold";
            fore[index] = Css(prefix + "Fore" + suffix); back[index] = Css(prefix + "Back" + suffix);
        }
        return new(fore, back, Css("EditorBackground"), dark ? new(240, 240, 240) : new(0, 0, 0));
        GitDiffColor Css(string name)
        {
            MatchCollection matches = Regex.Matches(css, @"\." + name + @"\s*\{\s*color:\s*#([a-fA-F0-9]{6});");
            string rgb = matches[^1].Groups[1].Value;
            return new(byte.Parse(rgb.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(rgb.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), byte.Parse(rgb.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
    }
    internal static string Original(string filename) => Path.Combine(Root(), "tests/app/UnitTests/GitUI.Tests/Editor/Diff", filename);
    private static string Root()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        { if (File.Exists(Path.Combine(directory.FullName, "src/app/GitUI/Editor/Diff/LinesMatcher.cs"))) { return directory.FullName; } }
        throw new InvalidOperationException("Original diff fixtures were not found in this checkout.");
    }
    private static void CheckOffsets(IReadOnlyList<GitDiffColorSpan> actual, (int Offset, int Length)[] expected)
        => Check(actual.Select(span => (span.Offset, span.Length)).SequenceEqual(expected),
            "Expected original offsets " + string.Join(", ", expected) + "; actual " + string.Join(", ", actual.Select(span => (span.Offset, span.Length))));
    private static void ExpectArgument(Action action)
    { try { action(); throw new InvalidOperationException("Invalid segment/palette was accepted."); } catch (ArgumentException) { } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
