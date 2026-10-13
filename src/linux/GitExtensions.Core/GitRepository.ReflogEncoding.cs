using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private async Task<IReadOnlyList<GitReflogEntry>> ReadReflogEntriesAsync(int? count, string reference, CancellationToken token)
    {
        List<string> arguments = ["reflog", "show", "--encoding=UTF-8", "-z", "--format=%H%x00%gd%x00%gs%x00%cI%x00%e%x00%B"];
        if (count is int limit) { arguments.Add("--max-count=" + limit.ToString(CultureInfo.InvariantCulture)); }
        arguments.AddRange([ValidateValue(reference), "--"]);
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, new(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput), result.StandardError)); }
        List<ReadOnlyMemory<byte>> fields = [];
        int start = 0;
        for (int end = 0; end < result.StandardOutput.Length; end++)
        {
            if (result.StandardOutput[end] != 0) { continue; }
            fields.Add(result.StandardOutput.AsMemory(start, end - start));
            start = end + 1;
        }
        if (start != result.StandardOutput.Length || fields.Count % 6 != 0) { throw new FormatException("Incomplete Git reflog record."); }
        List<GitReflogEntry> entries = [];
        for (int index = 0; index < fields.Count; index += 6)
        {
            token.ThrowIfCancellationRequested();
            string Field(int offset) => Encoding.UTF8.GetString(fields[index + offset].Span);
            entries.Add(new(Field(0), Field(1), DecodeReflogMessage(fields[index + 2].Span, Field(4), ReflogSubject(new UTF8Encoding(false, true).GetString(fields[index + 5].Span))),
                DateTimeOffset.Parse(Field(3), CultureInfo.InvariantCulture)));
        }
        return entries;
    }

    private static string DecodeReflogMessage(ReadOnlySpan<byte> bytes, string encodingName, string subject)
    {
        UTF8Encoding utf8 = new(false, true);
        if (string.IsNullOrEmpty(encodingName)) { return utf8.GetString(bytes); }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (encoding.CodePage == Encoding.UTF8.CodePage) { return utf8.GetString(bytes); }

        // --encoding converts %B but leaves %gs as stored. A commit action can
        // combine a UTF-8 prefix (including GIT_REFLOG_ACTION) and a legacy subject.
        byte[] legacySubject = encoding.GetBytes(subject);
        if (legacySubject.Length > 0 && bytes.EndsWith(legacySubject))
        {
            ReadOnlySpan<byte> prefix = bytes[..^legacySubject.Length];
            if (prefix.EndsWith(": "u8)) { return utf8.GetString(prefix) + subject; }
        }
        try { return utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return encoding.GetString(bytes); }
    }

    private static string ReflogSubject(string message)
    {
        // Git records only the first line and collapses ASCII whitespace. %s
        // would instead join the entire first paragraph, so use %B explicitly.
        StringBuilder subject = new();
        bool space = false;
        foreach (char character in message)
        {
            if (character == '\n') { break; }
            if (character is ' ' or '\t' or '\r' or '\v' or '\f') { space = subject.Length > 0; continue; }
            if (space) { subject.Append(' '); space = false; }
            subject.Append(character);
        }
        return subject.ToString();
    }
}
