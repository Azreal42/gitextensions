namespace GitCommands.Git.Gpg;

/// <summary>
/// Pure Windows signature classifications, shared with the portable Git reader.
/// </summary>
public static class GpgSignatureStatusParser
{
    public static CommitStatus Commit(string? value) => value switch
    {
        "G" => CommitStatus.GoodSignature,
        "B" or "U" or "X" or "Y" or "R" => CommitStatus.SignatureError,
        "E" => CommitStatus.MissingPublicKey,
        _ => CommitStatus.NoSignature
    };

    public static TagStatus Tag(int annotatedTagCount, string? rawMessage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(annotatedTagCount);

        if (annotatedTagCount == 0)
        {
            return TagStatus.NoTag;
        }

        if (annotatedTagCount > 1)
        {
            return TagStatus.Many;
        }

        ArgumentNullException.ThrowIfNull(rawMessage);

        if (rawMessage.Contains("GOODSIG", StringComparison.Ordinal) && rawMessage.Contains("VALIDSIG", StringComparison.Ordinal))
        {
            return TagStatus.OneGood;
        }

        if (rawMessage.Contains("error: no signature found", StringComparison.Ordinal))
        {
            return TagStatus.TagNotSigned;
        }

        if (rawMessage.Contains("NO_PUBKEY", StringComparison.Ordinal))
        {
            return TagStatus.NoPubKey;
        }

        return TagStatus.OneBad;
    }
}
