using System.Text;

namespace GitExtensions.Linux.Scripts;

/// <summary>Actual argument boundaries, separate from the historical display/preview text.</summary>
public sealed record ScriptArgumentPlan(IReadOnlyList<string> Arguments, string? Preview);
public sealed record ScriptExecutionPlan(string Command, string WorkingDirectory,
    ScriptArgumentPlan ArgumentPlan, ScriptTargetKind Target, bool RunInBackground);

/// <summary>Tracks RAW fragments and exact double-brace values before legacy Quote loses information.</summary>
internal sealed class ScriptArgumentPlanBuilder(string? source)
{
    private string _text = source ?? "";
    private readonly Dictionary<int, string[]> _opaque = [];
    private bool _invalid = source?.Contains('\0') == true;

    internal void Replace(string name, IReadOnlyList<string> exact, IReadOnlyList<string> raw)
    {
        _invalid |= exact.Concat(raw).Any(value => value.Contains('\0'));
        // Opaque values are never rescanned as RAW quoting or template syntax.
        string quoted = "{{" + name + "}}";
        if (_text.Contains(quoted, StringComparison.Ordinal))
        {
            int id = _opaque.Count;
            _opaque.Add(id, exact.ToArray());
            _text = _text.Replace(quoted, "\0" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0", StringComparison.Ordinal);
        }
        _text = _text.Replace("{" + name + "}", string.Join(' ', raw), StringComparison.Ordinal);
    }

    private sealed record Fragment(string? Text, string[]? Values);
    private sealed record ArgumentToken(IReadOnlyList<Fragment> Fragments, bool AuthorQuoted);

    internal bool TryBuild(string? preview, string? shellExecutable, out ScriptArgumentPlan? plan, out string? diagnostic)
    {
        plan = null; diagnostic = null;
        if (_invalid) { diagnostic = "Script arguments cannot contain NUL."; return false; }
        if (!Tokenize(out List<ArgumentToken> tokens, out diagnostic)) { return false; }
        int programIndex = IsPosixShell(shellExecutable) ? ShellProgramIndex(tokens) : -1;
        List<string> arguments = [];
        for (int index = 0; index < tokens.Count; index++)
        {
            ArgumentToken token = tokens[index];
            // A whole opaque program remains exact transport data, as do all non-program arguments.
            bool inline = token.Fragments.Count > 1 && token.Fragments.Any(fragment => fragment.Values is not null);
            if (index == programIndex && token.AuthorQuoted && inline)
            {
                if (!BuildShellProgram(token, out string program, out diagnostic)) { return false; }
                arguments.Add(program);
            }
            else { AppendArguments(token, arguments); }
        }
        plan = new(Array.AsReadOnly(arguments.ToArray()), preview); return true;
    }

    private bool Tokenize(out List<ArgumentToken> tokens, out string? diagnostic)
    {
        List<ArgumentToken> parsed = []; tokens = parsed; diagnostic = null;
        List<Fragment> fragments = []; StringBuilder literal = new(); bool started = false, authorQuoted = false; char quote = '\0';
        void FlushLiteral()
        {
            if (literal.Length > 0) { fragments.Add(new(literal.ToString(), null)); literal.Clear(); }
        }
        void Finish()
        {
            FlushLiteral();
            if (started || fragments.Count > 0) { parsed.Add(new(fragments.ToArray(), authorQuoted)); }
            fragments.Clear(); started = authorQuoted = false;
        }
        for (int index = 0; index < _text.Length; index++)
        {
            char value = _text[index];
            if (value == '\0')
            {
                int end = _text.IndexOf('\0', index + 1);
                if (end < 0 || !int.TryParse(_text.AsSpan(index + 1, end - index - 1), out int id) || !_opaque.TryGetValue(id, out string[]? values))
                { diagnostic = "Invalid internal script argument boundary."; return false; }
                FlushLiteral(); fragments.Add(new(null, values)); started |= values.Length > 0;
                index = end; continue;
            }
            if (value == '\\' && quote != '\'')
            {
                if (index + 1 == _text.Length) { diagnostic = "Script arguments end with an incomplete escape."; return false; }
                if (_text[index + 1] == '\0') { literal.Append(value); started = true; continue; }
                literal.Append(_text[++index]); started = true; continue;
            }
            if (quote != '\0')
            {
                if (value == quote) { quote = '\0'; }
                else { literal.Append(value); }
                started = true; continue;
            }
            if (value is '\'' or '"') { quote = value; started = authorQuoted = true; }
            else if (char.IsWhiteSpace(value)) { Finish(); }
            else { literal.Append(value); started = true; }
        }
        if (quote != '\0') { diagnostic = "Script arguments contain an unterminated quote."; return false; }
        Finish(); return true;
    }

    private static void AppendArguments(ArgumentToken token, List<string> arguments)
    {
        StringBuilder current = new(); bool started = token.AuthorQuoted;
        foreach (Fragment fragment in token.Fragments)
        {
            if (fragment.Text is string text) { current.Append(text); started = true; continue; }
            string[] values = fragment.Values!;
            for (int index = 0; index < values.Length; index++)
            {
                if (index > 0) { if (started) { arguments.Add(current.ToString()); } current.Clear(); }
                current.Append(values[index]); started = true;
            }
        }
        if (started) { arguments.Add(current.ToString()); }
    }

    private static bool IsPosixShell(string? executable)
        => executable is not null && System.IO.Path.GetFileName(executable) is "bash" or "sh" or "dash" or "zsh" or "ksh";

    private static int ShellProgramIndex(IReadOnlyList<ArgumentToken> tokens)
    {
        for (int index = 0; index < tokens.Count; index++)
        {
            ArgumentToken token = tokens[index];
            if (token.Fragments.Any(fragment => fragment.Values is not null)) { return -1; }
            string value = string.Concat(token.Fragments.Select(fragment => fragment.Text));
            if (value == "--command") { return index + 1; }
            if (value.Length < 2 || value[0] != '-' || value == "--") { return -1; }
            if (value[1] != '-' && value[1..].All(char.IsAsciiLetter))
            {
                // -o/-O consume a setting argument; do not mistake its contents for -c.
                int setting = value.IndexOfAny(['o', 'O'], 1), command = value.IndexOf('c', 1);
                if (command >= 0 && (setting < 0 || command < setting)) { return index + 1; }
                if (setting >= 0) { index++; }
                continue;
            }
            if (value is "--noprofile" or "--norc" or "--posix" or "--login" or "--restricted" or "--verbose") { continue; }
            return -1; // Unknown operand-taking options are not inferred.
        }
        return -1;
    }

    private static bool BuildShellProgram(ArgumentToken token, out string program, out string? diagnostic)
    {
        StringBuilder result = new(); char quote = '\0'; bool escaped = false;
        foreach (Fragment fragment in token.Fragments)
        {
            if (fragment.Text is string text)
            {
                result.Append(text);
                foreach (char value in text)
                {
                    if (escaped) { escaped = false; continue; }
                    if (value == '\\' && quote != '\'') { escaped = true; }
                    else if (quote != '\0') { if (value == quote) { quote = '\0'; } }
                    else if (value is '\'' or '"' or '`') { quote = value; }
                }
            }
            else
            {
                if (quote != '\0' || escaped)
                {
                    program = "";
                    diagnostic = "Double-brace values inside shell program quotes or escapes are not supported. Leave the marker unquoted or pass the value as a positional argument.";
                    return false;
                }
                result.Append(string.Join(' ', fragment.Values!.Select(ShellQuote)));
            }
        }
        program = result.ToString(); diagnostic = null; return true;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
