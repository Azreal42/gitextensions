namespace GitExtensions.Linux;

/// <summary>Selection uses UTF-16 viewer offsets; output retains the file codec and UTF-8 pathname headers.</summary>
public static class GitEncodedPatch
{
    public static byte[]? SelectLines(GitDiffDocument document, int selectionPosition, int selectionLength, bool staged = false,
        bool reverse = false, bool isNewFile = false, bool isRenamed = false)
    {
        Validate(document);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionPosition);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionLength);
        if (selectionPosition > document.Patch.Length || selectionLength > document.Patch.Length - selectionPosition) { throw new ArgumentOutOfRangeException(nameof(selectionLength)); }
        if (GitPatch.Parse(document.Patch).Count > 1) { throw new ArgumentException("Line selection requires a single file diff.", nameof(document)); }
        return GitCommands.Patches.PatchManager.GetSelectedLinesAsPatch(document.Patch, selectionPosition, GitPatchSelection.InclusiveLength(selectionLength),
            staged, GitFileEncodingCodec.GetEncoding(document.FileEncodingName), reverse, isNewFile, isRenamed);
    }

    public static byte[]? SelectResetLines(GitDiffDocument document, int selectionPosition, int selectionLength)
    {
        Validate(document);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionPosition);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionLength);
        if (selectionPosition > document.Patch.Length || selectionLength > document.Patch.Length - selectionPosition) { throw new ArgumentOutOfRangeException(nameof(selectionLength)); }
        if (GitPatch.Parse(document.Patch).Count > 1) { throw new ArgumentException("Line selection requires a single file diff.", nameof(document)); }
        return GitCommands.Patches.PatchManager.GetResetWorkTreeLinesAsPatch(document.Patch, selectionPosition, GitPatchSelection.InclusiveLength(selectionLength), GitFileEncodingCodec.GetEncoding(document.FileEncodingName));
    }

    public static byte[] SelectHunks(GitDiffDocument document, IEnumerable<int> indices)
    {
        Validate(document);
        GitPatchFile file = GitPatch.Parse(document.Patch).Single();
        return GitFileEncodingCodec.EncodePatch(file.SelectHunks(indices), document.FileEncodingName);
    }

    public static byte[] Encode(GitDiffDocument document, string patch)
    {
        Validate(document);
        return GitFileEncodingCodec.EncodePatch(patch, document.FileEncodingName);
    }

    private static void Validate(GitDiffDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanStageEquivalent || !document.IsLosslessPatch || document.EffectiveAppearance != GitDiffAppearance.Patch)
        { throw new InvalidOperationException("This presentation cannot produce a byte-equivalent partial patch."); }
    }
}
