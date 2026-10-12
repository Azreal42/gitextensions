using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record GitDifftasticLine(int Offset, int Length, int? OldLine, int? NewLine, GitDiffLineKind Kind)
{
    /// <summary>Includes original MinusPlus lines, whose common line kind is Context.</summary>
    public bool IsChanged { get; init; }
}
/// <summary>Line/span offsets are UTF16; RightColumnStart is the terminal-cell coordinate of the rendered right column.</summary>
public sealed record GitDifftasticText(GitColoredText Content, IReadOnlyList<GitDifftasticLine> Lines, int RightColumnStart);

/// <summary>Portable DifftasticHighlightService layout: move textual numbers to gutter metadata,
/// retain ANSI colors, discard section separators and align the right-hand column.</summary>
public static partial class GitDifftasticFormatter
{
    [GeneratedRegex(@"^(\s*(?<matchStart>(?<lineNo>\d+)|(\.+)) )", RegexOptions.ExplicitCapture)]
    private static partial Regex LineNumberRegex();

    public static GitDifftasticText Format(GitColoredText input, int width, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.Text); ArgumentNullException.ThrowIfNull(input.Spans);
        if (width <= 0) { throw new ArgumentOutOfRangeException(nameof(width), "Difftastic width must be positive."); }
        foreach (GitDiffColorSpan span in input.Spans)
        {
            if (span.Offset < 0 || span.Offset > input.Text.Length || span.Length < 0 || span.Length > input.Text.Length - span.Offset)
            { throw new ArgumentException("Difftastic colors must refer to valid UTF16 text segments.", nameof(input)); }
        }
        // A color-never one-column file can be byte-for-byte indistinguishable
        // from a side-by-side grid containing consecutive numeric code tokens.
        // Preserve the tool's text; geometric inference cannot prove gutters.
        if (input.Spans.Count == 0) { return Plain(input); }
        List<GitDiffColorSpan> resultSpans = []; List<GitDifftasticLine> lines = [];
        GitDiffColorSpan[] sourceSpans = [.. input.Spans.OrderBy(span => span.Offset)]; int sourceSpanIndex = 0;
        string[] rawLines = input.Text.Split('\n');
        // DFT_WIDTH is a ceiling. Difftastic 0.71 uses the widest visible source
        // line (at least 25 cells), and can therefore emit a much narrower grid.
        ColumnLayout[] layouts = FindLayouts(rawLines, sourceSpans, width);
        bool adaptive = Enumerable.Range(0, rawLines.Length).Any(row =>
        {
            int column = layouts[row].Column;
            if (column <= 0) { return false; }
            int index = IndexAtCell(rawLines[row], column);
            return column != width / 2 || index < rawLines[row].Length && index != column;
        });
        StringBuilder output = new(input.Text.Length); int half = width / 2,
            rightColumn = adaptive ? layouts.Max(layout => Math.Max(0, layout.Column - layout.LeftWidth)) : 0, sourceOffset = 0;
        bool nextHeader = true;
        for (int row = 0; row < rawLines.Length; row++)
        {
            string raw = rawLines[row]; ColumnLayout layout = layouts[row];
            half = layout.Column > 0 ? layout.Column : width / 2;
            List<GitDiffColorSpan> spans = [];
            while (sourceSpanIndex < sourceSpans.Length && sourceSpans[sourceSpanIndex].Offset + sourceSpans[sourceSpanIndex].Length <= sourceOffset)
            { sourceSpanIndex++; }
            for (int spanIndex = sourceSpanIndex; spanIndex < sourceSpans.Length && sourceSpans[spanIndex].Offset < sourceOffset + raw.Length; spanIndex++)
            {
                GitDiffColorSpan span = sourceSpans[spanIndex];
                int start = Math.Max(sourceOffset, span.Offset), end = Math.Min(sourceOffset + raw.Length, span.Offset + span.Length);
                if (end > start) { spans.Add(span with { Offset = start - sourceOffset, Length = end - start }); }
            }
            sourceOffset += raw.Length + 1;
            if (string.IsNullOrWhiteSpace(raw) && spans.Count == 0) { nextHeader = true; continue; }
            spans = [.. spans.OrderBy(span => span.Offset)];
            StringBuilder line = new(raw); int? oldLine = null, newLine = null;
            GitDiffLineKind kind = nextHeader ? GitDiffLineKind.Header : GitDiffLineKind.Context; bool changed = false;
            if (nextHeader) { nextHeader = false; Add(); continue; }
            Match left = LineNumberRegex().Match(line.ToString());
            if (!left.Success) { Add(); continue; }
            int rawRightOffset = adaptive ? IndexAtCell(raw, half) : half;
            Match rawRight = rawRightOffset < raw.Length ? LineNumberRegex().Match(raw[rawRightOffset..]) : Match.Empty;
            bool rightProven = layout.Column > 0 && rawRight.Success && PrefixColored(spans, rawRightOffset, rawRight.Length);
            if (!PrefixColored(spans, 0, left.Length) && !rightProven) { Add(); continue; }
            int leftLength;
            if (CellCount(raw.AsSpan(0, left.Groups["matchStart"].Index)) >= half)
            { leftLength = adaptive && layout.LeftWidth > 0 ? layout.LeftWidth : rightColumn > 0 ? Math.Max(0, half - rightColumn) : 0; }
            else
            {
                leftLength = left.Length; oldLine = Number(left);
                if (spans.Count > 0 && spans[0].Offset < leftLength && Changed(spans[0]))
                {
                    changed = true;
                    GitDiffColor color = (reverse ? spans[0].Background : spans[0].Foreground)!.Value;
                    if (color.R > color.G) { kind = GitDiffLineKind.Removed; }
                    else { kind = GitDiffLineKind.Added; newLine = oldLine; oldLine = null; }
                }
            }
            if (leftLength > 0) { Remove(0, leftLength); }
            int rightOffset = Math.Max(0, (adaptive ? IndexAtCell(raw, half) : half) - leftLength);
            Match right = line.Length > rightOffset ? LineNumberRegex().Match(line.ToString(rightOffset, line.Length - rightOffset)) : Match.Empty;
            if (!rightProven) { right = Match.Empty; }
            if (right.Success)
            { if (rightColumn == 0) { rightColumn = rightOffset; } }
            else
            {
                rightOffset = 0; right = LineNumberRegex().Match(line.ToString());
                // A one-column file can itself begin with a number. Only compact
                // paired gutter prefixes may be stripped a second time.
                if (right.Success && !left.Groups["lineNo"].Success)
                { /* A missing-left ellipsis followed immediately by the right gutter. */ }
                else if (right.Success && (kind == GitDiffLineKind.Added
                    || !spans.Any(span => span.Offset == 0 && span.Length == right.Length
                        && (!Changed(span) || (reverse ? span.Background : span.Foreground)!.Value.R < (reverse ? span.Background : span.Foreground)!.Value.G))))
                { right = Match.Empty; }
            }
            if (!right.Success) { Add(); continue; }
            newLine = Number(right);
            GitDiffColorSpan? firstRight = spans.FirstOrDefault(span => span.Offset + span.Length > rightOffset);
            if (firstRight is not null && Changed(firstRight))
            { kind = kind == GitDiffLineKind.Context ? GitDiffLineKind.Added : GitDiffLineKind.Context; changed = true; }
            Remove(rightOffset, right.Length);
            if (rightOffset > 0)
            {
                if (rightColumn == 0) { rightColumn = rightOffset; }
                int gap = rightColumn - (adaptive ? CellCount(line.ToString().AsSpan(0, rightOffset)) : rightOffset);
                if (gap > 0)
                {
                    line.Insert(rightOffset, new string(' ', gap));
                    List<GitDiffColorSpan> aligned = [];
                    foreach (GitDiffColorSpan span in spans)
                    {
                        if (span.Offset >= rightOffset) { aligned.Add(span with { Offset = span.Offset + gap }); }
                        else if (span.Offset + span.Length > rightOffset)
                        {
                            // Padding is uncolored; retain colors on both original text fragments.
                            int prefix = rightOffset - span.Offset;
                            aligned.Add(span with { Length = prefix });
                            aligned.Add(span with { Offset = rightOffset + gap, Length = span.Length - prefix });
                        }
                        else { aligned.Add(span); }
                    }
                    spans = aligned;
                }
            }
            Add();

            void Remove(int offset, int length)
            {
                line.Remove(offset, length); int gapEnd = offset + length;
                spans = [.. spans.Select(span =>
                {
                    if (span.Offset + span.Length <= offset) { return span; }
                    if (span.Offset >= gapEnd) { return span with { Offset = span.Offset - length }; }
                    int start = Math.Min(span.Offset, offset), end = span.Offset + span.Length;
                    end -= Math.Min(end - offset, length);
                    return span with { Offset = start, Length = Math.Max(0, end - start) };
                }).Where(span => span.Length > 0)];
            }
            void Add()
            {
                lines.Add(new(output.Length, line.Length, oldLine, newLine, kind) { IsChanged = changed });
                resultSpans.AddRange(spans.Select(span => span with { Offset = span.Offset + output.Length }));
                output.Append(line); output.Append('\n');
            }
        }
        return new(new(output.ToString(), resultSpans), lines, rightColumn);

        static int? Number(Match match) => match.Groups["lineNo"].Success
            && int.TryParse(match.Groups["lineNo"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
        bool Changed(GitDiffColorSpan span)
        { GitDiffColor? color = reverse ? span.Background : span.Foreground; return color is GitDiffColor rgb && rgb.R != rgb.G; }
    }

    private static bool PrefixColored(IEnumerable<GitDiffColorSpan> spans, int offset, int length) =>
        spans.Any(span => span.Offset <= offset && span.Offset + span.Length >= offset + length);

    private static GitDifftasticText Plain(GitColoredText input)
    {
        List<GitDifftasticLine> lines = []; int offset = 0; bool header = true;
        string[] rows = input.Text.Split('\n');
        for (int row = 0; row < rows.Length; row++)
        {
            string text = rows[row];
            if (row == rows.Length - 1 && text.Length == 0) { break; }
            bool blank = string.IsNullOrWhiteSpace(text);
            lines.Add(new(offset, text.Length, null, null, !blank && header ? GitDiffLineKind.Header : GitDiffLineKind.Context));
            header = blank; offset += text.Length + 1;
        }
        return new(input, lines, 0);
    }

    private readonly record struct ColumnLayout(int Column, int LeftWidth);

    private static ColumnLayout[] FindLayouts(string[] rows, GitDiffColorSpan[] spans, int width)
    {
        ColumnLayout[] layouts = new ColumnLayout[rows.Length]; int start = 0, sourceOffset = 0, spanCursor = 0;
        while (start < rows.Length)
        {
            if (string.IsNullOrWhiteSpace(rows[start])) { sourceOffset += rows[start++].Length + 1; continue; }
            int end = start + 1;
            while (end < rows.Length && !string.IsNullOrWhiteSpace(rows[end])) { end++; }
            List<(int Column, int Row, int LeftWidth)> colored = [];
            int offset = sourceOffset + rows[start].Length + 1;
            int minimum = Math.Min(width / 2, 29), maximum = width / 2 + 3;
            for (int row = start + 1; row < end; row++)
            {
                string text = rows[row]; Match left = LineNumberRegex().Match(text);
                int leftWidth = left.Success && left.Groups["matchStart"].Index < minimum ? left.Length : 0;
                int scanEnd = IndexAtCell(text, maximum + 10);
                int[] columns = CellPositions(text.AsSpan(0, scanEnd));
                while (spanCursor < spans.Length && spans[spanCursor].Offset + spans[spanCursor].Length <= offset) { spanCursor++; }
                GitDiffColorSpan? prefix = spanCursor < spans.Length ? spans[spanCursor] : null;
                bool prefixProven = prefix is not null && prefix.Offset == offset
                    && (leftWidth > 0 && prefix.Length >= leftWidth
                        || prefix.Length > 0 && prefix.Length <= text.Length && text.AsSpan(0, prefix.Length).Trim().IsEmpty);
                for (int spanIndex = spanCursor; spanIndex < spans.Length && spans[spanIndex].Offset < offset + scanEnd; spanIndex++)
                {
                    if (!prefixProven) { break; }
                    GitDiffColorSpan span = spans[spanIndex];
                    int index = span.Offset - offset;
                    if (index < 0 || index >= scanEnd) { continue; }
                    int column = columns[index];
                    if (column < minimum || column > maximum || index < 2 || text[index - 1] != ' ' || text[index - 2] != ' ') { continue; }
                    Match marker = LineNumberRegex().Match(text.Substring(index, scanEnd - index));
                    if (marker.Success && marker.Length <= span.Length && span.Offset + marker.Length <= offset + text.Length)
                    { colored.Add((column, row, leftWidth)); }
                }
                offset += text.Length + 1;
            }
            ColumnLayout layout = default;
            var strong = colored.GroupBy(candidate => candidate.Column).OrderByDescending(group => group.Select(candidate => candidate.Row).Distinct().Count()).FirstOrDefault();
            if (strong is not null) { layout = new(strong.Key, strong.Max(candidate => candidate.LeftWidth)); }
            for (int row = start; row < end; row++) { layouts[row] = layout; sourceOffset += rows[row].Length + 1; }
            start = end;
        }
        return layouts;

    }

    private static int[] CellPositions(ReadOnlySpan<char> text)
    {
        int[] columns = new int[text.Length + 1]; int cells = 0, index = 0;
        while (index < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text[index..]);
            for (int part = 0; part < length; part++) { columns[index + part] = cells; }
            cells += ElementWidth(text.Slice(index, length)); index += length;
        }
        columns[index] = cells; return columns;
    }

    // ANSI spans and line offsets remain UTF16. Grid positions use terminal cells,
    // as Difftastic's unicode_width padding does (including CJK and emoji).
    private static int CellCount(ReadOnlySpan<char> text)
    {
        int cells = 0, index = 0;
        while (index < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text[index..]);
            cells += ElementWidth(text.Slice(index, length)); index += length;
        }
        return cells;
    }

    private static int ElementWidth(ReadOnlySpan<char> text)
    {
        int width = 0, regionalIndicators = 0; bool emojiPresentation = false, emojiBase = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is >= 0x1f1e6 and <= 0x1f1ff) { regionalIndicators++; }
            if (value is 0xfe0f or 0x20e3) { emojiPresentation = true; }
            if (value is >= 0x2300 and <= 0x27ff or >= 0x1f000 and <= 0x1faff or >= '0' and <= '9' or '#' or '*') { emojiBase = true; }
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format || value < 32) { continue; }
            int cells = value is >= 0x1100 and <= 0x115f or >= 0x2329 and <= 0x232a
                or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
                or >= 0xfe10 and <= 0xfe19 or >= 0xfe30 and <= 0xfe6f or >= 0xff01 and <= 0xff60
                or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd ? 2 : 1;
            width = Math.Max(width, cells);
        }
        return regionalIndicators >= 2 || emojiPresentation && emojiBase ? 2 : width;
    }

    private static int IndexAtCell(string text, int column)
    {
        int cells = 0, index = 0;
        while (index < text.Length)
        {
            if (cells >= column) { return index; }
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            cells += ElementWidth(text.AsSpan(index, length)); index += length;
        }
        return text.Length;
    }
}
