using System.Security.Cryptography;
using System.Text;

namespace GitExtensions.Linux;

// Admission is explicit at a repository reader, never inferred from a runner's verb.
internal enum GitCachedReadKind { Object, CommitIdentity, Metadata, Graph, Tree, Blob, Diff, RangeDiff, Grep, Blame, Difftastic }

public sealed partial class GitRepository
{
    private async Task<GitBinaryResult> RunCachedBinaryAsync(GitCachedReadKind kind, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, byte[]? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null, IReadOnlyList<string>? normalizedArguments = null)
    {
        using IDisposable operation = _runner.BeginOperation();
        GitExecutionEnvironment execution = _runner.ExecutionEnvironment;
        string[] ownedArguments = arguments.ToArray();
        byte[]? ownedInput = standardInput?.ToArray();
        Dictionary<string, string?>? ownedEnvironment = environment is null ? null : new(environment, StringComparer.Ordinal);
        string[] keyArguments = normalizedArguments?.ToArray() ?? ownedArguments;
        string[] pathOptions = ownedArguments.FirstOrDefault() is "diff" or "show" or "blame" or "ls-tree" or "log" or "grep"
            ? ["--literal-pathspecs"] : [];
        Task<GitBinaryResult> Read(CancellationToken token) => _runner.RunBinaryAsync(Path,
            [.. GlobalOptions, .. pathOptions, .. ownedArguments], token, ownedInput, ownedEnvironment);
        CommandCacheAuthority? authority;
        try
        {
            IReadOnlyList<string> dependencies = CommandCacheInputFiles(kind, ownedArguments, keyArguments);
            authority = await GetCommandCacheAuthorityAsync(execution, ownedEnvironment, kind, dependencies, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsCommandCacheContextError(error)) { authority = null; }
        if (authority is null) { return await Read(cancellationToken).ConfigureAwait(false); }
        string inputIdentity = ownedInput is null ? "no-stdin" : "stdin:" + CacheHash(ownedInput);
        string overrideIdentity = CacheEnvironmentIdentity(ownedEnvironment);
        string key = CacheCommandText(ownedArguments);
        string identity = CacheHashText(CacheFrame(authority.EnvironmentIdentity) + CacheFrame(authority.Fingerprint)
            + CacheFrame(kind.ToString()) + string.Concat(keyArguments.Select(CacheFrame)) + CacheFrame(inputIdentity) + CacheFrame(overrideIdentity));
        bool readRan = false;
        GitBinaryResult result = await GitCommandCache.Shared.ReadThroughAsync(identity, key,
            CacheCommandText(CacheDisplayArguments(ownedArguments)), async token =>
            {
                readRan = true;
                GitBinaryResult actual = await Read(token).ConfigureAwait(false);
                bool admit = actual.Success && IsCommandCacheResultAdmissible(kind, ownedArguments, ownedInput, actual)
                    && await IsCommandCacheAuthorityCurrentAsync(authority, token).ConfigureAwait(false);
                return (actual, admit);
            }, cancellationToken).ConfigureAwait(false);
        // A miss has already returned a real native result, even if authority changed during that read.
        // A stale hit is retried once freshly, without admitting an approximation or changing the caller's error.
        if (!readRan && !await IsCommandCacheAuthorityCurrentAsync(authority, cancellationToken).ConfigureAwait(false))
        { return await Read(cancellationToken).ConfigureAwait(false); }
        return result;
    }

    private async Task<GitResult> RunCachedAsync(GitCachedReadKind kind, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, string? standardInput = null, int[]? allowedExitCodes = null,
        IReadOnlyDictionary<string, string?>? environment = null, IReadOnlyList<string>? normalizedArguments = null)
    {
        GitBinaryResult binary = await RunCachedBinaryAsync(kind, arguments, cancellationToken,
            standardInput is null ? null : Encoding.UTF8.GetBytes(standardInput), environment, normalizedArguments).ConfigureAwait(false);
        GitOutputDecoding decoded = GitOutputDecoding.Utf8(binary);
        GitResult result = new(binary.ExitCode, decoded.StandardOutput, decoded.StandardError)
        { StandardOutputBytes = binary.StandardOutput, StandardErrorBytes = binary.StandardErrorBytes, Warnings = decoded.Warnings };
        if (!result.Success && !(allowedExitCodes?.Contains(result.ExitCode) ?? false)) { throw new GitCommandException(arguments, result); }
        return result;
    }

    // A complete OID is an immutable input to this reader. Mobile names are still resolved freshly.
    // Cache the actual successful native type/peeling check, rather than silently accepting a tree as a commit.
    private async Task<string> ResolveCachedReadRevisionAsync(string revision, CancellationToken token)
    {
        if (revision is null || revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit))
        { return await ResolveRevisionAsync(revision!, token).ConfigureAwait(false); }
        return ReadOutputLine((await RunCachedAsync(GitCachedReadKind.CommitIdentity,
            ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], token).ConfigureAwait(false)).StandardOutput);
    }

    private IReadOnlyList<string> CommandCacheInputFiles(GitCachedReadKind kind, IReadOnlyList<string> arguments, IReadOnlyList<string> normalized)
    {
        if (kind != GitCachedReadKind.Grep) { return []; }
        List<string> files = [];
        for (int i = 0; i < arguments.Count && arguments[i] != "--"; i++)
        {
            if (arguments[i] is "-f" or "--file")
            {
                if (++i < arguments.Count && !normalized[i].StartsWith("\0pattern-sha256:", StringComparison.Ordinal))
                { files.Add(System.IO.Path.GetFullPath(arguments[i], Path)); }
            }
            else if (arguments[i].StartsWith("--file=", StringComparison.Ordinal))
            { files.Add(System.IO.Path.GetFullPath(arguments[i][7..], Path)); }
        }
        return files;
    }

    private static bool IsCommandCacheResultAdmissible(GitCachedReadKind kind, IReadOnlyList<string> arguments,
        byte[]? input, GitBinaryResult result)
    {
        if (kind == GitCachedReadKind.CommitIdentity)
        {
            string value = Encoding.ASCII.GetString(result.StandardOutput).TrimEnd('\r', '\n');
            return value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
        }
        if (input is not null && arguments.Contains("cat-file") && arguments.Contains("--batch"))
        {
            string oid = Encoding.ASCII.GetString(input).TrimEnd('\n');
            if (oid.Length is not (40 or 64) || !oid.All(Uri.IsHexDigit)) { return false; }
            int newline = Array.IndexOf(result.StandardOutput, (byte)'\n');
            if (newline < 0) { return false; }
            string[] header = Encoding.ASCII.GetString(result.StandardOutput, 0, newline).Split(' ');
            if (header.Length != 3 || !header[0].Equals(oid, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(header[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int size)
                || size < 0 || result.StandardOutput.LongLength != (long)newline + size + 2 || result.StandardOutput[^1] != (byte)'\n') { return false; }
            if (kind == GitCachedReadKind.Metadata) { return header[1] == "commit"; }
            using IncrementalHash hash = IncrementalHash.CreateHash(oid.Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes(header[1] + " " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
            hash.AppendData(result.StandardOutput.AsSpan(newline + 1, size));
            return Convert.ToHexString(hash.GetHashAndReset()).Equals(oid, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private static string CacheFrame(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
    private static string CacheHash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static string CacheHashText(string value) => CacheHash(Encoding.UTF8.GetBytes(value));
    private static string CacheEnvironmentIdentity(IReadOnlyDictionary<string, string?>? environment)
        => environment is null ? "no-overrides" : CacheHashText(string.Concat(environment.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => CacheFrame(p.Key) + (p.Value is null ? "null;" : CacheFrame(p.Value)))));
    private static IReadOnlyList<string> CacheDisplayArguments(IReadOnlyList<string> arguments)
    {
        List<string> display = [];
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            // Stop at the verb. In particular diff-tree -c is a combined-diff switch, not global configuration.
            if (!argument.StartsWith('-') || argument == "--")
            { display.AddRange(arguments.Skip(i)); break; }
            if (argument is "-c" or "--config-env") { if (i + 1 < arguments.Count) { i++; } continue; }
            if (argument == "--no-optional-locks" || argument.StartsWith("--config-env=", StringComparison.Ordinal)
                || argument.Length > 2 && argument.StartsWith("-c", StringComparison.Ordinal)) { continue; }
            display.Add(argument);
            // Preserve the arguments of other known global options while searching for the verb.
            if (argument is "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix")
            { if (i + 1 < arguments.Count) { display.Add(arguments[++i]); } }
        }
        return display;
    }

    private static string CacheCommandText(IReadOnlyList<string> arguments) => string.Join(" ", arguments.Select(argument =>
        argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c is not '\"' and not '\\') ? argument
            : "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\""));
}
