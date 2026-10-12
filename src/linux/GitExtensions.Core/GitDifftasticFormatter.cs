using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record GitDifftasticLine(int Offset, int Length, int? OldLine, int? NewLine, GitDiffLineKind Kind)
{
    /// <summary>Includes original MinusPlus lines, whose common line kind is Context.</summary>
    public bool IsChanged { get; init; }
}
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
        List<GitDiffColorSpan> resultSpans = []; List<GitDifftasticLine> lines = [];
        GitDiffColorSpan[] sourceSpans = [.. input.Spans.OrderBy(span => span.Offset)]; int sourceSpanIndex = 0;
        StringBuilder output = new(input.Text.Length); int half = width / 2, rightColumn = 0, sourceOffset = 0;
        bool nextHeader = true;
        foreach (string raw in input.Text.Split('\n'))
        {
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
            int leftLength;
            if (left.Groups["matchStart"].Index >= half)
            { leftLength = rightColumn > 0 ? Math.Max(0, half - rightColumn) : 0; }
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
            int rightOffset = Math.Max(0, half - leftLength);
            Match right = line.Length > rightOffset ? LineNumberRegex().Match(line.ToString(rightOffset, line.Length - rightOffset)) : Match.Empty;
            if (right.Success)
            { if (rightColumn == 0) { rightColumn = rightOffset; } }
            else { rightOffset = 0; right = LineNumberRegex().Match(line.ToString()); }
            if (!right.Success) { Add(); continue; }
            newLine = Number(right);
            GitDiffColorSpan? firstRight = spans.FirstOrDefault(span => span.Offset + span.Length > rightOffset);
            if (firstRight is not null && Changed(firstRight))
            { kind = kind == GitDiffLineKind.Context ? GitDiffLineKind.Added : GitDiffLineKind.Context; changed = true; }
            Remove(rightOffset, right.Length);
            if (rightOffset > 0)
            {
                if (rightColumn == 0) { rightColumn = rightOffset; }
                int gap = rightColumn - rightOffset;
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
}
