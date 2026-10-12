using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

/// <summary>Portable AnsiEscapeUtilities SGR decoding. Offsets refer to the returned UTF16 text.</summary>
public static class GitAnsiColorParser
{
    /// <summary>Strip only complete SGR sequences, keeping malformed sequences and all other source text.</summary>
    public static string StripAnsi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder output = new(text.Length); int previous = 0;
        foreach (var sequence in Sequences(text))
        { output.Append(text.AsSpan(previous, sequence.Offset - previous)); previous = sequence.End; }
        output.Append(text.AsSpan(previous)); return output.ToString();
    }

    public static GitColoredText Parse(string text, GitDiffColorPalette palette, bool themeColors = false)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(palette); palette.Validate();
        StringBuilder output = new(text.Length);
        List<GitDiffColorSpan> spans = [];
        int previous = 0, start = -1, currentColor = GitDiffColorMath.IsDark(palette) ? 7 : 0;
        GitDiffColor? background = null, foreground = null;
        foreach (var sequence in Sequences(text))
        {
            output.Append(text.AsSpan(previous, sequence.Offset - previous)); previous = sequence.End;
            Finish();
            if (TryColors(sequence.Codes, palette, ref currentColor, themeColors, out background, out foreground)) { start = output.Length; }
        }
        output.Append(text.AsSpan(previous)); Finish();
        return new(output.ToString(), spans);

        void Finish()
        {
            if (start < 0) { return; }
            int length = output.Length - start;
            if (length > 0 && !(length == 1 && output[start] == '\r'))
            {
                GitDiffColorSpan span = new(start, length, background ?? palette.EditorBackground, foreground);
                if (spans.Count > 0 && spans[^1].Background == span.Background && spans[^1].Foreground == span.Foreground)
                {
                    GitDiffColorSpan last = spans[^1]; int gap = start - last.Offset - last.Length;
                    if (gap == 0 || gap == 1 && (output[start - 1] is '\r' or '\n') || gap == 2 && output[start - 2] == '\r' && output[start - 1] == '\n')
                    { spans[^1] = last with { Length = last.Length + gap + length }; start = -1; return; }
                }
                spans.Add(span);
            }
            start = -1;
        }
    }

    // Same grammar as AnsiEscapeUtilities' ESC\[((\d+)\s*[:;]?\s*)*m.
    // A scanner avoids the nested-quantifier backtracking on incomplete source sequences.
    private static IEnumerable<(int Offset, int End, IReadOnlyList<int> Codes)> Sequences(string text)
    {
        int search = 0;
        while (search < text.Length)
        {
            int offset = text.IndexOf('\u001b', search);
            if (offset < 0) { yield break; }
            search = offset + 1;
            if (search == text.Length || text[search] != '[') { continue; }
            int cursor = search + 1; List<int> codes = [];
            while (cursor < text.Length)
            {
                if (text[cursor] == 'm')
                { search = cursor + 1; yield return (offset, search, codes); break; }
                int start = cursor;
                while (cursor < text.Length && char.IsDigit(text[cursor])) { cursor++; }
                if (cursor == start) { break; }
                codes.Add(int.TryParse(text.AsSpan(start, cursor - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0);
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) { cursor++; }
                if (cursor < text.Length && text[cursor] is ':' or ';') { cursor++; }
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) { cursor++; }
            }
        }
    }

    private static bool TryColors(IReadOnlyList<int> codes, GitDiffColorPalette palette, ref int currentColor,
        bool themeColors, out GitDiffColor? background, out GitDiffColor? foreground)
    {
        background = foreground = null;
        int fore = -1, back = -1, defaultColor = GitDiffColorMath.IsDark(palette) ? 7 : 0;
        if (codes.Count == 0) { currentColor = defaultColor; return false; }
        bool reverse = false, bold = false, dim = false, change = false;
        for (int index = 0; index < codes.Count; index++)
        {
            switch (codes[index])
            {
                case 0: reverse = bold = dim = false; background = foreground = null; fore = back = -1; currentColor = defaultColor; break;
                case 1 or 4 or 5 or 6 or 9: bold = change = true; break;
                case 2 or 3 or 8: dim = change = true; break;
                case 7: reverse = true; break;
                case 22: bold = dim = false; break;
                case 39: foreground = null; fore = currentColor = defaultColor; break;
                case 49: background = null; back = -1; change = true; break;
                case >= 30 and <= 37: fore = codes[index] - 30; break;
                case >= 90 and <= 97: fore = codes[index] - 90 + 8; break;
                case >= 40 and <= 47: back = codes[index] - 40; break;
                case >= 100 and <= 107: back = codes[index] - 100 + 8; break;
                case 38 or 48:
                    bool isForeground = codes[index] == 38;
                    if (index + 2 >= codes.Count) { break; }
                    int mode = codes[++index];
                    if (mode == 5)
                    {
                        int value = codes[++index];
                        if (value is >= 0 and <= 255) { if (isForeground) { fore = value; } else { back = value; } }
                    }
                    else if (mode == 2 && index + 3 < codes.Count)
                    {
                        int r = codes[index + 1], g = codes[index + 2], b = codes[index + 3]; index += 3;
                        if (r is >= 0 and <= 255 && g is >= 0 and <= 255 && b is >= 0 and <= 255)
                        {
                            GitDiffColor color = new((byte)r, (byte)g, (byte)b); currentColor = defaultColor;
                            if (isForeground) { fore = -1; foreground = color; } else { back = -1; background = color; }
                        }
                    }
                    break;
            }
        }
        if (fore is >= 0 and <= 15) { currentColor = fore & 7; }
        if (themeColors && !reverse && !dim && back < 0 && background is null && foreground is null && fore is 1 or 2 or 9 or 10)
        {
            bool backBold = bold, backDim = false;
            if (fore > 7) { fore -= 8; } else if (bold) { backBold = false; } else { backDim = true; }
            background = Color(fore, false, backBold, backDim, palette); fore = -1;
        }
        if (change && foreground is null && background is null && fore < 0 && back < 0) { fore = currentColor; }
        if (reverse) { (background, foreground) = (foreground, background); (back, fore) = (fore, back); }
        if (fore >= 0) { foreground = Color(fore, true, bold, dim, palette); }
        if (back >= 0) { background = Color(back, false, bold, dim, palette); }
        if (background is GitDiffColor backColor && foreground is null) { foreground = GitDiffColorMath.TextColor(backColor, palette.EditorForeground); }
        return background is not null || foreground is not null;
    }

    private static GitDiffColor Color(int code, bool foreground, bool bold, bool dim, GitDiffColorPalette palette)
    {
        bool extraBold = bold && code is >= 8 and <= 15;
        if (bold && code < 8) { code += 8; }
        GitDiffColor color = code < 16 ? (foreground ? palette.Foregrounds[code] : palette.Backgrounds[code])
            : code < 232 ? new((byte)((code - 16) / 36 * 51), (byte)(((code - 16) % 36) / 6 * 51), (byte)((code - 16) % 6 * 51))
            : new((byte)((code - 232) * 11), (byte)((code - 232) * 11), (byte)((code - 232) * 11));
        if (extraBold) { color = GitDiffColorMath.Brighten(color); }
        if (dim) { color = GitDiffColorMath.Dim(color, palette.EditorBackground); }
        return color;
    }
}
