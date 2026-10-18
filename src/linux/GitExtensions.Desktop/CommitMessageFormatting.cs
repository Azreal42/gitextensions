using System.Text.RegularExpressions;
using WindowsWordWrapper = GitUI.CommandsDialogs.CommitDialog.WordWrapper;

namespace GitExtensions.Linux.Desktop;

internal readonly record struct CommitMessageEdit(int Start, int Length, string Text);
internal readonly record struct CommitMessageLine(int Start, string Text);
internal readonly record struct CommitMessageWarning(string Item, string Source, string? Line = null);
internal sealed record CommitTemplateExpansion(string Text, string? Diagnostic);

internal static class CommitMessageFormatting
{
    internal static readonly IReadOnlyList<string> HeaderTypes = Array.AsReadOnly(new[] { "build", "chore", "ci", "docs", "feat", "fix", "perf", "refactor", "style", "test" });
    internal static readonly IReadOnlyList<string> FooterTypes = Array.AsReadOnly(new[] { "BREAKING CHANGE", "Co-authored-by", "Reviewed-by" });
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Placeholders = new(@"\{\{(?<pattern>.*?)\}\}(?:\[(?<index>\d+)\])?", RegexOptions.ExplicitCapture, RegexTimeout);

    internal static CommitTemplateExpansion Expand(CommitMessageTemplate template, string branch)
    {
        string message = template.Text;
        if (!template.IsRegex) { return new(message, null); }
        try
        {
            foreach (Match placeholder in Placeholders.Matches(message))
            {
                int group = int.TryParse(placeholder.Groups["index"].ValueSpan, out int number) ? number : 1;
                Match match = new Regex(placeholder.Groups["pattern"].Value, RegexOptions.None, RegexTimeout).Match(branch);
                string value = match.Success && match.Groups.Count > group ? match.Groups[group].Value : "";
                message = message.Replace(placeholder.Value, value, StringComparison.Ordinal);
            }
            return new(message, null);
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
        { return new(message, error.Message); } // Windows applies the text reached even when a later pattern fails.
    }

    internal static IReadOnlyList<CommitMessageLine> Lines(string text)
    {
        List<CommitMessageLine> lines = []; int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) { continue; }
            lines.Add(new(start, text[start..i]));
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
            start = i + 1;
        }
        lines.Add(new(start, text[start..])); return lines;
    }

    internal static IReadOnlyList<CommitMessageEdit> Format(string text, CommitMessagePreferences settings, bool manualWrap = false)
    {
        List<CommitMessageEdit> edits = []; IReadOnlyList<CommitMessageLine> lines = Lines(text);
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i]; string value = line.Text;
            bool insertBlank = !manualWrap && i == 1 && value.Length > 0 && settings.SecondLineMustBeEmpty;
            if (insertBlank && settings.IndentAfterFirstLine) { value = " - " + value; }
            int limit = manualWrap && settings.MaxLineLength == 0 ? 72 : settings.MaxLineLength;
            if ((manualWrap || settings.AutoWrap) && limit > 0 && value.Length > limit)
            { value = WindowsWordWrapper.WrapSingleLine(value, limit); }
            if (insertBlank) { value = Environment.NewLine + value; }
            if (value != line.Text) { edits.Add(new(line.Start, line.Text.Length, value)); }
        }
        return edits;
    }
    internal static int MapPosition(int position, IReadOnlyList<CommitMessageEdit> edits)
    {
        int delta = 0;
        foreach (var edit in edits)
        {
            if (position < edit.Start) { break; }
            if (position < edit.Start + edit.Length) { return edit.Start + delta + Math.Min(position - edit.Start, edit.Text.Length); }
            delta += edit.Text.Length - edit.Length;
        }
        return position + delta;
    }
    internal static IReadOnlyList<CommitMessageWarning> Warnings(string text, CommitMessagePreferences settings, out string? diagnostic)
    {
        diagnostic = null; List<CommitMessageWarning> warnings = [];
        var lines = Lines(text); string first = lines.FirstOrDefault(line => line.Text.Length > 0).Text ?? "";
        if (settings.MaxFirstLineLength > 0 && first.Length > settings.MaxFirstLineLength)
        { warnings.Add(new("_commitMsgFirstLineInvalid", "First line of commit message contains too many characters.\nDo you want to continue?")); }
        if (settings.MaxLineLength > 0)
        {
            foreach (var line in lines.Where(line => line.Text.Length > settings.MaxLineLength))
            { warnings.Add(new("_commitMsgLineInvalid", "The following line of commit message contains too many characters:\n\n{0}\n\nDo you want to continue?", line.Text)); }
        }
        // Preserve the source's >2-lines condition (not an invented >=2 gate).
        if (settings.SecondLineMustBeEmpty && lines.Count > 2 && lines[1].Text.Length > 0)
        { warnings.Add(new("_commitMsgSecondLineNotEmpty", "Second line of commit message is not empty.\nDo you want to continue?")); }
        if (settings.ValidationRegex.Length > 0 && !text.StartsWith("fixup!", StringComparison.Ordinal) && !text.StartsWith("squash!", StringComparison.Ordinal))
        {
            string candidate = text.StartsWith("amend!", StringComparison.Ordinal) && lines.Count > 2 && lines[1].Text.Length == 0
                ? text[lines[2].Start..] : text;
            try
            {
                if (!Regex.IsMatch(candidate, settings.ValidationRegex, RegexOptions.None, RegexTimeout))
                { warnings.Add(new("_commitMsgRegExNotMatched", "Commit message does not match RegEx.\nDo you want to continue?")); }
            }
            catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException) { diagnostic = error.Message; }
        }
        return warnings;
    }

    internal static (string Title, int Caret) PrefixKeyword(string message, int caret, string keyword, bool withScope)
    {
        string scope = withScope ? "()" : ""; int scopePosition = keyword.Length + 1, titlePosition = keyword.Length + scope.Length / 2 + 2;
        string title = string.IsNullOrWhiteSpace(message) ? "" : Lines(message)[0].Text;
        foreach (string key in HeaderTypes)
        {
            if (!title.StartsWith(key, StringComparison.Ordinal)) { continue; }
            if (title.Length == key.Length) { return ($"{keyword}{scope}: ", withScope ? scopePosition : titlePosition); }
            char next = title[key.Length];
            if (!withScope && next is ':' or '(' or '!') { return Replace(_ => titlePosition); }
            if (withScope && next is ':' or '!') { return ($"{keyword}(){title[key.Length..]}", scopePosition); }
            if (withScope && next == '(') { return Replace(value => 2 + Math.Max(value.IndexOf(':'), value.IndexOf('('))); }
            (string, int) Replace(Func<string, int> minimum)
            {
                string value = keyword + title[key.Length..]; int length = message.Length + value.Length - title.Length;
                return (value, Math.Min(length, Math.Max(minimum(value), caret + keyword.Length - key.Length)));
            }
        }
        return ($"{keyword}{scope}: {title}", withScope ? scopePosition : titlePosition + caret);
    }
}
