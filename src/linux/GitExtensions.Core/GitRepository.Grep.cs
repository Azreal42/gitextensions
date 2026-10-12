using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Searches source lines without decoding Git's output stream as UTF-8. Legacy SearchAsync remains available.</summary>
    public async Task<GitGrepResult> GrepAsync(GitGrepOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Expression);
        if (options.Expression.Contains('\0')) { throw new ArgumentException("A grep expression cannot contain NUL.", nameof(options)); }
        if (options.Cached && options.Revision is not null) { throw new ArgumentException("Choose either an index search or a commit search.", nameof(options)); }
        string[] paths = options.Paths is null ? [] : ValidatePaths(options.Paths);
        string[] user = GitGrepArguments.Split(options.UserArguments ?? "");
        string[] expression = GitGrepArguments.IsExpression(options.Expression) ? GitGrepArguments.Split(options.Expression) : ["-e", options.Expression];
        GrepArgumentState state = ValidateGrepArguments([.. user, .. expression]);
        if (state.Text && state.Context)
        { throw new ArgumentException("Git grep -a/--text cannot be combined with context options in structured results: NUL in binary context lines makes column headers ambiguous.", nameof(options)); }
        string encodingName = options.EncodingName ?? await GetFileEncodingNameAsync(cancellationToken).ConfigureAwait(false);
        Encoding encoding = GitFileEncodingCodec.GetEncoding(encodingName);
        if (!GitFileEncodingCodec.SupportsPatchText(encoding))
        { throw new NotSupportedException("Git grep patterns require an ASCII-compatible file encoding: " + encoding.WebName); }
        // Encode all requested patterns before creating files or starting grep. An unmappable
        // character must fail, rather than silently turning into a '?' pattern.
        List<byte[]> patterns = [];
        List<string> matching = [];
        EncodeGrepArguments(user, encoding, matching, patterns);
        EncodeGrepArguments(expression, encoding, matching, patterns);
        cancellationToken.ThrowIfCancellationRequested();
        string? revision = options.Revision is null ? null : await ResolveRevisionAsync(options.Revision, cancellationToken).ConfigureAwait(false);
        if (revision is null) { await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false); }
        string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-grep-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            for (int i = 0; i < patterns.Count; i++)
            {
                string file = System.IO.Path.Combine(temporary, i.ToString(CultureInfo.InvariantCulture) + ".pattern");
                await File.WriteAllBytesAsync(file, patterns[i], cancellationToken).ConfigureAwait(false);
                matching = matching.Select(argument => argument == "\0" + i.ToString(CultureInfo.InvariantCulture) ? file : argument).ToList();
            }
            List<string> arguments = ["--literal-pathspecs", "grep", "-I", "--basic-regexp", .. matching];
            if (options.IgnoreCase) { arguments.Add("--ignore-case"); }
            if (options.MatchWholeWord) { arguments.Add("--word-regexp"); }
            // Keep the native matching options, but make filenames and numeric metadata unambiguous.
            arguments.AddRange(["--no-color", "--no-heading", "--no-break", "--full-name", "--line-number", "--column", "--null", "--no-textconv"]);
            int sourceIndex = arguments.Count;
            if (options.Cached) { arguments.Add("--cached"); }
            if (revision is not null) { arguments.Add(revision); }
            arguments.AddRange(["--", .. paths]);
            byte[][] matchingFiles = [];
            string matchingDiagnostics = "";
            if (!state.Text)
            {
                // Windows first asks for -l -z, which also includes matching binary
                // blobs. Keep that native file membership separately from the -I
                // source-line stream; binary notices have no reliable coordinates.
                List<string> nativeFiles = [.. arguments]; nativeFiles.RemoveAt(2);
                nativeFiles.Insert(sourceIndex - 1, "--files-with-matches");
                GitBinaryResult files = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. nativeFiles], cancellationToken).ConfigureAwait(false);
                if (!files.Success && files.ExitCode != 1) { throw new GitCommandException(nativeFiles, new(files.ExitCode, "", files.StandardError)); }
                matchingFiles = SplitGrepPaths(files.StandardOutput).Select(path => StripGrepRevision(path, revision)).ToArray();
                matchingDiagnostics = files.StandardError;
            }
            if (state.Context && revision is null)
            {
                // Native context separators are "--\n". That can also be the start of
                // a real filename, so obtain Git's ordered, NUL-delimited matching-file
                // list to distinguish exact headers from separators without guessing.
                List<string> fileArguments = [.. arguments]; fileArguments.Insert(sourceIndex, "--files-with-matches");
                GitBinaryResult files = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. fileArguments], cancellationToken).ConfigureAwait(false);
                if (!files.Success && files.ExitCode != 1) { throw new GitCommandException(fileArguments, new(files.ExitCode, "", files.StandardError)); }
                List<GitGrepMatch> contextMatches = []; StringBuilder diagnostics = new(files.StandardError);
                foreach (byte[] path in SplitGrepPaths(files.StandardOutput))
                {
                    string name;
                    try { name = new UTF8Encoding(false, true).GetString(path); }
                    catch (DecoderFallbackException ex) { throw new NotSupportedException("Context grep cannot pass a non-UTF8 pathname through Unicode process arguments; its raw PathBytes remain supported by searches without context.", ex); }
                    // The original viewer also asks Git for one selected file at a time.
                    // An exact known header makes a filename beginning with --\n distinct
                    // from a context separator, even when both names exist in the repository.
                    List<string> oneFile = arguments.Take(arguments.IndexOf("--") + 1).ToList(); oneFile.Add(name);
                    GitBinaryResult file = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. oneFile], cancellationToken).ConfigureAwait(false);
                    if (!file.Success && file.ExitCode != 1) { throw new GitCommandException(oneFile, new(file.ExitCode, "", file.StandardError)); }
                    contextMatches.AddRange(ParseGrepMatches(file.StandardOutput, null, encoding.WebName, context: true, expectedPath: path));
                    diagnostics.Append(file.StandardError);
                }
                return CreateGrepResult(null, options.Cached, encoding.WebName, contextMatches, matchingFiles, matchingDiagnostics + diagnostics);
            }
            GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], cancellationToken).ConfigureAwait(false);
            if (!result.Success && result.ExitCode != 1)
            { throw new GitCommandException(arguments, new(result.ExitCode, "", result.StandardError)); }
            return CreateGrepResult(revision, options.Cached, encoding.WebName, ParseGrepMatches(result.StandardOutput, revision, encoding.WebName, state.Context), matchingFiles, matchingDiagnostics + result.StandardError);
        }
        finally { if (Directory.Exists(temporary)) { Directory.Delete(temporary, recursive: true); } }
    }

    private sealed class GrepArgumentState { internal bool Context; internal bool Text; }

    private static GrepArgumentState ValidateGrepArguments(IReadOnlyList<string> arguments)
    {
        GrepArgumentState state = new();
        HashSet<string> switches = ["-G", "--basic-regexp", "-E", "--extended-regexp", "-F", "--fixed-strings", "-P", "--perl-regexp",
            "-i", "--ignore-case", "-w", "--word-regexp", "-v", "--invert-match", "--all-match", "--and", "--or", "--not", "(", ")",
            "-p", "--show-function", "-W", "--function-context", "-a", "--text", "-I", "--no-textconv",
            "--untracked", "--exclude-standard", "--no-exclude-standard", "--no-color", "--no-heading", "--no-break"];
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (argument is "-e" or "--regexp" or "-f" or "--file" or "-A" or "-B" or "-C" or "--after-context" or "--before-context" or "--context" or "--threads" or "--max-depth")
            {
                if (++i == arguments.Count) { throw new ArgumentException("Git grep option requires a value: " + argument); }
                if (argument is "-A" or "-B" or "-C" or "--after-context" or "--before-context" or "--context") { state.Context = true; }
                continue;
            }
            if (argument.StartsWith("--regexp=", StringComparison.Ordinal) || argument.StartsWith("--file=", StringComparison.Ordinal)
                || argument.StartsWith("--threads=", StringComparison.Ordinal) || argument.StartsWith("--max-depth=", StringComparison.Ordinal)) { continue; }
            if (argument.StartsWith("--after-context=", StringComparison.Ordinal) || argument.StartsWith("--before-context=", StringComparison.Ordinal) || argument.StartsWith("--context=", StringComparison.Ordinal)
                || argument.Length > 2 && argument[0] == '-' && argument[1] is 'A' or 'B' or 'C' && argument.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
            { state.Context = true; continue; }
            if (argument is "-p" or "--show-function" or "-W" or "--function-context") { state.Context = true; }
            if (argument is "-a" or "--text") { state.Text = true; }
            if (switches.Contains(argument)) { continue; }
            if (argument.Length > 2 && argument[0] == '-' && argument[1] != '-' && argument.AsSpan(1).IndexOfAnyExcept("GEFPiwvpaWI".AsSpan()) < 0)
            { state.Context |= argument.Contains('p') || argument.Contains('W'); state.Text |= argument.Contains('a'); continue; }
            throw new ArgumentException("Unsupported structured Git grep option: '" + argument + "'. Use matching/regexp/context options; -l/-L (file lists), -c (counts), -q (quiet), -o (fragments), --heading/--break, --color, --textconv, pager and source-changing options cannot preserve source-line records.");
        }
        return state;
    }

    private static void EncodeGrepArguments(IReadOnlyList<string> arguments, Encoding encoding, List<string> output, List<byte[]> patterns)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            string? pattern = argument is "-e" or "--regexp" ? arguments[++i]
                : argument.StartsWith("--regexp=", StringComparison.Ordinal) ? argument[9..] : null;
            if (pattern is null) { output.Add(argument); continue; }
            try { patterns.Add(encoding.GetBytes(pattern + "\n")); }
            catch (EncoderFallbackException ex) { throw new ArgumentException("The grep pattern cannot be represented in " + encoding.WebName + ".", nameof(arguments), ex); }
            output.Add("-f"); output.Add("\0" + (patterns.Count - 1).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static byte[][] SplitGrepPaths(byte[] bytes)
    {
        List<byte[]> paths = []; int start = 0;
        while (start < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)0, start);
            if (end < 0) { throw new FormatException("Incomplete Git grep matching pathname."); }
            paths.Add(bytes[start..end]); start = end + 1;
        }
        return paths.ToArray();
    }

    private static GitGrepResult CreateGrepResult(string? revision, bool cached, string encodingName,
        IReadOnlyList<GitGrepMatch> matches, byte[][] nativeFiles, string standardError)
    {
        HashSet<string> textFiles = matches.Where(match => !match.IsContext).Select(match => Convert.ToBase64String(match.PathBytes.Span)).ToHashSet(StringComparer.Ordinal);
        List<GitGrepBinaryMatch> binary = [];
        foreach (byte[] path in nativeFiles)
        {
            if (textFiles.Contains(Convert.ToBase64String(path))) { continue; }
            (string name, bool lossless) = DecodeGrepPath(path);
            binary.Add(new(name, path, lossless));
        }
        return new(revision, cached, encodingName, matches, standardError) { BinaryMatches = binary };
    }

    private static byte[] StripGrepRevision(byte[] path, string? revision)
    {
        if (revision is null) { return path; }
        byte[] prefix = Encoding.ASCII.GetBytes(revision + ":");
        if (!path.AsSpan().StartsWith(prefix)) { throw new FormatException("Git grep returned a different revision prefix."); }
        return path[prefix.Length..];
    }

    private static (string Path, bool HasLosslessPath) DecodeGrepPath(byte[] path)
    {
        try { return (new UTF8Encoding(false, true).GetString(path), true); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(path), false); }
    }

    private static IReadOnlyList<GitGrepMatch> ParseGrepMatches(byte[] bytes, string? revision, string encodingName, bool context, byte[]? expectedPath = null)
    {
        List<GitGrepMatch> matches = []; int start = 0;
        while (start < bytes.Length)
        {
            if (expectedPath is not null)
            {
                ReadOnlySpan<byte> remaining = bytes.AsSpan(start);
                bool header = remaining.Length > expectedPath.Length && remaining.StartsWith(expectedPath) && remaining[expectedPath.Length] == 0;
                if (!header && remaining.StartsWith("--\n"u8)) { start += 3; continue; }
                if (!header) { throw new FormatException("Git grep returned a different pathname during a context read; retry the search."); }
            }
            else if (context && bytes.AsSpan(start).StartsWith("--\n"u8)) { start += 3; continue; }
            int pathEnd = Array.IndexOf(bytes, (byte)0, start);
            int lineEnd = pathEnd < 0 ? -1 : Array.IndexOf(bytes, (byte)0, pathEnd + 1);
            if (pathEnd < 0 || lineEnd < 0) { throw new FormatException("Incomplete Git grep pathname/line record."); }
            byte[] pathBytes = StripGrepRevision(bytes[start..pathEnd], revision);
            int line = ParseGrepNumber(bytes.AsSpan(pathEnd + 1, lineEnd - pathEnd - 1));
            int textEnd = Array.IndexOf(bytes, (byte)'\n', lineEnd + 1);
            if (textEnd < 0) { textEnd = bytes.Length; }
            int columnEnd = Array.IndexOf(bytes, (byte)0, lineEnd + 1, textEnd - lineEnd - 1);
            if (!context && columnEnd < 0) { throw new FormatException("Incomplete Git grep column record."); }
            int? column = columnEnd >= 0 ? ParseGrepNumber(bytes.AsSpan(lineEnd + 1, columnEnd - lineEnd - 1)) : null;
            byte[] lineBytes = bytes[(column is null ? lineEnd + 1 : columnEnd + 1)..textEnd];
            (string path, bool losslessPath) = DecodeGrepPath(pathBytes);
            matches.Add(new(path, pathBytes, losslessPath, line, column, lineBytes,
                GitFileEncodingCodec.DecodeFile(lineBytes, encodingName), column is null));
            start = textEnd + 1;
        }
        return matches;
    }

    private static int ParseGrepNumber(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0
            || !int.TryParse(Encoding.ASCII.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
        { throw new FormatException("Invalid Git grep line/column number."); }
        return number;
    }
}
