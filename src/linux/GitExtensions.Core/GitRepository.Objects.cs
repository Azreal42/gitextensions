using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public const string LostFoundTagPrefix = "LOST_FOUND_";
    private static readonly UTF8Encoding ObjectUtf8 = new(false, true);
    private static readonly Regex LostObjectDiagnostic = new(@"^(?:(?<kind>dangling|unreachable|missing) (?<type>commit|blob|tree|tag)|(?<kind>warning in tree)) (?<oid>[a-fA-F0-9]{64}|[a-fA-F0-9]{40})(?=$|[^a-fA-F0-9]).*$", RegexOptions.CultureInvariant);

    public Task<GitObjectRecoveryReport> GetLostObjectsAsync(GitObjectRecoveryOptions? options = null, CancellationToken cancellationToken = default)
        => WithMutationAsync(token => ScanLostObjectsAsync(options ?? new(), saveLostFound: false, token), cancellationToken);

    /// <summary>Reads a full SHA1/SHA256 object identity, including packed/alternate objects and unpeeled annotated tags.</summary>
    public async Task<GitObjectData> ReadObjectAsync(string objectId, CancellationToken cancellationToken = default)
    {
        string oid = ValidateObjectId(objectId);
        await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        string[] arguments = ["--no-replace-objects", "cat-file", "--batch"];
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], cancellationToken, Encoding.ASCII.GetBytes(oid + "\n")).ConfigureAwait(false);
        if (!result.Success) { throw ObjectCommandError(arguments, result); }
        int newline = Array.IndexOf(result.StandardOutput, (byte)'\n');
        if (newline < 0) { throw new InvalidDataException("Git omitted the object record header."); }
        string[] header = Encoding.ASCII.GetString(result.StandardOutput, 0, newline).Split(' ');
        if (header.Length == 2 && header[1] == "missing") { throw new FileNotFoundException("Git object could not be read: " + oid); }
        if (header.Length != 3 || header[0] != oid || !int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
            || size < 0 || result.StandardOutput.LongLength != (long)newline + size + 2 || result.StandardOutput[^1] != (byte)'\n')
        { throw new InvalidDataException("Invalid Git object record for " + oid + "."); }
        byte[] bytes = result.StandardOutput.AsSpan(newline + 1, size).ToArray();
        byte[] prefix = Encoding.ASCII.GetBytes(header[1] + " " + size.ToString(CultureInfo.InvariantCulture) + "\0");
        using IncrementalHash hash = IncrementalHash.CreateHash(oid.Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        hash.AppendData(prefix); hash.AppendData(bytes);
        if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(oid, StringComparison.Ordinal))
        { throw new InvalidDataException("Git object contents do not match the requested identity: " + oid); }
        return new(oid, ObjectType(header[1]), header[1], bytes);
    }

    /// <summary>Object preview is always read-only; a codec/object failure preserves the identity and any successfully read bytes.</summary>
    public async Task<GitObjectPreview> GetObjectPreviewAsync(string objectId, CancellationToken cancellationToken = default,
        GitComparisonDiffOptions? options = null, GitDiffDisplayOptions? displayOptions = null)
    {
        string oid = ValidateObjectId(objectId);
        GitObjectData? data = null;
        string encodingName = "utf-8", suggested = "file.txt";
        try
        {
            data = await ReadObjectAsync(oid, cancellationToken).ConfigureAwait(false);
            encodingName = displayOptions?.FileEncodingName is string overrideName ? GitFileEncodingCodec.GetEncoding(overrideName).WebName
                : await GetFileEncodingNameAsync(cancellationToken).ConfigureAwait(false);
            if (data.Type == GitLostObjectType.Blob)
            {
                GitDecodedFile file = GitFileEncodingCodec.DecodeFile(data.Bytes, encodingName);
                suggested = GuessLostObjectFileName(oid, data.Bytes, file.IsBinary ? null : file.Text);
                return new(oid, data, file.IsBinary ? null : file.Text, null, file.EncodingName, suggested, null);
            }
            if (data.Type is GitLostObjectType.Commit or GitLostObjectType.Tag)
            {
                // Metadata is re-encoded by Git; hunk content retains its independent file codec.
                List<string> args = ["show", "--encoding=UTF-8", "--format=fuller", "--no-ext-diff", "--no-textconv", "--no-color"];
                AddComparisonOptions(args, options ?? new());
                args.AddRange([oid, "--"]);
                GitBinaryResult patch = await ReadObjectShowAsync(args, cancellationToken).ConfigureAwait(false);
                GitDecodedPatch decoded = GitFileEncodingCodec.DecodePatch(patch.StandardOutput, encodingName);
                string display = decoded.Text;
                bool useColor = displayOptions?.UseGitColoring ?? true;
                if (useColor)
                {
                    args[args.IndexOf("--no-color")] = "--color=always";
                    IReadOnlyList<string> colors = await GetDiffDisplayConfigurationAsync("show", displayOptions?.ReverseGitColoring ?? true, cancellationToken).ConfigureAwait(false);
                    GitBinaryResult colored = await ReadObjectShowAsync([.. colors, .. args], cancellationToken).ConfigureAwait(false);
                    display = GitFileEncodingCodec.DecodePatch(colored.StandardOutput, encodingName).Text;
                }
                GitDiffDocument document = new(decoded.Text, display, GitDiffAppearance.Patch, false)
                { PatchBytes = patch.StandardOutput, FileEncodingName = encodingName, IsLosslessPatch = decoded.IsLossless, UseGitColoring = useColor };
                return new(oid, data, decoded.Text, document, encodingName, "commit.patch", null);
            }
            GitBinaryResult pretty = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, "--no-replace-objects", "cat-file", "-p", oid], cancellationToken).ConfigureAwait(false);
            if (!pretty.Success) { throw ObjectCommandError(["cat-file", "-p", oid], pretty); }
            return new(oid, data, ObjectUtf8.GetString(pretty.StandardOutput), null, "utf-8", suggested, null);
        }
        catch (Exception error) when (IsObjectReadError(error))
        { return new(oid, data, null, null, encodingName, suggested, error.Message); }
    }

    private async Task<GitBinaryResult> ReadObjectShowAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, "--no-replace-objects", "--literal-pathspecs", .. args], token).ConfigureAwait(false);
        if (!result.Success) { throw ObjectCommandError(args, result); }
        return result;
    }

    private async Task<GitObjectRecoveryReport> ScanLostObjectsAsync(GitObjectRecoveryOptions options, bool saveLostFound, CancellationToken token)
    {
        await GetInfoAsync(token).ConfigureAwait(false);
        List<string> args = ["fsck"];
        if (saveLostFound) { args.Add("--lost-found"); }
        if (options.FullCheck) { args.Add("--full"); }
        if (options.IncludeUnreachable) { args.Add("--unreachable"); }
        if (options.NoReflogs) { args.Add("--no-reflogs"); }
        GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, "--no-replace-objects", .. args], token).ConfigureAwait(false);
        List<GitLostObject> objects = [];
        Dictionary<string, GitLostObject> metadata = new(StringComparer.Ordinal);
        foreach (string line in (result.StandardOutput + "\n" + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            Match match = LostObjectDiagnostic.Match(line.TrimEnd('\r'));
            if (!match.Success) { continue; }
            string oid = match.Groups["oid"].Value.ToLowerInvariant();
            GitLostObject row = new(oid, ObjectType(match.Groups["type"].Value), match.Groups["kind"].Value
                + (match.Groups["type"].Success ? " " + match.Groups["type"].Value : ""), line.TrimEnd('\r'));
            if (!metadata.TryGetValue(oid, out GitLostObject? details))
            {
                try
                {
                    GitObjectData data = await ReadObjectAsync(oid, token).ConfigureAwait(false);
                    details = FillLostObject(row, data);
                    if (details.Type == GitLostObjectType.Commit) { details = await ReadLostCommitMetadataAsync(details, data, token).ConfigureAwait(false); }
                    if (details.Type == GitLostObjectType.Blob)
                    {
                        string loose = System.IO.Path.Combine(Info!.CommonDirectory, "objects", oid[..2], oid[2..]);
                        if (File.Exists(loose)) { details = details with { Date = new DateTimeOffset(File.GetCreationTimeUtc(loose)) }; }
                    }
                }
                catch (Exception error) when (IsObjectReadError(error)) { details = row with { ReadError = error.Message }; }
                metadata.Add(oid, details);
            }
            objects.Add(details with { RawType = row.RawType, Diagnostic = row.Diagnostic });
        }
        return new(result.ExitCode, result.StandardOutput, result.StandardError, objects.OrderByDescending(row => row.Date).ToArray(),
            await GetGitPathAsync("lost-found", token).ConfigureAwait(false));
    }

    private async Task<GitLostObject> ReadLostCommitMetadataAsync(GitLostObject row, GitObjectData data, CancellationToken token)
    {
        // Git's --encoding re-encodes the whole commit, including headers that already contain UTF8 identities.
        // Feed the raw UTF8 identity to check-mailmap instead of using a recoded %aN that cannot match its contact.
        int separator = data.Bytes.AsSpan().IndexOf("\n\n"u8);
        string identity = ObjectUtf8.GetString(data.Bytes.AsSpan(0, separator)).Split('\n')
            .FirstOrDefault(line => line.StartsWith("author ", StringComparison.Ordinal))?["author ".Length..]
            ?? throw new InvalidDataException("The lost commit has no author identity: " + row.Hash);
        int emailEnd = identity.LastIndexOf('>');
        if (emailEnd < 0) { throw new InvalidDataException("The lost commit author identity is incomplete: " + row.Hash); }
        string[] mapArgs = ["check-mailmap", "--stdin"];
        GitBinaryResult mapped = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. mapArgs], token,
            ObjectUtf8.GetBytes(identity[..(emailEnd + 1)] + "\n")).ConfigureAwait(false);
        if (!mapped.Success) { throw ObjectCommandError(mapArgs, mapped); }
        string contact = ObjectUtf8.GetString(mapped.StandardOutput).TrimEnd('\r', '\n');
        int nameEnd = contact.LastIndexOf(" <", StringComparison.Ordinal);
        if (nameEnd < 0) { throw new InvalidDataException("Git omitted the mapped author identity: " + row.Hash); }

        // Match FormVerify's complete subject paragraph, committer timestamp and first parent.
        string[] args = ["show", "--quiet", "--encoding=UTF-8", "--no-ext-diff", "--no-textconv", "--no-notes", "--format=%s%x00%ct%x00%P", row.Hash, "--"];
        GitBinaryResult result = await ReadObjectShowAsync(args, token).ConfigureAwait(false);
        string[] fields = ObjectUtf8.GetString(result.StandardOutput).TrimEnd('\r', '\n').Split('\0');
        if (fields.Length != 3 || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        { throw new InvalidDataException("Git omitted the lost commit metadata: " + row.Hash); }
        return row with { Author = contact[..nameEnd], Subject = fields[0], Date = DateTimeOffset.FromUnixTimeSeconds(seconds),
            Parent = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() };
    }

    private static GitLostObject FillLostObject(GitLostObject row, GitObjectData data)
    {
        if (data.Type is not (GitLostObjectType.Commit or GitLostObjectType.Tag)) { return row with { Type = data.Type }; }
        int separator = data.Bytes.AsSpan().IndexOf("\n\n"u8);
        if (separator < 0) { throw new InvalidDataException("Git object metadata is incomplete: " + data.Hash); }
        string[] headers = ObjectUtf8.GetString(data.Bytes.AsSpan(0, separator)).Split('\n');
        string? Header(string name) => headers.FirstOrDefault(line => line.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];
        string message = data.Type == GitLostObjectType.Commit
            ? GitFileEncodingCodec.GetEncoding(Header("encoding") ?? "utf-8").GetString(data.Bytes.AsSpan(separator + 2))
            : ObjectUtf8.GetString(data.Bytes.AsSpan(separator + 2));
        string? identity = Header(data.Type == GitLostObjectType.Commit ? "author" : "tagger");
        string? timestampIdentity = Header(data.Type == GitLostObjectType.Commit ? "committer" : "tagger");
        string? author = identity is null ? null : identity[..Math.Max(0, identity.LastIndexOf(" <", StringComparison.Ordinal))];
        DateTimeOffset? date = null;
        if (timestampIdentity is not null)
        {
            string[] parts = timestampIdentity.Split(' ');
            if (parts.Length >= 2 && long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            { date = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        }
        string? tagName = data.Type == GitLostObjectType.Tag ? Header("tag") : null;
        return row with { Type = data.Type, Date = date, Subject = (tagName is null ? "" : tagName + ":") + ReflogSubject(message), Author = author,
            Parent = Header(data.Type == GitLostObjectType.Commit ? "parent" : "object"), TagName = tagName };
    }

    private static string ValidateObjectId(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        if (objectId.Length is not (40 or 64) || !objectId.All(Uri.IsHexDigit))
        { throw new ArgumentException("Select a complete Git SHA1 or SHA256 object identity.", nameof(objectId)); }
        return objectId.ToLowerInvariant();
    }
    private static GitLostObjectType ObjectType(string type) => type switch
    { "commit" => GitLostObjectType.Commit, "blob" => GitLostObjectType.Blob, "tree" => GitLostObjectType.Tree, "tag" => GitLostObjectType.Tag, _ => GitLostObjectType.Other };
    private static bool IsObjectReadError(Exception error)
        => error is GitCommandException or IOException or UnauthorizedAccessException or DecoderFallbackException or NotSupportedException or FormatException or ArgumentOutOfRangeException;
    private static GitCommandException ObjectCommandError(IReadOnlyList<string> args, GitBinaryResult result)
        => new(args, new(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput), result.StandardError)
        { StandardOutputBytes = result.StandardOutput, StandardErrorBytes = result.StandardErrorBytes });
}
