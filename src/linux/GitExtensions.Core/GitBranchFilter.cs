using GitExtensions.Extensibility;
using System.Text;

namespace GitExtensions.Linux;

public sealed record GitBranchFilterInvalidToken(string Token, string Reason);

/// <summary>The accepted original expression and its individual missing references.</summary>
public sealed record GitBranchFilterValidation(string Filter, IReadOnlyList<string> ValidTokens,
    IReadOnlyList<GitBranchFilterInvalidToken> InvalidTokens, IReadOnlyList<string> Arguments);

public sealed partial class GitRepository
{
    /// <summary>
    /// Applies FilterToolBar's whitespace tokenization and FilterInfo's wildcard conversion.
    /// Options, wildcards and ranges are deliberately not resolved as individual commits:
    /// Git retains their ordering and reports invalid ranges/options at log execution time.
    /// </summary>
    public async Task<GitBranchFilterValidation> ValidateBranchFilterAsync(string? filter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] tokens = SplitBranchFilter(filter);

        List<string> accepted = [], arguments = [];
        List<GitBranchFilterInvalidToken> invalid = [];
        IReadOnlyList<GitReference>? references = null;
        foreach (string token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool option = token.StartsWith("--", StringComparison.Ordinal);
            bool range = token.Contains("..", StringComparison.Ordinal);
            bool wildcard = token.AsSpan().IndexOfAny(Delimiters.WildcardBranchSearchValues) >= 0;
            if (!option && !range && !wildcard)
            {
                string revision = token.StartsWith('^') ? token[1..] : token;
                references ??= await GetReferencesAsync(cancellationToken).ConfigureAwait(false);
                bool exists = !token.StartsWith('^') && references.Any(reference => reference.ShortName == token);
                if (!exists && !await IsBranchFilterRevisionAsync(revision, cancellationToken).ConfigureAwait(false))
                {
                    invalid.Add(new(token, "The reference does not resolve to a commit."));
                    continue;
                }
            }

            accepted.Add(token);
            // Preserve the upstream rule exactly, including ^wildcard and bracket patterns.
            arguments.Add(TransportBranchFilterArgument(wildcard && !option && !range ? "--branches=" + token : token));
        }

        return new(string.Join(" ", accepted), accepted.AsReadOnly(), invalid.AsReadOnly(), arguments.AsReadOnly());
    }

    // Refresh never revalidates references: a disappeared accepted ref must make Git fail,
    // rather than silently removing its token and expanding the viewer to all branches.
    private static IReadOnlyList<string> BuildBranchFilterArguments(string filter)
        => Array.AsReadOnly(SplitBranchFilter(filter).Select(token => TransportBranchFilterArgument(
            token.AsSpan().IndexOfAny(Delimiters.WildcardBranchSearchValues) >= 0
                && !token.StartsWith("--", StringComparison.Ordinal) && !token.Contains("..", StringComparison.Ordinal)
                ? "--branches=" + token : token)).ToArray());

    private static string[] SplitBranchFilter(string? filter)
    {
        if (filter?.Contains('\0') == true)
        {
            throw new ArgumentException("Branch filters cannot contain NUL.", nameof(filter));
        }

        string[] tokens = filter?.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        // Check all options before any subprocess. Git's revision options remain open-ended;
        // only operations incompatible with a read-only, canonical history are excluded.
        foreach (string token in tokens)
        {
            if (token.StartsWith("--", StringComparison.Ordinal)) { _ = TransportBranchFilterArgument(token); }
            else if (token.StartsWith('-'))
            {
                throw new ArgumentException($"History filter token '{token}' cannot be a revision beginning with a Git option.", nameof(filter));
            }
        }

        return tokens;
    }

    // The original ArgumentBuilder appends raw tokens to ProcessStartInfo.Arguments.
    // Preserve its double-quote/backslash transport for tokens without whitespace,
    // then give Git one ArgumentList item; no shell or quoted-phrase tokenizer is used.
    private static string TransportBranchFilterArgument(string token)
    {
        StringBuilder result = new();
        bool quoted = false;
        for (int index = 0; index < token.Length; index++)
        {
            int slashes = 0;
            while (index < token.Length && token[index] == '\\') { slashes++; index++; }
            if (index < token.Length && token[index] == '"')
            {
                result.Append('\\', slashes / 2);
                if (slashes % 2 != 0) { result.Append('"'); }
                else if (quoted && index + 1 < token.Length && token[index + 1] == '"') { result.Append('"'); index++; }
                else { quoted = !quoted; }
            }
            else
            {
                result.Append('\\', slashes);
                if (index < token.Length) { result.Append(token[index]); }
            }
        }

        if (quoted)
        {
            throw new ArgumentException("Branch-filter quotes must close within a whitespace-delimited token; quoted phrases are not supported.", nameof(token));
        }

        string argument = result.ToString();
        if (argument.StartsWith("--", StringComparison.Ordinal)) { ValidateBranchFilterOption(argument); }
        else if (argument.StartsWith('-'))
        {
            throw new ArgumentException($"History filter token '{token}' cannot be a revision beginning with a Git option.", nameof(token));
        }

        return argument;
    }

    private async Task<bool> IsBranchFilterRevisionAsync(string revision, CancellationToken cancellationToken)
    {
        if (revision.Length == 0 || revision.StartsWith('-')) { return false; }
        GitResult result = await ExecuteAsync(["rev-parse", "--quiet", "--verify", "--end-of-options", revision + "^{commit}"],
            cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false);
        return result.Success;
    }

    private static void ValidateBranchFilterOption(string token)
    {
        string name = token.Split('=', 2)[0];
        // --not is an exact revision option, not an abbreviation of --notes.
        if (name == "--not") { return; }
        if (name == "--" || name == "--end-of-options" || IncompatibleBranchFilterOptions.Any(option =>
            option.StartsWith(name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"History filter option '{token}' changes output, executes an external tool, or redirects input/output. "
                + "The history viewer requires its canonical read-only Git format.", nameof(token));
        }
    }

    // This is an incompatibility list, not a whitelist of revision features. Unknown revision
    // options still go to Git. Also reject their long-option abbreviations (e.g. --out=file).
    private static readonly string[] IncompatibleBranchFilterOptions =
    [
        "--output", "--ext-diff", "--textconv", "--alternate-refs", "--stdin", "--help", "--version",
        "--format", "--pretty", "--oneline", "--abbrev-commit", "--no-abbrev-commit", "--encoding", "--date",
        "--graph", "--show-linear-break", "--log-size", "--early-output", "--header", "--count",
        "--color", "--decorate", "--notes", "--show-notes", "--show-signature", "--quiet", "--silent",
        "--raw", "--patch", "--patch-with-raw", "--patch-with-stat", "--patch-with-numstat",
        "--diff-merges", "--cc", "--combined-all-paths", "--dense-combined", "--combined",
        "--stat", "--stat-width", "--stat-name-width", "--stat-graph-width", "--stat-count", "--stat-compact-summary",
        "--numstat", "--shortstat", "--summary", "--compact-summary", "--name-only", "--name-status",
        "--dirstat", "--dirstat-by-file", "--cumulative", "--check", "--binary",
        "--word-diff", "--word-diff-regex", "--color-words", "--color-moved", "--color-moved-ws",
        "--line-prefix", "--src-prefix", "--dst-prefix", "--no-prefix",
        "--inter-hunk-context", "--unified", "--output-indicator-new", "--output-indicator-old", "--output-indicator-context",
        "--objects", "--objects-edge", "--objects-edge-aggressive", "--no-object-names", "--disk-usage",
    ];

    private static void ValidateBranchFilteredLogOutput(string output)
    {
        if (output.Length == 0) { return; }
        string[] fields = output.Split('\0');
        if (fields[^1].Length != 0 || (fields.Length - 1) % 8 != 0)
        {
            throw new InvalidDataException("Git history options produced output incompatible with the canonical NUL-delimited history format.");
        }

        for (int index = 0; index + 7 < fields.Length; index += 8)
        {
            if (!IsObjectName(fields[index]) || fields[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(parent => !IsObjectName(parent)))
            {
                throw new InvalidDataException("Git history options altered the canonical commit identities.");
            }
        }

        static bool IsObjectName(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    }
}
